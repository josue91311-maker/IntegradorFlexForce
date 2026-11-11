-- ==========================================================================================
-- ARCHIVO: 13_Sincronizar_Caracteristicas_Clientes.sql
-- PROYECTO: IntegradorFlexForce (ERP Antiguo -> Force Distribución ERP)
-- BASE DE DATOS DESTINO: BDForceGVD (192.168.2.93)
-- SERVIDOR VINCULADO: SVBD7524 -> digalimenta41 (192.168.2.16)
-- OBJETIVO: Sincronizar e hidratar la tabla dbo.cli_CamposCliente con los campos libres 
--           (Campo1 a Campo4) y el JSON completo de Características (IDs + Descripciones)
--           para todos los clientes de Force.
-- NOTA: Incluye COLLATE DATABASE_DEFAULT para prevenir conflicto entre Modern_Spanish y SQL_Latin1
-- ==========================================================================================

USE [BDForceGVD]
GO

-- 1. ASEGURAR ESTRUCTURA DE TABLA dbo.cli_CamposCliente EN BDForceGVD
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

-- 2. PROCEDIMIENTO DE POBLACIÓN MASIVA ULTRA RÁPIDA DE CARACTERÍSTICAS
CREATE OR ALTER PROCEDURE dbo.usp_Sync_SincronizarCaracteristicasClientes
    @CodigoClienteEspecifico varchar(50) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

    PRINT '>>> 1/3 Extrayendo características desde digalimenta41...';

    IF OBJECT_ID('tempdb..#TmpCaractRemoto') IS NOT NULL DROP TABLE #TmpCaractRemoto;

    CREATE TABLE #TmpCaractRemoto (
        ID_Cliente_Origen   int NOT NULL,
        CodigoCliente       varchar(50) COLLATE Modern_Spanish_CI_AS NOT NULL,
        Campo1              nvarchar(255) COLLATE Modern_Spanish_CI_AS NULL,
        Campo2              nvarchar(255) COLLATE Modern_Spanish_CI_AS NULL,
        Campo3              nvarchar(255) COLLATE Modern_Spanish_CI_AS NULL,
        Campo4              nvarchar(255) COLLATE Modern_Spanish_CI_AS NULL,
        IDCaracteristica1   int NULL,
        DescCaract1         nvarchar(255) COLLATE Modern_Spanish_CI_AS NULL,
        IDCaracteristica2   int NULL,
        DescCaract2         nvarchar(255) COLLATE Modern_Spanish_CI_AS NULL,
        IDCaracteristica3   int NULL,
        DescCaract3         nvarchar(255) COLLATE Modern_Spanish_CI_AS NULL,
        IDCaracteristica4   int NULL,
        DescCaract4         nvarchar(255) COLLATE Modern_Spanish_CI_AS NULL
    );

    IF @CodigoClienteEspecifico IS NOT NULL
    BEGIN
        INSERT INTO #TmpCaractRemoto
        SELECT 
            c.PKID AS ID_Cliente_Origen,
            LTRIM(RTRIM(p.Codigo)) COLLATE Modern_Spanish_CI_AS AS CodigoCliente,
            NULLIF(LTRIM(RTRIM(c.Campo1)), '') COLLATE Modern_Spanish_CI_AS AS Campo1,
            NULLIF(LTRIM(RTRIM(c.Campo2)), '') COLLATE Modern_Spanish_CI_AS AS Campo2,
            NULLIF(LTRIM(RTRIM(c.Campo3)), '') COLLATE Modern_Spanish_CI_AS AS Campo3,
            NULLIF(LTRIM(RTRIM(c.Campo4)), '') COLLATE Modern_Spanish_CI_AS AS Campo4,
            ISNULL(c.IDCaracteristica1, 0) AS IDCaracteristica1,
            ISNULL(car1.Descripcion, 'Sin Caracteristica') COLLATE Modern_Spanish_CI_AS AS DescCaract1,
            ISNULL(c.IDCaracteristica2, 0) AS IDCaracteristica2,
            ISNULL(car2.Descripcion, 'Sin Caracteristica') COLLATE Modern_Spanish_CI_AS AS DescCaract2,
            ISNULL(c.IDCaracteristica3, 0) AS IDCaracteristica3,
            ISNULL(car3.Descripcion, 'Sin Caracteristica') COLLATE Modern_Spanish_CI_AS AS DescCaract3,
            ISNULL(c.IDCaracteristica4, 0) AS IDCaracteristica4,
            ISNULL(car4.Descripcion, 'Sin Caracteristica') COLLATE Modern_Spanish_CI_AS AS DescCaract4
        FROM SVBD7524.digalimenta41.dbo.Cliente c WITH (NOLOCK)
        INNER JOIN SVBD7524.digalimenta41.dbo.Persona p WITH (NOLOCK) ON p.PKID = c.PKID
        LEFT JOIN SVBD7524.digalimenta41.dbo.Caracteristica car1 WITH (NOLOCK) ON car1.PKID = c.IDCaracteristica1
        LEFT JOIN SVBD7524.digalimenta41.dbo.Caracteristica car2 WITH (NOLOCK) ON car2.PKID = c.IDCaracteristica2
        LEFT JOIN SVBD7524.digalimenta41.dbo.Caracteristica car3 WITH (NOLOCK) ON car3.PKID = c.IDCaracteristica3
        LEFT JOIN SVBD7524.digalimenta41.dbo.Caracteristica car4 WITH (NOLOCK) ON car4.PKID = c.IDCaracteristica4
        WHERE p.Codigo COLLATE Modern_Spanish_CI_AS = @CodigoClienteEspecifico COLLATE Modern_Spanish_CI_AS;
    END
    ELSE
    BEGIN
        INSERT INTO #TmpCaractRemoto
        SELECT 
            ID_Cliente_Origen,
            CodigoCliente COLLATE Modern_Spanish_CI_AS,
            Campo1 COLLATE Modern_Spanish_CI_AS,
            Campo2 COLLATE Modern_Spanish_CI_AS,
            Campo3 COLLATE Modern_Spanish_CI_AS,
            Campo4 COLLATE Modern_Spanish_CI_AS,
            IDCaracteristica1,
            DescCaract1 COLLATE Modern_Spanish_CI_AS,
            IDCaracteristica2,
            DescCaract2 COLLATE Modern_Spanish_CI_AS,
            IDCaracteristica3,
            DescCaract3 COLLATE Modern_Spanish_CI_AS,
            IDCaracteristica4,
            DescCaract4 COLLATE Modern_Spanish_CI_AS
        FROM OPENQUERY(SVBD7524, '
            SELECT 
                c.PKID AS ID_Cliente_Origen,
                LTRIM(RTRIM(p.Codigo)) AS CodigoCliente,
                NULLIF(LTRIM(RTRIM(c.Campo1)), '''') AS Campo1,
                NULLIF(LTRIM(RTRIM(c.Campo2)), '''') AS Campo2,
                NULLIF(LTRIM(RTRIM(c.Campo3)), '''') AS Campo3,
                NULLIF(LTRIM(RTRIM(c.Campo4)), '''') AS Campo4,
                ISNULL(c.IDCaracteristica1, 0) AS IDCaracteristica1,
                ISNULL(car1.Descripcion, ''Sin Caracteristica'') AS DescCaract1,
                ISNULL(c.IDCaracteristica2, 0) AS IDCaracteristica2,
                ISNULL(car2.Descripcion, ''Sin Caracteristica'') AS DescCaract2,
                ISNULL(c.IDCaracteristica3, 0) AS IDCaracteristica3,
                ISNULL(car3.Descripcion, ''Sin Caracteristica'') AS DescCaract3,
                ISNULL(c.IDCaracteristica4, 0) AS IDCaracteristica4,
                ISNULL(car4.Descripcion, ''Sin Caracteristica'') AS DescCaract4
            FROM digalimenta41.dbo.Cliente c WITH (NOLOCK)
            INNER JOIN digalimenta41.dbo.Persona p WITH (NOLOCK) ON p.PKID = c.PKID
            LEFT JOIN digalimenta41.dbo.Caracteristica car1 WITH (NOLOCK) ON car1.PKID = c.IDCaracteristica1
            LEFT JOIN digalimenta41.dbo.Caracteristica car2 WITH (NOLOCK) ON car2.PKID = c.IDCaracteristica2
            LEFT JOIN digalimenta41.dbo.Caracteristica car3 WITH (NOLOCK) ON car3.PKID = c.IDCaracteristica3
            LEFT JOIN digalimenta41.dbo.Caracteristica car4 WITH (NOLOCK) ON car4.PKID = c.IDCaracteristica4
            WHERE c.IDCaracteristica1 > 0 OR c.IDCaracteristica2 > 0 OR c.IDCaracteristica3 > 0 OR c.IDCaracteristica4 > 0
               OR NULLIF(LTRIM(RTRIM(c.Campo1)), '''') IS NOT NULL
               OR NULLIF(LTRIM(RTRIM(c.Campo2)), '''') IS NOT NULL
        ');
    END;

    CREATE CLUSTERED INDEX IX_TmpCaract_Origen ON #TmpCaractRemoto(ID_Cliente_Origen);

    PRINT '>>> 2/3 Generando JSON de características...';

    IF OBJECT_ID('tempdb..#TmpCaractConJson') IS NOT NULL DROP TABLE #TmpCaractConJson;

    CREATE TABLE #TmpCaractConJson (
        IDClienteForce      int NOT NULL,
        Campo1              nvarchar(255) COLLATE Modern_Spanish_CI_AS NULL,
        Campo2              nvarchar(255) COLLATE Modern_Spanish_CI_AS NULL,
        Campo3              nvarchar(255) COLLATE Modern_Spanish_CI_AS NULL,
        Campo4              nvarchar(255) COLLATE Modern_Spanish_CI_AS NULL,
        JsonCaract          nvarchar(max) COLLATE Modern_Spanish_CI_AS NULL
    );

    INSERT INTO #TmpCaractConJson
    SELECT 
        fc.ID AS IDClienteForce,
        r.Campo1,
        r.Campo2,
        r.Campo3,
        r.Campo4,
        CONCAT(
            '{',
            '"IDCaracteristica1":', r.IDCaracteristica1, ',"TipoNegocio_Giro":"', STRING_ESCAPE(r.DescCaract1, 'json'), '"',
            CASE WHEN r.IDCaracteristica2 > 0 THEN CONCAT(',"IDCaracteristica2":', r.IDCaracteristica2, ',"Caracteristica_2":"', STRING_ESCAPE(r.DescCaract2, 'json'), '"') ELSE '' END,
            CASE WHEN r.IDCaracteristica3 > 0 THEN CONCAT(',"IDCaracteristica3":', r.IDCaracteristica3, ',"Caracteristica_3":"', STRING_ESCAPE(r.DescCaract3, 'json'), '"') ELSE '' END,
            CASE WHEN r.IDCaracteristica4 > 0 THEN CONCAT(',"IDCaracteristica4":', r.IDCaracteristica4, ',"Caracteristica_4":"', STRING_ESCAPE(r.DescCaract4, 'json'), '"') ELSE '' END,
            '}'
        ) AS JsonCaract
    FROM #TmpCaractRemoto r
    INNER JOIN dbo.cli_Cliente fc WITH (NOLOCK) 
        ON (fc.IDExtra IS NOT NULL AND fc.IDExtra = r.ID_Cliente_Origen)
        OR (fc.Codigo COLLATE Modern_Spanish_CI_AS = r.CodigoCliente COLLATE Modern_Spanish_CI_AS);

    CREATE CLUSTERED INDEX IX_TmpConJson_Force ON #TmpCaractConJson(IDClienteForce);

    PRINT '>>> 3/3 Insertando y actualizando en dbo.cli_CamposCliente...';

    -- Actualizar registros existentes
    UPDATE cc
    SET 
        cc.Campo1 = COALESCE(r.Campo1, cc.Campo1),
        cc.Campo2 = COALESCE(r.Campo2, cc.Campo2),
        cc.Campo3 = COALESCE(r.Campo3, cc.Campo3),
        cc.Campo4 = COALESCE(r.Campo4, cc.Campo4),
        cc.Caracteristicas = r.JsonCaract,
        cc.FechaModificacion = GETDATE()
    FROM dbo.cli_CamposCliente cc
    INNER JOIN #TmpCaractConJson r ON r.IDClienteForce = cc.IDCliente;

    DECLARE @TotalUpdates int = @@ROWCOUNT;

    -- Insertar registros nuevos que no existían en cli_CamposCliente
    INSERT INTO dbo.cli_CamposCliente (IDCliente, Campo1, Campo2, Campo3, Campo4, Caracteristicas, FechaCreacion, FechaModificacion)
    SELECT 
        r.IDClienteForce,
        r.Campo1,
        r.Campo2,
        r.Campo3,
        r.Campo4,
        r.JsonCaract,
        GETDATE(),
        GETDATE()
    FROM #TmpCaractConJson r
    LEFT JOIN dbo.cli_CamposCliente cc WITH (NOLOCK) ON cc.IDCliente = r.IDClienteForce
    WHERE cc.IDCliente IS NULL;

    DECLARE @TotalInserts int = @@ROWCOUNT;

    PRINT '>>> ¡Sincronización Completada con Éxito!';
    PRINT '    Registros actualizados en cli_CamposCliente: ' + CAST(@TotalUpdates AS varchar(20));
    PRINT '    Registros insertados en cli_CamposCliente:   ' + CAST(@TotalInserts AS varchar(20));
END
GO
