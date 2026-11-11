-- ==========================================================================================
-- ARCHIVO: 12_SP_Sincronizar_Stock_Force.sql
-- PROYECTO: IntegradorFlexForce
-- BASE DE DATOS: BDForceGVD (192.168.2.93)
-- OBJETIVO: Motor de Sincronización Profesional de Stock en Línea para inv_ProductoAlmacen.
--           - 100% Solo Lectura contra el ERP Antiguo (SVBD7524.digalimenta41) sin alterar nada allá.
--           - Detección atómica diferencial por HASH (BINARY_CHECKSUM) en memoria local.
--           - Actualizaciones en micropulsos con ROWLOCK (cero lock escalation, cero bloqueos a vendedores).
--           - Prevención de solapamiento con sp_getapplock.
--           - Auditoría integrada con sync_TransaccionLote y sync_Log.
-- ==========================================================================================

USE [BDForceGVD]
GO

-- -------------------------------------------------------------
-- 1. CONFIGURACIÓN INICIAL DEL MÓDULO DE STOCK EN sync_Configuracion
-- -------------------------------------------------------------
IF EXISTS (SELECT * FROM sys.tables WHERE name = 'sync_Configuracion')
BEGIN
    IF NOT EXISTS (SELECT 1 FROM dbo.sync_Configuracion WHERE Clave = 'AUTO_STOCK_ACTIVO')
        INSERT INTO dbo.sync_Configuracion (Clave, Valor, Descripcion)
        VALUES ('AUTO_STOCK_ACTIVO', '1', '1 = Sincronización automática de stock activa, 0 = Pausada');

    IF NOT EXISTS (SELECT 1 FROM dbo.sync_Configuracion WHERE Clave = 'MODO_STOCK')
        INSERT INTO dbo.sync_Configuracion (Clave, Valor, Descripcion)
        VALUES ('MODO_STOCK', 'INTERVALO', 'INTERVALO o MADRUGADA');

    IF NOT EXISTS (SELECT 1 FROM dbo.sync_Configuracion WHERE Clave = 'INTERVALO_STOCK_MINUTOS')
        INSERT INTO dbo.sync_Configuracion (Clave, Valor, Descripcion)
        VALUES ('INTERVALO_STOCK_MINUTOS', '2', 'Frecuencia de sincronización de stock en minutos');

    IF NOT EXISTS (SELECT 1 FROM dbo.sync_Configuracion WHERE Clave = 'HORA_INICIO_STOCK')
        INSERT INTO dbo.sync_Configuracion (Clave, Valor, Descripcion)
        VALUES ('HORA_INICIO_STOCK', '05:00', 'Hora inicial permitida para sincronización de stock');

    IF NOT EXISTS (SELECT 1 FROM dbo.sync_Configuracion WHERE Clave = 'HORA_FIN_STOCK')
        INSERT INTO dbo.sync_Configuracion (Clave, Valor, Descripcion)
        VALUES ('HORA_FIN_STOCK', '23:30', 'Hora final permitida para sincronización de stock');

    IF NOT EXISTS (SELECT 1 FROM dbo.sync_Configuracion WHERE Clave = 'HORA_MADRUGADA_STOCK')
        INSERT INTO dbo.sync_Configuracion (Clave, Valor, Descripcion)
        VALUES ('HORA_MADRUGADA_STOCK', '04:30', 'Hora de ejecución en modo madrugada');
END
GO

