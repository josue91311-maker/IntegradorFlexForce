using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using IntegradorFlexForce.Models;

namespace IntegradorFlexForce.Services
{
    public class SyncEngine
    {
        private readonly string _connectionStringForce;
        private readonly string _connectionStringAntiguo;
        private readonly ILogger<SyncEngine> _logger;
        private static readonly SemaphoreSlim _syncLock = new(1, 1);
        private static SyncStatusDto _currentStatus = new();

        public SyncEngine(IConfiguration config, ILogger<SyncEngine> logger)
        {
            _connectionStringForce = config.GetConnectionString("ForceGVD") 
                ?? throw new InvalidOperationException("Falta ConnectionString ForceGVD");
            _connectionStringAntiguo = config.GetConnectionString("ERPAntiguo") 
                ?? throw new InvalidOperationException("Falta ConnectionString ERPAntiguo");
            _logger = logger;
        }

        public SyncStatusDto GetStatus() => _currentStatus;

        public async Task<bool> IsRunningAsync()
        {
            return _syncLock.CurrentCount == 0;
        }

        public async Task<SyncTransaccionDto> EjecutarSincronizacionClientesAsync(string tipoEjecucion = "AUTOMATICO", string usuario = "SYSTEM", CancellationToken ct = default)
        {
            if (!await _syncLock.WaitAsync(0, ct))
            {
                _logger.LogWarning("Ya hay una sincronización en curso. Se omite esta solicitud.");
                return new SyncTransaccionDto { Estado = "EN_PROCESO", Usuario = usuario };
            }

            var loteId = Guid.NewGuid();
            var swTotal = Stopwatch.StartNew();
            _currentStatus.IsRunning = true;
            _currentStatus.EstadoActual = "ESCANEANDO_HUELLAS";

            var transaccion = new SyncTransaccionDto
            {
                LoteId = loteId,
                Entidad = "CLIENTES",
                TipoEjecucion = tipoEjecucion,
                FechaInicio = DateTime.Now,
                Estado = "EN_PROCESO",
                Usuario = usuario
            };

            try
            {
                await RegistrarInicioLoteAsync(transaccion, ct);

                // -------------------------------------------------------------
                // 1. CARGAR CONFIGURACIÓN VIGENTE DE BDForceGVD
                // -------------------------------------------------------------
                var config = await CargarConfiguracionAsync(ct);
                _currentStatus.SincronizacionActiva = config.SincronizacionActiva;
                _currentStatus.IntervaloMinutos = config.IntervaloMinutos;
                _currentStatus.VentanaHoraria = $"{config.HoraInicioVentana} - {config.HoraFinVentana}";

                if (tipoEjecucion == "AUTOMATICO" && !config.SincronizacionActiva)
                {
                    _logger.LogInformation("Sincronización automática pausada en la configuración.");
                    transaccion.Estado = "PAUSADO";
                    await RegistrarFinLoteAsync(transaccion, ct);
                    return transaccion;
                }

                // -------------------------------------------------------------
                // 2. LEER HASHES LOCALES (BDForceGVD) Y REMOTOS (ERP Antiguo)
                // -------------------------------------------------------------
                var hashesLocales = await ObtenerHashesLocalesAsync(ct);
                var hashesRemotos = await ObtenerHashesRemotosAsync(ct);

                transaccion.TotalEscaneados = hashesRemotos.Count;
                _logger.LogInformation("Hashes escaneados: {Total} en ERP Antiguo vs {Locales} en Force.", hashesRemotos.Count, hashesLocales.Count);

                // -------------------------------------------------------------
                // 3. DETECTAR DELTA (NUEVOS O MODIFICADOS)
                // -------------------------------------------------------------
                var idsNuevos = new List<int>();
                var idsModificados = new List<int>();

                foreach (var (idOrigen, hashRemoto) in hashesRemotos)
                {
                    if (!hashesLocales.TryGetValue(idOrigen, out var hashLocal))
                    {
                        idsNuevos.Add(idOrigen);
                    }
                    else if (!hashLocal.AsSpan().SequenceEqual(hashRemoto))
                    {
                        idsModificados.Add(idOrigen);
                    }
                }

                transaccion.TotalNuevos = idsNuevos.Count;
                transaccion.TotalModificados = idsModificados.Count;
                var totalACambiar = idsNuevos.Count + idsModificados.Count;

                _logger.LogInformation("Delta detectado: {Nuevos} Nuevos, {Modificados} Modificados (Total: {Total})", 
                    idsNuevos.Count, idsModificados.Count, totalACambiar);

                if (totalACambiar == 0)
                {
                    transaccion.Estado = "EXITOSO";
                    swTotal.Stop();
                    transaccion.DuracionSegundos = (decimal)swTotal.Elapsed.TotalSeconds;
                    await RegistrarFinLoteAsync(transaccion, ct);

                    _currentStatus.EstadoActual = "SIN_CAMBIOS";
                    _currentStatus.UltimaEjecucion = DateTime.Now;
                    _currentStatus.UltimaDuracionSegundos = swTotal.Elapsed.TotalSeconds;
                    return transaccion;
                }

                // -------------------------------------------------------------
                // 4. EXTRAER DETALLE EN LOTES CONFIGURABLES (Default: 500)
                // -------------------------------------------------------------
                _currentStatus.EstadoActual = "PROCESANDO_LOTES";
                var idsPendientes = idsNuevos.Concat(idsModificados).ToList();
                int batchSize = config.TamanoLote > 0 ? config.TamanoLote : 500;
                int erroresCount = 0;

                for (int i = 0; i < idsPendientes.Count; i += batchSize)
                {
                    ct.ThrowIfCancellationRequested();
                    var loteIds = idsPendientes.Skip(i).Take(batchSize).ToList();

                    var clientesDetalle = await ObtenerDetalleClientesAsync(loteIds, ct);

                    // ---------------------------------------------------------
                    // 5. UPSERT INDIVIDUAL CON MANEJO DE ERRORES RESILIENTE
                    // (Un cliente con error NO detiene los otros 229,999)
                    // ---------------------------------------------------------
                    foreach (var cliente in clientesDetalle)
                    {
                        try
                        {
                            var idGenerado = await EjecutarUpsertClienteAsync(cliente, ct);
                            await ActualizarControlHashAsync(cliente.ID_Cliente, cliente.CodigoCliente, hashesRemotos[cliente.ID_Cliente], idGenerado, ct);
                        }
                        catch (Exception exCliente)
                        {
                            erroresCount++;
                            _logger.LogError(exCliente, "Error procesando cliente ID={ID}, Codigo={Codigo}", cliente.ID_Cliente, cliente.CodigoCliente);
                            await RegistrarLogAsync(loteId, "CLIENTES", "ERROR", cliente.ID_Cliente, cliente.CodigoCliente, 
                                $"Fallo al sincronizar cliente: {exCliente.Message}", exCliente.ToString(), ct);
                        }
                    }

                    _logger.LogInformation("Lote completado: {Progreso}/{Total}", Math.Min(i + batchSize, idsPendientes.Count), idsPendientes.Count);
                }

                swTotal.Stop();
                transaccion.TotalErrores = erroresCount;
                transaccion.DuracionSegundos = (decimal)swTotal.Elapsed.TotalSeconds;
                transaccion.Estado = erroresCount > 0 ? "CON_ERRORES" : "EXITOSO";

                await RegistrarFinLoteAsync(transaccion, ct);

                _currentStatus.UltimaEjecucion = DateTime.Now;
                _currentStatus.UltimoLoteProcesados = totalACambiar;
                _currentStatus.UltimoLoteNuevos = idsNuevos.Count;
                _currentStatus.UltimoLoteModificados = idsModificados.Count;
                _currentStatus.UltimoLoteErrores = erroresCount;
                _currentStatus.UltimaDuracionSegundos = swTotal.Elapsed.TotalSeconds;
                _currentStatus.EstadoActual = "COMPLETADO";

                return transaccion;
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "Error crítico en ciclo de sincronización de clientes");
                transaccion.Estado = "FALLIDO";
                swTotal.Stop();
                transaccion.DuracionSegundos = (decimal)swTotal.Elapsed.TotalSeconds;
                await RegistrarLogAsync(loteId, "CLIENTES", "FATAL", null, null, "Error crítico en el proceso de sincronización", ex.ToString(), ct);
                await RegistrarFinLoteAsync(transaccion, ct);
                _currentStatus.EstadoActual = "ERROR_CRITICO";
                throw;
            }
            finally
            {
                _currentStatus.IsRunning = false;
                _syncLock.Release();
            }
        }

