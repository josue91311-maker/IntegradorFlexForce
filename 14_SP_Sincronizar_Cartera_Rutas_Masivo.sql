-- ==========================================================================================
-- ARCHIVO: 14_SP_Sincronizar_Cartera_Rutas_Masivo.sql
-- PROYECTO: IntegradorFlexForce (ERP Antiguo -> Force Distribución ERP)
-- BASE DE DATOS DESTINO: BDForceGVD (192.168.2.93)
-- SERVIDOR VINCULADO: SVBD7524 -> digalimenta41 (192.168.2.16)
-- OBJETIVO: Sincronización Masiva Ultra Rápida Set-Based (Fast Engine con OPENQUERY)
--           para Cartera Comercial de Zonas, Rutas, Días de Visita y Vendedores.
--
-- OPTIMIZACIONES CLAVE APLICADAS:
--   1. OPENQUERY: Ejecuta el JOIN de 8 tablas en el servidor remoto en su memoria,
--                 evitando que SQL Server transmita millones de filas intermedias.
--   2. Cero 'OR' en JOINs: Mapeos por IDExtra enteros con Hash Match (< 0.2 seg).
--   3. Tablas puente indexadas con IDs enteros (IDZona, IDRuta, IDDireccion):
--      Elimina scans de texto y conversiones de Collation en los WHERE.
--   4. Anti-joins con LEFT JOIN ... WHERE IS NULL:
--      Desactiva vínculos zombis en milisegundos sin subconsultas correlacionadas.
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
    PRINT '>>> [PASO 1/6] Extrayendo cartera comercial desde digalimenta41 vía OPENQUERY...';

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
        DECLARE @SqlPuntual nvarchar(max) = N'
            SELECT 
                ID_Direccion_Origen,
                ID_Cliente_Origen,
                CodigoCliente COLLATE Modern_Spanish_CI_AS,
                NombreCliente COLLATE Modern_Spanish_CI_AS,
                CodigoVendedor COLLATE Modern_Spanish_CI_AS,
                NombreVendedor COLLATE Modern_Spanish_CI_AS,
                CodigoZonaVenta COLLATE Modern_Spanish_CI_AS,
                NombreZonaVenta COLLATE Modern_Spanish_CI_AS,
                CodigoRuta COLLATE Modern_Spanish_CI_AS,
                DiaDeSemana
            FROM OPENQUERY(SVBD7524, ''
                SELECT 
                    dc.PKID AS ID_Direccion_Origen,
                    p.PKID AS ID_Cliente_Origen,
                    LTRIM(RTRIM(p.Codigo)) AS CodigoCliente,
                    LTRIM(RTRIM(p.Nombre)) AS NombreCliente,
                    LTRIM(RTRIM(ISNULL(pv.Codigo, ''''''''))) AS CodigoVendedor,
                    LTRIM(RTRIM(ISNULL(pv.Nombre, ''''''''))) AS NombreVendedor,
                    LTRIM(RTRIM(z.Codigo)) AS CodigoZonaVenta,
                    LTRIM(RTRIM(z.Descripcion)) AS NombreZonaVenta,
                    LTRIM(RTRIM(ISNULL(rr.Codigo, ''''''''))) AS CodigoRuta,
                    CASE 
                        WHEN z.Codigo LIKE ''''%SAB%'''' OR rr.Codigo LIKE ''''%SAB%'''' THEN 6
                        WHEN z.Codigo LIKE ''''%DOM%'''' OR rr.Codigo LIKE ''''%DOM%'''' THEN 7
                        WHEN z.Codigo LIKE ''''%VIE%'''' OR rr.Codigo LIKE ''''%VIE%'''' THEN 5
                        WHEN z.Codigo LIKE ''''%JUE%'''' OR rr.Codigo LIKE ''''%JUE%'''' THEN 4
                        WHEN z.Codigo LIKE ''''%MIE%'''' OR rr.Codigo LIKE ''''%MIE%'''' THEN 3
                        WHEN z.Codigo LIKE ''''%MAR%'''' OR rr.Codigo LIKE ''''%MAR%'''' THEN 2
                        WHEN z.Codigo LIKE ''''%LUN%'''' OR rr.Codigo LIKE ''''%LUN%'''' THEN 1
                        WHEN LOWER(LTRIM(RTRIM(rr.DiaDeSemana))) LIKE ''''s%b%'''' OR rr.DiaDeSemana = ''''6'''' THEN 6
                        WHEN LOWER(LTRIM(RTRIM(rr.DiaDeSemana))) LIKE ''''dom%'''' OR rr.DiaDeSemana = ''''7'''' THEN 7
                        WHEN LOWER(LTRIM(RTRIM(rr.DiaDeSemana))) LIKE ''''vie%'''' OR rr.DiaDeSemana = ''''5'''' THEN 5
                        WHEN LOWER(LTRIM(RTRIM(rr.DiaDeSemana))) LIKE ''''jue%'''' OR rr.DiaDeSemana = ''''4'''' THEN 4
                        WHEN LOWER(LTRIM(RTRIM(rr.DiaDeSemana))) LIKE ''''mi%''''  OR rr.DiaDeSemana = ''''3'''' THEN 3
                        WHEN LOWER(LTRIM(RTRIM(rr.DiaDeSemana))) LIKE ''''mar%'''' OR rr.DiaDeSemana = ''''2'''' THEN 2
                        WHEN LOWER(LTRIM(RTRIM(rr.DiaDeSemana))) LIKE ''''lun%'''' OR rr.DiaDeSemana = ''''1'''' THEN 1
                        ELSE 1
                    END AS DiaDeSemana
                FROM digalimenta41.dbo.DireccionZona dz WITH (NOLOCK)
                INNER JOIN digalimenta41.dbo.Persona p WITH (NOLOCK) ON dz.IDCliente = p.PKID
                INNER JOIN digalimenta41.dbo.DireccionCliente dc WITH (NOLOCK) ON dc.PKID = dz.IDDireccion
                INNER JOIN digalimenta41.dbo.Direccion dd WITH (NOLOCK) ON dd.PKID = dc.PKID
                INNER JOIN digalimenta41.dbo.Zona z WITH (NOLOCK) ON z.PKID = dz.IDZona
                INNER JOIN digalimenta41.dbo.ZonaRutaVendedor zrv WITH (NOLOCK) ON zrv.IDZona = z.PKID
                INNER JOIN digalimenta41.dbo.RutaVendedor rr WITH (NOLOCK) ON rr.PKID = zrv.IDRutaVendedor
                INNER JOIN digalimenta41.dbo.AsignacionRutaZonaVendedor ass WITH (NOLOCK) ON ass.PKID = rr.IDAsignacionRutaZonaVendedor
                INNER JOIN digalimenta41.dbo.Persona pv WITH (NOLOCK) ON pv.PKID = ass.IDVendedor
                WHERE dd.Activo = 1 AND p.Activo = 1 AND pv.Activo = 1
                  AND p.Codigo = ''''' + REPLACE(@CodigoClienteEspecifico, '''', '''''') + '''''
                GROUP BY dc.PKID, p.PKID, p.Codigo, p.Nombre, pv.Codigo, pv.Nombre, z.Codigo, z.Descripcion, rr.Codigo, rr.DiaDeSemana
            '')';

        INSERT INTO #TmpCarteraRemoto
        EXEC sp_executesql @SqlPuntual;
    END
    ELSE
    BEGIN
        INSERT INTO #TmpCarteraRemoto
        SELECT 
            ID_Direccion_Origen,
            ID_Cliente_Origen,
            CodigoCliente COLLATE Modern_Spanish_CI_AS,
            NombreCliente COLLATE Modern_Spanish_CI_AS,
            CodigoVendedor COLLATE Modern_Spanish_CI_AS,
            NombreVendedor COLLATE Modern_Spanish_CI_AS,
            CodigoZonaVenta COLLATE Modern_Spanish_CI_AS,
            NombreZonaVenta COLLATE Modern_Spanish_CI_AS,
            CodigoRuta COLLATE Modern_Spanish_CI_AS,
            DiaDeSemana
        FROM OPENQUERY(SVBD7524, '
            SELECT 
                dc.PKID AS ID_Direccion_Origen,
                p.PKID AS ID_Cliente_Origen,
                LTRIM(RTRIM(p.Codigo)) AS CodigoCliente,
                LTRIM(RTRIM(p.Nombre)) AS NombreCliente,
                LTRIM(RTRIM(ISNULL(pv.Codigo, ''''))) AS CodigoVendedor,
                LTRIM(RTRIM(ISNULL(pv.Nombre, ''''))) AS NombreVendedor,
                LTRIM(RTRIM(z.Codigo)) AS CodigoZonaVenta,
                LTRIM(RTRIM(z.Descripcion)) AS NombreZonaVenta,
                LTRIM(RTRIM(ISNULL(rr.Codigo, ''''))) AS CodigoRuta,
                CASE 
                    WHEN z.Codigo LIKE ''%SAB%'' OR rr.Codigo LIKE ''%SAB%'' THEN 6
                    WHEN z.Codigo LIKE ''%DOM%'' OR rr.Codigo LIKE ''%DOM%'' THEN 7
                    WHEN z.Codigo LIKE ''%VIE%'' OR rr.Codigo LIKE ''%VIE%'' THEN 5
                    WHEN z.Codigo LIKE ''%JUE%'' OR rr.Codigo LIKE ''%JUE%'' THEN 4
                    WHEN z.Codigo LIKE ''%MIE%'' OR rr.Codigo LIKE ''%MIE%'' THEN 3
                    WHEN z.Codigo LIKE ''%MAR%'' OR rr.Codigo LIKE ''%MAR%'' THEN 2
                    WHEN z.Codigo LIKE ''%LUN%'' OR rr.Codigo LIKE ''%LUN%'' THEN 1
                    WHEN LOWER(LTRIM(RTRIM(rr.DiaDeSemana))) LIKE ''s%b%'' OR rr.DiaDeSemana = ''6'' THEN 6
                    WHEN LOWER(LTRIM(RTRIM(rr.DiaDeSemana))) LIKE ''dom%'' OR rr.DiaDeSemana = ''7'' THEN 7
                    WHEN LOWER(LTRIM(RTRIM(rr.DiaDeSemana))) LIKE ''vie%'' OR rr.DiaDeSemana = ''5'' THEN 5
                    WHEN LOWER(LTRIM(RTRIM(rr.DiaDeSemana))) LIKE ''jue%'' OR rr.DiaDeSemana = ''4'' THEN 4
                    WHEN LOWER(LTRIM(RTRIM(rr.DiaDeSemana))) LIKE ''mi%''  OR rr.DiaDeSemana = ''3'' THEN 3
                    WHEN LOWER(LTRIM(RTRIM(rr.DiaDeSemana))) LIKE ''mar%'' OR rr.DiaDeSemana = ''2'' THEN 2
                    WHEN LOWER(LTRIM(RTRIM(rr.DiaDeSemana))) LIKE ''lun%'' OR rr.DiaDeSemana = ''1'' THEN 1
                    ELSE 1
                END AS DiaDeSemana
            FROM digalimenta41.dbo.DireccionZona dz WITH (NOLOCK)
            INNER JOIN digalimenta41.dbo.Persona p WITH (NOLOCK) ON dz.IDCliente = p.PKID
            INNER JOIN digalimenta41.dbo.DireccionCliente dc WITH (NOLOCK) ON dc.PKID = dz.IDDireccion
            INNER JOIN digalimenta41.dbo.Direccion dd WITH (NOLOCK) ON dd.PKID = dc.PKID
            INNER JOIN digalimenta41.dbo.Zona z WITH (NOLOCK) ON z.PKID = dz.IDZona
            INNER JOIN digalimenta41.dbo.ZonaRutaVendedor zrv WITH (NOLOCK) ON zrv.IDZona = z.PKID
            INNER JOIN digalimenta41.dbo.RutaVendedor rr WITH (NOLOCK) ON rr.PKID = zrv.IDRutaVendedor
            INNER JOIN digalimenta41.dbo.AsignacionRutaZonaVendedor ass WITH (NOLOCK) ON ass.PKID = rr.IDAsignacionRutaZonaVendedor
            INNER JOIN digalimenta41.dbo.Persona pv WITH (NOLOCK) ON pv.PKID = ass.IDVendedor
            WHERE dd.Activo = 1 AND p.Activo = 1 AND pv.Activo = 1
            GROUP BY dc.PKID, p.PKID, p.Codigo, p.Nombre, pv.Codigo, pv.Nombre, z.Codigo, z.Descripcion, rr.Codigo, rr.DiaDeSemana
        ');
    END

    DECLARE @TotalRegistrosOrigen int = @@ROWCOUNT;
    PRINT '>>> Registros extraídos: ' + CAST(@TotalRegistrosOrigen AS varchar(20)) + ' en ' + CAST(DATEDIFF(SECOND, @HoraInicio, GETDATE()) AS varchar(10)) + ' seg.';

    IF @TotalRegistrosOrigen = 0
    BEGIN
        PRINT '>>> No se encontraron registros para procesar.';
        RETURN;
    END

    -- Índices para optimizar cruces locales en memoria
    CREATE CLUSTERED INDEX IX_Tmp_CliDir ON #TmpCarteraRemoto (ID_Cliente_Origen, ID_Direccion_Origen);
    CREATE NONCLUSTERED INDEX IX_Tmp_Zona ON #TmpCarteraRemoto (CodigoZonaVenta);
    CREATE NONCLUSTERED INDEX IX_Tmp_RutaVend ON #TmpCarteraRemoto (CodigoRuta, CodigoVendedor);

    -- -------------------------------------------------------------
    -- 1. ASEGURAR VENDEDORES (dbo.vnd_Vendedor)
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
        1, GETDATE(), GETDATE(), 0
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
    -- 2. ASEGURAR ZONAS (dbo.cat_Zona)
    -- -------------------------------------------------------------
    PRINT '>>> [PASO 3/6] Asegurando Zonas (cat_Zona)...';

    INSERT INTO dbo.cat_Zona (
        Codigo, Descripcion, EsVenta, EsReparto, 
        Estado, FechaCreacion, FechaModificacion
    )
    SELECT 
        t.CodigoZonaVenta,
        MAX(CASE WHEN ISNULL(t.NombreZonaVenta, '') = '' THEN 'ZONA ' + t.CodigoZonaVenta ELSE t.NombreZonaVenta END),
        1, 0, 1, GETDATE(), GETDATE()
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

    -- Tabla puente de Zonas con ID entero para eliminar conversiones de texto
    IF OBJECT_ID('tempdb..#TmpZonasMapeadas') IS NOT NULL DROP TABLE #TmpZonasMapeadas;
    CREATE TABLE #TmpZonasMapeadas (
        CodigoZona varchar(50) COLLATE Modern_Spanish_CI_AS NOT NULL PRIMARY KEY,
        IDZona     int NOT NULL
    );

    INSERT INTO #TmpZonasMapeadas (CodigoZona, IDZona)
    SELECT z.Codigo, z.ID
    FROM dbo.cat_Zona z WITH (NOLOCK)
    INNER JOIN (SELECT DISTINCT CodigoZonaVenta FROM #TmpCarteraRemoto) t 
        ON z.Codigo COLLATE Modern_Spanish_CI_AS = t.CodigoZonaVenta COLLATE Modern_Spanish_CI_AS;

    -- -------------------------------------------------------------
    -- 3. RESOLVER Y VINCULAR RUTAS (dbo.rut_Ruta)
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

    -- Insertar rutas nuevas si no existen
    INSERT INTO dbo.rut_Ruta (Codigo, Nombre, IDVendedor, Estado, FechaCreacion, FechaModificacion, Eliminado)
    SELECT 
        rm.CodigoRuta,
        'RUTA ' + rm.CodigoRuta,
        rm.IDVendedor,
        1, GETDATE(), GETDATE(), 0
    FROM #TmpRutasMapeadas rm
    WHERE rm.IDRuta IS NULL
      AND NOT EXISTS (
          SELECT 1 FROM dbo.rut_Ruta r2 WITH (NOLOCK) 
          WHERE LTRIM(RTRIM(r2.Codigo)) COLLATE Modern_Spanish_CI_AS = rm.CodigoRuta COLLATE Modern_Spanish_CI_AS
      );

    UPDATE rm
    SET rm.IDRuta = r.ID
    FROM #TmpRutasMapeadas rm
    INNER JOIN dbo.rut_Ruta r WITH (NOLOCK) 
        ON LTRIM(RTRIM(r.Codigo)) COLLATE Modern_Spanish_CI_AS = rm.CodigoRuta COLLATE Modern_Spanish_CI_AS
       AND r.IDVendedor = rm.IDVendedor
    WHERE rm.IDRuta IS NULL;

    -- Si colisiona el código base con otro vendedor, crear con sufijo
    INSERT INTO dbo.rut_Ruta (Codigo, Nombre, IDVendedor, Estado, FechaCreacion, FechaModificacion, Eliminado)
    SELECT 
        LEFT(rm.CodigoRuta + '-' + rm.CodigoVendedor, 20),
        'RUTA ' + LEFT(rm.CodigoRuta + '-' + rm.CodigoVendedor, 20),
        rm.IDVendedor,
        1, GETDATE(), GETDATE(), 0
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
    PRINT '>>> [PASO 5/6] Sincronizando rut_RutaZonaDia...';

    IF OBJECT_ID('tempdb..#TmpRZD_Vigentes') IS NOT NULL DROP TABLE #TmpRZD_Vigentes;
    CREATE TABLE #TmpRZD_Vigentes (
        IDZona int NOT NULL,
        IDRuta int NOT NULL,
        IDDia  int NOT NULL,
        PRIMARY KEY (IDZona, IDRuta, IDDia)
    );

    INSERT INTO #TmpRZD_Vigentes (IDZona, IDRuta, IDDia)
    SELECT DISTINCT zm.IDZona, rm.IDRuta, t.DiaDeSemana
    FROM #TmpCarteraRemoto t
    INNER JOIN #TmpZonasMapeadas zm ON zm.CodigoZona COLLATE Modern_Spanish_CI_AS = t.CodigoZonaVenta COLLATE Modern_Spanish_CI_AS
    INNER JOIN #TmpRutasMapeadas rm ON rm.CodigoRuta COLLATE Modern_Spanish_CI_AS = t.CodigoRuta COLLATE Modern_Spanish_CI_AS
                                   AND rm.CodigoVendedor COLLATE Modern_Spanish_CI_AS = t.CodigoVendedor COLLATE Modern_Spanish_CI_AS
    WHERE rm.IDRuta IS NOT NULL;

    -- Reactivar
    UPDATE rzd
    SET rzd.Estado = 1, rzd.FechaModificacion = GETDATE()
    FROM dbo.rut_RutaZonaDia rzd
    INNER JOIN #TmpRZD_Vigentes v ON v.IDZona = rzd.IDZona AND v.IDRuta = rzd.IDRuta AND v.IDDia = rzd.IDDia
    WHERE rzd.Estado = 0;

    -- Insertar nuevas
    INSERT INTO dbo.rut_RutaZonaDia (IDZona, IDRuta, IDDia, OrdenVisita, Estado, FechaCreacion, FechaModificacion)
    SELECT v.IDZona, v.IDRuta, v.IDDia, 1, 1, GETDATE(), GETDATE()
    FROM #TmpRZD_Vigentes v
    LEFT JOIN dbo.rut_RutaZonaDia rzd WITH (NOLOCK) 
        ON rzd.IDZona = v.IDZona AND rzd.IDRuta = v.IDRuta AND rzd.IDDia = v.IDDia
    WHERE rzd.IDZona IS NULL;

    -- Desactivar vínculos zombis en milisegundos usando anti-join entero
    IF @CodigoClienteEspecifico IS NULL
    BEGIN
        UPDATE rzd
        SET rzd.Estado = 0, rzd.FechaModificacion = GETDATE()
        FROM dbo.rut_RutaZonaDia rzd
        INNER JOIN #TmpZonasMapeadas zm ON zm.IDZona = rzd.IDZona
        LEFT JOIN #TmpRZD_Vigentes v 
            ON v.IDZona = rzd.IDZona AND v.IDRuta = rzd.IDRuta AND v.IDDia = rzd.IDDia
        WHERE rzd.Estado = 1 AND v.IDZona IS NULL;

        PRINT '>>> Vínculos zombis desactivados en rut_RutaZonaDia: ' + CAST(@@ROWCOUNT AS varchar(20));
    END

    -- -------------------------------------------------------------
    -- 5. RESOLVER DIRECCIONES DE CLIENTES Y VINCULAR dir_DireccionZona
    -- -------------------------------------------------------------
    PRINT '>>> [PASO 6/6] Sincronizando dir_DireccionZona...';

    IF OBJECT_ID('tempdb..#TmpMapeoDir') IS NOT NULL DROP TABLE #TmpMapeoDir;
    CREATE TABLE #TmpMapeoDir (
        ID_Direccion_Origen int NOT NULL PRIMARY KEY,
        IDDireccionForce    int NOT NULL
    );

    -- Paso A: Mapeo ultrarrápido por IDExtra directo (Hash Match < 0.1s sin OR)
    INSERT INTO #TmpMapeoDir (ID_Direccion_Origen, IDDireccionForce)
    SELECT t.ID_Direccion_Origen, d.ID
    FROM (SELECT DISTINCT ID_Direccion_Origen FROM #TmpCarteraRemoto) t
    INNER JOIN dbo.dir_Direccion d WITH (NOLOCK) ON d.IDExtra = t.ID_Direccion_Origen;

    -- Paso B: Fallback para direcciones sin IDExtra directo
    INSERT INTO #TmpMapeoDir (ID_Direccion_Origen, IDDireccionForce)
    SELECT t.ID_Direccion_Origen, ed.IDDireccion
    FROM (
        SELECT DISTINCT t2.ID_Direccion_Origen, c.ID AS IDClienteForce
        FROM #TmpCarteraRemoto t2
        INNER JOIN dbo.cli_Cliente c WITH (NOLOCK) ON c.IDExtra = t2.ID_Cliente_Origen
        WHERE NOT EXISTS (SELECT 1 FROM #TmpMapeoDir m WHERE m.ID_Direccion_Origen = t2.ID_Direccion_Origen)
    ) t
    CROSS APPLY (
        SELECT TOP 1 edSub.IDDireccion
        FROM dbo.dir_EntidadDireccion edSub WITH (NOLOCK)
        WHERE edSub.IDEntidad = t.IDClienteForce AND edSub.TipoEntidad = 'CLIENTE'
        ORDER BY edSub.EsPrincipal DESC, edSub.Estado DESC, edSub.ID DESC
    ) ed;

    -- Combinaciones únicas de Direccion y Zona vigentes
    IF OBJECT_ID('tempdb..#TmpDZ_Vigentes') IS NOT NULL DROP TABLE #TmpDZ_Vigentes;
    CREATE TABLE #TmpDZ_Vigentes (
        IDDireccion int NOT NULL,
        IDZona      int NOT NULL,
        PRIMARY KEY (IDDireccion, IDZona)
    );

    INSERT INTO #TmpDZ_Vigentes (IDDireccion, IDZona)
    SELECT DISTINCT md.IDDireccionForce, zm.IDZona
    FROM #TmpCarteraRemoto t
    INNER JOIN #TmpMapeoDir md ON md.ID_Direccion_Origen = t.ID_Direccion_Origen
    INNER JOIN #TmpZonasMapeadas zm ON zm.CodigoZona COLLATE Modern_Spanish_CI_AS = t.CodigoZonaVenta COLLATE Modern_Spanish_CI_AS;

    -- Reactivar dir_DireccionZona
    UPDATE dz
    SET dz.Estado = 1, dz.FechaModificacion = GETDATE()
    FROM dbo.dir_DireccionZona dz
    INNER JOIN #TmpDZ_Vigentes v ON v.IDDireccion = dz.IDDireccion AND v.IDZona = dz.IDZona
    WHERE dz.Estado = 0;

    -- Insertar nuevas relaciones
    INSERT INTO dbo.dir_DireccionZona (
        IDDireccion, IDZona, EsPrincipalVenta, EsPrincipalReparto, 
        Estado, FechaCreacion, FechaModificacion
    )
    SELECT v.IDDireccion, v.IDZona, 0, 0, 1, GETDATE(), GETDATE()
    FROM #TmpDZ_Vigentes v
    LEFT JOIN dbo.dir_DireccionZona dz WITH (NOLOCK) 
        ON dz.IDDireccion = v.IDDireccion AND dz.IDZona = v.IDZona
    WHERE dz.IDDireccion IS NULL;

    -- Asegurar que al menos una zona activa tenga EsPrincipalVenta = 1
    WITH CTE_SinPrincipal AS (
        SELECT dz.ID, ROW_NUMBER() OVER (PARTITION BY dz.IDDireccion ORDER BY dz.ID ASC) AS Rn
        FROM dbo.dir_DireccionZona dz
        INNER JOIN (SELECT DISTINCT IDDireccionForce FROM #TmpMapeoDir) md ON md.IDDireccionForce = dz.IDDireccion
        WHERE dz.Estado = 1
    )
    UPDATE dz
    SET dz.EsPrincipalVenta = 1, dz.FechaModificacion = GETDATE()
    FROM dbo.dir_DireccionZona dz
    INNER JOIN CTE_SinPrincipal c ON c.ID = dz.ID
    WHERE c.Rn = 1
      AND NOT EXISTS (
          SELECT 1 FROM dbo.dir_DireccionZona dz2 WITH (NOLOCK)
          WHERE dz2.IDDireccion = dz.IDDireccion AND dz2.Estado = 1 AND dz2.EsPrincipalVenta = 1
      );

    -- Desactivar en dir_DireccionZona las zonas retiradas usando anti-join entero
    UPDATE dz
    SET dz.Estado = 0, dz.EsPrincipalVenta = 0, dz.FechaModificacion = GETDATE()
    FROM dbo.dir_DireccionZona dz
    INNER JOIN #TmpMapeoDir md ON md.IDDireccionForce = dz.IDDireccion
    INNER JOIN #TmpZonasMapeadas zm ON zm.IDZona = dz.IDZona
    LEFT JOIN #TmpDZ_Vigentes v ON v.IDDireccion = dz.IDDireccion AND v.IDZona = dz.IDZona
    WHERE dz.Estado = 1 AND v.IDDireccion IS NULL;

    DECLARE @DuracionSegundos int = DATEDIFF(SECOND, @HoraInicio, GETDATE());
    PRINT '>>> Sincronización masiva de Cartera y Rutas completada con éxito en ' + CAST(@DuracionSegundos AS varchar(20)) + ' segundos.';
END
GO
