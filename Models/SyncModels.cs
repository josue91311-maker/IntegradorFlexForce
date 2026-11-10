using System;
using System.Collections.Generic;

namespace IntegradorFlexForce.Models
{
    public class SyncStatusDto
    {
        public bool IsRunning { get; set; }
        public string EstadoActual { get; set; } = "EN_ESPERA";
        public string FaseDetalle { get; set; } = "Sistema en espera (Modo Manual)";
        public int ProgresoActual { get; set; }
        public int ProgresoTotal { get; set; }
        public double Porcentaje => ProgresoTotal > 0 ? Math.Round((double)ProgresoActual / ProgresoTotal * 100, 1) : 0;

        public DateTime? UltimaEjecucion { get; set; }
        public int UltimoLoteProcesados { get; set; }
        public int UltimoLoteNuevos { get; set; }
        public int UltimoLoteModificados { get; set; }
        public int UltimoLoteDesactivados { get; set; }
        public int UltimoLoteZonasActualizadas { get; set; }
        public int UltimoLoteErrores { get; set; }
        public double UltimaDuracionSegundos { get; set; }
        public bool SincronizacionActiva { get; set; }
        public int IntervaloMinutos { get; set; }
        public string VentanaHoraria { get; set; } = "05:00 - 23:30";
    }

    public class TablaComparacionResumenDto
    {
        public string CodigoTabla { get; set; } = ""; // CLIENTES, DIRECCIONES, ZONAS_RUTAS, CREDITOS
        public string NombreTabla { get; set; } = "";
        public string TablasForce { get; set; } = "";
        public int TotalOrigen { get; set; }
        public int TotalDestino { get; set; }
        public int InsertsDetectados { get; set; }
        public int UpdatesDetectados { get; set; }
        public int DeletesBajasDetectados { get; set; }
        public int TotalCambios => InsertsDetectados + UpdatesDetectados + DeletesBajasDetectados;
        public string Observacion { get; set; } = "Sincronizado";
    }

    public class CambioDetectadoItemDto
    {
        public string Tabla { get; set; } = "";          // CLIENTES, DIRECCIONES, ZONAS_RUTAS, CREDITOS
        public string TipoOperacion { get; set; } = "";  // INSERT, UPDATE, BAJA / DELETE
        public int IDOrigen { get; set; }
        public string CodigoCliente { get; set; } = "";
        public string NombreCliente { get; set; } = "";
        public string CampoModificado { get; set; } = "";
        public string ValorActualForce { get; set; } = "";
        public string ValorNuevoERP { get; set; } = "";
    }

    public class SyncPreviewDto
    {
        public DateTime FechaAnalisis { get; set; } = DateTime.Now;
        public double TiempoEscaneoSegundos { get; set; }
        public List<TablaComparacionResumenDto> ResumenPorTabla { get; set; } = new();
        public List<CambioDetectadoItemDto> DetalleCambios { get; set; } = new();
    }

    public class SyncConfigDto
    {
        public bool SincronizacionActiva { get; set; } = false;
        public int IntervaloMinutos { get; set; } = 3;
        public string HoraInicioVentana { get; set; } = "05:00";
        public string HoraFinVentana { get; set; } = "23:30";
        public int TamanoLote { get; set; } = 500;
        public int ReintentosMaximos { get; set; } = 3;

        // --- Configuración por Módulo ---
        // Módulo Clientes
        public bool AutoClientesActivo { get; set; } = true;
        public string ModoClientes { get; set; } = "INTERVALO"; // "INTERVALO" o "MADRUGADA"
        public int IntervaloClientesMinutos { get; set; } = 3;
        public string HoraInicioClientes { get; set; } = "05:00";
        public string HoraFinClientes { get; set; } = "23:30";
        public string HoraMadrugadaClientes { get; set; } = "02:00";

        // Módulo Productos
        public bool AutoProductosActivo { get; set; } = true;
        public string ModoProductos { get; set; } = "INTERVALO"; // "INTERVALO" o "MADRUGADA"
        public int IntervaloProductosMinutos { get; set; } = 3;
        public string HoraInicioProductos { get; set; } = "05:00";
        public string HoraFinProductos { get; set; } = "23:30";
        public string HoraMadrugadaProductos { get; set; } = "03:30";

        // Módulo Vendedores
        public bool AutoVendedoresActivo { get; set; } = true;
        public string ModoVendedores { get; set; } = "INTERVALO"; // "INTERVALO" o "MADRUGADA"
        public int IntervaloVendedoresMinutos { get; set; } = 5;
        public string HoraInicioVendedores { get; set; } = "05:00";
        public string HoraFinVendedores { get; set; } = "23:30";
        public string HoraMadrugadaVendedores { get; set; } = "04:00";

