using System;
using System.Collections.Generic;

namespace IntegradorFlexForce.Models
{
    public class TraddeStepScheduleConfigDto
    {
        public string StepKey { get; set; } = string.Empty;
        public bool AutoActivo { get; set; } = false;
        /// <summary>
        /// "INTERVALO" (cada N minutos dentro de ventana) o "DIARIO" (hora exacta HH:mm una vez al día)
        /// </summary>
        public string ModoProgramacion { get; set; } = "INTERVALO";
        public int IntervaloMinutos { get; set; } = 15;
        public string HoraDiaria { get; set; } = "06:00";
        public string VentanaInicio { get; set; } = "05:00";
        public string VentanaFin { get; set; } = "21:00";
    }

    public class TraddeGlobalConfigDto
    {
        public bool AutoGlobalActivo { get; set; } = false;
        public bool GuardarLogsDisco { get; set; } = true;
        public bool RegistrarAuditoriaMongo { get; set; } = true;
        public bool OmitirDependientesSiFallaPadre { get; set; } = true;
        public int DiasRetencionLogs { get; set; } = 15;
        public string MongoConnectionString { get; set; } = "mongodb://192.168.2.125:27020/?directConnection=true";
        public string MongoDatabase { get; set; } = "tradde-mixtos";
        public string VentanaInicioDefault { get; set; } = "05:00";
        public string VentanaFinDefault { get; set; } = "21:00";
        public int BatchSizeMongo { get; set; } = 10000;
        public List<TraddeStepScheduleConfigDto> Schedules { get; set; } = new();
    }

    public class TraddeStepStatusDto
    {
        public int Orden { get; set; }
        public string StepKey { get; set; } = string.Empty;
        public string SeccionCodigo { get; set; } = string.Empty;
        public string SeccionNombre { get; set; } = string.Empty;
        public int SeccionOrden { get; set; }
        public string NombrePaso { get; set; } = string.Empty;
        public string Descripcion { get; set; } = string.Empty;
        public string TablasForceOrigen { get; set; } = string.Empty;
        public string ColeccionMongoDestino { get; set; } = string.Empty;
        public string EstrategiaCarga { get; set; } = string.Empty;
        public string NombreAppAuditoria { get; set; } = string.Empty;

        // Grafo de dependencias (StepKeys padres y sus nombres legibles)
        public List<string> DependeDe { get; set; } = new();
        public List<string> NombresDependencias { get; set; } = new();

        // Estado en tiempo real: "EN_ESPERA", "EN_COLA", "EJECUTANDO", "COMPLETADO", "ERROR", "OMITIDO"
        public string Estado { get; set; } = "EN_ESPERA";
        public bool IsRunning => Estado == "EJECUTANDO";
        public bool IsQueued => Estado == "EN_COLA";
        public int PosicionCola { get; set; }
        public int ProgresoPorcentaje { get; set; }
        public string FaseActual { get; set; } = "Listo para sincronizar";

        // Validación previa Force Nuevo (BDForceGVD) vs MongoDB (tradde-mixtos)
        public long? ConteoForceValidado { get; set; }
        public long? ConteoMongoValidado { get; set; }
        public long? DiferenciaValidada { get; set; }
        /// <summary>
        /// "SIN_VALIDAR", "ALINEADO", "DESFASADO", "VACIO_MONGO", "ERROR_VALIDACION"
        /// </summary>
        public string SemaforoValidacion { get; set; } = "SIN_VALIDAR";
        public DateTime? FechaUltimaValidacion { get; set; }
        public string? ErrorValidacion { get; set; }

        // Métricas de última ejecución
        public long FilasLeidasForce { get; set; }
        public long DocsInsertadosMongo { get; set; }
        public long DocsActualizadosMongo { get; set; }
        public DateTime? UltimaEjecucion { get; set; }
        public string? UltimaFechaDiaria { get; set; }
        public double UltimaDuracionSegundos { get; set; }
        public int TotalEjecuciones { get; set; }
        public int TotalErrores { get; set; }
        public string UltimoOrigenDisparo { get; set; } = "-";
        public string UltimoMensaje { get; set; } = "Sin ejecución previa";
        public string? UltimoError { get; set; }

        // Programación individual del paso
        public bool AutoActivo { get; set; }
        public string ModoProgramacion { get; set; } = "INTERVALO";
        public int IntervaloMinutos { get; set; } = 15;
        public string HoraDiaria { get; set; } = "06:00";
        public string VentanaInicio { get; set; } = "05:00";
        public string VentanaFin { get; set; } = "21:00";
        public DateTime? ProximaEjecucionEstimada { get; set; }
        public int? SegundosParaProximaEjecucion { get; set; }
    }

    public class TraddeSectionSummaryDto
    {
        public string SeccionCodigo { get; set; } = string.Empty;
        public string SeccionNombre { get; set; } = string.Empty;
        public int SeccionOrden { get; set; }
        public int TotalPasos { get; set; }
        public int PasosCompletados { get; set; }
        public int PasosEjecutando { get; set; }
        public int PasosEnCola { get; set; }
        public int PasosConError { get; set; }
        public int PasosOmitidos { get; set; }
        public int PasosAutoActivos { get; set; }
        public int PasosDesfasados { get; set; }
        public int ProgresoSeccionPorcentaje { get; set; }
    }

