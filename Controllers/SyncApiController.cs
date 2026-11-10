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
        private readonly SyncProductosEngine _syncProductosEngine;
        private readonly SyncVendedoresEngine _syncVendedoresEngine;

        public SyncApiController(SyncEngine syncEngine, SyncProductosEngine syncProductosEngine, SyncVendedoresEngine syncVendedoresEngine)
        {
            _syncEngine = syncEngine;
            _syncProductosEngine = syncProductosEngine;
            _syncVendedoresEngine = syncVendedoresEngine;
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
            try
            {
                var preview = await _syncEngine.AnalizarCambiosPendientesAsync(ct);
                return Ok(preview);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Error al analizar diferencias: " + ex.Message, detalle = ex.ToString() });
            }
        }

        [HttpPost("trigger")]
        public async Task<IActionResult> TriggerManual([FromQuery] string modo = "clientes", CancellationToken ct = default)
        {
            if (await _syncEngine.IsRunningAsync())
            {
                // Esperar hasta 15 segundos si el servicio de fondo está por finalizar su ciclo
                var swWait = System.Diagnostics.Stopwatch.StartNew();
                while (await _syncEngine.IsRunningAsync() && swWait.ElapsedMilliseconds < 15000)
                {
                    await Task.Delay(400, ct);
                }

                if (await _syncEngine.IsRunningAsync())
                {
                    return Conflict(new { message = "El servicio automático en segundo plano está sincronizando un lote. Espere unos segundos a que termine para iniciar el proceso manual." });
                }
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
        public async Task<IActionResult> GetLogs([FromQuery] int top = 100, [FromQuery] string? nivel = null, [FromQuery] Guid? loteId = null, CancellationToken ct = default)
        {
            var logs = await _syncEngine.ObtenerUltimosLogsAsync(top, string.IsNullOrWhiteSpace(nivel) ? null : nivel, loteId, ct);
            return Ok(logs);
        }

        [HttpGet("batches")]
        public async Task<IActionResult> GetBatches([FromQuery] int top = 50, CancellationToken ct = default)
        {
            var batches = await _syncEngine.ObtenerUltimasTransaccionesAsync(top, ct);
            return Ok(batches);
        }

        // =========================================================================
        // ENDPOINTS DEL MÓDULO DE PRODUCTOS, CATÁLOGO Y PRECIOS
        // =========================================================================

        [HttpGet("productos/status")]
        public IActionResult GetProductosStatus()
        {
            return Ok(_syncProductosEngine.GetCurrentStatus());
        }

        [HttpGet("productos/preview")]
        public async Task<IActionResult> PreviewCambiosProductos(CancellationToken ct)
        {
            var preview = await _syncProductosEngine.AnalizarCambiosPendientesAsync(ct);
            return Ok(preview);
        }

        [HttpPost("productos/trigger")]
        public async Task<IActionResult> TriggerProductos([FromQuery] string subModulo = "TODOS", CancellationToken ct = default)
        {
            var current = _syncProductosEngine.GetCurrentStatus();
            if (current.IsRunning)
            {
                var swWait = System.Diagnostics.Stopwatch.StartNew();
                while (_syncProductosEngine.GetCurrentStatus().IsRunning && swWait.ElapsedMilliseconds < 15000)
                {
                    await Task.Delay(400, ct);
                }

                if (_syncProductosEngine.GetCurrentStatus().IsRunning)
                {
                    return Conflict(new { message = "El servicio automático en segundo plano está sincronizando Productos. Espere unos segundos a que termine para iniciar el proceso manual." });
                }
            }

            _ = Task.Run(() => _syncProductosEngine.EjecutarSincronizacionProductosAsync(subModulo.ToUpperInvariant(), "PORTAL_WEB", CancellationToken.None));
            return Accepted(new { message = $"Sincronización de Productos ({subModulo}) iniciada." });
        }

        // =========================================================================
        // ENDPOINTS DEL MÓDULO DE VENDEDORES Y EQUIPOS COMERCIALES
        // =========================================================================

        [HttpGet("vendedores/status")]
        public IActionResult GetVendedoresStatus()
        {
            return Ok(_syncVendedoresEngine.GetCurrentStatus());
        }

        [HttpGet("vendedores/preview")]
        public async Task<IActionResult> PreviewCambiosVendedores(CancellationToken ct)
        {
            var preview = await _syncVendedoresEngine.AnalizarCambiosPendientesAsync(ct);
            return Ok(preview);
        }

        [HttpPost("vendedores/trigger")]
        public async Task<IActionResult> TriggerVendedores(CancellationToken ct = default)
        {
            var current = _syncVendedoresEngine.GetCurrentStatus();
            if (current.IsRunning)
            {
                var swWait = System.Diagnostics.Stopwatch.StartNew();
                while (_syncVendedoresEngine.GetCurrentStatus().IsRunning && swWait.ElapsedMilliseconds < 15000)
                {
                    await Task.Delay(400, ct);
                }

                if (_syncVendedoresEngine.GetCurrentStatus().IsRunning)
                {
                    return Conflict(new { message = "El servicio automático en segundo plano está sincronizando Vendedores. Espere unos segundos a que termine para iniciar el proceso manual." });
                }
            }

            _ = Task.Run(() => _syncVendedoresEngine.EjecutarSincronizacionVendedoresAsync("MANUAL", "PORTAL_WEB", CancellationToken.None));
            return Accepted(new { message = "Sincronización del Módulo de Vendedores iniciada." });
        }
    }
}
