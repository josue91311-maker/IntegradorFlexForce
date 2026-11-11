-- ==========================================================================================
-- ARCHIVO: 13_Sincronizar_Caracteristicas_Clientes.sql
-- PROYECTO: IntegradorFlexForce (ERP Antiguo -> Force Distribución ERP)
-- BASE DE DATOS DESTINO: BDForceGVD (192.168.2.93)
-- SERVIDOR VINCULADO: SVBD7524 -> digalimenta41 (192.168.2.16)
-- OBJETIVO: Sincronizar e hidratar la tabla dbo.cli_CamposCliente con los campos libres 
--           (Campo1 a Campo4) y el JSON completo de Características (IDs + Descripciones)
--           para todos los clientes de Force.
-- ==========================================================================================

USE [BDForceGVD]
GO

-- ------------------------------------------------------------------------------------------
-- 1. ASEGURAR ESTRUCTURA DE TABLA dbo.cli_CamposCliente EN BDForceGVD
-- ------------------------------------------------------------------------------------------
IF OBJECT_ID('dbo.cli_CamposCliente', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.cli_CamposCliente (
        ID int IDENTITY(1,1) NOT NULL PRIMARY KEY,
        IDCliente int NOT NULL UNIQUE CONSTRAINT FK_CamposCliente_Cliente REFERENCES dbo.cli_Cliente(ID) ON DELETE CASCADE,
        Campo1 nvarchar(255) NULL,
        Campo2 nvarchar(255) NULL,
        Campo3 nvarchar(255) NULL,
        Campo4 nvarchar(255) NULL,
        Campo5 nvarchar(255) NULL,
        Caracteristicas nvarchar(max) NULL,
        CamposAdicionales nvarchar(max) NULL,
        FechaCreacion datetime2(7) DEFAULT GETDATE(),
        FechaModificacion datetime2(7) DEFAULT GETDATE()
    );
END
GO

-- ------------------------------------------------------------------------------------------
-- 2. PROCEDIMIENTO DE POBLACIÓN MASIVA / DIFERENCIAL DE CARACTERÍSTICAS
-- ------------------------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE dbo.usp_Sync_SincronizarCaracteristicasClientes
    @CodigoClienteEspecifico varchar(50) = NULL -- Si se pasa, sincroniza solo ese cliente (ej: '371377')
AS
BEGIN
    SET NOCOUNT ON;
    SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

    PRINT '>>> Iniciando Sincronización de Características y Campos de Clientes...';

    -- Tabla temporal para recibir las características desde el ERP Antiguo
    IF OBJECT_ID('tempdb..#TmpCaractRemoto') IS NOT NULL DROP TABLE #TmpCaractRemoto;

    CREATE TABLE #TmpCaractRemoto (
        ID_Cliente_Origen   int NOT NULL,
        CodigoCliente       varchar(50) NOT NULL,
        Campo1              nvarchar(255) NULL,
        Campo2              nvarchar(255) NULL,
        Campo3              nvarchar(255) NULL,
        Campo4              nvarchar(255) NULL,
        IDCaracteristica1   int NULL,
        DescCaract1         nvarchar(255) NULL,
        IDCaracteristica2   int NULL,
        DescCaract2         nvarchar(255) NULL,
        IDCaracteristica3   int NULL,
        DescCaract3         nvarchar(255) NULL,
        IDCaracteristica4   int NULL,
        DescCaract4         nvarchar(255) NULL
    );

    IF @CodigoClienteEspecifico IS NOT NULL
    BEGIN
        -- Extracción puntual para un cliente
        INSERT INTO #TmpCaractRemoto
        SELECT 
            c.PKID AS ID_Cliente_Origen,
            LTRIM(RTRIM(p.Codigo)) AS CodigoCliente,
            NULLIF(LTRIM(RTRIM(c.Campo1)), '') AS Campo1,
            NULLIF(LTRIM(RTRIM(c.Campo2)), '') AS Campo2,
            NULLIF(LTRIM(RTRIM(c.Campo3)), '') AS Campo3,
            NULLIF(LTRIM(RTRIM(c.Campo4)), '') AS Campo4,
            ISNULL(c.IDCaracteristica1, 0),
            ISNULL(car1.Descripcion, 'Sin Caracteristica'),
            ISNULL(c.IDCaracteristica2, 0),
            ISNULL(car2.Descripcion, 'Sin Caracteristica'),
            ISNULL(c.IDCaracteristica3, 0),
            ISNULL(car3.Descripcion, 'Sin Caracteristica'),
            ISNULL(c.IDCaracteristica4, 0),
            ISNULL(car4.Descripcion, 'Sin Caracteristica')
        FROM SVBD7524.digalimenta41.dbo.Cliente c WITH (NOLOCK)
        INNER JOIN SVBD7524.digalimenta41.dbo.Persona p WITH (NOLOCK) ON p.PKID = c.PKID
        LEFT JOIN SVBD7524.digalimenta41.dbo.Caracteristica car1 WITH (NOLOCK) ON car1.PKID = c.IDCaracteristica1
        LEFT JOIN SVBD7524.digalimenta41.dbo.Caracteristica car2 WITH (NOLOCK) ON car2.PKID = c.IDCaracteristica2
        LEFT JOIN SVBD7524.digalimenta41.dbo.Caracteristica car3 WITH (NOLOCK) ON car3.PKID = c.IDCaracteristica3
        LEFT JOIN SVBD7524.digalimenta41.dbo.Caracteristica car4 WITH (NOLOCK) ON car4.PKID = c.IDCaracteristica4
        WHERE p.Codigo = @CodigoClienteEspecifico;
    END
    ELSE
    BEGIN
        -- Extracción masiva optimizada
        INSERT INTO #TmpCaractRemoto
        SELECT 
            c.PKID AS ID_Cliente_Origen,
            LTRIM(RTRIM(p.Codigo)) AS CodigoCliente,
            NULLIF(LTRIM(RTRIM(c.Campo1)), '') AS Campo1,
            NULLIF(LTRIM(RTRIM(c.Campo2)), '') AS Campo2,
            NULLIF(LTRIM(RTRIM(c.Campo3)), '') AS Campo3,
            NULLIF(LTRIM(RTRIM(c.Campo4)), '') AS Campo4,
            ISNULL(c.IDCaracteristica1, 0),
            ISNULL(car1.Descripcion, 'Sin Caracteristica'),
            ISNULL(c.IDCaracteristica2, 0),
            ISNULL(car2.Descripcion, 'Sin Caracteristica'),
            ISNULL(c.IDCaracteristica3, 0),
            ISNULL(car3.Descripcion, 'Sin Caracteristica'),
            ISNULL(c.IDCaracteristica4, 0),
            ISNULL(car4.Descripcion, 'Sin Caracteristica')
        FROM SVBD7524.digalimenta41.dbo.Cliente c WITH (NOLOCK)
        INNER JOIN SVBD7524.digalimenta41.dbo.Persona p WITH (NOLOCK) ON p.PKID = c.PKID
        LEFT JOIN SVBD7524.digalimenta41.dbo.Caracteristica car1 WITH (NOLOCK) ON car1.PKID = c.IDCaracteristica1
        LEFT JOIN SVBD7524.digalimenta41.dbo.Caracteristica car2 WITH (NOLOCK) ON car2.PKID = c.IDCaracteristica2
        LEFT JOIN SVBD7524.digalimenta41.dbo.Caracteristica car3 WITH (NOLOCK) ON car3.PKID = c.IDCaracteristica3
        LEFT JOIN SVBD7524.digalimenta41.dbo.Caracteristica car4 WITH (NOLOCK) ON car4.PKID = c.IDCaracteristica4
        WHERE c.IDCaracteristica1 > 0 OR c.IDCaracteristica2 > 0 OR c.IDCaracteristica3 > 0 OR c.IDCaracteristica4 > 0
           OR NULLIF(LTRIM(RTRIM(c.Campo1)), '') IS NOT NULL
           OR NULLIF(LTRIM(RTRIM(c.Campo2)), '') IS NOT NULL;
    END;

    CREATE CLUSTERED INDEX IX_TmpCaract_Origen ON #TmpCaractRemoto(ID_Cliente_Origen);

    -- --------------------------------------------------------------------------------------
    -- Generar JSON e Insertar / Actualizar en dbo.cli_CamposCliente
    -- --------------------------------------------------------------------------------------
    ;WITH DataConJson AS (
        SELECT 
            fc.ID AS IDClienteForce,
            r.Campo1,
            r.Campo2,
            r.Campo3,
            r.Campo4,
            (
                SELECT 
                    r.IDCaracteristica1 AS [IDCaracteristica1],
                    r.DescCaract1       AS [TipoNegocio_Giro],
                    r.IDCaracteristica2 AS [IDCaracteristica2],
                    r.DescCaract2       AS [Caracteristica_2],
                    r.IDCaracteristica3 AS [IDCaracteristica3],
                    r.DescCaract3       AS [Caracteristica_3],
                    r.IDCaracteristica4 AS [IDCaracteristica4],
                    r.DescCaract4       AS [Caracteristica_4]
                FOR JSON PATH, WITHOUT_ARRAY_WRAPPER
            ) AS CaracteristicasJson
        FROM #TmpCaractRemoto r
        INNER JOIN dbo.cli_Cliente fc WITH (NOLOCK) 
            ON fc.IDExtra = r.ID_Cliente_Origen OR fc.Codigo = r.CodigoCliente
    )
    MERGE INTO dbo.cli_CamposCliente AS target
    USING DataConJson AS src
    ON target.IDCliente = src.IDClienteForce
    WHEN MATCHED THEN
        UPDATE SET 
            target.Campo1 = COALESCE(src.Campo1, target.Campo1),
            target.Campo2 = COALESCE(src.Campo2, target.Campo2),
            target.Campo3 = COALESCE(src.Campo3, target.Campo3),
            target.Campo4 = COALESCE(src.Campo4, target.Campo4),
            target.Caracteristicas = src.CaracteristicasJson,
            target.FechaModificacion = GETDATE()
    WHEN NOT MATCHED THEN
        INSERT (IDCliente, Campo1, Campo2, Campo3, Campo4, Caracteristicas, FechaCreacion, FechaModificacion)
        VALUES (src.IDClienteForce, src.Campo1, src.Campo2, src.Campo3, src.Campo4, src.CaracteristicasJson, GETDATE(), GETDATE());

    PRINT '>>> Sincronización de Características y Campos finalizada exitosamente.';
END
GO

-- ------------------------------------------------------------------------------------------
-- 3. EJEMPLO DE EJECUCIÓN INMEDIATA PARA EL CLIENTE ESPECÍFICO (371377):
-- ------------------------------------------------------------------------------------------
-- EXEC dbo.usp_Sync_SincronizarCaracteristicasClientes @CodigoClienteEspecifico = '371377';
--
-- O PARA TODOS LOS CLIENTES DE UNA SOLA VEZ:
-- EXEC dbo.usp_Sync_SincronizarCaracteristicasClientes;
