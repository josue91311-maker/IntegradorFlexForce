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
        private readonly SyncVendedoresEngine _syncVendedoresEngine;
        private readonly ILogger<SyncBackgroundWorker> _logger;
        private static readonly SemaphoreSlim _cycleLock = new(1, 1);

        private DateTime? _ultimaEjecucionClientes;
        private DateTime? _ultimaEjecucionProductos;
        private DateTime? _ultimaEjecucionVendedores;
        private DateTime? _ultimoDiaMadrugadaClientes;
        private DateTime? _ultimoDiaMadrugadaProductos;
        private DateTime? _ultimoDiaMadrugadaVendedores;

        public SyncBackgroundWorker(SyncEngine syncEngine, SyncProductosEngine syncProductosEngine, SyncVendedoresEngine syncVendedoresEngine, ILogger<SyncBackgroundWorker> logger)
        {
            _syncEngine = syncEngine;
            _syncProductosEngine = syncProductosEngine;
            _syncVendedoresEngine = syncVendedoresEngine;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Iniciando servicio de fondo profesional SyncBackgroundWorker con control modular independiente y reintentos...");

            // Esperar 10 segundos tras el arranque antes del primer ciclo
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var config = await _syncEngine.CargarConfiguracionAsync(stoppingToken);

                    if (!config.SincronizacionActiva)
                    {
                        // Modo Manual global activo
                        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
                        continue;
                    }

                    // 1. EVALUAR EJECUCIÓN MODULAR: CLIENTES, RUTAS Y CRÉDITOS
                    if (config.AutoClientesActivo && DebeEjecutarseClientes(config))
                    {
                        await EjecutarClientesConReintentosAsync(config, stoppingToken);
                    }

                    // 2. EVALUAR EJECUCIÓN MODULAR: PRODUCTOS, PRECIOS Y CATÁLOGOS
                    if (config.AutoProductosActivo && DebeEjecutarseProductos(config))
                    {
                        await EjecutarProductosConReintentosAsync(config, stoppingToken);
                    }

                    // 3. EVALUAR EJECUCIÓN MODULAR: VENDEDORES Y EQUIPOS
                    if (config.AutoVendedoresActivo && DebeEjecutarseVendedores(config))
                    {
                        await EjecutarVendedoresConReintentosAsync(config, stoppingToken);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error en ciclo principal de SyncBackgroundWorker.");
                }

                // Ciclo de evaluación constante cada 20 segundos
                await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
            }

            _logger.LogInformation("Servicio de fondo SyncBackgroundWorker detenido.");
        }

        private bool DebeEjecutarseClientes(Models.SyncConfigDto config)
        {
            var ahora = DateTime.Now;

            if (config.ModoClientes == "MADRUGADA")
            {
                // Modo Madrugada: 1 vez al día en la hora configurada (ej: 02:00)
                if (_ultimoDiaMadrugadaClientes.HasValue && _ultimoDiaMadrugadaClientes.Value.Date == ahora.Date)
                {
                    return false; // Ya se ejecutó hoy en la madrugada
                }
                return EsHoraObjetivo(config.HoraMadrugadaClientes, ahora);
            }
            else
            {
                // Modo Intervalo periódico dentro de ventana horaria
                string horaInicio = string.IsNullOrWhiteSpace(config.HoraInicioClientes) ? config.HoraInicioVentana : config.HoraInicioClientes;
                string horaFin = string.IsNullOrWhiteSpace(config.HoraFinClientes) ? config.HoraFinVentana : config.HoraFinClientes;

                if (!EstaEnVentanaHoraria(horaInicio, horaFin))
                {
                    return false;
                }

                int intervaloMin = config.IntervaloClientesMinutos > 0 ? config.IntervaloClientesMinutos : (config.IntervaloMinutos > 0 ? config.IntervaloMinutos : 3);
                if (_ultimaEjecucionClientes == null)
                {
                    return true;
                }

                return (ahora - _ultimaEjecucionClientes.Value).TotalMinutes >= intervaloMin;
            }
        }

        private bool DebeEjecutarseProductos(Models.SyncConfigDto config)
        {
            var ahora = DateTime.Now;

            if (config.ModoProductos == "MADRUGADA")
            {
                // Modo Madrugada: 1 vez al día en la hora configurada (ej: 03:30)
                if (_ultimoDiaMadrugadaProductos.HasValue && _ultimoDiaMadrugadaProductos.Value.Date == ahora.Date)
                {
                    return false; // Ya se ejecutó hoy en la madrugada
                }
                return EsHoraObjetivo(config.HoraMadrugadaProductos, ahora);
            }
            else
            {
                // Modo Intervalo periódico dentro de ventana horaria
                string horaInicio = string.IsNullOrWhiteSpace(config.HoraInicioProductos) ? config.HoraInicioVentana : config.HoraInicioProductos;
                string horaFin = string.IsNullOrWhiteSpace(config.HoraFinProductos) ? config.HoraFinVentana : config.HoraFinProductos;

                if (!EstaEnVentanaHoraria(horaInicio, horaFin))
                {
                    return false;
                }

                int intervaloMin = config.IntervaloProductosMinutos > 0 ? config.IntervaloProductosMinutos : (config.IntervaloMinutos > 0 ? config.IntervaloMinutos : 3);
                if (_ultimaEjecucionProductos == null)
                {
                    return true;
                }

                return (ahora - _ultimaEjecucionProductos.Value).TotalMinutes >= intervaloMin;
            }
        }

        private bool DebeEjecutarseVendedores(Models.SyncConfigDto config)
        {
            var ahora = DateTime.Now;

            if (config.ModoVendedores == "MADRUGADA")
            {
                // Modo Madrugada: 1 vez al día en la hora configurada (ej: 04:00)
                if (_ultimoDiaMadrugadaVendedores.HasValue && _ultimoDiaMadrugadaVendedores.Value.Date == ahora.Date)
                {
                    return false;
                }
                return EsHoraObjetivo(config.HoraMadrugadaVendedores, ahora);
            }
            else
            {
                // Modo Intervalo periódico dentro de ventana horaria
                string horaInicio = string.IsNullOrWhiteSpace(config.HoraInicioVendedores) ? config.HoraInicioVentana : config.HoraInicioVendedores;
                string horaFin = string.IsNullOrWhiteSpace(config.HoraFinVendedores) ? config.HoraFinVentana : config.HoraFinVendedores;

                if (!EstaEnVentanaHoraria(horaInicio, horaFin))
                {
                    return false;
                }

                int intervaloMin = config.IntervaloVendedoresMinutos > 0 ? config.IntervaloVendedoresMinutos : 5;
                if (_ultimaEjecucionVendedores == null)
                {
                    return true;
                }

                return (ahora - _ultimaEjecucionVendedores.Value).TotalMinutes >= intervaloMin;
            }
        }

        private async Task EjecutarClientesConReintentosAsync(Models.SyncConfigDto config, CancellationToken ct)
        {
            int maxIntentos = config.HabilitarReintentos ? Math.Max(1, config.ReintentosMaximos) : 1;
            int esperaSegundos = config.EsperaReintentoSegundos > 0 ? config.EsperaReintentoSegundos : 30;

            for (int intento = 1; intento <= maxIntentos; intento++)
            {
                if (ct.IsCancellationRequested) break;

                bool lockTomado = false;
                try
                {
                    if (await _syncEngine.IsRunningAsync())
                    {
                        _logger.LogInformation("Módulo Clientes ya se encuentra en ejecución manual o previa. Omitiendo ciclo automático.");
                        return;
                    }

                    if (await _cycleLock.WaitAsync(0, ct))
                    {
                        lockTomado = true;
                        _logger.LogInformation("Ejecutando ciclo automático Módulo Clientes (Intento {Intento}/{Max})...", intento, maxIntentos);
                        await _syncEngine.EjecutarSincronizacionClientesAsync("AUTOMATICO", "WORKER_SERVICE", incluirZonas: true, ct);
                        
                        _ultimaEjecucionClientes = DateTime.Now;
                        if (config.ModoClientes == "MADRUGADA")
                        {
                            _ultimoDiaMadrugadaClientes = DateTime.Today;
                        }
                        return; // Éxito total
                    }
                    else
                    {
                        _logger.LogWarning("Ciclo Clientes omitido: semáforo de sincronización ocupado por otro módulo.");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Fallo en ejecución automática de Clientes (Intento {Intento}/{Max}).", intento, maxIntentos);
                    if (intento < maxIntentos)
                    {
                        _logger.LogInformation("Esperando {Seg}s antes de reintentar Módulo Clientes...", esperaSegundos);
                        await Task.Delay(TimeSpan.FromSeconds(esperaSegundos), ct);
                    }
                    else
                    {
                        _logger.LogError("Se agotaron los {Max} reintentos de Módulo Clientes tras caída o error de conexión.", maxIntentos);
                        _ultimaEjecucionClientes = DateTime.Now; // Registrar tiempo para evitar bucle infinito inmediato
                    }
                }
                finally
                {
                    if (lockTomado) _cycleLock.Release();
                }
            }
        }

        private async Task EjecutarProductosConReintentosAsync(Models.SyncConfigDto config, CancellationToken ct)
        {
            int maxIntentos = config.HabilitarReintentos ? Math.Max(1, config.ReintentosMaximos) : 1;
            int esperaSegundos = config.EsperaReintentoSegundos > 0 ? config.EsperaReintentoSegundos : 30;

            for (int intento = 1; intento <= maxIntentos; intento++)
            {
                if (ct.IsCancellationRequested) break;

                bool lockTomado = false;
                try
                {
                    if (_syncProductosEngine.GetCurrentStatus().IsRunning)
                    {
                        _logger.LogInformation("Módulo Productos ya se encuentra en ejecución manual o previa. Omitiendo ciclo automático.");
                        return;
                    }

                    if (await _cycleLock.WaitAsync(0, ct))
                    {
                        lockTomado = true;
                        _logger.LogInformation("Ejecutando ciclo automático Módulo Productos (Intento {Intento}/{Max})...", intento, maxIntentos);
                        await _syncProductosEngine.EjecutarSincronizacionProductosAsync("TODOS", "WORKER_SERVICE", ct);

                        _ultimaEjecucionProductos = DateTime.Now;
                        if (config.ModoProductos == "MADRUGADA")
                        {
                            _ultimoDiaMadrugadaProductos = DateTime.Today;
                        }
                        return; // Éxito total
                    }
                    else
                    {
                        _logger.LogWarning("Ciclo Productos omitido: semáforo de sincronización ocupado por otro módulo.");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Fallo en ejecución automática de Productos (Intento {Intento}/{Max}).", intento, maxIntentos);
                    if (intento < maxIntentos)
                    {
                        _logger.LogInformation("Esperando {Seg}s antes de reintentar Módulo Productos...", esperaSegundos);
                        await Task.Delay(TimeSpan.FromSeconds(esperaSegundos), ct);
                    }
                    else
                    {
                        _logger.LogError("Se agotaron los {Max} reintentos de Módulo Productos tras caída o error de conexión.", maxIntentos);
                        _ultimaEjecucionProductos = DateTime.Now; // Registrar tiempo para evitar bucle infinito inmediato
                    }
                }
                finally
                {
                    if (lockTomado) _cycleLock.Release();
                }
            }
        }

        private async Task EjecutarVendedoresConReintentosAsync(Models.SyncConfigDto config, CancellationToken ct)
        {
            int maxIntentos = config.HabilitarReintentos ? Math.Max(1, config.ReintentosMaximos) : 1;
            int esperaSegundos = config.EsperaReintentoSegundos > 0 ? config.EsperaReintentoSegundos : 30;

            for (int intento = 1; intento <= maxIntentos; intento++)
            {
                if (ct.IsCancellationRequested) break;

                bool lockTomado = false;
                try
                {
                    if (_syncVendedoresEngine.GetCurrentStatus().IsRunning)
                    {
                        _logger.LogInformation("Módulo Vendedores ya se encuentra en ejecución manual o previa. Omitiendo ciclo automático.");
                        return;
                    }

                    if (await _cycleLock.WaitAsync(0, ct))
                    {
                        lockTomado = true;
                        _logger.LogInformation("Ejecutando ciclo automático Módulo Vendedores (Intento {Intento}/{Max})...", intento, maxIntentos);
                        await _syncVendedoresEngine.EjecutarSincronizacionVendedoresAsync("AUTOMATICO", "WORKER_SERVICE", ct);

                        _ultimaEjecucionVendedores = DateTime.Now;
                        if (config.ModoVendedores == "MADRUGADA")
                        {
                            _ultimoDiaMadrugadaVendedores = DateTime.Today;
                        }
                        return;
                    }
                    else
                    {
                        _logger.LogWarning("Ciclo Vendedores omitido: semáforo de sincronización ocupado por otro módulo.");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Fallo en ejecución automática de Vendedores (Intento {Intento}/{Max}).", intento, maxIntentos);
                    if (intento < maxIntentos)
                    {
                        _logger.LogInformation("Esperando {Seg}s antes de reintentar Módulo Vendedores...", esperaSegundos);
                        await Task.Delay(TimeSpan.FromSeconds(esperaSegundos), ct);
                    }
                    else
                    {
                        _logger.LogError("Se agotaron los {Max} reintentos de Módulo Vendedores tras caída o error de conexión.", maxIntentos);
                        _ultimaEjecucionVendedores = DateTime.Now;
                    }
                }
                finally
                {
                    if (lockTomado) _cycleLock.Release();
                }
            }
        }

        private static bool EsHoraObjetivo(string horaObjetivoStr, DateTime ahora)
        {
            if (!TimeSpan.TryParse(horaObjetivoStr, out var objetivo))
            {
                // Si no tiene formato válido, probar 02:00
                objetivo = new TimeSpan(2, 0, 0);
            }

            var horaActual = ahora.TimeOfDay;
            // Ventana de coincidencia de 10 minutos
            var diff = Math.Abs((horaActual - objetivo).TotalMinutes);
            return diff <= 10;
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