        // =========================================================================
        // CONSULTAS SQL Y ACCESO A DATOS
        // =========================================================================

        private async Task<Dictionary<int, byte[]>> ObtenerHashesLocalesAsync(CancellationToken ct)
        {
            var dict = new Dictionary<int, byte[]>(250000);
            using var conn = new SqlConnection(_connectionStringForce);
            await conn.OpenAsync(ct);

            const string sql = "SELECT IDOrigen, LastHash FROM dbo.sync_ControlHashes WITH (NOLOCK) WHERE Entidad = 'CLIENTES';";
            using var cmd = new SqlCommand(sql, conn);
            using var reader = await cmd.ExecuteReaderAsync(ct);

            while (await reader.ReadAsync(ct))
            {
                dict[reader.GetInt32(0)] = (byte[])reader[1];
            }
            return dict;
        }

        private async Task<Dictionary<int, byte[]>> ObtenerHashesRemotosAsync(CancellationToken ct)
        {
            var dict = new Dictionary<int, byte[]>(250000);
            using var conn = new SqlConnection(_connectionStringAntiguo);
            await conn.OpenAsync(ct);

            const string sql = "SELECT ID_Cliente, RowHash FROM dbo.v_Extraccion_Clientes_Force WITH (NOLOCK);";
            using var cmd = new SqlCommand(sql, conn);
            cmd.CommandTimeout = 180;
            using var reader = await cmd.ExecuteReaderAsync(ct);

            while (await reader.ReadAsync(ct))
            {
                dict[reader.GetInt32(0)] = (byte[])reader[1];
            }
            return dict;
        }

