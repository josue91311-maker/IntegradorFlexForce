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
        private readonly SyncProductosEngine _syncProductosEngine;
        private readonly ILogger<SyncBackgroundWorker> _logger;
        private static readonly SemaphoreSlim _cycleLock = new(1, 1);

        public SyncBackgroundWorker(SyncEngine syncEngine, SyncProductosEngine syncProductosEngine, ILogger<SyncBackgroundWorker> logger)
        {
            _syncEngine = syncEngine;
            _syncProductosEngine = syncProductosEngine;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Iniciando servicio de fondo profesional SyncBackgroundWorker (Clientes + Productos)...");

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
                        _logger.LogInformation("Modo manual activo: La sincronización automática en segundo plano está pausada.");
                    }
                    else if (!EstaEnVentanaHoraria(config.HoraInicioVentana, config.HoraFinVentana))
                    {
                        _logger.LogInformation("Fuera de la ventana horaria permitida ({Inicio} - {Fin}). Próxima revisión en {Min} min.", 
                            config.HoraInicioVentana, config.HoraFinVentana, delayMinutes);
                    }
                    else
                    {
                        // Control de concurrencia profesional: nunca cruzarse ni bloquearse
                        if (await _cycleLock.WaitAsync(0, stoppingToken))
                        {
                            try
                            {
                                // 1. Ciclo automático Módulo Clientes (Inteligente: salta entidades al día)
                                if (!await _syncEngine.IsRunningAsync())
                                {
                                    _logger.LogInformation("Ejecutando ciclo automático inteligente Módulo Clientes...");
                                    await _syncEngine.EjecutarSincronizacionClientesAsync("AUTOMATICO", "WORKER_SERVICE", incluirZonas: true, stoppingToken);
                                }

                                // 2. Ciclo automático Módulo Productos (Inteligente: salta entidades al día)
                                if (!_syncProductosEngine.GetCurrentStatus().IsRunning)
                                {
                                    _logger.LogInformation("Ejecutando ciclo automático inteligente Módulo Productos...");
                                    await _syncProductosEngine.EjecutarSincronizacionProductosAsync("TODOS", "WORKER_SERVICE", stoppingToken);
                                }
                            }
                            finally
                            {
                                _cycleLock.Release();
                            }
                        }
                        else
                        {
                            _logger.LogWarning("Ciclo automático omitido: ya existe un proceso de sincronización en ejecución.");
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error en SyncBackgroundWorker. Reintentando en {Min} min.", delayMinutes);
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
