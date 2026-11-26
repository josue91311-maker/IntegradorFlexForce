using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using IntegradorFlexForce.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

namespace IntegradorFlexForce.Services
{
    public class TraddeUpdateEngine
    {
        private readonly string _sqlForceConnectionString;
        private readonly ILogger<TraddeUpdateEngine> _logger;
        private readonly string _configFilePath;

        private readonly object _configLock = new();
        private readonly object _mongoLock = new();
        private readonly object _queueSync = new();

        private TraddeGlobalConfigDto _config;
        private IMongoClient? _mongoClient;
        private string _cachedMongoConnStr = string.Empty;

        private sealed record QueueEntry(string StepKey, string Origen);

        private readonly Channel<QueueEntry> _queueChannel = Channel.CreateUnbounded<QueueEntry>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        private readonly List<string> _queuedKeys = new();

        private readonly Dictionary<string, TraddeStepStatusDto> _stepsByKey = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<TraddeStepStatusDto> _orderedSteps = new();
        private readonly LinkedList<TraddeEventoLogDto> _eventos = new();
        private readonly object _eventosLock = new();

        private string? _currentRunningKey;
        private long _totalEjecucionesGlobal;
        private DateTime? _lastHeartbeat;

        public TraddeUpdateEngine(IConfiguration configuration, ILogger<TraddeUpdateEngine> logger)
        {
            _logger = logger;
            _sqlForceConnectionString = configuration.GetConnectionString("ForceGVD")
                ?? throw new InvalidOperationException("Falta ConnectionStrings:ForceGVD en appsettings.json");

            _configFilePath = Path.Combine(AppContext.BaseDirectory, "tradde_update_config.json");

            string defaultMongoConn = configuration.GetConnectionString("MongoTradde")
                ?? "mongodb://192.168.2.125:27020/?directConnection=true";
            string defaultMongoDb = configuration["TraddeUpdate:MongoDatabase"] ?? "tradde-mixtos";
            bool defaultAuto = bool.TryParse(configuration["TraddeUpdate:AutoGlobalActivo"], out bool ag) && ag;
            string defaultWinStart = configuration["TraddeUpdate:VentanaInicioDefault"] ?? "05:00";
            string defaultWinEnd = configuration["TraddeUpdate:VentanaFinDefault"] ?? "21:00";

            _config = new TraddeGlobalConfigDto
            {
                AutoGlobalActivo = defaultAuto,
                MongoConnectionString = defaultMongoConn,
                MongoDatabase = defaultMongoDb,
                VentanaInicioDefault = defaultWinStart,
                VentanaFinDefault = defaultWinEnd,
                BatchSizeMongo = 10000
            };

            InicializarDefinicionPasos();
            CargarConfiguracionDisco();
        }

        private void InicializarDefinicionPasos()
        {
            var defs = new List<TraddeStepStatusDto>
            {
                // =========================================================================
                // SECCIÓN 1: MAESTROS Y GEOGRAFÍA DE CLIENTES (BDForceGVD -> tradde-mixtos)
                // =========================================================================
                CrearPaso(1, "cli_departamentos", "SEC_CLI_MAESTROS", 1, "1. Maestros y Cartera de Clientes",
                    "1.1 Departamentos", "Sincroniza el catálogo geográfico de departamentos desde Force.",
                    "dbo.cat_Departamento", "Departamento", "BulkWrite Upsert", "ETL_MIXTOS_MASTER_DEPARTAMENTO", "DIARIO", 60, "06:00"),

                CrearPaso(2, "cli_provincias", "SEC_CLI_MAESTROS", 1, "1. Maestros y Cartera de Clientes",
                    "1.2 Provincias", "Sincroniza el catálogo geográfico de provincias vinculadas a departamento.",
                    "dbo.cat_Provincia", "Provincia", "BulkWrite Upsert", "ETL_MIXTOS_MASTER_PROVINCIA", "DIARIO", 60, "06:02"),

                CrearPaso(3, "cli_distritos", "SEC_CLI_MAESTROS", 1, "1. Maestros y Cartera de Clientes",
                    "1.3 Distritos", "Sincroniza el catálogo geográfico de distritos desde Force.",
                    "dbo.cat_Distrito", "Distrito", "BulkWrite Upsert", "ETL_MIXTOS_MASTER_DISTRITO", "DIARIO", 60, "06:04"),

                CrearPaso(4, "cli_tipodoc", "SEC_CLI_MAESTROS", 1, "1. Maestros y Cartera de Clientes",
                    "1.4 Tipos de Documento de Identidad", "Sincroniza DNI, RUC, CE y tipos de documento SUNAT.",
                    "dbo.cat_TipoDocIdentidad", "TipoDocIdentidad", "BulkWrite Upsert", "ETL_MIXTOS_MASTER_TIPODOCUMENTO", "DIARIO", 60, "06:06"),

                CrearPaso(5, "cli_zonas", "SEC_CLI_MAESTROS", 1, "1. Maestros y Cartera de Clientes",
                    "1.5 Zonas de Venta y Reparto", "Sincroniza geocercas y zonas activas de venta y reparto.",
                    "dbo.cat_Zona", "Zona", "BulkWrite Upsert", "ETL_MIXTOS_MASTER_ZONA", "INTERVALO", 30, "06:10"),

                CrearPaso(6, "cli_categorias", "SEC_CLI_MAESTROS", 1, "1. Maestros y Cartera de Clientes",
                    "1.6 Categorías de Cliente", "Sincroniza clasificaciones comerciales de clientes.",
                    "dbo.cat_CategoriaCliente", "CategoriaCliente", "BulkWrite Upsert", "ETL_MIXTOS_CATEGORIACLIENTE", "DIARIO", 60, "06:12"),

                CrearPaso(7, "cli_tipos", "SEC_CLI_MAESTROS", 1, "1. Maestros y Cartera de Clientes",
                    "1.7 Tipos de Cliente", "Sincroniza giros y tipos de cliente desde Force.",
                    "dbo.cat_TipoCliente", "TipoCliente", "BulkWrite Upsert", "ETL_MIXTOS_TIPOCLIENTE", "DIARIO", 60, "06:14"),

                CrearPaso(8, "cli_caracteristicas", "SEC_CLI_MAESTROS", 1, "1. Maestros y Cartera de Clientes",
                    "1.8 Características Comerciales", "Sincroniza catálogo de características y segmentación de clientes.",
                    "dbo.cat_Caracteristica, dbo.cat_TipoCaracteristica", "caracteristica", "BulkWrite Upsert", "ETL_MIXTOS_CARACTERISTICA", "DIARIO", 60, "06:16"),

                CrearPaso(9, "cli_direccion_cliente", "SEC_CLI_MAESTROS", 1, "1. Maestros y Cartera de Clientes",
                    "1.9 Maestro Coordenadas y Ubigeo Dirección", "Carga masiva atómica de coordenadas GPS y ubigeo de direcciones.",
                    "dbo.dir_Direccion", "Direccion_cliente", "Atomic Temp-Swap", "ETL_MIXTOS_MASTER_DIRCLIENTE", "INTERVALO", 30, "06:20"),

                CrearPaso(10, "cli_master_persona", "SEC_CLI_MAESTROS", 1, "1. Maestros y Cartera de Clientes",
                    "1.10 Master Persona (Clientes)", "Carga masiva de identidad, RUC/DNI y dirección principal activa.",
                    "dbo.cli_Cliente, dbo.dir_EntidadDireccion", "master_persona", "Atomic Temp-Swap", "ETL_MIXTOS_MASTER_CL_PERSONA", "INTERVALO", 30, "06:25"),

                CrearPaso(11, "cli_base_cliente", "SEC_CLI_MAESTROS", 1, "1. Maestros y Cartera de Clientes",
                    "1.11 Colección Base Cliente y Segmentos", "Carga masiva de clientes con JSON de características (4, 21..30) y campos libres.",
                    "dbo.cli_Cliente, dbo.cli_CamposCliente, dbo.cat_Caracteristica", "cliente", "Atomic Temp-Swap", "ETL_MIXTOS_CLIENTE", "INTERVALO", 30, "06:30"),

                CrearPaso(12, "cli_direcciones", "SEC_CLI_MAESTROS", 1, "1. Maestros y Cartera de Clientes",
                    "1.12 Vínculo Direcciones por Cliente", "Carga masiva de direcciones de entrega y sucursal por cliente.",
                    "dbo.dir_Direccion, dbo.dir_EntidadDireccion", "direccion", "Atomic Temp-Swap", "ETL_MIXTOS_MASTER_DIRECCION", "INTERVALO", 30, "06:35"),

                // =========================================================================
                // SECCIÓN 2: CONSOLIDADOS COMERCIALES DE CLIENTES
                // =========================================================================
                CrearPaso(13, "cli_conversion", "SEC_CLI_VISTAS", 2, "2. Consolidados de Clientes (Conversión y Vista General)",
                    "2.1 Conversión Tabla Clientes", "Reemplazo atómico e indexado de conversion_tablaclientes desde Force.",
                    "dbo.cli_Cliente, dbo.cli_CamposCliente", "conversion_tablaclientes", "Atomic Temp-Swap + Índices", "clientes_v2", "INTERVALO", 15, "06:40"),

                CrearPaso(14, "cli_vista_general", "SEC_CLI_VISTAS", 2, "2. Consolidados de Clientes (Conversión y Vista General)",
                    "2.2 Vista General Cliente", "Reemplazo atómico e indexado de vista_General_cliente con descripciones completas.",
                    "dbo.cli_Cliente, dbo.cat_TipoCliente, dbo.cat_CategoriaCliente, dbo.cli_CamposCliente", "vista_General_cliente", "Atomic Temp-Swap + Índices", "vista_General_cliente", "INTERVALO", 15, "06:45"),

                // =========================================================================
                // SECCIÓN 3: MAESTROS Y CONSOLIDADOS DE MERCADERÍA / PRODUCTOS
                // =========================================================================
                CrearPaso(15, "prd_marcas", "SEC_PRODUCTOS", 3, "3. Maestros y Consolidados de Mercadería",
                    "3.1 Marcas de Producto", "Sincroniza catálogo de marcas desde Force.",
                    "dbo.prd_Marca", "marca", "BulkWrite Upsert", "ETL_VEGATIENDAS_MARCA", "INTERVALO", 60, "06:15"),

                CrearPaso(16, "prd_lineas", "SEC_PRODUCTOS", 3, "3. Maestros y Consolidados de Mercadería",
                    "3.2 Líneas de Producto", "Sincroniza líneas comerciales de productos desde Force.",
                    "dbo.prd_LineaProducto", "linea", "BulkWrite Upsert", "ETL_VEGATIENDAS_LINEA", "INTERVALO", 60, "06:16"),

                CrearPaso(17, "prd_subproveedores", "SEC_PRODUCTOS", 3, "3. Maestros y Consolidados de Mercadería",
                    "3.3 SubProveedores", "Sincroniza catálogo de subproveedores desde Force.",
                    "dbo.prd_SubProveedor", "SubProveedor", "BulkWrite Upsert", "ETL_VEGATIENDAS_SUBPROVEEDOR", "INTERVALO", 60, "06:17"),

                CrearPaso(18, "prd_proveedores", "SEC_PRODUCTOS", 3, "3. Maestros y Consolidados de Mercadería",
                    "3.4 Proveedores", "Sincroniza catálogo de proveedores comerciales hacia MongoDB.",
                    "dbo.prd_Proveedor", "Proveedor", "BulkWrite Upsert", "ETL_VEGATIENDAS_PROVEEDOR2", "INTERVALO", 60, "06:18"),

                CrearPaso(19, "prd_unidad_factor", "SEC_PRODUCTOS", 3, "3. Maestros y Consolidados de Mercadería",
                    "3.5 Unidades de Medida y Factores", "Carga atómica de presentaciones, pesos, volúmenes y factores de conversión.",
                    "dbo.prd_ProductoUnidad, dbo.prd_UnidadMedida", "UnidadFactor", "Atomic Temp-Swap", "UnidadFactorETL", "INTERVALO", 30, "06:20"),

                CrearPaso(20, "prd_mercaderia_a", "SEC_PRODUCTOS", 3, "3. Maestros y Consolidados de Mercadería",
                    "3.6 Maestro Mercadería Base (Mercaderia_A)", "Carga atómica de SKUs con pivote de unidad base y unidad de referencia.",
                    "dbo.prd_Producto, dbo.prd_ProductoUnidad", "Mercaderia_A", "Atomic Temp-Swap", "MercaderiaETL", "INTERVALO", 30, "06:22"),

                CrearPaso(21, "prd_conversion", "SEC_PRODUCTOS", 3, "3. Maestros y Consolidados de Mercadería",
                    "3.7 Conversión Tabla Mercaderías", "Reemplazo atómico e indexado de conversion_tablamercaderias.",
                    "dbo.prd_Producto, dbo.prd_ProductoUnidad", "conversion_tablamercaderias", "Atomic Temp-Swap + Índices", "productos_v2", "INTERVALO", 15, "06:25"),

                CrearPaso(22, "prd_vista_general", "SEC_PRODUCTOS", 3, "3. Maestros y Consolidados de Mercadería",
                    "3.8 Vista General Mercadería", "Reemplazo atómico de vista_General_mercaderia con marca, línea, proveedor e IGV.",
                    "dbo.prd_Producto, dbo.prd_Marca, dbo.prd_LineaProducto, dbo.prd_Proveedor, dbo.ImpuestoProducto", "vista_General_mercaderia", "Atomic Temp-Swap + Índices", "productos_vistav2", "INTERVALO", 15, "06:28"),

                // =========================================================================
                // SECCIÓN 4: MAESTROS Y CONSOLIDADOS DE FUERZA DE VENTAS
                // =========================================================================
                CrearPaso(23, "vnd_sucursales", "SEC_VENDEDORES", 4, "4. Maestros y Consolidados de Vendedores",
                    "4.1 Sucursales", "Sincroniza catálogo de sucursales activas desde Force.",
                    "dbo.cat_Sucursal", "Sucursal", "BulkWrite Upsert", "ETL_MIXTO_SUCURSAL", "DIARIO", 60, "06:05"),

                CrearPaso(24, "vnd_fuerzaventas", "SEC_VENDEDORES", 4, "4. Maestros y Consolidados de Vendedores",
                    "4.2 Fuerza de Ventas", "Sincroniza divisiones y fuerzas de venta desde Force.",
                    "dbo.vnd_FuerzaVenta", "fuerzaventas", "BulkWrite Upsert", "ETL_MIXTO_FUERZAVENTAS", "INTERVALO", 60, "06:08"),

                CrearPaso(25, "vnd_equipoventas", "SEC_VENDEDORES", 4, "4. Maestros y Consolidados de Vendedores",
                    "4.3 Equipos / Grupos de Ventas", "Sincroniza grupos de venta, supervisores y vínculo a fuerza de ventas.",
                    "dbo.vnd_GrupoVentas", "Equipoventas", "BulkWrite Upsert", "ETL_MIXTO_EQUIPOVENTAS", "INTERVALO", 60, "06:10"),

                CrearPaso(26, "vnd_vendedor_base", "SEC_VENDEDORES", 4, "4. Maestros y Consolidados de Vendedores",
                    "4.4 Colección Vendedor Base", "Sincroniza vendedores y asignación de grupo de ventas / sucursal.",
                    "dbo.vnd_Vendedor, dbo.vnd_GrupoVentas", "Vendedor", "BulkWrite Upsert", "ETL_MIXTO_VENDEDOR2", "INTERVALO", 30, "06:12"),

                CrearPaso(27, "vnd_conversion", "SEC_VENDEDORES", 4, "4. Maestros y Consolidados de Vendedores",
                    "4.5 Conversión Tabla Vendedores", "Construcción directa y atómica de conversion_tablavendedores desde Force.",
                    "dbo.vnd_Vendedor, dbo.vnd_GrupoVentas, dbo.vnd_FuerzaVenta, dbo.cat_Sucursal", "conversion_tablavendedores", "Atomic Temp-Swap + Índices", "ETL_MIXTO_CONV_VENDEDOR", "INTERVALO", 30, "06:15"),

                CrearPaso(28, "vnd_vista_general", "SEC_VENDEDORES", 4, "4. Maestros y Consolidados de Vendedores",
                    "4.6 Vista General Vendedor", "Construcción directa y atómica de vista_General_vendedor con nombres de equipo y fuerza.",
                    "dbo.vnd_Vendedor, dbo.vnd_GrupoVentas, dbo.vnd_FuerzaVenta, dbo.cat_Sucursal", "vista_General_vendedor", "Atomic Temp-Swap + Índices", "ETL_MIXTO_VG_VENDEDOR", "INTERVALO", 30, "06:18"),

                // =========================================================================
                // SECCIÓN 5: OPERACIONES RADAR EN LÍNEA (STOCK Y PRECIOS)
                // =========================================================================
                CrearPaso(29, "ops_radar_stock", "SEC_RADAR", 5, "5. Operaciones Radar en Línea (Stock y Lista de Precios)",
                    "5.1 Stock Radar por Sucursal", "Sincroniza existencias físicas en vivo de los 6 almacenes centrales hacia nestle_radar_stock.",
                    "dbo.inv_ProductoAlmacen, dbo.prd_Producto, dbo.inv_Almacen, dbo.cat_Sucursal", "nestle_radar_stock", "BulkWrite Upsert (Lotes 2K)", "StockRadarSync", "INTERVALO", 3, "06:00"),

                CrearPaso(30, "ops_lista_precios", "SEC_RADAR", 5, "5. Operaciones Radar en Línea (Stock y Lista de Precios)",
                    "5.2 Lista de Precios y Escalas Radar", "Sincroniza escalas vigentes con Decimal128, preservando created_at mediante reemplazo atómico.",
                    "dbo.prc_ItemPrecioProducto, dbo.prc_PrecioProductoUnidad, dbo.prc_ListaPrecio, dbo.prc_PrecioEscala", "lista_precios_radar", "Atomic Temp-Swap (Decimal128)", "ListaPreciosSyncRadar", "INTERVALO", 10, "06:05")
            };

            foreach (var d in defs)
            {
                _stepsByKey[d.StepKey] = d;
                _orderedSteps.Add(d);
            }
        }