        // Políticas de Resiliencia y Reintentos
        public bool HabilitarReintentos { get; set; } = true;
        public int EsperaReintentoSegundos { get; set; } = 30;
    }

    public class SyncTransaccionDto
    {
        public long ID { get; set; }
        public Guid LoteId { get; set; }
        public string Entidad { get; set; } = "";
        public string TipoEjecucion { get; set; } = "";
        public DateTime FechaInicio { get; set; }
        public DateTime? FechaFin { get; set; }
        public decimal? DuracionSegundos { get; set; }
        public int TotalEscaneados { get; set; }
        public int TotalNuevos { get; set; }
        public int TotalModificados { get; set; }
        public int TotalErrores { get; set; }
        public string Estado { get; set; } = "";
        public string Usuario { get; set; } = "";
    }

    public class SyncLogDto
    {
        public long ID { get; set; }
        public Guid? LoteId { get; set; }
        public string Entidad { get; set; } = "";
        public string Nivel { get; set; } = "";
        public int? IDOrigen { get; set; }
        public string? Codigo { get; set; }
        public string Mensaje { get; set; } = "";
        public string? DetalleTecnico { get; set; }
        public DateTime Fecha { get; set; }
    }

    public class ClienteExtraccionDto
    {
        public int ID_Cliente { get; set; }
        public string CodigoCliente { get; set; } = "";
        public string TipoPersona { get; set; } = "3";
        public string TipoDocIdentidad { get; set; } = "1";
        public string DocIdentidad { get; set; } = "";
        public string NombreCompleto { get; set; } = "";
        public string NombreComercial { get; set; } = "";
        public string PrimerNombre { get; set; } = "";
        public string SegundoNombre { get; set; } = "";
        public string ApellidoPaterno { get; set; } = "";
        public string ApellidoMaterno { get; set; } = "";
        public string CodigoCategoriaCliente { get; set; } = "CAT_A";
        public string CodigoTipoCliente { get; set; } = "GENERAL";
        public string CodigoTipoNegocio { get; set; } = "0";
        public string? Email { get; set; }
        public string Telefono { get; set; } = "";
        public string Contacto { get; set; } = "";
        public string PuestoContacto { get; set; } = "";
        public bool ExceptuadoPercepcion { get; set; }
        public string Observacion { get; set; } = "";
        public bool EstadoCliente { get; set; }

        public int ID_Direccion { get; set; }
        public bool EsPrincipal { get; set; }
        public string CodigoTipoDireccion { get; set; } = "FIS";
        public string Direccion { get; set; } = "";
        public string Referencia { get; set; } = "";
        public string Ubigeo { get; set; } = "150101";
        public string CodigoDepartamento { get; set; } = "15";
        public string CodigoProvincia { get; set; } = "01";
        public string CodigoDistrito { get; set; } = "01";
        public decimal Latitud { get; set; }
        public decimal Longitud { get; set; }
        public bool EstadoDireccion { get; set; }

        public string? Campo1 { get; set; }
        public string? Campo2 { get; set; }
        public string? Campo3 { get; set; }
        public string? Campo4 { get; set; }
        public string? CaracteristicasJson { get; set; }
    }

    public class CarteraRutaExtraccionDto
    {
        public int ID_Direccion { get; set; }
        public int ID_Cliente { get; set; }
        public string CodigoCliente { get; set; } = "";
        public string NombreCliente { get; set; } = "";
        public string CodigoVendedor { get; set; } = "";
        public string NombreVendedor { get; set; } = "";
        public int IDZonaVenta { get; set; }
        public string CodigoZonaVenta { get; set; } = "";
        public string NombreZonaVenta { get; set; } = "";
        public int IDZonaReparto { get; set; }
        public string CodigoZonaReparto { get; set; } = "";
        public string CodigoRuta { get; set; } = "";
        public int DiaDeSemana { get; set; } = 1;
    }

    public class CreditoCatalogoExtraccionDto
    {
        public int ID_LineaCredito { get; set; }
        public int ID_Cliente { get; set; }
        public string CodigoCliente { get; set; } = "";
        public string NombreCliente { get; set; } = "";
        public int ID_Portafolio { get; set; }
        public string CodigoPortafolio { get; set; } = "";
        public string NombrePortafolio { get; set; } = "";
        public decimal Monto { get; set; }
        public decimal BalanceSoles { get; set; }
        public decimal BalanceDolares { get; set; }
        public decimal Saldo { get; set; }
        public int DiasGracia { get; set; } = 5;
        public decimal PorcentajeMaximoVencidoCredito { get; set; }
        public decimal PorcentajeMaximoVencidoContado { get; set; } = 100;
        public bool Activo { get; set; } = true;
    }
}