        private async Task<List<ClienteExtraccionDto>> ObtenerDetalleClientesAsync(List<int> ids, CancellationToken ct)
        {
            var lista = new List<ClienteExtraccionDto>(ids.Count);
            using var conn = new SqlConnection(_connectionStringAntiguo);
            await conn.OpenAsync(ct);

            var inClause = string.Join(",", ids);
            var sql = $"SELECT * FROM dbo.v_Extraccion_Clientes_Force WITH (NOLOCK) WHERE ID_Cliente IN ({inClause});";

            using var cmd = new SqlCommand(sql, conn);
            cmd.CommandTimeout = 120;
            using var reader = await cmd.ExecuteReaderAsync(ct);

            while (await reader.ReadAsync(ct))
            {
                lista.Add(new ClienteExtraccionDto
                {
                    ID_Cliente = reader.GetInt32(reader.GetOrdinal("ID_Cliente")),
                    CodigoCliente = reader.GetString(reader.GetOrdinal("CodigoCliente")),
                    TipoPersona = reader.GetValue(reader.GetOrdinal("TipoPersona")).ToString() ?? "3",
                    TipoDocIdentidad = reader.GetString(reader.GetOrdinal("TipoDocIdentidad")),
                    DocIdentidad = reader.GetString(reader.GetOrdinal("DocIdentidad")),
                    NombreCompleto = reader.GetString(reader.GetOrdinal("NombreCompleto")),
                    NombreComercial = reader.GetString(reader.GetOrdinal("NombreComercial")),
                    PrimerNombre = reader.GetString(reader.GetOrdinal("PrimerNombre")),
                    SegundoNombre = reader.GetString(reader.GetOrdinal("SegundoNombre")),
                    ApellidoPaterno = reader.GetString(reader.GetOrdinal("ApellidoPaterno")),
                    ApellidoMaterno = reader.GetString(reader.GetOrdinal("ApellidoMaterno")),
                    CodigoCategoriaCliente = reader.GetValue(reader.GetOrdinal("CodigoCategoriaCliente")).ToString() ?? "CAT_A",
                    CodigoTipoCliente = reader.GetValue(reader.GetOrdinal("CodigoTipoCliente")).ToString() ?? "GENERAL",
                    CodigoTipoNegocio = reader.GetValue(reader.GetOrdinal("CodigoTipoNegocio")).ToString() ?? "0",
                    Email = reader.IsDBNull(reader.GetOrdinal("Email")) ? null : reader.GetString(reader.GetOrdinal("Email")),
                    Telefono = reader.GetString(reader.GetOrdinal("Telefono")),
                    Contacto = reader.GetString(reader.GetOrdinal("Contacto")),
                    PuestoContacto = reader.GetString(reader.GetOrdinal("PuestoContacto")),
                    ExceptuadoPercepcion = reader.GetBoolean(reader.GetOrdinal("ExceptuadoPercepcion")),
                    Observacion = reader.GetString(reader.GetOrdinal("Observacion")),
                    EstadoCliente = reader.GetBoolean(reader.GetOrdinal("EstadoCliente")),

                    ID_Direccion = reader.GetInt32(reader.GetOrdinal("ID_Direccion")),
                    EsPrincipal = reader.GetBoolean(reader.GetOrdinal("EsPrincipal")),
                    CodigoTipoDireccion = reader.GetString(reader.GetOrdinal("CodigoTipoDireccion")),
                    Direccion = reader.GetString(reader.GetOrdinal("Direccion")),
                    Referencia = reader.GetString(reader.GetOrdinal("Referencia")),
                    Ubigeo = reader.GetString(reader.GetOrdinal("Ubigeo")),
                    CodigoDepartamento = reader.GetString(reader.GetOrdinal("CodigoDepartamento")),
                    CodigoProvincia = reader.GetString(reader.GetOrdinal("CodigoProvincia")),
                    CodigoDistrito = reader.GetString(reader.GetOrdinal("CodigoDistrito")),
                    Latitud = reader.GetDecimal(reader.GetOrdinal("Latitud")),
                    Longitud = reader.GetDecimal(reader.GetOrdinal("Longitud")),
                    EstadoDireccion = reader.GetBoolean(reader.GetOrdinal("EstadoDireccion")),
                });
            }
            return lista;
        }

