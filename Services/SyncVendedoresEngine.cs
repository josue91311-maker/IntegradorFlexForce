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
    public class SyncVendedoresEngine
    {
        private readonly string _connectionStringForce;
        private readonly string _connectionStringAntiguo;
        private readonly ILogger<SyncVendedoresEngine> _logger;
        private readonly SemaphoreSlim _syncLock = new(1, 1);

        private readonly SyncStatusDto _currentStatus = new();
        private SyncPreviewDto? _lastPreview;

        // Caches de IDs detectados con diferencias
        private HashSet<int> _idsSucursalesConCambios = new();
        private HashSet<string> _codigosCanalesConCambios = new();
        private HashSet<string> _codigosJefesConCambios = new();
        private HashSet<string> _codigosSupervisoresConCambios = new();
        private HashSet<int> _idsFuerzasConCambios = new();
        private HashSet<int> _idsGruposConCambios = new();
        private HashSet<int> _idsVendedoresConCambios = new();

        public SyncVendedoresEngine(IConfiguration config, ILogger<SyncVendedoresEngine> logger)
        {
            _connectionStringForce = config.GetConnectionString("ForceGVD") ?? "";
            _connectionStringAntiguo = config.GetConnectionString("ERPAntiguo") ?? "";
            _logger = logger;
        }

        public SyncStatusDto GetCurrentStatus() => _currentStatus;
        public SyncPreviewDto? GetLastPreview() => _lastPreview;

        private bool _esquemaVerificado = false;
        private async Task AsegurarEsquemaBDForceAsync(CancellationToken ct)
        {
            if (_esquemaVerificado) return;
            try
            {
                using var conn = new SqlConnection(_connectionStringForce);
                await conn.OpenAsync(ct);

                string fileName = "11_SP_Upsert_Vendedor_Force.sql";
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

                // Alinear retroactivamente per_Persona para vendedores de vnd_Vendedor que falten
                const string sqlAlinearVendedores = @"
                    INSERT INTO dbo.per_Persona (
                        TipoEntidad, IDCliente, IDProveedor, IDVendedor,
                        NombreCompleto, NombreComercial, DocIdentidad, IDTipoDocIdentidad,
                        Telefono, Email, Observacion, Estado,
                        FechaCreacion, FechaModificacion, Eliminado
                    )
                    SELECT 
                        'VENDEDOR', NULL, NULL, v.ID,
                        LEFT(LTRIM(RTRIM(ISNULL(v.NombreCompleto, v.Codigo))), 200),
                        LEFT(LTRIM(RTRIM(ISNULL(v.NombreCorto, v.NombreCompleto))), 200),
                        LEFT(LTRIM(RTRIM(ISNULL(v.DocumentoIdentidad, v.Codigo))), 20),
                        ISNULL(v.IDTipoDocIdentidad, 19),
                        NULLIF(v.Telefono, ''), NULLIF(v.Email, ''), NULL,
                        ISNULL(v.Estado, 1),
                        ISNULL(v.FechaCreacion, GETDATE()),
                        ISNULL(v.FechaModificacion, GETDATE()),
                        ISNULL(v.Eliminado, 0)
                    FROM dbo.vnd_Vendedor v
                    WHERE NOT EXISTS (
                        SELECT 1 FROM dbo.per_Persona p 
                        WHERE p.TipoEntidad = 'VENDEDOR' AND p.IDVendedor = v.ID
                    );";
                using var cmdAlinear = new SqlCommand(sqlAlinearVendedores, conn) { CommandTimeout = 60 };
                await cmdAlinear.ExecuteNonQueryAsync(ct);

                _esquemaVerificado = true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AsegurarEsquemaBDForceAsync (Vendedores): Advertencia al aplicar esquema: {Msg}", ex.Message);
            }
        }

        // =========================================================================
        // 1. ESCANEAR Y COMPARAR DIFERENCIAS (SOLO LECTURA EN DIGALIMENTA41)
        // =========================================================================
        public async Task<SyncPreviewDto> AnalizarCambiosPendientesAsync(CancellationToken ct = default)
        {
            await AsegurarEsquemaBDForceAsync(ct);
            var sw = Stopwatch.StartNew();
            var preview = new SyncPreviewDto { FechaAnalisis = DateTime.Now };

            var nuevosIdsSuc = new HashSet<int>();
            var updatesIdsSuc = new HashSet<int>();

            var nuevosCanales = new HashSet<string>();
            var updatesCanales = new HashSet<string>();

            var nuevosJefes = new HashSet<string>();
            var updatesJefes = new HashSet<string>();

            var nuevosSupervisores = new HashSet<string>();
            var updatesSupervisores = new HashSet<string>();

            var nuevosFuerzas = new HashSet<int>();
            var updatesFuerzas = new HashSet<int>();

            var nuevosGrupos = new HashSet<int>();
            var updatesGrupos = new HashSet<int>();

            var nuevosVendedores = new HashSet<int>();
            var updatesVendedores = new HashSet<int>();

            const int maxDetallePorSubmodulo = 300;
            int countDetalles = 0;

            try
            {
                using var connAntiguo = new SqlConnection(_connectionStringAntiguo);
                using var connForce = new SqlConnection(_connectionStringForce);
                await connAntiguo.OpenAsync(ct);
                await connForce.OpenAsync(ct);

                // -----------------------------------------------------------------
                // A. SUCURSALES (cat_Sucursal)
                // -----------------------------------------------------------------
                var sucursalesForce = new Dictionary<string, (int ID, string Descripcion, bool Estado, int? IDExtra)>(StringComparer.OrdinalIgnoreCase);
                using (var cmd = new SqlCommand("SELECT ID, RTRIM(Codigo) AS Codigo, RTRIM(Descripcion) AS Descripcion, Estado, IDExtra FROM dbo.cat_Sucursal WITH (NOLOCK);", connForce))
                using (var r = await cmd.ExecuteReaderAsync(ct))
                {
                    while (await r.ReadAsync(ct))
                    {
                        var cod = r.GetString(1);
                        sucursalesForce[cod] = (r.GetInt32(0), r.GetString(2), r.GetBoolean(3), r.IsDBNull(4) ? null : r.GetInt32(4));
                    }
                }

                int totalSucOrigen = 0;
                using (var cmd = new SqlCommand("SELECT PKID, RTRIM(Codigo) AS Codigo, RTRIM(Nombre) AS Descripcion, Activo FROM dbo.Sucursal WITH (NOLOCK);", connAntiguo))
                using (var r = await cmd.ExecuteReaderAsync(ct))
                {
                    while (await r.ReadAsync(ct))
                    {
                        totalSucOrigen++;
                        int pkid = r.GetInt32(0);
                        string cod = r.GetString(1);
                        string desc = r.GetString(2);
                        bool activo = r.GetBoolean(3);

                        if (!sucursalesForce.TryGetValue(cod, out var f))
                        {
                            nuevosIdsSuc.Add(pkid);
                            if (countDetalles++ < maxDetallePorSubmodulo)
                            {
                                preview.DetalleCambios.Add(new CambioDetectadoItemDto
                                {
                                    Tabla = "SUCURSALES",
                                    TipoOperacion = "INSERT",
                                    IDOrigen = pkid,
                                    CodigoCliente = cod,
                                    NombreCliente = desc,
                                    CampoModificado = "Nueva Sucursal",
                                    ValorActualForce = "Inexistente en Force",
                                    ValorNuevoERP = $"Código: {cod} | {desc}"
                                });
                            }
                        }
                        else if (!string.Equals(f.Descripcion, desc, StringComparison.OrdinalIgnoreCase) || f.Estado != activo || f.IDExtra != pkid)
                        {
                            updatesIdsSuc.Add(pkid);
                            if (countDetalles++ < maxDetallePorSubmodulo)
                            {
                                preview.DetalleCambios.Add(new CambioDetectadoItemDto
                                {
                                    Tabla = "SUCURSALES",
                                    TipoOperacion = "UPDATE",
                                    IDOrigen = pkid,
                                    CodigoCliente = cod,
                                    NombreCliente = desc,
                                    CampoModificado = "Datos / Estado Sucursal",
                                    ValorActualForce = $"{f.Descripcion} | Estado: {(f.Estado ? "Activo" : "Inactivo")}",
                                    ValorNuevoERP = $"{desc} | Estado: {(activo ? "Activo" : "Inactivo")}"
                                });
                            }
                        }
                    }
                }

                preview.ResumenPorTabla.Add(new TablaComparacionResumenDto
                {
                    CodigoTabla = "SUCURSALES",
                    NombreTabla = "1. Sucursales de Venta",
                    TablasForce = "dbo.cat_Sucursal",
                    TotalOrigen = totalSucOrigen,
                    TotalDestino = sucursalesForce.Count,
                    InsertsDetectados = nuevosIdsSuc.Count,
                    UpdatesDetectados = updatesIdsSuc.Count,
                    DeletesBajasDetectados = 0,
                    Observacion = (nuevosIdsSuc.Count + updatesIdsSuc.Count == 0) ? "Al día" : "Diferencias detectadas"
                });

                // -----------------------------------------------------------------
                // B. CANALES DE VENTA (vnd_Canal)
                // -----------------------------------------------------------------
                var canalesForce = new Dictionary<string, (int ID, string Descripcion, bool Estado)>(StringComparer.OrdinalIgnoreCase);
                using (var cmd = new SqlCommand("SELECT ID, RTRIM(Codigo) AS Codigo, RTRIM(Descripcion) AS Descripcion, Estado FROM dbo.vnd_Canal WITH (NOLOCK);", connForce))
                using (var r = await cmd.ExecuteReaderAsync(ct))
                {
                    while (await r.ReadAsync(ct))
                    {
                        canalesForce[r.GetString(1)] = (r.GetInt32(0), r.GetString(2), r.GetBoolean(3));
                    }
                }

                int totalCanalesOrigen = 0;
                string sqlCanales = @"
                    SELECT DISTINCT 
                        LEFT(RTRIM(f.Observacion), 4) + '-' + RIGHT(RTRIM(f.Observacion), 4) + '-34' AS Codigo,
                        RTRIM(f.Observacion) AS Descripcion
                    FROM dbo.FuerzaVentas f WITH (NOLOCK)
                    WHERE f.Observacion IS NOT NULL AND LTRIM(RTRIM(f.Observacion)) <> '';";

                using (var cmd = new SqlCommand(sqlCanales, connAntiguo))
                using (var r = await cmd.ExecuteReaderAsync(ct))
                {
                    while (await r.ReadAsync(ct))
                    {
                        totalCanalesOrigen++;
                        string cod = r.GetString(0);
                        string desc = r.GetString(1);

                        if (!canalesForce.TryGetValue(cod, out var f))
                        {
                            nuevosCanales.Add(cod);
                            if (countDetalles++ < maxDetallePorSubmodulo)
                            {
                                preview.DetalleCambios.Add(new CambioDetectadoItemDto
                                {
                                    Tabla = "CANALES",
                                    TipoOperacion = "INSERT",
                                    IDOrigen = 0,
                                    CodigoCliente = cod,
                                    NombreCliente = desc,
                                    CampoModificado = "Nuevo Canal de Venta",
                                    ValorActualForce = "Inexistente en Force",
                                    ValorNuevoERP = $"Código: {cod} | {desc}"
                                });
                            }
                        }
                    }
                }

                preview.ResumenPorTabla.Add(new TablaComparacionResumenDto
                {
                    CodigoTabla = "CANALES",
                    NombreTabla = "2. Canales de Distribución y Venta",
                    TablasForce = "dbo.vnd_Canal",
                    TotalOrigen = totalCanalesOrigen,
                    TotalDestino = canalesForce.Count,
                    InsertsDetectados = nuevosCanales.Count,
                    UpdatesDetectados = updatesCanales.Count,
                    DeletesBajasDetectados = 0,
                    Observacion = (nuevosCanales.Count + updatesCanales.Count == 0) ? "Al día" : "Diferencias detectadas"
                });

                // -----------------------------------------------------------------
                // C. JEFATURAS DE VENTA (vnd_JefeVenta)
                // -----------------------------------------------------------------
                var jefesForce = new Dictionary<string, (int ID, string Descripcion, bool Estado)>(StringComparer.OrdinalIgnoreCase);
                using (var cmd = new SqlCommand("SELECT ID, RTRIM(Codigo) AS Codigo, RTRIM(Descripcion) AS Descripcion, Estado FROM dbo.vnd_JefeVenta WITH (NOLOCK);", connForce))
                using (var r = await cmd.ExecuteReaderAsync(ct))
                {
                    while (await r.ReadAsync(ct))
                    {
                        jefesForce[r.GetString(1)] = (r.GetInt32(0), r.GetString(2), r.GetBoolean(3));
                    }
                }

                int totalJefesOrigen = 0;
                string sqlJefes = @"
                    SELECT DISTINCT
                        CONCAT(REPLACE(g.Observacion, ' ', ''), 'JEFE') AS Codigo,
                        RTRIM(g.Observacion) AS Descripcion
                    FROM dbo.GrupoVentas g WITH (NOLOCK)
                    WHERE g.Observacion IS NOT NULL AND LTRIM(RTRIM(g.Observacion)) <> '';";

                using (var cmd = new SqlCommand(sqlJefes, connAntiguo))
                using (var r = await cmd.ExecuteReaderAsync(ct))
                {
                    while (await r.ReadAsync(ct))
                    {
                        totalJefesOrigen++;
                        string cod = r.GetString(0);
                        string desc = r.GetString(1);

                        if (!jefesForce.TryGetValue(cod, out var f))
                        {
                            nuevosJefes.Add(cod);
                            if (countDetalles++ < maxDetallePorSubmodulo)
                            {
                                preview.DetalleCambios.Add(new CambioDetectadoItemDto
                                {
                                    Tabla = "JEFES_VENTA",
                                    TipoOperacion = "INSERT",
                                    IDOrigen = 0,
                                    CodigoCliente = cod,
                                    NombreCliente = desc,
                                    CampoModificado = "Nueva Jefatura",
                                    ValorActualForce = "Inexistente en Force",
                                    ValorNuevoERP = $"Código: {cod} | {desc}"
                                });
                            }
                        }
                    }
                }

                preview.ResumenPorTabla.Add(new TablaComparacionResumenDto
                {
                    CodigoTabla = "JEFES_VENTA",
                    NombreTabla = "3. Jefaturas Comerciales",
                    TablasForce = "dbo.vnd_JefeVenta",
                    TotalOrigen = totalJefesOrigen,
                    TotalDestino = jefesForce.Count,
                    InsertsDetectados = nuevosJefes.Count,
                    UpdatesDetectados = updatesJefes.Count,
                    DeletesBajasDetectados = 0,
                    Observacion = (nuevosJefes.Count + updatesJefes.Count == 0) ? "Al día" : "Diferencias detectadas"
                });

                // -----------------------------------------------------------------
                // D. SUPERVISORES (vnd_Supervisor)
                // -----------------------------------------------------------------
                var supervisoresForce = new Dictionary<string, (int ID, string Descripcion, bool Estado)>(StringComparer.OrdinalIgnoreCase);
                using (var cmd = new SqlCommand("SELECT ID, RTRIM(Codigo) AS Codigo, RTRIM(Descripcion) AS Descripcion, Estado FROM dbo.vnd_Supervisor WITH (NOLOCK) WHERE Eliminado = 0;", connForce))
                using (var r = await cmd.ExecuteReaderAsync(ct))
                {
                    while (await r.ReadAsync(ct))
                    {
                        supervisoresForce[r.GetString(1)] = (r.GetInt32(0), r.GetString(2), r.GetBoolean(3));
                    }
                }

                int totalSupervisoresOrigen = 0;
                string sqlSupervisores = @"
                    SELECT DISTINCT 
                        p.PKID,
                        RTRIM(p.Codigo) AS Codigo,
                        RTRIM(p.Nombre) AS Descripcion,
                        p.Activo AS Estado
                    FROM dbo.Persona p WITH (NOLOCK)
                    INNER JOIN dbo.Vendedor v WITH (NOLOCK) ON v.PKID = p.PKID
                    INNER JOIN dbo.GrupoVentas ff WITH (NOLOCK) ON ff.IDSupervisor = p.PKID;";

                using (var cmd = new SqlCommand(sqlSupervisores, connAntiguo))
                using (var r = await cmd.ExecuteReaderAsync(ct))
                {
                    while (await r.ReadAsync(ct))
                    {
                        totalSupervisoresOrigen++;
                        int pkid = r.GetInt32(0);
                        string cod = r.GetString(1);
                        string desc = r.GetString(2);
                        bool estado = r.GetBoolean(3);

                        if (!supervisoresForce.TryGetValue(cod, out var f))
                        {
                            nuevosSupervisores.Add(cod);
                            if (countDetalles++ < maxDetallePorSubmodulo)
                            {
                                preview.DetalleCambios.Add(new CambioDetectadoItemDto
                                {
                                    Tabla = "SUPERVISORES",
                                    TipoOperacion = "INSERT",
                                    IDOrigen = pkid,
                                    CodigoCliente = cod,
                                    NombreCliente = desc,
                                    CampoModificado = "Nuevo Supervisor",
                                    ValorActualForce = "Inexistente en Force",
                                    ValorNuevoERP = $"Código: {cod} | {desc}"
                                });
                            }
                        }
                        else if (!string.Equals(f.Descripcion, desc, StringComparison.OrdinalIgnoreCase) || f.Estado != estado)
                        {
                            updatesSupervisores.Add(cod);
                            if (countDetalles++ < maxDetallePorSubmodulo)
                            {
                                preview.DetalleCambios.Add(new CambioDetectadoItemDto
                                {
                                    Tabla = "SUPERVISORES",
                                    TipoOperacion = "UPDATE",
                                    IDOrigen = pkid,
                                    CodigoCliente = cod,
                                    NombreCliente = desc,
                                    CampoModificado = "Nombre / Estado Supervisor",
                                    ValorActualForce = $"{f.Descripcion} | Estado: {(f.Estado ? "Activo" : "Inactivo")}",
                                    ValorNuevoERP = $"{desc} | Estado: {(estado ? "Activo" : "Inactivo")}"
                                });
                            }
                        }
                    }
                }

                preview.ResumenPorTabla.Add(new TablaComparacionResumenDto
                {
                    CodigoTabla = "SUPERVISORES",
                    NombreTabla = "4. Supervisores Comerciales",
                    TablasForce = "dbo.vnd_Supervisor",
                    TotalOrigen = totalSupervisoresOrigen,
                    TotalDestino = supervisoresForce.Count,
                    InsertsDetectados = nuevosSupervisores.Count,
                    UpdatesDetectados = updatesSupervisores.Count,
                    DeletesBajasDetectados = 0,
                    Observacion = (nuevosSupervisores.Count + updatesSupervisores.Count == 0) ? "Al día" : "Diferencias detectadas"
                });

                // -----------------------------------------------------------------
                // E. FUERZAS DE VENTA (vnd_FuerzaVenta)
                // -----------------------------------------------------------------
                var fuerzasForce = new Dictionary<int, (int ID, string Codigo, string Descripcion, bool Estado)>();
                using (var cmd = new SqlCommand("SELECT ID, RTRIM(Codigo) AS Codigo, RTRIM(Descripcion) AS Descripcion, Estado, IDExtra FROM dbo.vnd_FuerzaVenta WITH (NOLOCK) WHERE Eliminado = 0;", connForce))
                using (var r = await cmd.ExecuteReaderAsync(ct))
                {
                    while (await r.ReadAsync(ct))
                    {
                        if (!r.IsDBNull(4))
                            fuerzasForce[r.GetInt32(4)] = (r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetBoolean(3));
                    }
                }

                int totalFuerzasOrigen = 0;
                string sqlFuerzas = @"
                    SELECT 
                        f.PKID AS IDExtra,
                        RTRIM(f.Codigo) AS Codigo,
                        RTRIM(f.Nombre) AS Descripcion,
                        ISNULL(f.Observacion, '') AS Observacion
                    FROM dbo.FuerzaVentas f WITH (NOLOCK);";

                using (var cmd = new SqlCommand(sqlFuerzas, connAntiguo))
                using (var r = await cmd.ExecuteReaderAsync(ct))
                {
                    while (await r.ReadAsync(ct))
                    {
                        totalFuerzasOrigen++;
                        int pkid = r.GetInt32(0);
                        string cod = r.GetString(1);
                        string desc = r.GetString(2);

                        if (!fuerzasForce.TryGetValue(pkid, out var f))
                        {
                            nuevosFuerzas.Add(pkid);
                            if (countDetalles++ < maxDetallePorSubmodulo)
                            {
                                preview.DetalleCambios.Add(new CambioDetectadoItemDto
                                {
                                    Tabla = "FUERZAS_VENTA",
                                    TipoOperacion = "INSERT",
                                    IDOrigen = pkid,
                                    CodigoCliente = cod,
                                    NombreCliente = desc,
                                    CampoModificado = "Nueva Fuerza de Venta",
                                    ValorActualForce = "Inexistente en Force",
                                    ValorNuevoERP = $"Código: {cod} | {desc}"
                                });
                            }
                        }
                        else if (!string.Equals(f.Descripcion, desc, StringComparison.OrdinalIgnoreCase))
                        {
                            updatesFuerzas.Add(pkid);
                            if (countDetalles++ < maxDetallePorSubmodulo)
                            {
                                preview.DetalleCambios.Add(new CambioDetectadoItemDto
                                {
                                    Tabla = "FUERZAS_VENTA",
                                    TipoOperacion = "UPDATE",
                                    IDOrigen = pkid,
                                    CodigoCliente = cod,
                                    NombreCliente = desc,
                                    CampoModificado = "Descripción Fuerza de Venta",
                                    ValorActualForce = f.Descripcion,
                                    ValorNuevoERP = desc
                                });
                            }
                        }
                    }
                }

                preview.ResumenPorTabla.Add(new TablaComparacionResumenDto
                {
                    CodigoTabla = "FUERZAS_VENTA",
                    NombreTabla = "5. Fuerzas de Venta",
                    TablasForce = "dbo.vnd_FuerzaVenta",
                    TotalOrigen = totalFuerzasOrigen,
                    TotalDestino = fuerzasForce.Count,
                    InsertsDetectados = nuevosFuerzas.Count,
                    UpdatesDetectados = updatesFuerzas.Count,
                    DeletesBajasDetectados = 0,
                    Observacion = (nuevosFuerzas.Count + updatesFuerzas.Count == 0) ? "Al día" : "Diferencias detectadas"
                });

                // -----------------------------------------------------------------
                // F. GRUPOS DE VENTA (vnd_GrupoVentas)
                // -----------------------------------------------------------------
                var gruposForce = new Dictionary<int, (int ID, string Codigo, string Nombre, bool Estado)>();
                using (var cmd = new SqlCommand("SELECT ID, RTRIM(Codigo) AS Codigo, RTRIM(Nombre) AS Nombre, Estado, IDExtra FROM dbo.vnd_GrupoVentas WITH (NOLOCK) WHERE Eliminado = 0;", connForce))
                using (var r = await cmd.ExecuteReaderAsync(ct))
                {
                    while (await r.ReadAsync(ct))
                    {
                        if (!r.IsDBNull(4))
                            gruposForce[r.GetInt32(4)] = (r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetBoolean(3));
                    }
                }

                int totalGruposOrigen = 0;
                string sqlGrupos = @"
                    SELECT 
                        g.PKID AS IDExtra,
                        RTRIM(g.Codigo) AS Codigo,
                        RTRIM(g.Nombre) AS Nombre,
                        ISNULL(g.IDFuerzaVentas, 0) AS IDFuerzaVentas,
                        ISNULL(g.IDSupervisor, 0) AS IDSupervisor,
                        ISNULL(g.Observacion, '') AS Observacion
                    FROM dbo.GrupoVentas g WITH (NOLOCK);";

                using (var cmd = new SqlCommand(sqlGrupos, connAntiguo))
                using (var r = await cmd.ExecuteReaderAsync(ct))
                {
                    while (await r.ReadAsync(ct))
                    {
                        totalGruposOrigen++;
                        int pkid = r.GetInt32(0);
                        string cod = r.GetString(1);
                        string nombre = r.GetString(2);

                        if (!gruposForce.TryGetValue(pkid, out var f))
                        {
                            nuevosGrupos.Add(pkid);
                            if (countDetalles++ < maxDetallePorSubmodulo)
                            {
                                preview.DetalleCambios.Add(new CambioDetectadoItemDto
                                {
                                    Tabla = "GRUPOS_VENTA",
                                    TipoOperacion = "INSERT",
                                    IDOrigen = pkid,
                                    CodigoCliente = cod,
                                    NombreCliente = nombre,
                                    CampoModificado = "Nuevo Grupo de Ventas",
                                    ValorActualForce = "Inexistente en Force",
                                    ValorNuevoERP = $"Código: {cod} | {nombre}"
                                });
                            }
                        }
                        else if (!string.Equals(f.Nombre, nombre, StringComparison.OrdinalIgnoreCase))
                        {
                            updatesGrupos.Add(pkid);
                            if (countDetalles++ < maxDetallePorSubmodulo)
                            {
                                preview.DetalleCambios.Add(new CambioDetectadoItemDto
                                {
                                    Tabla = "GRUPOS_VENTA",
                                    TipoOperacion = "UPDATE",
                                    IDOrigen = pkid,
                                    CodigoCliente = cod,
                                    NombreCliente = nombre,
                                    CampoModificado = "Nombre Grupo de Ventas",
                                    ValorActualForce = f.Nombre,
                                    ValorNuevoERP = nombre
                                });
                            }
                        }
                    }
                }

                preview.ResumenPorTabla.Add(new TablaComparacionResumenDto
                {
                    CodigoTabla = "GRUPOS_VENTA",
                    NombreTabla = "6. Grupos de Venta y Equipos",
                    TablasForce = "dbo.vnd_GrupoVentas",
                    TotalOrigen = totalGruposOrigen,
                    TotalDestino = gruposForce.Count,
                    InsertsDetectados = nuevosGrupos.Count,
                    UpdatesDetectados = updatesGrupos.Count,
                    DeletesBajasDetectados = 0,
                    Observacion = (nuevosGrupos.Count + updatesGrupos.Count == 0) ? "Al día" : "Diferencias detectadas"
                });

                // -----------------------------------------------------------------
                // G. VENDEDORES Y PERSONAS (vnd_Vendedor + per_Persona)
                // -----------------------------------------------------------------
                var vendedoresForce = new Dictionary<int, (int ID, string Codigo, string Nombre, string Doc, bool Estado)>();
                using (var cmd = new SqlCommand("SELECT ID, RTRIM(Codigo) AS Codigo, RTRIM(NombreCompleto) AS NombreCompleto, RTRIM(ISNULL(DocumentoIdentidad, '')) AS Doc, Estado, IDExtra FROM dbo.vnd_Vendedor WITH (NOLOCK) WHERE Eliminado = 0;", connForce))
                using (var r = await cmd.ExecuteReaderAsync(ct))
                {
                    while (await r.ReadAsync(ct))
                    {
                        if (!r.IsDBNull(5))
                            vendedoresForce[r.GetInt32(5)] = (r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetBoolean(4));
                    }
                }

                int totalVendedoresOrigen = 0;
                string sqlVendedores = @"
                    SELECT 
                        v.PKID AS IDExtra,
                        RTRIM(p.Codigo) AS Codigo,
                        RTRIM(p.Nombre) AS NombreCompleto,
                        COALESCE(NULLIF(RTRIM(p.DocIdentidad), ''), RTRIM(p.Codigo)) AS DocIdentidad,
                        p.Activo AS Estado,
                        ISNULL(v.IDGrupoVentas, 0) AS IDGrupoVentas,
                        ISNULL(v.IDSucursal, 0) AS IDSucursal
                    FROM dbo.Vendedor v WITH (NOLOCK)
                    INNER JOIN dbo.Persona p WITH (NOLOCK) ON v.PKID = p.PKID;";

                using (var cmd = new SqlCommand(sqlVendedores, connAntiguo))
                using (var r = await cmd.ExecuteReaderAsync(ct))
                {
                    while (await r.ReadAsync(ct))
                    {
                        totalVendedoresOrigen++;
                        int pkid = r.GetInt32(0);
                        string cod = r.GetString(1);
                        string nombre = r.GetString(2);
                        string doc = r.GetString(3);
                        bool estado = r.GetBoolean(4);

                        if (!vendedoresForce.TryGetValue(pkid, out var f))
                        {
                            nuevosVendedores.Add(pkid);
                            if (countDetalles++ < maxDetallePorSubmodulo)
                            {
                                preview.DetalleCambios.Add(new CambioDetectadoItemDto
                                {
                                    Tabla = "VENDEDORES",
                                    TipoOperacion = "INSERT",
                                    IDOrigen = pkid,
                                    CodigoCliente = cod,
                                    NombreCliente = nombre,
                                    CampoModificado = "Nuevo Vendedor",
                                    ValorActualForce = "Inexistente en Force",
                                    ValorNuevoERP = $"Código: {cod} | Doc: {doc} | {nombre}"
                                });
                            }
                        }
                        else if (!string.Equals(f.Nombre, nombre, StringComparison.OrdinalIgnoreCase) || f.Estado != estado || !string.Equals(f.Doc, doc, StringComparison.OrdinalIgnoreCase))
                        {
                            updatesVendedores.Add(pkid);
                            if (countDetalles++ < maxDetallePorSubmodulo)
                            {
                                preview.DetalleCambios.Add(new CambioDetectadoItemDto
                                {
                                    Tabla = "VENDEDORES",
                                    TipoOperacion = "UPDATE",
                                    IDOrigen = pkid,
                                    CodigoCliente = cod,
                                    NombreCliente = nombre,
                                    CampoModificado = "Nombre / Doc / Estado Vendedor",
                                    ValorActualForce = $"{f.Nombre} | Doc: {f.Doc} | Estado: {(f.Estado ? "Activo" : "Inactivo")}",
                                    ValorNuevoERP = $"{nombre} | Doc: {doc} | Estado: {(estado ? "Activo" : "Inactivo")}"
                                });
                            }
                        }
                    }
                }

                preview.ResumenPorTabla.Add(new TablaComparacionResumenDto
                {
                    CodigoTabla = "VENDEDORES",
                    NombreTabla = "7. Cartera de Vendedores y Personas",
                    TablasForce = "dbo.vnd_Vendedor, dbo.per_Persona, dbo.ctg_CatalogoVendedor",
                    TotalOrigen = totalVendedoresOrigen,
                    TotalDestino = vendedoresForce.Count,
                    InsertsDetectados = nuevosVendedores.Count,
                    UpdatesDetectados = updatesVendedores.Count,
                    DeletesBajasDetectados = 0,
                    Observacion = (nuevosVendedores.Count + updatesVendedores.Count == 0) ? "Al día" : "Diferencias detectadas"
                });

                // Guardar caches de diferencias
                _idsSucursalesConCambios = nuevosIdsSuc.Union(updatesIdsSuc).ToHashSet();
                _codigosCanalesConCambios = nuevosCanales.Union(updatesCanales).ToHashSet();
                _codigosJefesConCambios = nuevosJefes.Union(updatesJefes).ToHashSet();
                _codigosSupervisoresConCambios = nuevosSupervisores.Union(updatesSupervisores).ToHashSet();
                _idsFuerzasConCambios = nuevosFuerzas.Union(updatesFuerzas).ToHashSet();
                _idsGruposConCambios = nuevosGrupos.Union(updatesGrupos).ToHashSet();
                _idsVendedoresConCambios = nuevosVendedores.Union(updatesVendedores).ToHashSet();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error al analizar diferencias en el Módulo de Vendedores.");
            }

            sw.Stop();
            preview.TiempoEscaneoSegundos = Math.Round(sw.Elapsed.TotalSeconds, 2);
            _lastPreview = preview;
            return preview;
        }

        // =========================================================================
        // 2. APLICAR CAMBIOS EN VENDEDORES EN BDForceGVD
        // =========================================================================
        public async Task<SyncTransaccionDto> EjecutarSincronizacionVendedoresAsync(string usuario = "PORTAL_WEB", CancellationToken ct = default)
        {
            if (!await _syncLock.WaitAsync(0, ct))
            {
                return new SyncTransaccionDto { Estado = "EN_PROCESO", Usuario = usuario };
            }

            var loteId = Guid.NewGuid();
            var swTotal = Stopwatch.StartNew();
            _currentStatus.IsRunning = true;
            _currentStatus.EstadoActual = "SINCRONIZANDO_VENDEDORES";
            _currentStatus.FaseDetalle = "Analizando diferencias en Sucursales, Canales, Equipos y Vendedores...";
            _currentStatus.ProgresoActual = 0;
            _currentStatus.ProgresoTotal = 0;

            var transaccion = new SyncTransaccionDto
            {
                LoteId = loteId,
                Entidad = "MODULO_VENDEDORES",
                TipoEjecucion = "MANUAL",
                FechaInicio = DateTime.Now,
                Estado = "EN_PROCESO",
                Usuario = usuario
            };

            await RegistrarInicioLoteAsync(transaccion, ct);

            try
            {
                var preview = await AnalizarCambiosPendientesAsync(ct);
                transaccion.TotalEscaneados = preview.ResumenPorTabla.Sum(x => x.TotalOrigen);
                transaccion.TotalNuevos = preview.ResumenPorTabla.Sum(x => x.InsertsDetectados);
                transaccion.TotalModificados = preview.ResumenPorTabla.Sum(x => x.UpdatesDetectados);

                int totalDifs = transaccion.TotalNuevos + transaccion.TotalModificados;
                _currentStatus.ProgresoTotal = totalDifs > 0 ? totalDifs : 1;
                _currentStatus.ProgresoActual = 0;

                int procesados = 0;
                int erroresCount = 0;

                using var connAntiguo = new SqlConnection(_connectionStringAntiguo);
                using var connForce = new SqlConnection(_connectionStringForce);
                await connAntiguo.OpenAsync(ct);
                await connForce.OpenAsync(ct);

                // ETAPA 1: SUCURSALES
                _currentStatus.FaseDetalle = "Sincronizando Sucursales de Venta...";
                using (var cmd = new SqlCommand("SELECT PKID, RTRIM(Codigo) AS Codigo, RTRIM(Nombre) AS Descripcion, Activo FROM dbo.Sucursal WITH (NOLOCK);", connAntiguo))
                using (var r = await cmd.ExecuteReaderAsync(ct))
                {
                    while (await r.ReadAsync(ct))
                    {
                        int pkid = r.GetInt32(0);
                        if (!_idsSucursalesConCambios.Contains(pkid) && totalDifs > 0) continue;

                        try
                        {
                            using var cmdSp = new SqlCommand("dbo.usp_Sync_UpsertSucursal", connForce) { CommandType = CommandType.StoredProcedure, CommandTimeout = 30 };
                            cmdSp.Parameters.AddWithValue("@ID_Sucursal_Origen", pkid);
                            cmdSp.Parameters.AddWithValue("@CodigoSucursal", r.GetString(1));
                            cmdSp.Parameters.AddWithValue("@DescripcionSucursal", r.GetString(2));
                            cmdSp.Parameters.AddWithValue("@EstadoSucursal", r.GetBoolean(3));
                            await cmdSp.ExecuteNonQueryAsync(ct);
                            procesados++;
                            _currentStatus.ProgresoActual = procesados;
                        }
                        catch (Exception ex)
                        {
                            erroresCount++;
                            _logger.LogWarning(ex, "Error al sincronizar sucursal {PKID}", pkid);
                        }
                    }
                }

                // ETAPA 2: CANALES DE VENTA
                _currentStatus.FaseDetalle = "Sincronizando Canales de Venta...";
                string sqlCanales = @"
                    SELECT DISTINCT 
                        LEFT(RTRIM(f.Observacion), 4) + '-' + RIGHT(RTRIM(f.Observacion), 4) + '-34' AS Codigo,
                        RTRIM(f.Observacion) AS Descripcion
                    FROM dbo.FuerzaVentas f WITH (NOLOCK)
                    WHERE f.Observacion IS NOT NULL AND LTRIM(RTRIM(f.Observacion)) <> '';";

                using (var cmd = new SqlCommand(sqlCanales, connAntiguo))
                using (var r = await cmd.ExecuteReaderAsync(ct))
                {
                    while (await r.ReadAsync(ct))
                    {
                        string cod = r.GetString(0);
                        if (!_codigosCanalesConCambios.Contains(cod) && totalDifs > 0) continue;

                        try
                        {
                            using var cmdSp = new SqlCommand("dbo.usp_Sync_UpsertCanalVenta", connForce) { CommandType = CommandType.StoredProcedure, CommandTimeout = 30 };
                            cmdSp.Parameters.AddWithValue("@CodigoCanal", cod);
                            cmdSp.Parameters.AddWithValue("@DescripcionCanal", r.GetString(1));
                            cmdSp.Parameters.AddWithValue("@EstadoCanal", true);
                            await cmdSp.ExecuteNonQueryAsync(ct);
                            procesados++;
                            _currentStatus.ProgresoActual = procesados;
                        }
                        catch (Exception ex)
                        {
                            erroresCount++;
                            _logger.LogWarning(ex, "Error al sincronizar canal {Cod}", cod);
                        }
                    }
                }

                // ETAPA 3: JEFES DE VENTA
                _currentStatus.FaseDetalle = "Sincronizando Jefaturas de Venta...";
                string sqlJefes = @"
                    SELECT DISTINCT
                        CONCAT(REPLACE(g.Observacion, ' ', ''), 'JEFE') AS Codigo,
                        RTRIM(g.Observacion) AS Descripcion
                    FROM dbo.GrupoVentas g WITH (NOLOCK)
                    WHERE g.Observacion IS NOT NULL AND LTRIM(RTRIM(g.Observacion)) <> '';";

                using (var cmd = new SqlCommand(sqlJefes, connAntiguo))
                using (var r = await cmd.ExecuteReaderAsync(ct))
                {
                    while (await r.ReadAsync(ct))
                    {
                        string cod = r.GetString(0);
                        if (!_codigosJefesConCambios.Contains(cod) && totalDifs > 0) continue;

                        try
                        {
                            using var cmdSp = new SqlCommand("dbo.usp_Sync_UpsertJefeVenta", connForce) { CommandType = CommandType.StoredProcedure, CommandTimeout = 30 };
                            cmdSp.Parameters.AddWithValue("@CodigoJefe", cod);
                            cmdSp.Parameters.AddWithValue("@DescripcionJefe", r.GetString(1));
                            cmdSp.Parameters.AddWithValue("@EstadoJefe", true);
                            await cmdSp.ExecuteNonQueryAsync(ct);
                            procesados++;
                            _currentStatus.ProgresoActual = procesados;
                        }
                        catch (Exception ex)
                        {
                            erroresCount++;
                            _logger.LogWarning(ex, "Error al sincronizar jefe {Cod}", cod);
                        }
                    }
                }

                // ETAPA 4: SUPERVISORES
                _currentStatus.FaseDetalle = "Sincronizando Supervisores Comerciales...";
                string sqlSupervisores = @"
                    SELECT DISTINCT 
                        p.PKID,
                        RTRIM(p.Codigo) AS Codigo,
                        RTRIM(p.Nombre) AS Descripcion,
                        p.Activo AS Estado
                    FROM dbo.Persona p WITH (NOLOCK)
                    INNER JOIN dbo.Vendedor v WITH (NOLOCK) ON v.PKID = p.PKID
                    INNER JOIN dbo.GrupoVentas ff WITH (NOLOCK) ON ff.IDSupervisor = p.PKID;";

                using (var cmd = new SqlCommand(sqlSupervisores, connAntiguo))
                using (var r = await cmd.ExecuteReaderAsync(ct))
                {
                    while (await r.ReadAsync(ct))
                    {
                        string cod = r.GetString(1);
                        if (!_codigosSupervisoresConCambios.Contains(cod) && totalDifs > 0) continue;

                        try
                        {
                            using var cmdSp = new SqlCommand("dbo.usp_Sync_UpsertSupervisor", connForce) { CommandType = CommandType.StoredProcedure, CommandTimeout = 30 };
                            cmdSp.Parameters.AddWithValue("@CodigoSupervisor", cod);
                            cmdSp.Parameters.AddWithValue("@DescripcionSupervisor", r.GetString(2));
                            cmdSp.Parameters.AddWithValue("@EstadoSupervisor", r.GetBoolean(3));
                            await cmdSp.ExecuteNonQueryAsync(ct);
                            procesados++;
                            _currentStatus.ProgresoActual = procesados;
                        }
                        catch (Exception ex)
                        {
                            erroresCount++;
                            _logger.LogWarning(ex, "Error al sincronizar supervisor {Cod}", cod);
                        }
                    }
                }

                // ETAPA 5: FUERZAS DE VENTA
                _currentStatus.FaseDetalle = "Sincronizando Fuerzas de Venta...";
                string sqlFuerzas = @"
                    SELECT 
                        f.PKID AS IDExtra,
                        RTRIM(f.Codigo) AS Codigo,
                        RTRIM(f.Nombre) AS Descripcion,
                        LEFT(RTRIM(f.Observacion), 4) + '-' + RIGHT(RTRIM(f.Observacion), 4) + '-34' AS CodigoCanal
                    FROM dbo.FuerzaVentas f WITH (NOLOCK);";

                using (var cmd = new SqlCommand(sqlFuerzas, connAntiguo))
                using (var r = await cmd.ExecuteReaderAsync(ct))
                {
                    while (await r.ReadAsync(ct))
                    {
                        int pkid = r.GetInt32(0);
                        if (!_idsFuerzasConCambios.Contains(pkid) && totalDifs > 0) continue;

                        try
                        {
                            using var cmdSp = new SqlCommand("dbo.usp_Sync_UpsertFuerzaVenta", connForce) { CommandType = CommandType.StoredProcedure, CommandTimeout = 30 };
                            cmdSp.Parameters.AddWithValue("@ID_FuerzaVenta_Origen", pkid);
                            cmdSp.Parameters.AddWithValue("@CodigoFuerzaVenta", r.GetString(1));
                            cmdSp.Parameters.AddWithValue("@DescripcionFuerzaVenta", r.GetString(2));
                            cmdSp.Parameters.AddWithValue("@CodigoCanalCalculado", r.GetString(3));
                            cmdSp.Parameters.AddWithValue("@EstadoFuerzaVenta", true);
                            await cmdSp.ExecuteNonQueryAsync(ct);
                            procesados++;
                            _currentStatus.ProgresoActual = procesados;
                        }
                        catch (Exception ex)
                        {
                            erroresCount++;
                            _logger.LogWarning(ex, "Error al sincronizar fuerza {PKID}", pkid);
                        }
                    }
                }

                // ETAPA 6: GRUPOS DE VENTA
                _currentStatus.FaseDetalle = "Sincronizando Grupos de Venta y Equipos...";
                string sqlGrupos = @"
                    SELECT 
                        g.PKID AS IDExtra,
                        RTRIM(g.Codigo) AS Codigo,
                        RTRIM(g.Nombre) AS Nombre,
                        ISNULL(g.IDFuerzaVentas, 0) AS IDFuerzaVentas,
                        ISNULL(pSup.Codigo, '') AS CodigoSupervisor,
                        ISNULL(g.Observacion, '') AS Observacion,
                        0 AS IDSucursal
                    FROM dbo.GrupoVentas g WITH (NOLOCK)
                    LEFT JOIN dbo.Persona pSup WITH (NOLOCK) ON pSup.PKID = g.IDSupervisor;";

                using (var cmd = new SqlCommand(sqlGrupos, connAntiguo))
                using (var r = await cmd.ExecuteReaderAsync(ct))
                {
                    while (await r.ReadAsync(ct))
                    {
                        int pkid = r.GetInt32(0);
                        if (!_idsGruposConCambios.Contains(pkid) && totalDifs > 0) continue;

                        try
                        {
                            using var cmdSp = new SqlCommand("dbo.usp_Sync_UpsertGrupoVentas", connForce) { CommandType = CommandType.StoredProcedure, CommandTimeout = 30 };
                            cmdSp.Parameters.AddWithValue("@ID_GrupoVenta_Origen", pkid);
                            cmdSp.Parameters.AddWithValue("@CodigoGrupoVenta", r.GetString(1));
                            cmdSp.Parameters.AddWithValue("@NombreGrupoVenta", r.GetString(2));
                            cmdSp.Parameters.AddWithValue("@ID_FuerzaVenta_Origen", r.GetInt32(3));
                            cmdSp.Parameters.AddWithValue("@CodigoSupervisor", r.GetString(4));
                            cmdSp.Parameters.AddWithValue("@ObservacionGrupoVenta", r.GetString(5));
                            cmdSp.Parameters.AddWithValue("@ID_Sucursal_Origen", r.GetInt32(6));
                            cmdSp.Parameters.AddWithValue("@EstadoGrupoVenta", true);
                            await cmdSp.ExecuteNonQueryAsync(ct);
                            procesados++;
                            _currentStatus.ProgresoActual = procesados;
                        }
                        catch (Exception ex)
                        {
                            erroresCount++;
                            _logger.LogWarning(ex, "Error al sincronizar grupo {PKID}", pkid);
                        }
                    }
                }

                // ETAPA 7: CARTERA DE VENDEDORES Y PERSONAS
                _currentStatus.FaseDetalle = "Sincronizando Cartera de Vendedores y Personas...";
                string sqlVendedores = @"
                    SELECT 
                        v.PKID AS IDExtra,
                        RTRIM(p.Codigo) AS Codigo,
                        RTRIM(p.Nombre) AS NombreCompleto,
                        RTRIM(p.Nombre) AS NombreCorto,
                        COALESCE(NULLIF(RTRIM(p.DocIdentidad), ''), RTRIM(p.Codigo)) AS DocIdentidad,
                        ISNULL(p.IDTipoDocIdentidad, 19) AS IDTipoDocIdentidad,
                        p.FechaCumpleaños AS FechaNacimiento,
                        ISNULL(p.Email, '') AS Email,
                        ISNULL(p.Telefono, '') AS Telefono,
                        p.Activo AS Estado,
                        ISNULL(v.IDGrupoVentas, 0) AS IDGrupoVentas,
                        ISNULL(v.IDSucursal, 0) AS IDSucursal
                    FROM dbo.Vendedor v WITH (NOLOCK)
                    INNER JOIN dbo.Persona p WITH (NOLOCK) ON v.PKID = p.PKID;";

                using (var cmd = new SqlCommand(sqlVendedores, connAntiguo))
                using (var r = await cmd.ExecuteReaderAsync(ct))
                {
                    while (await r.ReadAsync(ct))
                    {
                        int pkid = r.GetInt32(0);
                        if (!_idsVendedoresConCambios.Contains(pkid) && totalDifs > 0) continue;

                        try
                        {
                            using var cmdSp = new SqlCommand("dbo.usp_Sync_UpsertVendedorMaestro", connForce) { CommandType = CommandType.StoredProcedure, CommandTimeout = 30 };
                            cmdSp.Parameters.AddWithValue("@ID_Vendedor_Origen", pkid);
                            cmdSp.Parameters.AddWithValue("@CodigoVendedor", r.GetString(1));
                            cmdSp.Parameters.AddWithValue("@NombreCompleto", r.GetString(2));
                            cmdSp.Parameters.AddWithValue("@NombreCorto", r.GetString(3));
                            cmdSp.Parameters.AddWithValue("@DocumentoIdentidad", r.GetString(4));
                            cmdSp.Parameters.AddWithValue("@IDTipoDocIdentidad", r.GetInt32(5));
                            cmdSp.Parameters.AddWithValue("@FechaNacimiento", r.IsDBNull(6) ? (object)DBNull.Value : r.GetDateTime(6).Date);
                            cmdSp.Parameters.AddWithValue("@Email", string.IsNullOrWhiteSpace(r.GetString(7)) ? (object)DBNull.Value : r.GetString(7));
                            cmdSp.Parameters.AddWithValue("@Telefono", string.IsNullOrWhiteSpace(r.GetString(8)) ? (object)DBNull.Value : r.GetString(8));
                            cmdSp.Parameters.AddWithValue("@EstadoVendedor", r.GetBoolean(9));
                            cmdSp.Parameters.AddWithValue("@ID_GrupoVenta_Origen", r.GetInt32(10));
                            cmdSp.Parameters.AddWithValue("@ID_Sucursal_Origen", r.GetInt32(11));
                            await cmdSp.ExecuteNonQueryAsync(ct);
                            procesados++;
                            _currentStatus.ProgresoActual = procesados;
                        }
                        catch (Exception ex)
                        {
                            erroresCount++;
                            _logger.LogWarning(ex, "Error al sincronizar vendedor {PKID}", pkid);
                        }
                    }
                }

                swTotal.Stop();
                transaccion.TotalErrores = erroresCount;
                transaccion.DuracionSegundos = (decimal)swTotal.Elapsed.TotalSeconds;
                transaccion.Estado = erroresCount > 0 ? "CON_ERRORES" : "EXITOSO";
                await RegistrarFinLoteAsync(transaccion, ct);

                _currentStatus.UltimaEjecucion = DateTime.Now;
                _currentStatus.UltimoLoteProcesados = procesados;
                _currentStatus.UltimoLoteErrores = erroresCount;
                _currentStatus.UltimaDuracionSegundos = Math.Round(swTotal.Elapsed.TotalSeconds, 2);
                _currentStatus.EstadoActual = "COMPLETADO";
                _currentStatus.FaseDetalle = $"Sincronización de Vendedores finalizada: {procesados} registros procesados ({erroresCount} errores) en {_currentStatus.UltimaDuracionSegundos}s.";
            }
            catch (Exception ex)
            {
                swTotal.Stop();
                transaccion.Estado = "FALLIDO";
                transaccion.DuracionSegundos = (decimal)swTotal.Elapsed.TotalSeconds;
                await RegistrarFinLoteAsync(transaccion, ct);
                _logger.LogError(ex, "Error fatal al ejecutar sincronización del Módulo de Vendedores.");
                _currentStatus.EstadoActual = "ERROR";
                _currentStatus.FaseDetalle = $"Error: {ex.Message}";
            }
            finally
            {
                _currentStatus.IsRunning = false;
                _syncLock.Release();
            }

            return transaccion;
        }

        private async Task RegistrarInicioLoteAsync(SyncTransaccionDto t, CancellationToken ct)
        {
            try
            {
                using var conn = new SqlConnection(_connectionStringForce);
                await conn.OpenAsync(ct);
                const string sql = @"
                    INSERT INTO dbo.sync_TransaccionLote (
                        LoteId, Entidad, TipoEjecucion, FechaInicio, Estado, Usuario
                    )
                    VALUES (@LoteId, @Entidad, @TipoEjecucion, @FechaInicio, @Estado, @Usuario);
                    SELECT SCOPE_IDENTITY();";
                using var cmd = new SqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@LoteId", t.LoteId);
                cmd.Parameters.AddWithValue("@Entidad", t.Entidad);
                cmd.Parameters.AddWithValue("@TipoEjecucion", t.TipoEjecucion);
                cmd.Parameters.AddWithValue("@FechaInicio", t.FechaInicio);
                cmd.Parameters.AddWithValue("@Estado", t.Estado);
                cmd.Parameters.AddWithValue("@Usuario", t.Usuario ?? "SYSTEM");
                var objId = await cmd.ExecuteScalarAsync(ct);
                if (objId != null) t.ID = Convert.ToInt64(objId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "No se pudo registrar inicio de lote en BDForceGVD.");
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
                        DuracionSegundos = @Duracion,
                        TotalEscaneados = @Escaneados,
                        TotalNuevos = @Nuevos,
                        TotalModificados = @Modificados,
                        TotalErrores = @Errores,
                        Estado = @Estado
                    WHERE LoteId = @LoteId;";
                using var cmd = new SqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@LoteId", t.LoteId);
                cmd.Parameters.AddWithValue("@Duracion", (object?)t.DuracionSegundos ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Escaneados", t.TotalEscaneados);
                cmd.Parameters.AddWithValue("@Nuevos", t.TotalNuevos);
                cmd.Parameters.AddWithValue("@Modificados", t.TotalModificados);
                cmd.Parameters.AddWithValue("@Errores", t.TotalErrores);
                cmd.Parameters.AddWithValue("@Estado", t.Estado);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "No se pudo registrar fin de lote en BDForceGVD.");
            }
        }
    }
}
