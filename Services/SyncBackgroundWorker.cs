using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using IntegradorFlexForce.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IntegradorFlexForce.Services
{
    public class SyncBackgroundWorker : BackgroundService
    {
        private readonly SyncEngine _syncEngine;
        private readonly SyncProductosEngine _syncProductosEngine;
        private readonly SyncVendedoresEngine _syncVendedoresEngine;
        private readonly SyncStockEngine _syncStockEngine;
        private readonly ILogger<SyncBackgroundWorker> _logger;
        private static readonly SemaphoreSlim _cycleLock = new(1, 1);

        private DateTime? _ultimaEjecucionClientes;
        private DateTime? _ultimaEjecucionProductos;
        private DateTime? _ultimaEjecucionVendedores;
        private DateTime? _ultimaEjecucionStock;
        private DateTime? _ultimoDiaMadrugadaClientes;
        private DateTime? _ultimoDiaMadrugadaProductos;
        private DateTime? _ultimoDiaMadrugadaVendedores;
        private DateTime? _ultimoDiaMadrugadaStock;
        private DateTime? _workerHeartbeat;

        private sealed class ModuleLiveTelemetry
        {
            public bool EnEjecucion { get; set; }
            public bool Reintentando { get; set; }
            public int IntentoActual { get; set; }
            public int MaxIntentos { get; set; }
            public DateTime? UltimoExito { get; set; }
            public string? UltimoError { get; set; }
            public string UltimoResultado { get; set; } = "SIN_DATOS";
        }

        private readonly ConcurrentDictionary<string, ModuleLiveTelemetry> _telemetria = new()
        {
            ["CLIENTES"] = new(),
            ["PRODUCTOS"] = new(),
            ["VENDEDORES"] = new(),
            ["STOCK"] = new()
        };

        public DateTime? GetLastHeartbeat() => _workerHeartbeat;

        public SyncBackgroundWorker(
            SyncEngine syncEngine, 
            SyncProductosEngine syncProductosEngine, 
            SyncVendedoresEngine syncVendedoresEngine,
            SyncStockEngine syncStockEngine,
            ILogger<SyncBackgroundWorker> logger)
        {
            _syncEngine = syncEngine;
            _syncProductosEngine = syncProductosEngine;
            _syncVendedoresEngine = syncVendedoresEngine;
            _syncStockEngine = syncStockEngine;
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
                    _workerHeartbeat = DateTime.Now;
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

                    // 4. EVALUAR EJECUCIÓN MODULAR: STOCK EN LÍNEA POR ALMACÉN
                    if (config.AutoStockActivo && DebeEjecutarseStock(config))
                    {
                        await EjecutarStockConReintentosAsync(config, stoppingToken);
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

        private bool DebeEjecutarseStock(Models.SyncConfigDto config)
        {
            var ahora = DateTime.Now;

            if (config.ModoStock == "MADRUGADA")
            {
                // Modo Madrugada: 1 vez al día en la hora configurada (ej: 04:30)
                if (_ultimoDiaMadrugadaStock.HasValue && _ultimoDiaMadrugadaStock.Value.Date == ahora.Date)
                {
                    return false;
                }
                return EsHoraObjetivo(config.HoraMadrugadaStock, ahora);
            }
            else
            {
                // Modo Intervalo periódico dentro de ventana horaria
                string horaInicio = string.IsNullOrWhiteSpace(config.HoraInicioStock) ? config.HoraInicioVentana : config.HoraInicioStock;
                string horaFin = string.IsNullOrWhiteSpace(config.HoraFinStock) ? config.HoraFinVentana : config.HoraFinStock;

                if (!EstaEnVentanaHoraria(horaInicio, horaFin))
                {
                    return false;
                }

                int intervaloMin = config.IntervaloStockMinutos > 0 ? config.IntervaloStockMinutos : 2;
                if (_ultimaEjecucionStock == null)
                {
                    return true;
                }

                return (ahora - _ultimaEjecucionStock.Value).TotalMinutes >= intervaloMin;
            }
        }

        private async Task EjecutarClientesConReintentosAsync(Models.SyncConfigDto config, CancellationToken ct)
        {
            int maxIntentos = config.HabilitarReintentos ? Math.Max(1, config.ReintentosMaximos) : 1;
            int esperaSegundos = config.EsperaReintentoSegundos > 0 ? config.EsperaReintentoSegundos : 30;
            var tel = _telemetria["CLIENTES"];

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
                        tel.EnEjecucion = true;
                        tel.Reintentando = intento > 1;
                        tel.IntentoActual = intento;
                        tel.MaxIntentos = maxIntentos;
                        _logger.LogInformation("Ejecutando ciclo automático Módulo Clientes (Intento {Intento}/{Max})...", intento, maxIntentos);
                        await _syncEngine.EjecutarSincronizacionClientesAsync("AUTOMATICO", "WORKER_SERVICE", incluirZonas: true, ct);
                        
                        _ultimaEjecucionClientes = DateTime.Now;
                        tel.UltimoExito = _ultimaEjecucionClientes;
                        tel.UltimoResultado = "COMPLETADO";
                        tel.UltimoError = null;
                        tel.Reintentando = false;
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
                    tel.UltimoError = ex.Message;
                    _logger.LogWarning(ex, "Fallo en ejecución automática de Clientes (Intento {Intento}/{Max}).", intento, maxIntentos);
                    if (intento < maxIntentos)
                    {
                        tel.Reintentando = true;
                        _logger.LogInformation("Esperando {Seg}s antes de reintentar Módulo Clientes...", esperaSegundos);
                        await Task.Delay(TimeSpan.FromSeconds(esperaSegundos), ct);
                    }
                    else
                    {
                        _logger.LogError("Se agotaron los {Max} reintentos de Módulo Clientes tras caída o error de conexión.", maxIntentos);
                        _ultimaEjecucionClientes = DateTime.Now; // Registrar tiempo para evitar bucle infinito inmediato
                        tel.UltimoResultado = "ERROR";
                        tel.Reintentando = false;
                    }
                }
                finally
                {
                    tel.EnEjecucion = false;
                    if (lockTomado) _cycleLock.Release();
                }
            }
        }

        private async Task EjecutarProductosConReintentosAsync(Models.SyncConfigDto config, CancellationToken ct)
        {
            int maxIntentos = config.HabilitarReintentos ? Math.Max(1, config.ReintentosMaximos) : 1;
            int esperaSegundos = config.EsperaReintentoSegundos > 0 ? config.EsperaReintentoSegundos : 30;
            var tel = _telemetria["PRODUCTOS"];

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
                        tel.EnEjecucion = true;
                        tel.Reintentando = intento > 1;
                        tel.IntentoActual = intento;
                        tel.MaxIntentos = maxIntentos;
                        _logger.LogInformation("Ejecutando ciclo automático Módulo Productos (Intento {Intento}/{Max})...", intento, maxIntentos);
                        await _syncProductosEngine.EjecutarSincronizacionProductosAsync("TODOS", "WORKER_SERVICE", ct);

                        _ultimaEjecucionProductos = DateTime.Now;
                        tel.UltimoExito = _ultimaEjecucionProductos;
                        tel.UltimoResultado = "COMPLETADO";
                        tel.UltimoError = null;
                        tel.Reintentando = false;
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
                    tel.UltimoError = ex.Message;
                    _logger.LogWarning(ex, "Fallo en ejecución automática de Productos (Intento {Intento}/{Max}).", intento, maxIntentos);
                    if (intento < maxIntentos)
                    {
                        tel.Reintentando = true;
                        _logger.LogInformation("Esperando {Seg}s antes de reintentar Módulo Productos...", esperaSegundos);
                        await Task.Delay(TimeSpan.FromSeconds(esperaSegundos), ct);
                    }
                    else
                    {
                        _logger.LogError("Se agotaron los {Max} reintentos de Módulo Productos tras caída o error de conexión.", maxIntentos);
                        _ultimaEjecucionProductos = DateTime.Now; // Registrar tiempo para evitar bucle infinito inmediato
                        tel.UltimoResultado = "ERROR";
                        tel.Reintentando = false;
                    }
                }
                finally
                {
                    tel.EnEjecucion = false;
                    if (lockTomado) _cycleLock.Release();
                }
            }
        }

        private async Task EjecutarVendedoresConReintentosAsync(Models.SyncConfigDto config, CancellationToken ct)
        {
            int maxIntentos = config.HabilitarReintentos ? Math.Max(1, config.ReintentosMaximos) : 1;
            int esperaSegundos = config.EsperaReintentoSegundos > 0 ? config.EsperaReintentoSegundos : 30;
            var tel = _telemetria["VENDEDORES"];

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
                        tel.EnEjecucion = true;
                        tel.Reintentando = intento > 1;
                        tel.IntentoActual = intento;
                        tel.MaxIntentos = maxIntentos;
                        _logger.LogInformation("Ejecutando ciclo automático Módulo Vendedores (Intento {Intento}/{Max})...", intento, maxIntentos);
                        await _syncVendedoresEngine.EjecutarSincronizacionVendedoresAsync("AUTOMATICO", "WORKER_SERVICE", ct);

                        _ultimaEjecucionVendedores = DateTime.Now;
                        tel.UltimoExito = _ultimaEjecucionVendedores;
                        tel.UltimoResultado = "COMPLETADO";
                        tel.UltimoError = null;
                        tel.Reintentando = false;
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
                    tel.UltimoError = ex.Message;
                    _logger.LogWarning(ex, "Fallo en ejecución automática de Vendedores (Intento {Intento}/{Max}).", intento, maxIntentos);
                    if (intento < maxIntentos)
                    {
                        tel.Reintentando = true;
                        _logger.LogInformation("Esperando {Seg}s antes de reintentar Módulo Vendedores...", esperaSegundos);
                        await Task.Delay(TimeSpan.FromSeconds(esperaSegundos), ct);
                    }
                    else
                    {
                        _logger.LogError("Se agotaron los {Max} reintentos de Módulo Vendedores tras caída o error de conexión.", maxIntentos);
                        _ultimaEjecucionVendedores = DateTime.Now;
                        tel.UltimoResultado = "ERROR";
                        tel.Reintentando = false;
                    }
                }
                finally
                {
                    tel.EnEjecucion = false;
                    if (lockTomado) _cycleLock.Release();
                }
            }
        }

        private async Task EjecutarStockConReintentosAsync(Models.SyncConfigDto config, CancellationToken ct)
        {
            int maxIntentos = config.HabilitarReintentos ? Math.Max(1, config.ReintentosMaximos) : 1;
            int esperaSegundos = config.EsperaReintentoSegundos > 0 ? config.EsperaReintentoSegundos : 30;
            var tel = _telemetria["STOCK"];

            for (int intento = 1; intento <= maxIntentos; intento++)
            {
                if (ct.IsCancellationRequested) break;

                bool lockTomado = false;
                try
                {
                    if (_syncStockEngine.GetCurrentStatus().IsRunning)
                    {
                        _logger.LogInformation("Módulo Stock ya se encuentra en ejecución manual o previa. Omitiendo ciclo automático.");
                        return;
                    }

                    if (await _cycleLock.WaitAsync(0, ct))
                    {
                        lockTomado = true;
                        tel.EnEjecucion = true;
                        tel.Reintentando = intento > 1;
                        tel.IntentoActual = intento;
                        tel.MaxIntentos = maxIntentos;
                        _logger.LogInformation("Ejecutando ciclo automático Módulo Stock (Intento {Intento}/{Max})...", intento, maxIntentos);
                        await _syncStockEngine.EjecutarSincronizacionStockAsync("AUTOMATICO", "WORKER_SERVICE", ct);

                        _ultimaEjecucionStock = DateTime.Now;
                        tel.UltimoExito = _ultimaEjecucionStock;
                        tel.UltimoResultado = "COMPLETADO";
                        tel.UltimoError = null;
                        tel.Reintentando = false;
                        if (config.ModoStock == "MADRUGADA")
                        {
                            _ultimoDiaMadrugadaStock = DateTime.Today;
                        }
                        return; // Éxito total
                    }
                    else
                    {
                        _logger.LogWarning("Ciclo Stock omitido: semáforo de sincronización ocupado por otro módulo.");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    tel.UltimoError = ex.Message;
                    _logger.LogWarning(ex, "Fallo en ejecución automática de Stock (Intento {Intento}/{Max}).", intento, maxIntentos);
                    if (intento < maxIntentos)
                    {
                        tel.Reintentando = true;
                        _logger.LogInformation("Esperando {Seg}s antes de reintentar Módulo Stock...", esperaSegundos);
                        await Task.Delay(TimeSpan.FromSeconds(esperaSegundos), ct);
                    }
                    else
                    {
                        _logger.LogError("Se agotaron los {Max} reintentos de Módulo Stock tras caída o error de conexión.", maxIntentos);
                        _ultimaEjecucionStock = DateTime.Now;
                        tel.UltimoResultado = "ERROR";
                        tel.Reintentando = false;
                    }
                }
                finally
                {
                    tel.EnEjecucion = false;
                    if (lockTomado) _cycleLock.Release();
                }
            }
        }

        public List<AutoModuleStateDto> ObtenerEstadoVivoModulos(SyncConfigDto config)
        {
            var ahora = DateTime.Now;
            var cliStatus = _syncEngine.GetStatus();
            var prodStatus = _syncProductosEngine.GetCurrentStatus();
            var vendStatus = _syncVendedoresEngine.GetCurrentStatus();
            var stockStatus = _syncStockEngine.GetCurrentStatus();

            return new List<AutoModuleStateDto>
            {
                ConstruirEstadoModulo(
                    modulo: "CLIENTES",
                    nombre: "Clientes, Rutas y Créditos",
                    globalActivo: config.SincronizacionActiva,
                    autoActivo: config.AutoClientesActivo,
                    modo: config.ModoClientes,
                    intervaloMin: config.IntervaloClientesMinutos > 0 ? config.IntervaloClientesMinutos : (config.IntervaloMinutos > 0 ? config.IntervaloMinutos : 3),
                    horaInicio: string.IsNullOrWhiteSpace(config.HoraInicioClientes) ? config.HoraInicioVentana : config.HoraInicioClientes,
                    horaFin: string.IsNullOrWhiteSpace(config.HoraFinClientes) ? config.HoraFinVentana : config.HoraFinClientes,
                    horaMadrugada: config.HoraMadrugadaClientes,
                    ultimaEjecucion: _ultimaEjecucionClientes,
                    ultimoDiaMadrugada: _ultimoDiaMadrugadaClientes,
                    engineRunning: cliStatus.IsRunning,
                    enginePorcentaje: cliStatus.Porcentaje,
                    engineFase: cliStatus.FaseDetalle,
                    ahora: ahora
                ),
                ConstruirEstadoModulo(
                    modulo: "PRODUCTOS",
                    nombre: "Productos, Precios y Catálogos",
                    globalActivo: config.SincronizacionActiva,
                    autoActivo: config.AutoProductosActivo,
                    modo: config.ModoProductos,
                    intervaloMin: config.IntervaloProductosMinutos > 0 ? config.IntervaloProductosMinutos : (config.IntervaloMinutos > 0 ? config.IntervaloMinutos : 3),
                    horaInicio: string.IsNullOrWhiteSpace(config.HoraInicioProductos) ? config.HoraInicioVentana : config.HoraInicioProductos,
                    horaFin: string.IsNullOrWhiteSpace(config.HoraFinProductos) ? config.HoraFinVentana : config.HoraFinProductos,
                    horaMadrugada: config.HoraMadrugadaProductos,
                    ultimaEjecucion: _ultimaEjecucionProductos,
                    ultimoDiaMadrugada: _ultimoDiaMadrugadaProductos,
                    engineRunning: prodStatus.IsRunning,
                    enginePorcentaje: prodStatus.Porcentaje,
                    engineFase: prodStatus.FaseDetalle,
                    ahora: ahora
                ),
                ConstruirEstadoModulo(
                    modulo: "VENDEDORES",
                    nombre: "Vendedores y Equipos",
                    globalActivo: config.SincronizacionActiva,
                    autoActivo: config.AutoVendedoresActivo,
                    modo: config.ModoVendedores,
                    intervaloMin: config.IntervaloVendedoresMinutos > 0 ? config.IntervaloVendedoresMinutos : 5,
                    horaInicio: string.IsNullOrWhiteSpace(config.HoraInicioVendedores) ? config.HoraInicioVentana : config.HoraInicioVendedores,
                    horaFin: string.IsNullOrWhiteSpace(config.HoraFinVendedores) ? config.HoraFinVentana : config.HoraFinVendedores,
                    horaMadrugada: config.HoraMadrugadaVendedores,
                    ultimaEjecucion: _ultimaEjecucionVendedores,
                    ultimoDiaMadrugada: _ultimoDiaMadrugadaVendedores,
                    engineRunning: vendStatus.IsRunning,
                    enginePorcentaje: vendStatus.Porcentaje,
                    engineFase: vendStatus.FaseDetalle,
                    ahora: ahora
                ),
                ConstruirEstadoModulo(
                    modulo: "STOCK",
                    nombre: "Stock en Línea por Almacén",
                    globalActivo: config.SincronizacionActiva,
                    autoActivo: config.AutoStockActivo,
                    modo: config.ModoStock,
                    intervaloMin: config.IntervaloStockMinutos > 0 ? config.IntervaloStockMinutos : 2,
                    horaInicio: string.IsNullOrWhiteSpace(config.HoraInicioStock) ? config.HoraInicioVentana : config.HoraInicioStock,
                    horaFin: string.IsNullOrWhiteSpace(config.HoraFinStock) ? config.HoraFinVentana : config.HoraFinStock,
                    horaMadrugada: config.HoraMadrugadaStock,
                    ultimaEjecucion: _ultimaEjecucionStock,
                    ultimoDiaMadrugada: _ultimoDiaMadrugadaStock,
                    engineRunning: stockStatus.IsRunning,
                    enginePorcentaje: stockStatus.Porcentaje,
                    engineFase: stockStatus.FaseDetalle,
                    ahora: ahora
                )
            };
        }

        private AutoModuleStateDto ConstruirEstadoModulo(
            string modulo,
            string nombre,
            bool globalActivo,
            bool autoActivo,
            string modo,
            int intervaloMin,
            string horaInicio,
            string horaFin,
            string horaMadrugada,
            DateTime? ultimaEjecucion,
            DateTime? ultimoDiaMadrugada,
            bool engineRunning,
            double enginePorcentaje,
            string engineFase,
            DateTime ahora)
        {
            var tel = _telemetria[modulo];
            bool enVentana = modo == "MADRUGADA" || EstaEnVentanaHoraria(horaInicio, horaFin);

            string frecuenciaTexto = modo == "MADRUGADA"
                ? $"Madrugada · {horaMadrugada} hrs"
                : $"Cada {intervaloMin} min";

            string ventanaTexto = modo == "MADRUGADA"
                ? $"Diario {horaMadrugada}"
                : $"{horaInicio} - {horaFin}";

            DateTime? proxima = null;
            int? segParaProxima = null;

            if (globalActivo && autoActivo && !engineRunning)
            {
                if (modo == "MADRUGADA")
                {
                    if (!TimeSpan.TryParse(horaMadrugada, out var obj)) obj = new TimeSpan(2, 0, 0);
                    var candidato = ahora.Date.Add(obj);
                    if ((ultimoDiaMadrugada.HasValue && ultimoDiaMadrugada.Value.Date == ahora.Date) || ahora > candidato.AddMinutes(10))
                        candidato = candidato.AddDays(1);
                    proxima = candidato;
                }
                else if (enVentana)
                {
                    proxima = ultimaEjecucion.HasValue
                        ? ultimaEjecucion.Value.AddMinutes(intervaloMin)
                        : ahora.AddSeconds(15);
                    if (proxima < ahora) proxima = ahora.AddSeconds(10);
                }

                if (proxima.HasValue)
                {
                    segParaProxima = Math.Max(0, (int)(proxima.Value - ahora).TotalSeconds);
                }
            }

            string estadoActual;
            if (engineRunning || tel.EnEjecucion)
                estadoActual = tel.Reintentando ? "REINTENTANDO" : "EJECUTANDO";
            else if (!globalActivo || !autoActivo)
                estadoActual = "DESACTIVADO";
            else if (!enVentana)
                estadoActual = "FUERA_VENTANA";
            else if (tel.UltimoResultado == "ERROR")
                estadoActual = "ERROR";
            else
                estadoActual = "EN_ESPERA";

            return new AutoModuleStateDto
            {
                Modulo = modulo,
                NombreDisplay = nombre,
                AutoActivo = globalActivo && autoActivo,
                Modo = string.IsNullOrWhiteSpace(modo) ? "INTERVALO" : modo,
                FrecuenciaTexto = frecuenciaTexto,
                VentanaHoraria = ventanaTexto,
                EstadoActual = estadoActual,
                EnEjecucion = engineRunning || tel.EnEjecucion,
                ProgresoPorcentaje = enginePorcentaje,
                FaseDetalle = engineFase ?? "",
                IntentoActual = tel.IntentoActual,
                MaxIntentos = tel.MaxIntentos,
                UltimaEjecucion = ultimaEjecucion,
                UltimoExito = tel.UltimoExito,
                ProximaEjecucionEstimada = proxima,
                SegundosParaProxima = segParaProxima,
                UltimoResultado = tel.UltimoResultado,
                UltimoError = tel.UltimoError
            };
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
