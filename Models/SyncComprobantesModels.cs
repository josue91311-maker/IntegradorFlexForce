using System;
using System.Collections.Generic;

namespace IntegradorFlexForce.Models
{
    public class ComprobanteFiltroRequestDto
    {
        public List<int> VendedoresOrigenIds { get; set; } = new();
        public DateTime FechaDesde { get; set; }
        public DateTime FechaHasta { get; set; }
        public bool IncluirAnulados { get; set; } = false;
        public string TipoDocumento { get; set; } = "TODOS"; // TODOS, FACTURAS, BOLETAS
        public bool SoloPedidosFacturadosEnForce { get; set; } = true;
    }

    public class ComprobanteItemAlineadoDto
    {
        public int IDItemOrigen { get; set; }
        public int IDProductoOrigen { get; set; }
        public int? IDProductoForce { get; set; }
        public string CodigoProducto { get; set; } = string.Empty;
        public string DescripcionProducto { get; set; } = string.Empty;
        public int IDUnidadOrigen { get; set; }
        public int? IDUnidadForce { get; set; }
        public string UnidadAbrev { get; set; } = string.Empty;
        public decimal Cantidad { get; set; }
        public decimal CantidadFactor { get; set; } = 1m;
        public decimal PrecioUnitario { get; set; }
        public decimal SubTotal { get; set; }
        public decimal Total { get; set; }
        public decimal ValorAfecto { get; set; }
        public decimal ValorInafecto { get; set; }
        public decimal ValorExonerado { get; set; }
        public decimal ValorVenta { get; set; }
        public decimal ValorDescuento { get; set; }
        public decimal PorcentajeImpuesto { get; set; } = 18m;
        public bool EsBonificacion { get; set; }
        public bool Anulado { get; set; }
        public int IDItemCpRelacionadoOrigen { get; set; }
    }

    public class ComprobanteAlineadoDto
    {
        public int IDCpOrigen { get; set; }
        public string NumCp { get; set; } = string.Empty;
        public string NumCpContable { get; set; } = string.Empty;
        public DateTime FechaEmision { get; set; }
        public DateTime Fecha { get; set; }
        public DateTime Hora { get; set; }
        public int IDTipoCpOrigen { get; set; }
        public string SerieOrigen { get; set; } = string.Empty;
        public int CorrelativoOrigen { get; set; }
        public int IDTipoComprobantePagoOrigen { get; set; } // 1=Factura, 4=Boleta, 7=NotaCredito, 33=Ticket
        public string TipoCpDescripcion { get; set; } = string.Empty;

        // Clasificación de documento: VENTA vs NOTA_CREDITO
        public string ClaseDocumento { get; set; } = "VENTA";
        public bool EsNotaCredito => string.Equals(ClaseDocumento, "NOTA_CREDITO", StringComparison.OrdinalIgnoreCase);

        // Relación de Nota de Crédito con el comprobante de venta origen modificado
        public int IDCpModificadoOrigen { get; set; }
        public string NumCpModificadoOrigen { get; set; } = string.Empty;
        public int? IDCpForceModificado { get; set; }
        public int IDMotivoEmisionAnulacionOrigen { get; set; } = 2;
        public string EstadoEntregaDevolucion { get; set; } = "TOTAL";
        public int? IDNotaCreditoForce { get; set; }

        // TipoCpVenta Force
        public int? IDTipoCpVentaForce { get; set; }
        public string SerieForce { get; set; } = string.Empty;

        // Cliente
        public int IDClienteOrigen { get; set; }
        public int? IDClienteForce { get; set; }
        public string CodigoCliente { get; set; } = string.Empty;
        public string NombreCliente { get; set; } = string.Empty;
        public string DocIdentidadCliente { get; set; } = string.Empty;

        // Vendedor
        public int IDVendedorOrigen { get; set; }
        public int? IDVendedorForce { get; set; }
        public string CodigoVendedor { get; set; } = string.Empty;
        public string NombreVendedor { get; set; } = string.Empty;

        // Almacén y Sucursal
        public int IDAlmacenOrigen { get; set; }
        public int? IDAlmacenForce { get; set; }
        public string AlmacenNombre { get; set; } = string.Empty;
        public int IDSucursalOrigen { get; set; }
        public int? IDSucursalForce { get; set; }

        // Pedido Relacionado
        public int IDPedidoOrigen { get; set; }
        public string NumPedidoOrigen { get; set; } = string.Empty;
        public int? IDPedidoForce { get; set; }
        public string NumPedidoForce { get; set; } = string.Empty;
        public string EstadoPedidoForce { get; set; } = string.Empty;
        public int? IDCatalogoForce { get; set; }
        public int? IDModalidadCreditoForce { get; set; }
        public int ComprobanteNumeroOrden { get; set; } = 1;
        public int TotalComprobantesDelPedido { get; set; } = 1;
        public bool EsMulticomprobante => TotalComprobantesDelPedido > 1;
        public bool PedidoFacturadoEnForce => string.Equals(EstadoPedidoForce, "FACTURADO", StringComparison.OrdinalIgnoreCase);

