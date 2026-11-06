using System;
using System.Collections.Generic;

namespace IntegradorFlexForce.Models
{
    public class SyncStatusDto
    {
        public bool IsRunning { get; set; }
        public string EstadoActual { get; set; } = "EN_ESPERA"; // EN_ESPERA, ESCANEANDO, PROCESANDO_LOTES
        public DateTime? UltimaEjecucion { get; set; }
        public int TotalRegistrosDestino { get; set; }
        public int TotalConError { get; set; }
        public int UltimoLoteProcesados { get; set; }
        public int UltimoLoteNuevos { get; set; }
        public int UltimoLoteModificados { get; set; }
        public int UltimoLoteErrores { get; set; }
        public double UltimaDuracionSegundos { get; set; }
        public bool SincronizacionActiva { get; set; }
        public int IntervaloMinutos { get; set; }
        public string VentanaHoraria { get; set; } = "05:00 - 23:30";
    }

    public class SyncConfigDto
    {
        public bool SincronizacionActiva { get; set; } = true;
        public int IntervaloMinutos { get; set; } = 3;
        public string HoraInicioVentana { get; set; } = "05:00";
        public string HoraFinVentana { get; set; } = "23:30";
        public int TamanoLote { get; set; } = 500;
        public int ReintentosMaximos { get; set; } = 3;
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
    }
}