        private async Task<int> EjecutarUpsertClienteAsync(ClienteExtraccionDto c, CancellationToken ct)
        {
            using var conn = new SqlConnection(_connectionStringForce);
            await conn.OpenAsync(ct);

            using var cmd = new SqlCommand("dbo.usp_Sync_UpsertCliente", conn);
            cmd.CommandType = CommandType.StoredProcedure;
            cmd.CommandTimeout = 60;

            cmd.Parameters.AddWithValue("@ID_Cliente_Origen", c.ID_Cliente);
            cmd.Parameters.AddWithValue("@CodigoCliente", c.CodigoCliente);
            cmd.Parameters.AddWithValue("@TipoPersona", c.TipoPersona);
            cmd.Parameters.AddWithValue("@TipoDocIdentidad", c.TipoDocIdentidad);
            cmd.Parameters.AddWithValue("@DocIdentidad", c.DocIdentidad);
            cmd.Parameters.AddWithValue("@NombreCompleto", c.NombreCompleto);
            cmd.Parameters.AddWithValue("@NombreComercial", c.NombreComercial);
            cmd.Parameters.AddWithValue("@PrimerNombre", c.PrimerNombre);
            cmd.Parameters.AddWithValue("@SegundoNombre", c.SegundoNombre);
            cmd.Parameters.AddWithValue("@ApellidoPaterno", c.ApellidoPaterno);
            cmd.Parameters.AddWithValue("@ApellidoMaterno", c.ApellidoMaterno);
            cmd.Parameters.AddWithValue("@CodigoTipoCliente", c.CodigoTipoCliente);
            cmd.Parameters.AddWithValue("@CodigoCategoriaCliente", c.CodigoCategoriaCliente);
            cmd.Parameters.AddWithValue("@CodigoTipoNegocio", c.CodigoTipoNegocio);
            cmd.Parameters.AddWithValue("@Email", (object?)c.Email ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Telefono", c.Telefono);
            cmd.Parameters.AddWithValue("@Contacto", c.Contacto);
            cmd.Parameters.AddWithValue("@PuestoContacto", c.PuestoContacto);
            cmd.Parameters.AddWithValue("@ExceptuadoPercepcion", c.ExceptuadoPercepcion);
            cmd.Parameters.AddWithValue("@Observacion", c.Observacion);
            cmd.Parameters.AddWithValue("@EstadoCliente", c.EstadoCliente);

            cmd.Parameters.AddWithValue("@ID_Direccion_Origen", c.ID_Direccion);
            cmd.Parameters.AddWithValue("@EsPrincipal", c.EsPrincipal);
            cmd.Parameters.AddWithValue("@CodigoTipoDireccion", c.CodigoTipoDireccion);
            cmd.Parameters.AddWithValue("@Direccion", c.Direccion);
            cmd.Parameters.AddWithValue("@Referencia", c.Referencia);
            cmd.Parameters.AddWithValue("@Ubigeo", c.Ubigeo);
            cmd.Parameters.AddWithValue("@CodigoDepartamento", c.CodigoDepartamento);
            cmd.Parameters.AddWithValue("@CodigoProvincia", c.CodigoProvincia);
            cmd.Parameters.AddWithValue("@CodigoDistrito", c.CodigoDistrito);
            cmd.Parameters.AddWithValue("@Latitud", c.Latitud);
            cmd.Parameters.AddWithValue("@Longitud", c.Longitud);
            cmd.Parameters.AddWithValue("@EstadoDireccion", c.EstadoDireccion);

            var paramOut = new SqlParameter("@IDClienteGenerado", SqlDbType.Int)
            {
                Direction = ParameterDirection.Output
            };
            cmd.Parameters.Add(paramOut);

            await cmd.ExecuteNonQueryAsync(ct);
            return (int)paramOut.Value;
        }

        private async Task ActualizarControlHashAsync(int idOrigen, string codigo, byte[] newHash, int idDestino, CancellationToken ct)
        {
            using var conn = new SqlConnection(_connectionStringForce);
            await conn.OpenAsync(ct);

            const string sql = @"
                MERGE dbo.sync_ControlHashes AS target
                USING (SELECT 'CLIENTES' AS Entidad, @IDOrigen AS IDOrigen) AS source
                ON (target.Entidad = source.Entidad AND target.IDOrigen = source.IDOrigen)
                WHEN MATCHED THEN
                    UPDATE SET LastHash = @NewHash, IDDestino = @IDDestino, FechaSincronizacion = GETDATE(), EstadoSync = 'SINCRONIZADO', UltimoError = NULL
                WHEN NOT MATCHED THEN
                    INSERT (Entidad, IDOrigen, Codigo, LastHash, IDDestino, FechaSincronizacion, EstadoSync)
                    VALUES ('CLIENTES', @IDOrigen, @Codigo, @NewHash, @IDDestino, GETDATE(), 'SINCRONIZADO');";

            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@IDOrigen", idOrigen);
            cmd.Parameters.AddWithValue("@Codigo", codigo);
            cmd.Parameters.AddWithValue("@NewHash", newHash);
            cmd.Parameters.AddWithValue("@IDDestino", idDestino);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // =========================================================================
        // LOGS, CONFIGURACIÓN Y LOTES
        // =========================================================================

        public async Task<SyncConfigDto> CargarConfiguracionAsync(CancellationToken ct = default)
        {
            var config = new SyncConfigDto();
            try
            {
                using var conn = new SqlConnection(_connectionStringForce);
                await conn.OpenAsync(ct);

                const string sql = "SELECT Clave, Valor FROM dbo.sync_Configuracion WITH (NOLOCK);";
                using var cmd = new SqlCommand(sql, conn);
                using var reader = await cmd.ExecuteReaderAsync(ct);

                while (await reader.ReadAsync(ct))
                {
                    string clave = reader.GetString(0);
                    string valor = reader.GetString(1);

                    switch (clave)
                    {
                        case "SINCRONIZACION_ACTIVA":
                            config.SincronizacionActiva = valor == "1";
                            break;
                        case "INTERVALO_MINUTOS":
                            if (int.TryParse(valor, out int min)) config.IntervaloMinutos = min;
                            break;
                        case "HORA_INICIO_VENTANA":
                            config.HoraInicioVentana = valor;
                            break;
                        case "HORA_FIN_VENTANA":
                            config.HoraFinVentana = valor;
                            break;
                        case "TAMANO_LOTE":
                            if (int.TryParse(valor, out int lote)) config.TamanoLote = lote;
                            break;
                        case "REINTENTOS_MAXIMOS":
                            if (int.TryParse(valor, out int r)) config.ReintentosMaximos = r;
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "No se pudo leer sync_Configuracion. Usando valores predeterminados.");
            }
            return config;
        }

        public async Task GuardarConfiguracionAsync(SyncConfigDto config, CancellationToken ct = default)
        {
            using var conn = new SqlConnection(_connectionStringForce);
            await conn.OpenAsync(ct);

            var items = new (string Clave, string Valor)[]
            {
                ("SINCRONIZACION_ACTIVA", config.SincronizacionActiva ? "1" : "0"),
                ("INTERVALO_MINUTOS", config.IntervaloMinutos.ToString()),
                ("HORA_INICIO_VENTANA", config.HoraInicioVentana),
                ("HORA_FIN_VENTANA", config.HoraFinVentana),
                ("TAMANO_LOTE", config.TamanoLote.ToString()),
                ("REINTENTOS_MAXIMOS", config.ReintentosMaximos.ToString())
            };

            const string sql = @"
                MERGE dbo.sync_Configuracion AS target
                USING (SELECT @Clave AS Clave) AS source
                ON (target.Clave = source.Clave)
                WHEN MATCHED THEN
                    UPDATE SET Valor = @Valor, FechaModificacion = GETDATE()
                WHEN NOT MATCHED THEN
                    INSERT (Clave, Valor, Descripcion, FechaModificacion)
                    VALUES (@Clave, @Valor, 'Parámetro de configuración', GETDATE());";

            foreach (var item in items)
            {
                using var cmd = new SqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@Clave", item.Clave);
                cmd.Parameters.AddWithValue("@Valor", item.Valor);
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }

        private async Task RegistrarInicioLoteAsync(SyncTransaccionDto t, CancellationToken ct)
        {
            try
            {
                using var conn = new SqlConnection(_connectionStringForce);
                await conn.OpenAsync(ct);

                const string sql = @"
                    INSERT INTO dbo.sync_TransaccionLote (LoteId, Entidad, TipoEjecucion, FechaInicio, Estado, Usuario)
                    VALUES (@LoteId, @Entidad, @TipoEjecucion, @FechaInicio, @Estado, @Usuario);";

                using var cmd = new SqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@LoteId", t.LoteId);
                cmd.Parameters.AddWithValue("@Entidad", t.Entidad);
                cmd.Parameters.AddWithValue("@TipoEjecucion", t.TipoEjecucion);
                cmd.Parameters.AddWithValue("@FechaInicio", t.FechaInicio);
                cmd.Parameters.AddWithValue("@Estado", t.Estado);
                cmd.Parameters.AddWithValue("@Usuario", (object?)t.Usuario ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "No se pudo registrar inicio de lote en sync_TransaccionLote.");
            }
        }

        private async Task RegistrarFinLoteAsync(SyncTransaccionDto t, CancellationToken ct)
        {
            try
            {
                using var conn = new SqlConnection(_connectionStringForce);
                await conn.OpenAsync(ct);

                const string sql = @"
                    UPDATE dbo.sync_TransaccionLote
                    SET FechaFin = GETDATE(),
                        DuracionSegundos = @DuracionSegundos,
                        TotalEscaneados = @TotalEscaneados,
                        TotalNuevos = @TotalNuevos,
                        TotalModificados = @TotalModificados,
                        TotalErrores = @TotalErrores,
                        Estado = @Estado
                    WHERE LoteId = @LoteId;";

                using var cmd = new SqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@LoteId", t.LoteId);
                cmd.Parameters.AddWithValue("@DuracionSegundos", (object?)t.DuracionSegundos ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@TotalEscaneados", t.TotalEscaneados);
                cmd.Parameters.AddWithValue("@TotalNuevos", t.TotalNuevos);
                cmd.Parameters.AddWithValue("@TotalModificados", t.TotalModificados);
                cmd.Parameters.AddWithValue("@TotalErrores", t.TotalErrores);
                cmd.Parameters.AddWithValue("@Estado", t.Estado);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "No se pudo registrar fin de lote en sync_TransaccionLote.");
            }
        }

        public async Task RegistrarLogAsync(Guid? loteId, string entidad, string nivel, int? idOrigen, string? codigo, string mensaje, string? detalle, CancellationToken ct = default)
        {
            try
            {
                using var conn = new SqlConnection(_connectionStringForce);
                await conn.OpenAsync(ct);

                const string sql = @"
                    INSERT INTO dbo.sync_Log (LoteId, Entidad, Nivel, IDOrigen, Codigo, Mensaje, DetalleTecnico, Fecha)
                    VALUES (@LoteId, @Entidad, @Nivel, @IDOrigen, @Codigo, @Mensaje, @DetalleTecnico, GETDATE());";

                using var cmd = new SqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@LoteId", (object?)loteId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Entidad", entidad);
                cmd.Parameters.AddWithValue("@Nivel", nivel);
                cmd.Parameters.AddWithValue("@IDOrigen", (object?)idOrigen ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Codigo", (object?)codigo ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Mensaje", mensaje.Length > 500 ? mensaje[..500] : mensaje);
                cmd.Parameters.AddWithValue("@DetalleTecnico", (object?)detalle ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "No se pudo registrar log en dbo.sync_Log.");
            }
        }

        public async Task<List<SyncLogDto>> ObtenerUltimosLogsAsync(int cantidad = 100, string? nivel = null, CancellationToken ct = default)
        {
            var logs = new List<SyncLogDto>();
            using var conn = new SqlConnection(_connectionStringForce);
            await conn.OpenAsync(ct);

            string sql = $@"
                SELECT TOP ({cantidad}) ID, LoteId, Entidad, Nivel, IDOrigen, Codigo, Mensaje, DetalleTecnico, Fecha
                FROM dbo.sync_Log WITH (NOLOCK)
                WHERE (@Nivel IS NULL OR Nivel = @Nivel)
                ORDER BY Fecha DESC;";

            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@Nivel", (object?)nivel ?? DBNull.Value);
            using var reader = await cmd.ExecuteReaderAsync(ct);

            while (await reader.ReadAsync(ct))
            {
                logs.Add(new SyncLogDto
                {
                    ID = reader.GetInt64(0),
                    LoteId = reader.IsDBNull(1) ? null : reader.GetGuid(1),
                    Entidad = reader.GetString(2),
                    Nivel = reader.GetString(3),
                    IDOrigen = reader.IsDBNull(4) ? null : reader.GetInt32(4),
                    Codigo = reader.IsDBNull(5) ? null : reader.GetString(5),
                    Mensaje = reader.GetString(6),
                    DetalleTecnico = reader.IsDBNull(7) ? null : reader.GetString(7),
                    Fecha = reader.GetDateTime(8)
                });
            }
            return logs;
        }

        public async Task<List<SyncTransaccionDto>> ObtenerUltimasTransaccionesAsync(int cantidad = 20, CancellationToken ct = default)
        {
            var list = new List<SyncTransaccionDto>();
            using var conn = new SqlConnection(_connectionStringForce);
            await conn.OpenAsync(ct);

            string sql = $@"
                SELECT TOP ({cantidad}) ID, LoteId, Entidad, TipoEjecucion, FechaInicio, FechaFin, 
                       DuracionSegundos, TotalEscaneados, TotalNuevos, TotalModificados, TotalErrores, Estado, Usuario
                FROM dbo.sync_TransaccionLote WITH (NOLOCK)
                ORDER BY FechaInicio DESC;";

            using var cmd = new SqlCommand(sql, conn);
            using var reader = await cmd.ExecuteReaderAsync(ct);

            while (await reader.ReadAsync(ct))
            {
                list.Add(new SyncTransaccionDto
                {
                    ID = reader.GetInt64(0),
                    LoteId = reader.GetGuid(1),
                    Entidad = reader.GetString(2),
                    TipoEjecucion = reader.GetString(3),
                    FechaInicio = reader.GetDateTime(4),
                    FechaFin = reader.IsDBNull(5) ? null : reader.GetDateTime(5),
                    DuracionSegundos = reader.IsDBNull(6) ? null : reader.GetDecimal(6),
                    TotalEscaneados = reader.GetInt32(7),
                    TotalNuevos = reader.GetInt32(8),
                    TotalModificados = reader.GetInt32(9),
                    TotalErrores = reader.GetInt32(10),
                    Estado = reader.GetString(11),
                    Usuario = reader.IsDBNull(12) ? "" : reader.GetString(12)
                });
            }
            return list;
        }
    }
}