    public class TraddeEventoLogDto
    {
        public string Timestamp { get; set; } = string.Empty;
        public string StepKey { get; set; } = string.Empty;
        public string NombrePaso { get; set; } = string.Empty;
        public string Tipo { get; set; } = "INFO"; // INFO, SUCCESS, WARN, ERROR
        public string Origen { get; set; } = "MANUAL"; // MANUAL, AUTO
        public string Mensaje { get; set; } = string.Empty;
    }

    public class TraddeUpdateDashboardDto
    {
        public bool AutoGlobalActivo { get; set; }
        public bool GuardarLogsDisco { get; set; } = true;
        public bool RegistrarAuditoriaMongo { get; set; } = true;
        public bool OmitirDependientesSiFallaPadre { get; set; } = true;
        public int DiasRetencionLogs { get; set; } = 15;
        public string MongoConnectionStringMasked { get; set; } = string.Empty;
        public string MongoConnectionString { get; set; } = string.Empty;
        public string MongoDatabase { get; set; } = "tradde-mixtos";
        public string VentanaInicioDefault { get; set; } = "05:00";
        public string VentanaFinDefault { get; set; } = "21:00";
        public int BatchSizeMongo { get; set; } = 10000;

        public bool IsAnyRunning { get; set; }
        public bool IsValidating { get; set; }
        public DateTime? UltimaValidacionGlobal { get; set; }
        public string? CurrentRunningStepKey { get; set; }
        public string? CurrentRunningStepName { get; set; }
        public int QueueCount { get; set; }
        public int TotalSteps { get; set; }
        public int CompletedSteps { get; set; }
        public int ErrorSteps { get; set; }
        public int OmittedSteps { get; set; }
        public int AutoEnabledSteps { get; set; }
        public int DesfasadosSteps { get; set; }
        public int AlineadosSteps { get; set; }
        public long TotalEjecucionesGlobal { get; set; }
        public DateTime? LastWorkerHeartbeat { get; set; }

        public List<TraddeSectionSummaryDto> Secciones { get; set; } = new();
        public List<TraddeStepStatusDto> Pasos { get; set; } = new();
        public List<TraddeEventoLogDto> EventosRecientes { get; set; } = new();
    }

    public class TraddeExecuteRequestDto
    {
        public string? StepKey { get; set; }
        public string? SeccionCodigo { get; set; }
        public List<string>? StepKeys { get; set; }
        public bool EjecutarTodo { get; set; }
        public bool IncluirDependencias { get; set; }
        public bool SoloDesfasados { get; set; }
    }

    public class TraddeValidateRequestDto
    {
        public string? SeccionCodigo { get; set; }
        public string? StepKey { get; set; }
    }

    public class TraddeUpdateGlobalRequestDto
    {
        public bool? AutoGlobalActivo { get; set; }
        public bool? GuardarLogsDisco { get; set; }
        public bool? RegistrarAuditoriaMongo { get; set; }
        public bool? OmitirDependientesSiFallaPadre { get; set; }
        public int? DiasRetencionLogs { get; set; }
        public string? MongoConnectionString { get; set; }
        public string? MongoDatabase { get; set; }
        public string? VentanaInicioDefault { get; set; }
        public string? VentanaFinDefault { get; set; }
        public int? BatchSizeMongo { get; set; }
    }

    public class TraddeConnectionTestResultDto
    {
        public bool ForceOk { get; set; }
        public string ForceMensaje { get; set; } = string.Empty;
        public double ForceLatenciaMs { get; set; }
        public bool MongoOk { get; set; }
        public string MongoMensaje { get; set; } = string.Empty;
        public double MongoLatenciaMs { get; set; }
        public int ColeccionesEncontradas { get; set; }
    }

    public class TraddeLogFileEntryDto
    {
        public string FechaHora { get; set; } = string.Empty;
        public string RunId { get; set; } = string.Empty;
        public string StepKey { get; set; } = string.Empty;
        public string NombrePaso { get; set; } = string.Empty;
        public string ColeccionMongo { get; set; } = string.Empty;
        public string Origen { get; set; } = string.Empty;
        public string Resultado { get; set; } = string.Empty; // EXITO, ERROR, OMITIDO
        public long FilasLeidasForce { get; set; }
        public long DocsInsertadosMongo { get; set; }
        public long DocsActualizadosMongo { get; set; }
        public double DuracionSegundos { get; set; }
        public string Mensaje { get; set; } = string.Empty;
    }

    public class TraddeLogsResponseDto
    {
        public string FechaSeleccionada { get; set; } = string.Empty;
        public bool GuardarLogsDisco { get; set; }
        public List<string> FechasDisponibles { get; set; } = new();
        public List<TraddeLogFileEntryDto> Entradas { get; set; } = new();
    }
}
