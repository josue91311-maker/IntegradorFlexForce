-- ==========================================================================================
-- ARCHIVO: 04_Vista_Rutas_Zonas_Cartera.sql
-- PROYECTO: IntegradorFlexForce
-- BASE DE DATOS ORIGEN: digalimenta41 (ERP Antiguo - Solo Lectura)
-- TABLAS REALES USADAS: DireccionZona, Persona, DireccionCliente, Direccion, Zona,
--                       ZonaRutaVendedor, RutaVendedor, AsignacionRutaZonaVendedor,
--                       Vendedor, GrupoVentas, FuerzaVentas
-- OBJETIVO: Extraer la cartera de zonas, rutas y vendedores asignados a cada dirección
--           de cliente con huella digital SHA-256 (RowHash) para detectar cambios.
-- ==========================================================================================

CREATE OR ALTER VIEW dbo.vw_Sync_Cartera_Rutas_Zonas
AS
WITH CTE_Cartera AS (
    SELECT 
        dc.PKID                                 AS ID_Direccion,
        p.PKID                                  AS ID_Cliente,
        LTRIM(RTRIM(p.Codigo))                  AS CodigoCliente,
        LTRIM(RTRIM(p.Nombre))                  AS NombreCliente,
        LTRIM(RTRIM(pv.Codigo))                 AS CodigoVendedor,
        LTRIM(RTRIM(pv.Nombre))                 AS NombreVendedor,
        z.PKID                                  AS IDZonaVenta,
        LTRIM(RTRIM(z.Codigo))                  AS CodigoZonaVenta,
        LTRIM(RTRIM(z.Descripcion))             AS NombreZonaVenta,
        zr.PKID                                 AS IDZonaReparto,
        LTRIM(RTRIM(zr.Codigo))                 AS CodigoZonaReparto,
        LTRIM(RTRIM(rr.Codigo))                 AS CodigoRuta,
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
        END                                     AS DiaDeSemana,
        ROW_NUMBER() OVER (
            PARTITION BY dc.PKID, p.PKID, z.Codigo
            ORDER BY ISNULL(ass.Activo, 1) DESC, ISNULL(pv.Activo, 1) DESC, ISNULL(z.Activo, 1) DESC, rr.PKID DESC
        )                                       AS Rn
    FROM dbo.DireccionZona dz WITH (NOLOCK)
    INNER JOIN dbo.Persona p WITH (NOLOCK) ON dz.IDCliente = p.PKID
    INNER JOIN dbo.DireccionCliente dc WITH (NOLOCK) ON dc.PKID = dz.IDDireccion
    INNER JOIN dbo.Direccion dd WITH (NOLOCK) ON dd.PKID = dc.PKID
    INNER JOIN dbo.Zona z WITH (NOLOCK) ON z.PKID = dz.IDZona
    LEFT JOIN dbo.Zona zr WITH (NOLOCK) ON zr.PKID = dc.IDZonaReparto
    INNER JOIN dbo.ZonaRutaVendedor zrv WITH (NOLOCK) ON zrv.IDZona = z.PKID
    INNER JOIN dbo.RutaVendedor rr WITH (NOLOCK) ON rr.PKID = zrv.IDRutaVendedor
    INNER JOIN dbo.AsignacionRutaZonaVendedor ass WITH (NOLOCK) ON ass.PKID = rr.IDAsignacionRutaZonaVendedor
    INNER JOIN dbo.Persona pv WITH (NOLOCK) ON pv.PKID = ass.IDVendedor
    LEFT JOIN dbo.Vendedor v WITH (NOLOCK) ON v.PKID = pv.PKID
    LEFT JOIN dbo.GrupoVentas gv WITH (NOLOCK) ON gv.PKID = v.IDGrupoVentas
    LEFT JOIN dbo.FuerzaVentas fv WITH (NOLOCK) ON fv.PKID = gv.IDFuerzaVentas
    WHERE dd.Activo = 1 
      AND p.Activo = 1
)
SELECT 
    ID_Direccion,
    ID_Cliente,
    CodigoCliente,
    NombreCliente,
    CodigoVendedor,
    NombreVendedor,
    IDZonaVenta,
    CodigoZonaVenta,
    NombreZonaVenta,
    IDZonaReparto,
    CodigoZonaReparto,
    CodigoRuta,
    DiaDeSemana,
    HASHBYTES('SHA2_256',
        CONCAT_WS('|',
            ID_Direccion,
            ID_Cliente,
            CodigoVendedor,
            IDZonaVenta,
            CodigoZonaVenta,
            IDZonaReparto,
            CodigoZonaReparto,
            CodigoRuta,
            DiaDeSemana
        )
    ) AS RowHash
FROM CTE_Cartera
WHERE Rn = 1;
GO
