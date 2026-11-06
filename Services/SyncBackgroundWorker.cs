using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IntegradorFlexForce.Services
{
    public class SyncBackgroundWorker : BackgroundService
    {
        private readonly SyncEngine _syncEngine;
        private readonly ILogger<SyncBackgroundWorker> _logger;

        public SyncBackgroundWorker(SyncEngine syncEngine, ILogger<SyncBackgroundWorker> logger)
        {
            _syncEngine = syncEngine;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Iniciando servicio de fondo SyncBackgroundWorker...");

            // Esperar 10 segundos tras el arranque antes del primer ciclo
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                int delayMinutes = 3;

                try
                {
                    var config = await _syncEngine.CargarConfiguracionAsync(stoppingToken);
                    delayMinutes = config.IntervaloMinutos > 0 ? config.IntervaloMinutos : 3;

                    if (!config.SincronizacionActiva)
                    {
                        _logger.LogInformation("Sincronización automática está pausada según sync_Configuracion. Próxima revisión en {Min} min.", delayMinutes);
                    }
                    else if (!EstaEnVentanaHoraria(config.HoraInicioVentana, config.HoraFinVentana))
                    {
                        _logger.LogInformation("Fuera de la ventana horaria permitida ({Inicio} - {Fin}). Próxima revisión en {Min} min.", 
                            config.HoraInicioVentana, config.HoraFinVentana, delayMinutes);
                    }
                    else
                    {
                        _logger.LogInformation("Iniciando ciclo automático de sincronización...");
                        await _syncEngine.EjecutarSincronizacionClientesAsync("AUTOMATICO", "WORKER_SERVICE", stoppingToken);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error no controlado en SyncBackgroundWorker. Reintentando en {Min} min.", delayMinutes);
                }

                await Task.Delay(TimeSpan.FromMinutes(delayMinutes), stoppingToken);
            }

            _logger.LogInformation("Servicio de fondo SyncBackgroundWorker detenido.");
        }

        private static bool EstaEnVentanaHoraria(string horaInicioStr, string horaFinStr)
        {
            if (!TimeSpan.TryParse(horaInicioStr, out var inicio) || !TimeSpan.TryParse(horaFinStr, out var fin))
            {
                return true; // Si no hay formato válido, se permite
            }

            var ahora = DateTime.Now.TimeOfDay;
            if (inicio <= fin)
            {
                return ahora >= inicio && ahora <= fin;
            }
            // Caso ventana cruzando medianoche (ej: 22:00 a 06:00)
            return ahora >= inicio || ahora <= fin;
        }
    }
}
