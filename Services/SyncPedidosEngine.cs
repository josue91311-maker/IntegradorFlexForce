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
    public class SyncPedidosEngine
    {
        private readonly string _connectionStringForce;
        private readonly string _connectionStringAntiguo;
        private readonly ILogger<SyncPedidosEngine> _logger;
        private static readonly SemaphoreSlim _execLock = new(1, 1);

        private readonly PedidoStatusDto _status = new();
        private List<PedidoAlineadoDto> _ultimoAnalisis = new();

        public SyncPedidosEngine(IConfiguration config, ILogger<SyncPedidosEngine> logger)
        {
            _connectionStringForce = config.GetConnectionString("ForceGVD") ?? "";
            _connectionStringAntiguo = config.GetConnectionString("ERPAntiguo") ?? "";
            _logger = logger;
        }

        public PedidoStatusDto GetStatus() => _status;

        // =========================================================================
        // 1. OBTENER LISTA DE VENDEDORES PARA EL SELECTOR (ERP ANTIGUO + FORCE)
        // =========================================================================
        public async Task<List<VendedorPedidoOptionDto>> ObtenerVendedoresAsync(CancellationToken ct = default)
        {
            var lista = new List<VendedorPedidoOptionDto>();

            // Cargar vendedores y tipos de pedido desde Force
            var vendForceByExtra = new Dictionary<int, (int IDForce, string Codigo, string Nombre)>();
            var tipoPedidoByVend = new Dictionary<int, (int IDTipo, string Serie)>();

            using (var connForce = new SqlConnection(_connectionStringForce))
            {
                await connForce.OpenAsync(ct);

                const string sqlVendForce = @"
                    SELECT ID, IDExtra, LTRIM(RTRIM(ISNULL(Codigo, ''))), LTRIM(RTRIM(ISNULL(NombreCompleto, '')))
                    FROM dbo.vnd_Vendedor WITH (NOLOCK)
                    WHERE Eliminado = 0 AND IDExtra IS NOT NULL;";
                using (var cmd = new SqlCommand(sqlVendForce, connForce))
                using (var r = await cmd.ExecuteReaderAsync(ct))
                {
                    while (await r.ReadAsync(ct))
                    {
                        vendForceByExtra[r.GetInt32(1)] = (r.GetInt32(0), r.GetString(2), r.GetString(3));
                    }
                }

                const string sqlTipos = @"
                    SELECT ID, LTRIM(RTRIM(ISNULL(Serie, ''))), IDVendedor
                    FROM dbo.TipoCpPedido WITH (NOLOCK)
                    WHERE ISNULL(Activo, 1) = 1 AND IDVendedor IS NOT NULL;";
                using (var cmd = new SqlCommand(sqlTipos, connForce))
                using (var r = await cmd.ExecuteReaderAsync(ct))
                {
                    while (await r.ReadAsync(ct))
                    {
                        int idVendF = r.GetInt32(2);
                        if (!tipoPedidoByVend.ContainsKey(idVendF))
                            tipoPedidoByVend[idVendF] = (r.GetInt32(0), r.GetString(1));
                    }
                }
            }

            using (var connAntiguo = new SqlConnection(_connectionStringAntiguo))
            {
                await connAntiguo.OpenAsync(ct);

                const string sqlVendAntiguo = @"
                    SELECT 
                        v.PKID,
                        LTRIM(RTRIM(ISNULL(p.Codigo, ''))) AS Codigo,
                        LTRIM(RTRIM(ISNULL(p.Nombre, ''))) AS Nombre
                    FROM dbo.Vendedor v WITH (NOLOCK)
                    INNER JOIN dbo.Persona p WITH (NOLOCK) ON p.PKID = v.PKID
                    WHERE p.Activo = 1
                    ORDER BY p.Nombre;";

                using var cmd = new SqlCommand(sqlVendAntiguo, connAntiguo);
                using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    int pkid = r.GetInt32(0);
                    string cod = r.GetString(1);
                    string nom = r.GetString(2);

                    int? idVendForce = null;
                    int? idTipoCp = null;
                    string serie = "";

                    if (vendForceByExtra.TryGetValue(pkid, out var vf))
                    {
                        idVendForce = vf.IDForce;
                        if (tipoPedidoByVend.TryGetValue(vf.IDForce, out var tp))
                        {
                            idTipoCp = tp.IDTipo;
                            serie = tp.Serie;
                        }
                    }

                    lista.Add(new VendedorPedidoOptionDto
                    {
                        IDOrigen = pkid,
                        IDForce = idVendForce,
                        Codigo = cod,
                        Nombre = nom,
                        IDTipoCpPedidoForce = idTipoCp,
                        SeriePedidoForce = serie
                    });
                }
            }

            return lista;
        }

        // =========================================================================
        // 2. CONSULTAR PEDIDOS DEL RANGO Y VALIDAR ALINEACIÓN CONTRA MAESTROS FORCE
        // =========================================================================
        public async Task<PedidoAnalisisResponseDto> ConsultarYAlinearPedidosAsync(PedidoFiltroRequestDto filtro, CancellationToken ct = default)
        {
            var sw = Stopwatch.StartNew();
            var response = new PedidoAnalisisResponseDto();
            var pedidos = new List<PedidoAlineadoDto>();
            var itemsPorPedido = new Dictionary<int, List<PedidoItemAlineadoDto>>();

            DateTime fechaDesde = filtro.FechaDesde.Date;
            DateTime fechaHastaExclusivo = filtro.FechaHasta.Date.AddDays(1);

            // -----------------------------------------------------------------
            // A. EXTRAER CABECERAS E ÍTEMS DESDE ERP ANTIGUO (digalimenta41)
            // -----------------------------------------------------------------
            using (var connAntiguo = new SqlConnection(_connectionStringAntiguo))
            {
                await connAntiguo.OpenAsync(ct);

                string filtroVendedores = (filtro.VendedoresOrigenIds != null && filtro.VendedoresOrigenIds.Count > 0)
                    ? $"AND cp.IDResponsable IN ({string.Join(",", filtro.VendedoresOrigenIds.Distinct())})"
                    : "";

                string filtroAnulados = filtro.IncluirAnulados ? "" : "AND ISNULL(cp.Anulado, 0) = 0";
                string filtroFacturados = filtro.IncluirFacturados ? "" : "AND ISNULL(ped.EstadoFacturacion, 'PENDIENTE') <> 'TOTAL'";

                string sqlCabeceras = $@"
                    SELECT 
                        cp.PKID                                          AS IDPedidoOrigen,
                        LTRIM(RTRIM(ISNULL(cp.NumCp, '')))               AS NumPedidoOrigen,
                        ISNULL(cp.FechaEmision, GETDATE())               AS FechaEmision,
                        ped.FechaEntrega,
                        ISNULL(cp.IDPersona, 0)                          AS IDClienteOrigen,
                        LTRIM(RTRIM(ISNULL(pCli.Codigo, '')))            AS CodigoCliente,
                        LTRIM(RTRIM(ISNULL(pCli.Nombre, '')))            AS NombreCliente,
                        ISNULL(cp.IDResponsable, 0)                      AS IDVendedorOrigen,
                        LTRIM(RTRIM(ISNULL(pVend.Codigo, '')))           AS CodigoVendedor,
                        LTRIM(RTRIM(ISNULL(pVend.Nombre, '')))           AS NombreVendedor,
                        ISNULL(ci.IDAlmacen, 0)                          AS IDAlmacenOrigen,
                        LTRIM(RTRIM(ISNULL(alm.Nombre, '')))             AS AlmacenNombre,
                        ISNULL(cp.IDSucursal, 0)                         AS IDSucursalOrigen,
                        ISNULL(cd.IDPortafolioProducto, 0)               AS IDCatalogoOrigen,
                        LTRIM(RTRIM(ISNULL(port.Nombre, '')))            AS CatalogoNombre,
                        CAST(ISNULL(ped.Credito, 0) AS bit)              AS TieneCredito,
                        CAST(ISNULL(cp.SubTotal, 0) AS decimal(18,6))    AS SubTotal,
                        CAST(ISNULL(cp.Total, 0) AS decimal(18,6))       AS Total,
                        CAST(ISNULL(cp.TieneImpuesto, 1) AS bit)         AS TieneImpuesto,
                        CAST(ISNULL(cp.ValorAfecto, 0) AS decimal(18,6)) AS ValorAfecto,
                        CAST(ISNULL(cp.ValorInafecto, 0) AS decimal(18,6)) AS ValorInafecto,
                        CAST(ISNULL(cp.MontoPercepcion, 0) AS decimal(18,6)) AS MontoPercepcion,
                        CAST(ISNULL(cp.Anulado, 0) AS bit)               AS AnuladoOrigen,
                        UPPER(LTRIM(RTRIM(ISNULL(ped.EstadoFacturacion, 'PENDIENTE')))) AS EstadoFacturacion,
                        CAST(ISNULL(cp.ValorDescuento, 0) AS decimal(18,6)) AS ValorDescuento
                    FROM dbo.Cp cp WITH (NOLOCK)
                    INNER JOIN dbo.Pedido ped WITH (NOLOCK) ON ped.PKID = cp.PKID
                    LEFT JOIN dbo.CpInventario ci WITH (NOLOCK) ON ci.IDCp = cp.PKID
                    LEFT JOIN dbo.CpDistribucion cd WITH (NOLOCK) ON cd.PKID = cp.PKID
                    LEFT JOIN dbo.Persona pCli WITH (NOLOCK) ON pCli.PKID = cp.IDPersona
                    LEFT JOIN dbo.Persona pVend WITH (NOLOCK) ON pVend.PKID = cp.IDResponsable
                    LEFT JOIN dbo.Almacen alm WITH (NOLOCK) ON alm.PKID = ci.IDAlmacen
                    LEFT JOIN dbo.PortafolioProducto port WITH (NOLOCK) ON port.PKID = cd.IDPortafolioProducto
                    WHERE cp.FechaEmision >= @Desde 
                      AND cp.FechaEmision < @Hasta
                      {filtroVendedores}
                      {filtroAnulados}
                      {filtroFacturados}
                    ORDER BY cp.FechaEmision DESC, cp.PKID DESC;";

                using (var cmd = new SqlCommand(sqlCabeceras, connAntiguo) { CommandTimeout = 120 })
                {
                    cmd.Parameters.AddWithValue("@Desde", fechaDesde);
                    cmd.Parameters.AddWithValue("@Hasta", fechaHastaExclusivo);

                    using var r = await cmd.ExecuteReaderAsync(ct);
                    while (await r.ReadAsync(ct))
                    {
                        bool anulado = r.GetBoolean(22);
                        string estFact = r.GetString(23);
                        string estadoForce;
                        if (anulado)
                            estadoForce = "ANULADO";
                        else if (estFact == "TOTAL")
                            estadoForce = "FACTURADO";
                        else
                            estadoForce = "PENDIENTE"; // PENDIENTE y PARCIAL entran como PENDIENTE por restricción CK_Pedido_Estado

                        bool tieneCred = r.GetBoolean(15);
                        decimal sub = Math.Max(0, r.GetDecimal(16));
                        decimal tot = Math.Max(0, r.GetDecimal(17));
                        decimal valAf = Math.Max(0, r.GetDecimal(19));
                        decimal valInaf = Math.Max(0, r.GetDecimal(20));
                        decimal totalImpto = Math.Max(0, tot - sub);

                        var dto = new PedidoAlineadoDto
                        {
                            IDPedidoOrigen = r.GetInt32(0),
                            NumPedidoOrigen = r.GetString(1),
                            FechaEmision = r.GetDateTime(2),
                            FechaEntrega = r.IsDBNull(3) ? null : r.GetDateTime(3),
                            IDClienteOrigen = r.GetInt32(4),
                            CodigoCliente = r.GetString(5),
                            NombreCliente = r.GetString(6),
                            IDVendedorOrigen = r.GetInt32(7),
                            CodigoVendedor = r.GetString(8),
                            NombreVendedor = r.GetString(9),
                            IDAlmacenOrigen = r.GetInt32(10),
                            AlmacenNombre = r.GetString(11),
                            IDSucursalOrigen = r.GetInt32(12),
                            IDCatalogoOrigen = r.GetInt32(13),
                            CatalogoNombre = r.GetString(14),
                            TieneCredito = tieneCred,
                            SubTotal = sub,
                            Total = tot,
                            TotalCredito = tieneCred ? tot : 0m,
                            TotalContado = tieneCred ? 0m : tot,
                            TieneImpuesto = r.GetBoolean(18),
                            TotalImpuesto = totalImpto,
                            ValorAfecto = valAf,
                            ValorInafecto = valInaf,
                            TotalPercepcion = Math.Max(0, r.GetDecimal(21)),
                            AnuladoOrigen = anulado,
                            EstadoFacturacionOrigen = anulado ? "ANULADO" : estFact,
                            EstadoForceMapeado = estadoForce,
                            TotalDescuento = Math.Max(0, r.GetDecimal(24))
                        };

                        pedidos.Add(dto);
                        itemsPorPedido[dto.IDPedidoOrigen] = new List<PedidoItemAlineadoDto>();
                    }
                }

                // Leer todos los ítems de los pedidos extraídos en una sola consulta
                if (pedidos.Count > 0)
                {
                    var idsList = string.Join(",", pedidos.Select(p => p.IDPedidoOrigen));
                    string sqlItems = $@"
                        SELECT 
                            ic.IDCp                                            AS IDPedidoOrigen,
                            ic.PKID                                            AS IDItemOrigen,
                            ip.IDProducto                                      AS IDProductoOrigen,
                            LTRIM(RTRIM(ISNULL(ps.Codigo, '')))                AS CodigoProducto,
                            LTRIM(RTRIM(ISNULL(ps.Descripcion, ic.Descripcion))) AS NombreProducto,
                            ip.IDUnidad                                        AS IDUnidadOrigen,
                            LTRIM(RTRIM(ISNULL(u.Abreviacion, ISNULL(u.Descripcion, 'UN')))) AS UnidadNombre,
                            CAST(ISNULL(ic.Cantidad, 0) AS decimal(18,4))      AS Cantidad,
                            CAST(CASE WHEN ISNULL(ip.Factor, 1) <= 0 THEN 1 ELSE ip.Factor END AS decimal(18,4)) AS Factor,
                            CAST(ISNULL(ic.ValorUnitario, 0) AS decimal(18,6)) AS ValorUnitario,
                            CAST(ISNULL(ic.SubTotal, 0) AS decimal(18,6))      AS SubTotal,
                            CAST(ISNULL(ic.ValorVenta, 0) AS decimal(18,6))    AS ValorVenta,
                            CAST(ISNULL(ic.Total, 0) AS decimal(18,6))         AS Total,
                            CAST(ISNULL(ic.PorcentajeImpto, 0) AS decimal(18,4)) AS PorcentajeImpto,
                            CAST(ISNULL(ic.ValorAfecto, 0) AS decimal(18,6))   AS ValorAfecto,
                            CAST(ISNULL(ic.ValorInafecto, 0) AS decimal(18,6)) AS ValorInafecto,
                            CAST(ISNULL(ic.MontoPercepcion, 0) AS decimal(18,6)) AS MontoPercepcion,
                            CAST(ISNULL(iped.Credito, 0) AS bit)               AS Credito,
                            CAST(ISNULL(ic.ValorDescuento, 0) AS decimal(18,6)) AS ValorDescuento,
                            CAST(ISNULL(ic.Bonificacion, 0) AS bit)            AS EsBonificacion
                        FROM dbo.ItemCp ic WITH (NOLOCK)
                        INNER JOIN dbo.ItemProducto ip WITH (NOLOCK) ON ip.PKID = ic.PKID
                        LEFT JOIN dbo.ItemPedido iped WITH (NOLOCK) ON iped.PKID = ic.PKID
                        LEFT JOIN dbo.ProductoServicio ps WITH (NOLOCK) ON ps.PKID = ip.IDProducto
                        LEFT JOIN dbo.Unidad u WITH (NOLOCK) ON u.PKID = ip.IDUnidad
                        WHERE ic.IDCp IN ({idsList})
                          AND ISNULL(ic.Cantidad, 0) > 0
                        ORDER BY ic.IDCp, ic.PKID;";

                    using var cmdItems = new SqlCommand(sqlItems, connAntiguo) { CommandTimeout = 120 };
                    using var rIt = await cmdItems.ExecuteReaderAsync(ct);
                    while (await rIt.ReadAsync(ct))
                    {
                        int idPed = rIt.GetInt32(0);
                        decimal cant = rIt.GetDecimal(7);
                        decimal factor = rIt.GetDecimal(8);
                        decimal precioUnit = Math.Max(0, rIt.GetDecimal(9));
                        decimal sub = Math.Max(0, rIt.GetDecimal(10));
                        decimal valVenta = Math.Max(0, rIt.GetDecimal(11));
                        decimal tot = Math.Max(0, rIt.GetDecimal(12));
                        decimal pctImpto = Math.Max(0, rIt.GetDecimal(13));
                        decimal precioConIgv = pctImpto > 0 ? Math.Round(precioUnit * (1m + pctImpto / 100m), 6) : precioUnit;

                        var itemDto = new PedidoItemAlineadoDto
                        {
                            IDItemOrigen = rIt.GetInt32(1),
                            IDProductoOrigen = rIt.GetInt32(2),
                            CodigoProducto = rIt.GetString(3),
                            NombreProducto = rIt.GetString(4),
                            IDUnidadOrigen = rIt.GetInt32(5),
                            UnidadNombre = rIt.GetString(6),
                            Cantidad = cant,
                            CantidadFactor = factor,
                            PrecioUnitario = precioUnit,
                            PrecioNeto = precioUnit,
                            PrecioConImpuesto = precioConIgv,
                            SubTotal = sub,
                            SubTotalNeto = valVenta,
                            Total = tot,
                            TieneImpuesto = pctImpto > 0,
                            PorcentajeImpuesto = pctImpto,
                            MontoImpuesto = Math.Max(0, tot - valVenta),
                            ValorAfecto = Math.Max(0, rIt.GetDecimal(14)),
                            ValorInafecto = Math.Max(0, rIt.GetDecimal(15)),
                            MontoPercepcion = Math.Max(0, rIt.GetDecimal(16)),
                            Credito = rIt.GetBoolean(17),
                            Descuento = Math.Max(0, rIt.GetDecimal(18)),
                            EsBonificacion = rIt.GetBoolean(19) || tot == 0m
                        };

                        if (itemsPorPedido.TryGetValue(idPed, out var listIt))
                            listIt.Add(itemDto);
                    }

                    // Extraer comprobantes activos y anulados asociados a estos pedidos en digalimenta41
                    // para detectar reemplazos de comprobantes anulados por nuevos comprobantes
                    var dictPedidos = pedidos.ToDictionary(p => p.IDPedidoOrigen);
                    string sqlCpsPedido = $@"
                        SELECT 
                            cpr.IDCpRelacionado AS IDPedidoOrigen,
                            LTRIM(RTRIM(ISNULL(cpv.NumCp, ''))) AS NumCp,
                            CAST(ISNULL(cpv.Anulado, 0) AS bit) AS Anulado
                        FROM dbo.CpRelacionado cpr WITH (NOLOCK)
                        INNER JOIN dbo.Cp cpv WITH (NOLOCK) ON cpv.PKID = cpr.IDCp
                        INNER JOIN dbo.CpVenta v WITH (NOLOCK) ON v.PKID = cpv.PKID
                        WHERE cpr.IDCpRelacionado IN ({idsList})
                        ORDER BY cpv.PKID ASC;";

                    using (var cmdCps = new SqlCommand(sqlCpsPedido, connAntiguo) { CommandTimeout = 120 })
                    using (var rCp = await cmdCps.ExecuteReaderAsync(ct))
                    {
                        while (await rCp.ReadAsync(ct))
                        {
                            int idPedOrig = rCp.GetInt32(0);
                            string numCp = rCp.GetString(1);
                            bool anul = rCp.GetBoolean(2);
                            if (string.IsNullOrEmpty(numCp) || !dictPedidos.TryGetValue(idPedOrig, out var pDto)) continue;

                            if (anul)
                            {
                                if (!pDto.ComprobantesAnuladosOrigen.Contains(numCp, StringComparer.OrdinalIgnoreCase))
                                    pDto.ComprobantesAnuladosOrigen.Add(numCp);
                            }
                            else
                            {
                                if (!pDto.ComprobantesActivosOrigen.Contains(numCp, StringComparer.OrdinalIgnoreCase))
                                    pDto.ComprobantesActivosOrigen.Add(numCp);
                            }
                        }
                    }

                    // Ajustar EstadoForceMapeado según comprobantes vigentes reales si no está anulado
                    foreach (var p in pedidos)
                    {
                        if (!p.AnuladoOrigen && p.ComprobantesActivosOrigen.Count > 0)
                        {
                            p.EstadoForceMapeado = "FACTURADO";
                        }
                    }
                }
            }

            // -----------------------------------------------------------------
            // B. CARGAR DICCIONARIOS MAESTROS DE FORCE (BDForceGVD) PARA ALINEAR
            // -----------------------------------------------------------------
            var cliForceByExtra = new Dictionary<int, (int IDForce, int? IDCategoria)>();
            var vendForceByExtra = new Dictionary<int, int>();
            var tipoPedidoByVend = new Dictionary<int, (int IDTipo, string Serie, int? IDAlm, int? IDSuc)>();
            var tipoPedidoBySerie = new Dictionary<string, (int IDTipo, string Serie, int? IDAlm, int? IDSuc)>(StringComparer.OrdinalIgnoreCase);
            var prodForceByExtra = new Dictionary<int, int>();
            var unidForceByExtra = new Dictionary<int, int>();
            var almForceByExtra = new Dictionary<int, int>();
            var sucForceByExtra = new Dictionary<int, int>();
            var catForceByExtra = new Dictionary<int, int>();
            var listaPrecioByCat = new Dictionary<int, int>();
            var rutaByVend = new Dictionary<int, int>();
            var existentesPorMovilId = new Dictionary<string, (int IDForce, string Estado, decimal Total, int? IDCpVenta)>(StringComparer.OrdinalIgnoreCase);
            var existentesPorNumero = new Dictionary<string, (int IDForce, string Estado, decimal Total, int? IDCpVenta)>(StringComparer.OrdinalIgnoreCase);

            int defaultListaPrecio = 6;
            int defaultMedioPagoContado = 1;
            int defaultMedioPagoCredito = 9;
            int? defaultModalidadCredito = null;

            using (var connForce = new SqlConnection(_connectionStringForce))
            {
                await connForce.OpenAsync(ct);

                // Clientes
                using (var cmd = new SqlCommand("SELECT ID, IDExtra, IDCategoriaCliente FROM dbo.cli_Cliente WITH (NOLOCK) WHERE Eliminado = 0 AND IDExtra IS NOT NULL;", connForce))
                using (var r = await cmd.ExecuteReaderAsync(ct))
                    while (await r.ReadAsync(ct))
                        cliForceByExtra[r.GetInt32(1)] = (r.GetInt32(0), r.IsDBNull(2) ? null : r.GetInt32(2));

                // Vendedores
                using (var cmd = new SqlCommand("SELECT ID, IDExtra FROM dbo.vnd_Vendedor WITH (NOLOCK) WHERE Eliminado = 0 AND IDExtra IS NOT NULL;", connForce))
                using (var r = await cmd.ExecuteReaderAsync(ct))
                    while (await r.ReadAsync(ct))
                        vendForceByExtra[r.GetInt32(1)] = r.GetInt32(0);

                // TipoCpPedido
                using (var cmd = new SqlCommand("SELECT ID, LTRIM(RTRIM(ISNULL(Serie, ''))), IDVendedor, IDAlmacen, IDSucursal FROM dbo.TipoCpPedido WITH (NOLOCK) WHERE ISNULL(Activo, 1) = 1;", connForce))
                using (var r = await cmd.ExecuteReaderAsync(ct))
                {
                    while (await r.ReadAsync(ct))
                    {
                        int idT = r.GetInt32(0);
                        string ser = r.GetString(1);
                        int? idV = r.IsDBNull(2) ? null : r.GetInt32(2);
                        int? idA = r.IsDBNull(3) ? null : r.GetInt32(3);
                        int? idS = r.IsDBNull(4) ? null : r.GetInt32(4);

                        if (!string.IsNullOrEmpty(ser) && !tipoPedidoBySerie.ContainsKey(ser))
                            tipoPedidoBySerie[ser] = (idT, ser, idA, idS);
                        if (idV.HasValue && !tipoPedidoByVend.ContainsKey(idV.Value))
                            tipoPedidoByVend[idV.Value] = (idT, ser, idA, idS);
                    }
                }

                // Productos
                using (var cmd = new SqlCommand("SELECT ID, IDExtra FROM dbo.prd_Producto WITH (NOLOCK) WHERE Eliminado = 0 AND IDExtra IS NOT NULL;", connForce))
                using (var r = await cmd.ExecuteReaderAsync(ct))
                    while (await r.ReadAsync(ct))
                        prodForceByExtra[r.GetInt32(1)] = r.GetInt32(0);

                // ProductoUnidad (ped_PedidoItem.IDUnidad apunta a prd_ProductoUnidad.ID)
                using (var cmd = new SqlCommand("SELECT ID, IDExtra FROM dbo.prd_ProductoUnidad WITH (NOLOCK) WHERE Eliminado = 0 AND IDExtra IS NOT NULL;", connForce))
                using (var r = await cmd.ExecuteReaderAsync(ct))
                    while (await r.ReadAsync(ct))
                        unidForceByExtra[r.GetInt32(1)] = r.GetInt32(0);

                // Almacenes
                using (var cmd = new SqlCommand("SELECT ID, IDExtra FROM dbo.inv_Almacen WITH (NOLOCK) WHERE Eliminado = 0 AND IDExtra IS NOT NULL;", connForce))
                using (var r = await cmd.ExecuteReaderAsync(ct))
                    while (await r.ReadAsync(ct))
                        almForceByExtra[r.GetInt32(1)] = r.GetInt32(0);

                // Sucursales
                using (var cmd = new SqlCommand("SELECT ID, IDExtra FROM dbo.cat_Sucursal WITH (NOLOCK) WHERE IDExtra IS NOT NULL;", connForce))
                using (var r = await cmd.ExecuteReaderAsync(ct))
                    while (await r.ReadAsync(ct))
                        sucForceByExtra[r.GetInt32(1)] = r.GetInt32(0);

                // Catálogos
                using (var cmd = new SqlCommand("SELECT ID, IDExtra FROM dbo.ctg_Catalogo WITH (NOLOCK) WHERE Eliminado = 0 AND IDExtra IS NOT NULL;", connForce))
                using (var r = await cmd.ExecuteReaderAsync(ct))
                    while (await r.ReadAsync(ct))
                        catForceByExtra[r.GetInt32(1)] = r.GetInt32(0);

                // Listas de Precio
                using (var cmd = new SqlCommand("SELECT TOP 100 ID, IDCategoriaCliente FROM dbo.prc_ListaPrecio WITH (NOLOCK) WHERE Eliminado = 0 AND ISNULL(Estado, 1) = 1 ORDER BY Prioridad ASC, ID ASC;", connForce))
                using (var r = await cmd.ExecuteReaderAsync(ct))
                {
                    bool first = true;
                    while (await r.ReadAsync(ct))
                    {
                        int idLP = r.GetInt32(0);
                        if (first) { defaultListaPrecio = idLP; first = false; }
                        if (!r.IsDBNull(1) && !listaPrecioByCat.ContainsKey(r.GetInt32(1)))
                            listaPrecioByCat[r.GetInt32(1)] = idLP;
                    }
                }

                // Rutas por Vendedor
                using (var cmd = new SqlCommand("SELECT ID, IDVendedor FROM dbo.rut_Ruta WITH (NOLOCK) WHERE Eliminado = 0 AND ISNULL(Estado, 1) = 1;", connForce))
                using (var r = await cmd.ExecuteReaderAsync(ct))
                    while (await r.ReadAsync(ct))
                        if (!rutaByVend.ContainsKey(r.GetInt32(1)))
                            rutaByVend[r.GetInt32(1)] = r.GetInt32(0);

                // Medios de pago y modalidad crédito fallback
                using (var cmd = new SqlCommand("SELECT ID, Codigo FROM dbo.ped_MedioPago WITH (NOLOCK) WHERE ISNULL(Estado, 1) = 1;", connForce))
                using (var r = await cmd.ExecuteReaderAsync(ct))
                    while (await r.ReadAsync(ct))
                    {
                        string cod = r.GetString(1).Trim().ToUpperInvariant();
                        if (cod == "EFE") defaultMedioPagoContado = r.GetInt32(0);
                        if (cod == "CRED") defaultMedioPagoCredito = r.GetInt32(0);
                    }

                using (var cmd = new SqlCommand("SELECT TOP 1 ID FROM dbo.ctg_ModalidadCredito WITH (NOLOCK) WHERE ISNULL(Estado, 1) = 1 ORDER BY ID ASC;", connForce))
                {
                    var resMod = await cmd.ExecuteScalarAsync(ct);
                    if (resMod != null && resMod != DBNull.Value)
                        defaultModalidadCredito = Convert.ToInt32(resMod);
                }

                // Pedidos ya existentes en Force (idempotencia por PedidoMovilID o Numero y su estado actual)
                const string sqlForcePed = @"
                    SELECT 
                        ID, 
                        LTRIM(RTRIM(ISNULL(PedidoMovilID, ''))), 
                        LTRIM(RTRIM(ISNULL(Numero, ''))),
                        UPPER(LTRIM(RTRIM(ISNULL(Estado, 'PENDIENTE')))),
                        CAST(ISNULL(Total, 0) AS decimal(18,6)),
                        IDCpVenta
                    FROM dbo.ped_Pedido WITH (NOLOCK) 
                    WHERE Eliminado = 0;";
                using (var cmd = new SqlCommand(sqlForcePed, connForce))
                using (var r = await cmd.ExecuteReaderAsync(ct))
                {
                    while (await r.ReadAsync(ct))
                    {
                        int idF = r.GetInt32(0);
                        string mov = r.GetString(1);
                        string num = r.GetString(2);
                        string est = r.GetString(3);
                        decimal tot = r.GetDecimal(4);
                        int? idCpV = r.IsDBNull(5) ? null : r.GetInt32(5);

                        var info = (idF, est, tot, idCpV);
                        if (!string.IsNullOrEmpty(mov)) existentesPorMovilId[mov] = info;
                        if (!string.IsNullOrEmpty(num)) existentesPorNumero[num] = info;
                    }
                }
            }

            // -----------------------------------------------------------------
            // C. ALINEAR CADA PEDIDO Y SUS ÍTEMS Y GENERAR DIAGNÓSTICO
            // -----------------------------------------------------------------
            foreach (var p in pedidos)
            {
                p.Items = itemsPorPedido.TryGetValue(p.IDPedidoOrigen, out var its) ? its : new List<PedidoItemAlineadoDto>();
                p.CantidadTotal = p.Items.Sum(x => x.Cantidad);

                var erroresMaestros = new List<string>();
                var advertencias = new List<string>();

                // 1. Cliente
                int? idCatCliente = null;
                if (cliForceByExtra.TryGetValue(p.IDClienteOrigen, out var cliMatch))
                {
                    p.IDClienteForce = cliMatch.IDForce;
                    idCatCliente = cliMatch.IDCategoria;
                }
                else
                {
                    erroresMaestros.Add($"Cliente PKID {p.IDClienteOrigen} ({p.CodigoCliente}) no existe en Force");
                }

                // 2. Vendedor
                if (vendForceByExtra.TryGetValue(p.IDVendedorOrigen, out int idVendF))
                {
                    p.IDVendedorForce = idVendF;
                }
                else
                {
                    erroresMaestros.Add($"Vendedor PKID {p.IDVendedorOrigen} ({p.CodigoVendedor}) no existe en Force");
                }

                // 3. TipoCpPedido (Sin IDVega: Por Serie del NumCp o por IDVendedor en TipoCpPedido)
                string serieOrigen = p.NumPedidoOrigen.Contains('-')
                    ? p.NumPedidoOrigen.Split('-')[0].Trim()
                    : "";

                (int IDTipo, string Serie, int? IDAlm, int? IDSuc)? tipoMatch = null;
                if (!string.IsNullOrEmpty(serieOrigen) && tipoPedidoBySerie.TryGetValue(serieOrigen, out var tpSerie))
                {
                    tipoMatch = tpSerie;
                }
                else if (p.IDVendedorForce.HasValue && tipoPedidoByVend.TryGetValue(p.IDVendedorForce.Value, out var tpVend))
                {
                    tipoMatch = tpVend;
                }

                if (tipoMatch.HasValue)
                {
                    p.IDTipoCpPedidoForce = tipoMatch.Value.IDTipo;
                    p.SerieTipoPedidoForce = tipoMatch.Value.Serie;
                }
                else
                {
                    erroresMaestros.Add($"Sin TipoCpPedido activo en Force para Serie '{serieOrigen}' o Vendedor");
                }

                // 4. Almacén y Sucursal (Origen por IDExtra o Fallback del TipoCpPedido)
                if (almForceByExtra.TryGetValue(p.IDAlmacenOrigen, out int idAlmF))
                    p.IDAlmacenForce = idAlmF;
                else if (tipoMatch?.IDAlm != null)
                    p.IDAlmacenForce = tipoMatch.Value.IDAlm;
                else
                    erroresMaestros.Add($"Almacén PKID {p.IDAlmacenOrigen} no homologado en Force");

                if (sucForceByExtra.TryGetValue(p.IDSucursalOrigen, out int idSucF))
                    p.IDSucursalForce = idSucF;
                else if (tipoMatch?.IDSuc != null)
                    p.IDSucursalForce = tipoMatch.Value.IDSuc;
                else
                    erroresMaestros.Add($"Sucursal PKID {p.IDSucursalOrigen} no homologada en Force");

                // 5. Catálogo, ListaPrecio, MedioPago, ModalidadCredito, Ruta
                if (catForceByExtra.TryGetValue(p.IDCatalogoOrigen, out int idCatF))
                    p.IDCatalogoForce = idCatF;
                else if (catForceByExtra.Count > 0)
                {
                    p.IDCatalogoForce = catForceByExtra.Values.First();
                    advertencias.Add("Catálogo origen no hallado, asignado catálogo por defecto");
                }
                else
                    erroresMaestros.Add($"Catálogo PKID {p.IDCatalogoOrigen} no existe en Force");

                if (idCatCliente.HasValue && listaPrecioByCat.TryGetValue(idCatCliente.Value, out int idLP))
                    p.IDListaPrecioForce = idLP;
                else
                    p.IDListaPrecioForce = defaultListaPrecio;

                p.IDMedioPagoForce = p.TieneCredito ? defaultMedioPagoCredito : defaultMedioPagoContado;
                p.IDModalidadCreditoForce = defaultModalidadCredito;

                if (p.IDVendedorForce.HasValue && rutaByVend.TryGetValue(p.IDVendedorForce.Value, out int idRutF))
                    p.IDRutaForce = idRutF;

                // 6. Validar Ítems (Productos y Unidades)
                if (p.Items.Count == 0)
                {
                    erroresMaestros.Add("Pedido sin ítems válidos");
                }
                else
                {
                    int prodFaltantes = 0, unidFaltantes = 0;
                    foreach (var it in p.Items)
                    {
                        var obsIt = new List<string>();
                        if (prodForceByExtra.TryGetValue(it.IDProductoOrigen, out int idPF))
                            it.IDProductoForce = idPF;
                        else
                        {
                            prodFaltantes++;
                            obsIt.Add($"Producto PKID {it.IDProductoOrigen} ({it.CodigoProducto}) sin homologar");
                        }

                        if (unidForceByExtra.TryGetValue(it.IDUnidadOrigen, out int idUF))
                            it.IDUnidadForce = idUF;
                        else
                        {
                            unidFaltantes++;
                            obsIt.Add($"Unidad PKID {it.IDUnidadOrigen} ({it.UnidadNombre}) sin homologar");
                        }

                        it.ObservacionItem = obsIt.Count == 0 ? "✓ Homologado" : string.Join(" | ", obsIt);
                    }

                    if (prodFaltantes > 0)
                        erroresMaestros.Add($"{prodFaltantes} producto(s) no existen en Force");
                    if (unidFaltantes > 0)
                        erroresMaestros.Add($"{unidFaltantes} presentación(es) de unidad no existen en Force");
                }

                // 7. Validar Idempotencia (Si ya existe en Force)
                string movilKey = $"MIG-ERP-{p.IDPedidoOrigen}";
                if (existentesPorMovilId.TryGetValue(movilKey, out var infoMov))
                {
                    p.YaExisteEnForce = true;
                    p.IDPedidoForceExistente = infoMov.IDForce;
                    p.ForceEstadoActual = infoMov.Estado;
                    p.ForceTotalActual = infoMov.Total;
                    p.ForceIDCpVentaActual = infoMov.IDCpVenta;
                }
                else if (!string.IsNullOrEmpty(p.NumPedidoOrigen) && existentesPorNumero.TryGetValue(p.NumPedidoOrigen, out var infoNum))
                {
                    p.YaExisteEnForce = true;
                    p.IDPedidoForceExistente = infoNum.IDForce;
                    p.ForceEstadoActual = infoNum.Estado;
                    p.ForceTotalActual = infoNum.Total;
                    p.ForceIDCpVentaActual = infoNum.IDCpVenta;
                }

                if (p.TieneReemplazoComprobantes)
                {
                    advertencias.Add($"🔄 Comprobante(s) vigente(s): {string.Join(", ", p.ComprobantesActivosOrigen)} (reemplazó a anulado: {string.Join(", ", p.ComprobantesAnuladosOrigen)})");
                }

                // 8. Definir Semáforo, TipoOperacion y Observación Final
                if (p.YaExisteEnForce)
                {
                    if (p.AnuladoOrigen)
                    {
                        if (!string.Equals(p.ForceEstadoActual, "ANULADO", StringComparison.OrdinalIgnoreCase))
                        {
                            p.TipoOperacion = "ANULAR";
                            p.Semaforo = "AMBAR";
                            p.ObservacionValidacion = $"⚠ Pedido ANULADO en ERP Antiguo (En Force está '{p.ForceEstadoActual}'). Listo para anular en Force sin afectar stock.";
                        }
                        else
                        {
                            p.TipoOperacion = "SIN_CAMBIOS";
                            p.Semaforo = "AZUL";
                            p.ObservacionValidacion = $"ℹ Ya está anulado en Force (ID: {p.IDPedidoForceExistente}). Al día.";
                        }
                    }
                    else
                    {
                        bool cambioEstado = !string.Equals(p.ForceEstadoActual, p.EstadoForceMapeado, StringComparison.OrdinalIgnoreCase);
                        bool cambioTotal = Math.Abs((p.ForceTotalActual ?? 0) - p.Total) > 0.01m;

                        if (cambioEstado || cambioTotal)
                        {
                            p.TipoOperacion = "ACTUALIZAR";
                            p.Semaforo = "VERDE";
                            string reempTxt = p.TieneReemplazoComprobantes
                                ? $" | 🔄 Cp: {string.Join(", ", p.ComprobantesActivosOrigen)} (anulado: {string.Join(", ", p.ComprobantesAnuladosOrigen)})"
                                : "";
                            p.ObservacionValidacion = $"✓ Cambio detectado: Force [{p.ForceEstadoActual}, S/ {(p.ForceTotalActual ?? 0):N2}] ➔ Actual [{p.EstadoForceMapeado}, S/ {p.Total:N2}]{reempTxt}. Listo para actualizar sin tocar stock.";
                        }
                        else
                        {
                            p.TipoOperacion = "SIN_CAMBIOS";
                            p.Semaforo = "AZUL";
                            string reempTxt = p.TieneReemplazoComprobantes
                                ? $" | 🔄 Cp vigente: {string.Join(", ", p.ComprobantesActivosOrigen)} (reemplazó a {string.Join(", ", p.ComprobantesAnuladosOrigen)}; el enlace se actualiza en Módulo Comprobantes)"
                                : "";
                            p.ObservacionValidacion = $"ℹ Ya existe en Force (ID: {p.IDPedidoForceExistente} | Estado: {p.ForceEstadoActual}){reempTxt}. Al día.";
                        }
                    }
                }
                else
                {
                    if (p.AnuladoOrigen)
                    {
                        p.TipoOperacion = "SIN_CAMBIOS";
                        p.Semaforo = "AZUL";
                        p.ObservacionValidacion = "ℹ Pedido anulado en origen que nunca subió a Force. Se omite.";
                    }
                    else
                    {
                        p.TipoOperacion = "INSERT";
                        if (erroresMaestros.Count > 0)
                        {
                            p.Semaforo = "ROJO";
                            p.ObservacionValidacion = "✕ " + string.Join(" | ", erroresMaestros);
                        }
                        else
                        {
                            if (p.EstadoFacturacionOrigen == "PARCIAL" && p.ComprobantesActivosOrigen.Count == 0)
                                advertencias.Add("Estado ERP: PARCIAL (sin comprobante vigente), entrará como PENDIENTE");

                            if (advertencias.Count > 0)
                            {
                                p.Semaforo = "AMBAR";
                                p.ObservacionValidacion = "⚠ Listo (Obs: " + string.Join("; ", advertencias) + ")";
                            }
                            else
                            {
                                p.Semaforo = "VERDE";
                                p.ObservacionValidacion = "✓ Maestros 100% alineados — Listo para subir sin tocar stock";
                            }
                        }
                    }
                }
            }

            _ultimoAnalisis = pedidos;

            sw.Stop();
            response.TiempoSegundos = Math.Round(sw.Elapsed.TotalSeconds, 2);
            response.TotalEncontrados = pedidos.Count;
            response.ListosParaSubir = pedidos.Count(x => x.ListoParaSubir);
            response.NuevosAInsertar = pedidos.Count(x => x.TipoOperacion == "INSERT" && x.ListoParaSubir);
            response.PedidosAActualizar = pedidos.Count(x => x.TipoOperacion == "ACTUALIZAR" && x.ListoParaSubir);
            response.AnulacionesPendientes = pedidos.Count(x => x.TipoOperacion == "ANULAR" && x.ListoParaSubir);
            response.YaExistenEnForce = pedidos.Count(x => x.YaExisteEnForce);
            response.ObservadosConError = pedidos.Count(x => !x.YaExisteEnForce && x.Semaforo == "ROJO");
            response.Anulados = pedidos.Count(x => x.AnuladoOrigen);
            response.FacturadosTotal = pedidos.Count(x => !x.AnuladoOrigen && x.EstadoForceMapeado == "FACTURADO");
            response.Pendientes = pedidos.Count(x => !x.AnuladoOrigen && x.EstadoFacturacionOrigen == "PENDIENTE");
            response.Parciales = pedidos.Count(x => !x.AnuladoOrigen && x.EstadoFacturacionOrigen == "PARCIAL");
            response.Pedidos = pedidos;

            return response;
        }

        // =========================================================================
        // 3. SINCRONIZAR PEDIDOS EN FORCE CON PROTECCIÓN ABSOLUTA DE STOCK (TRIGGER OFF)
        // =========================================================================
        public async Task EjecutarSubidaPedidosAsync(List<int> idsPedidosOrigen, CancellationToken ct = default)
        {
            if (!await _execLock.WaitAsync(0, ct))
            {
                _logger.LogWarning("Ya existe una sincronización de pedidos en curso.");
                return;
            }

            try
            {
                var pedidosASubir = _ultimoAnalisis
                    .Where(p => idsPedidosOrigen.Contains(p.IDPedidoOrigen))
                    .ToList();

                _status.IsRunning = true;
                _status.EstadoActual = "EJECUTANDO";
                _status.ProgresoActual = 0;
                _status.ProgresoTotal = pedidosASubir.Count;
                _status.TotalSubidos = 0;
                _status.TotalActualizados = 0;
                _status.TotalAnulados = 0;
                _status.TotalOmitidos = 0;
                _status.TotalErrores = 0;
                _status.StockProtegidoConfirmado = false;
                _status.EventosRecientes.Clear();

                if (pedidosASubir.Count == 0)
                {
                    _status.FaseDetalle = "No se encontraron pedidos seleccionados en memoria. Vuelva a consultar.";
                    _status.EstadoActual = "COMPLETADO";
                    return;
                }

                using var connForce = new SqlConnection(_connectionStringForce);
                await connForce.OpenAsync(ct);

                // Consultar valor de IDSUNATCatalogo7 válido existente si aplica
                object sunatCat7Gravado = 10;
                using (var cmdSunat = new SqlCommand("SELECT TOP 1 IDSUNATCatalogo7 FROM dbo.ped_PedidoItem WITH (NOLOCK) WHERE IDSUNATCatalogo7 IS NOT NULL;", connForce))
                {
                    var val = await cmdSunat.ExecuteScalarAsync(ct);
                    if (val != null && val != DBNull.Value)
                        sunatCat7Gravado = val;
                }

                // -----------------------------------------------------------------
                // BLINDAJE DE STOCK: DESACTIVAR TRIGGERS DE RESERVA Y DESCUENTO
                // -----------------------------------------------------------------
                _status.FaseDetalle = "🔒 Desactivando triggers de stock en Force (Cero impacto en inventario)...";
                AgregarEvento(0, "SISTEMA", "INFO", "🔒 Deshabilitando trg_PedidoItem_ReservarStock y trg_Pedido_DescontarStock para proteger el stock.");

                const string sqlDisableTriggers = @"
                    IF EXISTS (SELECT 1 FROM sys.triggers WHERE name = 'trg_PedidoItem_ReservarStock')
                        DISABLE TRIGGER dbo.trg_PedidoItem_ReservarStock ON dbo.ped_PedidoItem;
                    IF EXISTS (SELECT 1 FROM sys.triggers WHERE name = 'trg_Pedido_DescontarStock')
                        DISABLE TRIGGER dbo.trg_Pedido_DescontarStock ON dbo.ped_Pedido;";

                using (var cmdDis = new SqlCommand(sqlDisableTriggers, connForce))
                {
                    await cmdDis.ExecuteNonQueryAsync(ct);
                }
                _status.StockProtegidoConfirmado = true;

                try
                {
                    foreach (var p in pedidosASubir)
                    {
                        if (ct.IsCancellationRequested) break;
                        _status.ProgresoActual++;
                        _status.FaseDetalle = $"Procesando pedido {p.NumPedidoOrigen} ({_status.ProgresoActual}/{_status.ProgresoTotal})...";

                        if (p.TipoOperacion == "SIN_CAMBIOS")
                        {
                            _status.TotalOmitidos++;
                            AgregarEvento(p.IDPedidoOrigen, p.NumPedidoOrigen, "INFO", $"Omitido: Ya existe en Force y se encuentra al día (ID: {p.IDPedidoForceExistente}).");
                            continue;
                        }

                        if (p.Semaforo == "ROJO")
                        {
                            _status.TotalErrores++;
                            AgregarEvento(p.IDPedidoOrigen, p.NumPedidoOrigen, "ERROR", $"Rechazado por maestros faltantes: {p.ObservacionValidacion}");
                            continue;
                        }

                        using var tx = connForce.BeginTransaction();
                        try
                        {
                            // ESCENARIO 1: ANULAR PEDIDO EN FORCE SIN AFECTAR STOCK
                            if (p.TipoOperacion == "ANULAR")
                            {
                                int idForce = p.IDPedidoForceExistente!.Value;
                                const string sqlAnularPed = @"
                                    UPDATE dbo.ped_Pedido
                                    SET Estado = 'ANULADO',
                                        IDCpVenta = NULL,
                                        FechaModificacion = GETDATE(),
                                        DetalleAdicional = ISNULL(DetalleAdicional, '') + ' | [ANULADO EN ERP ANTIGUO]'
                                    WHERE ID = @IDPedidoForce;";

                                using (var cmdAnul = new SqlCommand(sqlAnularPed, connForce, tx))
                                {
                                    cmdAnul.Parameters.AddWithValue("@IDPedidoForce", idForce);
                                    await cmdAnul.ExecuteNonQueryAsync(ct);
                                }

                                tx.Commit();
                                p.ForceEstadoActual = "ANULADO";
                                p.Semaforo = "AZUL";
                                p.TipoOperacion = "SIN_CAMBIOS";
                                p.ObservacionValidacion = $"✓ Anulado exitosamente en Force (ID: {idForce}) | Stock intacto.";
                                _status.TotalAnulados++;
                                AgregarEvento(p.IDPedidoOrigen, p.NumPedidoOrigen, "WARN", $"⚠ Pedido anulado en Force (ID #{idForce}) | Stock intacto.");
                            }
                            // ESCENARIO 2: ACTUALIZAR ESTADO / MONTOS DE PEDIDO EXISTENTE EN FORCE SIN AFECTAR STOCK
                            else if (p.TipoOperacion == "ACTUALIZAR")
                            {
                                int idForce = p.IDPedidoForceExistente!.Value;
                                bool tieneBonifPed = p.Items.Any(it => it.EsBonificacion);
                                decimal totalDescPed = p.TotalDescuento > 0 ? p.TotalDescuento : p.Items.Sum(it => it.Descuento);

                                const string sqlUpdPed = @"
                                    UPDATE dbo.ped_Pedido
                                    SET Estado = @Estado,
                                        TieneCredito = @TieneCredito,
                                        TieneBonificacion = @TieneBonificacion,
                                        ConBonificacion = @TieneBonificacion,
                                        Cantidad = @Cantidad,
                                        SubTotal = @SubTotal,
                                        TotalDescuento = @TotalDescuento,
                                        TotalCredito = @TotalCredito,
                                        TotalContado = @TotalContado,
                                        Total = @Total,
                                        TieneImpuesto = @TieneImpuesto,
                                        TotalImpuesto = @TotalImpuesto,
                                        ValorAfecto = @ValorAfecto,
                                        ValorInafecto = @ValorInafecto,
                                        TotalPercepcion = @TotalPercepcion,
                                        IDCpVenta = CASE WHEN @Estado = 'PENDIENTE' THEN NULL ELSE IDCpVenta END,
                                        FechaModificacion = GETDATE(),
                                        DetalleAdicional = @DetalleAdicional
                                    WHERE ID = @IDPedidoForce;";

                                using (var cmdUpd = new SqlCommand(sqlUpdPed, connForce, tx))
                                {
                                    cmdUpd.Parameters.AddWithValue("@IDPedidoForce", idForce);
                                    cmdUpd.Parameters.AddWithValue("@Estado", p.EstadoForceMapeado);
                                    cmdUpd.Parameters.AddWithValue("@TieneCredito", p.TieneCredito);
                                    cmdUpd.Parameters.AddWithValue("@TieneBonificacion", tieneBonifPed);
                                    cmdUpd.Parameters.AddWithValue("@Cantidad", p.CantidadTotal);
                                    cmdUpd.Parameters.AddWithValue("@SubTotal", p.SubTotal);
                                    cmdUpd.Parameters.AddWithValue("@TotalDescuento", totalDescPed);
                                    cmdUpd.Parameters.AddWithValue("@TotalCredito", p.TotalCredito);
                                    cmdUpd.Parameters.AddWithValue("@TotalContado", p.TotalContado);
                                    cmdUpd.Parameters.AddWithValue("@Total", p.Total);
                                    cmdUpd.Parameters.AddWithValue("@TieneImpuesto", p.TieneImpuesto);
                                    cmdUpd.Parameters.AddWithValue("@TotalImpuesto", p.TotalImpuesto);
                                    cmdUpd.Parameters.AddWithValue("@ValorAfecto", p.ValorAfecto);
                                    cmdUpd.Parameters.AddWithValue("@ValorInafecto", p.ValorInafecto);
                                    cmdUpd.Parameters.AddWithValue("@TotalPercepcion", p.TotalPercepcion);
                                    cmdUpd.Parameters.AddWithValue("@DetalleAdicional", $"Sincronizado ERP Antiguo (PKID: {p.IDPedidoOrigen} | EstFact: {p.EstadoFacturacionOrigen})");
                                    await cmdUpd.ExecuteNonQueryAsync(ct);
                                }

                                tx.Commit();
                                p.ForceEstadoActual = p.EstadoForceMapeado;
                                p.ForceTotalActual = p.Total;
                                p.Semaforo = "AZUL";
                                p.TipoOperacion = "SIN_CAMBIOS";
                                p.ObservacionValidacion = $"✓ Actualizado en Force (ID: {idForce} | Estado: {p.EstadoForceMapeado}) | Stock intacto.";
                                _status.TotalActualizados++;
                                AgregarEvento(p.IDPedidoOrigen, p.NumPedidoOrigen, "OK", $"✎ Pedido actualizado en Force (ID #{idForce}) | Estado: {p.EstadoForceMapeado} | Total S/ {p.Total:N2} | Stock Intacto.");
                            }
                            // ESCENARIO 3: INSERTAR NUEVO PEDIDO EN FORCE SIN AFECTAR STOCK
                            else
                            {
                                string movilId = $"MIG-ERP-{p.IDPedidoOrigen}";

                                // Verificar concurrencia por si se insertó justo antes
                                using (var cmdChk = new SqlCommand("SELECT TOP 1 ID FROM dbo.ped_Pedido WITH (NOLOCK) WHERE PedidoMovilID = @MovilId OR (Numero = @Numero AND Eliminado = 0);", connForce, tx))
                                {
                                    cmdChk.Parameters.AddWithValue("@MovilId", movilId);
                                    cmdChk.Parameters.AddWithValue("@Numero", p.NumPedidoOrigen);
                                    var exist = await cmdChk.ExecuteScalarAsync(ct);
                                    if (exist != null && exist != DBNull.Value)
                                    {
                                        tx.Rollback();
                                        _status.TotalOmitidos++;
                                        AgregarEvento(p.IDPedidoOrigen, p.NumPedidoOrigen, "WARN", $"Omitido: Ya registrado en Force (ID: {exist}).");
                                        continue;
                                    }
                                }

                                const string sqlInsertCab = @"
                                    INSERT INTO dbo.ped_Pedido (
                                        PedidoMovilID, Numero, IDCliente, IDVendedor, IDAlmacen, IDSucursal,
                                        IDListaPrecio, IDCatalogo, IDMedioPago, IDModalidadCredito,
                                        FechaEmision, FechaEntrega,
                                        TieneCredito, TieneBonificacion, ConBonificacion,
                                        Cantidad, SubTotal, TotalDescuento, TotalCredito, TotalContado, Total,
                                        Estado, CodigoPromocion, DetalleAdicional,
                                        UsuarioCreador, FechaCreacion, FechaModificacion, Eliminado,
                                        IDTipoCpPedido, IDRuta,
                                        TieneImpuesto, TotalImpuesto, ValorAfecto, ValorInafecto, TotalPercepcion, TotalICB
                                    )
                                    VALUES (
                                        @PedidoMovilID, @Numero, @IDCliente, @IDVendedor, @IDAlmacen, @IDSucursal,
                                        @IDListaPrecio, @IDCatalogo, @IDMedioPago, @IDModalidadCredito,
                                        @FechaEmision, @FechaEntrega,
                                        @TieneCredito, @TieneBonificacion, @TieneBonificacion,
                                        @Cantidad, @SubTotal, @TotalDescuento, @TotalCredito, @TotalContado, @Total,
                                        @Estado, NULL, @DetalleAdicional,
                                        16, GETDATE(), GETDATE(), 0,
                                        @IDTipoCpPedido, @IDRuta,
                                        @TieneImpuesto, @TotalImpuesto, @ValorAfecto, @ValorInafecto, @TotalPercepcion, 0
                                    );
                                    SELECT CAST(SCOPE_IDENTITY() AS int);";

                                bool tieneBonifPed = p.Items.Any(it => it.EsBonificacion);
                                decimal totalDescPed = p.TotalDescuento > 0 ? p.TotalDescuento : p.Items.Sum(it => it.Descuento);

                                int nuevoIdPedido;
                                using (var cmdCab = new SqlCommand(sqlInsertCab, connForce, tx))
                                {
                                    cmdCab.Parameters.AddWithValue("@PedidoMovilID", movilId);
                                    cmdCab.Parameters.AddWithValue("@Numero", p.NumPedidoOrigen);
                                    cmdCab.Parameters.AddWithValue("@IDCliente", p.IDClienteForce!.Value);
                                    cmdCab.Parameters.AddWithValue("@IDVendedor", p.IDVendedorForce!.Value);
                                    cmdCab.Parameters.AddWithValue("@IDAlmacen", p.IDAlmacenForce!.Value);
                                    cmdCab.Parameters.AddWithValue("@IDSucursal", p.IDSucursalForce!.Value);
                                    cmdCab.Parameters.AddWithValue("@IDListaPrecio", p.IDListaPrecioForce ?? 6);
                                    cmdCab.Parameters.AddWithValue("@IDCatalogo", p.IDCatalogoForce!.Value);
                                    cmdCab.Parameters.AddWithValue("@IDMedioPago", (object?)p.IDMedioPagoForce ?? DBNull.Value);
                                    cmdCab.Parameters.AddWithValue("@IDModalidadCredito", (object?)p.IDModalidadCreditoForce ?? DBNull.Value);
                                    cmdCab.Parameters.AddWithValue("@FechaEmision", p.FechaEmision);
                                    cmdCab.Parameters.AddWithValue("@FechaEntrega", (object?)p.FechaEntrega ?? DBNull.Value);
                                    cmdCab.Parameters.AddWithValue("@TieneCredito", p.TieneCredito);
                                    cmdCab.Parameters.AddWithValue("@TieneBonificacion", tieneBonifPed);
                                    cmdCab.Parameters.AddWithValue("@Cantidad", p.CantidadTotal);
                                    cmdCab.Parameters.AddWithValue("@SubTotal", p.SubTotal);
                                    cmdCab.Parameters.AddWithValue("@TotalDescuento", totalDescPed);
                                    cmdCab.Parameters.AddWithValue("@TotalCredito", p.TotalCredito);
                                    cmdCab.Parameters.AddWithValue("@TotalContado", p.TotalContado);
                                    cmdCab.Parameters.AddWithValue("@Total", p.Total);
                                    cmdCab.Parameters.AddWithValue("@Estado", p.EstadoForceMapeado);
                                    cmdCab.Parameters.AddWithValue("@DetalleAdicional", $"Migrado ERP Antiguo (PKID: {p.IDPedidoOrigen} | EstFact: {p.EstadoFacturacionOrigen})");
                                    cmdCab.Parameters.AddWithValue("@IDTipoCpPedido", p.IDTipoCpPedidoForce!.Value);
                                    cmdCab.Parameters.AddWithValue("@IDRuta", (object?)p.IDRutaForce ?? DBNull.Value);
                                    cmdCab.Parameters.AddWithValue("@TieneImpuesto", p.TieneImpuesto);
                                    cmdCab.Parameters.AddWithValue("@TotalImpuesto", p.TotalImpuesto);
                                    cmdCab.Parameters.AddWithValue("@ValorAfecto", p.ValorAfecto);
                                    cmdCab.Parameters.AddWithValue("@ValorInafecto", p.ValorInafecto);
                                    cmdCab.Parameters.AddWithValue("@TotalPercepcion", p.TotalPercepcion);

                                    nuevoIdPedido = (int)(await cmdCab.ExecuteScalarAsync(ct))!;
                                }

                                const string sqlInsertItem = @"
                                    INSERT INTO dbo.ped_PedidoItem (
                                        IDPedido, IDProducto, IDUnidad, Cantidad, CantidadFactor,
                                        Precio, PrecioNeto, PrecioConImpuesto, Descuento, DescuentoAcumulado,
                                        SubTotal, SubTotalNeto, SubTotalConImpuesto, Total,
                                        TieneImpuesto, PorcentajeImpuesto, MontoImpuesto, ValorAfecto, ValorInafecto,
                                        IDSUNATCatalogo7, MontoPercepcion, Credito, EsBonificacion, IDBonificacion, Eliminado
                                    )
                                    VALUES (
                                        @IDPedido, @IDProducto, @IDUnidad, @Cantidad, @CantidadFactor,
                                        @Precio, @PrecioNeto, @PrecioConImpuesto, @Descuento, @Descuento,
                                        @SubTotal, @SubTotalNeto, @SubTotalConImpuesto, @Total,
                                        @TieneImpuesto, @PorcentajeImpuesto, @MontoImpuesto, @ValorAfecto, @ValorInafecto,
                                        @IDSUNATCatalogo7, @MontoPercepcion, @Credito, @EsBonificacion, NULL, 0
                                    );";

                                foreach (var it in p.Items)
                                {
                                    using var cmdIt = new SqlCommand(sqlInsertItem, connForce, tx);
                                    cmdIt.Parameters.AddWithValue("@IDPedido", nuevoIdPedido);
                                    cmdIt.Parameters.AddWithValue("@IDProducto", it.IDProductoForce!.Value);
                                    cmdIt.Parameters.AddWithValue("@IDUnidad", it.IDUnidadForce!.Value);
                                    cmdIt.Parameters.AddWithValue("@Cantidad", it.Cantidad);
                                    cmdIt.Parameters.AddWithValue("@CantidadFactor", it.CantidadFactor);
                                    cmdIt.Parameters.AddWithValue("@Precio", it.PrecioUnitario);
                                    cmdIt.Parameters.AddWithValue("@PrecioNeto", it.PrecioNeto);
                                    cmdIt.Parameters.AddWithValue("@PrecioConImpuesto", it.PrecioConImpuesto);
                                    cmdIt.Parameters.AddWithValue("@Descuento", it.Descuento);
                                    cmdIt.Parameters.AddWithValue("@SubTotal", it.SubTotal);
                                    cmdIt.Parameters.AddWithValue("@SubTotalNeto", it.SubTotalNeto);
                                    cmdIt.Parameters.AddWithValue("@SubTotalConImpuesto", it.Total);
                                    cmdIt.Parameters.AddWithValue("@Total", it.Total);
                                    cmdIt.Parameters.AddWithValue("@TieneImpuesto", it.TieneImpuesto);
                                    cmdIt.Parameters.AddWithValue("@PorcentajeImpuesto", it.PorcentajeImpuesto);
                                    cmdIt.Parameters.AddWithValue("@MontoImpuesto", it.MontoImpuesto);
                                    cmdIt.Parameters.AddWithValue("@ValorAfecto", it.ValorAfecto);
                                    cmdIt.Parameters.AddWithValue("@ValorInafecto", it.ValorInafecto);
                                    cmdIt.Parameters.AddWithValue("@IDSUNATCatalogo7", sunatCat7Gravado);
                                    cmdIt.Parameters.AddWithValue("@MontoPercepcion", it.MontoPercepcion);
                                    cmdIt.Parameters.AddWithValue("@Credito", it.Credito);
                                    cmdIt.Parameters.AddWithValue("@EsBonificacion", it.EsBonificacion);

                                    await cmdIt.ExecuteNonQueryAsync(ct);
                                }

                                tx.Commit();
                                p.YaExisteEnForce = true;
                                p.IDPedidoForceExistente = nuevoIdPedido;
                                p.ForceEstadoActual = p.EstadoForceMapeado;
                                p.ForceTotalActual = p.Total;
                                p.Semaforo = "AZUL";
                                p.TipoOperacion = "SIN_CAMBIOS";
                                p.ObservacionValidacion = $"✓ Subido hoy a Force (ID: {nuevoIdPedido})";

                                _status.TotalSubidos++;
                                AgregarEvento(p.IDPedidoOrigen, p.NumPedidoOrigen, "OK", $"✓ Subido como Force ID #{nuevoIdPedido} | Estado: {p.EstadoForceMapeado} | {p.Items.Count} ítems | Stock Intacto.");
                            }
                        }
                        catch (Exception exPed)
                        {
                            try { tx.Rollback(); } catch { }
                            _status.TotalErrores++;
                            AgregarEvento(p.IDPedidoOrigen, p.NumPedidoOrigen, "ERROR", $"Error al procesar pedido: {exPed.Message}");
                            _logger.LogError(exPed, "Error procesando pedido origen {ID}", p.IDPedidoOrigen);
                        }
                    }
                }
                finally
                {
                    // SIEMPRE REACTIVAR TRIGGERS DE STOCK SIN IMPORTAR QUÉ PASE
                    const string sqlEnableTriggers = @"
                        IF EXISTS (SELECT 1 FROM sys.triggers WHERE name = 'trg_PedidoItem_ReservarStock')
                            ENABLE TRIGGER dbo.trg_PedidoItem_ReservarStock ON dbo.ped_PedidoItem;
                        IF EXISTS (SELECT 1 FROM sys.triggers WHERE name = 'trg_Pedido_DescontarStock')
                            ENABLE TRIGGER dbo.trg_Pedido_DescontarStock ON dbo.ped_Pedido;";

                    try
                    {
                        using var cmdEn = new SqlCommand(sqlEnableTriggers, connForce);
                        await cmdEn.ExecuteNonQueryAsync(CancellationToken.None);
                        AgregarEvento(0, "SISTEMA", "INFO", "🔓 Triggers de stock reactivados correctamente. Operación 100% segura.");
                    }
                    catch (Exception exEn)
                    {
                        _logger.LogCritical(exEn, "Error crítico reactivando triggers de stock.");
                        AgregarEvento(0, "SISTEMA", "ERROR", $"ALERTA: Error al reactivar triggers de stock: {exEn.Message}");
                    }
                }

                _status.EstadoActual = "COMPLETADO";
                _status.FaseDetalle = $"Sincronización finalizada: {_status.TotalSubidos} nuevos, {_status.TotalActualizados} actualizados, {_status.TotalAnulados} anulados, {_status.TotalOmitidos} omitidos, {_status.TotalErrores} errores (Stock Intacto).";
            }
            finally
            {
                _status.IsRunning = false;
                _execLock.Release();
            }
        }

        private void AgregarEvento(int idOrigen, string numPedido, string nivel, string msg)
        {
            _status.EventosRecientes.Insert(0, new PedidoEventoEjecucionDto
            {
                Hora = DateTime.Now.ToString("HH:mm:ss"),
                IDPedidoOrigen = idOrigen,
                NumPedido = numPedido,
                Nivel = nivel,
                Mensaje = msg
            });
            if (_status.EventosRecientes.Count > 200)
                _status.EventosRecientes.RemoveAt(_status.EventosRecientes.Count - 1);
        }
    }
}
