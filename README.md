# IntegradorFlexForce — Portal de Sincronización On-Premise

## 1. Visión General
**IntegradorFlexForce** es el middleware de integración y portal web de gestión para sincronizar de manera incremental, resiliente y continua los **230,000+ clientes, direcciones y cartera de rutas** desde el ERP Antiguo hacia el nuevo **Force Distribución ERP (`BDForceGVD`)**.

* **Tecnología**: .NET 8 (Web API + BackgroundService Worker + Portal SPA en HTML5/CSS3/JS vanilla sin dependencias pesadas).
* **Diseño On-Premise**: Listo para ejecutarse como Servicio de Windows en el servidor local.
* **Técnica de Alto Rendimiento**: **Data Fingerprinting (SHA-256 / `HASHBYTES`)**. Detecta deltas sobre 230,000 registros en **~1.5 segundos** sin aplicar bloqueos en el ERP antiguo.
* **Tolerancia a Fallos**: Procesamiento en lotes (batch de 500 registros con transacciones atómicas individuales). Un cliente con error de datos no detiene a los otros 229,999.

---

## 2. Mapa de Conexiones Configurables (`appsettings.json`)

```json
{
  "ConnectionStrings": {
    "ForceGVD": "Server=192.168.2.93;Database=BDForceGVD;User Id=ForceTradde;Password=Peru2021;TrustServerCertificate=True;MultipleActiveResultSets=True;Connection Timeout=60;",
    "ERPAntiguo": "Server=192.168.2.16;Database=digalimenta41;User Id=Test10;Password=Peru2023;TrustServerCertificate=True;MultipleActiveResultSets=True;Connection Timeout=60;"
  }
}
```
*(Ambas cadenas de conexión son 100% dinámicas y configurables sin recompilar).*

---

## 3. Catálogo de Scripts SQL en la Solución

| Archivo | Servidor / Base de Datos | Descripción |
| :--- | :--- | :--- |
| [`01_Vista_Extraccion_Clientes_ERP_Antiguo.sql`](01_Vista_Extraccion_Clientes_ERP_Antiguo.sql) | **ERP Antiguo** (`digalimenta41`) | Vista `vw_Sync_Clientes_Antiguo` con `HASHBYTES('SHA2_256', ...)` y `WITH (NOLOCK)`. |
| [`02_Tabla_Control_Hashes_Sync.sql`](02_Tabla_Control_Hashes_Sync.sql) | **Force ERP** (`BDForceGVD`) | Tabla `dbo.sync_ControlHashes` para control de deltas. |
| [`03_SP_Upsert_Cliente_Force.sql`](03_SP_Upsert_Cliente_Force.sql) | **Force ERP** (`BDForceGVD`) | SP `dbo.usp_Sync_UpsertCliente` con auto-aprovisionamiento y soporte `IDExtra`. |
| [`04_Vista_Rutas_Zonas_Cartera.sql`](04_Vista_Rutas_Zonas_Cartera.sql) | **ERP Antiguo** (`digalimenta41`) | Vista oficial de cartera y fuerza de ventas (`DireccionZona` -> `RutaVendedor`). |
| [`05_Vista_Creditos_Catalogos.sql`](05_Vista_Creditos_Catalogos.sql) | **ERP Antiguo** (`digalimenta41`) | Vista de líneas de crédito y plazos de pago. |
| [`06_Vista_Deudas_Cartera.sql`](06_Vista_Deudas_Cartera.sql) | **ERP Antiguo** (`digalimenta41`) | Vista de documentos pendientes y saldos por cobrar. |
| [`07_SP_Vincular_Cliente_Ruta.sql`](07_SP_Vincular_Cliente_Ruta.sql) | **Force ERP** (`BDForceGVD`) | SP `dbo.usp_Sync_VincularCarteraRuta` para asignar clientes a zonas y rutas. |
| [`08_Tablas_Logs_Transacciones_Config.sql`](08_Tablas_Logs_Transacciones_Config.sql) | **Force ERP** (`BDForceGVD`) | DDL de `sync_Configuracion`, `sync_TransaccionLote` y `sync_Log` para auditoría y control del portal. |

---

## 4. Instalación On-Premise como Servicio de Windows

1. En el servidor On-Premise, abrir PowerShell como Administrador.
2. Navegar a esta carpeta y ejecutar:
   ```powershell
   .\Install-WindowsService.ps1 -Port 5050
   ```
3. El script compilará la aplicación en `./publish`, registrará el servicio `IntegradorFlexForce` con inicio automático y recuperación ante fallos, e iniciará el portal.
4. Acceder al Portal Web desde cualquier navegador de la red:
   `http://192.168.2.XX:5050` o `http://localhost:5050`.

---

## 5. Capacidades del Portal Web
* **Monitoreo en Tiempo Real**: Estado actual (En Espera, Escaneando, Procesando Lotes), métricas del último lote y tiempos de respuesta.
* **Control Manual**: Botón `Sincronizar Ahora (Manual)` para forzar un barrido inmediato.
* **Control de Programación Dinámica**:
  * Activar/Pausar la sincronización en segundo plano con un clic.
  * Cambiar el intervalo de escaneo (ej. cada 3 minutos).
  * Definir la ventana horaria permitida (ej. de 05:00 a 23:30) para no operar durante cierres contables nocturnos.
  * Ajustar tamaño de lote (batch size de 500 registros) y reintentos.
* **Historial de Lotes / Transacciones**: Duración, nuevos, actualizados y fallidos por ejecución.
* **Auditoría y Logs Técnicos**: Filtro por nivel (INFO, WARN, ERROR), búsqueda por código de cliente o PKID, y visor modal con el StackTrace completo y error SQL exacto.
