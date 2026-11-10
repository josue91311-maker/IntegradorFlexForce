using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IntegradorFlexForce.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace IntegradorFlexForce.Services
{
    public class SyncProductosEngine
    {
        private readonly string _connectionStringForce;
        private readonly string _connectionStringAntiguo;
        private readonly ILogger<SyncProductosEngine> _logger;
        private readonly SemaphoreSlim _syncLock = new(1, 1);

        private readonly SyncStatusDto _currentStatus = new();
        private SyncPreviewDto? _lastPreview;

        // Caches de IDs con diferencias detectadas para aplicar en lote
        private HashSet<int> _idsProductosConCambios = new();
        private HashSet<int> _idsProductosInactivarForce = new();

        private HashSet<int> _idsUnidadesConCambios = new();
        private HashSet<int> _idsUnidadesInactivarForce = new();

        private HashSet<int> _idsItemsPortafolioConCambios = new();
        private HashSet<int> _idsCatalogoProductoInactivarForce = new();

        private HashSet<int> _idsEscalasConCambios = new();
        private HashSet<int> _idsEscalasInactivarForce = new();

        public SyncProductosEngine(IConfiguration config, ILogger<SyncProductosEngine> logger)
        {
            _connectionStringForce = config.GetConnectionString("ForceGVD") ?? "";
            _connectionStringAntiguo = config.GetConnectionString("ERPAntiguo") ?? "";
            _logger = logger;
        }

        public SyncStatusDto GetCurrentStatus() => _currentStatus;
        public SyncPreviewDto? GetLastPreview() => _lastPreview;

        // =========================================================================
        // 1. ESCANEAR Y COMPARAR DIFERENCIAS (SOLO LECTURA EN DIGALIMENTA41)
        // =========================================================================
        public async Task<SyncPreviewDto> AnalizarCambiosPendientesAsync(CancellationToken ct = default)
        {
            var sw = Stopwatch.StartNew();
            var preview = new SyncPreviewDto { FechaAnalisis = DateTime.Now };

            var nuevosIdsProd = new HashSet<int>();
            var inactivarIdsProdForce = new HashSet<int>();

            var nuevosIdsUnid = new HashSet<int>();
            var inactivarIdsUnidForce = new HashSet<int>();

            var nuevosIdsPort = new HashSet<int>();
            var inactivarIdsCatProdForce = new HashSet<int>();

            var nuevosIdsEscala = new HashSet<int>();
            var inactivarIdsEscalaForce = new HashSet<int>();

            const int maxDetallePorSubmodulo = 800;
            int prodDetalleCount = 0;
            int unidDetalleCount = 0;
            int catDetalleCount = 0;
            int prcDetalleCount = 0;

            // ---------------------------------------------------------------------
            // A. COMPARAR CATÁLOGO MAESTRO DE PRODUCTOS, MARCAS, LÍNEAS Y PROVEEDORES
            // ---------------------------------------------------------------------
            int totalProdOrigen = 0, totalProdForce = 0;
            int prodInserts = 0, prodUpdates = 0, prodDeletes = 0;

            var productosForcePorExtra = new Dictionary<int, (int IDForce, string Codigo, string Nombre, int? IDMarcaExtra, string NombreMarca, int? IDLineaExtra, string NombreLinea, int? IDProvExtra, string NombreProv, int? IDSubProvExtra, string NombreSubProv, bool Estado)>(40000);
            var productosForcePorCodigo = new Dictionary<string, (int IDForce, int? IDExtra, string Nombre, int? IDMarcaExtra, string NombreMarca, int? IDLineaExtra, string NombreLinea, int? IDProvExtra, string NombreProv, int? IDSubProvExtra, string NombreSubProv, bool Estado)>(40000, StringComparer.OrdinalIgnoreCase);

            try
            {
                using (var connForce = new SqlConnection(_connectionStringForce))
                {
                    await connForce.OpenAsync(ct);

                    const string sqlProdForce = @"
                        SELECT 
                            p.ID,
                            p.IDExtra,
                            LTRIM(RTRIM(ISNULL(p.Codigo, ''))) AS Codigo,
                            LTRIM(RTRIM(ISNULL(p.Nombre, ''))) AS Nombre,
                            m.IDExtra AS IDMarcaExtra,
                            LTRIM(RTRIM(ISNULL(m.Nombre, ''))) AS NombreMarca,
                            l.IDExtra AS IDLineaExtra,
                            LTRIM(RTRIM(ISNULL(l.Nombre, ''))) AS NombreLinea,
                            pr.IDExtra AS IDProvExtra,
                            LTRIM(RTRIM(ISNULL(pr.RazonSocial, ''))) AS NombreProv,
                            sp.IDExtra AS IDSubProvExtra,
                            LTRIM(RTRIM(ISNULL(sp.Nombre, ''))) AS NombreSubProv,
                            CAST(ISNULL(p.Estado, 1) AS bit) AS Estado
                        FROM dbo.prd_Producto p WITH (NOLOCK)
                        LEFT JOIN dbo.prd_Marca m WITH (NOLOCK) ON m.ID = p.IDMarca
                        LEFT JOIN dbo.prd_LineaProducto l WITH (NOLOCK) ON l.ID = p.IDLinea
                        LEFT JOIN dbo.prd_Proveedor pr WITH (NOLOCK) ON pr.ID = p.IDProveedor
                        LEFT JOIN dbo.prd_SubProveedor sp WITH (NOLOCK) ON sp.ID = p.IDSubProveedor
                        WHERE p.Eliminado = 0;";

                    using var cmd = new SqlCommand(sqlProdForce, connForce) { CommandTimeout = 120 };
                    using var r = await cmd.ExecuteReaderAsync(ct);
                    while (await r.ReadAsync(ct))
                    {
                        totalProdForce++;
                        int idForce = r.GetInt32(0);
                        int? idExtra = r.IsDBNull(1) ? null : r.GetInt32(1);
                        string cod = r.GetString(2);
                        string nom = r.GetString(3);
                        int? idMarcaEx = r.IsDBNull(4) ? null : r.GetInt32(4);
                        string nomMarca = r.GetString(5);
                        int? idLineaEx = r.IsDBNull(6) ? null : r.GetInt32(6);
                        string nomLinea = r.GetString(7);
                        int? idProvEx = r.IsDBNull(8) ? null : r.GetInt32(8);
                        string nomProv = r.GetString(9);
                        int? idSubProvEx = r.IsDBNull(10) ? null : r.GetInt32(10);
                        string nomSubProv = r.GetString(11);
                        bool est = r.GetBoolean(12);

                        if (idExtra.HasValue)
                            productosForcePorExtra[idExtra.Value] = (idForce, cod, nom, idMarcaEx, nomMarca, idLineaEx, nomLinea, idProvEx, nomProv, idSubProvEx, nomSubProv, est);
                        if (!string.IsNullOrEmpty(cod))
                            productosForcePorCodigo[cod] = (idForce, idExtra, nom, idMarcaEx, nomMarca, idLineaEx, nomLinea, idProvEx, nomProv, idSubProvEx, nomSubProv, est);
                    }
                }

                using (var connAntiguo = new SqlConnection(_connectionStringAntiguo))
                {
                    await connAntiguo.OpenAsync(ct);

                    const string sqlProdAntiguo = @"
                        SELECT 
                            p.PKID AS ID_Producto,
                            LTRIM(RTRIM(ISNULL(ps.Codigo, ''))) AS Codigo,
                            LTRIM(RTRIM(ISNULL(ps.Descripcion, ''))) AS Descripcion,
                            CAST(ISNULL(ps.Activo, 0) AS bit) AS Activo,
                            ISNULL(p.IDMarca, 0) AS IDMarca,
                            LTRIM(RTRIM(ISNULL(m.Descripcion, ''))) AS NombreMarca,
                            ISNULL(ps.IDClaseProductoServicio, 0) AS IDLinea,
                            LTRIM(RTRIM(ISNULL(cps.Descripcion, ''))) AS NombreLinea,
                            ISNULL(p.IDProveedor, 0) AS IDProveedor,
                            LTRIM(RTRIM(ISNULL(pp.Nombre, ''))) AS NombreProveedor,
                            ISNULL(subp.PKID, 0) AS IDSubProveedor,
                            ISNULL(subp.Descripcion, '') AS NombreSubProveedor
                        FROM dbo.Producto p WITH (NOLOCK)
                        INNER JOIN dbo.ProductoServicio ps WITH (NOLOCK) ON ps.PKID = p.PKID
                        LEFT JOIN dbo.Marca m WITH (NOLOCK) ON m.PKID = p.IDMarca
                        LEFT JOIN dbo.ClaseProductoServicio cps WITH (NOLOCK) ON cps.PKID = ps.IDClaseProductoServicio
                        LEFT JOIN dbo.Proveedor prv WITH (NOLOCK) ON prv.PKID = p.IDProveedor
                        LEFT JOIN dbo.Persona pp WITH (NOLOCK) ON pp.PKID = prv.PKID
                        OUTER APPLY (
                            SELECT TOP 1 c.PKID, LTRIM(RTRIM(c.Descripcion)) AS Descripcion
                            FROM dbo.Caracteristica c WITH (NOLOCK)
                            INNER JOIN dbo.TipoCaracteristica tc WITH (NOLOCK) ON tc.PKID = c.IDTipoCaracteristica
                            WHERE tc.Descripcion = 'SubProveedor'
                              AND c.PKID > 0
                              AND c.PKID IN (
                                  p.IDCaracteristica1, p.IDCaracteristica2, p.IDCaracteristica3, p.IDCaracteristica4, p.IDCaracteristica5,
                                  p.IDCaracteristica6, p.IDCaracteristica7, p.IDCaracteristica8, p.IDCaracteristica9, p.IDCaracteristica10
                              )
                        ) subp;";

                    using var cmd = new SqlCommand(sqlProdAntiguo, connAntiguo) { CommandTimeout = 120 };
                    using var r = await cmd.ExecuteReaderAsync(ct);
                    while (await r.ReadAsync(ct))
                    {
                        totalProdOrigen++;
                        int idProd = r.GetInt32(0);
                        string cod = r.GetString(1);
                        string nom = r.GetString(2);
                        bool activo = r.GetBoolean(3);
                        int idMarca = r.GetInt32(4);
                        string nomMarca = r.GetString(5);
                        int idLinea = r.GetInt32(6);
                        string nomLinea = r.GetString(7);
                        int idProv = r.GetInt32(8);
                        string nomProv = r.GetString(9);
                        int idSubProv = r.GetInt32(10);
                        string nomSubProv = r.GetString(11);

                        bool encontrado = productosForcePorExtra.TryGetValue(idProd, out var pf);
                        if (!encontrado && !string.IsNullOrEmpty(cod) && productosForcePorCodigo.TryGetValue(cod, out var pfCod))
                        {
                            pf = (pfCod.IDForce, cod, pfCod.Nombre, pfCod.IDMarcaExtra, pfCod.NombreMarca, pfCod.IDLineaExtra, pfCod.NombreLinea, pfCod.IDProvExtra, pfCod.NombreProv, pfCod.IDSubProvExtra, pfCod.NombreSubProv, pfCod.Estado);
                            encontrado = true;
                        }

                        if (!encontrado)
                        {
                            if (!activo) continue; // No insertar productos históricos ya dados de baja que nunca estuvieron en Force

                            prodInserts++;
                            nuevosIdsProd.Add(idProd);
                            if (prodDetalleCount < maxDetallePorSubmodulo)
                            {
                                prodDetalleCount++;
                                preview.DetalleCambios.Add(new CambioDetectadoItemDto
                                {
                                    Tabla = "PRODUCTOS",
                                    TipoOperacion = "INSERT",
                                    IDOrigen = idProd,
                                    CodigoCliente = cod,
                                    NombreCliente = nom,
                                    CampoModificado = "Alta de Producto Nuevo",
                                    ValorActualForce = "No existe en Force",
                                    ValorNuevoERP = $"{cod} - {nom} | Marca: {nomMarca} | Línea: {nomLinea}"
                                });
                            }
                        }
                        else
                        {
                            // Validar baja/inactivación
                            if (pf.Estado && !activo)
                            {
                                prodDeletes++;
                                nuevosIdsProd.Add(idProd);
                                inactivarIdsProdForce.Add(pf.IDForce);
                                if (prodDetalleCount < maxDetallePorSubmodulo)
                                {
                                    prodDetalleCount++;
                                    preview.DetalleCambios.Add(new CambioDetectadoItemDto
                                    {
                                        Tabla = "PRODUCTOS",
                                        TipoOperacion = "BAJA / DELETE",
                                        IDOrigen = idProd,
                                        CodigoCliente = cod,
                                        NombreCliente = nom,
                                        CampoModificado = "Producto Inactivado en ERP Antiguo",
                                        ValorActualForce = "ACTIVO (1)",
                                        ValorNuevoERP = "INACTIVO / BAJA (0)"
                                    });
                                }
                                continue;
                            }

                            if (!activo && !pf.Estado) continue; // Ambos inactivos, sin diferencias

                            var diffs = new List<string>();
                            string valForce = "", valErp = "";

                            if (!pf.Estado && activo)
                            {
                                diffs.Add("Reactivación de Producto");
                                valForce = "INACTIVO (0)";
                                valErp = "ACTIVO (1)";
                            }
                            if (!string.Equals(pf.Nombre, nom, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(nom))
                            {
                                diffs.Add("Descripción");
                                valForce = pf.Nombre;
                                valErp = nom;
                            }
                            if (idMarca > 0 && pf.IDMarcaExtra.HasValue && pf.IDMarcaExtra.Value != idMarca)
                            {
                                diffs.Add("Marca");
                                valForce = $"Marca: {pf.NombreMarca}";
                                valErp = $"Marca: {nomMarca}";
                            }
                            if (idLinea > 0 && pf.IDLineaExtra.HasValue && pf.IDLineaExtra.Value != idLinea)
                            {
                                diffs.Add("Línea");
                                valForce = $"Línea: {pf.NombreLinea}";
                                valErp = $"Línea: {nomLinea}";
                            }
                            if (idSubProv > 0 && pf.IDSubProvExtra.HasValue && pf.IDSubProvExtra.Value != idSubProv)
                            {
                                diffs.Add("SubProveedor");
                                valForce = $"SubProv: {pf.NombreSubProv}";
                                valErp = $"SubProv: {nomSubProv}";
                            }

                            if (diffs.Count > 0)
                            {
                                prodUpdates++;
                                nuevosIdsProd.Add(idProd);
                                if (prodDetalleCount < maxDetallePorSubmodulo)
                                {
                                    prodDetalleCount++;
                                    preview.DetalleCambios.Add(new CambioDetectadoItemDto
                                    {
                                        Tabla = "PRODUCTOS",
                                        TipoOperacion = "UPDATE",
                                        IDOrigen = idProd,
                                        CodigoCliente = cod,
                                        NombreCliente = nom,
                                        CampoModificado = string.Join(", ", diffs),
                                        ValorActualForce = valForce,
                                        ValorNuevoERP = valErp
                                    });
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error al comparar Catálogo de Productos.");
            }

            preview.ResumenPorTabla.Add(new TablaComparacionResumenDto
            {
                CodigoTabla = "PRODUCTOS",
                NombreTabla = "1. Catálogo Maestro de Productos, Marcas y Líneas",
                TablasForce = "dbo.prd_Producto, dbo.prd_Marca, dbo.prd_LineaProducto, dbo.prd_Proveedor, dbo.prd_SubProveedor",
                TotalOrigen = totalProdOrigen,
                TotalDestino = totalProdForce,
                InsertsDetectados = prodInserts,
                UpdatesDetectados = prodUpdates,
                DeletesBajasDetectados = prodDeletes,
                Observacion = (prodInserts + prodUpdates + prodDeletes) == 0 ? "Al día (Sin diferencias)" : "Cambios en productos detectados"
            });

            // ---------------------------------------------------------------------
            // B. COMPARAR PRESENTACIONES Y FACTORES DE CONVERSIÓN (prd_ProductoUnidad)
            // ---------------------------------------------------------------------
            int totalUnidOrigen = 0, totalUnidForce = 0;
            int unidInserts = 0, unidUpdates = 0, unidDeletes = 0;

            try
            {
                var unidadesForcePorExtra = new Dictionary<int, (int IDForce, int? IDProdExtra, string Abrev, decimal Factor, bool EsBase, bool Estado)>(60000);
                var unidadesForcePorProdAbrev = new Dictionary<string, (int IDForce, int? IDProdExtra, string Abrev, decimal Factor, bool EsBase, bool Estado)>(60000, StringComparer.OrdinalIgnoreCase);

                using (var connForce = new SqlConnection(_connectionStringForce))
                {
                    await connForce.OpenAsync(ct);

                    const string sqlUnidForce = @"
                        SELECT 
                            pu.ID,
                            pu.IDExtra,
                            p.IDExtra AS IDProdExtra,
                            LTRIM(RTRIM(ISNULL(pu.Abrev, ''))) AS Abrev,
                            ISNULL(pu.Factor, 1) AS Factor,
                            CAST(ISNULL(pu.EsUnidadBase, 0) AS bit) AS EsBase,
                            CAST(ISNULL(pu.Estado, 1) AS bit) AS Estado
                        FROM dbo.prd_ProductoUnidad pu WITH (NOLOCK)
                        INNER JOIN dbo.prd_Producto p WITH (NOLOCK) ON p.ID = pu.IDProducto
                        WHERE pu.Eliminado = 0 AND p.Eliminado = 0;";

                    using var cmd = new SqlCommand(sqlUnidForce, connForce) { CommandTimeout = 120 };
                    using var r = await cmd.ExecuteReaderAsync(ct);
                    while (await r.ReadAsync(ct))
                    {
                        totalUnidForce++;
                        int idForce = r.GetInt32(0);
                        int? idExtra = r.IsDBNull(1) ? null : r.GetInt32(1);
                        int? idProdEx = r.IsDBNull(2) ? null : r.GetInt32(2);
                        string abrev = r.GetString(3);
                        decimal factor = r.GetDecimal(4);
                        bool esBase = r.GetBoolean(5);
                        bool est = r.GetBoolean(6);

                        if (idExtra.HasValue)
                            unidadesForcePorExtra[idExtra.Value] = (idForce, idProdEx, abrev, factor, esBase, est);

                        if (idProdEx.HasValue && !string.IsNullOrEmpty(abrev))
                        {
                            string claveProd = $"{idProdEx.Value}_{abrev.Trim()}_{factor:F4}";
                            unidadesForcePorProdAbrev[claveProd] = (idForce, idProdEx, abrev, factor, esBase, est);
                        }
                    }
                }

                using (var connAntiguo = new SqlConnection(_connectionStringAntiguo))
                {
                    await connAntiguo.OpenAsync(ct);

                    const string sqlUnidAntiguo = @"
                        SELECT 
                            u.PKID AS ID_Unidad,
                            u.IDProductoServicio AS ID_Producto,
                            LTRIM(RTRIM(ISNULL(ps.Codigo, ''))) AS CodigoProducto,
                            LTRIM(RTRIM(ISNULL(ps.Descripcion, ''))) AS NombreProducto,
                            LTRIM(RTRIM(ISNULL(u.Descripcion, ''))) AS NombreUnidad,
                            LTRIM(RTRIM(ISNULL(u.Abreviacion, 'UN'))) AS Abrev,
                            CAST(ISNULL(u.Base, 0) AS bit) AS EsBase,
                            CAST(ISNULL(u.Factor, 1) AS decimal(18,6)) AS Factor,
                            CAST(ISNULL(u.Activo, 0) AS bit) AS Activo
                        FROM dbo.Unidad u WITH (NOLOCK)
                        INNER JOIN dbo.ProductoServicio ps WITH (NOLOCK) ON ps.PKID = u.IDProductoServicio
                        INNER JOIN dbo.Producto p WITH (NOLOCK) ON p.PKID = ps.PKID;";

                    using var cmd = new SqlCommand(sqlUnidAntiguo, connAntiguo) { CommandTimeout = 120 };
                    using var r = await cmd.ExecuteReaderAsync(ct);
                    while (await r.ReadAsync(ct))
                    {
                        totalUnidOrigen++;
                        int idUnid = r.GetInt32(0);
                        int idProd = r.GetInt32(1);
                        string codProd = r.GetString(2);
                        string nomProd = r.GetString(3);
                        string nomUnid = r.GetString(4);
                        string abrev = r.GetString(5);
                        bool esBase = r.GetBoolean(6);
                        decimal factor = r.GetDecimal(7);
                        bool activo = r.GetBoolean(8);

                        bool encontrada = unidadesForcePorExtra.TryGetValue(idUnid, out var uf);
                        if (!encontrada && !string.IsNullOrEmpty(abrev))
                        {
                            string claveProd = $"{idProd}_{abrev.Trim()}_{factor:F4}";
                            if (unidadesForcePorProdAbrev.TryGetValue(claveProd, out var ufProd))
                            {
                                uf = ufProd;
                                encontrada = true;
                            }
                        }

                        if (!encontrada)
                        {
                            if (!activo) continue;

                            unidInserts++;
                            nuevosIdsUnid.Add(idUnid);
                            if (unidDetalleCount < maxDetallePorSubmodulo)
                            {
                                unidDetalleCount++;
                                preview.DetalleCambios.Add(new CambioDetectadoItemDto
                                {
                                    Tabla = "UNIDADES Y FACTORES",
                                    TipoOperacion = "INSERT",
                                    IDOrigen = idUnid,
                                    CodigoCliente = codProd,
                                    NombreCliente = nomProd,
                                    CampoModificado = $"Nueva Presentación ({abrev})",
                                    ValorActualForce = "Sin unidad en Force",
                                    ValorNuevoERP = $"Unidad: {nomUnid} ({abrev}) | Factor: {factor:N2} | Base: {(esBase ? "Sí" : "No")}"
                                });
                            }
                        }
                        else
                        {
                            if (uf.Estado && !activo)
                            {
                                unidDeletes++;
                                nuevosIdsUnid.Add(idUnid);
                                inactivarIdsUnidForce.Add(uf.IDForce);
                                if (unidDetalleCount < maxDetallePorSubmodulo)
                                {
                                    unidDetalleCount++;
                                    preview.DetalleCambios.Add(new CambioDetectadoItemDto
                                    {
                                        Tabla = "UNIDADES Y FACTORES",
                                        TipoOperacion = "BAJA / DELETE",
                                        IDOrigen = idUnid,
                                        CodigoCliente = codProd,
                                        NombreCliente = nomProd,
                                        CampoModificado = $"Presentación Inactivada ({abrev})",
                                        ValorActualForce = $"Factor: {uf.Factor:N2} | ACTIVO",
                                        ValorNuevoERP = "INACTIVO / BAJA (0)"
                                    });
                                }
                                continue;
                            }

                            if (!activo && !uf.Estado) continue;

                            bool diffFactor = Math.Abs(uf.Factor - factor) > 0.001m;
                            bool diffAbrev = !string.Equals(uf.Abrev, abrev, StringComparison.OrdinalIgnoreCase);
                            bool diffEstado = !uf.Estado && activo;

                            if (diffFactor || diffAbrev || diffEstado)
                            {
                                unidUpdates++;
                                nuevosIdsUnid.Add(idUnid);
                                if (unidDetalleCount < maxDetallePorSubmodulo)
                                {
                                    unidDetalleCount++;
                                    preview.DetalleCambios.Add(new CambioDetectadoItemDto
                                    {
                                        Tabla = "UNIDADES Y FACTORES",
                                        TipoOperacion = "UPDATE",
                                        IDOrigen = idUnid,
                                        CodigoCliente = codProd,
                                        NombreCliente = nomProd,
                                        CampoModificado = diffFactor ? $"Cambio de Factor ({abrev})" : $"Actualización Unidad ({abrev})",
                                        ValorActualForce = $"Unidad: {uf.Abrev} | Factor: {uf.Factor:N2} | {(uf.Estado ? "ACTIVO" : "INACTIVO")}",
                                        ValorNuevoERP = $"Unidad: {abrev} | Factor: {factor:N2} | {(activo ? "ACTIVO" : "INACTIVO")}"
                                    });
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error al comparar Unidades y Factores de Conversión.");
            }

            preview.ResumenPorTabla.Add(new TablaComparacionResumenDto
            {
                CodigoTabla = "UNIDADES_FACTORES",
                NombreTabla = "2. Presentaciones y Factores de Conversión",
                TablasForce = "dbo.prd_ProductoUnidad, dbo.prd_UnidadMedida",
                TotalOrigen = totalUnidOrigen,
                TotalDestino = totalUnidForce,
                InsertsDetectados = unidInserts,
                UpdatesDetectados = unidUpdates,
                DeletesBajasDetectados = unidDeletes,
                Observacion = (unidInserts + unidUpdates + unidDeletes) == 0 ? "Al día (Sin diferencias)" : "Cambios de factor/unidad detectados"
            });

            // ---------------------------------------------------------------------
            // C. COMPARAR CATÁLOGOS Y PORTAFOLIOS DE PRODUCTOS (ctg_CatalogoProducto)
            // ---------------------------------------------------------------------
            int totalCatOrigen = 0, totalCatForce = 0;
            int catInserts = 0, catUpdates = 0, catDeletes = 0;

            try
            {
                // Clave: "{IDPortafolioOrigen}_{IDProductoOrigen}" -> (IDCatalogoProductoForce, Estado)
                var catalogoProdForce = new Dictionary<string, (int IDForce, int IDPortOrigen, int IDProdOrigen, string CodCatalogo, string CodProducto, bool Estado)>(100000, StringComparer.OrdinalIgnoreCase);

                using (var connForce = new SqlConnection(_connectionStringForce))
                {
                    await connForce.OpenAsync(ct);

                    const string sqlCatForce = @"
                        SELECT 
                            cp.ID,
                            c.IDExtra AS IDPortafolioOrigen,
                            p.IDExtra AS IDProductoOrigen,
                            LTRIM(RTRIM(ISNULL(c.Codigo, ''))) AS CodCatalogo,
                            LTRIM(RTRIM(ISNULL(p.Codigo, ''))) AS CodProducto,
                            CAST(ISNULL(cp.Estado, 1) AS bit) AS Estado
                        FROM dbo.ctg_CatalogoProducto cp WITH (NOLOCK)
                        INNER JOIN dbo.ctg_Catalogo c WITH (NOLOCK) ON c.ID = cp.IDCatalogo
                        INNER JOIN dbo.prd_Producto p WITH (NOLOCK) ON p.ID = cp.IDProducto
                        WHERE c.Eliminado = 0 AND p.Eliminado = 0;";

                    using var cmd = new SqlCommand(sqlCatForce, connForce) { CommandTimeout = 120 };
                    using var r = await cmd.ExecuteReaderAsync(ct);
                    while (await r.ReadAsync(ct))
                    {
                        int idCp = r.GetInt32(0);
                        int? idPortEx = r.IsDBNull(1) ? null : r.GetInt32(1);
                        int? idProdEx = r.IsDBNull(2) ? null : r.GetInt32(2);
                        string codCat = r.GetString(3);
                        string codProd = r.GetString(4);
                        bool est = r.GetBoolean(5);

                        if (est) totalCatForce++;

                        if (idPortEx.HasValue && idProdEx.HasValue)
                        {
                            catalogoProdForce[$"{idPortEx.Value}_{idProdEx.Value}"] = (idCp, idPortEx.Value, idProdEx.Value, codCat, codProd, est);
                        }
                    }
                }

                var clavesVistasEnOrigen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                using (var connAntiguo = new SqlConnection(_connectionStringAntiguo))
                {
                    await connAntiguo.OpenAsync(ct);

                    const string sqlCatAntiguo = @"
                        SELECT 
                            ip.PKID AS ID_ItemPortafolio,
                            ip.IDPortafolioProducto,
                            LTRIM(RTRIM(ISNULL(pp.Codigo, ''))) AS CodigoPortafolio,
                            LTRIM(RTRIM(ISNULL(pp.Nombre, ''))) AS NombrePortafolio,
                            CAST(ISNULL(pp.Activo, 1) AS bit) AS PortafolioActivo,
                            ip.IDProducto,
                            LTRIM(RTRIM(ISNULL(ps.Codigo, ISNULL(ip.Codigo, '')))) AS CodigoProducto,
                            LTRIM(RTRIM(ISNULL(ps.Descripcion, ISNULL(ip.Descripcion, '')))) AS NombreProducto
                        FROM dbo.ItemPortafolioProductoProducto ip WITH (NOLOCK)
                        INNER JOIN dbo.PortafolioProducto pp WITH (NOLOCK) ON pp.PKID = ip.IDPortafolioProducto
                        LEFT JOIN dbo.ProductoServicio ps WITH (NOLOCK) ON ps.PKID = ip.IDProducto;";

                    using var cmd = new SqlCommand(sqlCatAntiguo, connAntiguo) { CommandTimeout = 120 };
                    using var r = await cmd.ExecuteReaderAsync(ct);
                    while (await r.ReadAsync(ct))
                    {
                        int idItem = r.GetInt32(0);
                        int idPort = r.GetInt32(1);
                        string codPort = r.GetString(2);
                        string nomPort = r.GetString(3);
                        bool portActivo = r.GetBoolean(4);
                        int idProd = r.GetInt32(5);
                        string codProd = r.GetString(6);
                        string nomProd = r.GetString(7);

                        if (!portActivo) continue; // Solo portafolios activos

                        totalCatOrigen++;
                        string key = $"{idPort}_{idProd}";
                        clavesVistasEnOrigen.Add(key);

                        if (!catalogoProdForce.TryGetValue(key, out var cf))
                        {
                            catInserts++;
                            nuevosIdsPort.Add(idItem);
                            if (catDetalleCount < maxDetallePorSubmodulo)
                            {
                                catDetalleCount++;
                                preview.DetalleCambios.Add(new CambioDetectadoItemDto
                                {
                                    Tabla = "CATALOGOS",
                                    TipoOperacion = "INSERT",
                                    IDOrigen = idItem,
                                    CodigoCliente = codProd,
                                    NombreCliente = nomProd,
                                    CampoModificado = $"Producto en Catálogo ({nomPort})",
                                    ValorActualForce = "No asignado al catálogo en Force",
                                    ValorNuevoERP = $"Catálogo: {codPort} - {nomPort} | Producto: {codProd}"
                                });
                            }
                        }
                        else if (!cf.Estado)
                        {
                            catUpdates++;
                            nuevosIdsPort.Add(idItem);
                            if (catDetalleCount < maxDetallePorSubmodulo)
                            {
                                catDetalleCount++;
                                preview.DetalleCambios.Add(new CambioDetectadoItemDto
                                {
                                    Tabla = "CATALOGOS",
                                    TipoOperacion = "UPDATE",
                                    IDOrigen = idItem,
                                    CodigoCliente = codProd,
                                    NombreCliente = nomProd,
                                    CampoModificado = $"Reactivar en Catálogo ({nomPort})",
                                    ValorActualForce = "INACTIVO (0)",
                                    ValorNuevoERP = $"ACTIVO en {codPort} - {nomPort}"
                                });
                            }
                        }
                    }
                }

                // Detectar productos retirados de un catálogo en el ERP Antiguo
                foreach (var kvp in catalogoProdForce)
                {
                    if (kvp.Value.Estado && !clavesVistasEnOrigen.Contains(kvp.Key))
                    {
                        catDeletes++;
                        inactivarIdsCatProdForce.Add(kvp.Value.IDForce);
                        if (catDetalleCount < maxDetallePorSubmodulo)
                        {
                            catDetalleCount++;
                            preview.DetalleCambios.Add(new CambioDetectadoItemDto
                            {
                                Tabla = "CATALOGOS",
                                TipoOperacion = "BAJA / DELETE",
                                IDOrigen = kvp.Value.IDForce,
                                CodigoCliente = kvp.Value.CodProducto,
                                NombreCliente = $"Catálogo {kvp.Value.CodCatalogo}",
                                CampoModificado = $"Producto retirado de Catálogo ({kvp.Value.CodCatalogo})",
                                ValorActualForce = $"Asignado en Catálogo {kvp.Value.CodCatalogo}",
                                ValorNuevoERP = "Retirado del portafolio en ERP Antiguo"
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error al comparar Catálogos y Portafolios de Productos.");
            }

            preview.ResumenPorTabla.Add(new TablaComparacionResumenDto
            {
                CodigoTabla = "CATALOGOS_PORTAFOLIO",
                NombreTabla = "3. Catálogos y Portafolios de Productos",
                TablasForce = "dbo.ctg_Catalogo, dbo.ctg_CatalogoProducto",
                TotalOrigen = totalCatOrigen,
                TotalDestino = totalCatForce,
                InsertsDetectados = catInserts,
                UpdatesDetectados = catUpdates,
                DeletesBajasDetectados = catDeletes,
                Observacion = (catInserts + catUpdates + catDeletes) == 0 ? "Al día (Sin diferencias)" : "Cambios de catálogo detectados"
            });

            // ---------------------------------------------------------------------
            // D. COMPARAR LISTAS DE PRECIOS Y ESCALAS VIGENTES (prc_PrecioEscala)
            // ---------------------------------------------------------------------
            int totalPrcOrigen = 0, totalPrcForce = 0;
            int prcInserts = 0, prcUpdates = 0, prcDeletes = 0;

            try
            {
                var escalasForcePorExtra = new Dictionary<int, (int IDForce, decimal Precio, decimal PrecioIncImpto, decimal Desde, decimal Hasta, bool EsVigente, string NombreLista, string CodProducto)>(150000);

                using (var connForce = new SqlConnection(_connectionStringForce))
                {
                    await connForce.OpenAsync(ct);

                    const string sqlPrcForce = @"
                        SELECT 
                            pe.ID,
                            pe.IDExtra,
                            ISNULL(pe.Precio, 0) AS Precio,
                            ISNULL(pe.PrecioIncImpto, 0) AS PrecioIncImpto,
                            ISNULL(pe.Desde, 0) AS Desde,
                            ISNULL(pe.Hasta, 0) AS Hasta,
                            CAST(ISNULL(pe.Esvigente, 1) AS bit) AS EsVigente,
                            LTRIM(RTRIM(ISNULL(lp.Nombre, ''))) AS NombreLista,
                            LTRIM(RTRIM(ISNULL(p.Codigo, ''))) AS CodProducto
                        FROM dbo.prc_PrecioEscala pe WITH (NOLOCK)
                        INNER JOIN dbo.prc_PrecioProductoUnidad ppu WITH (NOLOCK) ON ppu.ID = pe.IDUnidadItemListaPrecios
                        INNER JOIN dbo.prc_ItemPrecioProducto ipp WITH (NOLOCK) ON ipp.ID = ppu.IDItemPrecioProducto
                        INNER JOIN dbo.prc_ListaPrecio lp WITH (NOLOCK) ON lp.ID = ipp.IDListaPrecios
                        INNER JOIN dbo.prd_Producto p WITH (NOLOCK) ON p.ID = ipp.IDProducto
                        WHERE pe.Eliminado = 0 AND ipp.Eliminado = 0 AND lp.Eliminado = 0;";

                    using var cmd = new SqlCommand(sqlPrcForce, connForce) { CommandTimeout = 120 };
                    using var r = await cmd.ExecuteReaderAsync(ct);
                    while (await r.ReadAsync(ct))
                    {
                        int idForce = r.GetInt32(0);
                        int? idExtra = r.IsDBNull(1) ? null : r.GetInt32(1);
                        decimal precio = r.GetDecimal(2);
                        decimal precioIgv = r.GetDecimal(3);
                        decimal desde = r.GetDecimal(4);
                        decimal hasta = r.GetDecimal(5);
                        bool esVig = r.GetBoolean(6);
                        string nomLista = r.GetString(7);
                        string codProd = r.GetString(8);

                        if (esVig) totalPrcForce++;

                        if (idExtra.HasValue)
                            escalasForcePorExtra[idExtra.Value] = (idForce, precio, precioIgv, desde, hasta, esVig, nomLista, codProd);
                    }
                }

                var escalasVistasEnOrigen = new HashSet<int>();

                using (var connAntiguo = new SqlConnection(_connectionStringAntiguo))
                {
                    await connAntiguo.OpenAsync(ct);

                    const string sqlPrcAntiguo = @"
                        SELECT 
                            e.PKID AS ID_Escala,
                            LTRIM(RTRIM(ISNULL(lp.Descripcion, ''))) AS NombreLista,
                            LTRIM(RTRIM(ISNULL(ps.Codigo, ''))) AS CodigoProducto,
                            LTRIM(RTRIM(ISNULL(ps.Descripcion, ''))) AS NombreProducto,
                            LTRIM(RTRIM(ISNULL(u.Abreviacion, 'UN'))) AS AbrevUnidad,
                            CAST(ISNULL(e.Desde, 0) AS decimal(18,4)) AS Desde,
                            CAST(ISNULL(e.Hasta, 999999) AS decimal(18,4)) AS Hasta,
                            CAST(ISNULL(e.Precio, 0) AS decimal(18,6)) AS Precio,
                            CAST(ISNULL(e.PrecioIncImpto, 0) AS decimal(18,6)) AS PrecioIncImpto,
                            CAST(ISNULL(e.EsVigente, 0) AS bit) AS EsVigente
                        FROM dbo.Escala e WITH (NOLOCK)
                        INNER JOIN dbo.UnidadItemListaPrecios uilp WITH (NOLOCK) ON uilp.PKID = e.IDUnidadItemListaPrecios
                        INNER JOIN dbo.ItemListaPrecios ilp WITH (NOLOCK) ON ilp.PKID = uilp.IDItemListaPrecios
                        INNER JOIN dbo.ListaPrecios lp WITH (NOLOCK) ON lp.PKID = ilp.IDListaPrecios
                        INNER JOIN dbo.ProductoServicio ps WITH (NOLOCK) ON ps.PKID = ilp.IDProducto
                        INNER JOIN dbo.Unidad u WITH (NOLOCK) ON u.PKID = uilp.IDUnidad
                        WHERE e.EsVigente = 1
                          AND lp.Activo = 1
                          AND ilp.Desactivado = 0
                          AND ps.Activo = 1;";

                    using var cmd = new SqlCommand(sqlPrcAntiguo, connAntiguo) { CommandTimeout = 180 };
                    using var r = await cmd.ExecuteReaderAsync(ct);
                    while (await r.ReadAsync(ct))
                    {
                        totalPrcOrigen++;
                        int idEscala = r.GetInt32(0);
                        string nomLista = r.GetString(1);
                        string codProd = r.GetString(2);
                        string nomProd = r.GetString(3);
                        string abrev = r.GetString(4);
                        decimal desde = r.GetDecimal(5);
                        decimal hasta = r.GetDecimal(6);
                        decimal precio = r.GetDecimal(7);
                        decimal precioIgv = r.GetDecimal(8);

                        escalasVistasEnOrigen.Add(idEscala);

                        if (!escalasForcePorExtra.TryGetValue(idEscala, out var ef))
                        {
                            prcInserts++;
                            nuevosIdsEscala.Add(idEscala);
                            if (prcDetalleCount < maxDetallePorSubmodulo)
                            {
                                prcDetalleCount++;
                                preview.DetalleCambios.Add(new CambioDetectadoItemDto
                                {
                                    Tabla = "PRECIOS Y ESCALAS",
                                    TipoOperacion = "INSERT",
                                    IDOrigen = idEscala,
                                    CodigoCliente = codProd,
                                    NombreCliente = nomProd,
                                    CampoModificado = $"Nuevo Precio en Lista {nomLista} ({abrev})",
                                    ValorActualForce = "Sin escala en Force",
                                    ValorNuevoERP = $"Lista: {nomLista} | S/. {precioIgv:N4} (Neto: S/. {precio:N4})"
                                });
                            }
                        }
                        else
                        {
                            bool diffPrecio = Math.Abs(ef.Precio - precio) > 0.0001m || Math.Abs(ef.PrecioIncImpto - precioIgv) > 0.0001m;
                            bool diffVig = !ef.EsVigente;

                            if (diffPrecio || diffVig)
                            {
                                prcUpdates++;
                                nuevosIdsEscala.Add(idEscala);
                                if (prcDetalleCount < maxDetallePorSubmodulo)
                                {
                                    prcDetalleCount++;
                                    preview.DetalleCambios.Add(new CambioDetectadoItemDto
                                    {
                                        Tabla = "PRECIOS Y ESCALAS",
                                        TipoOperacion = "UPDATE",
                                        IDOrigen = idEscala,
                                        CodigoCliente = codProd,
                                        NombreCliente = nomProd,
                                        CampoModificado = $"Cambio de Precio Lista {nomLista} ({abrev})",
                                        ValorActualForce = $"S/. {ef.PrecioIncImpto:N4} (Neto: S/. {ef.Precio:N4})",
                                        ValorNuevoERP = $"S/. {precioIgv:N4} (Neto: S/. {precio:N4})"
                                    });
                                }
                            }
                        }
                    }
                }

                // Detectar escalas que quedaron vigentes en Force pero ya no están vigentes en ERP Antiguo
                foreach (var kvp in escalasForcePorExtra)
                {
                    if (kvp.Value.EsVigente && !escalasVistasEnOrigen.Contains(kvp.Key))
                    {
                        prcDeletes++;
                        inactivarIdsEscalaForce.Add(kvp.Value.IDForce);
                        if (prcDetalleCount < maxDetallePorSubmodulo)
                        {
                            prcDetalleCount++;
                            preview.DetalleCambios.Add(new CambioDetectadoItemDto
                            {
                                Tabla = "PRECIOS Y ESCALAS",
                                TipoOperacion = "BAJA / DELETE",
                                IDOrigen = kvp.Key,
                                CodigoCliente = kvp.Value.CodProducto,
                                NombreCliente = $"Lista {kvp.Value.NombreLista}",
                                CampoModificado = "Escala dejó de estar vigente",
                                ValorActualForce = $"S/. {kvp.Value.PrecioIncImpto:N4} | VIGENTE",
                                ValorNuevoERP = "NO VIGENTE / INACTIVO"
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error al comparar Listas de Precios y Escalas.");
            }

            preview.ResumenPorTabla.Add(new TablaComparacionResumenDto
            {
                CodigoTabla = "PRECIOS_ESCALAS",
                NombreTabla = "4. Listas de Precios y Escalas Vigentes",
                TablasForce = "dbo.prc_ListaPrecio, dbo.prc_ItemPrecioProducto, dbo.prc_PrecioProductoUnidad, dbo.prc_PrecioEscala",
                TotalOrigen = totalPrcOrigen,
                TotalDestino = totalPrcForce,
                InsertsDetectados = prcInserts,
                UpdatesDetectados = prcUpdates,
                DeletesBajasDetectados = prcDeletes,
                Observacion = (prcInserts + prcUpdates + prcDeletes) == 0 ? "Al día (Sin diferencias)" : "Cambios de precio detectados"
            });

            sw.Stop();
            preview.TiempoEscaneoSegundos = Math.Round(sw.Elapsed.TotalSeconds, 2);

            _idsProductosConCambios = nuevosIdsProd;
            _idsProductosInactivarForce = inactivarIdsProdForce;
            _idsUnidadesConCambios = nuevosIdsUnid;
            _idsUnidadesInactivarForce = inactivarIdsUnidForce;
            _idsItemsPortafolioConCambios = nuevosIdsPort;
            _idsCatalogoProductoInactivarForce = inactivarIdsCatProdForce;
            _idsEscalasConCambios = nuevosIdsEscala;
            _idsEscalasInactivarForce = inactivarIdsEscalaForce;

            _lastPreview = preview;
            return preview;
        }

        // =========================================================================
        // 2. EJECUTAR SINCRONIZACIÓN DE PRODUCTOS, CATÁLOGO Y PRECIOS EN BDFORCEGVD
        // =========================================================================
        public async Task<SyncTransaccionDto> EjecutarSincronizacionProductosAsync(string subModulo = "TODOS", string usuario = "PORTAL_WEB", CancellationToken ct = default)
        {
            if (!await _syncLock.WaitAsync(0, ct))
            {
                return new SyncTransaccionDto { Estado = "EN_PROCESO", Usuario = usuario };
            }

            var loteId = Guid.NewGuid();
            var swTotal = Stopwatch.StartNew();
            _currentStatus.IsRunning = true;
            _currentStatus.EstadoActual = "SINCRONIZANDO_PRODUCTOS";
            _currentStatus.FaseDetalle = "Analizando diferencias en Productos, Catálogo y Precios...";
            _currentStatus.ProgresoActual = 0;
            _currentStatus.ProgresoTotal = 0;

            var transaccion = new SyncTransaccionDto
            {
                LoteId = loteId,
                Entidad = $"PRODUCTOS_{subModulo}",
                TipoEjecucion = "MANUAL",
                FechaInicio = DateTime.Now,
                Estado = "EN_PROCESO",
                Usuario = usuario
            };

            try
            {
                var preview = await AnalizarCambiosPendientesAsync(ct);

                bool syncProd = subModulo == "TODOS" || subModulo == "PRODUCTOS";
                bool syncUnid = subModulo == "TODOS" || subModulo == "UNIDADES_FACTORES";
                bool syncCat  = subModulo == "TODOS" || subModulo == "CATALOGOS_PORTAFOLIO";
                bool syncPrc  = subModulo == "TODOS" || subModulo == "PRECIOS_ESCALAS";

                int totalOps = (syncProd ? _idsProductosConCambios.Count : 0)
                             + (syncUnid ? _idsUnidadesConCambios.Count : 0)
                             + (syncCat  ? _idsItemsPortafolioConCambios.Count + _idsCatalogoProductoInactivarForce.Count : 0)
                             + (syncPrc  ? _idsEscalasConCambios.Count + _idsEscalasInactivarForce.Count : 0);

                _currentStatus.ProgresoTotal = Math.Max(1, totalOps);
                int procesados = 0, errores = 0;
                const int batchSize = 500;

                // 1. PRODUCTOS MAESTROS
                if (syncProd && _idsProductosConCambios.Count > 0)
                {
                    var ids = _idsProductosConCambios.ToList();
                    for (int i = 0; i < ids.Count; i += batchSize)
                    {
                        ct.ThrowIfCancellationRequested();
                        var lote = ids.Skip(i).Take(batchSize).ToList();
                        _currentStatus.FaseDetalle = $"Sincronizando Productos Maestros: {Math.Min(i + batchSize, ids.Count):N0} de {ids.Count:N0}";
                        var (p, e) = await ProcesarLoteProductosMaestrosAsync(lote, ct);
                        procesados += p;
                        errores += e;
                        _currentStatus.ProgresoActual = Math.Min(_currentStatus.ProgresoTotal, _currentStatus.ProgresoActual + lote.Count);
                    }
                }

                // 2. PRESENTACIONES Y FACTORES
                if (syncUnid && _idsUnidadesConCambios.Count > 0)
                {
                    var ids = _idsUnidadesConCambios.ToList();
                    for (int i = 0; i < ids.Count; i += batchSize)
                    {
                        ct.ThrowIfCancellationRequested();
                        var lote = ids.Skip(i).Take(batchSize).ToList();
                        _currentStatus.FaseDetalle = $"Sincronizando Presentaciones y Factores: {Math.Min(i + batchSize, ids.Count):N0} de {ids.Count:N0}";
                        var (p, e) = await ProcesarLoteUnidadesAsync(lote, ct);
                        procesados += p;
                        errores += e;
                        _currentStatus.ProgresoActual = Math.Min(_currentStatus.ProgresoTotal, _currentStatus.ProgresoActual + lote.Count);
                    }
                }

                // 3. CATÁLOGOS Y PORTAFOLIOS
                if (syncCat)
                {
                    if (_idsCatalogoProductoInactivarForce.Count > 0)
                    {
                        using var connForce = new SqlConnection(_connectionStringForce);
                        await connForce.OpenAsync(ct);
                        var idsBaja = _idsCatalogoProductoInactivarForce.ToList();
                        for (int i = 0; i < idsBaja.Count; i += batchSize)
                        {
                            var lote = idsBaja.Skip(i).Take(batchSize).ToList();
                            var sql = $"UPDATE dbo.ctg_CatalogoProducto SET Estado = 0, FechaModificacion = GETDATE() WHERE ID IN ({string.Join(",", lote)});";
                            using var cmd = new SqlCommand(sql, connForce) { CommandTimeout = 60 };
                            await cmd.ExecuteNonQueryAsync(ct);
                            procesados += lote.Count;
                            _currentStatus.ProgresoActual = Math.Min(_currentStatus.ProgresoTotal, _currentStatus.ProgresoActual + lote.Count);
                        }
                    }

                    if (_idsItemsPortafolioConCambios.Count > 0)
                    {
                        var ids = _idsItemsPortafolioConCambios.ToList();
                        for (int i = 0; i < ids.Count; i += batchSize)
                        {
                            ct.ThrowIfCancellationRequested();
                            var lote = ids.Skip(i).Take(batchSize).ToList();
                            _currentStatus.FaseDetalle = $"Sincronizando Catálogos y Portafolios: {Math.Min(i + batchSize, ids.Count):N0} de {ids.Count:N0}";
                            var (p, e) = await ProcesarLoteCatalogosAsync(lote, ct);
                            procesados += p;
                            errores += e;
                            _currentStatus.ProgresoActual = Math.Min(_currentStatus.ProgresoTotal, _currentStatus.ProgresoActual + lote.Count);
                        }
                    }
                }

                // 4. LISTAS DE PRECIOS Y ESCALAS
                if (syncPrc)
                {
                    if (_idsEscalasInactivarForce.Count > 0)
                    {
                        using var connForce = new SqlConnection(_connectionStringForce);
                        await connForce.OpenAsync(ct);
                        var idsBaja = _idsEscalasInactivarForce.ToList();
                        for (int i = 0; i < idsBaja.Count; i += batchSize)
                        {
                            var lote = idsBaja.Skip(i).Take(batchSize).ToList();
                            var sql = $"UPDATE dbo.prc_PrecioEscala SET Esvigente = 0, FechaModificacion = GETDATE() WHERE ID IN ({string.Join(",", lote)});";
                            using var cmd = new SqlCommand(sql, connForce) { CommandTimeout = 60 };
                            await cmd.ExecuteNonQueryAsync(ct);
                            procesados += lote.Count;
                            _currentStatus.ProgresoActual = Math.Min(_currentStatus.ProgresoTotal, _currentStatus.ProgresoActual + lote.Count);
                        }
                    }

                    if (_idsEscalasConCambios.Count > 0)
                    {
                        var ids = _idsEscalasConCambios.ToList();
                        for (int i = 0; i < ids.Count; i += batchSize)
                        {
                            ct.ThrowIfCancellationRequested();
                            var lote = ids.Skip(i).Take(batchSize).ToList();
                            _currentStatus.FaseDetalle = $"Sincronizando Precios y Escalas: {Math.Min(i + batchSize, ids.Count):N0} de {ids.Count:N0}";
                            var (p, e) = await ProcesarLotePreciosEscalasAsync(lote, ct);
                            procesados += p;
                            errores += e;
                            _currentStatus.ProgresoActual = Math.Min(_currentStatus.ProgresoTotal, _currentStatus.ProgresoActual + lote.Count);
                        }
                    }
                }

                swTotal.Stop();
                transaccion.TotalEscaneados = preview.ResumenPorTabla.Sum(x => x.TotalOrigen);
                transaccion.TotalNuevos = preview.ResumenPorTabla.Sum(x => x.InsertsDetectados);
                transaccion.TotalModificados = procesados;
                transaccion.TotalErrores = errores;
                transaccion.DuracionSegundos = (decimal)swTotal.Elapsed.TotalSeconds;
                transaccion.Estado = errores > 0 ? "CON_ERRORES" : "EXITOSO";

                _currentStatus.UltimaEjecucion = DateTime.Now;
                _currentStatus.UltimoLoteProcesados = procesados;
                _currentStatus.UltimoLoteErrores = errores;
                _currentStatus.UltimaDuracionSegundos = Math.Round(swTotal.Elapsed.TotalSeconds, 2);
                _currentStatus.EstadoActual = "COMPLETADO";
                _currentStatus.FaseDetalle = $"Sincronización de Productos completada: {procesados:N0} registros en {_currentStatus.UltimaDuracionSegundos}s.";

                return transaccion;
            }
            finally
            {
                _currentStatus.IsRunning = false;
                _syncLock.Release();
            }
        }

        private async Task<(int Procesados, int Errores)> ProcesarLoteProductosMaestrosAsync(List<int> idsProd, CancellationToken ct)
        {
            int procesados = 0, errores = 0;
            var inClause = string.Join(",", idsProd);

            using var connAntiguo = new SqlConnection(_connectionStringAntiguo);
            await connAntiguo.OpenAsync(ct);

            var sql = $@"
                SELECT 
                    p.PKID AS ID_Producto,
                    LTRIM(RTRIM(ISNULL(ps.Codigo, ''))) AS CodigoProducto,
                    LTRIM(RTRIM(ISNULL(ps.Descripcion, ''))) AS NombreProducto,
                    CAST(ISNULL(ps.Activo, 0) AS bit) AS EstadoProducto,
                    ISNULL(p.IDMarca, 0) AS IDMarca,
                    LTRIM(RTRIM(ISNULL(m.Codigo, ''))) AS CodigoMarca,
                    LTRIM(RTRIM(ISNULL(m.Descripcion, ''))) AS NombreMarca,
                    CAST(ISNULL(m.Activo, 1) AS bit) AS EstadoMarca,
                    ISNULL(ps.IDClaseProductoServicio, 0) AS IDLinea,
                    LTRIM(RTRIM(ISNULL(cps.CodigoClase, ''))) AS CodigoLinea,
                    LTRIM(RTRIM(ISNULL(cps.Descripcion, ''))) AS NombreLinea,
                    CAST(ISNULL(cps.Activo, 1) AS bit) AS EstadoLinea,
                    ISNULL(p.IDProveedor, 0) AS IDProveedor,
                    LTRIM(RTRIM(ISNULL(pp.Codigo, ''))) AS CodigoProveedor,
                    LTRIM(RTRIM(ISNULL(pp.Nombre, ''))) AS RazonSocialProveedor,
                    LTRIM(RTRIM(ISNULL(pp.DocIdentidad, '0'))) AS RUCProveedor,
                    CAST(ISNULL(pp.Activo, 1) AS bit) AS EstadoProveedor,
                    ISNULL(subp.PKID, 0) AS IDSubProveedor,
                    ISNULL(subp.Descripcion, '') AS NombreSubProveedor
                FROM dbo.Producto p WITH (NOLOCK)
                INNER JOIN dbo.ProductoServicio ps WITH (NOLOCK) ON ps.PKID = p.PKID
                LEFT JOIN dbo.Marca m WITH (NOLOCK) ON m.PKID = p.IDMarca
                LEFT JOIN dbo.ClaseProductoServicio cps WITH (NOLOCK) ON cps.PKID = ps.IDClaseProductoServicio
                LEFT JOIN dbo.Proveedor prv WITH (NOLOCK) ON prv.PKID = p.IDProveedor
                LEFT JOIN dbo.Persona pp WITH (NOLOCK) ON pp.PKID = prv.PKID
                OUTER APPLY (
                    SELECT TOP 1 c.PKID, LTRIM(RTRIM(c.Descripcion)) AS Descripcion
                    FROM dbo.Caracteristica c WITH (NOLOCK)
                    INNER JOIN dbo.TipoCaracteristica tc WITH (NOLOCK) ON tc.PKID = c.IDTipoCaracteristica
                    WHERE tc.Descripcion = 'SubProveedor'
                      AND c.PKID > 0
                      AND c.PKID IN (
                          p.IDCaracteristica1, p.IDCaracteristica2, p.IDCaracteristica3, p.IDCaracteristica4, p.IDCaracteristica5,
                          p.IDCaracteristica6, p.IDCaracteristica7, p.IDCaracteristica8, p.IDCaracteristica9, p.IDCaracteristica10
                      )
                ) subp
                WHERE p.PKID IN ({inClause});";

            using var cmdRead = new SqlCommand(sql, connAntiguo) { CommandTimeout = 120 };
            using var r = await cmdRead.ExecuteReaderAsync(ct);

            using var connForce = new SqlConnection(_connectionStringForce);
            await connForce.OpenAsync(ct);

            while (await r.ReadAsync(ct))
            {
                try
                {
                    using var cmd = new SqlCommand("dbo.usp_Sync_UpsertProductoMaestro", connForce)
                    {
                        CommandType = CommandType.StoredProcedure,
                        CommandTimeout = 60
                    };
                    cmd.Parameters.AddWithValue("@ID_Producto_Origen", r.GetInt32(0));
                    cmd.Parameters.AddWithValue("@CodigoProducto", r.GetString(1));
                    cmd.Parameters.AddWithValue("@NombreProducto", r.GetString(2));
                    cmd.Parameters.AddWithValue("@DescripcionProducto", r.GetString(2));
                    cmd.Parameters.AddWithValue("@EstadoProducto", r.GetBoolean(3));
                    cmd.Parameters.AddWithValue("@ID_Marca_Origen", r.GetInt32(4));
                    cmd.Parameters.AddWithValue("@CodigoMarca", r.GetString(5));
                    cmd.Parameters.AddWithValue("@NombreMarca", r.GetString(6));
                    cmd.Parameters.AddWithValue("@EstadoMarca", r.GetBoolean(7));
                    cmd.Parameters.AddWithValue("@ID_Linea_Origen", r.GetInt32(8));
                    cmd.Parameters.AddWithValue("@CodigoLinea", r.GetString(9));
                    cmd.Parameters.AddWithValue("@NombreLinea", r.GetString(10));
                    cmd.Parameters.AddWithValue("@EstadoLinea", r.GetBoolean(11));
                    cmd.Parameters.AddWithValue("@ID_Proveedor_Origen", r.GetInt32(12));
                    cmd.Parameters.AddWithValue("@CodigoProveedor", r.GetString(13));
                    cmd.Parameters.AddWithValue("@RazonSocialProveedor", r.GetString(14));
                    cmd.Parameters.AddWithValue("@RUCProveedor", r.GetString(15));
                    cmd.Parameters.AddWithValue("@EstadoProveedor", r.GetBoolean(16));
                    cmd.Parameters.AddWithValue("@ID_SubProveedor_Origen", r.GetInt32(17));
                    cmd.Parameters.AddWithValue("@NombreSubProveedor", r.GetString(18));
                    var pOut = new SqlParameter("@IDProductoGenerado", SqlDbType.Int) { Direction = ParameterDirection.Output };
                    cmd.Parameters.Add(pOut);

                    await cmd.ExecuteNonQueryAsync(ct);
                    procesados++;
                }
                catch (Exception ex)
                {
                    errores++;
                    _logger.LogWarning(ex, "Error sincronizando producto ID {Id}", r.GetInt32(0));
                }
            }

            return (procesados, errores);
        }

        private async Task<(int Procesados, int Errores)> ProcesarLoteUnidadesAsync(List<int> idsUnid, CancellationToken ct)
        {
            int procesados = 0, errores = 0;
            var inClause = string.Join(",", idsUnid);

            using var connAntiguo = new SqlConnection(_connectionStringAntiguo);
            await connAntiguo.OpenAsync(ct);

            var sql = $@"
                SELECT 
                    u.PKID AS ID_Unidad,
                    u.IDProductoServicio AS ID_Producto,
                    LTRIM(RTRIM(ISNULL(u.Codigo, ''))) AS CodigoUnidad,
                    LTRIM(RTRIM(ISNULL(u.Descripcion, ''))) AS NombreUnidad,
                    LTRIM(RTRIM(ISNULL(u.Abreviacion, 'UN'))) AS Abreviacion,
                    CAST(ISNULL(u.Base, 0) AS bit) AS EsBase,
                    CAST(ISNULL(u.Factor, 1) AS decimal(18,6)) AS Factor,
                    CAST(ISNULL(u.Peso, 0) AS decimal(10,4)) AS Peso,
                    CAST(ISNULL(u.Volumen, 0) AS decimal(12,6)) AS Volumen,
                    CAST(ISNULL(u.Largo, 0) AS decimal(10,4)) AS Largo,
                    CAST(ISNULL(u.Ancho, 0) AS decimal(10,4)) AS Ancho,
                    CAST(ISNULL(u.Alto, 0) AS decimal(10,4)) AS Alto,
                    CAST(ISNULL(u.Activo, 0) AS bit) AS Activo
                FROM dbo.Unidad u WITH (NOLOCK)
                WHERE u.PKID IN ({inClause});";

            using var cmdRead = new SqlCommand(sql, connAntiguo) { CommandTimeout = 120 };
            using var r = await cmdRead.ExecuteReaderAsync(ct);

            using var connForce = new SqlConnection(_connectionStringForce);
            await connForce.OpenAsync(ct);

            while (await r.ReadAsync(ct))
            {
                try
                {
                    using var cmd = new SqlCommand("dbo.usp_Sync_UpsertProductoUnidad", connForce)
                    {
                        CommandType = CommandType.StoredProcedure,
                        CommandTimeout = 60
                    };
                    cmd.Parameters.AddWithValue("@ID_Unidad_Origen", r.GetInt32(0));
                    cmd.Parameters.AddWithValue("@ID_Producto_Origen", r.GetInt32(1));
                    cmd.Parameters.AddWithValue("@CodigoUnidad", r.GetString(2));
                    cmd.Parameters.AddWithValue("@NombreUnidad", r.GetString(3));
                    cmd.Parameters.AddWithValue("@Abreviacion", r.GetString(4));
                    cmd.Parameters.AddWithValue("@EsUnidadBase", r.GetBoolean(5));
                    cmd.Parameters.AddWithValue("@Factor", r.GetDecimal(6));
                    cmd.Parameters.AddWithValue("@Peso", r.GetDecimal(7));
                    cmd.Parameters.AddWithValue("@Volumen", r.GetDecimal(8));
                    cmd.Parameters.AddWithValue("@Largo", r.GetDecimal(9));
                    cmd.Parameters.AddWithValue("@Ancho", r.GetDecimal(10));
                    cmd.Parameters.AddWithValue("@Alto", r.GetDecimal(11));
                    cmd.Parameters.AddWithValue("@Estado", r.GetBoolean(12));

                    await cmd.ExecuteNonQueryAsync(ct);
                    procesados++;
                }
                catch (Exception ex)
                {
                    errores++;
                    _logger.LogWarning(ex, "Error sincronizando unidad ID {Id}", r.GetInt32(0));
                }
            }

            return (procesados, errores);
        }

        private async Task<(int Procesados, int Errores)> ProcesarLoteCatalogosAsync(List<int> idsItemsPort, CancellationToken ct)
        {
            int procesados = 0, errores = 0;
            var inClause = string.Join(",", idsItemsPort);

            using var connAntiguo = new SqlConnection(_connectionStringAntiguo);
            await connAntiguo.OpenAsync(ct);

            var sql = $@"
                SELECT 
                    ip.IDPortafolioProducto,
                    LTRIM(RTRIM(ISNULL(pp.Codigo, ''))) AS CodigoPortafolio,
                    LTRIM(RTRIM(ISNULL(pp.Nombre, ''))) AS NombrePortafolio,
                    LTRIM(RTRIM(ISNULL(pp.Observacion, ''))) AS ObservacionPortafolio,
                    CAST(ISNULL(pp.Activo, 1) AS bit) AS EstadoPortafolio,
                    ip.IDProducto
                FROM dbo.ItemPortafolioProductoProducto ip WITH (NOLOCK)
                INNER JOIN dbo.PortafolioProducto pp WITH (NOLOCK) ON pp.PKID = ip.IDPortafolioProducto
                WHERE ip.PKID IN ({inClause});";

            using var cmdRead = new SqlCommand(sql, connAntiguo) { CommandTimeout = 120 };
            using var r = await cmdRead.ExecuteReaderAsync(ct);

            using var connForce = new SqlConnection(_connectionStringForce);
            await connForce.OpenAsync(ct);

            while (await r.ReadAsync(ct))
            {
                try
                {
                    using var cmd = new SqlCommand("dbo.usp_Sync_UpsertCatalogoProducto", connForce)
                    {
                        CommandType = CommandType.StoredProcedure,
                        CommandTimeout = 60
                    };
                    cmd.Parameters.AddWithValue("@ID_Portafolio_Origen", r.GetInt32(0));
                    cmd.Parameters.AddWithValue("@CodigoPortafolio", r.GetString(1));
                    cmd.Parameters.AddWithValue("@NombrePortafolio", r.GetString(2));
                    cmd.Parameters.AddWithValue("@ObservacionPortafolio", r.GetString(3));
                    cmd.Parameters.AddWithValue("@EstadoPortafolio", r.GetBoolean(4));
                    cmd.Parameters.AddWithValue("@ID_Producto_Origen", r.GetInt32(5));
                    cmd.Parameters.AddWithValue("@EstadoAsignacion", true);

                    await cmd.ExecuteNonQueryAsync(ct);
                    procesados++;
                }
                catch (Exception ex)
                {
                    errores++;
                    _logger.LogWarning(ex, "Error sincronizando catálogo producto");
                }
            }

            return (procesados, errores);
        }

        private async Task<(int Procesados, int Errores)> ProcesarLotePreciosEscalasAsync(List<int> idsEscalas, CancellationToken ct)
        {
            int procesados = 0, errores = 0;
            var inClause = string.Join(",", idsEscalas);

            using var connAntiguo = new SqlConnection(_connectionStringAntiguo);
            await connAntiguo.OpenAsync(ct);

            var sql = $@"
                SELECT 
                    e.PKID AS ID_Escala,
                    uilp.PKID AS ID_UnidadItemLista,
                    ilp.PKID AS ID_ItemLista,
                    lp.PKID AS ID_ListaPrecio,
                    LTRIM(RTRIM(ISNULL(lp.Descripcion, ''))) AS NombreListaPrecio,
                    CAST(ISNULL(lp.Activo, 1) AS bit) AS EstadoListaPrecio,
                    ilp.IDProducto AS ID_Producto,
                    uilp.IDUnidad AS ID_Unidad,
                    CAST(ISNULL(ilp.Desactivado, 0) AS bit) AS DesactivadoItem,
                    CAST(ISNULL(ilp.Credito, 1) AS bit) AS Credito,
                    CAST(ISNULL(ilp.Contado, 1) AS bit) AS Contado,
                    CAST(ISNULL(uilp.Venta, 1) AS bit) AS PermiteVentaUnidad,
                    CAST(ISNULL(e.Desde, 0) AS decimal(18,4)) AS Desde,
                    CAST(ISNULL(e.Hasta, 999999) AS decimal(18,4)) AS Hasta,
                    CAST(ISNULL(e.Precio, 0) AS decimal(18,6)) AS Precio,
                    CAST(ISNULL(e.PrecioIncImpto, 0) AS decimal(18,6)) AS PrecioIncImpto,
                    CAST(ISNULL(e.UtilidadMonto, 0) AS decimal(18,6)) AS UtilidadMonto,
                    CAST(ISNULL(e.UtilidadPorcentual, 0) AS decimal(18,6)) AS UtilidadPorcentual,
                    CAST(ISNULL(e.EsVigente, 1) AS bit) AS EsVigente
                FROM dbo.Escala e WITH (NOLOCK)
                INNER JOIN dbo.UnidadItemListaPrecios uilp WITH (NOLOCK) ON uilp.PKID = e.IDUnidadItemListaPrecios
                INNER JOIN dbo.ItemListaPrecios ilp WITH (NOLOCK) ON ilp.PKID = uilp.IDItemListaPrecios
                INNER JOIN dbo.ListaPrecios lp WITH (NOLOCK) ON lp.PKID = ilp.IDListaPrecios
                WHERE e.PKID IN ({inClause});";

            using var cmdRead = new SqlCommand(sql, connAntiguo) { CommandTimeout = 120 };
            using var r = await cmdRead.ExecuteReaderAsync(ct);

            using var connForce = new SqlConnection(_connectionStringForce);
            await connForce.OpenAsync(ct);

            while (await r.ReadAsync(ct))
            {
                try
                {
                    using var cmd = new SqlCommand("dbo.usp_Sync_UpsertPrecioEscala", connForce)
                    {
                        CommandType = CommandType.StoredProcedure,
                        CommandTimeout = 60
                    };
                    cmd.Parameters.AddWithValue("@ID_Escala_Origen", r.GetInt32(0));
                    cmd.Parameters.AddWithValue("@ID_UnidadItemLista_Origen", r.GetInt32(1));
                    cmd.Parameters.AddWithValue("@ID_ItemLista_Origen", r.GetInt32(2));
                    cmd.Parameters.AddWithValue("@ID_ListaPrecio_Origen", r.GetInt32(3));
                    cmd.Parameters.AddWithValue("@NombreListaPrecio", r.GetString(4));
                    cmd.Parameters.AddWithValue("@EstadoListaPrecio", r.GetBoolean(5));
                    cmd.Parameters.AddWithValue("@ID_Producto_Origen", r.GetInt32(6));
                    cmd.Parameters.AddWithValue("@ID_Unidad_Origen", r.GetInt32(7));
                    cmd.Parameters.AddWithValue("@DesactivadoItem", r.GetBoolean(8));
                    cmd.Parameters.AddWithValue("@Credito", r.GetBoolean(9));
                    cmd.Parameters.AddWithValue("@Contado", r.GetBoolean(10));
                    cmd.Parameters.AddWithValue("@PermiteVentaUnidad", r.GetBoolean(11));
                    cmd.Parameters.AddWithValue("@Desde", r.GetDecimal(12));
                    cmd.Parameters.AddWithValue("@Hasta", r.GetDecimal(13));
                    cmd.Parameters.AddWithValue("@Precio", r.GetDecimal(14));
                    cmd.Parameters.AddWithValue("@PrecioIncImpto", r.GetDecimal(15));
                    cmd.Parameters.AddWithValue("@UtilidadMonto", r.GetDecimal(16));
                    cmd.Parameters.AddWithValue("@UtilidadPorcentual", r.GetDecimal(17));
                    cmd.Parameters.AddWithValue("@EsVigente", r.GetBoolean(18));

                    await cmd.ExecuteNonQueryAsync(ct);
                    procesados++;
                }
                catch (Exception ex)
                {
                    errores++;
                    _logger.LogWarning(ex, "Error sincronizando escala precio ID {Id}", r.GetInt32(0));
                }
            }

            return (procesados, errores);
        }
    }
}
