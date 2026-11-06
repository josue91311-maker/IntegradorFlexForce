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

        // Caché en memoria de los IDs detectados en la última validación para procesarlos directo
        private static HashSet<int> _idsClientesConCambios = new();
        private static HashSet<int> _idsDireccionesZonaConCambios = new();
        private static HashSet<int> _idsDireccionesZonaQuitadas = new();

        public SyncEngine(IConfiguration config, ILogger<SyncEngine> logger)
        {
            _connectionStringForce = config.GetConnectionString("ForceGVD") 
                ?? throw new InvalidOperationException("Falta ConnectionString ForceGVD");
            _connectionStringAntiguo = config.GetConnectionString("ERPAntiguo") 
                ?? throw new InvalidOperationException("Falta ConnectionString ERPAntiguo");
            _logger = logger;
        }

        public SyncStatusDto GetStatus() => _currentStatus;

        public Task<bool> IsRunningAsync() => Task.FromResult(_syncLock.CurrentCount == 0);

        // =========================================================================
        // 1. AUDITORÍA Y DETECCIÓN REAL TABLA POR TABLA (SIN EMOJIS, COMPARACIÓN DIRECTA)
        // Compara directamente las tablas físicas de BDForceGVD contra digalimenta41
        // =========================================================================
        public async Task<SyncPreviewDto> AnalizarCambiosPendientesAsync(CancellationToken ct = default)
        {
            var sw = Stopwatch.StartNew();
            var preview = new SyncPreviewDto();
            var nuevosIdsClientes = new HashSet<int>();
            var nuevosIdsZonas = new HashSet<int>();
            var zonasQuitadasIds = new HashSet<int>();

            // ---------------------------------------------------------------------
            // A. CARGAR ESTADO ACTUAL EN FORCE (cli_Cliente y dir_Direccion)
            // ---------------------------------------------------------------------
            var clientesForcePorIdExtra = new Dictionary<int, (int IDForce, string Codigo, string Nombre, bool Estado)>(380000);
            var clientesForcePorCodigo = new Dictionary<string, (int IDForce, int? IDExtra, string Nombre, bool Estado)>(380000, StringComparer.OrdinalIgnoreCase);
            var direccionesForcePorIdExtra = new Dictionary<int, (int IDForce, string Direccion, bool Estado)>(380000);

            using (var connForce = new SqlConnection(_connectionStringForce))
            {
                await connForce.OpenAsync(ct);

                // 1. Leer cli_Cliente real de BDForceGVD
                const string sqlCliForce = @"
                    SELECT ID, IDExtra, LTRIM(RTRIM(ISNULL(Codigo, ''))), LTRIM(RTRIM(ISNULL(NombreCompleto, ''))), CAST(ISNULL(Estado, 0) AS bit)
                    FROM dbo.cli_Cliente WITH (NOLOCK)
                    WHERE Eliminado = 0;";

                using (var cmd = new SqlCommand(sqlCliForce, connForce) { CommandTimeout = 120 })
                using (var r = await cmd.ExecuteReaderAsync(ct))
                {
                    while (await r.ReadAsync(ct))
                    {
                        int idForce = r.GetInt32(0);
                        int? idExtra = r.IsDBNull(1) ? null : r.GetInt32(1);
                        string codigo = r.GetString(2);
                        string nombre = r.GetString(3);
                        bool estado = r.GetBoolean(4);

                        if (idExtra.HasValue)
                            clientesForcePorIdExtra[idExtra.Value] = (idForce, codigo, nombre, estado);
                        if (!string.IsNullOrEmpty(codigo))
                            clientesForcePorCodigo[codigo] = (idForce, idExtra, nombre, estado);
                    }
                }

                // 2. Leer dir_Direccion real de BDForceGVD
                const string sqlDirForce = @"
                    SELECT ID, IDExtra, LTRIM(RTRIM(ISNULL(Direccion, ''))), CAST(ISNULL(Estado, 1) AS bit)
                    FROM dbo.dir_Direccion WITH (NOLOCK)
                    WHERE Eliminado = 0 AND IDExtra IS NOT NULL;";

                using (var cmd = new SqlCommand(sqlDirForce, connForce) { CommandTimeout = 120 })
                using (var r = await cmd.ExecuteReaderAsync(ct))
                {
                    while (await r.ReadAsync(ct))
                    {
                        direccionesForcePorIdExtra[r.GetInt32(1)] = (r.GetInt32(0), r.GetString(2), r.GetBoolean(3));
                    }
                }
            }

            // ---------------------------------------------------------------------
            // B. COMPARAR CLIENTES Y DIRECCIONES CONTRA ERP ANTIGUO (digalimenta41)
            // ---------------------------------------------------------------------
            int totalCliOrigen = 0;
            int cliInserts = 0, cliUpdates = 0, cliDeletes = 0;
            int totalDirOrigen = 0;
            int dirInserts = 0, dirUpdates = 0, dirDeletes = 0;

            using (var connAntiguo = new SqlConnection(_connectionStringAntiguo))
            {
                await connAntiguo.OpenAsync(ct);

                const string sqlAntiguo = @"
                    SELECT ID_Cliente, CodigoCliente, NombreCompleto, EstadoCliente,
                           ID_Direccion, Direccion, EstadoDireccion
                    FROM dbo.v_Extraccion_Clientes_Force WITH (NOLOCK);";

                using var cmd = new SqlCommand(sqlAntiguo, connAntiguo) { CommandTimeout = 180 };
                using var r = await cmd.ExecuteReaderAsync(ct);

                while (await r.ReadAsync(ct))
                {
                    totalCliOrigen++;
                    int idCliente = r.GetInt32(0);
                    string codigo = r.GetString(1);
                    string nombre = r.GetString(2);
                    bool activoCli = r.GetBoolean(3);

                    int idDireccion = r.GetInt32(4);
                    string direccion = r.GetString(5);
                    bool activoDir = r.GetBoolean(6);

                    if (!string.Equals(direccion, "SIN DIRECCION", StringComparison.OrdinalIgnoreCase))
                        totalDirOrigen++;

                    // --- Evaluar Maestro de Clientes (cli_Cliente / per_Persona) ---
                    bool existeCli = clientesForcePorIdExtra.TryGetValue(idCliente, out var cliForce);
                    if (!existeCli && clientesForcePorCodigo.TryGetValue(codigo, out var cliPorCod))
                    {
                        existeCli = true;
                        cliForce = (cliPorCod.IDForce, codigo, cliPorCod.Nombre, cliPorCod.Estado);
                    }

                    if (!existeCli)
                    {
                        // Solo es un INSERT pendiente si el cliente está ACTIVO en el ERP antiguo
                        if (activoCli)
                        {
                            cliInserts++;
                            nuevosIdsClientes.Add(idCliente);
                            if (preview.DetalleCambios.Count < 250)
                            {
                                preview.DetalleCambios.Add(new CambioDetectadoItemDto
                                {
                                    Tabla = "CLIENTES (cli_Cliente / per_Persona)",
                                    TipoOperacion = "INSERT",
                                    IDOrigen = idCliente,
                                    CodigoCliente = codigo,
                                    NombreCliente = nombre,
                                    CampoModificado = "Nuevo Registro",
                                    ValorActualForce = "No existe en Force",
                                    ValorNuevoERP = $"{codigo} - {nombre}"
                                });
                            }
                        }
                    }
                    else
                    {
                        // Existe en Force: verificar si fue dado de BAJA (Estaba Activo=1 en Force y pasó a Activo=0 en ERP viejo)
                        if (cliForce.Estado && !activoCli)
                        {
                            cliDeletes++;
                            nuevosIdsClientes.Add(idCliente);
                            if (preview.DetalleCambios.Count < 250)
                            {
                                preview.DetalleCambios.Add(new CambioDetectadoItemDto
                                {
                                    Tabla = "CLIENTES (cli_Cliente / per_Persona)",
                                    TipoOperacion = "BAJA / DELETE",
                                    IDOrigen = idCliente,
                                    CodigoCliente = codigo,
                                    NombreCliente = nombre,
                                    CampoModificado = "Estado Activo",
                                    ValorActualForce = "ACTIVO (1)",
                                    ValorNuevoERP = "INACTIVO / BAJA (0)"
                                });
                            }
                        }
                        else if (!cliForce.Estado && activoCli)
                        {
                            // Fue reactivado en el ERP viejo
                            cliUpdates++;
                            nuevosIdsClientes.Add(idCliente);
                            if (preview.DetalleCambios.Count < 250)
                            {
                                preview.DetalleCambios.Add(new CambioDetectadoItemDto
                                {
                                    Tabla = "CLIENTES (cli_Cliente / per_Persona)",
                                    TipoOperacion = "UPDATE",
                                    IDOrigen = idCliente,
                                    CodigoCliente = codigo,
                                    NombreCliente = nombre,
                                    CampoModificado = "Reactivación de Cliente",
                                    ValorActualForce = "INACTIVO (0)",
                                    ValorNuevoERP = "ACTIVO (1)"
                                });
                            }
                        }
                        else if (activoCli && ! NormalizarTexto(cliForce.Nombre).Equals(NormalizarTexto(nombre), StringComparison.OrdinalIgnoreCase))
                        {
                            cliUpdates++;
                            nuevosIdsClientes.Add(idCliente);
                            if (preview.DetalleCambios.Count < 250)
                            {
                                preview.DetalleCambios.Add(new CambioDetectadoItemDto
                                {
                                    Tabla = "CLIENTES (cli_Cliente / per_Persona)",
                                    TipoOperacion = "UPDATE",
                                    IDOrigen = idCliente,
                                    CodigoCliente = codigo,
                                    NombreCliente = nombre,
                                    CampoModificado = "NombreCompleto",
                                    ValorActualForce = cliForce.Nombre,
                                    ValorNuevoERP = nombre
                                });
                            }
                        }
                    }

                    // --- Evaluar Direcciones (dir_Direccion / dir_EntidadDireccion) ---
                    // Solo evaluamos direcciones de clientes activos o ya existentes en Force
                    if (existeCli || activoCli)
                    {
                        bool existeDir = direccionesForcePorIdExtra.TryGetValue(idDireccion, out var dirForce);
                        if (!existeDir)
                        {
                            if (activoCli && activoDir && !string.Equals(direccion, "SIN DIRECCION", StringComparison.OrdinalIgnoreCase))
                            {
                                dirInserts++;
                                nuevosIdsClientes.Add(idCliente);
                                if (preview.DetalleCambios.Count < 250)
                                {
                                    preview.DetalleCambios.Add(new CambioDetectadoItemDto
                                    {
                                        Tabla = "DIRECCIONES (dir_Direccion)",
                                        TipoOperacion = "INSERT",
                                        IDOrigen = idDireccion,
                                        CodigoCliente = codigo,
                                        NombreCliente = nombre,
                                        CampoModificado = "Nueva Dirección Principal",
                                        ValorActualForce = "Sin dirección registrada",
                                        ValorNuevoERP = direccion
                                    });
                                }
                            }
                        }
                        else
                        {
                            if (dirForce.Estado && !activoDir)
                            {
                                dirDeletes++;
                                nuevosIdsClientes.Add(idCliente);
                                if (preview.DetalleCambios.Count < 250)
                                {
                                    preview.DetalleCambios.Add(new CambioDetectadoItemDto
                                    {
                                        Tabla = "DIRECCIONES (dir_Direccion)",
                                        TipoOperacion = "BAJA / DELETE",
                                        IDOrigen = idDireccion,
                                        CodigoCliente = codigo,
                                        NombreCliente = nombre,
                                        CampoModificado = "Estado Dirección",
                                        ValorActualForce = "ACTIVA (1)",
                                        ValorNuevoERP = "INACTIVA (0)"
                                    });
                                }
                            }
                            else if (activoDir && !NormalizarTexto(dirForce.Direccion).Equals(NormalizarTexto(direccion), StringComparison.OrdinalIgnoreCase))
                            {
                                dirUpdates++;
                                nuevosIdsClientes.Add(idCliente);
                                if (preview.DetalleCambios.Count < 250)
                                {
                                    preview.DetalleCambios.Add(new CambioDetectadoItemDto
                                    {
                                        Tabla = "DIRECCIONES (dir_Direccion)",
                                        TipoOperacion = "UPDATE",
                                        IDOrigen = idDireccion,
                                        CodigoCliente = codigo,
                                        NombreCliente = nombre,
                                        CampoModificado = "Dirección Física",
                                        ValorActualForce = dirForce.Direccion,
                                        ValorNuevoERP = direccion
                                    });
                                }
                            }
                        }
                    }
                }
            }

            preview.ResumenPorTabla.Add(new TablaComparacionResumenDto
            {
                CodigoTabla = "CLIENTES",
                NombreTabla = "1. Clientes y Personas",
                TablasForce = "dbo.cli_Cliente, dbo.per_Persona",
                TotalOrigen = totalCliOrigen,
                TotalDestino = clientesForcePorIdExtra.Count,
                InsertsDetectados = cliInserts,
                UpdatesDetectados = cliUpdates,
                DeletesBajasDetectados = cliDeletes,
                Observacion = (cliInserts + cliUpdates + cliDeletes) == 0 ? "Al día (Sin diferencias)" : "Cambios pendientes de aplicar"
            });

            preview.ResumenPorTabla.Add(new TablaComparacionResumenDto
            {
                CodigoTabla = "DIRECCIONES",
                NombreTabla = "2. Direcciones de Clientes",
                TablasForce = "dbo.dir_Direccion, dbo.dir_EntidadDireccion",
                TotalOrigen = totalDirOrigen,
                TotalDestino = direccionesForcePorIdExtra.Count,
                InsertsDetectados = dirInserts,
                UpdatesDetectados = dirUpdates,
                DeletesBajasDetectados = dirDeletes,
                Observacion = (dirInserts + dirUpdates + dirDeletes) == 0 ? "Al día (Sin diferencias)" : "Cambios pendientes de aplicar"
            });

            // ---------------------------------------------------------------------
            // C. COMPARAR ZONAS Y RUTAS ASIGNADAS (dir_DireccionZona / cat_Zona / rut_Ruta)
            // ---------------------------------------------------------------------
            int totalZonasOrigen = 0, totalZonasDestino = 0;
            int zonasInserts = 0, zonasUpdates = 0, zonasDeletes = 0;
            string obsZonas = "Al día (Sin diferencias)";

            try
            {
                var zonasForcePorIdDirOrigen = new Dictionary<int, (string CodigoZona, bool Estado)>(100000);

                using (var connForce = new SqlConnection(_connectionStringForce))
                {
                    await connForce.OpenAsync(ct);
                    const string sqlZonasForce = @"
                        SELECT dd.IDExtra, LTRIM(RTRIM(z.Codigo)), CAST(ISNULL(dz.Estado, 1) AS bit)
                        FROM dbo.dir_DireccionZona dz WITH (NOLOCK)
                        INNER JOIN dbo.dir_Direccion dd WITH (NOLOCK) ON dd.ID = dz.IDDireccion
                        INNER JOIN dbo.cat_Zona z WITH (NOLOCK) ON z.ID = dz.IDZona
                        WHERE dd.IDExtra IS NOT NULL AND dz.EsPrincipalVenta = 1;";

                    using var cmd = new SqlCommand(sqlZonasForce, connForce) { CommandTimeout = 120 };
                    using var r = await cmd.ExecuteReaderAsync(ct);
                    while (await r.ReadAsync(ct))
                    {
                        zonasForcePorIdDirOrigen[r.GetInt32(0)] = (r.GetString(1), r.GetBoolean(2));
                    }
                    totalZonasDestino = zonasForcePorIdDirOrigen.Count;
                }

                var dirsVistasEnOrigen = new HashSet<int>();

                using (var connAntiguo = new SqlConnection(_connectionStringAntiguo))
                {
                    await connAntiguo.OpenAsync(ct);
                    const string sqlZonasAntiguo = @"
                        SELECT ID_Direccion, ID_Cliente, CodigoCliente, NombreCliente, CodigoZonaVenta, CodigoRuta, CodigoVendedor
                        FROM dbo.vw_Sync_Cartera_Rutas_Zonas WITH (NOLOCK);";

                    using var cmd = new SqlCommand(sqlZonasAntiguo, connAntiguo) { CommandTimeout = 180 };
                    using var r = await cmd.ExecuteReaderAsync(ct);
                    while (await r.ReadAsync(ct))
                    {
                        totalZonasOrigen++;
                        int idDir = r.GetInt32(0);
                        int idCli = r.GetInt32(1);
                        string codCli = r.GetString(2);
                        string nomCli = r.GetString(3);
                        string codZona = r.GetString(4);
                        string codRuta = r.IsDBNull(5) ? "" : r.GetString(5);
                        string codVend = r.IsDBNull(6) ? "" : r.GetString(6);

                        dirsVistasEnOrigen.Add(idDir);

                        if (!zonasForcePorIdDirOrigen.TryGetValue(idDir, out var zForce))
                        {
                            zonasInserts++;
                            nuevosIdsZonas.Add(idDir);
                            if (preview.DetalleCambios.Count < 250)
                            {
                                preview.DetalleCambios.Add(new CambioDetectadoItemDto
                                {
                                    Tabla = "ZONAS Y RUTAS (dir_DireccionZona)",
                                    TipoOperacion = "INSERT",
                                    IDOrigen = idDir,
                                    CodigoCliente = codCli,
                                    NombreCliente = nomCli,
                                    CampoModificado = "Nueva Asignación Zona/Ruta",
                                    ValorActualForce = "Sin zona asignada",
                                    ValorNuevoERP = $"Zona: {codZona} | Ruta: {codRuta} | Vend: {codVend}"
                                });
                            }
                        }
                        else if (!zForce.Estado || !string.Equals(zForce.CodigoZona, codZona, StringComparison.OrdinalIgnoreCase))
                        {
                            zonasUpdates++;
                            nuevosIdsZonas.Add(idDir);
                            if (preview.DetalleCambios.Count < 250)
                            {
                                preview.DetalleCambios.Add(new CambioDetectadoItemDto
                                {
                                    Tabla = "ZONAS Y RUTAS (dir_DireccionZona)",
                                    TipoOperacion = "UPDATE",
                                    IDOrigen = idDir,
                                    CodigoCliente = codCli,
                                    NombreCliente = nomCli,
                                    CampoModificado = "Zona Movida / Reasignada",
                                    ValorActualForce = $"Zona: {zForce.CodigoZona} (Activo: {(zForce.Estado ? "Sí" : "No")})",
                                    ValorNuevoERP = $"Zona: {codZona} | Ruta: {codRuta} | Vend: {codVend}"
                                });
                            }
                        }
                    }
                }

                // Verificar asignaciones quitadas (estaban activas en Force pero ya no salen en la cartera activa del ERP Antiguo)
                foreach (var (idDirForce, zForce) in zonasForcePorIdDirOrigen)
                {
                    if (zForce.Estado && !dirsVistasEnOrigen.Contains(idDirForce))
                    {
                        zonasDeletes++;
                        zonasQuitadasIds.Add(idDirForce);
                        if (preview.DetalleCambios.Count < 250)
                        {
                            preview.DetalleCambios.Add(new CambioDetectadoItemDto
                            {
                                Tabla = "ZONAS Y RUTAS (dir_DireccionZona)",
                                TipoOperacion = "BAJA / QUITADA",
                                IDOrigen = idDirForce,
                                CodigoCliente = $"DirID:{idDirForce}",
                                NombreCliente = "Asignación retirada de cartera",
                                CampoModificado = "Zona Retirada",
                                ValorActualForce = $"Zona Activa: {zForce.CodigoZona}",
                                ValorNuevoERP = "Retirado de ruta en ERP Antiguo"
                            });
                        }
                    }
                }

                obsZonas = (zonasInserts + zonasUpdates + zonasDeletes) == 0 ? "Al día (Sin diferencias)" : "Cambios de zona detectados";
            }
            catch (Exception ex)
            {
                obsZonas = "Ejecute 04_Vista_Rutas_Zonas_Cartera.sql en digalimenta41";
                _logger.LogWarning(ex, "No se pudo consultar vw_Sync_Cartera_Rutas_Zonas");
            }

            preview.ResumenPorTabla.Add(new TablaComparacionResumenDto
            {
                CodigoTabla = "ZONAS_RUTAS",
                NombreTabla = "3. Cartera de Zonas, Rutas y Vendedor",
                TablasForce = "dbo.dir_DireccionZona, dbo.cat_Zona, dbo.rut_Ruta",
                TotalOrigen = totalZonasOrigen,
                TotalDestino = totalZonasDestino,
                InsertsDetectados = zonasInserts,
                UpdatesDetectados = zonasUpdates,
                DeletesBajasDetectados = zonasDeletes,
                Observacion = obsZonas
            });

            // ---------------------------------------------------------------------
            // D. RESUMEN DE LÍNEAS Y MODALIDADES DE CRÉDITO (ctg_ClienteCatalogo)
            // ---------------------------------------------------------------------
            int totalCreditosForce = 0;
            try
            {
                using var connForce = new SqlConnection(_connectionStringForce);
                await connForce.OpenAsync(ct);
                using var cmd = new SqlCommand("SELECT COUNT(*) FROM dbo.ctg_ClienteCatalogo WITH (NOLOCK) WHERE Eliminado = 0;", connForce);
                totalCreditosForce = (int)(await cmd.ExecuteScalarAsync(ct) ?? 0);
            }
            catch { }

            preview.ResumenPorTabla.Add(new TablaComparacionResumenDto
            {
                CodigoTabla = "CREDITOS",
                NombreTabla = "4. Líneas y Modalidades de Crédito",
                TablasForce = "dbo.ctg_ClienteCatalogo, dbo.ctg_ModalidadCreditoCatalogo",
                TotalOrigen = totalCreditosForce,
                TotalDestino = totalCreditosForce,
                InsertsDetectados = 0,
                UpdatesDetectados = 0,
                DeletesBajasDetectados = 0,
                Observacion = "Monitoreado en Force"
            });

            _idsClientesConCambios = nuevosIdsClientes;
            _idsDireccionesZonaConCambios = nuevosIdsZonas;
            _idsDireccionesZonaQuitadas = zonasQuitadasIds;

            sw.Stop();
            preview.TiempoEscaneoSegundos = Math.Round(sw.Elapsed.TotalSeconds, 2);
            _currentStatus.FaseDetalle = $"Validación completada en {preview.TiempoEscaneoSegundos}s. Detectados: {nuevosIdsClientes.Count} clientes/direcciones y {nuevosIdsZonas.Count + zonasQuitadasIds.Count} zonas por actualizar.";

            return preview;
        }

        private static string NormalizarTexto(string? input)
        {
            if (string.IsNullOrWhiteSpace(input)) return "";
            return input.Trim();
        }

        // =========================================================================
        // 2. EJECUCIÓN DE CAMBIOS DETECTADOS EN CLIENTES Y DIRECCIONES
        // Procesa ÚNICAMENTE las diferencias reales detectadas (Inserts, Updates, Bajas)
        // =========================================================================
        public async Task<SyncTransaccionDto> EjecutarSincronizacionClientesAsync(string tipoEjecucion = "MANUAL", string usuario = "PORTAL_WEB", bool incluirZonas = false, CancellationToken ct = default)
        {
            if (!await _syncLock.WaitAsync(0, ct))
            {
                return new SyncTransaccionDto { Estado = "EN_PROCESO", Usuario = usuario };
            }

            var loteId = Guid.NewGuid();
            var swTotal = Stopwatch.StartNew();
            _currentStatus.IsRunning = true;
            _currentStatus.EstadoActual = "VALIDANDO_DIFERENCIAS";
            _currentStatus.FaseDetalle = "Analizando diferencias reales entre digalimenta41 y BDForceGVD...";
            _currentStatus.ProgresoActual = 0;
            _currentStatus.ProgresoTotal = 0;

            var transaccion = new SyncTransaccionDto
            {
                LoteId = loteId,
                Entidad = incluirZonas ? "MODULO_CLIENTES_COMPLETO" : "CLIENTES_Y_DIRECCIONES",
                TipoEjecucion = tipoEjecucion,
                FechaInicio = DateTime.Now,
                Estado = "EN_PROCESO",
                Usuario = usuario
            };

            try
            {
                await RegistrarInicioLoteAsync(transaccion, ct);
                var config = await CargarConfiguracionAsync(ct);

                // Si no hay análisis previo en memoria o es corrida nueva, analizar primero las diferencias reales
                var preview = await AnalizarCambiosPendientesAsync(ct);
                var resumenCli = preview.ResumenPorTabla.FirstOrDefault(x => x.CodigoTabla == "CLIENTES");
                var resumenDir = preview.ResumenPorTabla.FirstOrDefault(x => x.CodigoTabla == "DIRECCIONES");

                transaccion.TotalEscaneados = resumenCli?.TotalOrigen ?? 0;
                transaccion.TotalNuevos = (resumenCli?.InsertsDetectados ?? 0) + (resumenDir?.InsertsDetectados ?? 0);
                transaccion.TotalModificados = (resumenCli?.UpdatesDetectados ?? 0) + (resumenCli?.DeletesBajasDetectados ?? 0)
                                             + (resumenDir?.UpdatesDetectados ?? 0) + (resumenDir?.DeletesBajasDetectados ?? 0);

                var idsPendientes = _idsClientesConCambios.ToList();
                _currentStatus.ProgresoTotal = idsPendientes.Count;

                int erroresCount = 0;

                if (idsPendientes.Count > 0)
                {
                    _currentStatus.EstadoActual = "APLICANDO_CAMBIOS_CLIENTES";
                    int batchSize = config.TamanoLote > 0 ? config.TamanoLote : 500;

                    for (int i = 0; i < idsPendientes.Count; i += batchSize)
                    {
                        ct.ThrowIfCancellationRequested();
                        var loteIds = idsPendientes.Skip(i).Take(batchSize).ToList();
                        _currentStatus.FaseDetalle = $"Aplicando cambios en Clientes y Direcciones: {Math.Min(i + batchSize, idsPendientes.Count):N0} de {idsPendientes.Count:N0}";

                        var clientesDetalle = await ObtenerDetalleClientesAsync(loteIds, ct);

                        foreach (var cliente in clientesDetalle)
                        {
                            try
                            {
                                await EjecutarUpsertClienteAsync(cliente, ct);
                            }
                            catch (Exception exCliente)
                            {
                                erroresCount++;
                                _logger.LogError(exCliente, "Error procesando cliente ID={ID}, Codigo={Codigo}", cliente.ID_Cliente, cliente.CodigoCliente);
                                await RegistrarLogAsync(loteId, "CLIENTES", "ERROR", cliente.ID_Cliente, cliente.CodigoCliente, 
                                    $"Fallo al sincronizar cliente: {exCliente.Message}", exCliente.ToString(), ct);
                            }
                            finally
                            {
                                _currentStatus.ProgresoActual++;
                            }
                        }
                    }
                }

                int zonasActualizadas = 0;
                if (incluirZonas)
                {
                    var (zCambios, zErrores) = await ProcesarZonasDiferenciasAsync(loteId, config.TamanoLote, ct);
                    zonasActualizadas = zCambios;
                    erroresCount += zErrores;
                }

                swTotal.Stop();
                transaccion.TotalErrores = erroresCount;
                transaccion.DuracionSegundos = (decimal)swTotal.Elapsed.TotalSeconds;
                transaccion.Estado = erroresCount > 0 ? "CON_ERRORES" : "EXITOSO";

                await RegistrarFinLoteAsync(transaccion, ct);

                _currentStatus.UltimaEjecucion = DateTime.Now;
                _currentStatus.UltimoLoteProcesados = idsPendientes.Count + zonasActualizadas;
                _currentStatus.UltimoLoteNuevos = transaccion.TotalNuevos;
                _currentStatus.UltimoLoteModificados = transaccion.TotalModificados;
                _currentStatus.UltimoLoteZonasActualizadas = zonasActualizadas;
                _currentStatus.UltimoLoteErrores = erroresCount;
                _currentStatus.UltimaDuracionSegundos = Math.Round(swTotal.Elapsed.TotalSeconds, 2);
                _currentStatus.EstadoActual = "COMPLETADO";
                _currentStatus.FaseDetalle = $"Sincronización finalizada en {_currentStatus.UltimaDuracionSegundos}s. Procesados: {idsPendientes.Count} clientes/direcciones, {zonasActualizadas} zonas.";

                return transaccion;
            }
            catch (Exception ex)
            {
                transaccion.Estado = "FALLIDO";
                swTotal.Stop();
                transaccion.DuracionSegundos = (decimal)swTotal.Elapsed.TotalSeconds;
                await RegistrarLogAsync(loteId, "CLIENTES", "FATAL", null, null, "Error crítico en el proceso de sincronización", ex.ToString(), ct);
                await RegistrarFinLoteAsync(transaccion, ct);
                _currentStatus.EstadoActual = "ERROR_CRITICO";
                _currentStatus.FaseDetalle = $"Error crítico: {ex.Message}";
                throw;
            }
            finally
            {
                _currentStatus.IsRunning = false;
                _syncLock.Release();
            }
        }

        // =========================================================================
        // 3. EJECUCIÓN DE CAMBIOS DETECTADOS EN ZONAS Y RUTAS
        // =========================================================================
        public async Task<SyncTransaccionDto> EjecutarSincronizacionZonasAsync(string tipoEjecucion = "MANUAL", string usuario = "PORTAL_WEB", CancellationToken ct = default)
        {
            if (!await _syncLock.WaitAsync(0, ct))
            {
                return new SyncTransaccionDto { Estado = "EN_PROCESO", Usuario = usuario };
            }

            var loteId = Guid.NewGuid();
            var swTotal = Stopwatch.StartNew();
            _currentStatus.IsRunning = true;
            _currentStatus.EstadoActual = "PROCESANDO_ZONAS";
            _currentStatus.FaseDetalle = "Detectando diferencias en Cartera de Zonas y Rutas...";

            var transaccion = new SyncTransaccionDto
            {
                LoteId = loteId,
                Entidad = "ZONAS_Y_RUTAS",
                TipoEjecucion = tipoEjecucion,
                FechaInicio = DateTime.Now,
                Estado = "EN_PROCESO",
                Usuario = usuario
            };

            try
            {
                await RegistrarInicioLoteAsync(transaccion, ct);
                var config = await CargarConfiguracionAsync(ct);

                await AnalizarCambiosPendientesAsync(ct);
                var (cambios, errores) = await ProcesarZonasDiferenciasAsync(loteId, config.TamanoLote, ct);

                swTotal.Stop();
                transaccion.TotalModificados = cambios;
                transaccion.TotalErrores = errores;
                transaccion.DuracionSegundos = (decimal)swTotal.Elapsed.TotalSeconds;
                transaccion.Estado = errores > 0 ? "CON_ERRORES" : "EXITOSO";

                await RegistrarFinLoteAsync(transaccion, ct);

                _currentStatus.UltimaEjecucion = DateTime.Now;
                _currentStatus.UltimoLoteZonasActualizadas = cambios;
                _currentStatus.UltimoLoteErrores = errores;
                _currentStatus.UltimaDuracionSegundos = Math.Round(swTotal.Elapsed.TotalSeconds, 2);
                _currentStatus.EstadoActual = "COMPLETADO";
                _currentStatus.FaseDetalle = $"Zonas y Rutas actualizadas: {cambios} registros en {_currentStatus.UltimaDuracionSegundos}s.";

                return transaccion;
            }
            finally
            {
                _currentStatus.IsRunning = false;
                _syncLock.Release();
            }
        }

        private async Task<(int Procesados, int Errores)> ProcesarZonasDiferenciasAsync(Guid loteId, int batchSize, CancellationToken ct)
        {
            var idsCambiados = _idsDireccionesZonaConCambios.ToList();
            var idsQuitados = _idsDireccionesZonaQuitadas.ToList();

            int totalOperaciones = idsCambiados.Count + idsQuitados.Count;
            if (totalOperaciones == 0) return (0, 0);

            _currentStatus.ProgresoActual = 0;
            _currentStatus.ProgresoTotal = totalOperaciones;
            int errores = 0;
            int procesados = 0;
            if (batchSize <= 0) batchSize = 500;

            // 1. Procesar Altas y Cambios de Zona/Ruta
            for (int i = 0; i < idsCambiados.Count; i += batchSize)
            {
                ct.ThrowIfCancellationRequested();
                var loteIds = idsCambiados.Skip(i).Take(batchSize).ToList();
                _currentStatus.FaseDetalle = $"Actualizando Zonas y Rutas: {Math.Min(i + batchSize, idsCambiados.Count):N0} de {idsCambiados.Count:N0}";

                var carteraDetalle = await ObtenerDetalleCarteraZonasAsync(loteIds, ct);

                foreach (var item in carteraDetalle)
                {
                    try
                    {
                        await EjecutarVincularCarteraRutaAsync(item, ct);
                        procesados++;
                    }
                    catch (Exception ex)
                    {
                        errores++;
                        await RegistrarLogAsync(loteId, "ZONAS_RUTAS", "ERROR", item.ID_Direccion, item.CodigoCliente,
                            $"Error vinculando zona ({item.CodigoZonaVenta}): {ex.Message}", ex.ToString(), ct);
                    }
                    finally
                    {
                        _currentStatus.ProgresoActual++;
                    }
                }
            }

            // 2. Procesar Asignaciones Quitadas (Inactivar en dir_DireccionZona)
            if (idsQuitados.Count > 0)
            {
                using var connForce = new SqlConnection(_connectionStringForce);
                await connForce.OpenAsync(ct);

                foreach (var idDirOrigen in idsQuitados)
                {
                    try
                    {
                        const string sqlDesactivar = @"
                            UPDATE dz
                            SET dz.Estado = 0, dz.FechaModificacion = GETDATE()
                            FROM dbo.dir_DireccionZona dz
                            INNER JOIN dbo.dir_Direccion dd ON dd.ID = dz.IDDireccion
                            WHERE dd.IDExtra = @IDDirOrigen AND dz.Estado = 1;";

                        using var cmd = new SqlCommand(sqlDesactivar, connForce);
                        cmd.Parameters.AddWithValue("@IDDirOrigen", idDirOrigen);
                        await cmd.ExecuteNonQueryAsync(ct);
                        procesados++;
                    }
                    catch (Exception ex)
                    {
                        errores++;
                        await RegistrarLogAsync(loteId, "ZONAS_RUTAS", "ERROR", idDirOrigen, null,
                            $"Error al desactivar zona retirada: {ex.Message}", ex.ToString(), ct);
                    }
                    finally
                    {
                        _currentStatus.ProgresoActual++;
                    }
                }
            }

            return (procesados, errores);
        }

        // =========================================================================
        // CONSULTAS DE EXTRACCIÓN POR LOTES Y STORED PROCEDURES
        // =========================================================================

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

        private async Task<List<CarteraRutaExtraccionDto>> ObtenerDetalleCarteraZonasAsync(List<int> idsDireccion, CancellationToken ct)
        {
            var lista = new List<CarteraRutaExtraccionDto>(idsDireccion.Count);
            using var conn = new SqlConnection(_connectionStringAntiguo);
            await conn.OpenAsync(ct);

            var inClause = string.Join(",", idsDireccion);
            var sql = $"SELECT * FROM dbo.vw_Sync_Cartera_Rutas_Zonas WITH (NOLOCK) WHERE ID_Direccion IN ({inClause});";

            using var cmd = new SqlCommand(sql, conn);
            cmd.CommandTimeout = 120;
            using var reader = await cmd.ExecuteReaderAsync(ct);

            while (await reader.ReadAsync(ct))
            {
                lista.Add(new CarteraRutaExtraccionDto
                {
                    ID_Direccion = reader.GetInt32(reader.GetOrdinal("ID_Direccion")),
                    ID_Cliente = reader.GetInt32(reader.GetOrdinal("ID_Cliente")),
                    CodigoCliente = reader.GetString(reader.GetOrdinal("CodigoCliente")),
                    NombreCliente = reader.GetString(reader.GetOrdinal("NombreCliente")),
                    CodigoVendedor = reader.IsDBNull(reader.GetOrdinal("CodigoVendedor")) ? "" : reader.GetString(reader.GetOrdinal("CodigoVendedor")),
                    NombreVendedor = reader.IsDBNull(reader.GetOrdinal("NombreVendedor")) ? "" : reader.GetString(reader.GetOrdinal("NombreVendedor")),
                    IDZonaVenta = reader.GetInt32(reader.GetOrdinal("IDZonaVenta")),
                    CodigoZonaVenta = reader.GetString(reader.GetOrdinal("CodigoZonaVenta")),
                    NombreZonaVenta = reader.IsDBNull(reader.GetOrdinal("NombreZonaVenta")) ? "" : reader.GetString(reader.GetOrdinal("NombreZonaVenta")),
                    IDZonaReparto = reader.GetInt32(reader.GetOrdinal("IDZonaReparto")),
                    CodigoZonaReparto = reader.IsDBNull(reader.GetOrdinal("CodigoZonaReparto")) ? "" : reader.GetString(reader.GetOrdinal("CodigoZonaReparto")),
                    CodigoRuta = reader.IsDBNull(reader.GetOrdinal("CodigoRuta")) ? "" : reader.GetString(reader.GetOrdinal("CodigoRuta")),
                    DiaDeSemana = reader.GetInt32(reader.GetOrdinal("DiaDeSemana"))
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

            // Validación adicional de Email en C# para nunca violar CHK_Cliente_Email
            string? emailSeguro = c.Email?.Trim();
            if (string.IsNullOrEmpty(emailSeguro) || !emailSeguro.Contains('@') || !emailSeguro.Contains('.') || emailSeguro.Contains(' '))
            {
                emailSeguro = null;
            }

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
            cmd.Parameters.AddWithValue("@Email", (object?)emailSeguro ?? DBNull.Value);
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

        private async Task EjecutarVincularCarteraRutaAsync(CarteraRutaExtraccionDto r, CancellationToken ct)
        {
            using var conn = new SqlConnection(_connectionStringForce);
            await conn.OpenAsync(ct);

            using var cmd = new SqlCommand("dbo.usp_Sync_VincularCarteraRuta", conn);
            cmd.CommandType = CommandType.StoredProcedure;
            cmd.CommandTimeout = 60;

            cmd.Parameters.AddWithValue("@ID_Cliente_Origen", r.ID_Cliente);
            cmd.Parameters.AddWithValue("@ID_Direccion_Origen", r.ID_Direccion);
            cmd.Parameters.AddWithValue("@CodigoZonaVenta", r.CodigoZonaVenta);
            cmd.Parameters.AddWithValue("@NombreZonaVenta", r.NombreZonaVenta);
            cmd.Parameters.AddWithValue("@CodigoZonaReparto", (object?)r.CodigoZonaReparto ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@CodigoRuta", r.CodigoRuta);
            cmd.Parameters.AddWithValue("@CodigoVendedor", r.CodigoVendedor);
            cmd.Parameters.AddWithValue("@NombreVendedor", r.NombreVendedor);
            cmd.Parameters.AddWithValue("@DiaDeSemana", r.DiaDeSemana);
            cmd.Parameters.AddWithValue("@OrdenVisita", 1);
            cmd.Parameters.AddWithValue("@Estado", true);

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
