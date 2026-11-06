using System;
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
        public async Task<IActionResult> GetStatus()
        {
            var status = _syncEngine.GetStatus();
            status.IsRunning = await _syncEngine.IsRunningAsync();
            return Ok(status);
        }

        [HttpPost("trigger")]
        public async Task<IActionResult> TriggerManualSync([FromQuery] string usuario = "PORTAL_WEB")
        {
            if (await _syncEngine.IsRunningAsync())
            {
                return Conflict(new { message = "Ya existe una sincronización en curso. Por favor espere a que finalice." });
            }

            // Iniciar en segundo plano para no congelar la petición web
            _ = Task.Run(async () =>
            {
                await _syncEngine.EjecutarSincronizacionClientesAsync("MANUAL", usuario);
            });

            return Accepted(new { message = "Sincronización manual iniciada exitosamente." });
        }

        [HttpGet("config")]
        public async Task<IActionResult> GetConfig()
        {
            var config = await _syncEngine.CargarConfiguracionAsync();
            return Ok(config);
        }

        [HttpPost("config")]
        public async Task<IActionResult> SaveConfig([FromBody] SyncConfigDto config)
        {
            await _syncEngine.GuardarConfiguracionAsync(config);
            return Ok(new { message = "Configuración actualizada correctamente en BDForceGVD.", config });
        }

        [HttpGet("logs")]
        public async Task<IActionResult> GetLogs([FromQuery] int cantidad = 100, [FromQuery] string? nivel = null)
        {
            var logs = await _syncEngine.ObtenerUltimosLogsAsync(cantidad, nivel);
            return Ok(logs);
        }

        [HttpGet("batches")]
        public async Task<IActionResult> GetBatches([FromQuery] int cantidad = 20)
        {
            var batches = await _syncEngine.ObtenerUltimasTransaccionesAsync(cantidad);
            return Ok(batches);
        }
    }
}
