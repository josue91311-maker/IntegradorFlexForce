-- ==========================================================================================
-- ARCHIVO: 08_Tablas_Logs_Transacciones_Config.sql
-- PROYECTO: IntegradorFlexForce
-- BASE DE DATOS: BDForceGVD
-- OBJETIVO: Tablas para control de transacciones por lotes, logs detallados de auditoría
--           y configuración dinámica de horarios/frecuencia para el Portal Web.
-- ==========================================================================================

USE [BDForceGVD]
GO

-- -------------------------------------------------------------
-- 1. TABLA DE CONFIGURACIÓN DINÁMICA DEL SINCRONIZADOR
-- -------------------------------------------------------------
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'sync_Configuracion')
BEGIN
    CREATE TABLE dbo.sync_Configuracion (
        Clave               varchar(50)     NOT NULL,
        Valor               nvarchar(500)   NOT NULL,
        Descripcion         nvarchar(255)   NOT NULL,
        FechaModificacion   datetime        NOT NULL DEFAULT (GETDATE()),
        CONSTRAINT PK_sync_Configuracion PRIMARY KEY (Clave)
    );

    -- Valores iniciales recomendados (MODO MANUAL POR DEFECTO)
    INSERT INTO dbo.sync_Configuracion (Clave, Valor, Descripcion)
    VALUES 
    ('SINCRONIZACION_ACTIVA', '0', '1 = Sincronizador automático activo, 0 = Pausado (Solo Manual)'),
    ('MODO_EJECUCION', 'INTERVALO', 'INTERVALO (cada X minutos) o PROGRAMADO (hora fija)'),
    ('INTERVALO_MINUTOS', '3', 'Minutos entre cada escaneo de huellas (RowHash)'),
    ('HORA_INICIO_VENTANA', '05:00', 'Hora inicial permitida para sincronizaciones automáticas'),
    ('HORA_FIN_VENTANA', '23:30', 'Hora final permitida para sincronizaciones automáticas'),
    ('TAMANO_LOTE', '500', 'Cantidad de registros a procesar por transacción individual'),
    ('REINTENTOS_MAXIMOS', '3', 'Intentos automáticos si un cliente individual falla');

    PRINT 'Tabla dbo.sync_Configuracion creada con parámetros por defecto.';
END
GO

-- -------------------------------------------------------------
-- 2. TABLA DE LOTES Y TRANSACCIONES DE SINCRONIZACIÓN
-- -------------------------------------------------------------
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'sync_TransaccionLote')
BEGIN
    CREATE TABLE dbo.sync_TransaccionLote (
        ID                  bigint IDENTITY(1,1) NOT NULL,
        LoteId              uniqueidentifier     NOT NULL,
        Entidad             varchar(50)          NOT NULL, -- 'CLIENTES', 'CARTERA_RUTAS', etc.
        TipoEjecucion       varchar(20)          NOT NULL, -- 'AUTOMATICO', 'MANUAL'
        FechaInicio         datetime             NOT NULL DEFAULT (GETDATE()),
        FechaFin            datetime             NULL,
        DuracionSegundos    decimal(10,2)        NULL,
        TotalEscaneados     int                  NOT NULL DEFAULT (0),
        TotalNuevos         int                  NOT NULL DEFAULT (0),
        TotalModificados    int                  NOT NULL DEFAULT (0),
        TotalErrores        int                  NOT NULL DEFAULT (0),
        Estado              varchar(20)          NOT NULL DEFAULT ('EN_PROCESO'), -- 'EN_PROCESO', 'EXITOSO', 'CON_ERRORES', 'FALLIDO'
        Usuario             nvarchar(50)         NULL DEFAULT ('SYSTEM'),
        
        CONSTRAINT PK_sync_TransaccionLote PRIMARY KEY (ID)
    );

    CREATE NONCLUSTERED INDEX IX_sync_TransaccionLote_Fecha 
    ON dbo.sync_TransaccionLote (FechaInicio DESC);

    PRINT 'Tabla dbo.sync_TransaccionLote creada exitosamente.';
END
GO

-- -------------------------------------------------------------
-- 3. TABLA DE LOGS DETALLADOS Y AUDITORÍA DE ERRORES
-- -------------------------------------------------------------
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'sync_Log')
BEGIN
    CREATE TABLE dbo.sync_Log (
        ID                  bigint IDENTITY(1,1) NOT NULL,
        LoteId              uniqueidentifier     NULL,
        Entidad             varchar(50)          NOT NULL,
        Nivel               varchar(20)          NOT NULL, -- 'INFO', 'WARN', 'ERROR', 'FATAL'
        IDOrigen            int                  NULL,     -- c.PKID del ERP viejo
        Codigo              nvarchar(50)         NULL,     -- CodigoCliente
        Mensaje             nvarchar(500)        NOT NULL,
        DetalleTecnico      nvarchar(max)        NULL,     -- StackTrace, JSON o mensaje SQL
        Fecha               datetime             NOT NULL DEFAULT (GETDATE()),
        
        CONSTRAINT PK_sync_Log PRIMARY KEY (ID)
    );

    CREATE NONCLUSTERED INDEX IX_sync_Log_Fecha_Nivel 
    ON dbo.sync_Log (Fecha DESC, Nivel);

    CREATE NONCLUSTERED INDEX IX_sync_Log_IDOrigen 
    ON dbo.sync_Log (IDOrigen);

    PRINT 'Tabla dbo.sync_Log creada exitosamente.';
END
GO
