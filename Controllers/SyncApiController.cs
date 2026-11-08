using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using IntegradorFlexForce.Models;
using IntegradorFlexForce.Services;

namespace IntegradorFlexForce.Controllers
{
    [ApiController]
    [Route("api/sync")]
    public class SyncApiController : ControllerBase
    {
        private readonly SyncEngine _syncEngine;

        public SyncApiController(SyncEngine syncEngine)
        {
            _syncEngine = syncEngine;
        }

        [HttpGet("status")]
        public async Task<IActionResult> GetStatus(CancellationToken ct)
        {
            var status = _syncEngine.GetStatus();
            var config = await _syncEngine.CargarConfiguracionAsync(ct);
            status.SincronizacionActiva = config.SincronizacionActiva;
            status.IntervaloMinutos = config.IntervaloMinutos;
            status.VentanaHoraria = $"{config.HoraInicioVentana} - {config.HoraFinVentana}";

            if (status.UltimaEjecucion == null)
            {
                var ultimosLotes = await _syncEngine.ObtenerUltimasTransaccionesAsync(1, ct);
                if (ultimosLotes.Count > 0)
                {
                    var ultimo = ultimosLotes[0];
                    status.UltimaEjecucion = ultimo.FechaFin ?? ultimo.FechaInicio;
                    status.UltimaDuracionSegundos = (double)(ultimo.DuracionSegundos ?? 0);
                    status.UltimoLoteProcesados = (ultimo.TotalNuevos + ultimo.TotalModificados > 0)
                        ? (ultimo.TotalNuevos + ultimo.TotalModificados)
                        : ultimo.TotalEscaneados;
                    status.UltimoLoteNuevos = ultimo.TotalNuevos;
                    status.UltimoLoteModificados = ultimo.TotalModificados;
                    status.UltimoLoteErrores = ultimo.TotalErrores;
                    if (status.EstadoActual == "EN_ESPERA" && ultimo.Estado == "EXITOSO")
                    {
                        status.EstadoActual = "COMPLETADO";
                        status.FaseDetalle = $"Lote #{ultimo.ID} finalizado con {status.UltimoLoteProcesados:N0} registros procesados ({ultimo.TotalErrores} errores).";
                    }
                }
            }

            return Ok(status);
        }

        [HttpGet("preview")]
        public async Task<IActionResult> PreviewCambios(CancellationToken ct)
        {
            var preview = await _syncEngine.AnalizarCambiosPendientesAsync(ct);
            return Ok(preview);
        }

        [HttpPost("trigger")]
        public async Task<IActionResult> TriggerManual([FromQuery] string modo = "clientes", CancellationToken ct = default)
        {
            if (await _syncEngine.IsRunningAsync())
            {
                return Conflict(new { message = "Ya hay un proceso de sincronización en ejecución." });
            }

            if (modo == "zonas")
            {
                _ = Task.Run(() => _syncEngine.EjecutarSincronizacionZonasAsync("MANUAL", "PORTAL_WEB", CancellationToken.None));
                return Accepted(new { message = "Sincronización manual de Zonas y Rutas iniciada." });
            }
            else if (modo == "todo")
            {
                _ = Task.Run(() => _syncEngine.EjecutarSincronizacionClientesAsync("MANUAL", "PORTAL_WEB", incluirZonas: true, CancellationToken.None));
                return Accepted(new { message = "Sincronización completa (Clientes + Direcciones + Zonas) iniciada." });
            }
            else
            {
                _ = Task.Run(() => _syncEngine.EjecutarSincronizacionClientesAsync("MANUAL", "PORTAL_WEB", incluirZonas: false, CancellationToken.None));
                return Accepted(new { message = "Sincronización manual de Clientes y Direcciones iniciada." });
            }
        }

        [HttpGet("config")]
        public async Task<IActionResult> GetConfig(CancellationToken ct)
        {
            var config = await _syncEngine.CargarConfiguracionAsync(ct);
            return Ok(config);
        }

        [HttpPost("config")]
        public async Task<IActionResult> SaveConfig([FromBody] SyncConfigDto config, CancellationToken ct)
        {
            await _syncEngine.GuardarConfiguracionAsync(config, ct);
            return Ok(new { message = "Configuración actualizada exitosamente." });
        }

        [HttpGet("logs")]
        public async Task<IActionResult> GetLogs([FromQuery] int top = 100, [FromQuery] string? nivel = null, CancellationToken ct = default)
        {
            var logs = await _syncEngine.ObtenerUltimosLogsAsync(top, string.IsNullOrWhiteSpace(nivel) ? null : nivel, ct);
            return Ok(logs);
        }

        [HttpGet("batches")]
        public async Task<IActionResult> GetBatches([FromQuery] int top = 20, CancellationToken ct = default)
        {
            var batches = await _syncEngine.ObtenerUltimasTransaccionesAsync(top, ct);
            return Ok(batches);
        }
    }
}