-- -------------------------------------------------------------
-- 2. PROCEDIMIENTO ALMACENADO PRINCIPAL: usp_Sync_SincronizarStock_Directo
-- -------------------------------------------------------------
CREATE OR ALTER PROCEDURE dbo.usp_Sync_SincronizarStock_Directo
    @TipoEjecucion          varchar(20)          = 'AUTOMATICO', -- 'AUTOMATICO', 'MANUAL'
    @Usuario                nvarchar(50)         = 'SYSTEM',
    @LoteId                 uniqueidentifier     = NULL OUTPUT,
    @TotalRemotos           int                  = 0 OUTPUT,
    @TotalActualizados      int                  = 0 OUTPUT,
    @TotalInsertados        int                  = 0 OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    SET DEADLOCK_PRIORITY LOW;

    DECLARE @FechaInicio datetime = GETDATE();
    DECLARE @LockResult int;
    DECLARE @TransaccionID bigint = NULL;

    IF @LoteId IS NULL
        SET @LoteId = NEWID();

    -- A. PROTECCIÓN CONTRA EJECUCIÓN SOLAPADA
    EXEC @LockResult = sp_getapplock 
        @Resource    = 'Lock_usp_Sync_Stock', 
        @LockMode    = 'Exclusive', 
        @LockOwner   = 'Session', 
        @LockTimeout = 0;

    IF @LockResult < 0
    BEGIN
        IF EXISTS (SELECT * FROM sys.tables WHERE name = 'sync_Log')
        BEGIN
            INSERT INTO dbo.sync_Log (LoteId, Entidad, Nivel, Mensaje, DetalleTecnico, Fecha)
            VALUES (@LoteId, 'STOCK', 'WARN', 'Sincronización de stock omitida: Ya existe otro ciclo en ejecución activa.', 'sp_getapplock retorno: ' + CAST(@LockResult AS varchar(10)), GETDATE());
        END
        RETURN;
    END

    -- B. REGISTRAR INICIO DE LOTE EN sync_TransaccionLote
    IF EXISTS (SELECT * FROM sys.tables WHERE name = 'sync_TransaccionLote')
    BEGIN
        INSERT INTO dbo.sync_TransaccionLote (
            LoteId, Entidad, TipoEjecucion, FechaInicio, Estado, Usuario
        )
        VALUES (
            @LoteId, 'STOCK', @TipoEjecucion, @FechaInicio, 'EN_PROCESO', @Usuario
        );
        SET @TransaccionID = SCOPE_IDENTITY();
    END

    BEGIN TRY
        -- ---------------------------------------------------------------------
        -- 1. LECTURA DIRECTA 100% READ-ONLY DESDE EL ERP ANTIGUO (SVBD7524)
        -- Trae únicamente los datos de stock y calcula un hash matemático BINARY_CHECKSUM
        -- ---------------------------------------------------------------------
        IF OBJECT_ID('tempdb..#StageStockRemote') IS NOT NULL 
            DROP TABLE #StageStockRemote;

        SELECT 
            r.IDProducto,
            r.IDAlmacen,
            r.Capacidad,
            r.StockFisico,
            r.StockContable,
            r.StockPorEntregar,
            r.StockPorRecibir,
            r.TiempoDeEntrega,
            r.FrecuenciaDeCompra,
            r.CantidadMinima,
            r.StockMinimo,
            r.Activo,
            r.HashRemoto
        INTO #StageStockRemote
        FROM OPENQUERY(SVBD7524, '
            SELECT 
                pa.IDProducto, 
                pa.IDAlmacen, 
                ISNULL(pa.Capacidad, 0)         AS Capacidad,
                ISNULL(pa.StockFisico, 0)       AS StockFisico,
                ISNULL(pa.StockContable, 0)     AS StockContable,
                ISNULL(pa.StockPorEntregar, 0)  AS StockPorEntregar,
                ISNULL(pa.StockPorRecibir, 0)   AS StockPorRecibir,
                ISNULL(pa.TiempoDeEntrega, 0)   AS TiempoDeEntrega,
                ISNULL(pa.FrecuenciaDeCompra, 0) AS FrecuenciaDeCompra,
                pa.CantidadMinima,
                pa.StockMinimo,
                pa.Activo,
                BINARY_CHECKSUM(
                    pa.Capacidad, pa.StockFisico, pa.StockContable, 
                    pa.StockPorEntregar, pa.StockPorRecibir, 
                    pa.TiempoDeEntrega, pa.FrecuenciaDeCompra, 
                    pa.CantidadMinima, pa.StockMinimo, pa.Activo
                ) AS HashRemoto
            FROM digalimenta41.dbo.ProductoAlmacen pa WITH (NOLOCK)
        ') r;

        SET @TotalRemotos = @@ROWCOUNT;

        CREATE CLUSTERED INDEX IX_StageStock_Prod_Alm ON #StageStockRemote(IDProducto, IDAlmacen);

        -- ---------------------------------------------------------------------
        -- 2. DETECTAR DIFERENCIAS EN MEMORIA LOCAL
        -- Solo se seleccionan registros donde el Hash local difiera del Hash remoto
        -- ---------------------------------------------------------------------
        IF OBJECT_ID('tempdb..#DiferenciasStock') IS NOT NULL 
            DROP TABLE #DiferenciasStock;

        SELECT 
            p.ID AS LocalIDProducto,
            a.ID AS LocalIDAlmacen,
            rem.Capacidad,
            rem.StockFisico,
            rem.StockContable,
            rem.StockPorEntregar,
            rem.StockPorRecibir,
            rem.TiempoDeEntrega,
            rem.FrecuenciaDeCompra,
            rem.CantidadMinima,
            rem.StockMinimo,
            rem.Activo,
            ROW_NUMBER() OVER (ORDER BY p.ID, a.ID) AS RN
        INTO #DiferenciasStock
        FROM #StageStockRemote rem
        INNER JOIN dbo.prd_Producto p  ON p.IDExtra = rem.IDProducto
        INNER JOIN dbo.inv_Almacen  a  ON a.IDExtra = rem.IDAlmacen
        INNER JOIN dbo.inv_ProductoAlmacen loc WITH (NOLOCK)
            ON loc.IDProducto = p.ID 
           AND loc.IDAlmacen  = a.ID
        WHERE 
            BINARY_CHECKSUM(
                loc.Capacidad, loc.StockFisico, loc.StockContable, 
                loc.StockPorEntregar, loc.StockPorRecibir, 
                loc.TiempoDeEntrega, loc.FrecuenciaDeCompra, 
                loc.CantidadMinima, loc.StockMinimo, loc.Estado
            ) <> rem.HashRemoto;

        CREATE UNIQUE CLUSTERED INDEX IX_DiferenciasStock_RN ON #DiferenciasStock(RN);

        SET @TotalActualizados = (SELECT COUNT(*) FROM #DiferenciasStock);

        -- ---------------------------------------------------------------------
        -- 3. ACTUALIZACIÓN EN MICROPULSOS (ANTI-BLOQUEOS / ROWLOCK)
        -- Si cambiaron 50 productos, solo se tocan esos 50. Cero Lock Escalation.
        -- ---------------------------------------------------------------------
        IF @TotalActualizados > 0
        BEGIN
            DECLARE @BatchSize INT = 2500;
            DECLARE @Desde INT = 1;

            WHILE @Desde <= @TotalActualizados
            BEGIN
                UPDATE target WITH (ROWLOCK)
                SET 
                    target.Capacidad          = c.Capacidad,
                    target.StockFisico        = c.StockFisico,
                    target.StockContable      = c.StockContable,
                    target.StockPorEntregar   = c.StockPorEntregar,
                    target.StockPorRecibir    = c.StockPorRecibir,
                    target.TiempoDeEntrega    = c.TiempoDeEntrega,
                    target.FrecuenciaDeCompra = c.FrecuenciaDeCompra,
                    target.CantidadMinima     = c.CantidadMinima,
                    target.StockMinimo        = c.StockMinimo,
                    target.Estado             = c.Activo,
                    target.FechaModificacion  = SYSDATETIME()
                FROM dbo.inv_ProductoAlmacen target
                INNER JOIN #DiferenciasStock c 
                    ON target.IDProducto = c.LocalIDProducto 
                   AND target.IDAlmacen  = c.LocalIDAlmacen
                WHERE c.RN BETWEEN @Desde AND @Desde + @BatchSize - 1;

                SET @Desde += @BatchSize;
            END
        END

        -- ---------------------------------------------------------------------
        -- 4. INSERTAR RELACIONES NUEVAS (Si aparecieron nuevos productos/almacenes)
        -- ---------------------------------------------------------------------
        INSERT INTO dbo.inv_ProductoAlmacen (
            IDProducto, IDAlmacen, Capacidad, StockFisico, StockContable,
            StockPorEntregar, StockPorRecibir, TiempoDeEntrega, FrecuenciaDeCompra,
            CantidadMinima, StockMinimo, StockMaximo, Estado, FechaCreacion, FechaModificacion,
            Eliminado, FechaEliminacion, UsuarioEliminacion, MotivoEliminacion
        )
        SELECT 
            p.ID, a.ID, rem.Capacidad, rem.StockFisico, rem.StockContable,
            rem.StockPorEntregar, rem.StockPorRecibir, rem.TiempoDeEntrega, rem.FrecuenciaDeCompra,
            rem.CantidadMinima, rem.StockMinimo, NULL, rem.Activo, GETDATE(), GETDATE(),
            0, NULL, NULL, NULL
        FROM #StageStockRemote rem
        INNER JOIN dbo.prd_Producto p ON p.IDExtra = rem.IDProducto
        INNER JOIN dbo.inv_Almacen  a ON a.IDExtra = rem.IDAlmacen
        WHERE NOT EXISTS (
            SELECT 1 FROM dbo.inv_ProductoAlmacen loc
            WHERE loc.IDProducto = p.ID AND loc.IDAlmacen = a.ID
        );

        SET @TotalInsertados = @@ROWCOUNT;

        -- ---------------------------------------------------------------------
        -- 5. FINALIZAR AUDITORÍA DE LOTE EXITOSO
        -- ---------------------------------------------------------------------
        DECLARE @FechaFin datetime = GETDATE();
        DECLARE @DuracionSeg decimal(10,2) = DATEDIFF(MILLISECOND, @FechaInicio, @FechaFin) / 1000.0;

        IF @TransaccionID IS NOT NULL
        BEGIN
            UPDATE dbo.sync_TransaccionLote
            SET 
                FechaFin         = @FechaFin,
                DuracionSegundos = @DuracionSeg,
                TotalEscaneados  = @TotalRemotos,
                TotalNuevos      = @TotalInsertados,
                TotalModificados = @TotalActualizados,
                TotalErrores     = 0,
                Estado           = 'EXITOSO'
            WHERE ID = @TransaccionID;
        END

        IF EXISTS (SELECT * FROM sys.tables WHERE name = 'sync_Log')
        BEGIN
            INSERT INTO dbo.sync_Log (LoteId, Entidad, Nivel, Mensaje, DetalleTecnico, Fecha)
            VALUES (
                @LoteId, 'STOCK', 'INFO', 
                CONCAT('Sincronización de Stock exitosa. Modificados: ', @TotalActualizados, ', Nuevos: ', @TotalInsertados, ', Escaneados: ', @TotalRemotos, '. Duración: ', @DuracionSeg, 's.'),
                NULL, GETDATE()
            );
        END

        -- Limpieza de temporales
        DROP TABLE #StageStockRemote;
        DROP TABLE #DiferenciasStock;

    END TRY
    BEGIN CATCH
        DECLARE @ErrorMsg nvarchar(max) = ERROR_MESSAGE();
        DECLARE @FechaError datetime = GETDATE();
        DECLARE @DuracionErr decimal(10,2) = DATEDIFF(MILLISECOND, @FechaInicio, @FechaError) / 1000.0;

        IF @TransaccionID IS NOT NULL
        BEGIN
            UPDATE dbo.sync_TransaccionLote
            SET 
                FechaFin         = @FechaError,
                DuracionSegundos = @DuracionErr,
                TotalErrores     = 1,
                Estado           = 'FALLIDO'
            WHERE ID = @TransaccionID;
        END

        IF EXISTS (SELECT * FROM sys.tables WHERE name = 'sync_Log')
        BEGIN
            INSERT INTO dbo.sync_Log (LoteId, Entidad, Nivel, Mensaje, DetalleTecnico, Fecha)
            VALUES (@LoteId, 'STOCK', 'ERROR', CONCAT('Error en sincronización de stock: ', LEFT(@ErrorMsg, 400)), @ErrorMsg, GETDATE());
        END

        EXEC sp_releaseapplock @Resource = 'Lock_usp_Sync_Stock', @LockOwner = 'Session';
        THROW;
    END CATCH;

    EXEC sp_releaseapplock @Resource = 'Lock_usp_Sync_Stock', @LockOwner = 'Session';
END;
GO

PRINT 'Procedimiento dbo.usp_Sync_SincronizarStock_Directo creado/actualizado exitosamente.';
GO