        // Direcciones, crédito y cobranzas
        public string DireccionFiscal { get; set; } = string.Empty;
        public string DireccionEntrega { get; set; } = string.Empty;
        public string EstadoEntrega { get; set; } = "PENDIENTE";
        public bool EsCredito { get; set; }
        public decimal SaldoPendiente { get; set; }
        public DateTime? FechaVencimiento { get; set; }
        public DateTime? FechaProgramacionPago { get; set; }
        public bool DebeGenerarDeuda => !AnuladoOrigen && EsCredito && SaldoPendiente > 0;

        // Totales monetarios
        public decimal SubTotal { get; set; }
        public decimal Total { get; set; }
        public decimal ValorAfecto { get; set; }
        public decimal ValorInafecto { get; set; }
        public decimal ValorExonerado { get; set; }
        public decimal ValorDescuento { get; set; }
        public decimal ValorVenta { get; set; }
        public bool TieneImpuesto { get; set; } = true;
        public bool PreciosIncImpto { get; set; } = true;

        public bool AnuladoOrigen { get; set; }

        // Validaciones Force
        public bool YaExisteEnForce { get; set; }
        public int? IDCpForceExistente { get; set; }
        public int? ForceIDPedido { get; set; }
        public bool ForceAnulado { get; set; }
        public decimal? ForceTotal { get; set; }
        public decimal? ForceSaldoDeuda { get; set; }
        public string ForceEstadoDeuda { get; set; } = string.Empty;
        public int? IDDeudaForce { get; set; }

        // Detección de reemplazo de comprobantes anulados en el mismo pedido
        public bool EsReemplazoDeAnulado { get; set; }
        public string ComprobantesAnuladosDelPedido { get; set; } = string.Empty;

        // Operación Sincronizada: "INSERT", "UPDATE_DEUDA", "REENLAZAR_PEDIDO", "ANULAR", "SIN_CAMBIOS"
        public string TipoOperacion { get; set; } = "INSERT";
        public string MotivoAnulacion { get; set; } = string.Empty;

        // Diagnóstico / Semáforo
        public string Semaforo { get; set; } = "VERDE"; // VERDE, AZUL, AMBAR, ROJO
        public string ObservacionValidacion { get; set; } = string.Empty;
        public bool ListoParaSubir => (Semaforo == "VERDE" || Semaforo == "AMBAR") && TipoOperacion != "SIN_CAMBIOS";

        public List<ComprobanteItemAlineadoDto> Items { get; set; } = new();
    }

    public class ComprobanteAnalisisResponseDto
    {
        public double TiempoSegundos { get; set; }
        public int TotalEncontrados { get; set; }
        public int ListosParaSubir { get; set; }
        public int NuevosAInsertar { get; set; }
        public int DeudasAActualizar { get; set; }
        public int AnulacionesPendientes { get; set; }
        public int ReemplazosDeAnulados { get; set; }
        public int YaExistenEnForce { get; set; }
        public int SinPedidoEnlazado { get; set; }
        public int ObservadosConError { get; set; }
        public int Anulados { get; set; }
        public int FacturasTotal { get; set; }
        public int BoletasTotal { get; set; }
        public int NotasCreditoTotal { get; set; }
        public int ConDeudaPorCobrar { get; set; }
        public decimal TotalDeudaPendiente { get; set; }
        public List<ComprobanteAlineadoDto> Comprobantes { get; set; } = new();
    }

    public class ComprobanteSubidaRequestDto
    {
        public List<int> IDsCpOrigen { get; set; } = new();
    }

    public class ComprobanteLogEventoDto
    {
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public int IDCpOrigen { get; set; }
        public string NumCp { get; set; } = string.Empty;
        public string Tipo { get; set; } = "INFO"; // INFO, WARN, ERROR, SUCCESS
        public string Mensaje { get; set; } = string.Empty;
    }

    public class ComprobanteStatusDto
    {
        public bool IsRunning { get; set; } = false;
        public string EstadoActual { get; set; } = "IDLE"; // IDLE, ANALIZANDO, EJECUTANDO, COMPLETADO, ERROR
        public string FaseDetalle { get; set; } = "En espera";
        public int ProgresoActual { get; set; }
        public int ProgresoTotal { get; set; }
        public int TotalSubidos { get; set; }
        public int TotalActualizados { get; set; }
        public int TotalAnulados { get; set; }
        public int TotalDeudasGeneradas { get; set; }
        public int TotalOmitidos { get; set; }
        public int TotalErrores { get; set; }
        public List<ComprobanteLogEventoDto> EventosRecientes { get; set; } = new();
    }
}
