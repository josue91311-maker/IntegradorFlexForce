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
    public class SyncComprobantesEngine
    {
        private readonly string _connectionStringForce;
        private readonly string _connectionStringAntiguo;
        private readonly ILogger<SyncComprobantesEngine> _logger;
        private readonly SemaphoreSlim _execLock = new(1, 1);

        private readonly ComprobanteStatusDto _status = new();
        private List<ComprobanteAlineadoDto> _ultimoAnalisis = new();

        public SyncComprobantesEngine(IConfiguration configuration, ILogger<SyncComprobantesEngine> logger)
        {
            _logger = logger;
            _connectionStringForce = configuration.GetConnectionString("ForceGVD")
                ?? throw new InvalidOperationException("Cadena de conexión 'ForceGVD' no configurada.");
            _connectionStringAntiguo = configuration.GetConnectionString("ERPAntiguo")
                ?? throw new InvalidOperationException("Cadena de conexión 'ERPAntiguo' no configurada.");
        }

        public ComprobanteStatusDto GetStatus()
        {
            lock (_status)
            {
                return new ComprobanteStatusDto
                {
                    IsRunning = _status.IsRunning,
                    EstadoActual = _status.EstadoActual,
                    FaseDetalle = _status.FaseDetalle,
                    ProgresoActual = _status.ProgresoActual,
                    ProgresoTotal = _status.ProgresoTotal,
                    TotalSubidos = _status.TotalSubidos,
                    TotalDeudasGeneradas = _status.TotalDeudasGeneradas,
                    TotalOmitidos = _status.TotalOmitidos,
                    TotalErrores = _status.TotalErrores,
                    EventosRecientes = _status.EventosRecientes.TakeLast(30).ToList()
                };
            }
        }

        private void AgregarEvento(int idCpOrigen, string numCp, string tipo, string mensaje)
        {
            lock (_status)
            {
                _status.EventosRecientes.Add(new ComprobanteLogEventoDto
                {
                    Timestamp = DateTime.Now,
                    IDCpOrigen = idCpOrigen,
                    NumCp = numCp,
                    Tipo = tipo,
                    Mensaje = mensaje
                });

                if (_status.EventosRecientes.Count > 200)
                    _status.EventosRecientes.RemoveAt(0);
            }
        }

        // =========================================================================
        // 1. CONSULTA Y ALINEACIÓN DE COMPROBANTES (ORIGEN ERP -> FORCE GVD)
        // =========================================================================
        public async Task<ComprobanteAnalisisResponseDto> ConsultarYAlinearComprobantesAsync(
            ComprobanteFiltroRequestDto filtro,
            CancellationToken ct = default)
        {
            var sw = Stopwatch.StartNew();
            var response = new ComprobanteAnalisisResponseDto();

            DateTime fechaDesde = filtro.FechaDesde.Date;
            DateTime fechaHastaExclusivo = filtro.FechaHasta.Date.AddDays(1);

            _logger.LogInformation("Consultando Comprobantes desde {Desde} hasta {Hasta}...", fechaDesde, fechaHastaExclusivo);

            var comprobantes = new List<ComprobanteAlineadoDto>();

            // 1. Extraer cabeceras desde digalimenta41
            using (var connAntiguo = new SqlConnection(_connectionStringAntiguo))
            {
                await connAntiguo.OpenAsync(ct);

                string filtroVendedores = (filtro.VendedoresOrigenIds != null && filtro.VendedoresOrigenIds.Count > 0)
                    ? $"AND cp.IDResponsable IN ({string.Join(",", filtro.VendedoresOrigenIds.Distinct())})"
                    : "";

                string filtroAnulados = filtro.IncluirAnulados ? "" : "AND ISNULL(cp.Anulado, 0) = 0";

                string filtroTipoDoc = "";
                if (filtro.TipoDocumento == "FACTURAS")
                    filtroTipoDoc = "AND tcp.IDTipoComprobantePago = 1";
                else if (filtro.TipoDocumento == "BOLETAS")
                    filtroTipoDoc = "AND tcp.IDTipoComprobantePago = 4";

                string sqlCabeceras = $@"
                    SELECT 
                        cp.PKID                                          AS IDCpOrigen,
                        LTRIM(RTRIM(ISNULL(cp.NumCp, '')))               AS NumCp,
                        LTRIM(RTRIM(ISNULL(cp.NumCpContable, '')))       AS NumCpContable,
                        ISNULL(cp.Fecha, GETDATE())                      AS Fecha,
                        ISNULL(cp.FechaEmision, GETDATE())               AS FechaEmision,
                        ISNULL(cp.Hora, GETDATE())                       AS Hora,
                        cp.IDTipoCp                                      AS IDTipoCpOrigen,
                        LTRIM(RTRIM(ISNULL(tcp.Serie, '')))              AS SerieOrigen,
                        ISNULL(tcp.Numero, 0)                            AS CorrelativoOrigen,
                        ISNULL(tcp.IDTipoComprobantePago, 4)             AS IDTipoComprobantePagoOrigen,
                        LTRIM(RTRIM(ISNULL(tcp.Descripcion, '')))        AS TipoCpDescripcion,
                        ISNULL(cp.IDPersona, 0)                          AS IDClienteOrigen,
                        LTRIM(RTRIM(ISNULL(pCli.Codigo, '')))            AS CodigoCliente,
                        LTRIM(RTRIM(ISNULL(pCli.Nombre, '')))            AS NombreCliente,
                        LTRIM(RTRIM(ISNULL(cpv.DocIdentidad, '')))       AS DocIdentidadCliente,
                        ISNULL(cp.IDResponsable, 0)                      AS IDVendedorOrigen,
                        LTRIM(RTRIM(ISNULL(pVend.Codigo, '')))           AS CodigoVendedor,
                        LTRIM(RTRIM(ISNULL(pVend.Nombre, '')))           AS NombreVendedor,
                        ISNULL(ci.IDAlmacen, 0)                          AS IDAlmacenOrigen,
                        LTRIM(RTRIM(ISNULL(alm.Nombre, '')))             AS AlmacenNombre,
                        ISNULL(cp.IDSucursal, 0)                         AS IDSucursalOrigen,
                        LTRIM(RTRIM(ISNULL(cpv.Direccion, '')))          AS DireccionFiscal,
                        LTRIM(RTRIM(ISNULL(cpv.DireccionEntrega, '')))   AS DireccionEntrega,
                        LTRIM(RTRIM(ISNULL(cpv.EstadoEntrega, 'PENDIENTE'))) AS EstadoEntrega,
                        ISNULL(cpr.IDCpRelacionado, 0)                   AS IDPedidoOrigen,
                        LTRIM(RTRIM(ISNULL(cpPed.NumCp, '')))            AS NumPedidoOrigen,
                        CAST(ISNULL(cp.SubTotal, 0) AS decimal(18,6))    AS SubTotal,
                        CAST(ISNULL(cp.Total, 0) AS decimal(18,6))       AS Total,
                        CAST(ISNULL(cp.ValorAfecto, 0) AS decimal(18,6)) AS ValorAfecto,
                        CAST(ISNULL(cp.ValorInafecto, 0) AS decimal(18,6)) AS ValorInafecto,
                        CAST(ISNULL(cp.ValorExonerado, 0) AS decimal(18,6)) AS ValorExonerado,
                        CAST(ISNULL(cp.ValorDescuento, 0) AS decimal(18,6)) AS ValorDescuento,
                        CAST(ISNULL(cp.ValorVenta, 0) AS decimal(18,6))  AS ValorVenta,
                        CAST(ISNULL(cp.TieneImpuesto, 1) AS bit)         AS TieneImpuesto,
                        CAST(ISNULL(cp.PreciosIncImpto, 1) AS bit)       AS PreciosIncImpto,
                        CAST(ISNULL(cp.Anulado, 0) AS bit)               AS AnuladoOrigen,
                        CAST(CASE WHEN ISNULL(ccp.Credito, 0) = 1 OR ISNULL(cpv.OmitirPoliticasCredito, 0) = 1 THEN 1 ELSE 0 END AS bit) AS EsCredito,
                        CAST(ISNULL(ccp.Saldo, 0) AS decimal(18,6))      AS SaldoPendiente,
                        ccp.FechaVencimiento                             AS FechaVencimiento,
                        ccp.FechaProgramacionPago                        AS FechaProgramacionPago,
                        ISNULL(ccp.MotivoEliminacion, '')                AS MotivoEliminacion
                    FROM dbo.Cp cp WITH (NOLOCK)
                    INNER JOIN dbo.CpVenta cpv WITH (NOLOCK) ON cpv.PKID = cp.PKID
                    INNER JOIN dbo.TipoCp tcp WITH (NOLOCK) ON tcp.PKID = cp.IDTipoCp
                    LEFT JOIN dbo.CpInventario ci WITH (NOLOCK) ON ci.IDCp = cp.PKID
                    LEFT JOIN dbo.Persona pCli WITH (NOLOCK) ON pCli.PKID = cp.IDPersona
                    LEFT JOIN dbo.Persona pVend WITH (NOLOCK) ON pVend.PKID = cp.IDResponsable
                    LEFT JOIN dbo.Almacen alm WITH (NOLOCK) ON alm.PKID = ci.IDAlmacen
                    LEFT JOIN dbo.CpRelacionado cpr WITH (NOLOCK) ON cpr.IDCp = cp.PKID
                    LEFT JOIN dbo.Cp cpPed WITH (NOLOCK) ON cpPed.PKID = cpr.IDCpRelacionado
                    LEFT JOIN dbo.CpCobrarPagar ccp WITH (NOLOCK) ON ccp.PKID = cp.PKID
                    WHERE cp.FechaEmision >= @Desde 
                      AND cp.FechaEmision < @Hasta
                      AND tcp.TipoCp = 'Venta'
                      {filtroVendedores}
                      {filtroAnulados}
                      {filtroTipoDoc}
                    ORDER BY cp.FechaEmision DESC, cp.PKID DESC;";

                using (var cmd = new SqlCommand(sqlCabeceras, connAntiguo) { CommandTimeout = 120 })
                {
                    cmd.Parameters.AddWithValue("@Desde", fechaDesde);
                    cmd.Parameters.AddWithValue("@Hasta", fechaHastaExclusivo);

                    using var r = await cmd.ExecuteReaderAsync(ct);
                    while (await r.ReadAsync(ct))
                    {
                        var dto = new ComprobanteAlineadoDto
                        {
                            IDCpOrigen = r.GetInt32(0),
                            NumCp = r.GetString(1),
                            NumCpContable = r.GetString(2),
                            Fecha = r.GetDateTime(3),
                            FechaEmision = r.GetDateTime(4),
                            Hora = r.GetDateTime(5),
                            IDTipoCpOrigen = r.GetInt32(6),
                            SerieOrigen = r.GetString(7),
                            CorrelativoOrigen = r.GetInt32(8),
                            IDTipoComprobantePagoOrigen = r.GetInt32(9),
                            TipoCpDescripcion = r.GetString(10),
                            IDClienteOrigen = r.GetInt32(11),
                            CodigoCliente = r.GetString(12),
                            NombreCliente = r.GetString(13),
                            DocIdentidadCliente = r.GetString(14),
                            IDVendedorOrigen = r.GetInt32(15),
                            CodigoVendedor = r.GetString(16),
                            NombreVendedor = r.GetString(17),
                            IDAlmacenOrigen = r.GetInt32(18),
                            AlmacenNombre = r.GetString(19),
                            IDSucursalOrigen = r.GetInt32(20),
                            DireccionFiscal = r.GetString(21),
                            DireccionEntrega = r.GetString(22),
                            EstadoEntrega = r.GetString(23),
                            IDPedidoOrigen = r.GetInt32(24),
                            NumPedidoOrigen = r.GetString(25),
                            SubTotal = r.GetDecimal(26),
                            Total = r.GetDecimal(27),
                            ValorAfecto = r.GetDecimal(28),
                            ValorInafecto = r.GetDecimal(29),
                            ValorExonerado = r.GetDecimal(30),
                            ValorDescuento = r.GetDecimal(31),
                            ValorVenta = r.GetDecimal(32),
                            TieneImpuesto = r.GetBoolean(33),
                            PreciosIncImpto = r.GetBoolean(34),
                            AnuladoOrigen = r.GetBoolean(35),
                            EsCredito = r.GetBoolean(36),
                            SaldoPendiente = r.GetDecimal(37),
                            FechaVencimiento = r.IsDBNull(38) ? null : r.GetDateTime(38),
                            FechaProgramacionPago = r.IsDBNull(39) ? null : r.GetDateTime(39),
                            MotivoAnulacion = r.IsDBNull(40) ? "" : r.GetString(40)
                        };

                        comprobantes.Add(dto);
                    }
                }

                // 2. Extraer ítems desde digalimenta41 en bloques
                if (comprobantes.Count > 0)
                {
                    var idsCp = comprobantes.Select(x => x.IDCpOrigen).ToList();
                    const int batchSize = 1000;
                    var dictComprobantes = comprobantes.ToDictionary(x => x.IDCpOrigen);

                    for (int i = 0; i < idsCp.Count; i += batchSize)
                    {
                        var chunk = idsCp.Skip(i).Take(batchSize).ToList();
                        string sqlItems = $@"
                            SELECT 
                                icp.PKID                                         AS IDItemOrigen,
                                icp.IDCp                                         AS IDCpOrigen,
                                ISNULL(ip.IDProducto, 0)                         AS IDProductoOrigen,
                                LTRIM(RTRIM(ISNULL(ps.Codigo, '')))              AS CodigoProducto,
                                LTRIM(RTRIM(ISNULL(icp.Descripcion, '')))        AS DescripcionProducto,
                                ISNULL(ip.IDUnidad, 0)                           AS IDUnidadOrigen,
                                LTRIM(RTRIM(ISNULL(u.Abreviacion, 'UND')))       AS UnidadAbrev,
                                CAST(ISNULL(icp.Cantidad, 0) AS decimal(18,6))   AS Cantidad,
                                CAST(ISNULL(ip.Factor, 1) AS decimal(18,6))      AS CantidadFactor,
                                CAST(ISNULL(icp.ValorUnitario, 0) AS decimal(18,6)) AS PrecioUnitario,
                                CAST(ISNULL(icp.SubTotal, 0) AS decimal(18,6))   AS SubTotal,
                                CAST(ISNULL(icp.Total, 0) AS decimal(18,6))      AS Total,
                                CAST(ISNULL(icp.PorcentajeImpto, 18) AS decimal(18,6)) AS PorcentajeImpuesto,
                                CAST(ISNULL(icp.ValorAfecto, 0) AS decimal(18,6)) AS ValorAfecto,
                                CAST(ISNULL(icp.ValorInafecto, 0) AS decimal(18,6)) AS ValorInafecto,
                                CAST(ISNULL(icp.ValorExonerado, 0) AS decimal(18,6)) AS ValorExonerado,
                                CAST(ISNULL(icp.ValorVenta, 0) AS decimal(18,6)) AS ValorVenta,
                                CAST(ISNULL(icp.Bonificacion, 0) AS bit)         AS EsBonificacion,
                                CAST(ISNULL(icp.Anulado, 0) AS bit)              AS Anulado,
                                CAST(ISNULL(icp.ValorDescuento, 0) AS decimal(18,6)) AS ValorDescuento
                            FROM dbo.ItemCp icp WITH (NOLOCK)
                            LEFT JOIN dbo.ItemProducto ip WITH (NOLOCK) ON ip.PKID = icp.PKID
                            LEFT JOIN dbo.ProductoServicio ps WITH (NOLOCK) ON ps.PKID = ip.IDProducto
                            LEFT JOIN dbo.Unidad u WITH (NOLOCK) ON u.PKID = ip.IDUnidad
                            WHERE icp.IDCp IN ({string.Join(",", chunk)});";

                        using var cmdIt = new SqlCommand(sqlItems, connAntiguo) { CommandTimeout = 120 };
                        using var rIt = await cmdIt.ExecuteReaderAsync(ct);
                        while (await rIt.ReadAsync(ct))
                        {
                            int idCp = rIt.GetInt32(1);
                            if (dictComprobantes.TryGetValue(idCp, out var cpDto))
                            {
                                cpDto.Items.Add(new ComprobanteItemAlineadoDto
                                {
                                    IDItemOrigen = rIt.GetInt32(0),
                                    IDProductoOrigen = rIt.GetInt32(2),
                                    CodigoProducto = rIt.GetString(3),
                                    DescripcionProducto = rIt.GetString(4),
                                    IDUnidadOrigen = rIt.GetInt32(5),
                                    UnidadAbrev = rIt.GetString(6),
                                    Cantidad = rIt.GetDecimal(7),
                                    CantidadFactor = rIt.GetDecimal(8),
                                    PrecioUnitario = rIt.GetDecimal(9),
                                    SubTotal = rIt.GetDecimal(10),
                                    Total = rIt.GetDecimal(11),
                                    PorcentajeImpuesto = rIt.GetDecimal(12),
                                    ValorAfecto = rIt.GetDecimal(13),
                                    ValorInafecto = rIt.GetDecimal(14),
                                    ValorVenta = rIt.GetDecimal(16),
                                    EsBonificacion = rIt.GetBoolean(17),
                                    Anulado = rIt.GetBoolean(18),
                                    ValorDescuento = Math.Max(0, rIt.GetDecimal(19))
                                });
                            }
                        }
                    }
                }

                // 2.B Detectar todos los comprobantes ANULADOS en digalimenta41 asociados a los mismos pedidos
                // (Incluso si fueron emitidos en otra fecha o si el filtro IncluirAnulados estaba desmarcado)
                var idsPedOrigen = comprobantes.Where(x => x.IDPedidoOrigen > 0).Select(x => x.IDPedidoOrigen).Distinct().ToList();
                if (idsPedOrigen.Count > 0)
                {
                    var anuladosPorPedidoOrigen = new Dictionary<int, List<string>>();
                    for (int i = 0; i < idsPedOrigen.Count; i += 1000)
                    {
                        var chunkPed = idsPedOrigen.Skip(i).Take(1000).ToList();
                        string sqlAnuladosPed = $@"
                            SELECT 
                                cpr.IDCpRelacionado AS IDPedidoOrigen,
                                LTRIM(RTRIM(ISNULL(cp.NumCp, ''))) AS NumCp
                            FROM dbo.CpRelacionado cpr WITH (NOLOCK)
                            INNER JOIN dbo.Cp cp WITH (NOLOCK) ON cp.PKID = cpr.IDCp
                            INNER JOIN dbo.CpVenta cpv WITH (NOLOCK) ON cpv.PKID = cp.PKID
                            WHERE cpr.IDCpRelacionado IN ({string.Join(",", chunkPed)})
                              AND ISNULL(cp.Anulado, 0) = 1
                            ORDER BY cp.PKID ASC;";

                        using var cmdAnPed = new SqlCommand(sqlAnuladosPed, connAntiguo) { CommandTimeout = 120 };
                        using var rAn = await cmdAnPed.ExecuteReaderAsync(ct);
                        while (await rAn.ReadAsync(ct))
                        {
                            int idPedOrig = rAn.GetInt32(0);
                            string numAnul = rAn.GetString(1);
                            if (string.IsNullOrEmpty(numAnul)) continue;
                            if (!anuladosPorPedidoOrigen.TryGetValue(idPedOrig, out var listAn))
                            {
                                listAn = new List<string>();
                                anuladosPorPedidoOrigen[idPedOrig] = listAn;
                            }
                            if (!listAn.Contains(numAnul, StringComparer.OrdinalIgnoreCase))
                                listAn.Add(numAnul);
                        }
                    }

                    foreach (var c in comprobantes)
                    {
                        if (!c.AnuladoOrigen && c.IDPedidoOrigen > 0 && anuladosPorPedidoOrigen.TryGetValue(c.IDPedidoOrigen, out var prevAnulados) && prevAnulados.Count > 0)
                        {
                            c.EsReemplazoDeAnulado = true;
                            c.ComprobantesAnuladosDelPedido = string.Join(", ", prevAnulados);
                        }
                    }
                }
            }

            // 3. Cargar Equivalencias desde Force (BDForceGVD)
            var cliForceByExtra = new Dictionary<int, int>();
            var vendForceByExtra = new Dictionary<int, int>();
            var almForceByExtra = new Dictionary<int, int>();
            var sucForceByExtra = new Dictionary<int, int>();
            var prodForceByExtra = new Dictionary<int, int>();
            var unidForceByExtra = new Dictionary<int, int>();
            var tipoCpVentasBySerie = new Dictionary<string, (int ID, int Correlativo, int IDComprobanteTipo)>(StringComparer.OrdinalIgnoreCase);
            var pedidosForcePorOrigen = new Dictionary<int, (int IDForce, string NumeroForce, int? IDCpVenta, string EstadoForce, int? IDCatalogo, int? IDModalidadCredito)>();
            var pedidosForcePorNumero = new Dictionary<string, (int IDForce, string NumeroForce, int? IDCpVenta, string EstadoForce, int? IDCatalogo, int? IDModalidadCredito)>(StringComparer.OrdinalIgnoreCase);
            var comprobantesForceInfo = new Dictionary<string, (int IDForce, bool Anulado, decimal Total, int? IDPedido, int? IDDeuda, decimal? SaldoDeuda, string EstadoDeuda)>(StringComparer.OrdinalIgnoreCase);
            var cpPorIdForce = new Dictionary<int, (string NumCp, bool Anulado, int? IDPedido)>();

            using (var connForce = new SqlConnection(_connectionStringForce))
            {
                await connForce.OpenAsync(ct);

                // Clientes
                using (var cmd = new SqlCommand("SELECT ID, IDExtra FROM dbo.cli_Cliente WITH (NOLOCK) WHERE Eliminado = 0 AND IDExtra IS NOT NULL;", connForce))
                using (var r = await cmd.ExecuteReaderAsync(ct))
                    while (await r.ReadAsync(ct))
                        cliForceByExtra[r.GetInt32(1)] = r.GetInt32(0);

                // Vendedores
                using (var cmd = new SqlCommand("SELECT ID, IDExtra FROM dbo.vnd_Vendedor WITH (NOLOCK) WHERE Eliminado = 0 AND IDExtra IS NOT NULL;", connForce))
                using (var r = await cmd.ExecuteReaderAsync(ct))
                    while (await r.ReadAsync(ct))
                        vendForceByExtra[r.GetInt32(1)] = r.GetInt32(0);

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

                // Productos
                using (var cmd = new SqlCommand("SELECT ID, IDExtra FROM dbo.prd_Producto WITH (NOLOCK) WHERE Eliminado = 0 AND IDExtra IS NOT NULL;", connForce))
                using (var r = await cmd.ExecuteReaderAsync(ct))
                    while (await r.ReadAsync(ct))
                        prodForceByExtra[r.GetInt32(1)] = r.GetInt32(0);

                // Unidades
                using (var cmd = new SqlCommand("SELECT ID, IDExtra FROM dbo.prd_ProductoUnidad WITH (NOLOCK) WHERE Eliminado = 0 AND IDExtra IS NOT NULL;", connForce))
                using (var r = await cmd.ExecuteReaderAsync(ct))
                    while (await r.ReadAsync(ct))
                        unidForceByExtra[r.GetInt32(1)] = r.GetInt32(0);

                // Tipos de Comprobante de Venta en Force (TipoCpVentas)
                using (var cmd = new SqlCommand("SELECT ID, Serie, Numero, ISNULL(IDComprobanteTipo, 2) FROM dbo.TipoCpVentas WITH (NOLOCK);", connForce))
                using (var r = await cmd.ExecuteReaderAsync(ct))
                {
                    while (await r.ReadAsync(ct))
                    {
                        string ser = r.GetString(1).Trim();
                        if (!string.IsNullOrEmpty(ser) && !tipoCpVentasBySerie.ContainsKey(ser))
                        {
                            tipoCpVentasBySerie[ser] = (r.GetInt32(0), r.GetInt32(2), r.GetInt32(3));
                        }
                    }
                }

                // Pedidos existentes en Force para enlace bidireccional y validación de estado FACTURADO
                using (var cmd = new SqlCommand("SELECT ID, PedidoMovilID, Numero, IDCpVenta, UPPER(LTRIM(RTRIM(ISNULL(Estado, '')))) AS Estado, IDCatalogo, IDModalidadCredito FROM dbo.ped_Pedido WITH (NOLOCK) WHERE Eliminado = 0;", connForce))
                using (var r = await cmd.ExecuteReaderAsync(ct))
                {
                    while (await r.ReadAsync(ct))
                    {
                        int idForce = r.GetInt32(0);
                        string movilId = r.IsDBNull(1) ? "" : r.GetString(1).Trim();
                        string num = r.IsDBNull(2) ? "" : r.GetString(2).Trim();
                        int? idCpVenta = r.IsDBNull(3) ? null : r.GetInt32(3);
                        string estado = r.IsDBNull(4) ? "" : r.GetString(4);
                        int? idCat = r.IsDBNull(5) ? null : r.GetInt32(5);
                        int? idModCred = r.IsDBNull(6) ? null : r.GetInt32(6);

                        if (!string.IsNullOrEmpty(movilId) && movilId.StartsWith("MIG-ERP-", StringComparison.OrdinalIgnoreCase))
                        {
                            if (int.TryParse(movilId.Substring(8), out int idOrig))
                                pedidosForcePorOrigen[idOrig] = (idForce, num, idCpVenta, estado, idCat, idModCred);
                        }

                        if (!string.IsNullOrEmpty(num) && !pedidosForcePorNumero.ContainsKey(num))
                            pedidosForcePorNumero[num] = (idForce, num, idCpVenta, estado, idCat, idModCred);
                    }
                }

                // Comprobantes existentes en Force con estado de anulación, pedido vinculado y deuda
                const string sqlForceCp = @"
                    SELECT 
                        cp.ID, 
                        cp.NumCp, 
                        CAST(ISNULL(cp.Anulado, 0) AS bit) AS Anulado, 
                        CAST(ISNULL(cp.Total, 0) AS decimal(18,6)) AS Total,
                        cp.IDPedido,
                        d.ID AS IDDeuda,
                        CAST(ISNULL(d.Saldo, 0) AS decimal(18,6)) AS SaldoDeuda,
                        ISNULL(d.Estado, '') AS EstadoDeuda
                    FROM dbo.Cp cp WITH (NOLOCK)
                    LEFT JOIN dbo.cob_DeudaCliente d WITH (NOLOCK) ON d.IDCpVenta = cp.ID AND d.Eliminado = 0
                    WHERE cp.Eliminado = 0 AND cp.NumCp IS NOT NULL;";

                using (var cmd = new SqlCommand(sqlForceCp, connForce))
                using (var r = await cmd.ExecuteReaderAsync(ct))
                {
                    while (await r.ReadAsync(ct))
                    {
                        int idCpF = r.GetInt32(0);
                        string numCp = r.GetString(1).Trim();
                        bool anulF = r.GetBoolean(2);
                        decimal totF = r.GetDecimal(3);
                        int? idPedF = r.IsDBNull(4) ? null : r.GetInt32(4);

                        cpPorIdForce[idCpF] = (numCp, anulF, idPedF);

                        if (!string.IsNullOrEmpty(numCp) && !comprobantesForceInfo.ContainsKey(numCp))
                        {
                            comprobantesForceInfo[numCp] = (
                                IDForce: idCpF,
                                Anulado: anulF,
                                Total: totF,
                                IDPedido: idPedF,
                                IDDeuda: r.IsDBNull(5) ? null : r.GetInt32(5),
                                SaldoDeuda: r.IsDBNull(6) ? null : r.GetDecimal(6),
                                EstadoDeuda: r.IsDBNull(7) ? "" : r.GetString(7)
                            );
                        }
                    }
                }
            }

            // Detección de Multi-comprobante VIGENTE por pedido origen (solo comprobantes NO anulados)
            var gruposPorPedido = comprobantes
                .Where(c => c.IDPedidoOrigen > 0 && !c.AnuladoOrigen)
                .GroupBy(c => c.IDPedidoOrigen)
                .ToDictionary(g => g.Key, g => g.OrderBy(x => x.IDCpOrigen).ToList());

            foreach (var kvp in gruposPorPedido)
            {
                int orden = 1;
                int total = kvp.Value.Count;
                foreach (var item in kvp.Value)
                {
                    item.TotalComprobantesDelPedido = total;
                    item.ComprobanteNumeroOrden = orden++;
                }
            }

            // 4. Mapeo y Diagnóstico
            foreach (var c in comprobantes)
            {
                var errores = new List<string>();
                var advertencias = new List<string>();

                // A. Ya existe en Force
                if (comprobantesForceInfo.TryGetValue(c.NumCp, out var forceInfo))
                {
                    c.YaExisteEnForce = true;
                    c.IDCpForceExistente = forceInfo.IDForce;
                    c.ForceIDPedido = forceInfo.IDPedido;
                    c.ForceAnulado = forceInfo.Anulado;
                    c.ForceTotal = forceInfo.Total;
                    c.IDDeudaForce = forceInfo.IDDeuda;
                    c.ForceSaldoDeuda = forceInfo.SaldoDeuda;
                    c.ForceEstadoDeuda = forceInfo.EstadoDeuda;
                }

                // B. Cliente
                if (cliForceByExtra.TryGetValue(c.IDClienteOrigen, out int idCliForce))
                    c.IDClienteForce = idCliForce;
                else
                    errores.Add($"Cliente origen #{c.IDClienteOrigen} ({c.CodigoCliente}) sin IDExtra en cli_Cliente");

                // C. Vendedor
                if (vendForceByExtra.TryGetValue(c.IDVendedorOrigen, out int idVendForce))
                    c.IDVendedorForce = idVendForce;
                else
                    errores.Add($"Vendedor origen #{c.IDVendedorOrigen} ({c.CodigoVendedor}) sin IDExtra en vnd_Vendedor");

                // D. Almacén y Sucursal
                if (almForceByExtra.TryGetValue(c.IDAlmacenOrigen, out int idAlmForce))
                    c.IDAlmacenForce = idAlmForce;
                else
                    c.IDAlmacenForce = 169; // Almacén Central por defecto si no mapea

                if (sucForceByExtra.TryGetValue(c.IDSucursalOrigen, out int idSucForce))
                    c.IDSucursalForce = idSucForce;
                else
                    c.IDSucursalForce = 5; // Sucursal por defecto

                // E. TipoCpVenta (Serie)
                if (tipoCpVentasBySerie.TryGetValue(c.SerieOrigen, out var tcv))
                {
                    c.IDTipoCpVentaForce = tcv.ID;
                    c.SerieForce = c.SerieOrigen;
                }
                else
                {
                    advertencias.Add($"Serie '{c.SerieOrigen}' no existe en TipoCpVentas (se auto-aprovisionará al subir)");
                }

                // F. Pedido Relacionado (Búsqueda en pedidos subidos a Force)
                int? idCpVentaActualPedidoForce = null;
                if (c.IDPedidoOrigen > 0 && pedidosForcePorOrigen.TryGetValue(c.IDPedidoOrigen, out var pedF))
                {
                    c.IDPedidoForce = pedF.IDForce;
                    c.NumPedidoForce = pedF.NumeroForce;
                    c.EstadoPedidoForce = pedF.EstadoForce;
                    c.IDCatalogoForce = pedF.IDCatalogo;
                    c.IDModalidadCreditoForce = pedF.IDModalidadCredito;
                    idCpVentaActualPedidoForce = pedF.IDCpVenta;
                }
                else if (!string.IsNullOrEmpty(c.NumPedidoOrigen) && pedidosForcePorNumero.TryGetValue(c.NumPedidoOrigen, out var pedFNum))
                {
                    c.IDPedidoForce = pedFNum.IDForce;
                    c.NumPedidoForce = pedFNum.NumeroForce;
                    c.EstadoPedidoForce = pedFNum.EstadoForce;
                    c.IDCatalogoForce = pedFNum.IDCatalogo;
                    c.IDModalidadCreditoForce = pedFNum.IDModalidadCredito;
                    idCpVentaActualPedidoForce = pedFNum.IDCpVenta;
                }
                else
                {
                    if (c.IDPedidoOrigen > 0)
                        advertencias.Add($"Pedido de origen #{c.IDPedidoOrigen} ({c.NumPedidoOrigen}) aún no ha sido migrado a Force");
                    else
                        advertencias.Add("Venta directa sin pedido relacionado");
                }

                if (c.IDPedidoForce.HasValue && !string.Equals(c.EstadoPedidoForce, "FACTURADO", StringComparison.OrdinalIgnoreCase))
                {
                    if (!c.AnuladoOrigen)
                        advertencias.Add($"Pedido #{c.IDPedidoForce} en Force está en '{c.EstadoPedidoForce}' (pasará a FACTURADO al enlazar comprobante)");
                    else
                        advertencias.Add($"Pedido #{c.IDPedidoForce} en Force está en estado '{c.EstadoPedidoForce}'");
                }

                if (c.EsReemplazoDeAnulado)
                {
                    advertencias.Add($"🔄 Reemplaza a Cp anulado ({c.ComprobantesAnuladosDelPedido}) del Pedido #{c.IDPedidoForce ?? c.IDPedidoOrigen}");
                }

                if (c.EsMulticomprobante)
                {
                    advertencias.Add($"Multi-comprobante vigente: #{c.ComprobanteNumeroOrden} de {c.TotalComprobantesDelPedido} del pedido #{c.IDPedidoForce ?? c.IDPedidoOrigen}");
                }

                // G. Validar Ítems
                foreach (var it in c.Items)
                {
                    if (prodForceByExtra.TryGetValue(it.IDProductoOrigen, out int idProdForce))
                        it.IDProductoForce = idProdForce;
                    else
                        errores.Add($"Producto '{it.CodigoProducto}' ({it.DescripcionProducto}) sin IDExtra en prd_Producto");

                    if (unidForceByExtra.TryGetValue(it.IDUnidadOrigen, out int idUnidForce))
                        it.IDUnidadForce = idUnidForce;
                    else
                        advertencias.Add($"Unidad #{it.IDUnidadOrigen} ({it.UnidadAbrev}) de '{it.CodigoProducto}' sin IDExtra en prd_ProductoUnidad");
                }

                // Verificar si el pedido en Force tiene su enlace roto o apuntando a un comprobante anulado
                bool necesitaReenlacePedido = false;
                if (!c.AnuladoOrigen && c.IDPedidoForce.HasValue)
                {
                    bool cpSinIdPedido = c.YaExisteEnForce && c.ForceIDPedido != c.IDPedidoForce;
                    bool pedidoSinCpVigente = !idCpVentaActualPedidoForce.HasValue || idCpVentaActualPedidoForce.Value == 0 ||
                                              (cpPorIdForce.TryGetValue(idCpVentaActualPedidoForce.Value, out var cpVinc) && cpVinc.Anulado);
                    bool pedidoNoFacturado = !string.Equals(c.EstadoPedidoForce, "FACTURADO", StringComparison.OrdinalIgnoreCase);

                    // También verificar si algún comprobante anulado previo de este pedido sigue activo (Anulado=0) en Force
                    bool tieneAnuladoPrevioActivoEnForce = false;
                    if (c.EsReemplazoDeAnulado && !string.IsNullOrWhiteSpace(c.ComprobantesAnuladosDelPedido))
                    {
                        foreach (var numPrev in c.ComprobantesAnuladosDelPedido.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        {
                            if (comprobantesForceInfo.TryGetValue(numPrev, out var infoPrev) && !infoPrev.Anulado)
                            {
                                tieneAnuladoPrevioActivoEnForce = true;
                                break;
                            }
                        }
                    }

                    if (cpSinIdPedido || pedidoSinCpVigente || pedidoNoFacturado || tieneAnuladoPrevioActivoEnForce)
                    {
                        necesitaReenlacePedido = true;
                    }
                }

                // H. Semáforo y Determinación de Operación (INSERT, UPDATE_DEUDA, REENLAZAR_PEDIDO, ANULAR, SIN_CAMBIOS)
                if (c.YaExisteEnForce)
                {
                    if (c.AnuladoOrigen)
                    {
                        if (!c.ForceAnulado)
                        {
                            c.TipoOperacion = "ANULAR";
                            c.Semaforo = "AMBAR";
                            string mot = !string.IsNullOrWhiteSpace(c.MotivoAnulacion) ? $" (Motivo: {c.MotivoAnulacion})" : "";
                            c.ObservacionValidacion = $"⚠ Anulado en ERP Antiguo{mot}. Listo para marcar Anulado=1, cancelar deuda y desenlazar del pedido en Force.";
                        }
                        else
                        {
                            c.TipoOperacion = "SIN_CAMBIOS";
                            c.Semaforo = "AZUL";
                            c.ObservacionValidacion = $"ℹ Ya está anulado en Force (Cp ID: {c.IDCpForceExistente}). Al día.";
                        }
                    }
                    else
                    {
                        // En origen NO está anulado. ¿Cambió el saldo de la deuda o requiere re-enlazar pedido por reemplazo de anulado?
                        bool requiereUpdateDeuda = false;
                        string msgDeuda = "";

                        if (c.EsCredito)
                        {
                            decimal saldoOrigen = c.SaldoPendiente;
                            decimal saldoForce = c.ForceSaldoDeuda ?? 0;

                            if (c.IDDeudaForce == null && saldoOrigen > 0)
                            {
                                requiereUpdateDeuda = true;
                                msgDeuda = $"✓ Deuda pendiente no registrada en Force. Listo para registrar deuda S/ {saldoOrigen:N2}.";
                            }
                            else if (c.IDDeudaForce != null && Math.Abs(saldoForce - saldoOrigen) > 0.01m)
                            {
                                requiereUpdateDeuda = true;
                                string estadoCobranza = saldoOrigen <= 0 ? "CANCELADO" : (saldoOrigen < c.Total ? "PARCIAL" : "PENDIENTE");
                                msgDeuda = $"✓ Cobranza en ERP Antiguo: Saldo Force S/ {saldoForce:N2} -> Nuevo Saldo S/ {saldoOrigen:N2} [{estadoCobranza}].";
                            }
                        }

                        if (requiereUpdateDeuda)
                        {
                            c.TipoOperacion = "UPDATE_DEUDA";
                            c.Semaforo = "VERDE";
                            string extraReenlace = necesitaReenlacePedido ? $" Además re-enlazará Pedido #{c.IDPedidoForce}." : "";
                            c.ObservacionValidacion = msgDeuda + extraReenlace;
                        }
                        else if (necesitaReenlacePedido)
                        {
                            c.TipoOperacion = "REENLAZAR_PEDIDO";
                            c.Semaforo = "VERDE";
                            string reempTxt = c.EsReemplazoDeAnulado ? $" (reemplazando a {c.ComprobantesAnuladosDelPedido})" : "";
                            c.ObservacionValidacion = $"✓ Re-enlazar con Pedido Force #{c.IDPedidoForce}{reempTxt} y actualizar IDCpVenta vigente.";
                        }
                        else
                        {
                            c.TipoOperacion = "SIN_CAMBIOS";
                            c.Semaforo = "AZUL";
                            string infoCred = c.EsCredito ? $" con deuda al día (Saldo S/ {(c.ForceSaldoDeuda ?? 0):N2})" : " (Contado)";
                            c.ObservacionValidacion = $"ℹ Ya existe en Force (Cp ID: {c.IDCpForceExistente}){infoCred}. Al día.";
                        }
                    }
                }
                else
                {
                    if (c.AnuladoOrigen)
                    {
                        c.TipoOperacion = "SIN_CAMBIOS";
                        c.Semaforo = "AZUL";
                        c.ObservacionValidacion = "ℹ Comprobante anulado en origen que nunca subió a Force. Se omite.";
                    }
                    else
                    {
                        c.TipoOperacion = "INSERT";
                        if (errores.Count > 0)
                        {
                            c.Semaforo = "ROJO";
                            c.ObservacionValidacion = "✕ " + string.Join(" | ", errores.Distinct());
                        }
                        else if (advertencias.Count > 0)
                        {
                            c.Semaforo = "AMBAR";
                            c.ObservacionValidacion = "⚠ Listo (Obs: " + string.Join("; ", advertencias.Distinct()) + ")";
                        }
                        else
                        {
                            c.Semaforo = "VERDE";
                            string multiTag = c.EsMulticomprobante ? $" [Comp {c.ComprobanteNumeroOrden}/{c.TotalComprobantesDelPedido}]" : "";
                            string reempTag = c.EsReemplazoDeAnulado ? $" [🔄 Reemplaza a {c.ComprobantesAnuladosDelPedido}]" : "";
                            c.ObservacionValidacion = $"✓ 100% Alineado — Enlazado a Pedido Force #{c.IDPedidoForce} (FACTURADO){multiTag}{reempTag}";
                        }
                    }
                }
            }

            // Si el filtro exige comprobantes con Pedido en Force:
            // Incluye pedidos FACTURADO, o PENDIENTE (ej. cuando se anularon sus comprobantes previos y tiene nuevos de reemplazo), o anulaciones pendientes en Force.
            if (filtro.SoloPedidosFacturadosEnForce)
            {
                comprobantes = comprobantes
                    .Where(c =>
                        (c.IDPedidoForce.HasValue && (
                            string.Equals(c.EstadoPedidoForce, "FACTURADO", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(c.EstadoPedidoForce, "PENDIENTE", StringComparison.OrdinalIgnoreCase) ||
                            c.TipoOperacion == "ANULAR")) ||
                        c.TipoOperacion == "ANULAR")
                    .ToList();
            }

            _ultimoAnalisis = comprobantes;

            sw.Stop();
            response.TiempoSegundos = Math.Round(sw.Elapsed.TotalSeconds, 2);
            response.TotalEncontrados = comprobantes.Count;
            response.ListosParaSubir = comprobantes.Count(x => x.ListoParaSubir);
            response.NuevosAInsertar = comprobantes.Count(x => x.TipoOperacion == "INSERT" && x.ListoParaSubir);
            response.DeudasAActualizar = comprobantes.Count(x => (x.TipoOperacion == "UPDATE_DEUDA" || x.TipoOperacion == "REENLAZAR_PEDIDO") && x.ListoParaSubir);
            response.AnulacionesPendientes = comprobantes.Count(x => x.TipoOperacion == "ANULAR" && x.ListoParaSubir);
            response.ReemplazosDeAnulados = comprobantes.Count(x => x.EsReemplazoDeAnulado);
            response.YaExistenEnForce = comprobantes.Count(x => x.YaExisteEnForce);
            response.SinPedidoEnlazado = comprobantes.Count(x => !x.YaExisteEnForce && !x.IDPedidoForce.HasValue);
            response.ObservadosConError = comprobantes.Count(x => !x.YaExisteEnForce && x.Semaforo == "ROJO");
            response.Anulados = comprobantes.Count(x => x.AnuladoOrigen);
            response.FacturasTotal = comprobantes.Count(x => x.IDTipoComprobantePagoOrigen == 1);
            response.BoletasTotal = comprobantes.Count(x => x.IDTipoComprobantePagoOrigen == 4);
            response.ConDeudaPorCobrar = comprobantes.Count(x => x.DebeGenerarDeuda);
            response.TotalDeudaPendiente = comprobantes.Where(x => x.DebeGenerarDeuda).Sum(x => x.SaldoPendiente);
            response.Comprobantes = comprobantes;

            return response;
        }

        // =========================================================================
        // 2. SUBIDA DE COMPROBANTES A FORCE CON ENLACE AUTOMÁTICO A PEDIDOS
        // =========================================================================
        public async Task EjecutarSubidaComprobantesAsync(List<int> idsCpOrigen, CancellationToken ct = default)
        {
            if (!await _execLock.WaitAsync(0, ct))
            {
                _logger.LogWarning("Ya existe una subida de comprobantes en curso.");
                return;
            }

            try
            {
                // Ordenar para que PRIMERO se ejecuten las ANULACIONES (liberando IDCpVenta de pedidos) y LUEGO las INSERCIONES/REEMPLAZOS
                var comprobantesASubir = _ultimoAnalisis
                    .Where(c => idsCpOrigen.Contains(c.IDCpOrigen))
                    .OrderBy(c => c.TipoOperacion == "ANULAR" ? 0 : (c.TipoOperacion == "INSERT" ? 1 : 2))
                    .ThenBy(c => c.IDCpOrigen)
                    .ToList();

                _status.IsRunning = true;
                _status.EstadoActual = "EJECUTANDO";
                _status.ProgresoActual = 0;
                _status.ProgresoTotal = comprobantesASubir.Count;
                _status.TotalSubidos = 0;
                _status.TotalActualizados = 0;
                _status.TotalAnulados = 0;
                _status.TotalDeudasGeneradas = 0;
                _status.TotalOmitidos = 0;
                _status.TotalErrores = 0;
                _status.EventosRecientes.Clear();

                if (comprobantesASubir.Count == 0)
                {
                    _status.FaseDetalle = "No se encontraron comprobantes seleccionados en memoria. Vuelva a consultar.";
                    _status.EstadoActual = "COMPLETADO";
                    return;
                }

                using var connForce = new SqlConnection(_connectionStringForce);
                await connForce.OpenAsync(ct);

                // BLINDAJE DE STOCK: Desactivar triggers en Force para que la carga de comprobantes NO altere stock
                _status.FaseDetalle = "🔒 Protegiendo stock (Desactivando triggers de stock en Force)...";
                AgregarEvento(0, "SISTEMA", "INFO", "🔒 Deshabilitando trg_PedidoItem_ReservarStock y trg_Pedido_DescontarStock para garantizar stock intacto.");

                const string sqlDisableTriggers = @"
                    IF EXISTS (SELECT 1 FROM sys.triggers WHERE name = 'trg_PedidoItem_ReservarStock')
                        DISABLE TRIGGER dbo.trg_PedidoItem_ReservarStock ON dbo.ped_PedidoItem;
                    IF EXISTS (SELECT 1 FROM sys.triggers WHERE name = 'trg_Pedido_DescontarStock')
                        DISABLE TRIGGER dbo.trg_Pedido_DescontarStock ON dbo.ped_Pedido;";

                using (var cmdDis = new SqlCommand(sqlDisableTriggers, connForce))
                {
                    await cmdDis.ExecuteNonQueryAsync(ct);
                }

                try
                {
                    foreach (var c in comprobantesASubir)
                    {
                        if (ct.IsCancellationRequested) break;
                        _status.ProgresoActual++;
                        _status.FaseDetalle = $"Procesando comprobante {c.NumCp} ({_status.ProgresoActual}/{_status.ProgresoTotal})...";

                        if (c.TipoOperacion == "SIN_CAMBIOS")
                        {
                            _status.TotalOmitidos++;
                            AgregarEvento(c.IDCpOrigen, c.NumCp, "INFO", $"Omitido: Ya existe en Force y se encuentra al día (Cp ID: {c.IDCpForceExistente}).");
                            continue;
                        }

                        if (c.Semaforo == "ROJO")
                        {
                            _status.TotalErrores++;
                            AgregarEvento(c.IDCpOrigen, c.NumCp, "ERROR", $"Rechazado por maestros faltantes: {c.ObservacionValidacion}");
                            continue;
                        }

                        using var tx = connForce.BeginTransaction();
                        try
                        {
                            // ESCENARIO 1: ANULACIÓN DE COMPROBANTE EN FORCE, CANCELACIÓN DE DEUDA Y REAJUSTE DE PEDIDO
                            if (c.TipoOperacion == "ANULAR")
                            {
                                int idCp = c.IDCpForceExistente!.Value;
                                string mot = !string.IsNullOrWhiteSpace(c.MotivoAnulacion) ? c.MotivoAnulacion : "Anulado en ERP Antiguo";

                                const string sqlAnulCp = @"
                                    UPDATE dbo.Cp
                                    SET Anulado = 1,
                                        HoraAnulacion = GETDATE(),
                                        Observacion = ISNULL(Observacion, '') + ' [ANULADO: ' + @Motivo + ']'
                                    WHERE ID = @IDCp;";
                                using (var cmdAnul = new SqlCommand(sqlAnulCp, connForce, tx))
                                {
                                    cmdAnul.Parameters.AddWithValue("@IDCp", idCp);
                                    cmdAnul.Parameters.AddWithValue("@Motivo", mot);
                                    await cmdAnul.ExecuteNonQueryAsync(ct);
                                }

                                const string sqlAnulIt = @"
                                    UPDATE dbo.ItemCp
                                    SET Anulado = 1
                                    WHERE IDCp = @IDCp;";
                                using (var cmdAnulIt = new SqlCommand(sqlAnulIt, connForce, tx))
                                {
                                    cmdAnulIt.Parameters.AddWithValue("@IDCp", idCp);
                                    await cmdAnulIt.ExecuteNonQueryAsync(ct);
                                }

                                const string sqlAnulDeuda = @"
                                    UPDATE dbo.cob_DeudaCliente
                                    SET Eliminado = 1,
                                        Estado = 'ANULADO',
                                        FechaEliminacion = GETDATE(),
                                        MotivoEliminacion = @Motivo,
                                        UsuarioEliminacion = 0
                                    WHERE IDCpVenta = @IDCp;";
                                using (var cmdAnulDeuda = new SqlCommand(sqlAnulDeuda, connForce, tx))
                                {
                                    cmdAnulDeuda.Parameters.AddWithValue("@IDCp", idCp);
                                    cmdAnulDeuda.Parameters.AddWithValue("@Motivo", mot);
                                    await cmdAnulDeuda.ExecuteNonQueryAsync(ct);
                                }

                                // Si el comprobante anulado estaba vinculado a un pedido en Force, actualizar el vínculo del pedido:
                                // Si queda otro comprobante vigente en ese pedido, asignar ese; si no queda ninguno, limpiar IDCpVenta y pasar a PENDIENTE.
                                int? idPedAsociado = c.IDPedidoForce ?? c.ForceIDPedido;
                                if (idPedAsociado.HasValue && idPedAsociado.Value > 0)
                                {
                                    const string sqlReajustarPed = @"
                                        DECLARE @PrimerCpActivo int;
                                        SELECT TOP 1 @PrimerCpActivo = ID
                                        FROM dbo.Cp
                                        WHERE IDPedido = @IDPedido AND ISNULL(Anulado, 0) = 0 AND ISNULL(Eliminado, 0) = 0
                                        ORDER BY ID ASC;

                                        IF @PrimerCpActivo IS NOT NULL
                                        BEGIN
                                            UPDATE dbo.ped_Pedido
                                            SET IDCpVenta = @PrimerCpActivo,
                                                Estado = 'FACTURADO',
                                                FechaModificacion = GETDATE()
                                            WHERE ID = @IDPedido;
                                        END
                                        ELSE
                                        BEGIN
                                            UPDATE dbo.ped_Pedido
                                            SET IDCpVenta = NULL,
                                                Estado = CASE WHEN Estado = 'ANULADO' THEN 'ANULADO' ELSE 'PENDIENTE' END,
                                                FechaModificacion = GETDATE()
                                            WHERE ID = @IDPedido;
                                        END";
                                    using var cmdReajPed = new SqlCommand(sqlReajustarPed, connForce, tx);
                                    cmdReajPed.Parameters.AddWithValue("@IDPedido", idPedAsociado.Value);
                                    await cmdReajPed.ExecuteNonQueryAsync(ct);
                                }

                                await tx.CommitAsync(ct);
                                _status.TotalAnulados++;
                                c.ForceAnulado = true;
                                c.Semaforo = "AZUL";
                                c.TipoOperacion = "SIN_CAMBIOS";
                                c.ObservacionValidacion = $"✓ Anulado exitosamente en Force (Cp #{idCp}). Deuda cancelada y pedido actualizado.";
                                AgregarEvento(c.IDCpOrigen, c.NumCp, "WARN", $"⚠ Anulado en Force (Cp #{idCp}) | Motivo: {mot} | Deuda cancelada.");
                            }
                            // ESCENARIO 2: ACTUALIZACIÓN DE DEUDA Y/O RE-ENLACE CON PEDIDO (POR REEMPLAZO DE COMPROBANTE ANULADO)
                            else if (c.TipoOperacion == "UPDATE_DEUDA" || c.TipoOperacion == "REENLAZAR_PEDIDO")
                            {
                                int idCp = c.IDCpForceExistente!.Value;
                                decimal nuevoSaldo = Math.Max(0, Math.Min(c.SaldoPendiente, c.Total));
                                string nuevoEstado = (nuevoSaldo <= 0) ? "CANCELADO" : (nuevoSaldo < c.Total ? "PARCIAL" : "PENDIENTE");

                                if (c.EsCredito)
                                {
                                    DateTime fechaVenc = c.FechaVencimiento ?? c.FechaEmision;
                                    DateTime fechaProg = c.FechaProgramacionPago ?? fechaVenc;

                                    const string sqlUpdDeuda = @"
                                        IF EXISTS (SELECT 1 FROM dbo.cob_DeudaCliente WHERE IDCpVenta = @IDCpVenta AND Eliminado = 0)
                                        BEGIN
                                            UPDATE dbo.cob_DeudaCliente
                                            SET Saldo = @Saldo,
                                                Estado = @Estado,
                                                IDPedido = ISNULL(@IDPedido, IDPedido),
                                                FechaModificacion = GETDATE(),
                                                UsuarioModificador = 'INTEGRADOR'
                                            WHERE IDCpVenta = @IDCpVenta AND Eliminado = 0;
                                        END
                                        ELSE IF @Saldo > 0
                                        BEGIN
                                            INSERT INTO dbo.cob_DeudaCliente (
                                                IDCliente, IDCatalogo, IDPedido, IDCpVenta, IDMoneda, IDModalidadCredito,
                                                IDMedioPago, IDVendedor, Total, Saldo, MontoDetraccion,
                                                FechaEmision, FechaVencimiento, FechaProgramacionPago,
                                                TipoCp, Credito, MovSaldo, SujetoADetraccion, SujetoAPercepcion,
                                                Estado, UsuarioCreador, FechaCreacion, Eliminado
                                            ) VALUES (
                                                @IDCliente, @IDCatalogo, @IDPedido, @IDCpVenta, 1, @IDModalidadCredito,
                                                NULL, @IDVendedor, @Total, @Saldo, 0,
                                                @FechaEmision, @FechaVencimiento, @FechaProgramacionPago,
                                                'Cobrar', 1, 1, 0, 0,
                                                @Estado, '0', GETDATE(), 0
                                            );
                                        END";

                                    using (var cmdDeuda = new SqlCommand(sqlUpdDeuda, connForce, tx))
                                    {
                                        cmdDeuda.Parameters.AddWithValue("@IDCpVenta", idCp);
                                        cmdDeuda.Parameters.AddWithValue("@IDCliente", c.IDClienteForce ?? (object)DBNull.Value);
                                        cmdDeuda.Parameters.AddWithValue("@IDCatalogo", (object?)c.IDCatalogoForce ?? DBNull.Value);
                                        cmdDeuda.Parameters.AddWithValue("@IDPedido", (object?)c.IDPedidoForce ?? DBNull.Value);
                                        cmdDeuda.Parameters.AddWithValue("@IDModalidadCredito", (object?)c.IDModalidadCreditoForce ?? DBNull.Value);
                                        cmdDeuda.Parameters.AddWithValue("@IDVendedor", c.IDVendedorForce ?? (object)DBNull.Value);
                                        cmdDeuda.Parameters.AddWithValue("@Total", c.Total);
                                        cmdDeuda.Parameters.AddWithValue("@Saldo", nuevoSaldo);
                                        cmdDeuda.Parameters.AddWithValue("@FechaEmision", c.FechaEmision);
                                        cmdDeuda.Parameters.AddWithValue("@FechaVencimiento", fechaVenc);
                                        cmdDeuda.Parameters.AddWithValue("@FechaProgramacionPago", fechaProg);
                                        cmdDeuda.Parameters.AddWithValue("@Estado", nuevoEstado);

                                        await cmdDeuda.ExecuteNonQueryAsync(ct);
                                    }
                                }

                                // Si tiene pedido en Force, asegurar enlace bidireccional y anular en Force cualquier comprobante previo anulado de ese pedido
                                if (c.IDPedidoForce.HasValue && c.IDPedidoForce.Value > 0)
                                {
                                    await SincronizarEnlacePedidoYAnuladosPreviosAsync(connForce, tx, c, idCp, ct);
                                }

                                await tx.CommitAsync(ct);
                                _status.TotalActualizados++;
                                c.ForceSaldoDeuda = nuevoSaldo;
                                c.ForceEstadoDeuda = nuevoEstado;
                                c.ForceIDPedido = c.IDPedidoForce;
                                c.Semaforo = "AZUL";
                                string opRealizada = c.TipoOperacion;
                                c.TipoOperacion = "SIN_CAMBIOS";
                                if (opRealizada == "REENLAZAR_PEDIDO")
                                {
                                    c.ObservacionValidacion = $"✓ Enlazado correctamente a Pedido Force #{c.IDPedidoForce} (Cp #{idCp}).";
                                    AgregarEvento(c.IDCpOrigen, c.NumCp, "SUCCESS", $"🔗 Re-enlazado Cp #{idCp} ({c.NumCp}) con Pedido Force #{c.IDPedidoForce}.");
                                }
                                else
                                {
                                    c.ObservacionValidacion = $"✓ Cobranza sincronizada en Force (Cp #{idCp}) | Saldo: S/ {nuevoSaldo:N2} [{nuevoEstado}].";
                                    AgregarEvento(c.IDCpOrigen, c.NumCp, "SUCCESS", $"✎ Cobranza sincronizada Cp #{idCp} ({c.NumCp}) | Saldo: S/ {nuevoSaldo:N2} [{nuevoEstado}].");
                                }
                            }
                            // ESCENARIO 3: INSERCIÓN DE NUEVO COMPROBANTE CON CORRELATIVO Y ENLACE
                            else
                            {
                                // 1. Auto-provisionar TipoCpVentas si la serie no existe aún
                                int idTipoCpFinal;
                                if (c.IDTipoCpVentaForce.HasValue && c.IDTipoCpVentaForce.Value > 0)
                                {
                                    idTipoCpFinal = c.IDTipoCpVentaForce.Value;
                                }
                                else
                                {
                                    const string sqlEnsureTipo = @"
                                        DECLARE @IDExistente int;
                                        SELECT @IDExistente = ID FROM dbo.TipoCpVentas WITH (UPDLOCK, ROWLOCK) WHERE Serie = @Serie;

                                        IF @IDExistente IS NOT NULL
                                        BEGIN
                                            SELECT @IDExistente;
                                        END
                                        ELSE
                                        BEGIN
                                            INSERT INTO dbo.TipoCpVentas (
                                                Codigo, Descripcion, Abreviacion, Numero, Serie, LongitudNumero, LongitudSerie,
                                                NumeracionAutomatica, MaximoNumeroItems, PeriodoAnulacion, PermitirFechasFuturas, PermitirFechasPasadas,
                                                Activo, RequiereCliente, RestringirAlmacen, FueraDeRuta, AplicaDistribucion, AplicaRedondeo,
                                                MontoMinimo, NumeroPrecisionRedondeo, FechaCreacion, IDComprobanteTipo, IDImpuesto,
                                                TieneImpuesto, PreciosIncluyenImpuesto
                                            ) VALUES (
                                                @Codigo, @Descripcion, @Abreviacion, @Numero, @Serie, 8, 4,
                                                0, 500, 7, 0, 1,
                                                1, 1, 0, 0, 1, 1,
                                                0.00, 2, GETDATE(), @IDComprobanteTipo, 1,
                                                1, 1
                                            );
                                            SELECT CAST(SCOPE_IDENTITY() AS int);
                                        END";

                                    bool esFactura = c.IDTipoComprobantePagoOrigen == 1;
                                    using (var cmdTipo = new SqlCommand(sqlEnsureTipo, connForce, tx))
                                    {
                                        cmdTipo.Parameters.AddWithValue("@Serie", c.SerieOrigen);
                                        cmdTipo.Parameters.AddWithValue("@Codigo", $"{(esFactura ? "FACTURA" : "BOLETA")}-{c.SerieOrigen}");
                                        cmdTipo.Parameters.AddWithValue("@Descripcion", $"{(esFactura ? "Factura Electrónica" : "Boleta Electrónica")} {c.SerieOrigen}");
                                        cmdTipo.Parameters.AddWithValue("@Abreviacion", esFactura ? "FAC" : "BOL");
                                        cmdTipo.Parameters.AddWithValue("@Numero", c.CorrelativoOrigen);
                                        cmdTipo.Parameters.AddWithValue("@IDComprobanteTipo", esFactura ? 1 : 2);

                                        idTipoCpFinal = (int)(await cmdTipo.ExecuteScalarAsync(ct))!;
                                    }
                                }

                                // 2. Insertar Cabecera en dbo.Cp
                                const string sqlInsertCp = @"
                                    INSERT INTO dbo.Cp (
                                        IDTipoCp, NumCp, NumCpContable, IDPedido, IDCliente, IDVendedor, IDSucursal, IDMoneda,
                                        Fecha, FechaEmision, Hora, Total, SubTotal, ValorVenta, ValorAfecto, ValorInafecto, ValorExonerado, ValorDescuento,
                                        TieneImpuesto, IDImpuesto, PreciosIncImpto, EsCredito, Anulado, Observacion, Eliminado
                                    ) VALUES (
                                        @IDTipoCp, @NumCp, @NumCpContable, @IDPedido, @IDCliente, @IDVendedor, @IDSucursal, 1,
                                        @Fecha, @FechaEmision, @Hora, @Total, @SubTotal, @ValorVenta, @ValorAfecto, @ValorInafecto, @ValorExonerado, @ValorDescuento,
                                        @TieneImpuesto, 1, @PreciosIncImpto, @EsCredito, @Anulado, @Observacion, 0
                                    );
                                    SELECT CAST(SCOPE_IDENTITY() AS int);";

                                int nuevoIdCp;
                                using (var cmdCp = new SqlCommand(sqlInsertCp, connForce, tx))
                                {
                                    cmdCp.Parameters.AddWithValue("@IDTipoCp", idTipoCpFinal);
                                    cmdCp.Parameters.AddWithValue("@NumCp", c.NumCp);
                                    cmdCp.Parameters.AddWithValue("@NumCpContable", string.IsNullOrEmpty(c.NumCpContable) ? (object)DBNull.Value : c.NumCpContable);
                                    cmdCp.Parameters.AddWithValue("@IDPedido", (object?)c.IDPedidoForce ?? DBNull.Value);
                                    cmdCp.Parameters.AddWithValue("@IDCliente", c.IDClienteForce!.Value);
                                    cmdCp.Parameters.AddWithValue("@IDVendedor", c.IDVendedorForce!.Value);
                                    cmdCp.Parameters.AddWithValue("@IDSucursal", c.IDSucursalForce ?? 5);
                                    cmdCp.Parameters.AddWithValue("@Fecha", c.Fecha);
                                    cmdCp.Parameters.AddWithValue("@FechaEmision", c.FechaEmision);
                                    cmdCp.Parameters.AddWithValue("@Hora", c.Hora);
                                    cmdCp.Parameters.AddWithValue("@Total", c.Total);
                                    cmdCp.Parameters.AddWithValue("@SubTotal", c.SubTotal);
                                    cmdCp.Parameters.AddWithValue("@ValorVenta", c.ValorVenta);
                                    cmdCp.Parameters.AddWithValue("@ValorAfecto", c.ValorAfecto);
                                    cmdCp.Parameters.AddWithValue("@ValorInafecto", c.ValorInafecto);
                                    cmdCp.Parameters.AddWithValue("@ValorExonerado", c.ValorExonerado);
                                    cmdCp.Parameters.AddWithValue("@ValorDescuento", c.ValorDescuento);
                                    cmdCp.Parameters.AddWithValue("@TieneImpuesto", c.TieneImpuesto);
                                    cmdCp.Parameters.AddWithValue("@PreciosIncImpto", c.PreciosIncImpto);
                                    cmdCp.Parameters.AddWithValue("@EsCredito", c.EsCredito);
                                    cmdCp.Parameters.AddWithValue("@Anulado", c.AnuladoOrigen);
                                    cmdCp.Parameters.AddWithValue("@Observacion", $"Migrado ERP Antiguo (PKID: {c.IDCpOrigen})");

                                    nuevoIdCp = (int)(await cmdCp.ExecuteScalarAsync(ct))!;
                                }

                                // 3. Insertar Extensión en dbo.CpVenta
                                const string sqlInsertCpVenta = @"
                                    INSERT INTO dbo.CpVenta (
                                        ID, IDTipoOperacion, IDAlmacen, TipoCpInventario, EstadoEntrega, EstadoDevolucion,
                                        EstadoEntregaDevolucion, EstadoDescuento, Direccion, DireccionEntrega, IDDireccionEntrega,
                                        IDSUNATCatalogo51, DetraccionMN, PercepcionMN, AplicaDistribucion, OmitirPoliticasCredito
                                    ) VALUES (
                                        @ID, 1, @IDAlmacen, 'Salida', @EstadoEntrega, 'PENDIENTE',
                                        'PENDIENTE', 'PENDIENTE', @Direccion, @DireccionEntrega, NULL,
                                        '6', 0, 0, 1, 0
                                    );";

                                using (var cmdVenta = new SqlCommand(sqlInsertCpVenta, connForce, tx))
                                {
                                    cmdVenta.Parameters.AddWithValue("@ID", nuevoIdCp);
                                    cmdVenta.Parameters.AddWithValue("@IDAlmacen", c.IDAlmacenForce ?? 169);
                                    cmdVenta.Parameters.AddWithValue("@EstadoEntrega", string.IsNullOrEmpty(c.EstadoEntrega) ? "PENDIENTE" : c.EstadoEntrega);
                                    cmdVenta.Parameters.AddWithValue("@Direccion", string.IsNullOrEmpty(c.DireccionFiscal) ? (object)DBNull.Value : c.DireccionFiscal);
                                    cmdVenta.Parameters.AddWithValue("@DireccionEntrega", string.IsNullOrEmpty(c.DireccionEntrega) ? (object)DBNull.Value : c.DireccionEntrega);

                                    await cmdVenta.ExecuteNonQueryAsync(ct);
                                }

                                // 4. Insertar Ítems en dbo.ItemCp (esquema real 192.168.2.93)
                                const string sqlInsertItem = @"
                                    INSERT INTO dbo.ItemCp (
                                        IDCp, Cantidad, IDProducto, IDUnidad, Factor, IDAlmacen, Descripcion,
                                        Total, ValorUnitario, PorcentajeImpto, Inafecto, Bonificacion,
                                        ValorAfecto, ValorVenta, SubTotal, ValorDescuento, ValorInafecto,
                                        BonificacionManual, DescuentoPieCp, RecargoPieCp,
                                        PorcentajePercepcion, MontoPercepcion, PorcentajeDetraccion, MontoDetraccion, ValorReferencialDetraccion,
                                        Anulado, ValorExonerado, TotalAnticipo,
                                        AplicaDescuentoConVale, EscalaPreferencial, DescuentoAplicado, DescuentoFijo,
                                        IDDefinicionBonificacion, IDDefinicionDescuento, Amortizacion, IDSUNATCatalogo7, DescuentoRecargo
                                    ) VALUES (
                                        @IDCp, @Cantidad, @IDProducto, @IDUnidad, @Factor, @IDAlmacen, @Descripcion,
                                        @Total, @ValorUnitario, @PorcentajeImpto, @Inafecto, @Bonificacion,
                                        @ValorAfecto, @ValorVenta, @SubTotal, @ValorDescuento, @ValorInafecto,
                                        0, 0, 0,
                                        0, 0, 0, 0, 0,
                                        @Anulado, 0, 0,
                                        0, 0, @DescuentoAplicado, 0,
                                        NULL, NULL, 0, @IDSUNATCatalogo7, ''
                                    );";

                                foreach (var it in c.Items)
                                {
                                    if (!it.IDProductoForce.HasValue) continue;

                                    bool esInafecto = it.ValorInafecto > 0 && it.ValorAfecto == 0;
                                    int sunatCat7 = esInafecto ? 30 : 10;

                                    using var cmdIt = new SqlCommand(sqlInsertItem, connForce, tx);
                                    cmdIt.Parameters.AddWithValue("@IDCp", nuevoIdCp);
                                    cmdIt.Parameters.AddWithValue("@Cantidad", it.Cantidad);
                                    cmdIt.Parameters.AddWithValue("@IDProducto", it.IDProductoForce.Value);
                                    cmdIt.Parameters.AddWithValue("@IDUnidad", (object?)it.IDUnidadForce ?? DBNull.Value);
                                    cmdIt.Parameters.AddWithValue("@Factor", it.CantidadFactor);
                                    cmdIt.Parameters.AddWithValue("@IDAlmacen", c.IDAlmacenForce ?? 169);
                                    cmdIt.Parameters.AddWithValue("@Descripcion", it.DescripcionProducto ?? "");
                                    cmdIt.Parameters.AddWithValue("@Total", it.Total);
                                    cmdIt.Parameters.AddWithValue("@ValorUnitario", it.PrecioUnitario);
                                    cmdIt.Parameters.AddWithValue("@PorcentajeImpto", it.PorcentajeImpuesto);
                                    cmdIt.Parameters.AddWithValue("@Inafecto", esInafecto);
                                    cmdIt.Parameters.AddWithValue("@Bonificacion", it.EsBonificacion);
                                    cmdIt.Parameters.AddWithValue("@ValorAfecto", it.ValorAfecto);
                                    cmdIt.Parameters.AddWithValue("@ValorVenta", it.ValorVenta);
                                    cmdIt.Parameters.AddWithValue("@SubTotal", it.SubTotal);
                                    cmdIt.Parameters.AddWithValue("@ValorDescuento", it.ValorDescuento);
                                    cmdIt.Parameters.AddWithValue("@ValorInafecto", it.ValorInafecto);
                                    cmdIt.Parameters.AddWithValue("@Anulado", it.Anulado);
                                    cmdIt.Parameters.AddWithValue("@DescuentoAplicado", it.ValorDescuento);
                                    cmdIt.Parameters.AddWithValue("@IDSUNATCatalogo7", sunatCat7);

                                    await cmdIt.ExecuteNonQueryAsync(ct);
                                }

                                // 5. Enlace Bidireccional: Actualizar ped_Pedido.IDCpVenta y anular comprobantes previos reemplazados
                                if (c.IDPedidoForce.HasValue && c.IDPedidoForce.Value > 0)
                                {
                                    await SincronizarEnlacePedidoYAnuladosPreviosAsync(connForce, tx, c, nuevoIdCp, ct);
                                }

                                // 6. Actualizar Correlativo en TipoCpVentas
                                const string sqlUpdCorrelativo = @"
                                    UPDATE dbo.TipoCpVentas
                                    SET Numero = @Correlativo
                                    WHERE ID = @IDTipoCp AND ISNULL(Numero, 0) < @Correlativo;";

                                using (var cmdCorr = new SqlCommand(sqlUpdCorrelativo, connForce, tx))
                                {
                                    cmdCorr.Parameters.AddWithValue("@IDTipoCp", idTipoCpFinal);
                                    cmdCorr.Parameters.AddWithValue("@Correlativo", c.CorrelativoOrigen);
                                    await cmdCorr.ExecuteNonQueryAsync(ct);
                                }

                                // 7. Generar Deuda en dbo.cob_DeudaCliente si es a Crédito y tiene Saldo > 0
                                bool deudaCreada = false;
                                if (c.DebeGenerarDeuda)
                                {
                                    DateTime fechaVenc = c.FechaVencimiento ?? c.FechaEmision;
                                    DateTime fechaProg = c.FechaProgramacionPago ?? fechaVenc;
                                    decimal saldoInsertar = Math.Min(c.SaldoPendiente, c.Total);
                                    string estadoDeuda = (saldoInsertar < c.Total) ? "PARCIAL" : "PENDIENTE";

                                    const string sqlInsertDeuda = @"
                                        IF NOT EXISTS (SELECT 1 FROM dbo.cob_DeudaCliente WHERE IDCpVenta = @IDCpVenta AND Eliminado = 0)
                                        BEGIN
                                            INSERT INTO dbo.cob_DeudaCliente (
                                                IDCliente, IDCatalogo, IDPedido, IDCpVenta, IDMoneda, IDModalidadCredito,
                                                IDMedioPago, IDVendedor, Total, Saldo, MontoDetraccion,
                                                FechaEmision, FechaVencimiento, FechaProgramacionPago,
                                                TipoCp, Credito, MovSaldo, SujetoADetraccion, SujetoAPercepcion,
                                                Estado, UsuarioCreador, FechaCreacion, Eliminado
                                            ) VALUES (
                                                @IDCliente, @IDCatalogo, @IDPedido, @IDCpVenta, 1, @IDModalidadCredito,
                                                NULL, @IDVendedor, @Total, @Saldo, 0,
                                                @FechaEmision, @FechaVencimiento, @FechaProgramacionPago,
                                                'Cobrar', 1, 1, 0, 0,
                                                @EstadoDeuda, '0', GETDATE(), 0
                                            );
                                        END";

                                    using var cmdDeuda = new SqlCommand(sqlInsertDeuda, connForce, tx);
                                    cmdDeuda.Parameters.AddWithValue("@IDCliente", c.IDClienteForce!.Value);
                                    cmdDeuda.Parameters.AddWithValue("@IDCatalogo", (object?)c.IDCatalogoForce ?? DBNull.Value);
                                    cmdDeuda.Parameters.AddWithValue("@IDPedido", (object?)c.IDPedidoForce ?? DBNull.Value);
                                    cmdDeuda.Parameters.AddWithValue("@IDCpVenta", nuevoIdCp);
                                    cmdDeuda.Parameters.AddWithValue("@IDModalidadCredito", (object?)c.IDModalidadCreditoForce ?? DBNull.Value);
                                    cmdDeuda.Parameters.AddWithValue("@IDVendedor", c.IDVendedorForce!.Value);
                                    cmdDeuda.Parameters.AddWithValue("@Total", c.Total);
                                    cmdDeuda.Parameters.AddWithValue("@Saldo", saldoInsertar);
                                    cmdDeuda.Parameters.AddWithValue("@FechaEmision", c.FechaEmision);
                                    cmdDeuda.Parameters.AddWithValue("@FechaVencimiento", fechaVenc);
                                    cmdDeuda.Parameters.AddWithValue("@FechaProgramacionPago", fechaProg);
                                    cmdDeuda.Parameters.AddWithValue("@EstadoDeuda", estadoDeuda);

                                    await cmdDeuda.ExecuteNonQueryAsync(ct);
                                    deudaCreada = true;
                                }

                                await tx.CommitAsync(ct);

                                c.YaExisteEnForce = true;
                                c.IDCpForceExistente = nuevoIdCp;
                                c.ForceIDPedido = c.IDPedidoForce;
                                c.Semaforo = "AZUL";
                                c.TipoOperacion = "SIN_CAMBIOS";
                                _status.TotalSubidos++;
                                if (deudaCreada) _status.TotalDeudasGeneradas++;

                                string multiBadge = c.EsMulticomprobante ? $" [Comp {c.ComprobanteNumeroOrden}/{c.TotalComprobantesDelPedido}]" : "";
                                string reempBadge = c.EsReemplazoDeAnulado ? $" [🔄 Reemplazó a {c.ComprobantesAnuladosDelPedido}]" : "";
                                string infoEnlace = c.IDPedidoForce.HasValue ? $" | Enlazado a Pedido #{c.IDPedidoForce}{multiBadge}{reempBadge}" : " (Sin pedido)";
                                string infoDeuda = deudaCreada ? $" | Deuda: S/ {c.SaldoPendiente:N2}" : "";
                                AgregarEvento(c.IDCpOrigen, c.NumCp, "SUCCESS", $"✓ Subido como Cp #{nuevoIdCp} | Total S/ {c.Total:N2}{infoEnlace}{infoDeuda}");
                            }
                        }
                        catch (Exception exCp)
                        {
                            await tx.RollbackAsync(ct);
                            _status.TotalErrores++;
                            _logger.LogError(exCp, "Error al procesar comprobante {NumCp}", c.NumCp);
                            AgregarEvento(c.IDCpOrigen, c.NumCp, "ERROR", $"Excepción SQL: {exCp.Message}");
                        }
                    }
                }
                finally
                {
                    // REACTIVAR TRIGGERS DE STOCK
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
                    catch (Exception exTrg)
                    {
                        _logger.LogError(exTrg, "Error reactivando triggers de stock en comprobantes.");
                    }
                }

                _status.FaseDetalle = $"Sincronización finalizada: {_status.TotalSubidos} subidos, {_status.TotalActualizados} cobranzas/enlaces actualizados, {_status.TotalAnulados} anulados, {_status.TotalOmitidos} omitidos, {_status.TotalErrores} errores.";
                AgregarEvento(0, "SISTEMA", "INFO", $"Resumen final: {_status.TotalSubidos} nuevos subidos, {_status.TotalActualizados} actualizados, {_status.TotalAnulados} anulados, {_status.TotalErrores} errores.");
                _status.EstadoActual = "COMPLETADO";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error global en subida de comprobantes.");
                _status.EstadoActual = "ERROR";
                _status.FaseDetalle = $"Error crítico: {ex.Message}";
                AgregarEvento(0, "SISTEMA", "ERROR", $"Error crítico: {ex.Message}");
            }
            finally
            {
                _status.IsRunning = false;
                _execLock.Release();
            }
        }

        /// <summary>
        /// Garantiza el enlace bidireccional Cp.IDPedido <-> ped_Pedido.IDCpVenta.
        /// Si el pedido tenía comprobantes previos anulados en el ERP Antiguo (incluso de otras fechas),
        /// los marca como Anulado=1 en Force, cancela su deuda en cob_DeudaCliente y asigna el primer comprobante vigente a ped_Pedido.IDCpVenta.
        /// </summary>
        private async Task SincronizarEnlacePedidoYAnuladosPreviosAsync(
            SqlConnection connForce,
            SqlTransaction tx,
            ComprobanteAlineadoDto c,
            int idCpActualForce,
            CancellationToken ct)
        {
            int idPedidoForce = c.IDPedidoForce!.Value;

            // 1. Asegurar que este comprobante tenga asignado IDPedido en dbo.Cp
            const string sqlLinkCp = @"
                UPDATE dbo.Cp
                SET IDPedido = @IDPedido
                WHERE ID = @IDCp AND (IDPedido IS NULL OR IDPedido <> @IDPedido);";
            using (var cmdLinkCp = new SqlCommand(sqlLinkCp, connForce, tx))
            {
                cmdLinkCp.Parameters.AddWithValue("@IDPedido", idPedidoForce);
                cmdLinkCp.Parameters.AddWithValue("@IDCp", idCpActualForce);
                await cmdLinkCp.ExecuteNonQueryAsync(ct);
            }

            // 2. Si este comprobante reemplaza a comprobantes anulados previos del mismo pedido,
            // asegurar que dichos comprobantes anulados estén marcados como Anulado=1 y su deuda cancelada en Force
            if (c.EsReemplazoDeAnulado && !string.IsNullOrWhiteSpace(c.ComprobantesAnuladosDelPedido))
            {
                var numsAnulados = c.ComprobantesAnuladosDelPedido
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                foreach (var numAnul in numsAnulados)
                {
                    const string sqlAnularPrevio = @"
                        DECLARE @IDCpAnulado int;
                        SELECT TOP 1 @IDCpAnulado = ID
                        FROM dbo.Cp
                        WHERE NumCp = @NumCpAnulado AND ISNULL(Eliminado, 0) = 0 AND ISNULL(Anulado, 0) = 0;

                        IF @IDCpAnulado IS NOT NULL
                        BEGIN
                            UPDATE dbo.Cp
                            SET Anulado = 1,
                                HoraAnulacion = GETDATE(),
                                Observacion = ISNULL(Observacion, '') + ' [ANULADO Y REEMPLAZADO POR ' + @NumCpNuevo + ']'
                            WHERE ID = @IDCpAnulado;

                            UPDATE dbo.ItemCp
                            SET Anulado = 1
                            WHERE IDCp = @IDCpAnulado;

                            UPDATE dbo.cob_DeudaCliente
                            SET Eliminado = 1,
                                Estado = 'ANULADO',
                                FechaEliminacion = GETDATE(),
                                MotivoEliminacion = 'Anulado y reemplazado por ' + @NumCpNuevo,
                                UsuarioEliminacion = 0
                            WHERE IDCpVenta = @IDCpAnulado AND ISNULL(Eliminado, 0) = 0;
                        END";

                    using var cmdAnPrev = new SqlCommand(sqlAnularPrevio, connForce, tx);
                    cmdAnPrev.Parameters.AddWithValue("@NumCpAnulado", numAnul);
                    cmdAnPrev.Parameters.AddWithValue("@NumCpNuevo", c.NumCp);
                    await cmdAnPrev.ExecuteNonQueryAsync(ct);
                }
            }

            // 3. Actualizar ped_Pedido.IDCpVenta apuntando siempre al PRIMER comprobante VIGENTE (Anulado = 0) del pedido
            // y asegurando que el estado del pedido sea FACTURADO
            const string sqlUpdatePed = @"
                DECLARE @PrimerCpVigente int;
                SELECT TOP 1 @PrimerCpVigente = ID
                FROM dbo.Cp
                WHERE IDPedido = @IDPedido
                  AND ISNULL(Anulado, 0) = 0
                  AND ISNULL(Eliminado, 0) = 0
                ORDER BY ID ASC;

                UPDATE dbo.ped_Pedido
                SET IDCpVenta = ISNULL(@PrimerCpVigente, @IDCp),
                    Estado = 'FACTURADO',
                    FechaModificacion = GETDATE()
                WHERE ID = @IDPedido;";

            using (var cmdPed = new SqlCommand(sqlUpdatePed, connForce, tx))
            {
                cmdPed.Parameters.AddWithValue("@IDCp", idCpActualForce);
                cmdPed.Parameters.AddWithValue("@IDPedido", idPedidoForce);
                await cmdPed.ExecuteNonQueryAsync(ct);
            }
        }
    }
}
