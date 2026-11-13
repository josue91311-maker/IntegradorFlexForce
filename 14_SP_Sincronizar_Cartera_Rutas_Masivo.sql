-- ==========================================================================================
-- ARCHIVO: 14_SP_Sincronizar_Cartera_Rutas_Masivo.sql
-- PROYECTO: IntegradorFlexForce (ERP Antiguo -> Force Distribución ERP)
-- BASE DE DATOS DESTINO: BDForceGVD (192.168.2.93)
-- SERVIDOR VINCULADO: SVBD7524 -> digalimenta41 (192.168.2.16)
-- OBJETIVO: Sincronización Masiva de Alto Rendimiento (Set-Based Fast Engine)
--           para la Cartera Comercial de Zonas, Rutas, Días de Visita y Vendedores.
--           Procesa más de 80,000 registros en ~1-2 minutos directamente dentro del motor SQL
--           eliminando el cuello de botella de llamadas individuales (1 a 1).
--
-- TABLAS IMPACTADAS EN BDForceGVD:
--   1. dbo.vnd_Vendedor      (Altas / Reactivación)
--   2. dbo.cat_Zona          (Altas / Reactivación - SIN columna Eliminado)
--   3. dbo.rut_Ruta          (Altas / Asignación a Vendedor)
--   4. dbo.rut_RutaZonaDia   (Sincronización Set-Based de días activos y baja de zombis)
--   5. dbo.dir_DireccionZona (Vinculación masiva de Direcciones a Zonas con EsPrincipalVenta)
--
-- GARANTÍAS DE INTEGRIDAD:
--   - No borra información física (eliminación lógica: Estado = 0).
--   - Maneja colisiones de collation (Modern_Spanish_CI_AS vs SQL_Latin1_General).
--   - Protege rutas hermanas co-existentes en la misma zona y día.
--   - Solo desactiva zonas que están activamente gestionadas por el ERP Antiguo.
-- ==========================================================================================

USE [BDForceGVD]
GO

