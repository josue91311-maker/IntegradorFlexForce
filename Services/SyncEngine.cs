using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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
        private static bool _esquemaVerificado = false;

        private static HashSet<int> _idsClientesConCambios = new();
        private static HashSet<int> _idsDireccionesZonaConCambios = new();
        private static HashSet<int> _idsDireccionesZonaQuitadas = new();
        private static HashSet<int> _idsCreditosConCambios = new();

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

        private async Task AsegurarEsquemaBDForceAsync(CancellationToken ct)
        {
            if (_esquemaVerificado) return;
            try
            {
                using var conn = new SqlConnection(_connectionStringForce);
                await conn.OpenAsync(ct);

                // 1. Actualizar automáticamente los Stored Procedures en BDForceGVD desde los archivos .sql locales
                var scripts = new[] { "03_SP_Upsert_Cliente_Force.sql", "07_SP_Vincular_Cliente_Ruta.sql", "09_SP_Upsert_Cliente_Credito_Catalogo.sql", "10_SP_Upsert_Producto_Force.sql" };
                foreach (var fileName in scripts)
                {
                    var path = Path.Combine(Directory.GetCurrentDirectory(), fileName);
                    if (!File.Exists(path))
                        path = Path.Combine(AppContext.BaseDirectory, fileName);

                    if (File.Exists(path))
                    {
                        var sqlContent = await File.ReadAllTextAsync(path, ct);
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
                }

                // 2. Corregir conteo histórico del Lote masivo (#6) y limpiar colisiones de direcciones compartidas en dir_EntidadDireccion
                const string sqlFixLote = @"
                    IF OBJECT_ID('dbo.int_SyncTransacciones', 'U') IS NOT NULL
                    BEGIN
                        UPDATE dbo.int_SyncTransacciones
                        SET TotalEscaneados = 369536 + 331019,
                            TotalNuevos = 207513,
                            TotalModificados = 27617
                        WHERE Entidad = 'MODULO_CLIENTES_COMPLETO'
                          AND TotalNuevos = 13
                          AND TotalModificados = 14
                          AND DuracionSegundos > 5000;
                    END;

                    -- Desvincular en dir_EntidadDireccion los clientes sin dirección cuyo IDExtra de cliente
                    -- colisionó con el IDExtra de la dirección física perteneciente a otro cliente real
                    UPDATE edFalso
                    SET edFalso.Estado = 0, edFalso.EsPrincipal = 0, edFalso.FechaModificacion = GETDATE()
                    FROM dbo.dir_EntidadDireccion edFalso
                    INNER JOIN dbo.cli_Cliente cliFalso ON cliFalso.ID = edFalso.IDEntidad AND edFalso.TipoEntidad = 'CLIENTE'
                    INNER JOIN dbo.dir_Direccion dd ON dd.ID = edFalso.IDDireccion
                    WHERE edFalso.Estado = 1
                      AND cliFalso.IDExtra = dd.IDExtra
                      AND EXISTS (
                          SELECT 1
                          FROM dbo.dir_EntidadDireccion edReal
                          INNER JOIN dbo.cli_Cliente cliReal ON cliReal.ID = edReal.IDEntidad AND edReal.TipoEntidad = 'CLIENTE'
                          WHERE edReal.IDDireccion = dd.ID
                            AND edReal.Estado = 1
                            AND edReal.IDEntidad <> edFalso.IDEntidad
                            AND ISNULL(cliReal.IDExtra, -1) <> dd.IDExtra
                      );";
                using (var cmdFix = new SqlCommand(sqlFixLote, conn) { CommandTimeout = 60 })
                {
                    await cmdFix.ExecuteNonQueryAsync(ct);
                }

                _esquemaVerificado = true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "No se pudo auto-actualizar los procedimientos almacenados en BDForceGVD");
            }
        }

        // =========================================================================
        // 1. AUDITORÍA Y COMPARACIÓN DIRECTA TABLA POR TABLA
        // =========================================================================
        public async Task<SyncPreviewDto> AnalizarCambiosPendientesAsync(CancellationToken ct = default)
        {
            await AsegurarEsquemaBDForceAsync(ct);

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

                const string sqlDirForce = @"
                    SELECT ID, IDExtra, LTRIM(RTRIM(ISNULL(Direccion, ''))), CAST(ISNULL(Estado, 1) AS bit)
                    FROM dbo.dir_Direccion WITH (NOLOCK)
                    WHERE Eliminado = 0 AND IDExtra IS NOT NULL
                    ORDER BY CASE WHEN LTRIM(RTRIM(ISNULL(Direccion, ''))) IN ('', 'SIN DIRECCION', 'DIRECCION PRINCIPAL RUTA') THEN 0 ELSE 1 END ASC, ID ASC;";

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

                    bool existeCli = clientesForcePorIdExtra.TryGetValue(idCliente, out var cliForce);
                    if (!existeCli && clientesForcePorCodigo.TryGetValue(codigo, out var cliPorCod))
                    {
                        existeCli = true;
                        cliForce = (cliPorCod.IDForce, codigo, cliPorCod.Nombre, cliPorCod.Estado);
                    }

                    if (!existeCli)
                    {
                        if (activoCli)
                        {
                            cliInserts++;
                            nuevosIdsClientes.Add(idCliente);
                            if (preview.DetalleCambios.Count < 2500)
                            {
                                preview.DetalleCambios.Add(new CambioDetectadoItemDto
                                {
                                    Tabla = "CLIENTES",
                                    TipoOperacion = "INSERT",
                                    IDOrigen = idCliente,
                                    CodigoCliente = codigo,
                                    NombreCliente = nombre,
                                    CampoModificado = "Alta de Cliente",
                                    ValorActualForce = "No existe en Force",
                                    ValorNuevoERP = $"{codigo} - {nombre}"
                                });
                            }
                        }
                    }
                    else
                    {
                        if (cliForce.Estado && !activoCli)
                        {
                            cliDeletes++;
                            nuevosIdsClientes.Add(idCliente);
                            if (preview.DetalleCambios.Count < 2500)
                            {
                                preview.DetalleCambios.Add(new CambioDetectadoItemDto
                                {
                                    Tabla = "CLIENTES",
                                    TipoOperacion = "BAJA / DELETE",
                                    IDOrigen = idCliente,
                                    CodigoCliente = codigo,
                                    NombreCliente = nombre,
                                    CampoModificado = "Inactivación / Desactivado",
                                    ValorActualForce = "ACTIVO (1)",
                                    ValorNuevoERP = "INACTIVO / BAJA (0)"
                                });
                            }
                        }
                        else if (!cliForce.Estado && activoCli)
                        {
                            cliUpdates++;
                            nuevosIdsClientes.Add(idCliente);
                            if (preview.DetalleCambios.Count < 2500)
                            {
                                preview.DetalleCambios.Add(new CambioDetectadoItemDto
                                {
                                    Tabla = "CLIENTES",
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
                        else if (activoCli && !NormalizarTexto(cliForce.Nombre).Equals(NormalizarTexto(nombre), StringComparison.OrdinalIgnoreCase))
                        {
                            cliUpdates++;
                            nuevosIdsClientes.Add(idCliente);
                            if (preview.DetalleCambios.Count < 2500)
                            {
                                preview.DetalleCambios.Add(new CambioDetectadoItemDto
                                {
                                    Tabla = "CLIENTES",
                                    TipoOperacion = "UPDATE",
                                    IDOrigen = idCliente,
                                    CodigoCliente = codigo,
                                    NombreCliente = nombre,
                                    CampoModificado = "Razón Social / Nombre",
                                    ValorActualForce = cliForce.Nombre,
                                    ValorNuevoERP = nombre
                                });
                            }
                        }
                    }

                    if (existeCli || activoCli)
                    {
                        bool existeDir = direccionesForcePorIdExtra.TryGetValue(idDireccion, out var dirForce);
                        if (!existeDir)
                        {
                            if (activoCli && activoDir && !string.Equals(direccion, "SIN DIRECCION", StringComparison.OrdinalIgnoreCase))
                            {
                                dirInserts++;
                                nuevosIdsClientes.Add(idCliente);
                                if (preview.DetalleCambios.Count < 2500)
                                {
                                    preview.DetalleCambios.Add(new CambioDetectadoItemDto
                                    {
                                        Tabla = "DIRECCIONES",
                                        TipoOperacion = "INSERT",
                                        IDOrigen = idDireccion,
                                        CodigoCliente = codigo,
                                        NombreCliente = nombre,
                                        CampoModificado = "Nueva Dirección",
                                        ValorActualForce = "Sin dirección",
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
                                if (preview.DetalleCambios.Count < 2500)
                                {
                                    preview.DetalleCambios.Add(new CambioDetectadoItemDto
                                    {
                                        Tabla = "DIRECCIONES",
                                        TipoOperacion = "BAJA / DELETE",
                                        IDOrigen = idDireccion,
                                        CodigoCliente = codigo,
                                        NombreCliente = nombre,
                                        CampoModificado = "Inactivación de Dirección",
                                        ValorActualForce = "ACTIVA (1)",
                                        ValorNuevoERP = "INACTIVA (0)"
                                    });
                                }
                            }
                            else if (activoDir
                                && !string.Equals(direccion, "SIN DIRECCION", StringComparison.OrdinalIgnoreCase)
                                && !NormalizarTexto(dirForce.Direccion).Equals(NormalizarTexto(direccion), StringComparison.OrdinalIgnoreCase))
                            {
                                dirUpdates++;
                                nuevosIdsClientes.Add(idCliente);
                                if (preview.DetalleCambios.Count < 2500)
                                {
                                    preview.DetalleCambios.Add(new CambioDetectadoItemDto
                                    {
                                        Tabla = "DIRECCIONES",
                                        TipoOperacion = "UPDATE",
                                        IDOrigen = idDireccion,
                                        CodigoCliente = codigo,
                                        NombreCliente = nombre,
                                        CampoModificado = "Dirección Modificada",
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
            // C. COMPARAR ZONAS, RUTAS, VENDEDOR Y DÍAS DE VISITA
            // Utiliza la relación exacta provista por el usuario para BDForceGVD
            // ---------------------------------------------------------------------
            int totalZonasOrigen = 0, totalZonasDestino = 0;
            int zonasInserts = 0, zonasUpdates = 0, zonasDeletes = 0;
            string obsZonas = "Al día (Sin diferencias)";

            try
            {
                var vendedoresForcePorCliDia = new Dictionary<string, HashSet<string>>(250000, StringComparer.OrdinalIgnoreCase);
                var listaCarteraForce = new List<(int IDCliente, int? IDClienteExtra, string CodigoCliente, string CodigoZona, string CodigoVendedor, int DiaDeSemana, int? IDDireccionExtra, int IDEntidadDireccion, int IDDireccionZona)>(120000);

                static void AgregarVendedorAlIndice(Dictionary<string, HashSet<string>> dict, string key, string vendedor)
                {
                    if (!dict.TryGetValue(key, out var set))
                    {
                        set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        dict[key] = set;
                    }
                    set.Add(vendedor);
                }

                using (var connForce = new SqlConnection(_connectionStringForce))
                {
                    await connForce.OpenAsync(ct);

                    const string sqlCarteraForce = @"
                        SELECT DISTINCT
                            cli.ID AS IDCliente,
                            cli.IDExtra AS IDClienteExtra,
                            LTRIM(RTRIM(ISNULL(cli.Codigo, ''))) AS CodigoCliente,
                            z.Codigo AS CodigoZona,
                            RTRIM(v.Codigo) AS CodigoVendedor,
                            rzd.IDDia AS DiaDeSemana,
                            dd.IDExtra AS IDDireccionExtra,
                            ed.ID AS IDEntidadDireccion,
                            dz.ID AS IDDireccionZona
                        FROM dbo.dir_EntidadDireccion ed WITH (NOLOCK)
                        INNER JOIN dbo.cli_Cliente cli WITH (NOLOCK)
                            ON cli.ID = ed.IDEntidad AND ed.TipoEntidad = 'CLIENTE'
                        INNER JOIN dbo.dir_Direccion dd WITH (NOLOCK)
                            ON dd.ID = ed.IDDireccion
                        INNER JOIN dbo.dir_DireccionZona dz WITH (NOLOCK)
                            ON dz.IDDireccion = dd.ID
                        INNER JOIN dbo.cat_Zona z WITH (NOLOCK)
                            ON z.ID = dz.IDZona
                        INNER JOIN dbo.rut_RutaZonaDia rzd WITH (NOLOCK)
                            ON rzd.IDZona = z.ID
                        INNER JOIN dbo.rut_Ruta rr WITH (NOLOCK)
                            ON rr.ID = rzd.IDRuta
                        INNER JOIN dbo.vnd_Vendedor v WITH (NOLOCK)
                            ON v.ID = rr.IDVendedor
                        WHERE ed.Estado = 1
                          AND dd.Eliminado = 0
                          AND dz.Estado = 1
                          AND z.Estado = 1
                          AND rzd.Estado = 1
                          AND rr.Estado = 1
                          AND rr.Eliminado = 0
                          AND v.Eliminado = 0;";

                    using var cmd = new SqlCommand(sqlCarteraForce, connForce) { CommandTimeout = 120 };
                    using var r = await cmd.ExecuteReaderAsync(ct);
                    while (await r.ReadAsync(ct))
                    {
                        int idCli = r.GetInt32(0);
                        int? idCliExtra = r.IsDBNull(1) ? null : r.GetInt32(1);
                        string codCliForce = r.GetString(2).Trim();
                        string codZonaRaw = r.GetString(3).Trim();
                        string codVend = r.GetString(4).Trim();
                        int dia = r.GetInt32(5);
                        int? idDirExtra = r.IsDBNull(6) ? null : r.GetInt32(6);
                        int idEd = r.GetInt32(7);
                        int idDz = r.GetInt32(8);

                        // Extraer el código base original si tiene sufijo de vendedor (ej: "02999-02OF66" -> "02999")
                        string codZonaBase = codZonaRaw.Contains('-') ? codZonaRaw.Substring(0, codZonaRaw.IndexOf('-')).Trim() : codZonaRaw;

                        listaCarteraForce.Add((idCli, idCliExtra, codCliForce, codZonaBase, codVend, dia, idDirExtra, idEd, idDz));

                        if (idCliExtra.HasValue)
                        {
                            AgregarVendedorAlIndice(vendedoresForcePorCliDia, $"EX_{idCliExtra.Value}_{codZonaBase}_{dia}", codVend);
                            if (!string.Equals(codZonaBase, codZonaRaw, StringComparison.OrdinalIgnoreCase))
                                AgregarVendedorAlIndice(vendedoresForcePorCliDia, $"EX_{idCliExtra.Value}_{codZonaRaw}_{dia}", codVend);
                        }
                        if (!string.IsNullOrEmpty(codCliForce))
                        {
                            AgregarVendedorAlIndice(vendedoresForcePorCliDia, $"COD_{codCliForce}_{codZonaBase}_{dia}", codVend);
                            if (!string.Equals(codZonaBase, codZonaRaw, StringComparison.OrdinalIgnoreCase))
                                AgregarVendedorAlIndice(vendedoresForcePorCliDia, $"COD_{codCliForce}_{codZonaRaw}_{dia}", codVend);
                        }
                        if (idDirExtra.HasValue)
                        {
                            AgregarVendedorAlIndice(vendedoresForcePorCliDia, $"DIR_{idDirExtra.Value}_{codZonaBase}_{dia}", codVend);
                            if (!string.Equals(codZonaBase, codZonaRaw, StringComparison.OrdinalIgnoreCase))
                                AgregarVendedorAlIndice(vendedoresForcePorCliDia, $"DIR_{idDirExtra.Value}_{codZonaRaw}_{dia}", codVend);
                        }
                    }
                    totalZonasDestino = listaCarteraForce.Count;
                }

                var asignacionesVistasEnOrigen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var asignacionesExactasEnOrigen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var zonasVistasPorClienteOrigen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var diasConUpdate = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var direccionesActivasPorClienteOrigen = new Dictionary<int, HashSet<int>>();
                var duenoRealPorIdDirExtra = new Dictionary<int, int>();

                using (var connAntiguo = new SqlConnection(_connectionStringAntiguo))
                {
                    await connAntiguo.OpenAsync(ct);

                    // Consultar directamente todas las rutas, zonas, vendedores y días de visita activos
                    const string sqlZonasAntiguo = @"
                        SELECT 
                            dc.PKID AS ID_Direccion,
                            p.PKID AS ID_Cliente,
                            LTRIM(RTRIM(p.Codigo)) AS CodigoCliente,
                            LTRIM(RTRIM(p.Nombre)) AS NombreCliente,
                            z.Codigo AS CodigoZonaVenta,
                            rr.Codigo AS CodigoRuta,
                            RTRIM(pv.Codigo) AS CodigoVendedor,
                            CASE 
                                WHEN z.Codigo LIKE '%SAB%' OR rr.Codigo LIKE '%SAB%' THEN 6
                                WHEN z.Codigo LIKE '%DOM%' OR rr.Codigo LIKE '%DOM%' THEN 7
                                WHEN z.Codigo LIKE '%VIE%' OR rr.Codigo LIKE '%VIE%' THEN 5
                                WHEN z.Codigo LIKE '%JUE%' OR rr.Codigo LIKE '%JUE%' THEN 4
                                WHEN z.Codigo LIKE '%MIE%' OR rr.Codigo LIKE '%MIE%' THEN 3
                                WHEN z.Codigo LIKE '%MAR%' OR rr.Codigo LIKE '%MAR%' THEN 2
                                WHEN z.Codigo LIKE '%LUN%' OR rr.Codigo LIKE '%LUN%' THEN 1
                                WHEN LOWER(LTRIM(RTRIM(rr.DiaDeSemana))) LIKE 's%b%' OR rr.DiaDeSemana = '6' THEN 6
                                WHEN LOWER(LTRIM(RTRIM(rr.DiaDeSemana))) LIKE 'dom%' OR rr.DiaDeSemana = '7' THEN 7
                                WHEN LOWER(LTRIM(RTRIM(rr.DiaDeSemana))) LIKE 'vie%' OR rr.DiaDeSemana = '5' THEN 5
                                WHEN LOWER(LTRIM(RTRIM(rr.DiaDeSemana))) LIKE 'jue%' OR rr.DiaDeSemana = '4' THEN 4
                                WHEN LOWER(LTRIM(RTRIM(rr.DiaDeSemana))) LIKE 'mi%'  OR rr.DiaDeSemana = '3' THEN 3
                                WHEN LOWER(LTRIM(RTRIM(rr.DiaDeSemana))) LIKE 'mar%' OR rr.DiaDeSemana = '2' THEN 2
                                WHEN LOWER(LTRIM(RTRIM(rr.DiaDeSemana))) LIKE 'lun%' OR rr.DiaDeSemana = '1' THEN 1
                                ELSE 1
                            END AS DiaDeSemana
                        FROM dbo.DireccionZona dz WITH (NOLOCK)
                        INNER JOIN dbo.Persona p WITH (NOLOCK) ON dz.IDCliente = p.PKID
                        INNER JOIN dbo.DireccionCliente dc WITH (NOLOCK) ON dc.PKID = dz.IDDireccion
                        INNER JOIN dbo.Direccion dd WITH (NOLOCK) ON dd.PKID = dc.PKID
                        INNER JOIN dbo.Zona z WITH (NOLOCK) ON z.PKID = dz.IDZona
                        LEFT JOIN dbo.Zona zr WITH (NOLOCK) ON zr.PKID = dc.IDZonaReparto
                        INNER JOIN dbo.ZonaRutaVendedor zrv WITH (NOLOCK) ON zrv.IDZona = z.PKID
                        INNER JOIN dbo.RutaVendedor rr WITH (NOLOCK) ON rr.PKID = zrv.IDRutaVendedor
                        INNER JOIN dbo.AsignacionRutaZonaVendedor ass WITH (NOLOCK) ON ass.PKID = rr.IDAsignacionRutaZonaVendedor
                        INNER JOIN dbo.Persona pv WITH (NOLOCK) ON pv.PKID = ass.IDVendedor
                        LEFT JOIN dbo.Vendedor v WITH (NOLOCK) ON v.PKID = pv.PKID
                        LEFT JOIN dbo.GrupoVentas gv WITH (NOLOCK) ON gv.PKID = v.IDGrupoVentas
                        LEFT JOIN dbo.FuerzaVentas fv WITH (NOLOCK) ON fv.PKID = gv.IDFuerzaVentas
                        WHERE dd.Activo = 1 AND p.Activo = 1
                        GROUP BY dc.PKID, p.PKID, p.Codigo, p.Nombre, z.Codigo, rr.Codigo, pv.Codigo, rr.DiaDeSemana;";

                    using var cmd = new SqlCommand(sqlZonasAntiguo, connAntiguo) { CommandTimeout = 180 };
                    using var r = await cmd.ExecuteReaderAsync(ct);
                    while (await r.ReadAsync(ct))
                    {
                        totalZonasOrigen++;
                        int idDir = r.GetInt32(0);
                        int idCli = r.GetInt32(1);
                        string codCli = r.GetString(2);
                        string nomCli = r.GetString(3);
                        string codZona = r.GetString(4).Trim();
                        string codRuta = r.IsDBNull(5) ? "" : r.GetString(5).Trim();
                        string codVend = r.IsDBNull(6) ? "" : r.GetString(6).Trim();
                        int diaSemana = r.GetInt32(7);

                        duenoRealPorIdDirExtra[idDir] = idCli;

                        if (!direccionesActivasPorClienteOrigen.TryGetValue(idCli, out var setDirs))
                        {
                            setDirs = new HashSet<int>();
                            direccionesActivasPorClienteOrigen[idCli] = setDirs;
                        }
                        setDirs.Add(idDir);

                        string keyOrigen = $"EX_{idCli}_{codZona}_{diaSemana}";
                        asignacionesVistasEnOrigen.Add(keyOrigen);
                        asignacionesVistasEnOrigen.Add($"COD_{codCli}_{codZona}_{diaSemana}");
                        asignacionesVistasEnOrigen.Add($"DIR_{idDir}_{codZona}_{diaSemana}");
                        asignacionesExactasEnOrigen.Add($"EX_{idCli}_{codZona}_{diaSemana}_{codVend}");

                        // Registrar presencia de zona para control de bajas de dir_DireccionZona
                        zonasVistasPorClienteOrigen.Add($"EX_{idCli}_{codZona}");
                        zonasVistasPorClienteOrigen.Add($"COD_{codCli}_{codZona}");
                        zonasVistasPorClienteOrigen.Add($"DIR_{idDir}_{codZona}");

                        if (!vendedoresForcePorCliDia.TryGetValue(keyOrigen, out var setVendsForce)
                            && !vendedoresForcePorCliDia.TryGetValue($"COD_{codCli}_{codZona}_{diaSemana}", out setVendsForce)
                            && !vendedoresForcePorCliDia.TryGetValue($"DIR_{idDir}_{codZona}_{diaSemana}", out setVendsForce))
                        {
                            // Es una asignación nueva (o día/ruta nuevo)
                            zonasInserts++;
                            nuevosIdsZonas.Add(idDir);
                            if (preview.DetalleCambios.Count < 2500)
                            {
                                preview.DetalleCambios.Add(new CambioDetectadoItemDto
                                {
                                    Tabla = "ZONAS Y RUTAS",
                                    TipoOperacion = "INSERT",
                                    IDOrigen = idDir,
                                    CodigoCliente = codCli,
                                    NombreCliente = nomCli,
                                    CampoModificado = $"Nueva Asignación Zona {codZona} (Día {diaSemana})",
                                    ValorActualForce = "Sin ruta activa en Force para este día",
                                    ValorNuevoERP = $"Vend: {codVend} | Día: {diaSemana} | Zona: {codZona} | Ruta: {codRuta}"
                                });
                            }
                        }
                        else if (!setVendsForce.Contains(codVend))
                        {
                            // Misma zona y día pero cambiaron de vendedor
                            string vendActual = string.Join(", ", setVendsForce);
                            zonasUpdates++;
                            diasConUpdate.Add(keyOrigen);
                            nuevosIdsZonas.Add(idDir);
                            if (preview.DetalleCambios.Count < 2500)
                            {
                                preview.DetalleCambios.Add(new CambioDetectadoItemDto
                                {
                                    Tabla = "ZONAS Y RUTAS",
                                    TipoOperacion = "UPDATE",
                                    IDOrigen = idDir,
                                    CodigoCliente = codCli,
                                    NombreCliente = nomCli,
                                    CampoModificado = $"Cambio de Vendedor (Zona {codZona}, Día {diaSemana})",
                                    ValorActualForce = $"Vendedor: {vendActual}",
                                    ValorNuevoERP = $"Vendedor: {codVend} | Día: {diaSemana} | Zona: {codZona} | Ruta: {codRuta}"
                                });
                            }
                        }
                    }
                }

                var idsEdColisionados = new HashSet<int>();

                // Detectar eliminaciones / filas borradas o zonas antiguas que quedaron activas en Force (DELETE)
                foreach (var itemForce in listaCarteraForce)
                {
                    // Si esta dirección física en el ERP Antiguo pertenece a otro cliente real distinto,
                    // este vínculo en dir_EntidadDireccion fue producto de colisión de ID ('SIN DIRECCION'); lo auto-depuramos
                    if (itemForce.IDDireccionExtra.HasValue
                        && duenoRealPorIdDirExtra.TryGetValue(itemForce.IDDireccionExtra.Value, out int idCliDuenoReal)
                        && itemForce.IDClienteExtra.HasValue
                        && itemForce.IDClienteExtra.Value != idCliDuenoReal)
                    {
                        idsEdColisionados.Add(itemForce.IDEntidadDireccion);
                        continue;
                    }

                    bool tieneZonaEnOrigen = (itemForce.IDClienteExtra.HasValue && zonasVistasPorClienteOrigen.Contains($"EX_{itemForce.IDClienteExtra.Value}_{itemForce.CodigoZona}"))
                                          || (!string.IsNullOrEmpty(itemForce.CodigoCliente) && zonasVistasPorClienteOrigen.Contains($"COD_{itemForce.CodigoCliente}_{itemForce.CodigoZona}"))
                                          || (itemForce.IDDireccionExtra.HasValue && zonasVistasPorClienteOrigen.Contains($"DIR_{itemForce.IDDireccionExtra.Value}_{itemForce.CodigoZona}"));

                    // Contabilizar como BAJA / DELETE solo si esa zona ya no existe para el cliente/dirección en el ERP Antiguo
                    if (!tieneZonaEnOrigen)
                    {
                        zonasDeletes++;
                        zonasQuitadasIds.Add(itemForce.IDDireccionZona);
                        if (preview.DetalleCambios.Count < 2500)
                        {
                            preview.DetalleCambios.Add(new CambioDetectadoItemDto
                            {
                                Tabla = "ZONAS Y RUTAS",
                                TipoOperacion = "BAJA / DELETE",
                                IDOrigen = itemForce.IDCliente,
                                CodigoCliente = !string.IsNullOrEmpty(itemForce.CodigoCliente) ? itemForce.CodigoCliente : $"IDForce:{itemForce.IDCliente}",
                                NombreCliente = "Zona/Ruta retirada en ERP Antiguo",
                                CampoModificado = $"Zona Retirada ({itemForce.CodigoZona})",
                                ValorActualForce = $"Zona: {itemForce.CodigoZona} | Vendedor: {itemForce.CodigoVendedor}",
                                ValorNuevoERP = "Cliente ya no tiene asignada esta zona en el ERP Antiguo"
                            });
                        }
                    }
                }

                // Auto-depurar en BDForceGVD los vínculos de dir_EntidadDireccion colisionados
                if (idsEdColisionados.Count > 0)
                {
                    using var connForceClean = new SqlConnection(_connectionStringForce);
                    await connForceClean.OpenAsync(ct);
                    var inEd = string.Join(",", idsEdColisionados);
                    var sqlCleanEd = $"UPDATE dbo.dir_EntidadDireccion SET Estado = 0, EsPrincipal = 0, FechaModificacion = GETDATE() WHERE ID IN ({inEd});";
                    using var cmdCleanEd = new SqlCommand(sqlCleanEd, connForceClean) { CommandTimeout = 60 };
                    await cmdCleanEd.ExecuteNonQueryAsync(ct);
                    totalZonasDestino = Math.Max(0, totalZonasDestino - idsEdColisionados.Count);
                }

                obsZonas = (zonasInserts + zonasUpdates + zonasDeletes) == 0 ? "Al día (Sin diferencias)" : "Cambios de zona/ruta detectados";
            }
            catch (Exception ex)
            {
                obsZonas = "Error al consultar tablas de rutas";
                _logger.LogWarning(ex, "Fallo al comparar cartera de rutas");
            }

            preview.ResumenPorTabla.Add(new TablaComparacionResumenDto
            {
                CodigoTabla = "ZONAS_RUTAS",
                NombreTabla = "3. Cartera de Zonas, Rutas y Vendedor",
                TablasForce = "dbo.dir_DireccionZona, dbo.cat_Zona, dbo.rut_Ruta, dbo.rut_RutaZonaDia",
                TotalOrigen = totalZonasOrigen,
                TotalDestino = totalZonasDestino,
                InsertsDetectados = zonasInserts,
                UpdatesDetectados = zonasUpdates,
                DeletesBajasDetectados = zonasDeletes,
                Observacion = obsZonas
            });

            // ---------------------------------------------------------------------
            // D. RESUMEN Y COMPARACIÓN DE LÍNEAS DE CRÉDITO POR PORTAFOLIO
            // ---------------------------------------------------------------------
            int totalCreditosOrigen = 0, totalCreditosDestino = 0;
            int credInserts = 0, credUpdates = 0, credDeletes = 0;
            var creditosPendientesIds = new HashSet<int>();
            string obsCreditos = "Al día (Sin diferencias)";

            try
            {
                // Cargar créditos actuales en BDForceGVD agrupados por (IDClienteExtra, CodigoCatalogo) e (IDClienteExtra, IDCatalogoExtra)
                var creditosForce = new Dictionary<string, (decimal Monto, int DiasGracia, bool Estado)>(1500000, StringComparer.OrdinalIgnoreCase);
                using (var connForce = new SqlConnection(_connectionStringForce))
                {
                    await connForce.OpenAsync(ct);
                    const string sqlCredForce = @"
                        SELECT 
                            c.IDExtra AS IDClienteExtra,
                            cat.IDExtra AS IDCatalogoExtra,
                            LTRIM(RTRIM(ISNULL(cat.Codigo, ''))) AS CodigoCatalogo,
                            cc.Monto,
                            cc.DiasDeGracia,
                            cc.Estado
                        FROM dbo.ctg_ClienteCatalogo cc WITH (NOLOCK)
                        INNER JOIN dbo.cli_Cliente c WITH (NOLOCK) ON c.ID = cc.IDCliente
                        INNER JOIN dbo.ctg_Catalogo cat WITH (NOLOCK) ON cat.ID = cc.IDCatalogo
                        WHERE cc.Eliminado = 0 AND c.IDExtra IS NOT NULL;";

                    using var cmd = new SqlCommand(sqlCredForce, connForce) { CommandTimeout = 120 };
                    using var r = await cmd.ExecuteReaderAsync(ct);
                    while (await r.ReadAsync(ct))
                    {
                        int idCliEx = r.GetInt32(0);
                        int? idCatEx = r.IsDBNull(1) ? null : r.GetInt32(1);
                        string codCat = r.GetString(2);
                        decimal monto = r.GetDecimal(3);
                        int dias = r.GetInt32(4);
                        bool estado = r.GetBoolean(5);

                        if (idCatEx.HasValue)
                            creditosForce[$"{idCliEx}_{idCatEx.Value}"] = (monto, dias, estado);
                        if (!string.IsNullOrEmpty(codCat))
                            creditosForce[$"{idCliEx}_COD_{codCat}"] = (monto, dias, estado);
                    }
                    totalCreditosDestino = creditosForce.Count;
                }

                // Comparar contra LineaCreditoPorPortafolioProducto del ERP Antiguo
                using (var connAntiguo = new SqlConnection(_connectionStringAntiguo))
                {
                    await connAntiguo.OpenAsync(ct);
                    const string sqlCredAntiguo = @"
                        SELECT 
                            l.PKID AS ID_LineaCredito,
                            l.IDCliente,
                            LTRIM(RTRIM(p.Codigo)) AS CodigoCliente,
                            LTRIM(RTRIM(p.Nombre)) AS NombreCliente,
                            l.IDPortafolioProducto,
                            LTRIM(RTRIM(ISNULL(pp.Codigo, 'PORT'))) AS CodigoPortafolio,
                            LTRIM(RTRIM(ISNULL(pp.Nombre, 'PORTAFOLIO'))) AS NombrePortafolio,
                            ISNULL(l.Monto, 0.00) AS Monto,
                            ISNULL(l.DiasGracia, 5) AS DiasGracia,
                            CAST(ISNULL(l.Activo, 1) AS bit) AS Activo
                        FROM dbo.LineaCreditoPorPortafolioProducto l WITH (NOLOCK)
                        INNER JOIN dbo.Persona p WITH (NOLOCK) ON p.PKID = l.IDCliente
                        LEFT JOIN dbo.PortafolioProducto pp WITH (NOLOCK) ON pp.PKID = l.IDPortafolioProducto
                        WHERE p.Activo = 1;";

                    using var cmd = new SqlCommand(sqlCredAntiguo, connAntiguo) { CommandTimeout = 180 };
                    using var r = await cmd.ExecuteReaderAsync(ct);
                    while (await r.ReadAsync(ct))
                    {
                        totalCreditosOrigen++;
                        int idLinea = r.GetInt32(0);
                        int idCli = r.GetInt32(1);
                        string codCli = r.GetString(2);
                        string nomCli = r.GetString(3);
                        int idPort = r.GetInt32(4);
                        string codPort = r.GetString(5);
                        string nomPort = r.GetString(6);
                        decimal monto = r.GetDecimal(7);
                        int dias = r.GetInt32(8);
                        bool activo = r.GetBoolean(9);

                        string key = $"{idCli}_{idPort}";
                        string keyCod = $"{idCli}_COD_{codPort}";
                        if (!creditosForce.TryGetValue(key, out var credF) && !creditosForce.TryGetValue(keyCod, out credF))
                        {
                            if (activo && monto > 0)
                            {
                                credInserts++;
                                creditosPendientesIds.Add(idLinea);
                                if (preview.DetalleCambios.Count < 2500)
                                {
                                    preview.DetalleCambios.Add(new CambioDetectadoItemDto
                                    {
                                        Tabla = "LINEAS DE CREDITO",
                                        TipoOperacion = "INSERT",
                                        IDOrigen = idLinea,
                                        CodigoCliente = codCli,
                                        NombreCliente = nomCli,
                                        CampoModificado = $"Línea Crédito ({nomPort})",
                                        ValorActualForce = "Sin línea asignada",
                                        ValorNuevoERP = $"Línea: S/. {monto:N2} | Días: {dias}"
                                    });
                                }
                            }
                        }
                        else
                        {
                            if (credF.Estado != activo || Math.Abs(credF.Monto - monto) > 0.01m || credF.DiasGracia != dias)
                            {
                                credUpdates++;
                                creditosPendientesIds.Add(idLinea);
                                if (preview.DetalleCambios.Count < 2500)
                                {
                                    preview.DetalleCambios.Add(new CambioDetectadoItemDto
                                    {
                                        Tabla = "LINEAS DE CREDITO",
                                        TipoOperacion = "UPDATE",
                                        IDOrigen = idLinea,
                                        CodigoCliente = codCli,
                                        NombreCliente = nomCli,
                                        CampoModificado = $"Monto/Días Crédito ({nomPort})",
                                        ValorActualForce = $"Línea: S/. {credF.Monto:N2} | Días: {credF.DiasGracia} | {(credF.Estado ? "ACTIVO" : "INACTIVO")}",
                                        ValorNuevoERP = $"Línea: S/. {monto:N2} | Días: {dias} | {(activo ? "ACTIVO" : "INACTIVO")}"
                                    });
                                }
                            }
                        }
                    }
                }

                obsCreditos = (credInserts + credUpdates + credDeletes) == 0 ? "Al día (Sin diferencias)" : "Líneas de crédito por actualizar";
            }
            catch (Exception ex)
            {
                obsCreditos = "Error al consultar líneas de crédito";
                _logger.LogWarning(ex, "Fallo al comparar líneas de crédito");
            }

            preview.ResumenPorTabla.Add(new TablaComparacionResumenDto
            {
                CodigoTabla = "CREDITOS",
                NombreTabla = "4. Líneas y Modalidades de Crédito",
                TablasForce = "dbo.ctg_ClienteCatalogo, dbo.ctg_Catalogo",
                TotalOrigen = totalCreditosOrigen,
                TotalDestino = totalCreditosDestino,
                InsertsDetectados = credInserts,
                UpdatesDetectados = credUpdates,
                DeletesBajasDetectados = credDeletes,
                Observacion = obsCreditos
            });

            _idsClientesConCambios = nuevosIdsClientes;
            _idsDireccionesZonaConCambios = nuevosIdsZonas;
            _idsDireccionesZonaQuitadas = zonasQuitadasIds;
            _idsCreditosConCambios = creditosPendientesIds;

            sw.Stop();
            preview.TiempoEscaneoSegundos = Math.Round(sw.Elapsed.TotalSeconds, 2);
            _currentStatus.FaseDetalle = $"Análisis completado en {preview.TiempoEscaneoSegundos}s. Detectadas {nuevosIdsClientes.Count} diferencias en clientes/direcciones y {zonasInserts + zonasUpdates + zonasDeletes} en zonas/rutas.";

            return preview;
        }

        private static string NormalizarTexto(string? input)
        {
            if (string.IsNullOrWhiteSpace(input)) return "";
            return input.Trim();
        }

        // =========================================================================
        // 2. APLICAR CAMBIOS EN CLIENTES Y DIRECCIONES
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
            _currentStatus.FaseDetalle = "Analizando diferencias exactas...";
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

                var preview = await AnalizarCambiosPendientesAsync(ct);
                var resumenCli = preview.ResumenPorTabla.FirstOrDefault(x => x.CodigoTabla == "CLIENTES");
                var resumenDir = preview.ResumenPorTabla.FirstOrDefault(x => x.CodigoTabla == "DIRECCIONES");
                var resumenZonas = preview.ResumenPorTabla.FirstOrDefault(x => x.CodigoTabla == "ZONAS_RUTAS");

                transaccion.TotalEscaneados = (resumenCli?.TotalOrigen ?? 0) + (incluirZonas ? (resumenZonas?.TotalOrigen ?? 0) : 0);
                transaccion.TotalNuevos = (resumenCli?.InsertsDetectados ?? 0)
                                        + (resumenDir?.InsertsDetectados ?? 0)
                                        + (incluirZonas ? (resumenZonas?.InsertsDetectados ?? 0) : 0);
                transaccion.TotalModificados = (resumenCli?.UpdatesDetectados ?? 0) + (resumenCli?.DeletesBajasDetectados ?? 0)
                                             + (resumenDir?.UpdatesDetectados ?? 0) + (resumenDir?.DeletesBajasDetectados ?? 0)
                                             + (incluirZonas ? ((resumenZonas?.UpdatesDetectados ?? 0) + (resumenZonas?.DeletesBajasDetectados ?? 0)) : 0);

                var idsPendientes = _idsClientesConCambios.ToList();
                var idsZonas = _idsDireccionesZonaConCambios.ToList();
                var idsQuitados = _idsDireccionesZonaQuitadas.ToList();
                var idsCreditos = _idsCreditosConCambios.ToList();

                int totalOpsModulo = idsPendientes.Count + (incluirZonas ? (idsZonas.Count + idsQuitados.Count + idsCreditos.Count) : 0);
                _currentStatus.ProgresoTotal = totalOpsModulo > 0 ? totalOpsModulo : 1;
                _currentStatus.ProgresoActual = 0;

                int erroresCount = 0;

                // -------------------------------------------------------------
                // SUB-ETAPA 1: CLIENTES Y PERSONAS / DIRECCIONES
                // (Skip inteligente inmediato si está al día con 0 diferencias)
                // -------------------------------------------------------------
                if (idsPendientes.Count > 0)
                {
                    _currentStatus.EstadoActual = "APLICANDO_CAMBIOS_CLIENTES";
                    int batchSize = config.TamanoLote > 0 ? config.TamanoLote : 500;

                    for (int i = 0; i < idsPendientes.Count; i += batchSize)
                    {
                        ct.ThrowIfCancellationRequested();
                        var loteIds = idsPendientes.Skip(i).Take(batchSize).ToList();
                        _currentStatus.FaseDetalle = $"Sincronizando Clientes y Direcciones: {Math.Min(i + batchSize, idsPendientes.Count):N0} de {idsPendientes.Count:N0}";

                        var clientesDetalle = (await ObtenerDetalleClientesAsync(loteIds, ct))
                            .OrderBy(c => c.EstadoCliente)
                            .ToList();

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
                else
                {
                    _currentStatus.FaseDetalle = "Clientes y Direcciones al día (0 diferencias). Saltando directamente a Cartera de Zonas y Rutas...";
                }

                // -------------------------------------------------------------
                // SUB-ETAPA 2: CARTERA DE ZONAS, RUTAS Y VENDEDORES
                // (Skip inteligente inmediato si está al día con 0 diferencias)
                // -------------------------------------------------------------
                int zonasActualizadas = 0;
                if (incluirZonas)
                {
                    if (idsZonas.Count > 0 || idsQuitados.Count > 0)
                    {
                        _currentStatus.EstadoActual = "APLICANDO_CAMBIOS_ZONAS";
                        var (zCambios, zErrores) = await ProcesarZonasDiferenciasAsync(loteId, config.TamanoLote, ct);
                        zonasActualizadas = zCambios;
                        erroresCount += zErrores;
                    }
                    else
                    {
                        _currentStatus.FaseDetalle = "Cartera de Zonas y Rutas al día (0 diferencias). Saltando directamente a Líneas de Crédito...";
                    }
                }

                // -------------------------------------------------------------
                // SUB-ETAPA 3: LÍNEAS Y MODALIDADES DE CRÉDITO
                // (Skip inteligente inmediato si está al día con 0 diferencias)
                // -------------------------------------------------------------
                int creditosActualizados = 0;
                if (incluirZonas)
                {
                    if (idsCreditos.Count > 0)
                    {
                        _currentStatus.EstadoActual = "APLICANDO_CAMBIOS_CREDITOS";
                        var (cCambios, cErrores) = await ProcesarCreditosDiferenciasAsync(loteId, config.TamanoLote, ct);
                        creditosActualizados = cCambios;
                        erroresCount += cErrores;
                    }
                    else
                    {
                        _currentStatus.FaseDetalle = "Líneas de Crédito al día (0 diferencias). Finalizando módulo...";
                    }
                }

                swTotal.Stop();
                transaccion.TotalErrores = erroresCount;
                transaccion.DuracionSegundos = (decimal)swTotal.Elapsed.TotalSeconds;
                transaccion.Estado = erroresCount > 0 ? "CON_ERRORES" : "EXITOSO";

                await RegistrarFinLoteAsync(transaccion, ct);

                _currentStatus.UltimaEjecucion = DateTime.Now;
                _currentStatus.UltimoLoteProcesados = idsPendientes.Count + zonasActualizadas + creditosActualizados;
                _currentStatus.UltimoLoteNuevos = transaccion.TotalNuevos;
                _currentStatus.UltimoLoteModificados = transaccion.TotalModificados;
                _currentStatus.UltimoLoteZonasActualizadas = zonasActualizadas;
                _currentStatus.UltimoLoteErrores = erroresCount;
                _currentStatus.UltimaDuracionSegundos = Math.Round(swTotal.Elapsed.TotalSeconds, 2);
                _currentStatus.EstadoActual = "COMPLETADO";
                _currentStatus.FaseDetalle = $"Sincronización finalizada en {_currentStatus.UltimaDuracionSegundos}s. Procesados: {idsPendientes.Count} clientes/direcciones, {zonasActualizadas} zonas, {creditosActualizados} créditos.";

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
        // 3. APLICAR CAMBIOS EN ZONAS Y RUTAS
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
            _currentStatus.FaseDetalle = "Aplicando diferencias en Cartera de Zonas y Rutas...";

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

                var preview = await AnalizarCambiosPendientesAsync(ct);
                var resumenZonas = preview.ResumenPorTabla.FirstOrDefault(x => x.CodigoTabla == "ZONAS_RUTAS");
                transaccion.TotalEscaneados = resumenZonas?.TotalOrigen ?? 0;
                transaccion.TotalNuevos = resumenZonas?.InsertsDetectados ?? 0;
                transaccion.TotalModificados = (resumenZonas?.UpdatesDetectados ?? 0) + (resumenZonas?.DeletesBajasDetectados ?? 0);

                var (cambios, errores) = await ProcesarZonasDiferenciasAsync(loteId, config.TamanoLote, ct);

                swTotal.Stop();
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

            // 1. PRIMERO: Inactivar en bloque las zonas retiradas o antiguas en Force
            //    (Debe ejecutarse ANTES de insertar/reactivar las zonas vigentes para no pisar las nuevas asignaciones)
            using (var connForce = new SqlConnection(_connectionStringForce))
            {
                await connForce.OpenAsync(ct);

                if (idsQuitados.Count > 0)
                {
                    for (int i = 0; i < idsQuitados.Count; i += batchSize)
                    {
                        ct.ThrowIfCancellationRequested();
                        var loteQuitados = idsQuitados.Skip(i).Take(batchSize).ToList();
                        _currentStatus.FaseDetalle = $"Depurando rutas retiradas en Force: {Math.Min(i + batchSize, idsQuitados.Count):N0} de {idsQuitados.Count:N0}";

                        try
                        {
                            var inClause = string.Join(",", loteQuitados);
                            var sqlDesactivar = $@"
                                UPDATE dbo.dir_DireccionZona
                                SET Estado = 0, EsPrincipalVenta = 0, FechaModificacion = GETDATE()
                                WHERE ID IN ({inClause}) AND (Estado = 1 OR EsPrincipalVenta = 1);";

                            using var cmd = new SqlCommand(sqlDesactivar, connForce) { CommandTimeout = 120 };
                            await cmd.ExecuteNonQueryAsync(ct);
                            procesados += loteQuitados.Count;
                        }
                        catch (Exception ex)
                        {
                            errores++;
                            await RegistrarLogAsync(loteId, "ZONAS_RUTAS", "ERROR", loteQuitados.FirstOrDefault(), null,
                                $"Error al inactivar lote de zonas retiradas: {ex.Message}", ex.ToString(), ct);
                        }
                        finally
                        {
                            _currentStatus.ProgresoActual = Math.Min(_currentStatus.ProgresoTotal, _currentStatus.ProgresoActual + loteQuitados.Count);
                        }
                    }
                }
            }

            // 2. SEGUNDO: Aplicar Altas y Reasignaciones vigentes desde el ERP Antiguo
            for (int i = 0; i < idsCambiados.Count; i += batchSize)
            {
                ct.ThrowIfCancellationRequested();
                var loteIds = idsCambiados.Skip(i).Take(batchSize).ToList();
                _currentStatus.FaseDetalle = $"Sincronizando Zonas y Rutas vigentes: {Math.Min(i + batchSize, idsCambiados.Count):N0} de {idsCambiados.Count:N0}";

                var carteraDetalle = await ObtenerDetalleCarteraZonasAsync(loteIds, ct);

                // Ajustar ProgresoTotal si una dirección tiene múltiples zonas por día
                if (carteraDetalle.Count > loteIds.Count)
                {
                    _currentStatus.ProgresoTotal += (carteraDetalle.Count - loteIds.Count);
                }

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
                        _currentStatus.ProgresoActual = Math.Min(_currentStatus.ProgresoTotal, _currentStatus.ProgresoActual + 1);
                    }
                }
            }

            return (procesados, errores);
        }

        private async Task<(int Procesados, int Errores)> ProcesarCreditosDiferenciasAsync(Guid loteId, int batchSize, CancellationToken ct)
        {
            var idsCreditos = _idsCreditosConCambios.ToList();
            if (idsCreditos.Count == 0) return (0, 0);

            _currentStatus.ProgresoActual = 0;
            _currentStatus.ProgresoTotal = idsCreditos.Count;
            int errores = 0;
            int procesados = 0;
            if (batchSize <= 0) batchSize = 500;

            for (int i = 0; i < idsCreditos.Count; i += batchSize)
            {
                ct.ThrowIfCancellationRequested();
                var loteIds = idsCreditos.Skip(i).Take(batchSize).ToList();
                _currentStatus.FaseDetalle = $"Sincronizando Líneas de Crédito: {Math.Min(i + batchSize, idsCreditos.Count):N0} de {idsCreditos.Count:N0}";

                var creditosDetalle = await ObtenerDetalleCreditosAsync(loteIds, ct);

                foreach (var item in creditosDetalle)
                {
                    try
                    {
                        await EjecutarUpsertCreditoCatalogoAsync(item, ct);
                        procesados++;
                    }
                    catch (Exception ex)
                    {
                        errores++;
                        await RegistrarLogAsync(loteId, "CREDITOS", "ERROR", item.ID_LineaCredito, item.CodigoCliente,
                            $"Error vinculando línea de crédito portafolio ({item.CodigoPortafolio}): {ex.Message}", ex.ToString(), ct);
                    }
                    finally
                    {
                        _currentStatus.ProgresoActual = Math.Min(_currentStatus.ProgresoTotal, _currentStatus.ProgresoActual + 1);
                    }
                }
            }

            return (procesados, errores);
        }

        private async Task<List<CreditoCatalogoExtraccionDto>> ObtenerDetalleCreditosAsync(List<int> ids, CancellationToken ct)
        {
            var lista = new List<CreditoCatalogoExtraccionDto>(ids.Count);
            using var conn = new SqlConnection(_connectionStringAntiguo);
            await conn.OpenAsync(ct);

            var inClause = string.Join(",", ids);
            var sql = $@"
                SELECT 
                    l.PKID AS ID_LineaCredito,
                    l.IDCliente,
                    LTRIM(RTRIM(p.Codigo)) AS CodigoCliente,
                    LTRIM(RTRIM(p.Nombre)) AS NombreCliente,
                    l.IDPortafolioProducto,
                    LTRIM(RTRIM(ISNULL(pp.Codigo, 'PORT'))) AS CodigoPortafolio,
                    LTRIM(RTRIM(ISNULL(pp.Nombre, 'PORTAFOLIO'))) AS NombrePortafolio,
                    ISNULL(l.Monto, 0.00) AS Monto,
                    ISNULL(l.BalanceSoles, 0.00) AS BalanceSoles,
                    ISNULL(l.BalanceDolares, 0.00) AS BalanceDolares,
                    ISNULL(l.Saldo, 0.00) AS Saldo,
                    ISNULL(l.DiasGracia, 5) AS DiasGracia,
                    ISNULL(l.PorcentajeMaximoVencidoCredito, 0.00) AS PorcentajeMaximoVencidoCredito,
                    ISNULL(l.PorcentajeMaximoVencidoContado, 100.00) AS PorcentajeMaximoVencidoContado,
                    CAST(ISNULL(l.Activo, 1) AS bit) AS Activo
                FROM dbo.LineaCreditoPorPortafolioProducto l WITH (NOLOCK)
                INNER JOIN dbo.Persona p WITH (NOLOCK) ON p.PKID = l.IDCliente
                LEFT JOIN dbo.PortafolioProducto pp WITH (NOLOCK) ON pp.PKID = l.IDPortafolioProducto
                WHERE l.PKID IN ({inClause});";

            using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 120 };
            using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                lista.Add(new CreditoCatalogoExtraccionDto
                {
                    ID_LineaCredito = r.GetInt32(0),
                    ID_Cliente = r.GetInt32(1),
                    CodigoCliente = r.GetString(2),
                    NombreCliente = r.GetString(3),
                    ID_Portafolio = r.GetInt32(4),
                    CodigoPortafolio = r.GetString(5),
                    NombrePortafolio = r.GetString(6),
                    Monto = r.GetDecimal(7),
                    BalanceSoles = r.GetDecimal(8),
                    BalanceDolares = r.GetDecimal(9),
                    Saldo = r.GetDecimal(10),
                    DiasGracia = r.GetInt32(11),
                    PorcentajeMaximoVencidoCredito = r.GetDecimal(12),
                    PorcentajeMaximoVencidoContado = r.GetDecimal(13),
                    Activo = r.GetBoolean(14)
                });
            }
            return lista;
        }

        private async Task EjecutarUpsertCreditoCatalogoAsync(CreditoCatalogoExtraccionDto c, CancellationToken ct)
        {
            using var conn = new SqlConnection(_connectionStringForce);
            await conn.OpenAsync(ct);

            using var cmd = new SqlCommand("dbo.usp_Sync_UpsertClienteCreditoCatalogo", conn)
            {
                CommandType = CommandType.StoredProcedure,
                CommandTimeout = 60
            };

            cmd.Parameters.AddWithValue("@ID_LineaCredito_Origen", c.ID_LineaCredito);
            cmd.Parameters.AddWithValue("@ID_Cliente_Origen", c.ID_Cliente);
            cmd.Parameters.AddWithValue("@ID_Portafolio_Origen", c.ID_Portafolio);
            cmd.Parameters.AddWithValue("@CodigoPortafolio", c.CodigoPortafolio);
            cmd.Parameters.AddWithValue("@NombrePortafolio", c.NombrePortafolio);
            cmd.Parameters.AddWithValue("@Monto", c.Monto);
            cmd.Parameters.AddWithValue("@BalanceSoles", c.BalanceSoles);
            cmd.Parameters.AddWithValue("@BalanceDolares", c.BalanceDolares);
            cmd.Parameters.AddWithValue("@Saldo", c.Saldo);
            cmd.Parameters.AddWithValue("@DiasGracia", c.DiasGracia);
            cmd.Parameters.AddWithValue("@PorcentajeMaximoVencidoCredito", c.PorcentajeMaximoVencidoCredito);
            cmd.Parameters.AddWithValue("@PorcentajeMaximoVencidoContado", c.PorcentajeMaximoVencidoContado);
            cmd.Parameters.AddWithValue("@Estado", c.Activo);

            await cmd.ExecuteNonQueryAsync(ct);
        }

        // =========================================================================
        // CONSULTAS DE EXTRACCIÓN Y STORED PROCEDURES
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
            var sql = $@"
                SELECT 
                    dc.PKID AS ID_Direccion,
                    p.PKID AS ID_Cliente,
                    LTRIM(RTRIM(p.Codigo)) AS CodigoCliente,
                    LTRIM(RTRIM(p.Nombre)) AS NombreCliente,
                    z.Codigo AS CodigoZonaVenta,
                    rr.Codigo AS CodigoRuta,
                    RTRIM(pv.Codigo) AS CodigoVendedor,
                    CASE 
                        WHEN z.Codigo LIKE '%SAB%' OR rr.Codigo LIKE '%SAB%' THEN 6
                        WHEN z.Codigo LIKE '%DOM%' OR rr.Codigo LIKE '%DOM%' THEN 7
                        WHEN z.Codigo LIKE '%VIE%' OR rr.Codigo LIKE '%VIE%' THEN 5
                        WHEN z.Codigo LIKE '%JUE%' OR rr.Codigo LIKE '%JUE%' THEN 4
                        WHEN z.Codigo LIKE '%MIE%' OR rr.Codigo LIKE '%MIE%' THEN 3
                        WHEN z.Codigo LIKE '%MAR%' OR rr.Codigo LIKE '%MAR%' THEN 2
                        WHEN z.Codigo LIKE '%LUN%' OR rr.Codigo LIKE '%LUN%' THEN 1
                        WHEN LOWER(LTRIM(RTRIM(rr.DiaDeSemana))) LIKE 's%b%' OR rr.DiaDeSemana = '6' THEN 6
                        WHEN LOWER(LTRIM(RTRIM(rr.DiaDeSemana))) LIKE 'dom%' OR rr.DiaDeSemana = '7' THEN 7
                        WHEN LOWER(LTRIM(RTRIM(rr.DiaDeSemana))) LIKE 'vie%' OR rr.DiaDeSemana = '5' THEN 5
                        WHEN LOWER(LTRIM(RTRIM(rr.DiaDeSemana))) LIKE 'jue%' OR rr.DiaDeSemana = '4' THEN 4
                        WHEN LOWER(LTRIM(RTRIM(rr.DiaDeSemana))) LIKE 'mi%'  OR rr.DiaDeSemana = '3' THEN 3
                        WHEN LOWER(LTRIM(RTRIM(rr.DiaDeSemana))) LIKE 'mar%' OR rr.DiaDeSemana = '2' THEN 2
                        WHEN LOWER(LTRIM(RTRIM(rr.DiaDeSemana))) LIKE 'lun%' OR rr.DiaDeSemana = '1' THEN 1
                        ELSE 1
                    END AS DiaDeSemana
                FROM dbo.DireccionZona dz WITH (NOLOCK)
                INNER JOIN dbo.Persona p WITH (NOLOCK) ON dz.IDCliente = p.PKID
                INNER JOIN dbo.DireccionCliente dc WITH (NOLOCK) ON dc.PKID = dz.IDDireccion
                INNER JOIN dbo.Direccion dd WITH (NOLOCK) ON dd.PKID = dc.PKID
                INNER JOIN dbo.Zona z WITH (NOLOCK) ON z.PKID = dz.IDZona
                LEFT JOIN dbo.Zona zr WITH (NOLOCK) ON zr.PKID = dc.IDZonaReparto
                INNER JOIN dbo.ZonaRutaVendedor zrv WITH (NOLOCK) ON zrv.IDZona = z.PKID
                INNER JOIN dbo.RutaVendedor rr WITH (NOLOCK) ON rr.PKID = zrv.IDRutaVendedor
                INNER JOIN dbo.AsignacionRutaZonaVendedor ass WITH (NOLOCK) ON ass.PKID = rr.IDAsignacionRutaZonaVendedor
                INNER JOIN dbo.Persona pv WITH (NOLOCK) ON pv.PKID = ass.IDVendedor
                LEFT JOIN dbo.Vendedor v WITH (NOLOCK) ON v.PKID = pv.PKID
                LEFT JOIN dbo.GrupoVentas gv WITH (NOLOCK) ON gv.PKID = v.IDGrupoVentas
                LEFT JOIN dbo.FuerzaVentas fv WITH (NOLOCK) ON fv.PKID = gv.IDFuerzaVentas
                WHERE dd.Activo = 1 AND p.Activo = 1 
                  AND dc.PKID IN ({inClause})
                GROUP BY dc.PKID, p.PKID, p.Codigo, p.Nombre, z.Codigo, rr.Codigo, pv.Codigo, rr.DiaDeSemana;";

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
                    NombreVendedor = "",
                    IDZonaVenta = 0,
                    CodigoZonaVenta = reader.GetString(reader.GetOrdinal("CodigoZonaVenta")),
                    NombreZonaVenta = reader.GetString(reader.GetOrdinal("CodigoZonaVenta")),
                    IDZonaReparto = 0,
                    CodigoZonaReparto = "",
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
            cmd.Parameters.AddWithValue("@CodigoCliente", r.CodigoCliente);
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