        private TraddeStepStatusDto CrearPaso(
            int orden, string key, string secCod, int secOrd, string secNom,
            string nombre, string desc, string tablasForce, string colMongo,
            string estrategia, string appAudit, string modoDefault, int intervaloDefault, string horaDefault)
        {
            return new TraddeStepStatusDto
            {
                Orden = orden,
                StepKey = key,
                SeccionCodigo = secCod,
                SeccionOrden = secOrd,
                SeccionNombre = secNom,
                NombrePaso = nombre,
                Descripcion = desc,
                TablasForceOrigen = tablasForce,
                ColeccionMongoDestino = colMongo,
                EstrategiaCarga = estrategia,
                NombreAppAuditoria = appAudit,
                AutoActivo = secCod == "SEC_RADAR" || secCod == "SEC_CLI_VISTAS",
                ModoProgramacion = modoDefault,
                IntervaloMinutos = intervaloDefault,
                HoraDiaria = horaDefault,
                VentanaInicio = _config.VentanaInicioDefault,
                VentanaFin = _config.VentanaFinDefault
            };
        }

        // =========================================================================
        // PERSISTENCIA DE CONFIGURACIÓN Y HORARIOS INDIVIDUALES EN DISCO
        // =========================================================================
        private void CargarConfiguracionDisco()
        {
            lock (_configLock)
            {
                try
                {
                    if (!File.Exists(_configFilePath)) return;
                    string json = File.ReadAllText(_configFilePath);
                    var saved = JsonSerializer.Deserialize<TraddeGlobalConfigDto>(json, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });
                    if (saved == null) return;

                    _config.AutoGlobalActivo = saved.AutoGlobalActivo;
                    if (!string.IsNullOrWhiteSpace(saved.MongoConnectionString))
                        _config.MongoConnectionString = saved.MongoConnectionString.Trim();
                    if (!string.IsNullOrWhiteSpace(saved.MongoDatabase))
                        _config.MongoDatabase = saved.MongoDatabase.Trim();
                    if (!string.IsNullOrWhiteSpace(saved.VentanaInicioDefault))
                        _config.VentanaInicioDefault = saved.VentanaInicioDefault.Trim();
                    if (!string.IsNullOrWhiteSpace(saved.VentanaFinDefault))
                        _config.VentanaFinDefault = saved.VentanaFinDefault.Trim();
                    if (saved.BatchSizeMongo >= 500)
                        _config.BatchSizeMongo = saved.BatchSizeMongo;

                    if (saved.Schedules != null)
                    {
                        foreach (var sch in saved.Schedules)
                        {
                            if (!string.IsNullOrWhiteSpace(sch.StepKey) && _stepsByKey.TryGetValue(sch.StepKey, out var step))
                            {
                                step.AutoActivo = sch.AutoActivo;
                                step.ModoProgramacion = NormalizarModo(sch.ModoProgramacion);
                                step.IntervaloMinutos = Math.Clamp(sch.IntervaloMinutos, 1, 1440);
                                step.HoraDiaria = string.IsNullOrWhiteSpace(sch.HoraDiaria) ? "06:00" : sch.HoraDiaria.Trim();
                                step.VentanaInicio = string.IsNullOrWhiteSpace(sch.VentanaInicio) ? _config.VentanaInicioDefault : sch.VentanaInicio.Trim();
                                step.VentanaFin = string.IsNullOrWhiteSpace(sch.VentanaFin) ? _config.VentanaFinDefault : sch.VentanaFin.Trim();
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "No se pudo leer tradde_update_config.json, usando configuración por defecto.");
                }
            }
        }

        private void GuardarConfiguracionDisco()
        {
            lock (_configLock)
            {
                try
                {
                    _config.Schedules = _orderedSteps.Select(s => new TraddeStepScheduleConfigDto
                    {
                        StepKey = s.StepKey,
                        AutoActivo = s.AutoActivo,
                        ModoProgramacion = s.ModoProgramacion,
                        IntervaloMinutos = s.IntervaloMinutos,
                        HoraDiaria = s.HoraDiaria,
                        VentanaInicio = s.VentanaInicio,
                        VentanaFin = s.VentanaFin
                    }).ToList();

                    string json = JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true });
                    string tmpPath = _configFilePath + ".tmp";
                    File.WriteAllText(tmpPath, json);
                    File.Move(tmpPath, _configFilePath, overwrite: true);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "No se pudo guardar tradde_update_config.json.");
                }
            }
        }

        public void ActualizarConfigGlobal(TraddeUpdateGlobalRequestDto req)
        {
            lock (_configLock)
            {
                _config.AutoGlobalActivo = req.AutoGlobalActivo;
                if (!string.IsNullOrWhiteSpace(req.MongoConnectionString))
                    _config.MongoConnectionString = req.MongoConnectionString.Trim();
                if (!string.IsNullOrWhiteSpace(req.MongoDatabase))
                    _config.MongoDatabase = req.MongoDatabase.Trim();
                if (!string.IsNullOrWhiteSpace(req.VentanaInicioDefault))
                    _config.VentanaInicioDefault = req.VentanaInicioDefault.Trim();
                if (!string.IsNullOrWhiteSpace(req.VentanaFinDefault))
                    _config.VentanaFinDefault = req.VentanaFinDefault.Trim();
                if (req.BatchSizeMongo >= 500)
                    _config.BatchSizeMongo = Math.Clamp(req.BatchSizeMongo, 500, 50000);

                GuardarConfiguracionDisco();
            }

            RegistrarEvento("SISTEMA", "Configuración Global", "INFO", "MANUAL",
                $"Configuración actualizada: Automático Global = {(_config.AutoGlobalActivo ? "ACTIVO" : "MANUAL")} · BD Mongo = {_config.MongoDatabase}");
        }

        public bool ActualizarProgramacionPaso(TraddeStepScheduleConfigDto req)
        {
            if (string.IsNullOrWhiteSpace(req.StepKey) || !_stepsByKey.TryGetValue(req.StepKey, out var step))
                return false;

            lock (_configLock)
            {
                step.AutoActivo = req.AutoActivo;
                step.ModoProgramacion = NormalizarModo(req.ModoProgramacion);
                step.IntervaloMinutos = Math.Clamp(req.IntervaloMinutos, 1, 1440);
                step.HoraDiaria = string.IsNullOrWhiteSpace(req.HoraDiaria) ? "06:00" : req.HoraDiaria.Trim();
                step.VentanaInicio = string.IsNullOrWhiteSpace(req.VentanaInicio) ? _config.VentanaInicioDefault : req.VentanaInicio.Trim();
                step.VentanaFin = string.IsNullOrWhiteSpace(req.VentanaFin) ? _config.VentanaFinDefault : req.VentanaFin.Trim();
                GuardarConfiguracionDisco();
            }

            string detalle = step.ModoProgramacion == "DIARIO"
                ? $"Diario a las {step.HoraDiaria}"
                : $"Cada {step.IntervaloMinutos} min ({step.VentanaInicio}-{step.VentanaFin})";

            RegistrarEvento(step.StepKey, step.NombrePaso, "INFO", "MANUAL",
                $"Programación actualizada: {(step.AutoActivo ? "AUTO ON" : "AUTO OFF")} · {detalle}");
            return true;
        }

        private static string NormalizarModo(string? modo)
        {
            string m = (modo ?? "INTERVALO").Trim().ToUpperInvariant();
            return m == "DIARIO" ? "DIARIO" : "INTERVALO";
        }

        private IMongoDatabase ObtenerMongoDb()
        {
            string connStr;
            string dbName;
            lock (_configLock)
            {
                connStr = _config.MongoConnectionString;
                dbName = _config.MongoDatabase;
            }

            lock (_mongoLock)
            {
                if (_mongoClient == null || !string.Equals(_cachedMongoConnStr, connStr, StringComparison.Ordinal))
                {
                    var settings = MongoClientSettings.FromConnectionString(connStr);
                    settings.ServerSelectionTimeout = TimeSpan.FromSeconds(8);
                    settings.ConnectTimeout = TimeSpan.FromSeconds(8);
                    settings.SocketTimeout = TimeSpan.FromMinutes(5);
                    _mongoClient = new MongoClient(settings);
                    _cachedMongoConnStr = connStr;
                }
                return _mongoClient.GetDatabase(dbName);
            }
        }

        // =========================================================================
        // TEST DE CONECTIVIDAD FORCE + MONGODB
        // =========================================================================
        public async Task<TraddeConnectionTestResultDto> ProbarConexionesAsync(CancellationToken ct = default)
        {
            var res = new TraddeConnectionTestResultDto();

            var swForce = Stopwatch.StartNew();
            try
            {
                using var conn = new SqlConnection(_sqlForceConnectionString);
                await conn.OpenAsync(ct);
                using var cmd = new SqlCommand("SELECT DB_NAME();", conn);
                var dbName = Convert.ToString(await cmd.ExecuteScalarAsync(ct));
                swForce.Stop();
                res.ForceOk = true;
                res.ForceLatenciaMs = Math.Round(swForce.Elapsed.TotalMilliseconds, 1);
                res.ForceMensaje = $"Conectado a Force Nuevo ({dbName}) en {res.ForceLatenciaMs} ms";
            }
            catch (Exception ex)
            {
                swForce.Stop();
                res.ForceOk = false;
                res.ForceMensaje = $"Error Force SQL: {ex.Message}";
            }

            var swMongo = Stopwatch.StartNew();
            try
            {
                var db = ObtenerMongoDb();
                await db.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: ct);
                var names = await (await db.ListCollectionNamesAsync(cancellationToken: ct)).ToListAsync(ct);
                swMongo.Stop();
                res.MongoOk = true;
                res.MongoLatenciaMs = Math.Round(swMongo.Elapsed.TotalMilliseconds, 1);
                res.ColeccionesEncontradas = names.Count;
                res.MongoMensaje = $"Conectado a MongoDB ({db.DatabaseNamespace.DatabaseName} · {names.Count} colecciones) en {res.MongoLatenciaMs} ms";
            }
            catch (Exception ex)
            {
                swMongo.Stop();
                res.MongoOk = false;
                res.MongoMensaje = $"Error MongoDB: {ex.Message}";
            }

            return res;
        }

        // =========================================================================
        // GESTIÓN DE COLA DE EJECUCIÓN (MANUAL Y AUTOMÁTICA)
        // =========================================================================
        public (int Encolados, string Mensaje) EncolarEjecucion(TraddeExecuteRequestDto req, string origen = "MANUAL")
        {
            var keysToEnqueue = new List<string>();

            if (req.EjecutarTodo)
            {
                keysToEnqueue.AddRange(_orderedSteps.Select(s => s.StepKey));
            }
            else if (!string.IsNullOrWhiteSpace(req.SeccionCodigo))
            {
                keysToEnqueue.AddRange(_orderedSteps
                    .Where(s => string.Equals(s.SeccionCodigo, req.SeccionCodigo, StringComparison.OrdinalIgnoreCase))
                    .Select(s => s.StepKey));
            }
            else if (req.StepKeys != null && req.StepKeys.Count > 0)
            {
                foreach (var k in req.StepKeys)
                {
                    if (_stepsByKey.ContainsKey(k))
                        keysToEnqueue.Add(k);
                }
            }
            else if (!string.IsNullOrWhiteSpace(req.StepKey) && _stepsByKey.ContainsKey(req.StepKey))
            {
                keysToEnqueue.Add(req.StepKey);
            }

            int added = 0;
            lock (_queueSync)
            {
                foreach (var key in keysToEnqueue.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var step = _stepsByKey[key];
                    if (step.Estado == "EJECUTANDO" || _queuedKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
                        continue;

                    _queuedKeys.Add(key);
                    step.Estado = "EN_COLA";
                    step.PosicionCola = _queuedKeys.Count;
                    step.FaseActual = $"En cola (#{step.PosicionCola}) esperando turno...";
                    _queueChannel.Writer.TryWrite(new QueueEntry(key, origen));
                    added++;
                }
                RecalcularPosicionesColaNoLock();
            }

            if (added > 0)
            {
                RegistrarEvento("COLA", "Orquestador TraddeUpdate", "INFO", origen,
                    $"Se añadieron {added} paso(s) a la cola de sincronización hacia MongoDB.");
            }

            return (added, added > 0
                ? $"Se encolaron {added} paso(s) para subida a MongoDB."
                : "Los pasos seleccionados ya se encuentran en ejecución o en cola.");
        }

        public int LimpiarColaPendiente()
        {
            int removidos = 0;
            lock (_queueSync)
            {
                while (_queueChannel.Reader.TryRead(out var item))
                {
                    if (_stepsByKey.TryGetValue(item.StepKey, out var st) && st.Estado == "EN_COLA")
                    {
                        st.Estado = st.TotalEjecuciones > 0 ? "COMPLETADO" : "EN_ESPERA";
                        st.PosicionCola = 0;
                        st.FaseActual = "Cola cancelada por el usuario";
                    }
                    removidos++;
                }
                _queuedKeys.Clear();
            }

            if (removidos > 0)
                RegistrarEvento("COLA", "Orquestador TraddeUpdate", "WARN", "MANUAL", $"Se cancelaron {removidos} paso(s) pendientes en cola.");

            return removidos;
        }

        private void RecalcularPosicionesColaNoLock()
        {
            for (int i = 0; i < _queuedKeys.Count; i++)
            {
                if (_stepsByKey.TryGetValue(_queuedKeys[i], out var s) && s.Estado == "EN_COLA")
                {
                    s.PosicionCola = i + 1;
                    s.FaseActual = $"En cola (#{s.PosicionCola})";
                }
            }
        }

        // =========================================================================
        // EVALUACIÓN DE PROGRAMACIÓN AUTOMÁTICA INDIVIDUAL POR PASO
        // =========================================================================
        public void EvaluarProgramacionesAutomaticas()
        {
            _lastHeartbeat = DateTime.Now;

            bool globalAuto;
            lock (_configLock)
            {
                globalAuto = _config.AutoGlobalActivo;
            }
            if (!globalAuto) return;

            var ahora = DateTime.Now;
            var vencidos = new List<string>();

            foreach (var step in _orderedSteps)
            {
                if (!step.AutoActivo) continue;
                if (step.Estado == "EJECUTANDO" || step.Estado == "EN_COLA") continue;

                if (DebeEjecutarsePaso(step, ahora))
                {
                    vencidos.Add(step.StepKey);
                }
            }

            if (vencidos.Count > 0)
            {
                EncolarEjecucion(new TraddeExecuteRequestDto { StepKeys = vencidos }, "AUTO");
            }
        }

        private static bool DebeEjecutarsePaso(TraddeStepStatusDto step, DateTime ahora)
        {
            if (step.ModoProgramacion == "DIARIO")
            {
                string hoyStr = ahora.ToString("yyyy-MM-dd");
                if (step.UltimaFechaDiaria == hoyStr)
                    return false;

                if (TimeSpan.TryParseExact(step.HoraDiaria, @"hh\:mm", CultureInfo.InvariantCulture, out var horaObj))
                {
                    var diffMin = (ahora.TimeOfDay - horaObj).TotalMinutes;
                    // Ventana de disparo de 15 minutos a partir de la hora programada
                    return diffMin >= 0 && diffMin <= 15;
                }
                return false;
            }
            else
            {
                if (!EstaEnVentana(ahora.TimeOfDay, step.VentanaInicio, step.VentanaFin))
                    return false;

                if (!step.UltimaEjecucion.HasValue)
                    return true;

                return (ahora - step.UltimaEjecucion.Value).TotalMinutes >= step.IntervaloMinutos;
            }
        }

        private static DateTime? CalcularProximaEjecucion(TraddeStepStatusDto step, bool globalAuto, DateTime ahora)
        {
            if (!globalAuto || !step.AutoActivo) return null;

            if (step.ModoProgramacion == "DIARIO")
            {
                if (!TimeSpan.TryParseExact(step.HoraDiaria, @"hh\:mm", CultureInfo.InvariantCulture, out var horaObj))
                    return null;

                var candidatoHoy = ahora.Date.Add(horaObj);
                bool yaEjecutoHoy = step.UltimaFechaDiaria == ahora.ToString("yyyy-MM-dd");
                if (!yaEjecutoHoy && candidatoHoy >= ahora.AddMinutes(-15))
                    return candidatoHoy > ahora ? candidatoHoy : ahora.AddSeconds(15);

                return ahora.Date.AddDays(1).Add(horaObj);
            }
            else
            {
                TimeSpan.TryParseExact(step.VentanaInicio, @"hh\:mm", CultureInfo.InvariantCulture, out var ini);
                TimeSpan.TryParseExact(step.VentanaFin, @"hh\:mm", CultureInfo.InvariantCulture, out var fin);

                if (!EstaEnVentana(ahora.TimeOfDay, step.VentanaInicio, step.VentanaFin))
                {
                    var proxInicio = ahora.TimeOfDay < ini ? ahora.Date.Add(ini) : ahora.Date.AddDays(1).Add(ini);
                    return proxInicio;
                }

                if (!step.UltimaEjecucion.HasValue)
                    return ahora.AddSeconds(15);

                var prox = step.UltimaEjecucion.Value.AddMinutes(step.IntervaloMinutos);
                if (prox < ahora) prox = ahora.AddSeconds(15);
                if (fin > ini && prox.TimeOfDay > fin)
                    return ahora.Date.AddDays(1).Add(ini);

                return prox;
            }
        }

        private static bool EstaEnVentana(TimeSpan actual, string inicioStr, string finStr)
        {
            if (!TimeSpan.TryParseExact(inicioStr, @"hh\:mm", CultureInfo.InvariantCulture, out var ini))
                ini = new TimeSpan(5, 0, 0);
            if (!TimeSpan.TryParseExact(finStr, @"hh\:mm", CultureInfo.InvariantCulture, out var fin))
                fin = new TimeSpan(21, 0, 0);

            if (ini <= fin)
                return actual >= ini && actual <= fin;
            return actual >= ini || actual <= fin;
        }

        // =========================================================================
        // CONSUMIDOR DE COLA EN SEGUNDO PLANO
        // =========================================================================
        public async Task ConsumirColaAsync(CancellationToken stoppingToken)
        {
            await foreach (var entry in _queueChannel.Reader.ReadAllAsync(stoppingToken))
            {
                string key = entry.StepKey;
                lock (_queueSync)
                {
                    _queuedKeys.RemoveAll(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
                    RecalcularPosicionesColaNoLock();
                    _currentRunningKey = key;
                }

                try
                {
                    await EjecutarPasoInternoAsync(key, entry.Origen, stoppingToken);
                }
                finally
                {
                    lock (_queueSync)
                    {
                        _currentRunningKey = null;
                    }
                }
            }
        }

        // =========================================================================
        // SNAPSHOT DEL DASHBOARD PARA LA INTERFAZ EN TIEMPO REAL
        // =========================================================================
        public TraddeUpdateDashboardDto ObtenerDashboardSnapshot()
        {
            var ahora = DateTime.Now;
            bool globalAuto;
            string mongoConn, mongoDb, winIni, winFin;
            int batchSize;

            lock (_configLock)
            {
                globalAuto = _config.AutoGlobalActivo;
                mongoConn = _config.MongoConnectionString;
                mongoDb = _config.MongoDatabase;
                winIni = _config.VentanaInicioDefault;
                winFin = _config.VentanaFinDefault;
                batchSize = _config.BatchSizeMongo;
            }

            int queueCount;
            string? runningKey;
            lock (_queueSync)
            {
                queueCount = _queuedKeys.Count;
                runningKey = _currentRunningKey;
            }

            foreach (var s in _orderedSteps)
            {
                var prox = CalcularProximaEjecucion(s, globalAuto, ahora);
                s.ProximaEjecucionEstimada = prox;
                s.SegundosParaProximaEjecucion = prox.HasValue
                    ? Math.Max(0, (int)(prox.Value - ahora).TotalSeconds)
                    : null;
            }

            var secciones = _orderedSteps
                .GroupBy(s => new { s.SeccionCodigo, s.SeccionNombre, s.SeccionOrden })
                .OrderBy(g => g.Key.SeccionOrden)
                .Select(g =>
                {
                    int total = g.Count();
                    int comp = g.Count(x => x.Estado == "COMPLETADO");
                    int run = g.Count(x => x.Estado == "EJECUTANDO");
                    int col = g.Count(x => x.Estado == "EN_COLA");
                    int err = g.Count(x => x.Estado == "ERROR");
                    int auto = g.Count(x => x.AutoActivo);
                    int pct = total > 0 ? (int)Math.Round((comp * 100.0) / total) : 0;

                    return new TraddeSectionSummaryDto
                    {
                        SeccionCodigo = g.Key.SeccionCodigo,
                        SeccionNombre = g.Key.SeccionNombre,
                        SeccionOrden = g.Key.SeccionOrden,
                        TotalPasos = total,
                        PasosCompletados = comp,
                        PasosEjecutando = run,
                        PasosEnCola = col,
                        PasosConError = err,
                        PasosAutoActivos = auto,
                        ProgresoSeccionPorcentaje = pct
                    };
                }).ToList();

            List<TraddeEventoLogDto> evCopy;
            lock (_eventosLock)
            {
                evCopy = _eventos.ToList();
            }

            return new TraddeUpdateDashboardDto
            {
                AutoGlobalActivo = globalAuto,
                MongoConnectionString = mongoConn,
                MongoConnectionStringMasked = mongoConn,
                MongoDatabase = mongoDb,
                VentanaInicioDefault = winIni,
                VentanaFinDefault = winFin,
                BatchSizeMongo = batchSize,
                IsAnyRunning = runningKey != null,
                CurrentRunningStepKey = runningKey,
                CurrentRunningStepName = runningKey != null && _stepsByKey.TryGetValue(runningKey, out var rs) ? rs.NombrePaso : null,
                QueueCount = queueCount,
                TotalSteps = _orderedSteps.Count,
                CompletedSteps = _orderedSteps.Count(s => s.Estado == "COMPLETADO"),
                ErrorSteps = _orderedSteps.Count(s => s.Estado == "ERROR"),
                AutoEnabledSteps = _orderedSteps.Count(s => s.AutoActivo),
                TotalEjecucionesGlobal = Interlocked.Read(ref _totalEjecucionesGlobal),
                LastWorkerHeartbeat = _lastHeartbeat,
                Secciones = secciones,
                Pasos = _orderedSteps,
                EventosRecientes = evCopy
            };
        }

        private void RegistrarEvento(string stepKey, string nombrePaso, string tipo, string origen, string mensaje)
        {
            lock (_eventosLock)
            {
                _eventos.AddFirst(new TraddeEventoLogDto
                {
                    Timestamp = DateTime.Now.ToString("HH:mm:ss"),
                    StepKey = stepKey,
                    NombrePaso = nombrePaso,
                    Tipo = tipo,
                    Origen = origen,
                    Mensaje = mensaje
                });
                while (_eventos.Count > 120)
                    _eventos.RemoveLast();
            }
        }

        // =========================================================================
        // EJECUCIÓN REAL DE CADA PASO: BDFORCEGVD -> MONGODB (STREAMING & ATOMIC)
        // =========================================================================
        private async Task EjecutarPasoInternoAsync(string stepKey, string origen, CancellationToken ct)
        {
            if (!_stepsByKey.TryGetValue(stepKey, out var step)) return;

            var sw = Stopwatch.StartNew();
            var inicioDt = DateTime.Now;

            step.Estado = "EJECUTANDO";
            step.PosicionCola = 0;
            step.ProgresoPorcentaje = 5;
            step.FaseActual = "Conectando a Force Nuevo (BDForceGVD)...";
            step.UltimoOrigenDisparo = origen;
            step.UltimoError = null;

            RegistrarEvento(step.StepKey, step.NombrePaso, "INFO", origen,
                $"▶ Iniciando subida hacia MongoDB [{step.ColeccionMongoDestino}]...");

            try
            {
                var db = ObtenerMongoDb();
                (long leidos, long insertados, long actualizados) resultado = stepKey switch
                {
                    // SECCIÓN 1: MAESTROS CLIENTES
                    "cli_departamentos" => await SyncBulkUpsertPorIdVegaAsync(step, db, "Departamento",
                        "SELECT d.ID AS ID_Vega, ISNULL(d.Nombre, '') AS Descripcion, 0 AS Ubigeo FROM dbo.cat_Departamento d WITH (NOLOCK);",
                        r => new BsonDocument
                        {
                            ["ID_Vega"] = GetInt(r, 0),
                            ["Descripcion"] = GetStr(r, 1),
                            ["Ubigeo"] = GetInt(r, 2)
                        }, ct),

                    "cli_provincias" => await SyncBulkUpsertPorIdVegaAsync(step, db, "Provincia",
                        "SELECT pp.ID AS ID_Vega, ISNULL(pp.IDDepartamento, 0) AS IDDepartamento, ISNULL(pp.Nombre, '') AS Descripcion, 0 AS Ubigeo FROM dbo.cat_Provincia pp WITH (NOLOCK);",
                        r => new BsonDocument
                        {
                            ["ID_Vega"] = GetInt(r, 0),
                            ["IDDepartamento"] = GetInt(r, 1),
                            ["Descripcion"] = GetStr(r, 2),
                            ["Ubigeo"] = GetInt(r, 3)
                        }, ct),

                    "cli_distritos" => await SyncBulkUpsertPorIdVegaAsync(step, db, "Distrito",
                        "SELECT d.ID AS ID_Vega, ISNULL(d.IDProvincia, 0) AS IDProvincia, ISNULL(d.Nombre, '') AS Descripcion, 0 AS Ubigeo FROM dbo.cat_Distrito d WITH (NOLOCK);",
                        r => new BsonDocument
                        {
                            ["ID_Vega"] = GetInt(r, 0),
                            ["IDProvincia"] = GetInt(r, 1),
                            ["Descripcion"] = GetStr(r, 2),
                            ["Ubigeo"] = GetInt(r, 3)
                        }, ct),

                    "cli_tipodoc" => await SyncBulkUpsertPorIdVegaAsync(step, db, "TipoDocIdentidad",
                        "SELECT c.ID AS ID_Vega, ISNULL(c.Codigo, '') AS Codigo, ISNULL(c.Descripcion, '') AS Descripcion, 0 AS Longitud, ISNULL(c.Abreviacion, '') AS Abreviacion FROM dbo.cat_TipoDocIdentidad c WITH (NOLOCK);",
                        r => new BsonDocument
                        {
                            ["ID_Vega"] = GetInt(r, 0),
                            ["Codigo"] = GetStr(r, 1),
                            ["Descripcion"] = GetStr(r, 2),
                            ["Longitud"] = GetInt(r, 3),
                            ["Abreviacion"] = GetStr(r, 4)
                        }, ct),

                    "cli_zonas" => await SyncBulkUpsertPorIdVegaAsync(step, db, "Zona",
                        @"SELECT z.ID AS ID_Vega, 0 AS IDZonaPadre, ISNULL(z.Descripcion, '') AS Descripcion,
                                 ISNULL(z.Codigo, '') AS Codigo, CAST(ISNULL(z.Estado, 1) AS bit) AS Activo,
                                 CAST(ISNULL(z.EsVenta, 0) AS bit) AS Venta, CAST(ISNULL(z.EsReparto, 0) AS bit) AS Reparto
                          FROM dbo.cat_Zona z WITH (NOLOCK);",
                        r => new BsonDocument
                        {
                            ["ID_Vega"] = GetInt(r, 0),
                            ["IDZonaPadre"] = GetInt(r, 1),
                            ["Descripcion"] = GetStr(r, 2),
                            ["Codigo"] = GetStr(r, 3),
                            ["Activo"] = GetBool(r, 4),
                            ["Nivel"] = 0,
                            ["SubZonas"] = false,
                            ["CodigoPostal"] = "",
                            ["Venta"] = GetBool(r, 5),
                            ["Reparto"] = GetBool(r, 6),
                            ["GestionaRecargos"] = "0",
                            ["PorcentajeRecargo"] = 0.0,
                            ["Cobranza"] = false
                        }, ct),

                    "cli_categorias" => await SyncBulkUpsertPorIdVegaAsync(step, db, "CategoriaCliente",
                        "SELECT ca.ID AS ID_Vega, ISNULL(ca.Descripcion, '') AS Descripcion, CAST(0 AS bit) AS PorDefecto, CAST(ISNULL(ca.Estado, 1) AS bit) AS Activo, ISNULL(ca.Codigo, '') AS Codigo FROM dbo.cat_CategoriaCliente ca WITH (NOLOCK);",
                        r => new BsonDocument
                        {
                            ["ID_Vega"] = GetInt(r, 0),
                            ["Descripcion"] = GetStr(r, 1),
                            ["PorDefecto"] = GetBool(r, 2),
                            ["Activo"] = GetBool(r, 3),
                            ["Codigo"] = GetStr(r, 4)
                        }, ct),

                    "cli_tipos" => await SyncBulkUpsertPorIdVegaAsync(step, db, "TipoCliente",
                        "SELECT cc.ID AS ID_Vega, ISNULL(cc.Codigo, '') AS Codigo, ISNULL(cc.Descripcion, '') AS Descripcion FROM dbo.cat_TipoCliente cc WITH (NOLOCK);",
                        r => new BsonDocument
                        {
                            ["ID_Vega"] = GetInt(r, 0),
                            ["Codigo"] = GetStr(r, 1),
                            ["Descripcion"] = GetStr(r, 2)
                        }, ct),

                    "cli_caracteristicas" => await SyncBulkUpsertPorIdVegaAsync(step, db, "caracteristica",
                        @"SELECT c.ID AS ID_Vega, RTRIM(ISNULL(t.Descripcion, '')) AS TipoCaracteristica, RTRIM(ISNULL(c.Descripcion, '')) AS Caracteristica
                          FROM dbo.cat_Caracteristica c WITH (NOLOCK)
                          INNER JOIN dbo.cat_TipoCaracteristica t WITH (NOLOCK) ON c.IDTipoCaracteristica = t.ID;",
                        r => new BsonDocument
                        {
                            ["ID_Vega"] = GetInt(r, 0),
                            ["TipoCaracteristica"] = GetStr(r, 1),
                            ["Caracteristica"] = GetStr(r, 2)
                        }, ct),

                    "cli_direccion_cliente" => await SyncAtomicTempSwapAsync(step, db, "Direccion_cliente", "Direccion_cliente_temp",
                        @"SELECT d.ID AS ID_Vega, CAST(ISNULL(d.Longitud, 0) AS float) AS Longitud, CAST(ISNULL(d.Latitud, 0) AS float) AS Latitud,
                                 ISNULL(d.Ubigeo, '') AS Codigo
                          FROM dbo.dir_Direccion d WITH (NOLOCK)
                          WHERE ISNULL(d.Eliminado, 0) = 0;",
                        r => new BsonDocument
                        {
                            ["IDZonaReparto"] = 0,
                            ["Longitud"] = GetDouble(r, 1),
                            ["Latitud"] = GetDouble(r, 2),
                            ["IDFormatoDireccion"] = 0,
                            ["Nro"] = "",
                            ["Manzana"] = "",
                            ["Lote"] = "",
                            ["Cuadra"] = "",
                            ["Km"] = 0,
                            ["Interior"] = "",
                            ["Referencia"] = "",
                            ["IDTipoVia"] = 1,
                            ["IDTipoInterior"] = 1,
                            ["IDTipoUrbanizacion"] = 1,
                            ["IDVia"] = 1,
                            ["IDVivienda"] = 1,
                            ["IDUrbanizacion"] = 1,
                            ["Codigo"] = GetStr(r, 3),
                            ["ID_Vega"] = GetInt(r, 0)
                        },
                        indexAllColumns: true, uniqueKey: null, migrationStatusType: null, ct: ct),

                    "cli_master_persona" => await SyncAtomicTempSwapAsync(step, db, "master_persona", "master_persona_temp",
                        @"SELECT c.ID AS ID_Vega, ISNULL(c.IDTipoDocIdentidad, 0) AS IDTipoDocIdentidad, ISNULL(c.Email, '') AS Email,
                                 ISNULL(c.Observacion, '') AS Observacion, ISNULL(c.DocIdentidad, '') AS DocIdentidad, ISNULL(c.Codigo, '') AS Codigo,
                                 ISNULL(c.NombreCompleto, '') AS Nombre, ISNULL(c.ApellidoPaterno, '') AS ApellidoPaterno,
                                 ISNULL(c.ApellidoMaterno, '') AS ApellidoMaterno, ISNULL(c.PrimerNombre, '') AS PrimerNombre,
                                 c.SegundoNombre, CAST(ISNULL(c.Estado, 1) AS bit) AS Activo,
                                 ISNULL(edPrin.IDDireccion, 0) AS IDDireccionPrincipal,
                                 CAST(ISNULL(c.Exceptuadopercepcion, 0) AS bit) AS ExceptuadoDePercepcion
                          FROM dbo.cli_Cliente c WITH (NOLOCK)
                          OUTER APPLY (
                              SELECT TOP 1 d.IDDireccion
                              FROM dbo.dir_EntidadDireccion d WITH (NOLOCK)
                              WHERE d.IDEntidad = c.ID AND d.TipoEntidad = 'CLIENTE' AND d.EsPrincipal = 1 AND ISNULL(d.Estado, 1) = 1
                              ORDER BY d.IDDireccion ASC
                          ) edPrin
                          WHERE ISNULL(c.Eliminado, 0) = 0;",
                        r => new BsonDocument
                        {
                            ["IDTipoTercero"] = 1,
                            ["IDTipoDocIdentidad"] = GetInt(r, 1),
                            ["Email"] = GetStr(r, 2),
                            ["Web"] = "",
                            ["Observacion"] = GetStr(r, 3),
                            ["DocIdentidad"] = GetStr(r, 4),
                            ["Codigo"] = GetStr(r, 5),
                            ["Nombre"] = GetStr(r, 6),
                            ["ApellidoPaterno"] = GetStr(r, 7),
                            ["ApellidoMaterno"] = GetStr(r, 8),
                            ["PrimerNombre"] = GetStr(r, 9),
                            ["SegundoNombre"] = r.IsDBNull(10) ? BsonNull.Value : (BsonValue)r.GetString(10),
                            ["Descriptivo"] = false,
                            ["Activo"] = GetBool(r, 11),
                            ["Extranjero"] = false,
                            ["CuentaBancaria"] = "",
                            ["FechaAniversario"] = BsonNull.Value,
                            ["FechaCumpleaños"] = BsonNull.Value,
                            ["IDDireccionPrincipal"] = GetInt(r, 12),
                            ["ExceptuadoDePercepcion"] = GetBool(r, 13),
                            ["ID_Vega"] = GetInt(r, 0)
                        },
                        indexAllColumns: true, uniqueKey: null, migrationStatusType: null, ct: ct),

                    "cli_base_cliente" => await SyncAtomicTempSwapAsync(step, db, "cliente", "cliente_temp",
                        @"SELECT
                              C.ID AS PKID, ISNULL(C.IDCategoriaCliente, 0) AS IDCategoriaCliente, ISNULL(C.DiasACreditoPermitidos, 0) AS DiasCredito,
                              ISNULL(C.IDTipoCliente, 0) AS IDTipoCliente,
                              ISNULL(campos.Campo1, '') AS Campo1, ISNULL(campos.Campo2, '') AS Campo2,
                              ISNULL(campos.Campo3, '') AS Campo3, ISNULL(campos.Campo4, '') AS Campo4,
                              ISNULL(c4.ID, 0), ISNULL(c27.ID, 0), ISNULL(c21.ID, 0), ISNULL(c22.ID, 0), ISNULL(c23.ID, 0),
                              ISNULL(c24.ID, 0), ISNULL(c25.ID, 0), ISNULL(c26.ID, 0), ISNULL(c28.ID, 0), ISNULL(c29.ID, 0), ISNULL(c30.ID, 0),
                              RTRIM(ISNULL(c4.Descripcion, '')), RTRIM(ISNULL(c27.Descripcion, '')), RTRIM(ISNULL(c21.Descripcion, '')),
                              RTRIM(ISNULL(c22.Descripcion, '')), RTRIM(ISNULL(c23.Descripcion, '')), RTRIM(ISNULL(c24.Descripcion, '')),
                              RTRIM(ISNULL(c25.Descripcion, '')), RTRIM(ISNULL(c26.Descripcion, '')), RTRIM(ISNULL(c28.Descripcion, '')),
                              RTRIM(ISNULL(c29.Descripcion, '')), RTRIM(ISNULL(c30.Descripcion, ''))
                          FROM dbo.cli_Cliente C WITH (NOLOCK)
                          LEFT JOIN dbo.cli_CamposCliente campos WITH (NOLOCK) ON campos.IDCliente = C.ID
                          LEFT JOIN dbo.cat_Caracteristica c4  WITH (NOLOCK) ON c4.ID  = TRY_CAST(JSON_VALUE(campos.Caracteristicas, '$.VOLUMEN_COMPRA') AS INT)
                          LEFT JOIN dbo.cat_Caracteristica c21 WITH (NOLOCK) ON c21.ID = TRY_CAST(JSON_VALUE(campos.Caracteristicas, '$.TIPO_ESTABLECIMIENTO') AS INT)
                          LEFT JOIN dbo.cat_Caracteristica c22 WITH (NOLOCK) ON c22.ID = TRY_CAST(JSON_VALUE(campos.Caracteristicas, '$.TAMANO_NEGOCIO') AS INT)
                          LEFT JOIN dbo.cat_Caracteristica c23 WITH (NOLOCK) ON c23.ID = TRY_CAST(JSON_VALUE(campos.Caracteristicas, '$.HORARIO') AS INT)
                          LEFT JOIN dbo.cat_Caracteristica c24 WITH (NOLOCK) ON c24.ID = TRY_CAST(JSON_VALUE(campos.Caracteristicas, '$.VOLUMEN_COMPRA') AS INT)
                          LEFT JOIN dbo.cat_Caracteristica c25 WITH (NOLOCK) ON c25.ID = TRY_CAST(JSON_VALUE(campos.Caracteristicas, '$.PREFERENCIA_PAGO') AS INT)
                          LEFT JOIN dbo.cat_Caracteristica c26 WITH (NOLOCK) ON c26.ID = TRY_CAST(JSON_VALUE(campos.Caracteristicas, '$.CANAL_PREFERIDO') AS INT)
                          LEFT JOIN dbo.cat_Caracteristica c27 WITH (NOLOCK) ON c27.ID = TRY_CAST(JSON_VALUE(campos.Caracteristicas, '$.SEGMENTO') AS INT)
                          LEFT JOIN dbo.cat_Caracteristica c28 WITH (NOLOCK) ON c28.ID = TRY_CAST(JSON_VALUE(campos.Caracteristicas, '$.POTENCIAL') AS INT)
                          LEFT JOIN dbo.cat_Caracteristica c29 WITH (NOLOCK) ON c29.ID = TRY_CAST(JSON_VALUE(campos.Caracteristicas, '$.FRECUENCIA_COMPRA') AS INT)
                          LEFT JOIN dbo.cat_Caracteristica c30 WITH (NOLOCK) ON c30.ID = TRY_CAST(JSON_VALUE(campos.Caracteristicas, '$.NIVEL_SERVICIO') AS INT)
                          WHERE ISNULL(C.Eliminado, 0) = 0;",
                        r => new BsonDocument
                        {
                            ["PKID"] = GetInt(r, 0),
                            ["IDCategoriaCliente"] = GetInt(r, 1),
                            ["IDCategoriaCliente2"] = 1,
                            ["DiasCredito"] = GetInt(r, 2),
                            ["CreditoContado"] = false,
                            ["InhabilitadoBonificacion"] = false,
                            ["InhabilitadoDescuento"] = false,
                            ["IDTipoCliente"] = GetInt(r, 3),
                            ["Campo1"] = Trunc255(GetStr(r, 4)),
                            ["Campo2"] = Trunc255(GetStr(r, 5)),
                            ["Campo3"] = Trunc255(GetStr(r, 6)),
                            ["Campo4"] = Trunc255(GetStr(r, 7)),
                            ["IDMigrado"] = 0,
                            ["IDPanalero"] = 0,
                            ["IDVerificacion"] = 0,
                            ["IDPlanamiento"] = 0,
                            ["Corporativo"] = false,
                            ["IDClienteCorporativo"] = 0,
                            ["ClienteCaracteristica4"] = GetInt(r, 8).ToString(),
                            ["ClienteCaracteristica27"] = GetInt(r, 9).ToString(),
                            ["ClienteCaracteristica20"] = "0",
                            ["ClienteCaracteristica21"] = GetInt(r, 10).ToString(),
                            ["ClienteCaracteristica22"] = GetInt(r, 11).ToString(),
                            ["ClienteCaracteristica23"] = GetInt(r, 12).ToString(),
                            ["ClienteCaracteristica24"] = GetInt(r, 13).ToString(),
                            ["ClienteCaracteristica25"] = GetInt(r, 14).ToString(),
                            ["ClienteCaracteristica26"] = GetInt(r, 15).ToString(),
                            ["ClienteCaracteristica28"] = GetInt(r, 16).ToString(),
                            ["ClienteCaracteristica29"] = GetInt(r, 17).ToString(),
                            ["ClienteCaracteristica30"] = GetInt(r, 18).ToString(),
                            ["Caracteristica4"] = Trunc255(GetStr(r, 19)),
                            ["Caracteristica27"] = Trunc255(GetStr(r, 20)),
                            ["Caracteristica20"] = "",
                            ["Caracteristica21"] = Trunc255(GetStr(r, 21)),
                            ["Caracteristica22"] = Trunc255(GetStr(r, 22)),
                            ["Caracteristica23"] = Trunc255(GetStr(r, 23)),
                            ["Caracteristica24"] = Trunc255(GetStr(r, 24)),
                            ["Caracteristica25"] = Trunc255(GetStr(r, 25)),
                            ["Caracteristica26"] = Trunc255(GetStr(r, 26)),
                            ["Caracteristica28"] = Trunc255(GetStr(r, 27)),
                            ["Caracteristica29"] = Trunc255(GetStr(r, 28)),
                            ["Caracteristica30"] = Trunc255(GetStr(r, 29))
                        },
                        indexAllColumns: true, uniqueKey: null, migrationStatusType: null, ct: ct),

                    "cli_direcciones" => await SyncAtomicTempSwapAsync(step, db, "direccion", "direccion_temp",
                        @"SELECT dd.ID AS ID_Vega, dd.IDDistrito, dd.IDProvincia, dd.IDDepartamento,
                                 ISNULL(ed.IDEntidad, 0) AS IDPersona, ISNULL(dd.Direccion, '') AS Descripcion,
                                 CAST(ISNULL(ed.EsPrincipal, 0) AS bit) AS DireccionPrincipal,
                                 dd.IDSucursal, CAST(ISNULL(dd.Estado, 1) AS bit) AS Activo
                          FROM dbo.dir_Direccion dd WITH (NOLOCK)
                          INNER JOIN dbo.dir_EntidadDireccion ed WITH (NOLOCK)
                              ON ed.IDDireccion = dd.ID AND ed.TipoEntidad = 'CLIENTE'
                          WHERE ISNULL(dd.Eliminado, 0) = 0;",
                        r => new BsonDocument
                        {
                            ["ID_Vega"] = GetInt(r, 0),
                            ["IDDistrito"] = r.IsDBNull(1) ? BsonNull.Value : (BsonValue)r.GetInt32(1),
                            ["IDProvincia"] = r.IsDBNull(2) ? BsonNull.Value : (BsonValue)r.GetInt32(2),
                            ["IDDepartamento"] = r.IsDBNull(3) ? BsonNull.Value : (BsonValue)r.GetInt32(3),
                            ["IDPersona"] = GetInt(r, 4),
                            ["Descripcion"] = GetStr(r, 5),
                            ["DireccionPrincipal"] = GetBool(r, 6) ? "True" : "False",
                            ["Sucursal"] = r.IsDBNull(7) ? BsonNull.Value : (BsonValue)r.GetInt32(7).ToString(),
                            ["ID2"] = 0,
                            ["Activo"] = GetBool(r, 8)
                        },
                        indexAllColumns: true, uniqueKey: null, migrationStatusType: null, ct: ct),

                    // =========================================================================
                    // SECCIÓN 2: CONSOLIDADOS CLIENTES
                    // =========================================================================
                    "cli_conversion" => await SyncAtomicTempSwapAsync(step, db, "conversion_tablaclientes", "conversion_tablaclientesv2_temp",
                        @"SELECT
                              c.ID AS ID_Vega, ISNULL(c.IDTipoCliente, 0) AS IDVegaTipoCliente, ISNULL(c.IDCategoriaCliente, 0) AS IDVegaClienteCategoria,
                              ISNULL(c.ApellidoMaterno, '') AS ClienteApellidoMaterno, ISNULL(c.ApellidoPaterno, '') AS ClienteApellidoPaterno,
                              ISNULL(c.PrimerNombre, '') AS ClientePrimerNombre, c.SegundoNombre,
                              ISNULL(c.NombreCompleto, '') AS ClienteNombreCompleto, ISNULL(c.DocIdentidad, '') AS ClienteDocIdentidad,
                              CAST(ISNULL(c.Exceptuadopercepcion, 0) AS bit) AS ClienteExceptuadoDePercepcion,
                              ISNULL(c.Codigo, '') AS ClienteCodigo, ISNULL(c.Email, '') AS ClienteEmail,
                              CAST(ISNULL(c.Estado, 1) AS bit) AS ClienteActivo, ISNULL(c.DiasACreditoPermitidos, 0) AS ClienteDiasCredito,
                              ISNULL(cc.Campo1, '') AS ClienteCampo1,
                              ISNULL(TRY_CAST(JSON_VALUE(cc.Caracteristicas, '$.VOLUMEN_COMPRA') AS INT), 0),
                              ISNULL(TRY_CAST(JSON_VALUE(cc.Caracteristicas, '$.TIPO_ESTABLECIMIENTO') AS INT), 0),
                              ISNULL(TRY_CAST(JSON_VALUE(cc.Caracteristicas, '$.TAMANO_NEGOCIO') AS INT), 0),
                              ISNULL(TRY_CAST(JSON_VALUE(cc.Caracteristicas, '$.HORARIO') AS INT), 0),
                              ISNULL(TRY_CAST(JSON_VALUE(cc.Caracteristicas, '$.VOLUMEN_COMPRA') AS INT), 0),
                              ISNULL(TRY_CAST(JSON_VALUE(cc.Caracteristicas, '$.PREFERENCIA_PAGO') AS INT), 0),
                              ISNULL(TRY_CAST(JSON_VALUE(cc.Caracteristicas, '$.CANAL_PREFERIDO') AS INT), 0),
                              ISNULL(TRY_CAST(JSON_VALUE(cc.Caracteristicas, '$.SEGMENTO') AS INT), 0),
                              ISNULL(TRY_CAST(JSON_VALUE(cc.Caracteristicas, '$.POTENCIAL') AS INT), 0),
                              ISNULL(TRY_CAST(JSON_VALUE(cc.Caracteristicas, '$.FRECUENCIA_COMPRA') AS INT), 0),
                              ISNULL(TRY_CAST(JSON_VALUE(cc.Caracteristicas, '$.NIVEL_SERVICIO') AS INT), 0)
                          FROM dbo.cli_Cliente c WITH (NOLOCK)
                          LEFT JOIN dbo.cli_CamposCliente cc WITH (NOLOCK) ON cc.IDCliente = c.ID
                          WHERE ISNULL(c.Eliminado, 0) = 0;",
                        r =>
                        {
                            int idV = GetInt(r, 0);
                            int idCat = GetInt(r, 2);
                            return new BsonDocument
                            {
                                ["ID_Vega"] = idV,
                                ["PKID_Cliente"] = idV,
                                ["IDVegaClienteTipoTercero"] = 1,
                                ["IDVegaTipoCliente"] = GetInt(r, 1),
                                ["IDVegaClienteCategoria"] = idCat,
                                ["IDVegaClienteCategoria2"] = idCat,
                                ["IDVegaClientePlanamiento"] = 0,
                                ["IDVegaClientePanalero"] = 0,
                                ["IDVegaClienteMigrado"] = 0,
                                ["IDVegaClienteVerificacion"] = 0,
                                ["ClienteApellidoMaterno"] = GetStr(r, 3),
                                ["ClienteApellidoPaterno"] = GetStr(r, 4),
                                ["ClientePrimerNombre"] = GetStr(r, 5),
                                ["ClienteSegundoNombre"] = r.IsDBNull(6) ? BsonNull.Value : (BsonValue)r.GetString(6),
                                ["ClienteNombreCompleto"] = GetStr(r, 7),
                                ["ClienteDocIdentidad"] = GetStr(r, 8),
                                ["ClienteCuentaBancaria"] = "",
                                ["ClienteExceptuadoDePercepcion"] = GetBool(r, 9),
                                ["ClienteExtranjero"] = 0,
                                ["ClienteDescriptivo"] = 0,
                                ["ClienteCodigo"] = GetStr(r, 10),
                                ["ClienteEmail"] = GetStr(r, 11),
                                ["ClienteWeb"] = "",
                                ["ClienteActivo"] = GetBool(r, 12),
                                ["ClienteFechaAniversario"] = BsonNull.Value,
                                ["ClienteFechaCumpleaños"] = BsonNull.Value,
                                ["ClienteDiasCredito"] = GetInt(r, 13),
                                ["ClienteCreditoContado"] = "false",
                                ["ClienteCampo1"] = GetStr(r, 14),
                                ["ClienteCaracteristica4"] = GetInt(r, 15),
                                ["ClienteCaracteristica21"] = GetInt(r, 16),
                                ["ClienteCaracteristica22"] = GetInt(r, 17),
                                ["ClienteCaracteristica23"] = GetInt(r, 18),
                                ["ClienteCaracteristica24"] = GetInt(r, 19),
                                ["ClienteCaracteristica25"] = GetInt(r, 20),
                                ["ClienteCaracteristica26"] = GetInt(r, 21),
                                ["ClienteCaracteristica27"] = GetInt(r, 22),
                                ["ClienteCaracteristica28"] = GetInt(r, 23),
                                ["ClienteCaracteristica29"] = GetInt(r, 24),
                                ["ClienteCaracteristica30"] = GetInt(r, 25),
                                ["ClienteInhabilitadoBonificacion"] = 0,
                                ["ClienteInhabilitadoDescuento"] = 0,
                                ["migration_timestamp"] = DateTime.UtcNow
                            };
                        },
                        indexAllColumns: true, uniqueKey: null, migrationStatusType: "clientes_v2", ct: ct),

                    "cli_vista_general" => await SyncAtomicTempSwapAsync(step, db, "vista_General_cliente", "vista_General_clientev2_temp",
                        @"SELECT
                              RTRIM(ISNULL(c.NombreCompleto, '')) AS ClienteNombreCompleto,
                              RTRIM(ISNULL(c4.Descripcion, ''))  AS C4,  RTRIM(ISNULL(c27.Descripcion, '')) AS C27,
                              RTRIM(ISNULL(c21.Descripcion, '')) AS C21, RTRIM(ISNULL(c22.Descripcion, '')) AS C22,
                              RTRIM(ISNULL(c23.Descripcion, '')) AS C23, c.ID AS ID_Vega,
                              RTRIM(ISNULL(c24.Descripcion, '')) AS C24, RTRIM(ISNULL(c25.Descripcion, '')) AS C25,
                              RTRIM(ISNULL(c26.Descripcion, '')) AS C26, RTRIM(ISNULL(c28.Descripcion, '')) AS C28,
                              RTRIM(ISNULL(c29.Descripcion, '')) AS C29, RTRIM(ISNULL(c30.Descripcion, '')) AS C30,
                              RTRIM(ISNULL(c.ApellidoMaterno, '')), RTRIM(ISNULL(c.ApellidoPaterno, '')),
                              CAST(ISNULL(c.Estado, 1) AS bit), RTRIM(ISNULL(c.Codigo, '')), RTRIM(ISNULL(c.Email, '')),
                              RTRIM(ISNULL(c.DocIdentidad, '')), RTRIM(ISNULL(tc.Descripcion, '')), RTRIM(ISNULL(catc.Descripcion, '')),
                              ISNULL(c.DiasACreditoPermitidos, 0),
                              RTRIM(ISNULL(cc.Campo1, '')), RTRIM(ISNULL(cc.Campo2, '')), RTRIM(ISNULL(cc.Campo3, '')), RTRIM(ISNULL(cc.Campo4, ''))
                          FROM dbo.cli_Cliente c WITH (NOLOCK)
                          LEFT JOIN dbo.cat_TipoPersona tp WITH (NOLOCK) ON tp.ID = c.IDTipoPersona
                          LEFT JOIN dbo.cat_TipoCliente tc WITH (NOLOCK) ON tc.ID = c.IDTipoCliente
                          LEFT JOIN dbo.cat_CategoriaCliente catc WITH (NOLOCK) ON catc.ID = c.IDCategoriaCliente
                          LEFT JOIN dbo.cli_CamposCliente cc WITH (NOLOCK) ON cc.IDCliente = c.ID
                          LEFT JOIN dbo.cat_Caracteristica c4  WITH (NOLOCK) ON c4.ID  = TRY_CAST(JSON_VALUE(cc.Caracteristicas, '$.VOLUMEN_COMPRA') AS INT)
                          LEFT JOIN dbo.cat_Caracteristica c21 WITH (NOLOCK) ON c21.ID = TRY_CAST(JSON_VALUE(cc.Caracteristicas, '$.TIPO_ESTABLECIMIENTO') AS INT)
                          LEFT JOIN dbo.cat_Caracteristica c22 WITH (NOLOCK) ON c22.ID = TRY_CAST(JSON_VALUE(cc.Caracteristicas, '$.TAMANO_NEGOCIO') AS INT)
                          LEFT JOIN dbo.cat_Caracteristica c23 WITH (NOLOCK) ON c23.ID = TRY_CAST(JSON_VALUE(cc.Caracteristicas, '$.HORARIO') AS INT)
                          LEFT JOIN dbo.cat_Caracteristica c24 WITH (NOLOCK) ON c24.ID = TRY_CAST(JSON_VALUE(cc.Caracteristicas, '$.VOLUMEN_COMPRA') AS INT)
                          LEFT JOIN dbo.cat_Caracteristica c25 WITH (NOLOCK) ON c25.ID = TRY_CAST(JSON_VALUE(cc.Caracteristicas, '$.PREFERENCIA_PAGO') AS INT)
                          LEFT JOIN dbo.cat_Caracteristica c26 WITH (NOLOCK) ON c26.ID = TRY_CAST(JSON_VALUE(cc.Caracteristicas, '$.CANAL_PREFERIDO') AS INT)
                          LEFT JOIN dbo.cat_Caracteristica c27 WITH (NOLOCK) ON c27.ID = TRY_CAST(JSON_VALUE(cc.Caracteristicas, '$.SEGMENTO') AS INT)
                          LEFT JOIN dbo.cat_Caracteristica c28 WITH (NOLOCK) ON c28.ID = TRY_CAST(JSON_VALUE(cc.Caracteristicas, '$.POTENCIAL') AS INT)
                          LEFT JOIN dbo.cat_Caracteristica c29 WITH (NOLOCK) ON c29.ID = TRY_CAST(JSON_VALUE(cc.Caracteristicas, '$.FRECUENCIA_COMPRA') AS INT)
                          LEFT JOIN dbo.cat_Caracteristica c30 WITH (NOLOCK) ON c30.ID = TRY_CAST(JSON_VALUE(cc.Caracteristicas, '$.NIVEL_SERVICIO') AS INT)
                          WHERE ISNULL(c.Eliminado, 0) = 0;",
                        r => new BsonDocument
                        {
                            ["ClienteNombreCompleto"] = GetStr(r, 0),
                            ["ClienteCaracteristica4"] = GetStr(r, 1),
                            ["ClienteCaracteristica27"] = GetStr(r, 2),
                            ["ClienteCaracteristica20"] = "",
                            ["ClienteCaracteristica21"] = GetStr(r, 3),
                            ["ClienteCaracteristica22"] = GetStr(r, 4),
                            ["ClienteCaracteristica23"] = GetStr(r, 5),
                            ["ID_Vega"] = GetInt(r, 6),
                            ["ClienteCaracteristica24"] = GetStr(r, 7),
                            ["ClienteCaracteristica25"] = GetStr(r, 8),
                            ["ClienteCaracteristica26"] = GetStr(r, 9),
                            ["ClienteCaracteristica28"] = GetStr(r, 10),
                            ["ClienteCaracteristica29"] = GetStr(r, 11),
                            ["ClienteCaracteristica30"] = GetStr(r, 12),
                            ["ClienteApellidoMaterno"] = GetStr(r, 13),
                            ["ClienteApellidoPaterno"] = GetStr(r, 14),
                            ["ClienteActivo"] = GetBool(r, 15),
                            ["ClienteCodigo"] = GetStr(r, 16),
                            ["ClienteEmail"] = GetStr(r, 17),
                            ["ClienteDocIdentidad"] = GetStr(r, 18),
                            ["ClienteNombreTipo"] = GetStr(r, 19),
                            ["ClienteNombreCategoria"] = GetStr(r, 20),
                            ["ClienteDiasCredito"] = GetInt(r, 21),
                            ["ClienteCreditoContado"] = "false",
                            ["ClienteCampo1"] = GetStr(r, 22),
                            ["ClienteCampo2"] = GetStr(r, 23),
                            ["ClienteCampo3"] = GetStr(r, 24),
                            ["ClienteCampo4"] = GetStr(r, 25),
                            ["ClienteInhabilitadoBonificacion"] = 0,
                            ["ClienteInhabilitadoDescuento"] = 0,
                            ["migration_timestamp"] = DateTime.UtcNow
                        },
                        indexAllColumns: true, uniqueKey: null, migrationStatusType: "vista_General_cliente", ct: ct),

                    // =========================================================================
                    // SECCIÓN 3: MAESTROS Y CONSOLIDADOS DE PRODUCTOS / MERCADERÍA
                    // =========================================================================
                    "prd_marcas" => await SyncBulkUpsertPorIdVegaAsync(step, db, "marca",
                        "SELECT m.ID AS ID_Vega, ISNULL(m.Codigo, '') AS Codigo, ISNULL(m.Nombre, '') AS Descripcion, CAST(ISNULL(m.Estado, 1) AS bit) AS Activo FROM dbo.prd_Marca m WITH (NOLOCK) WHERE ISNULL(m.Eliminado, 0) = 0;",
                        r => new BsonDocument
                        {
                            ["ID_Vega"] = GetInt(r, 0),
                            ["Codigo"] = GetStr(r, 1),
                            ["Descripcion"] = GetStr(r, 2),
                            ["Activo"] = GetBool(r, 3)
                        }, ct),

                    "prd_lineas" => await SyncBulkUpsertPorIdVegaAsync(step, db, "linea",
                        "SELECT l.ID AS ID_Vega, ISNULL(l.Codigo, '') AS Codigo, ISNULL(l.Nombre, '') AS Descripcion, CAST(ISNULL(l.Estado, 1) AS bit) AS Activo FROM dbo.prd_LineaProducto l WITH (NOLOCK);",
                        r => new BsonDocument
                        {
                            ["ID_Vega"] = GetInt(r, 0),
                            ["Codigo"] = GetStr(r, 1),
                            ["Descripcion"] = GetStr(r, 2),
                            ["Activo"] = GetBool(r, 3)
                        }, ct),

                    "prd_subproveedores" => await SyncBulkUpsertPorIdVegaAsync(step, db, "SubProveedor",
                        "SELECT s.ID AS ID_Vega, ISNULL(s.Codigo, '') AS Codigo, ISNULL(s.Nombre, '') AS Descripcion, CAST(ISNULL(s.Estado, 1) AS bit) AS Activo FROM dbo.prd_SubProveedor s WITH (NOLOCK) WHERE ISNULL(s.Eliminado, 0) = 0;",
                        r => new BsonDocument
                        {
                            ["ID_Vega"] = GetInt(r, 0),
                            ["Codigo"] = GetStr(r, 1),
                            ["Descripcion"] = GetStr(r, 2),
                            ["Activo"] = GetBool(r, 3)
                        }, ct),

                    "prd_proveedores" => await SyncBulkUpsertPorIdVegaAsync(step, db, "Proveedor",
                        "SELECT pro.ID AS ID_Vega FROM dbo.prd_Proveedor pro WITH (NOLOCK) WHERE ISNULL(pro.Eliminado, 0) = 0;",
                        r => new BsonDocument
                        {
                            ["ID_Vega"] = GetInt(r, 0),
                            ["DiasCredito"] = 0,
                            ["Restrictivo"] = false,
                            ["CreditoContado"] = false
                        }, ct),

                    "prd_unidad_factor" => await SyncAtomicTempSwapAsync(step, db, "UnidadFactor", "UnidadFactor_temp",
                        @"SELECT o.ID AS ID_Vega, ISNULL(u.Descripcion, ISNULL(u.Nombre, '')) AS Descripcion, ISNULL(o.Abrev, 'UN') AS Abreviacion,
                                 CAST(ISNULL(o.EsUnidadBase, 0) AS bit) AS Base, CAST(ISNULL(o.Factor, 1) AS int) AS Factor,
                                 CAST(ISNULL(o.Estado, 1) AS bit) AS Activo, o.IDProducto AS IDMercaderia,
                                 CAST(ISNULL(o.PesoBruto, 0) AS float) AS Peso, CAST(ISNULL(o.Volumen, 0) AS float) AS Volumen,
                                 CAST(ISNULL(o.UsadoEnCompra, 1) AS bit) AS Compra
                          FROM dbo.prd_ProductoUnidad o WITH (NOLOCK)
                          INNER JOIN dbo.prd_UnidadMedida u WITH (NOLOCK) ON o.IDUnidadMedida = u.ID
                          WHERE ISNULL(o.Eliminado, 0) = 0;",
                        r => new BsonDocument
                        {
                            ["ID_Vega"] = GetInt(r, 0),
                            ["Descripcion"] = GetStr(r, 1),
                            ["Abreviacion"] = GetStr(r, 2),
                            ["Base"] = GetBool(r, 3),
                            ["Factor"] = GetInt(r, 4),
                            ["Activo"] = GetBool(r, 5),
                            ["IDMercaderia"] = GetInt(r, 6),
                            ["Peso"] = GetDouble(r, 7),
                            ["Volumen"] = GetDouble(r, 8),
                            ["Referencia"] = false,
                            ["Compra"] = GetBool(r, 9)
                        },
                        indexAllColumns: false, uniqueKey: "ID_Vega", migrationStatusType: null, ct: ct),

                    "prd_mercaderia_a" => await SyncAtomicTempSwapAsync(step, db, "Mercaderia_A", "Mercaderia_A_temp",
                        @"WITH UnidadesProducto AS (
                              SELECT pu.IDProducto, pu.IDUnidadMedida,
                                     ROW_NUMBER() OVER (PARTITION BY pu.IDProducto ORDER BY pu.EsUnidadBase DESC, pu.Factor ASC, pu.ID ASC) AS rn
                              FROM dbo.prd_ProductoUnidad pu WITH (NOLOCK)
                              WHERE ISNULL(pu.Estado, 1) = 1 AND ISNULL(pu.Eliminado, 0) = 0
                          ),
                          UnidadesPivot AS (
                              SELECT IDProducto,
                                     MAX(CASE WHEN rn = 1 THEN IDUnidadMedida END) AS IDUnidadBase,
                                     COALESCE(MAX(CASE WHEN rn = 2 THEN IDUnidadMedida END), MAX(CASE WHEN rn = 1 THEN IDUnidadMedida END), 0) AS IDUnidadReferencia
                              FROM UnidadesProducto
                              GROUP BY IDProducto
                          )
                          SELECT pro.ID AS ID_Vega, pro.IDMarca, pro.IDProveedor, ISNULL(pro.IDSubProveedor, 0) AS IDSubProveedor,
                                 pro.IDCategoria, pro.IDLinea, ISNULL(up.IDUnidadBase, 0) AS IDUnidadBase, ISNULL(up.IDUnidadReferencia, 0) AS IDUnidadReferencia,
                                 ISNULL(pro.Codigo, '') AS Codigo, ISNULL(NULLIF(LTRIM(RTRIM(pro.Descripcion)), ''), pro.Nombre) AS Descripcion,
                                 CAST(ISNULL(pro.Estado, 1) AS bit) AS Activo
                          FROM dbo.prd_Producto pro WITH (NOLOCK)
                          LEFT JOIN UnidadesPivot up ON up.IDProducto = pro.ID
                          WHERE ISNULL(pro.Eliminado, 0) = 0;",
                        r =>
                        {
                            string cod = GetStr(r, 8);
                            string desc = GetStr(r, 9);
                            return new BsonDocument
                            {
                                ["ID_Vega"] = GetInt(r, 0),
                                ["CodigoFabrica"] = "",
                                ["StockMinimo"] = 0.0,
                                ["StockMaximo"] = 0.0,
                                ["NivelReposicion"] = 0.0,
                                ["IDMarca"] = GetInt(r, 1),
                                ["IDProveedor"] = GetInt(r, 2),
                                ["IDSubProveedor"] = GetInt(r, 3),
                                ["Observacion"] = "",
                                ["EsINAFECTO"] = false,
                                ["Agrupacion"] = false,
                                ["GestionaStock"] = false,
                                ["IDCategoria"] = GetInt(r, 4),
                                ["IDLinea"] = GetInt(r, 5),
                                ["IDMundo"] = 1,
                                ["IDUnidadBase"] = GetInt(r, 6),
                                ["IDUnidadReferencia"] = GetInt(r, 7),
                                ["Codigo"] = cod,
                                ["CodigoAfinidad"] = cod,
                                ["Descripcion"] = desc,
                                ["Activo"] = GetBool(r, 10),
                                ["CodigoAfinidad2"] = "",
                                ["IDTipoMercaderia"] = 1,
                                ["DescripcionResumida"] = desc,
                                ["CodigoProductoSunat"] = ""
                            };
                        },
                        indexAllColumns: false, uniqueKey: "ID_Vega", migrationStatusType: null, ct: ct),

                    "prd_conversion" => await SyncAtomicTempSwapAsync(step, db, "conversion_tablamercaderias", "conversion_tablamercaderiasv2_temp",
                        @"SELECT pro.ID AS ID_Vega, pro.IDMarca, pro.IDLinea, ISNULL(pro.Codigo, '') AS ProductoCodigo,
                                 CAST(ISNULL(pro.Estado, 1) AS bit) AS ProductoActivo,
                                 ISNULL(NULLIF(LTRIM(RTRIM(pro.Descripcion)), ''), pro.Nombre) AS ProductoNombre,
                                 ISNULL(up.IDUnidadMedida, 0) AS ProductoIDUnidadBase, pro.IDProveedor
                          FROM dbo.prd_Producto pro WITH (NOLOCK)
                          OUTER APPLY (
                              SELECT TOP 1 pu.IDUnidadMedida
                              FROM dbo.prd_ProductoUnidad pu WITH (NOLOCK)
                              WHERE pu.IDProducto = pro.ID AND ISNULL(pu.Eliminado, 0) = 0
                              ORDER BY pu.EsUnidadBase DESC, pu.ID ASC
                          ) up
                          WHERE ISNULL(pro.Eliminado, 0) = 0;",
                        r =>
                        {
                            int idV = GetInt(r, 0);
                            string cod = GetStr(r, 3);
                            return new BsonDocument
                            {
                                ["ID_Vega"] = idV,
                                ["IDVegaMarca"] = GetInt(r, 1),
                                ["IDVegaLinea"] = GetInt(r, 2),
                                ["ProductoCodigo"] = cod,
                                ["IDVegaTipoProducto"] = 1,
                                ["ProductoActivo"] = GetBool(r, 4),
                                ["ProductoGestionaStock"] = 0,
                                ["ProductoAgrupacion"] = 0,
                                ["ProductoNivelReposicion"] = 0,
                                ["ProductoNombre"] = GetStr(r, 5),
                                ["ProductoStockMaximo"] = 0,
                                ["ProductoStockMinimo"] = 0,
                                ["ProductoCodigoAfinidad"] = cod,
                                ["ProductoCodigoAfinidad2"] = cod,
                                ["ProductoCodigoSunat"] = "",
                                ["ProductoDescripcionResumida"] = "",
                                ["ProductoObservacion"] = "",
                                ["ProductoIDUnidadBase"] = GetInt(r, 6),
                                ["ProductoIDUnidadReferencia"] = 0,
                                ["IDVegaProveedor"] = GetInt(r, 7),
                                ["migration_timestamp"] = DateTime.UtcNow
                            };
                        },
                        indexAllColumns: true, uniqueKey: null, migrationStatusType: "productos_v2", ct: ct),

                    "prd_vista_general" => await SyncAtomicTempSwapAsync(step, db, "vista_General_mercaderia", "vista_General_mercaderiav2_temp",
                        @"SELECT ISNULL(s.Nombre, '') AS SubProveedor, ISNULL(oo.RazonSocial, '') AS Proveedor,
                                 pro.ID AS ID_Vega, ISNULL(pro.Codigo, '') AS Codigo,
                                 ISNULL(NULLIF(LTRIM(RTRIM(pro.Descripcion)), ''), pro.Nombre) AS ProductoNombre,
                                 ISNULL(m.Nombre, '') AS Marca, ISNULL(l.Nombre, '') AS Linea,
                                 CAST(ISNULL(pro.Estado, 1) AS bit) AS Activo,
                                 CAST(ISNULL(pp.Inafecto, 0) AS bit) AS EsInafecto,
                                 ISNULL(up.IDUnidadMedida, 0) AS IDUnidadBase
                          FROM dbo.prd_Producto pro WITH (NOLOCK)
                          LEFT JOIN dbo.prd_Marca m WITH (NOLOCK) ON m.ID = pro.IDMarca
                          LEFT JOIN dbo.prd_LineaProducto l WITH (NOLOCK) ON l.ID = pro.IDLinea
                          LEFT JOIN dbo.prd_SubProveedor s WITH (NOLOCK) ON s.ID = pro.IDSubProveedor
                          LEFT JOIN dbo.prd_Proveedor oo WITH (NOLOCK) ON oo.ID = pro.IDProveedor
                          OUTER APPLY (
                              SELECT TOP 1 pu.IDUnidadMedida
                              FROM dbo.prd_ProductoUnidad pu WITH (NOLOCK)
                              WHERE pu.IDProducto = pro.ID AND ISNULL(pu.Eliminado, 0) = 0
                              ORDER BY pu.EsUnidadBase DESC, pu.ID ASC
                          ) up
                          OUTER APPLY (
                              SELECT TOP 1 ip.Inafecto
                              FROM dbo.ImpuestoProducto ip WITH (NOLOCK)
                              WHERE ip.IDProducto = pro.ID
                              ORDER BY ip.IDImpuesto ASC
                          ) pp
                          WHERE ISNULL(pro.Eliminado, 0) = 0;",
                        r => new BsonDocument
                        {
                            ["ProductoNombreSubProveedor"] = GetStr(r, 0),
                            ["ProductoNombreProveedor"] = GetStr(r, 1),
                            ["ProductoDocIdentidad"] = "SIN DocIdentidad",
                            ["ProductoNombreTipo"] = "MERCADERIA",
                            ["ProductoNivelReposicion"] = 0,
                            ["ProductoStockMaximo"] = 0,
                            ["ProductoStockMinimo"] = 0,
                            ["ID_Vega"] = GetInt(r, 2),
                            ["ProductoCodigo"] = GetStr(r, 3),
                            ["ProductoNombre"] = GetStr(r, 4),
                            ["ProductoMarcaDescripcion"] = GetStr(r, 5),
                            ["ProductoLineaDescripcion"] = GetStr(r, 6),
                            ["ProductoActivo"] = GetBool(r, 7),
                            ["ProductoGestionaStock"] = 0,
                            ["ProductoEsInafecto"] = GetBool(r, 8),
                            ["ProductoAgrupacion"] = 0,
                            ["ProductoObservacion"] = "",
                            ["ProductoIDUnidadBase"] = GetInt(r, 9),
                            ["ProductoIDUnidadReferencia"] = 0,
                            ["migration_timestamp"] = DateTime.UtcNow
                        },
                        indexAllColumns: true, uniqueKey: null, migrationStatusType: "productos_vistav2", ct: ct),

                    // =========================================================================
                    // SECCIÓN 4: MAESTROS Y CONSOLIDADOS DE FUERZA DE VENTAS
                    // =========================================================================
                    "vnd_sucursales" => await SyncBulkUpsertPorIdVegaAsync(step, db, "Sucursal",
                        "SELECT s.ID AS ID_Vega, ISNULL(s.Descripcion, '') AS Nombre, CAST(ISNULL(s.Estado, 1) AS bit) AS Activo, ISNULL(s.Codigo, '') AS Codigo FROM dbo.cat_Sucursal s WITH (NOLOCK);",
                        r => new BsonDocument
                        {
                            ["ID_Vega"] = GetInt(r, 0),
                            ["Nombre"] = GetStr(r, 1),
                            ["Direccion"] = "",
                            ["Activo"] = GetBool(r, 2),
                            ["Codigo"] = GetStr(r, 3),
                            ["IDDistrito"] = 0,
                            ["Correo"] = "",
                            ["Telefono"] = ""
                        }, ct),

                    "vnd_fuerzaventas" => await SyncBulkUpsertPorIdVegaAsync(step, db, "fuerzaventas",
                        "SELECT f.ID AS ID_Vega, CAST(f.ID AS nvarchar(20)) AS Codigo, ISNULL(f.Descripcion, '') AS Nombre FROM dbo.vnd_FuerzaVenta f WITH (NOLOCK) WHERE ISNULL(f.Eliminado, 0) = 0;",
                        r => new BsonDocument
                        {
                            ["ID_Vega"] = GetInt(r, 0),
                            ["IDDivision"] = 0,
                            ["Codigo"] = GetStr(r, 1),
                            ["Nombre"] = GetStr(r, 2),
                            ["Observacion"] = ""
                        }, ct),

                    "vnd_equipoventas" => await SyncBulkUpsertPorIdVegaAsync(step, db, "Equipoventas",
                        "SELECT g.ID AS ID_Vega, ISNULL(g.IDFuerzaVenta, 0) AS IDFuerzaVentas, ISNULL(g.IDSupervisor, 0) AS IDSupervisor, CAST(g.ID AS nvarchar(20)) AS Codigo, ISNULL(g.Nombre, '') AS Nombre FROM dbo.vnd_GrupoVentas g WITH (NOLOCK) WHERE ISNULL(g.Eliminado, 0) = 0;",
                        r => new BsonDocument
                        {
                            ["ID_Vega"] = GetInt(r, 0),
                            ["IDFuerzaVentas"] = GetInt(r, 1),
                            ["IDSupervisor"] = GetInt(r, 2),
                            ["Codigo"] = GetStr(r, 3),
                            ["Nombre"] = GetStr(r, 4),
                            ["Observacion"] = ""
                        }, ct),

                    "vnd_vendedor_base" => await SyncBulkUpsertPorIdVegaAsync(step, db, "Vendedor",
                        @"SELECT v.ID AS ID_Vega, ISNULL(v.IDGrupoVenta, 0) AS IDGrupoVentas, ISNULL(g.IDSucursal, 0) AS IDAgencia
                          FROM dbo.vnd_Vendedor v WITH (NOLOCK)
                          LEFT JOIN dbo.vnd_GrupoVentas g WITH (NOLOCK) ON g.ID = v.IDGrupoVenta
                          WHERE ISNULL(v.Eliminado, 0) = 0;",
                        r => new BsonDocument
                        {
                            ["ID_Vega"] = GetInt(r, 0),
                            ["IDGrupoVentas"] = GetInt(r, 1),
                            ["FueraDeRuta"] = false,
                            ["IDAgencia"] = GetInt(r, 2)
                        }, ct),

                    "vnd_conversion" => await SyncAtomicTempSwapAsync(step, db, "conversion_tablavendedores", "conversion_tablavendedores_temp",
                        @"SELECT v.ID AS ID_Vega, g.IDSucursal, g.ID AS IDEquipoVenta, fv.ID AS IDFuerzaVenta,
                                 ISNULL(v.NombreCompleto, '') AS NombreCompleto, ISNULL(v.DocumentoIdentidad, '') AS DocIdentidad,
                                 ISNULL(v.Codigo, '') AS Codigo, ISNULL(v.Email, '') AS Email,
                                 CAST(ISNULL(v.Estado, 1) AS bit) AS Activo, ISNULL(v.IDTipoDocIdentidad, 0) AS IDTipoDocIdentidad,
                                 ISNULL(v.Observacion, '') AS Observacion
                          FROM dbo.vnd_Vendedor v WITH (NOLOCK)
                          LEFT JOIN dbo.vnd_GrupoVentas g WITH (NOLOCK) ON g.ID = v.IDGrupoVenta
                          LEFT JOIN dbo.vnd_FuerzaVenta fv WITH (NOLOCK) ON fv.ID = g.IDFuerzaVenta
                          WHERE ISNULL(v.Eliminado, 0) = 0
                          ORDER BY v.ID ASC;",
                        r =>
                        {
                            int idV = GetInt(r, 0);
                            return new BsonDocument
                            {
                                ["PKID_Vendedor"] = idV,
                                ["ID_Vega"] = idV,
                                ["IDVegaSucursal"] = r.IsDBNull(1) ? BsonNull.Value : (BsonValue)r.GetInt32(1),
                                ["VendedorApellidoMaterno"] = "",
                                ["VendedorApellidoPaterno"] = "",
                                ["IDVegaEquipoVenta"] = r.IsDBNull(2) ? BsonNull.Value : (BsonValue)r.GetInt32(2),
                                ["IDVegaFuerzaVenta"] = r.IsDBNull(3) ? BsonNull.Value : (BsonValue)r.GetInt32(3),
                                ["VendedorPrimerNombre"] = "",
                                ["VendedorSegundoNombre"] = "",
                                ["VendedorNombreCompleto"] = GetStr(r, 4),
                                ["VendedorDocIdentidad"] = GetStr(r, 5),
                                ["VendedorCuentaBancaria"] = "",
                                ["VendedorCodigo"] = GetStr(r, 6),
                                ["VendedorExtranjero"] = false,
                                ["VendedorDescriptivo"] = false,
                                ["VendedorEmail"] = GetStr(r, 7),
                                ["VendedorWeb"] = "",
                                ["VendedorActivo"] = GetBool(r, 8),
                                ["VendedorFechaAniversario"] = BsonNull.Value,
                                ["VendedorFechaCumpleaños"] = BsonNull.Value,
                                ["VendedorExceptuadoDePercepcion"] = false,
                                ["VendedorIDTipoDocIdentidad"] = GetInt(r, 9),
                                ["VendedorIDTipoTercero"] = 1,
                                ["VendedorObservacion"] = GetStr(r, 10),
                                ["VendedorIDDireccionPrincipal"] = 0
                            };
                        },
                        indexAllColumns: true, uniqueKey: null, migrationStatusType: null, ct: ct),

                    "vnd_vista_general" => await SyncAtomicTempSwapAsync(step, db, "vista_General_vendedor", "vista_General_vendedor_temp",
                        @"SELECT v.ID AS ID_Vega, ISNULL(v.Codigo, '') AS Codigo, ISNULL(v.NombreCompleto, '') AS NombreCompleto,
                                 CAST(ISNULL(v.Estado, 1) AS bit) AS Activo, ISNULL(v.Email, '') AS Email,
                                 v.FechaNacimiento, CASE WHEN fv.ID IS NOT NULL THEN CAST(fv.ID AS nvarchar(20)) ELSE NULL END AS CodFV, g.Nombre AS NomEquipo,
                                 s.Descripcion AS NomSucursal, s.Codigo AS CodSucursal, fv.Descripcion AS NomFV,
                                 ISNULL(v.DocumentoIdentidad, '') AS DocIdentidad
                          FROM dbo.vnd_Vendedor v WITH (NOLOCK)
                          LEFT JOIN dbo.vnd_GrupoVentas g WITH (NOLOCK) ON g.ID = v.IDGrupoVenta
                          LEFT JOIN dbo.vnd_FuerzaVenta fv WITH (NOLOCK) ON fv.ID = g.IDFuerzaVenta
                          LEFT JOIN dbo.cat_Sucursal s WITH (NOLOCK) ON s.ID = g.IDSucursal
                          WHERE ISNULL(v.Eliminado, 0) = 0
                          ORDER BY v.ID ASC;",
                        r => new BsonDocument
                        {
                            ["ID_Vega"] = GetInt(r, 0),
                            ["VendedorCodigo"] = GetStr(r, 1),
                            ["VendedorNombreCompleto"] = GetStr(r, 2),
                            ["VendedorActivo"] = GetBool(r, 3) ? "SI" : "NO",
                            ["VendedorEmail"] = GetStr(r, 4),
                            ["VendedorFechaNacimiento"] = r.IsDBNull(5) ? BsonNull.Value : (BsonValue)r.GetDateTime(5).ToString("yyyy-MM-dd"),
                            ["VendedorCodigoFuerzaVentas"] = r.IsDBNull(6) ? BsonNull.Value : (BsonValue)r.GetString(6).Trim(),
                            ["VendedorNombreEquipoVentas"] = r.IsDBNull(7) ? BsonNull.Value : (BsonValue)r.GetString(7).Trim(),
                            ["VendedorNombreSucursal"] = r.IsDBNull(8) ? BsonNull.Value : (BsonValue)r.GetString(8).Trim(),
                            ["VendedorCodigoSucursal"] = r.IsDBNull(9) ? BsonNull.Value : (BsonValue)r.GetString(9).Trim(),
                            ["VendedorNombreFuerzaVentas"] = r.IsDBNull(10) ? BsonNull.Value : (BsonValue)r.GetString(10).Trim(),
                            ["VendedorObservacionFuerzaVentas"] = "",
                            ["VendedorDocIdentidad"] = GetStr(r, 11)
                        },
                        indexAllColumns: true, uniqueKey: null, migrationStatusType: null, ct: ct),

                    // =========================================================================
                    // SECCIÓN 5: OPERACIONES RADAR EN LÍNEA (STOCK Y LISTA DE PRECIOS)
                    // =========================================================================
                    "ops_radar_stock" => await SyncRadarStockAsync(step, db, ct),
                    "ops_lista_precios" => await SyncRadarListaPreciosAsync(step, db, ct),

                    _ => throw new InvalidOperationException($"Paso desconocido: {stepKey}")
                };

                sw.Stop();
                var finDt = DateTime.Now;
                double durSeg = Math.Round(sw.Elapsed.TotalSeconds, 2);

                step.FilasLeidasForce = resultado.leidos;
                step.DocsInsertadosMongo = resultado.insertados;
                step.DocsActualizadosMongo = resultado.actualizados;
                step.UltimaEjecucion = finDt;
                if (step.ModoProgramacion == "DIARIO")
                    step.UltimaFechaDiaria = finDt.ToString("yyyy-MM-dd");
                step.UltimaDuracionSegundos = durSeg;
                step.TotalEjecuciones++;
                step.ProgresoPorcentaje = 100;
                step.Estado = "COMPLETADO";
                step.FaseActual = $"Completado en {durSeg:F2}s";
                step.UltimoMensaje = $"✓ {resultado.leidos:N0} leídos · +{resultado.insertados:N0} nuevos · {resultado.actualizados:N0} act. ({durSeg:F2}s)";

                Interlocked.Increment(ref _totalEjecucionesGlobal);

                await RegistrarAuditoriaMongoSafeAsync(db, step.NombreAppAuditoria, inicioDt, finDt, durSeg, "Éxito", null, ct);

                RegistrarEvento(step.StepKey, step.NombrePaso, "SUCCESS", origen,
                    $"✓ [{step.ColeccionMongoDestino}] {resultado.leidos:N0} filas Force → Mongo (+{resultado.insertados:N0} ins, {resultado.actualizados:N0} mod) en {durSeg:F2}s.");
            }
            catch (Exception ex)
            {
                sw.Stop();
                var finDt = DateTime.Now;
                double durSeg = Math.Round(sw.Elapsed.TotalSeconds, 2);

                step.UltimaEjecucion = finDt;
                step.UltimaDuracionSegundos = durSeg;
                step.TotalErrores++;
                step.Estado = "ERROR";
                step.ProgresoPorcentaje = 100;
                step.FaseActual = "Error en sincronización";
                step.UltimoError = ex.Message;
                step.UltimoMensaje = $"✕ Error: {ex.Message}";

                _logger.LogError(ex, "Error en TraddeUpdate paso {StepKey}", stepKey);
                RegistrarEvento(step.StepKey, step.NombrePaso, "ERROR", origen, $"✕ Falló [{step.ColeccionMongoDestino}]: {ex.Message}");

                try
                {
                    var db = ObtenerMongoDb();
                    await RegistrarAuditoriaMongoSafeAsync(db, step.NombreAppAuditoria, inicioDt, finDt, durSeg, "Error", ex.Message, ct);
                }
                catch { }
            }
        }

        // =========================================================================
        // PATRÓN 1: BULKWRITE UPSERT POR ID_Vega (CATÁLOGOS Y MAESTROS)
        // =========================================================================
        private async Task<(long Leidos, long Insertados, long Actualizados)> SyncBulkUpsertPorIdVegaAsync(
            TraddeStepStatusDto step,
            IMongoDatabase db,
            string collectionName,
            string sqlQuery,
            Func<SqlDataReader, BsonDocument> mapRow,
            CancellationToken ct)
        {
            var col = db.GetCollection<BsonDocument>(collectionName);
            const int batchSize = 2500;

            long leidos = 0, insertados = 0, actualizados = 0;
            var ops = new List<WriteModel<BsonDocument>>(batchSize);

            step.FaseActual = "Extrayendo desde BDForceGVD...";
            step.ProgresoPorcentaje = 15;

            using var conn = new SqlConnection(_sqlForceConnectionString);
            await conn.OpenAsync(ct);
            using var cmd = new SqlCommand(sqlQuery, conn) { CommandTimeout = 180 };
            using var reader = await cmd.ExecuteReaderAsync(ct);

            step.FaseActual = $"Subiendo lotes a {collectionName}...";
            step.ProgresoPorcentaje = 35;

            while (await reader.ReadAsync(ct))
            {
                leidos++;
                var doc = mapRow(reader);
                var idVega = doc["ID_Vega"];
                var filter = Builders<BsonDocument>.Filter.Eq("ID_Vega", idVega);
                var update = new BsonDocument("$set", doc);
                ops.Add(new UpdateOneModel<BsonDocument>(filter, update) { IsUpsert = true });

                if (ops.Count >= batchSize)
                {
                    var res = await col.BulkWriteAsync(ops, new BulkWriteOptions { IsOrdered = false }, ct);
                    insertados += res.Upserts.Count;
                    actualizados += res.ModifiedCount;
                    ops.Clear();
                    step.FaseActual = $"Sincronizados {leidos:N0} registros en {collectionName}...";
                    step.ProgresoPorcentaje = Math.Min(90, step.ProgresoPorcentaje + 10);
                }
            }

            if (ops.Count > 0)
            {
                var res = await col.BulkWriteAsync(ops, new BulkWriteOptions { IsOrdered = false }, ct);
                insertados += res.Upserts.Count;
                actualizados += res.ModifiedCount;
                ops.Clear();
            }

            try
            {
                await col.Indexes.CreateOneAsync(
                    new CreateIndexModel<BsonDocument>(
                        Builders<BsonDocument>.IndexKeys.Ascending("ID_Vega"),
                        new CreateIndexOptions { Background = true }),
                    cancellationToken: ct);
            }
            catch { }

            return (leidos, insertados, actualizados);
        }

        // =========================================================================
        // PATRÓN 2: REEMPLAZO ATÓMICO ZERO-DOWNTIME (TEMP -> INDEX -> RENAME)
        // =========================================================================
        private async Task<(long Leidos, long Insertados, long Actualizados)> SyncAtomicTempSwapAsync(
            TraddeStepStatusDto step,
            IMongoDatabase db,
            string finalCollectionName,
            string tempCollectionName,
            string sqlQuery,
            Func<SqlDataReader, BsonDocument> mapRow,
            bool indexAllColumns,
            string? uniqueKey,
            string? migrationStatusType,
            CancellationToken ct)
        {
            var inicioUtc = DateTime.UtcNow;
            int batchSize;
            lock (_configLock) { batchSize = _config.BatchSizeMongo; }

            step.FaseActual = $"Limpiando temporal {tempCollectionName}...";
            step.ProgresoPorcentaje = 10;

            try { await db.DropCollectionAsync(tempCollectionName, ct); } catch { }

            var tempCol = db.GetCollection<BsonDocument>(tempCollectionName);
            var batch = new List<BsonDocument>(batchSize);
            BsonDocument? sampleDoc = null;
            long leidos = 0, insertados = 0;

            step.FaseActual = "Leyendo en streaming desde BDForceGVD...";
            step.ProgresoPorcentaje = 20;

            using (var conn = new SqlConnection(_sqlForceConnectionString))
            {
                await conn.OpenAsync(ct);
                using var cmd = new SqlCommand(sqlQuery, conn) { CommandTimeout = 300 };
                using var reader = await cmd.ExecuteReaderAsync(ct);

                while (await reader.ReadAsync(ct))
                {
                    leidos++;
                    var doc = mapRow(reader);
                    sampleDoc ??= doc;
                    batch.Add(doc);

                    if (batch.Count >= batchSize)
                    {
                        await tempCol.InsertManyAsync(batch, new InsertManyOptions { IsOrdered = false }, ct);
                        insertados += batch.Count;
                        batch.Clear();
                        step.FaseActual = $"Cargando temporal ({insertados:N0} docs)...";
                        step.ProgresoPorcentaje = Math.Min(75, 20 + (int)(insertados / 5000));
                    }
                }
            }

            if (batch.Count > 0)
            {
                await tempCol.InsertManyAsync(batch, new InsertManyOptions { IsOrdered = false }, ct);
                insertados += batch.Count;
                batch.Clear();
            }

            if (insertados == 0)
            {
                try { await db.DropCollectionAsync(tempCollectionName, ct); } catch { }
                throw new InvalidOperationException($"La consulta de {finalCollectionName} devolvió 0 registros; se abortó el swap para proteger la colección actual.");
            }

            // Crear índices en la colección temporal ANTES del swap atómico
            step.FaseActual = $"Construyendo índices en {tempCollectionName}...";
            step.ProgresoPorcentaje = 82;

            if (!string.IsNullOrEmpty(uniqueKey))
            {
                try
                {
                    await tempCol.Indexes.CreateOneAsync(
                        new CreateIndexModel<BsonDocument>(
                            Builders<BsonDocument>.IndexKeys.Ascending(uniqueKey),
                            new CreateIndexOptions { Unique = true, Background = true }),
                        cancellationToken: ct);
                }
                catch { }
            }
            else if (indexAllColumns && sampleDoc != null)
            {
                var idxModels = sampleDoc.Names
                    .Where(n => n != "_id")
                    .Select(n => new CreateIndexModel<BsonDocument>(
                        Builders<BsonDocument>.IndexKeys.Ascending(n),
                        new CreateIndexOptions { Background = true }))
                    .ToList();

                if (idxModels.Count > 0)
                {
                    try { await tempCol.Indexes.CreateManyAsync(idxModels, ct); } catch { }
                }
            }

            // Swap atómico en un solo paso en MongoDB (DropTarget = true)
            step.FaseActual = $"Ejecutando swap atómico → {finalCollectionName}...";
            step.ProgresoPorcentaje = 93;
            await db.RenameCollectionAsync(tempCollectionName, finalCollectionName, new RenameCollectionOptions { DropTarget = true }, ct);

            if (!string.IsNullOrEmpty(migrationStatusType))
            {
                try
                {
                    var finUtc = DateTime.UtcNow;
                    var statusCol = db.GetCollection<BsonDocument>("migration_status");
                    var statusDoc = new BsonDocument
                    {
                        ["migration_type"] = migrationStatusType,
                        ["start_time"] = inicioUtc,
                        ["end_time"] = finUtc,
                        ["total_records"] = insertados,
                        ["duration_seconds"] = (finUtc - inicioUtc).TotalSeconds
                    };
                    await statusCol.UpdateOneAsync(
                        Builders<BsonDocument>.Filter.Eq("migration_type", migrationStatusType),
                        new BsonDocument("$set", statusDoc),
                        new UpdateOptions { IsUpsert = true },
                        ct);
                }
                catch { }
            }

            return (leidos, insertados, 0);
        }

        // =========================================================================
        // PATRÓN 3: STOCK RADAR (nestle_radar_stock - 6 ALMACENES CENTRALES)
        // =========================================================================
        private async Task<(long Leidos, long Insertados, long Actualizados)> SyncRadarStockAsync(
            TraddeStepStatusDto step, IMongoDatabase db, CancellationToken ct)
        {
            const string sqlStock = @"
                SELECT pp.ID AS ID_Producto,
                       s.ID AS IDSucursal,
                       CAST(ISNULL(p.StockFisico, 0) AS int) AS StockUnidad,
                       CAST(ISNULL(pp.Estado, 1) AS bit) AS isActive
                FROM dbo.inv_ProductoAlmacen p WITH (NOLOCK)
                INNER JOIN dbo.prd_Producto pp WITH (NOLOCK) ON p.IDProducto = pp.ID
                INNER JOIN dbo.inv_Almacen a WITH (NOLOCK) ON a.ID = p.IDAlmacen
                INNER JOIN dbo.cat_Sucursal s WITH (NOLOCK) ON s.ID = a.IDSucursal
                WHERE a.Nombre IN (
                    'ALMACEN CENTRAL NORTE',
                    'ALMACEN CENTRAL CENTRO',
                    'ALMACEN CENTRAL ESTE',
                    'ALMACEN CENTRAL SUR',
                    'ALMACEN CENTRAL PRINCIPAL',
                    'ALMACEN CENTRAL HUAURA'
                )
                  AND ISNULL(pp.Eliminado, 0) = 0;";

            var col = db.GetCollection<BsonDocument>("nestle_radar_stock");
            try
            {
                await col.Indexes.CreateOneAsync(
                    new CreateIndexModel<BsonDocument>(
                        Builders<BsonDocument>.IndexKeys.Ascending("ID_Producto").Ascending("IDSucursal")),
                    cancellationToken: ct);
            }
            catch (MongoCommandException mce) when (mce.Code == 85) { }
            catch { }

            const int batchSize = 2000;
            var ops = new List<WriteModel<BsonDocument>>(batchSize);
            long leidos = 0, insertados = 0, actualizados = 0;
            var nowUtc = DateTime.UtcNow;

            step.FaseActual = "Consultando existencias de almacenes centrales en Force...";
            step.ProgresoPorcentaje = 20;

            using var conn = new SqlConnection(_sqlForceConnectionString);
            await conn.OpenAsync(ct);
            using var cmd = new SqlCommand(sqlStock, conn) { CommandTimeout = 180 };
            using var reader = await cmd.ExecuteReaderAsync(ct);

            while (await reader.ReadAsync(ct))
            {
                leidos++;
                int idProd = GetInt(reader, 0);
                int idSuc = GetInt(reader, 1);
                int stock = GetInt(reader, 2);
                bool active = GetBool(reader, 3);

                var setDoc = new BsonDocument
                {
                    ["ID_Producto"] = idProd,
                    ["IDSucursal"] = idSuc,
                    ["StockUnidad"] = stock,
                    ["isActive"] = active,
                    ["clave_compuesta"] = $"{idProd}_{idSuc}",
                    ["update"] = nowUtc
                };

                var filter = Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.Eq("ID_Producto", idProd),
                    Builders<BsonDocument>.Filter.Eq("IDSucursal", idSuc));

                var update = new BsonDocument
                {
                    ["$set"] = setDoc,
                    ["$setOnInsert"] = new BsonDocument("create", nowUtc)
                };

                ops.Add(new UpdateOneModel<BsonDocument>(filter, update) { IsUpsert = true });

                if (ops.Count >= batchSize)
                {
                    var res = await col.BulkWriteAsync(ops, new BulkWriteOptions { IsOrdered = false }, ct);
                    insertados += res.Upserts.Count;
                    actualizados += res.ModifiedCount;
                    ops.Clear();
                    step.FaseActual = $"Stock Radar sincronizado: {leidos:N0} SKUs...";
                    step.ProgresoPorcentaje = Math.Min(92, step.ProgresoPorcentaje + 12);
                }
            }

            if (ops.Count > 0)
            {
                var res = await col.BulkWriteAsync(ops, new BulkWriteOptions { IsOrdered = false }, ct);
                insertados += res.Upserts.Count;
                actualizados += res.ModifiedCount;
                ops.Clear();
            }

            return (leidos, insertados, actualizados);
        }

        // =========================================================================
        // PATRÓN 4: LISTA DE PRECIOS RADAR (Decimal128 + preservación created_at)
        // =========================================================================
        private async Task<(long Leidos, long Insertados, long Actualizados)> SyncRadarListaPreciosAsync(
            TraddeStepStatusDto step, IMongoDatabase db, CancellationToken ct)
        {
            const string mainName = "lista_precios_radar";
            const string tmpName = "lista_precios_radar_tmp";

            var mainCol = db.GetCollection<BsonDocument>(mainName);
            var createdPrevios = new Dictionary<string, BsonValue>(StringComparer.Ordinal);

            step.FaseActual = "Preservando marcas de tiempo created_at previas...";
            step.ProgresoPorcentaje = 12;

            try
            {
                var proj = Builders<BsonDocument>.Projection.Include("clave_compuesta").Include("created_at").Exclude("_id");
                using var cursor = await mainCol.Find(FilterDefinition<BsonDocument>.Empty).Project(proj).ToCursorAsync(ct);
                while (await cursor.MoveNextAsync(ct))
                {
                    foreach (var d in cursor.Current)
                    {
                        if (d.TryGetValue("clave_compuesta", out var kVal) && kVal.IsString &&
                            d.TryGetValue("created_at", out var cVal))
                        {
                            createdPrevios[kVal.AsString] = cVal;
                        }
                    }
                }
            }
            catch { }

            try { await db.DropCollectionAsync(tmpName, ct); } catch { }

            const string sqlPrecios = @"
                SELECT i.IDProducto AS Id_Producto,
                       p.IDUnidad AS Id_Unidad,
                       CAST(ISNULL(pp.Desde, 0) AS int) AS Desde,
                       CAST(ISNULL(pp.Hasta, 0) AS int) AS Hasta,
                       CAST(ISNULL(pp.Precio, 0) AS decimal(18,6)) AS Precio,
                       LTRIM(RTRIM(ISNULL(ll.Nombre, ''))) AS Canal
                FROM dbo.prc_ItemPrecioProducto i WITH (NOLOCK)
                INNER JOIN dbo.prc_PrecioProductoUnidad p WITH (NOLOCK) ON i.ID = p.IDItemPrecioProducto
                INNER JOIN dbo.prc_ListaPrecio ll WITH (NOLOCK) ON ll.ID = i.IDListaPrecios
                INNER JOIN dbo.prc_PrecioEscala pp WITH (NOLOCK) ON pp.IDUnidadItemListaPrecios = p.ID
                WHERE ISNULL(ll.Estado, 1) = 1
                  AND ISNULL(ll.Eliminado, 0) = 0;";

            var docsPorClave = new Dictionary<string, BsonDocument>(StringComparer.Ordinal);
            long leidos = 0;
            var nowBson = new BsonDateTime(DateTime.UtcNow);

            step.FaseActual = "Extrayendo escalas de precios activas desde BDForceGVD...";
            step.ProgresoPorcentaje = 30;

            using (var conn = new SqlConnection(_sqlForceConnectionString))
            {
                await conn.OpenAsync(ct);
                using var cmd = new SqlCommand(sqlPrecios, conn) { CommandTimeout = 240 };
                using var reader = await cmd.ExecuteReaderAsync(ct);

                while (await reader.ReadAsync(ct))
                {
                    leidos++;
                    int idProd = GetInt(reader, 0);
                    int idUnid = GetInt(reader, 1);
                    int desde = GetInt(reader, 2);
                    int hasta = GetInt(reader, 3);
                    decimal precioDec = reader.IsDBNull(4) ? 0m : reader.GetDecimal(4);
                    string canal = GetStr(reader, 5);
                    string clave = $"{idProd}_{idUnid}_{canal}";

                    var createdVal = createdPrevios.TryGetValue(clave, out var prev) ? prev : nowBson;

                    docsPorClave[clave] = new BsonDocument
                    {
                        ["Id_Producto"] = idProd,
                        ["Id_Unidad"] = idUnid,
                        ["Desde"] = desde,
                        ["Hasta"] = hasta,
                        ["Precio"] = new BsonDecimal128(new Decimal128(Math.Round(precioDec, 6))),
                        ["Canal"] = canal,
                        ["clave_compuesta"] = clave,
                        ["created_at"] = createdVal,
                        ["updated_at"] = nowBson
                    };
                }
            }

            if (docsPorClave.Count == 0)
                throw new InvalidOperationException("La consulta de Lista de Precios no devolvió registros; se conserva la colección actual.");

            var tmpCol = db.GetCollection<BsonDocument>(tmpName);
            const int batchSize = 10000;
            var lote = new List<BsonDocument>(batchSize);
            long insertados = 0;

            step.FaseActual = $"Insertando {docsPorClave.Count:N0} precios únicos en temporal...";
            step.ProgresoPorcentaje = 65;

            foreach (var doc in docsPorClave.Values)
            {
                lote.Add(doc);
                if (lote.Count >= batchSize)
                {
                    await tmpCol.InsertManyAsync(lote, new InsertManyOptions { IsOrdered = false }, ct);
                    insertados += lote.Count;
                    lote.Clear();
                }
            }
            if (lote.Count > 0)
            {
                await tmpCol.InsertManyAsync(lote, new InsertManyOptions { IsOrdered = false }, ct);
                insertados += lote.Count;
                lote.Clear();
            }

            step.FaseActual = "Creando índice único clave_compuesta y aplicando swap atómico...";
            step.ProgresoPorcentaje = 88;

            await tmpCol.Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys.Ascending("clave_compuesta"),
                    new CreateIndexOptions { Unique = true }),
                cancellationToken: ct);

            await db.RenameCollectionAsync(tmpName, mainName, new RenameCollectionOptions { DropTarget = true }, ct);

            return (leidos, insertados, 0);
        }

        private static async Task RegistrarAuditoriaMongoSafeAsync(
            IMongoDatabase db, string nombreApp, DateTime inicio, DateTime fin, double duracionSeg, string resultado, string? error, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(nombreApp)) return;
            try
            {
                var auditCol = db.GetCollection<BsonDocument>("Auditoria_Api");
                var setDoc = new BsonDocument
                {
                    ["nombre_app"] = nombreApp,
                    ["inicio_ejecucion"] = inicio,
                    ["fin_ejecucion"] = fin,
                    ["duracion_hms"] = TimeSpan.FromSeconds(duracionSeg).ToString(@"hh\:mm\:ss"),
                    ["duracion"] = duracionSeg,
                    ["resultado"] = resultado,
                    ["error"] = error == null ? BsonNull.Value : (BsonValue)error
                };
                await auditCol.UpdateOneAsync(
                    Builders<BsonDocument>.Filter.Eq("nombre_app", nombreApp),
                    new BsonDocument("$set", setDoc),
                    new UpdateOptions { IsUpsert = true },
                    ct);
            }
            catch { }
        }

        private static int GetInt(SqlDataReader r, int i) => r.IsDBNull(i) ? 0 : Convert.ToInt32(r.GetValue(i));
        private static double GetDouble(SqlDataReader r, int i) => r.IsDBNull(i) ? 0.0 : Convert.ToDouble(r.GetValue(i));
        private static bool GetBool(SqlDataReader r, int i) => !r.IsDBNull(i) && Convert.ToBoolean(r.GetValue(i));
        private static string GetStr(SqlDataReader r, int i) => r.IsDBNull(i) ? string.Empty : Convert.ToString(r.GetValue(i))?.Trim() ?? string.Empty;
        private static string Trunc255(string s) => s.Length <= 255 ? s : s.Substring(0, 255);
    }
}
