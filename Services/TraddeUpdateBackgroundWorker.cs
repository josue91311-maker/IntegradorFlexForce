using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IntegradorFlexForce.Services
{
    public class TraddeUpdateBackgroundWorker : BackgroundService
    {
        private readonly TraddeUpdateEngine _engine;
        private readonly ILogger<TraddeUpdateBackgroundWorker> _logger;

        public TraddeUpdateBackgroundWorker(
            TraddeUpdateEngine engine,
            ILogger<TraddeUpdateBackgroundWorker> logger)
        {
            _engine = engine;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("TraddeUpdateBackgroundWorker iniciado (BDForceGVD -> MongoDB tradde-mixtos).");

            var consumerTask = Task.Run(async () =>
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        await _engine.ConsumirColaAsync(stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error en consumidor de cola TraddeUpdate. Reiniciando en 5s...");
                        try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); } catch { break; }
                    }
                }
            }, stoppingToken);

            var schedulerTask = Task.Run(async () =>
            {
                // Espera breve al arranque del servidor antes de evaluar programaciones
                try { await Task.Delay(TimeSpan.FromSeconds(8), stoppingToken); } catch { return; }

                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
                while (!stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        _engine.EvaluarProgramacionesAutomaticas();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error evaluando programaciones automáticas de TraddeUpdate.");
                    }

                    try
                    {
                        if (!await timer.WaitForNextTickAsync(stoppingToken))
                            break;
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }, stoppingToken);

            await Task.WhenAll(consumerTask, schedulerTask);
            _logger.LogInformation("TraddeUpdateBackgroundWorker detenido.");
        }
    }
}
