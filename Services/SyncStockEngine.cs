using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using IntegradorFlexForce.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace IntegradorFlexForce.Services
{
    public class SyncStockEngine
    {
        private readonly string _connectionStringForce;
        private readonly string _connectionStringAntiguo;
        private readonly ILogger<SyncStockEngine> _logger;
        private static readonly SemaphoreSlim _syncLock = new(1, 1);

        private readonly SyncStatusDto _currentStatus = new();
        private SyncPreviewDto? _lastPreview;

        public SyncStockEngine(IConfiguration config, ILogger<SyncStockEngine> logger)
        {
            _connectionStringForce = config.GetConnectionString("ForceGVD") ?? "";
            _connectionStringAntiguo = config.GetConnectionString("ERPAntiguo") ?? "";
            _logger = logger;

            _ = Task.Run(PrecargarUltimoEstadoAsync);
        }

        public SyncStatusDto GetCurrentStatus() => _currentStatus;
        public SyncPreviewDto? GetLastPreview() => _lastPreview;

        private async Task PrecargarUltimoEstadoAsync()
        {
            try
            {
                using var conn = new SqlConnection(_connectionStringForce);
                await conn.OpenAsync();

                const string sql = @"
                    SELECT TOP 1 FechaInicio, DuracionSegundos, TotalEscaneados, TotalNuevos, TotalModificados, TotalErrores, Estado
                    FROM dbo.sync_TransaccionLote WITH (NOLOCK)
                    WHERE Entidad = 'STOCK'
                    ORDER BY ID DESC;";

                using var cmd = new SqlCommand(sql, conn);
                using var r = await cmd.ExecuteReaderAsync();
                if (await r.ReadAsync())
                {
                    _currentStatus.UltimaEjecucion = r.GetDateTime(0);
                    _currentStatus.UltimaDuracionSegundos = r.IsDBNull(1) ? 0 : Convert.ToDouble(r.GetDecimal(1));
                    _currentStatus.UltimoLoteProcesados = (r.IsDBNull(3) ? 0 : r.GetInt32(3)) + (r.IsDBNull(4) ? 0 : r.GetInt32(4));
                    _currentStatus.UltimoLoteErrores = r.IsDBNull(5) ? 0 : r.GetInt32(5);
                    _currentStatus.EstadoActual = (r.GetString(6) == "COMPLETADO" || r.GetString(6) == "EXITOSO") ? "COMPLETADO" : (r.GetString(6) == "FALLIDO" ? "ERROR" : "EN_ESPERA");
                    _currentStatus.FaseDetalle = $"Última ejecución: {_currentStatus.UltimoLoteProcesados} actualizados ({_currentStatus.UltimoLoteErrores} errores).";
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "No se pudo precargar último estado del módulo de Stock.");
            }
        }

        private bool _esquemaVerificado = false;
        public async Task AsegurarEsquemaBDForceAsync(CancellationToken ct)
        {
            if (_esquemaVerificado) return;
            try
            {
                using var conn = new SqlConnection(_connectionStringForce);
                await conn.OpenAsync(ct);

                string fileName = "12_SP_Sincronizar_Stock_Force.sql";
                string[] possiblePaths = new[]
                {
                    Path.Combine(Directory.GetCurrentDirectory(), fileName),
                    Path.Combine(AppContext.BaseDirectory, fileName),
                    Path.Combine(AppContext.BaseDirectory, "..", "..", "..", fileName),
                    Path.Combine(@"C:\Users\iraula.j\Desktop\ProyectoInterfaz\IntegradorFlexForce", fileName)
                };
                string? path = possiblePaths.FirstOrDefault(File.Exists);

                if (!string.IsNullOrEmpty(path))
                {
                    var sqlContent = await File.ReadAllTextAsync(path, ct);
                    // Protección: nunca crear SPs con suplantación (evita error 15517 si el owner 'dbo' de la BD está huérfano)
                    sqlContent = Regex.Replace(sqlContent, @"WITH\s+EXECUTE\s+AS\s+OWNER", "", RegexOptions.IgnoreCase);
                    var bloques = Regex.Split(sqlContent, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
                    foreach (var bloque in bloques)
                    {
                        var b = bloque.Trim();
                        if (string.IsNullOrWhiteSpace(b) || b.StartsWith("USE ", StringComparison.OrdinalIgnoreCase))
                            continue;
                        using var cmdSp = new SqlCommand(b, conn) { CommandTimeout = 60 };
                        await cmdSp.ExecuteNonQueryAsync(ct);
                    }
                }

                _esquemaVerificado = true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AsegurarEsquemaBDForceAsync (Stock): No se pudo aplicar 12_SP_Sincronizar_Stock_Force.sql: {Msg}", ex.Message);
            }
        }

        // =========================================================================
        // 1. ESCANEAR Y COMPARAR DIFERENCIAS DE STOCK (SOLO LECTURA)
        // =========================================================================
        public async Task<SyncPreviewDto> AnalizarCambiosPendientesAsync(CancellationToken ct = default)
        {
            await AsegurarEsquemaBDForceAsync(ct);
            var sw = Stopwatch.StartNew();
            var preview = new SyncPreviewDto { FechaAnalisis = DateTime.Now };

            int totalOrigen = 0, totalDestino = 0;
            int insertsDetectados = 0, updatesDetectados = 0;
            const int maxDetalle = 500;
            int detalleCount = 0;

            try
            {
                using var connForce = new SqlConnection(_connectionStringForce);
                await connForce.OpenAsync(ct);

                // Consulta rápida local para obtener conteo de diferencias sin bloquear
                const string sqlDiff = @"
                    SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

                    IF OBJECT_ID('tempdb..#StageStockPreview') IS NOT NULL DROP TABLE #StageStockPreview;

                    SELECT 
                        pa.IDProducto, pa.IDAlmacen, 
                        ISNULL(pa.Capacidad, 0) AS Capacidad,
                        ISNULL(pa.StockFisico, 0) AS StockFisico,
                        ISNULL(pa.StockContable, 0) AS StockContable,
                        ISNULL(pa.StockPorEntregar, 0) AS StockPorEntregar,
                        ISNULL(pa.StockPorRecibir, 0) AS StockPorRecibir,
                        pa.Activo,
                        BINARY_CHECKSUM(
                            pa.Capacidad, pa.StockFisico, pa.StockContable, 
                            pa.StockPorEntregar, pa.StockPorRecibir, 
                            pa.TiempoDeEntrega, pa.FrecuenciaDeCompra, 
                            pa.CantidadMinima, pa.StockMinimo, pa.Activo
                        ) AS HashRemoto
                    INTO #StageStockPreview
                    FROM OPENQUERY(SVBD7524, '
                        SELECT 
                            pa.IDProducto, pa.IDAlmacen, pa.Capacidad, pa.StockFisico, pa.StockContable,
                            pa.StockPorEntregar, pa.StockPorRecibir, pa.TiempoDeEntrega, pa.FrecuenciaDeCompra,
                            pa.CantidadMinima, pa.StockMinimo, pa.Activo
                        FROM ProductoAlmacen pa WITH (NOLOCK)
                    ') pa;

                    SELECT 
                        (SELECT COUNT(*) FROM #StageStockPreview) AS TotalOrigen,
                        (SELECT COUNT(*) FROM dbo.inv_ProductoAlmacen WITH (NOLOCK)) AS TotalDestino,
                        (
                            SELECT COUNT(*) 
                            FROM #StageStockPreview rem
                            INNER JOIN dbo.prd_Producto p ON p.IDExtra = rem.IDProducto
                            INNER JOIN dbo.inv_Almacen a ON a.IDExtra = rem.IDAlmacen
                            WHERE NOT EXISTS (
                                SELECT 1 FROM dbo.inv_ProductoAlmacen loc 
                                WHERE loc.IDProducto = p.ID AND loc.IDAlmacen = a.ID
                            )
                        ) AS TotalNuevos,
                        (
                            SELECT COUNT(*)
                            FROM #StageStockPreview rem
                            INNER JOIN dbo.prd_Producto p ON p.IDExtra = rem.IDProducto
                            INNER JOIN dbo.inv_Almacen a ON a.IDExtra = rem.IDAlmacen
                            INNER JOIN dbo.inv_ProductoAlmacen loc ON loc.IDProducto = p.ID AND loc.IDAlmacen = a.ID
                            WHERE BINARY_CHECKSUM(
                                loc.Capacidad, loc.StockFisico, loc.StockContable, 
                                loc.StockPorEntregar, loc.StockPorRecibir, 
                                loc.TiempoDeEntrega, loc.FrecuenciaDeCompra, 
                                loc.CantidadMinima, loc.StockMinimo, loc.Estado
                            ) <> rem.HashRemoto
                        ) AS TotalModificados;

                    -- Muestra de los primeros 100 con cambios para la grilla
                    SELECT TOP 100
                        p.IDExtra AS IDProductoOrigen,
                        p.Codigo AS CodigoProducto,
                        p.Nombre AS NombreProducto,
                        a.Nombre AS NombreAlmacen,
                        loc.StockFisico AS StockFisicoActual,
                        rem.StockFisico AS StockFisicoNuevo,
                        loc.StockPorEntregar AS StockPorEntregarActual,
                        rem.StockPorEntregar AS StockPorEntregarNuevo
                    FROM #StageStockPreview rem
                    INNER JOIN dbo.prd_Producto p ON p.IDExtra = rem.IDProducto
                    INNER JOIN dbo.inv_Almacen a ON a.IDExtra = rem.IDAlmacen
                    INNER JOIN dbo.inv_ProductoAlmacen loc ON loc.IDProducto = p.ID AND loc.IDAlmacen = a.ID
                    WHERE BINARY_CHECKSUM(
                        loc.Capacidad, loc.StockFisico, loc.StockContable, 
                        loc.StockPorEntregar, loc.StockPorRecibir, 
                        loc.TiempoDeEntrega, loc.FrecuenciaDeCompra, 
                        loc.CantidadMinima, loc.StockMinimo, loc.Estado
                    ) <> rem.HashRemoto;

                    DROP TABLE #StageStockPreview;
                ";

                const string sqlExec = @"
                    IF OBJECT_ID('dbo.usp_Sync_PreviewStock', 'P') IS NOT NULL
                        EXEC dbo.usp_Sync_PreviewStock @Top = 100;
                    ELSE
                    BEGIN
                " + sqlDiff + @"
                    END;
                ";

                using var cmd = new SqlCommand(sqlExec, connForce) { CommandTimeout = 120 };
                using var reader = await cmd.ExecuteReaderAsync(ct);

                if (await reader.ReadAsync(ct))
                {
                    totalOrigen = reader.GetInt32(0);
                    totalDestino = reader.GetInt32(1);
                    insertsDetectados = reader.GetInt32(2);
                    updatesDetectados = reader.GetInt32(3);
                }

                if (await reader.NextResultAsync(ct))
                {
                    while (await reader.ReadAsync(ct) && detalleCount < maxDetalle)
                    {
                        int idProd = reader.GetInt32(0);
                        string codProd = reader.IsDBNull(1) ? "" : reader.GetString(1).Trim();
                        string nomProd = reader.IsDBNull(2) ? "" : reader.GetString(2).Trim();
                        string nomAlm = reader.IsDBNull(3) ? "" : reader.GetString(3).Trim();
                        int stockFisAct = reader.GetInt32(4);
                        int stockFisNue = reader.GetInt32(5);
                        int stockEntAct = reader.GetInt32(6);
                        int stockEntNue = reader.GetInt32(7);

                        preview.DetalleCambios.Add(new CambioDetectadoItemDto
                        {
                            Tabla = "STOCK_INVENTARIO",
                            TipoOperacion = "UPDATE",
                            IDOrigen = idProd,
                            CodigoCliente = codProd,
                            NombreCliente = $"{nomProd} [{nomAlm}]",
                            CampoModificado = "StockFisico / StockPorEntregar",
                            ValorActualForce = $"Fis: {stockFisAct} | Entr: {stockEntAct}",
                            ValorNuevoERP = $"Fis: {stockFisNue} | Entr: {stockEntNue}"
                        });
                        detalleCount++;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error al generar vista previa de diferencias de stock.");
            }

            preview.ResumenPorTabla.Add(new TablaComparacionResumenDto
            {
                CodigoTabla = "STOCK_INVENTARIO",
                NombreTabla = "Stock en Línea por Almacén",
                TablasForce = "inv_ProductoAlmacen",
                TotalOrigen = totalOrigen,
                TotalDestino = totalDestino,
                InsertsDetectados = insertsDetectados,
                UpdatesDetectados = updatesDetectados,
                DeletesBajasDetectados = 0,
                Observacion = (insertsDetectados + updatesDetectados == 0) ? "100% Sincronizado" : $"{insertsDetectados + updatesDetectados} productos requieren actualización de stock"
            });

            sw.Stop();
            preview.TiempoEscaneoSegundos = Math.Round(sw.Elapsed.TotalSeconds, 2);
            _lastPreview = preview;
            return preview;
        }

        // =========================================================================
        // 2. EJECUTAR SINCRONIZACIÓN DE STOCK (TRANSACCIONAL CON ROWLOCK)
        // =========================================================================
        public async Task<SyncTransaccionDto> EjecutarSincronizacionStockAsync(string tipoEjecucion = "AUTOMATICO", string usuario = "SYSTEM", CancellationToken ct = default)
        {
            if (!await _syncLock.WaitAsync(0, ct))
            {
                throw new InvalidOperationException("Ya existe una sincronización de Stock en ejecución.");
            }

            var swTotal = Stopwatch.StartNew();
            var loteId = Guid.NewGuid();
            var transaccion = new SyncTransaccionDto
            {
                LoteId = loteId,
                Entidad = "STOCK",
                TipoEjecucion = tipoEjecucion,
                FechaInicio = DateTime.Now,
                Estado = "EN_PROCESO",
                Usuario = usuario
            };

            _currentStatus.IsRunning = true;
            _currentStatus.EstadoActual = "EJECUTANDO";
            _currentStatus.FaseDetalle = "Ejecutando motor diferencial de Stock directo en Force (192.168.2.93)...";
            _currentStatus.ProgresoActual = 0;
            _currentStatus.ProgresoTotal = 100;

            try
            {
                await AsegurarEsquemaBDForceAsync(ct);

                using var conn = new SqlConnection(_connectionStringForce);
                await conn.OpenAsync(ct);

                using var cmd = new SqlCommand("dbo.usp_Sync_SincronizarStock_Directo", conn)
                {
                    CommandType = CommandType.StoredProcedure,
                    CommandTimeout = 300 // 5 minutos timeout para alta concurrencia
                };

                cmd.Parameters.AddWithValue("@TipoEjecucion", tipoEjecucion);
                cmd.Parameters.AddWithValue("@Usuario", usuario);
                cmd.Parameters.AddWithValue("@LoteId", loteId);

                var pTotalRemotos = new SqlParameter("@TotalRemotos", SqlDbType.Int) { Direction = ParameterDirection.Output };
                var pTotalActualizados = new SqlParameter("@TotalActualizados", SqlDbType.Int) { Direction = ParameterDirection.Output };
                var pTotalInsertados = new SqlParameter("@TotalInsertados", SqlDbType.Int) { Direction = ParameterDirection.Output };

                cmd.Parameters.Add(pTotalRemotos);
                cmd.Parameters.Add(pTotalActualizados);
                cmd.Parameters.Add(pTotalInsertados);

                _currentStatus.FaseDetalle = "Comparando hashes de stock y actualizando registros modificados...";
                await cmd.ExecuteNonQueryAsync(ct);

                int remotos = pTotalRemotos.Value != DBNull.Value ? (int)pTotalRemotos.Value : 0;
                int actualizados = pTotalActualizados.Value != DBNull.Value ? (int)pTotalActualizados.Value : 0;
                int insertados = pTotalInsertados.Value != DBNull.Value ? (int)pTotalInsertados.Value : 0;

                swTotal.Stop();
                transaccion.FechaFin = DateTime.Now;
                transaccion.DuracionSegundos = (decimal)Math.Round(swTotal.Elapsed.TotalSeconds, 2);
                transaccion.TotalEscaneados = remotos;
                transaccion.TotalModificados = actualizados;
                transaccion.TotalNuevos = insertados;
                transaccion.TotalErrores = 0;
                transaccion.Estado = "EXITOSO";

                _currentStatus.UltimaEjecucion = DateTime.Now;
                _currentStatus.UltimoLoteProcesados = actualizados + insertados;
                _currentStatus.UltimoLoteModificados = actualizados;
                _currentStatus.UltimoLoteNuevos = insertados;
                _currentStatus.UltimoLoteErrores = 0;
                _currentStatus.UltimaDuracionSegundos = Math.Round(swTotal.Elapsed.TotalSeconds, 2);
                _currentStatus.EstadoActual = "COMPLETADO";
                _currentStatus.FaseDetalle = $"Stock sincronizado: {actualizados} actualizados, {insertados} nuevos en {_currentStatus.UltimaDuracionSegundos}s.";
                _currentStatus.ProgresoActual = 100;

                _logger.LogInformation("Sincronización de Stock completada exitosamente. Actualizados: {Act}, Nuevos: {Nuev}, Duración: {Dur}s",
                    actualizados, insertados, _currentStatus.UltimaDuracionSegundos);

                return transaccion;
            }
            catch (Exception ex)
            {
                swTotal.Stop();
                transaccion.FechaFin = DateTime.Now;
                transaccion.DuracionSegundos = (decimal)Math.Round(swTotal.Elapsed.TotalSeconds, 2);
                transaccion.TotalErrores = 1;
                transaccion.Estado = "FALLIDO";

                _currentStatus.EstadoActual = "ERROR";
                _currentStatus.UltimoLoteErrores = 1;
                _currentStatus.FaseDetalle = $"Error en sincronización de stock: {ex.Message}";
                _logger.LogError(ex, "Error al ejecutar sincronización de Stock.");
                throw;
            }
            finally
            {
                _currentStatus.IsRunning = false;
                _syncLock.Release();
            }
        }
    }
}