CREATE OR ALTER PROCEDURE dbo.usp_Sync_SincronizarCarteraRutasMasivo
    @CodigoClienteEspecifico varchar(50) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

    DECLARE @HoraInicio datetime = GETDATE();
    PRINT '>>> [PASO 1/6] Extrayendo cartera comercial desde SVBD7524.digalimenta41...';

    -- -------------------------------------------------------------
    -- 0. TABLA TEMPORAL DE EXTRACCIÓN MASIVA
    -- -------------------------------------------------------------
    IF OBJECT_ID('tempdb..#TmpCarteraRemoto') IS NOT NULL DROP TABLE #TmpCarteraRemoto;

    CREATE TABLE #TmpCarteraRemoto (
        ID_Direccion_Origen int NOT NULL,
        ID_Cliente_Origen   int NOT NULL,
        CodigoCliente       varchar(50) COLLATE Modern_Spanish_CI_AS NOT NULL,
        NombreCliente       nvarchar(255) COLLATE Modern_Spanish_CI_AS NULL,
        CodigoVendedor      varchar(50) COLLATE Modern_Spanish_CI_AS NOT NULL,
        NombreVendedor      nvarchar(255) COLLATE Modern_Spanish_CI_AS NULL,
        CodigoZonaVenta     varchar(50) COLLATE Modern_Spanish_CI_AS NOT NULL,
        NombreZonaVenta     nvarchar(255) COLLATE Modern_Spanish_CI_AS NULL,
        CodigoRuta          varchar(50) COLLATE Modern_Spanish_CI_AS NOT NULL,
        DiaDeSemana         int NOT NULL
    );

    IF @CodigoClienteEspecifico IS NOT NULL AND LTRIM(RTRIM(@CodigoClienteEspecifico)) <> ''
    BEGIN
        INSERT INTO #TmpCarteraRemoto
        SELECT 
            dc.PKID                                 AS ID_Direccion_Origen,
            p.PKID                                  AS ID_Cliente_Origen,
            LTRIM(RTRIM(p.Codigo)) COLLATE Modern_Spanish_CI_AS AS CodigoCliente,
            LTRIM(RTRIM(p.Nombre)) COLLATE Modern_Spanish_CI_AS AS NombreCliente,
            LTRIM(RTRIM(ISNULL(pv.Codigo, ''))) COLLATE Modern_Spanish_CI_AS AS CodigoVendedor,
            LTRIM(RTRIM(ISNULL(pv.Nombre, ''))) COLLATE Modern_Spanish_CI_AS AS NombreVendedor,
            LTRIM(RTRIM(z.Codigo)) COLLATE Modern_Spanish_CI_AS AS CodigoZonaVenta,
            LTRIM(RTRIM(z.Descripcion)) COLLATE Modern_Spanish_CI_AS AS NombreZonaVenta,
            LTRIM(RTRIM(ISNULL(rr.Codigo, ''))) COLLATE Modern_Spanish_CI_AS AS CodigoRuta,
            CASE 
                WHEN z.Codigo LIKE '%SAB%' OR rr.Codigo LIKE '%SAB%' THEN 6
                WHEN z.Codigo LIKE '%DOM%' OR rr.Codigo LIKE '%DOM%' THEN 7
                WHEN z.Codigo LIKE '%VIE%' OR rr.Codigo LIKE '%VIE%' THEN 5
                WHEN z.Codigo LIKE '%JUE%' OR rr.Codigo LIKE '%JUE%' THEN 4
                WHEN z.Codigo LIKE '%MIE%' OR rr.Codigo LIKE '%MIE%' THEN 3
                WHEN z.Codigo LIKE '%MAR%' OR rr.Codigo LIKE '%MAR%' THEN 2
                WHEN z.Codigo LIKE '%LUN%' OR rr.Codigo LIKE '%LUN%' THEN 1
                WHEN LOWER(LTRIM(RTRIM(rr.DiaDeSemana))) LIKE 's%b%' OR rr.DiaDeSemana = '6' THEN 6
                WHEN LOWER(LTRIM(RTRIM(rr.DiaDeSemana))) LIKE 'dom%' OR rr.DiaDeSemana = '7' THEN 7
                WHEN LOWER(LTRIM(RTRIM(rr.DiaDeSemana))) LIKE 'vie%' OR rr.DiaDeSemana = '5' THEN 5
                WHEN LOWER(LTRIM(RTRIM(rr.DiaDeSemana))) LIKE 'jue%' OR rr.DiaDeSemana = '4' THEN 4
                WHEN LOWER(LTRIM(RTRIM(rr.DiaDeSemana))) LIKE 'mi%'  OR rr.DiaDeSemana = '3' THEN 3
                WHEN LOWER(LTRIM(RTRIM(rr.DiaDeSemana))) LIKE 'mar%' OR rr.DiaDeSemana = '2' THEN 2
                WHEN LOWER(LTRIM(RTRIM(rr.DiaDeSemana))) LIKE 'lun%' OR rr.DiaDeSemana = '1' THEN 1
                ELSE 1
            END                                     AS DiaDeSemana
        FROM SVBD7524.digalimenta41.dbo.DireccionZona dz WITH (NOLOCK)
        INNER JOIN SVBD7524.digalimenta41.dbo.Persona p WITH (NOLOCK) ON dz.IDCliente = p.PKID
        INNER JOIN SVBD7524.digalimenta41.dbo.DireccionCliente dc WITH (NOLOCK) ON dc.PKID = dz.IDDireccion
        INNER JOIN SVBD7524.digalimenta41.dbo.Direccion dd WITH (NOLOCK) ON dd.PKID = dc.PKID
        INNER JOIN SVBD7524.digalimenta41.dbo.Zona z WITH (NOLOCK) ON z.PKID = dz.IDZona
        INNER JOIN SVBD7524.digalimenta41.dbo.ZonaRutaVendedor zrv WITH (NOLOCK) ON zrv.IDZona = z.PKID
        INNER JOIN SVBD7524.digalimenta41.dbo.RutaVendedor rr WITH (NOLOCK) ON rr.PKID = zrv.IDRutaVendedor
        INNER JOIN SVBD7524.digalimenta41.dbo.AsignacionRutaZonaVendedor ass WITH (NOLOCK) ON ass.PKID = rr.IDAsignacionRutaZonaVendedor
        INNER JOIN SVBD7524.digalimenta41.dbo.Persona pv WITH (NOLOCK) ON pv.PKID = ass.IDVendedor
        WHERE dd.Activo = 1 
          AND p.Activo = 1 
          AND pv.Activo = 1
          AND p.Codigo COLLATE Modern_Spanish_CI_AS = @CodigoClienteEspecifico COLLATE Modern_Spanish_CI_AS
        GROUP BY dc.PKID, p.PKID, p.Codigo, p.Nombre, pv.Codigo, pv.Nombre, z.Codigo, z.Descripcion, rr.Codigo, rr.DiaDeSemana;
    END
    ELSE
    BEGIN
        INSERT INTO #TmpCarteraRemoto
        SELECT 
            dc.PKID                                 AS ID_Direccion_Origen,
            p.PKID                                  AS ID_Cliente_Origen,
            LTRIM(RTRIM(p.Codigo)) COLLATE Modern_Spanish_CI_AS AS CodigoCliente,
            LTRIM(RTRIM(p.Nombre)) COLLATE Modern_Spanish_CI_AS AS NombreCliente,
            LTRIM(RTRIM(ISNULL(pv.Codigo, ''))) COLLATE Modern_Spanish_CI_AS AS CodigoVendedor,
            LTRIM(RTRIM(ISNULL(pv.Nombre, ''))) COLLATE Modern_Spanish_CI_AS AS NombreVendedor,
            LTRIM(RTRIM(z.Codigo)) COLLATE Modern_Spanish_CI_AS AS CodigoZonaVenta,
            LTRIM(RTRIM(z.Descripcion)) COLLATE Modern_Spanish_CI_AS AS NombreZonaVenta,
            LTRIM(RTRIM(ISNULL(rr.Codigo, ''))) COLLATE Modern_Spanish_CI_AS AS CodigoRuta,
            CASE 
                WHEN z.Codigo LIKE '%SAB%' OR rr.Codigo LIKE '%SAB%' THEN 6
                WHEN z.Codigo LIKE '%DOM%' OR rr.Codigo LIKE '%DOM%' THEN 7
                WHEN z.Codigo LIKE '%VIE%' OR rr.Codigo LIKE '%VIE%' THEN 5
                WHEN z.Codigo LIKE '%JUE%' OR rr.Codigo LIKE '%JUE%' THEN 4
                WHEN z.Codigo LIKE '%MIE%' OR rr.Codigo LIKE '%MIE%' THEN 3
                WHEN z.Codigo LIKE '%MAR%' OR rr.Codigo LIKE '%MAR%' THEN 2
                WHEN z.Codigo LIKE '%LUN%' OR rr.Codigo LIKE '%LUN%' THEN 1
                WHEN LOWER(LTRIM(RTRIM(rr.DiaDeSemana))) LIKE 's%b%' OR rr.DiaDeSemana = '6' THEN 6
                WHEN LOWER(LTRIM(RTRIM(rr.DiaDeSemana))) LIKE 'dom%' OR rr.DiaDeSemana = '7' THEN 7
                WHEN LOWER(LTRIM(RTRIM(rr.DiaDeSemana))) LIKE 'vie%' OR rr.DiaDeSemana = '5' THEN 5
                WHEN LOWER(LTRIM(RTRIM(rr.DiaDeSemana))) LIKE 'jue%' OR rr.DiaDeSemana = '4' THEN 4
                WHEN LOWER(LTRIM(RTRIM(rr.DiaDeSemana))) LIKE 'mi%'  OR rr.DiaDeSemana = '3' THEN 3
                WHEN LOWER(LTRIM(RTRIM(rr.DiaDeSemana))) LIKE 'mar%' OR rr.DiaDeSemana = '2' THEN 2
                WHEN LOWER(LTRIM(RTRIM(rr.DiaDeSemana))) LIKE 'lun%' OR rr.DiaDeSemana = '1' THEN 1
                ELSE 1
            END                                     AS DiaDeSemana
        FROM SVBD7524.digalimenta41.dbo.DireccionZona dz WITH (NOLOCK)
        INNER JOIN SVBD7524.digalimenta41.dbo.Persona p WITH (NOLOCK) ON dz.IDCliente = p.PKID
        INNER JOIN SVBD7524.digalimenta41.dbo.DireccionCliente dc WITH (NOLOCK) ON dc.PKID = dz.IDDireccion
        INNER JOIN SVBD7524.digalimenta41.dbo.Direccion dd WITH (NOLOCK) ON dd.PKID = dc.PKID
        INNER JOIN SVBD7524.digalimenta41.dbo.Zona z WITH (NOLOCK) ON z.PKID = dz.IDZona
        INNER JOIN SVBD7524.digalimenta41.dbo.ZonaRutaVendedor zrv WITH (NOLOCK) ON zrv.IDZona = z.PKID
        INNER JOIN SVBD7524.digalimenta41.dbo.RutaVendedor rr WITH (NOLOCK) ON rr.PKID = zrv.IDRutaVendedor
        INNER JOIN SVBD7524.digalimenta41.dbo.AsignacionRutaZonaVendedor ass WITH (NOLOCK) ON ass.PKID = rr.IDAsignacionRutaZonaVendedor
        INNER JOIN SVBD7524.digalimenta41.dbo.Persona pv WITH (NOLOCK) ON pv.PKID = ass.IDVendedor
        WHERE dd.Activo = 1 
          AND p.Activo = 1 
          AND pv.Activo = 1
        GROUP BY dc.PKID, p.PKID, p.Codigo, p.Nombre, pv.Codigo, pv.Nombre, z.Codigo, z.Descripcion, rr.Codigo, rr.DiaDeSemana;
    END

    DECLARE @TotalRegistrosOrigen int = @@ROWCOUNT;
    PRINT '>>> Registros extraídos de origen: ' + CAST(@TotalRegistrosOrigen AS varchar(20));

    IF @TotalRegistrosOrigen = 0
    BEGIN
        PRINT '>>> No se encontraron registros para procesar.';
        RETURN;
    END

    -- Índices temporales para acelerar joins de alta concurrencia
    CREATE CLUSTERED INDEX IX_Tmp_CliDir ON #TmpCarteraRemoto (ID_Cliente_Origen, ID_Direccion_Origen);
    CREATE NONCLUSTERED INDEX IX_Tmp_ZonaDiaRuta ON #TmpCarteraRemoto (CodigoZonaVenta, DiaDeSemana, CodigoRuta, CodigoVendedor);
    CREATE NONCLUSTERED INDEX IX_Tmp_CodCli ON #TmpCarteraRemoto (CodigoCliente);

    -- -------------------------------------------------------------
    -- 1. ASEGURAR MAESTRO DE VENDEDORES (dbo.vnd_Vendedor)
    -- -------------------------------------------------------------
    PRINT '>>> [PASO 2/6] Asegurando Vendedores (vnd_Vendedor)...';

    INSERT INTO dbo.vnd_Vendedor (
        Codigo, NombreCompleto, DocumentoIdentidad, 
        Estado, FechaCreacion, FechaModificacion, Eliminado
    )
    SELECT 
        t.CodigoVendedor,
        MAX(CASE WHEN ISNULL(t.NombreVendedor, '') = '' THEN 'VENDEDOR ' + t.CodigoVendedor ELSE t.NombreVendedor END),
        t.CodigoVendedor,
        1,
        GETDATE(),
        GETDATE(),
        0
    FROM #TmpCarteraRemoto t
    WHERE t.CodigoVendedor <> ''
      AND NOT EXISTS (
          SELECT 1 FROM dbo.vnd_Vendedor v WITH (NOLOCK)
          WHERE LTRIM(RTRIM(v.Codigo)) COLLATE Modern_Spanish_CI_AS = t.CodigoVendedor COLLATE Modern_Spanish_CI_AS
      )
    GROUP BY t.CodigoVendedor;

    UPDATE v
    SET v.Estado = 1, v.Eliminado = 0, v.FechaModificacion = GETDATE()
    FROM dbo.vnd_Vendedor v
    INNER JOIN (SELECT DISTINCT CodigoVendedor FROM #TmpCarteraRemoto WHERE CodigoVendedor <> '') t 
        ON LTRIM(RTRIM(v.Codigo)) COLLATE Modern_Spanish_CI_AS = t.CodigoVendedor COLLATE Modern_Spanish_CI_AS
    WHERE v.Estado = 0 OR v.Eliminado = 1;

    -- -------------------------------------------------------------
    -- 2. ASEGURAR MAESTRO DE ZONAS (dbo.cat_Zona)
    -- NOTA: cat_Zona NO tiene columna Eliminado
    -- -------------------------------------------------------------
    PRINT '>>> [PASO 3/6] Asegurando Zonas (cat_Zona)...';

    INSERT INTO dbo.cat_Zona (
        Codigo, Descripcion, EsVenta, EsReparto, 
        Estado, FechaCreacion, FechaModificacion
    )
    SELECT 
        t.CodigoZonaVenta,
        MAX(CASE WHEN ISNULL(t.NombreZonaVenta, '') = '' THEN 'ZONA ' + t.CodigoZonaVenta ELSE t.NombreZonaVenta END),
        1,
        0,
        1,
        GETDATE(),
        GETDATE()
    FROM #TmpCarteraRemoto t
    WHERE t.CodigoZonaVenta <> ''
      AND NOT EXISTS (
          SELECT 1 FROM dbo.cat_Zona z WITH (NOLOCK)
          WHERE LTRIM(RTRIM(z.Codigo)) COLLATE Modern_Spanish_CI_AS = t.CodigoZonaVenta COLLATE Modern_Spanish_CI_AS
      )
    GROUP BY t.CodigoZonaVenta;

    UPDATE z
    SET z.Estado = 1, z.FechaModificacion = GETDATE()
    FROM dbo.cat_Zona z
    INNER JOIN (SELECT DISTINCT CodigoZonaVenta FROM #TmpCarteraRemoto WHERE CodigoZonaVenta <> '') t 
        ON LTRIM(RTRIM(z.Codigo)) COLLATE Modern_Spanish_CI_AS = t.CodigoZonaVenta COLLATE Modern_Spanish_CI_AS
    WHERE z.Estado = 0;

    -- -------------------------------------------------------------
    -- 3. RESOLVER Y VINCULAR RUTAS (dbo.rut_Ruta)
    -- Mapeo único (CodigoRuta, CodigoVendedor) -> IDRuta en BDForceGVD
    -- -------------------------------------------------------------
    PRINT '>>> [PASO 4/6] Asegurando Rutas (rut_Ruta)...';

    IF OBJECT_ID('tempdb..#TmpRutasMapeadas') IS NOT NULL DROP TABLE #TmpRutasMapeadas;
    CREATE TABLE #TmpRutasMapeadas (
        CodigoRuta      varchar(50) COLLATE Modern_Spanish_CI_AS NOT NULL,
        CodigoVendedor  varchar(50) COLLATE Modern_Spanish_CI_AS NOT NULL,
        IDVendedor      int NOT NULL,
        IDRuta          int NULL
    );

    INSERT INTO #TmpRutasMapeadas (CodigoRuta, CodigoVendedor, IDVendedor)
    SELECT 
        t.CodigoRuta,
        t.CodigoVendedor,
        v.ID AS IDVendedor
    FROM (
        SELECT DISTINCT CodigoRuta, CodigoVendedor 
        FROM #TmpCarteraRemoto 
        WHERE CodigoRuta <> '' AND CodigoVendedor <> ''
    ) t
    INNER JOIN dbo.vnd_Vendedor v WITH (NOLOCK)
        ON LTRIM(RTRIM(v.Codigo)) COLLATE Modern_Spanish_CI_AS = t.CodigoVendedor COLLATE Modern_Spanish_CI_AS;

    -- Buscar si ya existe la ruta para ese vendedor exacto
    UPDATE rm
    SET rm.IDRuta = r.ID
    FROM #TmpRutasMapeadas rm
    INNER JOIN dbo.rut_Ruta r WITH (NOLOCK) 
        ON r.IDVendedor = rm.IDVendedor
       AND (
            LTRIM(RTRIM(r.Codigo)) COLLATE Modern_Spanish_CI_AS = rm.CodigoRuta COLLATE Modern_Spanish_CI_AS
            OR LTRIM(RTRIM(r.Codigo)) COLLATE Modern_Spanish_CI_AS = LEFT(rm.CodigoRuta + '-' + rm.CodigoVendedor, 20) COLLATE Modern_Spanish_CI_AS
       );

    -- Para las que no existan, crear o reasignar
    -- A) Si existe la ruta con el mismo código y no está en uso activo por otro vendedor
    UPDATE rm
    SET rm.IDRuta = r.ID
    FROM #TmpRutasMapeadas rm
    INNER JOIN dbo.rut_Ruta r WITH (NOLOCK) 
        ON LTRIM(RTRIM(r.Codigo)) COLLATE Modern_Spanish_CI_AS = rm.CodigoRuta COLLATE Modern_Spanish_CI_AS
    WHERE rm.IDRuta IS NULL
      AND NOT EXISTS (
          SELECT 1 FROM dbo.rut_RutaZonaDia rzd WITH (NOLOCK)
          WHERE rzd.IDRuta = r.ID AND rzd.Estado = 1
      );

    -- Reasignar el IDVendedor de las rutas adoptadas
    UPDATE r
    SET r.IDVendedor = rm.IDVendedor, r.Estado = 1, r.Eliminado = 0, r.FechaModificacion = GETDATE()
    FROM dbo.rut_Ruta r
    INNER JOIN #TmpRutasMapeadas rm ON rm.IDRuta = r.ID AND r.IDVendedor <> rm.IDVendedor;

    -- B) Insertar las rutas totalmente nuevas
    INSERT INTO dbo.rut_Ruta (
        Codigo, Nombre, IDVendedor, Estado, FechaCreacion, FechaModificacion, Eliminado
    )
    SELECT 
        rm.CodigoRuta,
        'RUTA ' + rm.CodigoRuta,
        rm.IDVendedor,
        1,
        GETDATE(),
        GETDATE(),
        0
    FROM #TmpRutasMapeadas rm
    WHERE rm.IDRuta IS NULL
      AND NOT EXISTS (
          SELECT 1 FROM dbo.rut_Ruta r2 WITH (NOLOCK) 
          WHERE LTRIM(RTRIM(r2.Codigo)) COLLATE Modern_Spanish_CI_AS = rm.CodigoRuta COLLATE Modern_Spanish_CI_AS
      );

    -- Actualizar los IDs recién insertados
    UPDATE rm
    SET rm.IDRuta = r.ID
    FROM #TmpRutasMapeadas rm
    INNER JOIN dbo.rut_Ruta r WITH (NOLOCK) 
        ON LTRIM(RTRIM(r.Codigo)) COLLATE Modern_Spanish_CI_AS = rm.CodigoRuta COLLATE Modern_Spanish_CI_AS
       AND r.IDVendedor = rm.IDVendedor
    WHERE rm.IDRuta IS NULL;

    -- C) Para las que colisionan (el código base ya está en uso activo por otro vendedor), usar sufijo
    INSERT INTO dbo.rut_Ruta (
        Codigo, Nombre, IDVendedor, Estado, FechaCreacion, FechaModificacion, Eliminado
    )
    SELECT 
        LEFT(rm.CodigoRuta + '-' + rm.CodigoVendedor, 20),
        'RUTA ' + LEFT(rm.CodigoRuta + '-' + rm.CodigoVendedor, 20),
        rm.IDVendedor,
        1,
        GETDATE(),
        GETDATE(),
        0
    FROM #TmpRutasMapeadas rm
    WHERE rm.IDRuta IS NULL
      AND NOT EXISTS (
          SELECT 1 FROM dbo.rut_Ruta r2 WITH (NOLOCK) 
          WHERE LTRIM(RTRIM(r2.Codigo)) COLLATE Modern_Spanish_CI_AS = LEFT(rm.CodigoRuta + '-' + rm.CodigoVendedor, 20) COLLATE Modern_Spanish_CI_AS
      );

    UPDATE rm
    SET rm.IDRuta = r.ID
    FROM #TmpRutasMapeadas rm
    INNER JOIN dbo.rut_Ruta r WITH (NOLOCK) 
        ON LTRIM(RTRIM(r.Codigo)) COLLATE Modern_Spanish_CI_AS = LEFT(rm.CodigoRuta + '-' + rm.CodigoVendedor, 20) COLLATE Modern_Spanish_CI_AS
       AND r.IDVendedor = rm.IDVendedor
    WHERE rm.IDRuta IS NULL;

    CREATE CLUSTERED INDEX IX_TmpRutasMapeadas ON #TmpRutasMapeadas (CodigoRuta, CodigoVendedor);

    -- -------------------------------------------------------------
    -- 4. SINCRONIZACIÓN SET-BASED DE rut_RutaZonaDia
    -- -------------------------------------------------------------
    PRINT '>>> [PASO 5/6] Sincronizando vínculos Ruta-Zona-Día (rut_RutaZonaDia)...';

    -- Combinaciones únicas vigentes en origen
    IF OBJECT_ID('tempdb..#TmpCombRutaZonaDia') IS NOT NULL DROP TABLE #TmpCombRutaZonaDia;
    SELECT DISTINCT
        z.ID AS IDZona,
        rm.IDRuta,
        t.DiaDeSemana AS IDDia
    INTO #TmpCombRutaZonaDia
    FROM #TmpCarteraRemoto t
    INNER JOIN dbo.cat_Zona z WITH (NOLOCK) 
        ON LTRIM(RTRIM(z.Codigo)) COLLATE Modern_Spanish_CI_AS = t.CodigoZonaVenta COLLATE Modern_Spanish_CI_AS
    INNER JOIN #TmpRutasMapeadas rm 
        ON rm.CodigoRuta COLLATE Modern_Spanish_CI_AS = t.CodigoRuta COLLATE Modern_Spanish_CI_AS
       AND rm.CodigoVendedor COLLATE Modern_Spanish_CI_AS = t.CodigoVendedor COLLATE Modern_Spanish_CI_AS
    WHERE rm.IDRuta IS NOT NULL;

    CREATE CLUSTERED INDEX IX_TmpCombRzd ON #TmpCombRutaZonaDia (IDZona, IDRuta, IDDia);

    -- 4.1 Reactivar / asegurar Estado = 1
    UPDATE rzd
    SET rzd.Estado = 1, rzd.FechaModificacion = GETDATE()
    FROM dbo.rut_RutaZonaDia rzd
    INNER JOIN #TmpCombRutaZonaDia c 
        ON c.IDZona = rzd.IDZona 
       AND c.IDRuta = rzd.IDRuta 
       AND c.IDDia = rzd.IDDia
    WHERE rzd.Estado = 0;

    -- 4.2 Insertar combinaciones nuevas
    INSERT INTO dbo.rut_RutaZonaDia (
        IDZona, IDRuta, IDDia, OrdenVisita, Estado, FechaCreacion, FechaModificacion
    )
    SELECT 
        c.IDZona,
        c.IDRuta,
        c.IDDia,
        1,
        1,
        GETDATE(),
        GETDATE()
    FROM #TmpCombRutaZonaDia c
    WHERE NOT EXISTS (
        SELECT 1 FROM dbo.rut_RutaZonaDia rzd WITH (NOLOCK)
        WHERE rzd.IDZona = c.IDZona AND rzd.IDRuta = c.IDRuta AND rzd.IDDia = c.IDDia
    );

    -- 4.3 DESACTIVAR VÍNCULOS ZOMBI / OBSOLETOS EN rut_RutaZonaDia
    -- (Solo para zonas que gestionamos en el origen)
    IF @CodigoClienteEspecifico IS NULL
    BEGIN
        UPDATE rzd
        SET rzd.Estado = 0, rzd.FechaModificacion = GETDATE()
        FROM dbo.rut_RutaZonaDia rzd
        INNER JOIN dbo.cat_Zona z WITH (NOLOCK) ON z.ID = rzd.IDZona
        WHERE rzd.Estado = 1
          AND z.Codigo COLLATE Modern_Spanish_CI_AS IN (SELECT DISTINCT CodigoZonaVenta FROM #TmpCarteraRemoto)
          AND NOT EXISTS (
              SELECT 1 FROM #TmpCombRutaZonaDia c 
              WHERE c.IDZona = rzd.IDZona AND c.IDRuta = rzd.IDRuta AND c.IDDia = rzd.IDDia
          );
        PRINT '>>> Vínculos zombis desactivados en rut_RutaZonaDia: ' + CAST(@@ROWCOUNT AS varchar(20));
    END

    -- -------------------------------------------------------------
    -- 5. RESOLVER DIRECCIONES DE CLIENTES Y VINCULAR dir_DireccionZona
    -- -------------------------------------------------------------
    PRINT '>>> [PASO 6/6] Sincronizando Direcciones y Zonas (dir_DireccionZona)...';

    IF OBJECT_ID('tempdb..#TmpMapeoDireccionForce') IS NOT NULL DROP TABLE #TmpMapeoDireccionForce;
    CREATE TABLE #TmpMapeoDireccionForce (
        ID_Direccion_Origen int NOT NULL,
        ID_Cliente_Origen   int NOT NULL,
        IDClienteForce      int NOT NULL,
        IDDireccionForce    int NOT NULL
    );

    -- A) Resolver por IDExtra exacto en dir_Direccion
    INSERT INTO #TmpMapeoDireccionForce (ID_Direccion_Origen, ID_Cliente_Origen, IDClienteForce, IDDireccionForce)
    SELECT 
        t.ID_Direccion_Origen,
        t.ID_Cliente_Origen,
        c.ID AS IDClienteForce,
        d.ID AS IDDireccionForce
    FROM (
        SELECT DISTINCT ID_Direccion_Origen, ID_Cliente_Origen, CodigoCliente 
        FROM #TmpCarteraRemoto
    ) t
    INNER JOIN dbo.cli_Cliente c WITH (NOLOCK) 
        ON LTRIM(RTRIM(c.Codigo)) COLLATE Modern_Spanish_CI_AS = t.CodigoCliente COLLATE Modern_Spanish_CI_AS
        OR c.IDExtra = t.ID_Cliente_Origen
    INNER JOIN dbo.dir_Direccion d WITH (NOLOCK) 
        ON d.IDExtra = t.ID_Direccion_Origen;

    -- B) Fallback para direcciones no resueltas por IDExtra directo: buscar por dir_EntidadDireccion
    INSERT INTO #TmpMapeoDireccionForce (ID_Direccion_Origen, ID_Cliente_Origen, IDClienteForce, IDDireccionForce)
    SELECT 
        t.ID_Direccion_Origen,
        t.ID_Cliente_Origen,
        c.ID AS IDClienteForce,
        ed.IDDireccion AS IDDireccionForce
    FROM (
        SELECT DISTINCT ID_Direccion_Origen, ID_Cliente_Origen, CodigoCliente 
        FROM #TmpCarteraRemoto
    ) t
    INNER JOIN dbo.cli_Cliente c WITH (NOLOCK) 
        ON LTRIM(RTRIM(c.Codigo)) COLLATE Modern_Spanish_CI_AS = t.CodigoCliente COLLATE Modern_Spanish_CI_AS
        OR c.IDExtra = t.ID_Cliente_Origen
    CROSS APPLY (
        SELECT TOP 1 edSub.IDDireccion 
        FROM dbo.dir_EntidadDireccion edSub WITH (NOLOCK)
        WHERE edSub.IDEntidad = c.ID AND edSub.TipoEntidad = 'CLIENTE'
        ORDER BY edSub.EsPrincipal DESC, edSub.Estado DESC, edSub.ID DESC
    ) ed
    WHERE NOT EXISTS (
        SELECT 1 FROM #TmpMapeoDireccionForce m 
        WHERE m.ID_Direccion_Origen = t.ID_Direccion_Origen
    );

    CREATE CLUSTERED INDEX IX_TmpMapDir ON #TmpMapeoDireccionForce (ID_Direccion_Origen);

    -- Asegurar estado activo en dir_Direccion y dir_EntidadDireccion
    UPDATE d
    SET d.Estado = 1, d.Eliminado = 0, d.FechaModificacion = GETDATE()
    FROM dbo.dir_Direccion d
    INNER JOIN #TmpMapeoDireccionForce m ON m.IDDireccionForce = d.ID
    WHERE d.Estado = 0 OR d.Eliminado = 1;

    UPDATE ed
    SET ed.Estado = 1, ed.FechaModificacion = GETDATE()
    FROM dbo.dir_EntidadDireccion ed
    INNER JOIN #TmpMapeoDireccionForce m ON m.IDDireccionForce = ed.IDDireccion AND m.IDClienteForce = ed.IDEntidad
    WHERE ed.TipoEntidad = 'CLIENTE' AND ed.Estado = 0;

    -- Mapeo único (IDDireccionForce, IDZona) que debe estar activo en dir_DireccionZona
    IF OBJECT_ID('tempdb..#TmpDireccionZonaVigente') IS NOT NULL DROP TABLE #TmpDireccionZonaVigente;
    SELECT DISTINCT
        m.IDDireccionForce,
        z.ID AS IDZona
    INTO #TmpDireccionZonaVigente
    FROM #TmpCarteraRemoto t
    INNER JOIN #TmpMapeoDireccionForce m ON m.ID_Direccion_Origen = t.ID_Direccion_Origen
    INNER JOIN dbo.cat_Zona z WITH (NOLOCK) 
        ON LTRIM(RTRIM(z.Codigo)) COLLATE Modern_Spanish_CI_AS = t.CodigoZonaVenta COLLATE Modern_Spanish_CI_AS;

    CREATE CLUSTERED INDEX IX_TmpDZV ON #TmpDireccionZonaVigente (IDDireccionForce, IDZona);

    -- 5.1 Reactivar en dir_DireccionZona
    UPDATE dz
    SET dz.Estado = 1, dz.FechaModificacion = GETDATE()
    FROM dbo.dir_DireccionZona dz
    INNER JOIN #TmpDireccionZonaVigente v 
        ON v.IDDireccionForce = dz.IDDireccion 
       AND v.IDZona = dz.IDZona
    WHERE dz.Estado = 0;

    -- 5.2 Insertar nuevas relaciones en dir_DireccionZona
    INSERT INTO dbo.dir_DireccionZona (
        IDDireccion, IDZona, EsPrincipalVenta, EsPrincipalReparto, 
        Estado, FechaCreacion, FechaModificacion
    )
    SELECT 
        v.IDDireccionForce,
        v.IDZona,
        CASE WHEN ROW_NUMBER() OVER (PARTITION BY v.IDDireccionForce ORDER BY v.IDZona ASC) = 1 THEN 1 ELSE 0 END,
        0,
        1,
        GETDATE(),
        GETDATE()
    FROM #TmpDireccionZonaVigente v
    WHERE NOT EXISTS (
        SELECT 1 FROM dbo.dir_DireccionZona dz WITH (NOLOCK)
        WHERE dz.IDDireccion = v.IDDireccionForce AND dz.IDZona = v.IDZona
    );

    -- 5.3 Asegurar que al menos una zona activa tenga EsPrincipalVenta = 1 por dirección
    WITH CTE_SinPrincipal AS (
        SELECT dz.ID, ROW_NUMBER() OVER (PARTITION BY dz.IDDireccion ORDER BY dz.ID ASC) AS Rn
        FROM dbo.dir_DireccionZona dz
        INNER JOIN (SELECT DISTINCT IDDireccionForce FROM #TmpMapeoDireccionForce) m ON m.IDDireccionForce = dz.IDDireccion
        WHERE dz.Estado = 1
          AND NOT EXISTS (
              SELECT 1 FROM dbo.dir_DireccionZona dz2 WITH (NOLOCK)
              WHERE dz2.IDDireccion = dz.IDDireccion AND dz2.Estado = 1 AND dz2.EsPrincipalVenta = 1
          )
    )
    UPDATE dz
    SET dz.EsPrincipalVenta = 1, dz.FechaModificacion = GETDATE()
    FROM dbo.dir_DireccionZona dz
    INNER JOIN CTE_SinPrincipal c ON c.ID = dz.ID
    WHERE c.Rn = 1;

    -- 5.4 Desactivar en dir_DireccionZona las zonas que el cliente ya NO tiene en origen
    UPDATE dz
    SET dz.Estado = 0, dz.EsPrincipalVenta = 0, dz.FechaModificacion = GETDATE()
    FROM dbo.dir_DireccionZona dz
    INNER JOIN (SELECT DISTINCT IDDireccionForce FROM #TmpMapeoDireccionForce) m ON m.IDDireccionForce = dz.IDDireccion
    INNER JOIN dbo.cat_Zona z WITH (NOLOCK) ON z.ID = dz.IDZona
    WHERE dz.Estado = 1
      AND z.Codigo COLLATE Modern_Spanish_CI_AS IN (SELECT DISTINCT CodigoZonaVenta FROM #TmpCarteraRemoto)
      AND NOT EXISTS (
          SELECT 1 FROM #TmpDireccionZonaVigente v 
          WHERE v.IDDireccionForce = dz.IDDireccion AND v.IDZona = dz.IDZona
      );

    DECLARE @DuracionSegundos int = DATEDIFF(SECOND, @HoraInicio, GETDATE());
    PRINT '>>> Sincronización masiva de Cartera y Rutas completada con éxito en ' + CAST(@DuracionSegundos AS varchar(20)) + ' segundos.';
END
GO
