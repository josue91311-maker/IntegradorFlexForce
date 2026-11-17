using System;
using System.Collections.Generic;

namespace IntegradorFlexForce.Models
{
    public class VendedorPedidoOptionDto
    {
        public int IDOrigen { get; set; }
        public int? IDForce { get; set; }
        public string Codigo { get; set; } = "";
        public string Nombre { get; set; } = "";
        public int? IDTipoCpPedidoForce { get; set; }
        public string SeriePedidoForce { get; set; } = "";
        public bool TieneTipoPedido => IDTipoCpPedidoForce.HasValue;
    }

    public class PedidoFiltroRequestDto
    {
        public List<int> VendedoresOrigenIds { get; set; } = new();
        public DateTime FechaDesde { get; set; } = DateTime.Today;
        public DateTime FechaHasta { get; set; } = DateTime.Today;
        public bool IncluirAnulados { get; set; } = true;
        public bool IncluirFacturados { get; set; } = true;
    }

    public class PedidoItemAlineadoDto
    {
        public int IDItemOrigen { get; set; }
        public int IDProductoOrigen { get; set; }
        public string CodigoProducto { get; set; } = "";
        public string NombreProducto { get; set; } = "";
        public int IDUnidadOrigen { get; set; }
        public string UnidadNombre { get; set; } = "";
        public decimal Cantidad { get; set; }
        public decimal CantidadFactor { get; set; }
        public decimal PrecioUnitario { get; set; }
        public decimal PrecioNeto { get; set; }
        public decimal PrecioConImpuesto { get; set; }
        public decimal SubTotal { get; set; }
        public decimal SubTotalNeto { get; set; }
        public decimal Total { get; set; }
        public bool TieneImpuesto { get; set; }
        public decimal PorcentajeImpuesto { get; set; }
        public decimal MontoImpuesto { get; set; }
        public decimal ValorAfecto { get; set; }
        public decimal ValorInafecto { get; set; }
        public decimal MontoPercepcion { get; set; }
        public decimal Descuento { get; set; }
        public bool EsBonificacion { get; set; }
        public bool Credito { get; set; }

        // Homologación Force
        public int? IDProductoForce { get; set; }
        public int? IDUnidadForce { get; set; }
        public bool ProductoOk => IDProductoForce.HasValue;
        public bool UnidadOk => IDUnidadForce.HasValue;
        public string ObservacionItem { get; set; } = "";
    }

    public class PedidoAlineadoDto
    {
        public int IDPedidoOrigen { get; set; }
        public string NumPedidoOrigen { get; set; } = "";
        public DateTime FechaEmision { get; set; }
        public DateTime? FechaEntrega { get; set; }

        // Cliente
        public int IDClienteOrigen { get; set; }
        public string CodigoCliente { get; set; } = "";
        public string NombreCliente { get; set; } = "";
        public int? IDClienteForce { get; set; }

        // Vendedor y Tipo Pedido
        public int IDVendedorOrigen { get; set; }
        public string CodigoVendedor { get; set; } = "";
        public string NombreVendedor { get; set; } = "";
        public int? IDVendedorForce { get; set; }
        public int? IDTipoCpPedidoForce { get; set; }
        public string SerieTipoPedidoForce { get; set; } = "";

        // Organización y Logística
        public int IDAlmacenOrigen { get; set; }
        public int? IDAlmacenForce { get; set; }
        public string AlmacenNombre { get; set; } = "";

        public int IDSucursalOrigen { get; set; }
        public int? IDSucursalForce { get; set; }

        public int IDCatalogoOrigen { get; set; }
        public int? IDCatalogoForce { get; set; }
        public string CatalogoNombre { get; set; } = "";

        public int? IDListaPrecioForce { get; set; }
        public int? IDMedioPagoForce { get; set; }
        public int? IDModalidadCreditoForce { get; set; }
        public int? IDRutaForce { get; set; }

        // Condición y Totales
        public bool TieneCredito { get; set; }
        public decimal CantidadTotal { get; set; }
        public decimal SubTotal { get; set; }
        public decimal TotalDescuento { get; set; }
        public bool TieneBonificacion { get; set; }
        public decimal Total { get; set; }
        public decimal TotalCredito { get; set; }
        public decimal TotalContado { get; set; }
        public bool TieneImpuesto { get; set; }
        public decimal TotalImpuesto { get; set; }
        public decimal ValorAfecto { get; set; }
        public decimal ValorInafecto { get; set; }
        public decimal TotalPercepcion { get; set; }

        // Estados
        public bool AnuladoOrigen { get; set; }
        public string EstadoFacturacionOrigen { get; set; } = "PENDIENTE"; // PENDIENTE, TOTAL, PARCIAL, ANULADO
        public string EstadoForceMapeado { get; set; } = "PENDIENTE";      // PENDIENTE, FACTURADO, ANULADO

        // Control Idempotencia y Diagnóstico
        public bool YaExisteEnForce { get; set; }
        public int? IDPedidoForceExistente { get; set; }
        public string Semaforo { get; set; } = "VERDE"; // VERDE, AMBAR, AZUL, ROJO
        public string ObservacionValidacion { get; set; } = "";
        public bool ListoParaSubir => !YaExisteEnForce && Semaforo != "ROJO";

        public List<PedidoItemAlineadoDto> Items { get; set; } = new();
    }

    public class PedidoAnalisisResponseDto
    {
        public DateTime FechaAnalisis { get; set; } = DateTime.Now;
        public double TiempoSegundos { get; set; }
        public int TotalEncontrados { get; set; }
        public int ListosParaSubir { get; set; }
        public int YaExistenEnForce { get; set; }
        public int ObservadosConError { get; set; }
        public int Anulados { get; set; }
        public int FacturadosTotal { get; set; }
        public int Pendientes { get; set; }
        public int Parciales { get; set; }
        public List<PedidoAlineadoDto> Pedidos { get; set; } = new();
    }

    public class PedidoSubidaRequestDto
    {
        public List<int> IDsPedidosOrigen { get; set; } = new();
    }

    public class PedidoEventoEjecucionDto
    {
        public string Hora { get; set; } = DateTime.Now.ToString("HH:mm:ss");
        public int IDPedidoOrigen { get; set; }
        public string NumPedido { get; set; } = "";
        public string Nivel { get; set; } = "INFO"; // OK, WARN, ERROR, INFO
        public string Mensaje { get; set; } = "";
    }

    public class PedidoStatusDto
    {
        public bool IsRunning { get; set; }
        public string EstadoActual { get; set; } = "EN_ESPERA";
        public string FaseDetalle { get; set; } = "Seleccione vendedor(es) y rango de fechas para consultar.";
        public int ProgresoActual { get; set; }
        public int ProgresoTotal { get; set; }
        public double Porcentaje => ProgresoTotal > 0 ? Math.Round((double)ProgresoActual / ProgresoTotal * 100, 1) : 0;
        public int TotalSubidos { get; set; }
        public int TotalOmitidos { get; set; }
        public int TotalErrores { get; set; }
        public bool StockProtegidoConfirmado { get; set; }
        public List<PedidoEventoEjecucionDto> EventosRecientes { get; set; } = new();
    }
}
