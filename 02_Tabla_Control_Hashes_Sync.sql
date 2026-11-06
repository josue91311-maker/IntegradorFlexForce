-- ==========================================================================================
-- ARCHIVO: 02_Tabla_Control_Hashes_Sync.sql
-- PROYECTO: IntegradorFlexForce
-- BASE DE DATOS: BDForceGVD (o base de datos intermedia del Integrador)
-- OBJETIVO: Tabla de control de huellas digitales (Fingerprinting).
--           Pesa menos de 15 MB para 230,000 registros y permite detectar cambios en ~1 segundo.
-- ==========================================================================================

USE [BDForceGVD]
GO

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'sync_ControlHashes')
BEGIN
    CREATE TABLE dbo.sync_ControlHashes (
        Entidad             varchar(50)     NOT NULL, -- 'CLIENTES', 'PRODUCTOS', 'PRECIOS', etc.
        IDOrigen            int             NOT NULL, -- ID del ERP Antiguo (va a IDExtra)
        Codigo              nvarchar(50)    NOT NULL, -- Código de negocio (ej: CodigoCliente)
        LastHash            varbinary(32)   NOT NULL, -- Hash SHA-256 de la última versión sincronizada
        IDDestino           int             NULL,     -- ID autonumérico asignado en Force (cli_Cliente.ID)
        FechaSincronizacion datetime        NOT NULL DEFAULT (getdate()),
        EstadoSync          varchar(20)     NOT NULL DEFAULT ('SINCRONIZADO'), -- 'SINCRONIZADO', 'ERROR', 'PENDIENTE'
        UltimoError         nvarchar(max)   NULL,
        
        CONSTRAINT PK_sync_ControlHashes PRIMARY KEY CLUSTERED (Entidad, IDOrigen)
    );

    CREATE NONCLUSTERED INDEX IX_sync_ControlHashes_Codigo 
    ON dbo.sync_ControlHashes (Entidad, Codigo);

    PRINT 'Tabla dbo.sync_ControlHashes creada exitosamente.';
END
ELSE
BEGIN
    PRINT 'La tabla dbo.sync_ControlHashes ya existe.';
END
GO
