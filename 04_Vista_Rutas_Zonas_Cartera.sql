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
        MAX(LTRIM(RTRIM(pv.Codigo)))            AS CodigoVendedor,
        MAX(LTRIM(RTRIM(pv.Nombre)))            AS NombreVendedor,
        z.PKID                                  AS IDZonaVenta,
        LTRIM(RTRIM(z.Codigo))                  AS CodigoZonaVenta,
        LTRIM(RTRIM(z.Descripcion))             AS NombreZonaVenta,
        zr.PKID                                 AS IDZonaReparto,
        LTRIM(RTRIM(zr.Codigo))                 AS CodigoZonaReparto,
        MAX(LTRIM(RTRIM(rr.Codigo)))            AS CodigoRuta,
        MAX(
            CASE LOWER(LTRIM(RTRIM(rr.DiaDeSemana)))
                WHEN 'lunes'     THEN 1
                WHEN 'martes'    THEN 2
                WHEN 'miercoles' THEN 3
                WHEN 'miércoles' THEN 3
                WHEN 'jueves'    THEN 4
                WHEN 'viernes'   THEN 5
                WHEN 'sabado'    THEN 6
                WHEN 'sábado'    THEN 6
                WHEN 'domingo'   THEN 7
                ELSE 1
            END
        )                                       AS DiaDeSemana
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
    GROUP BY 
        dc.PKID, p.PKID, p.Codigo, p.Nombre,
        z.PKID, z.Codigo, z.Descripcion, zr.PKID, zr.Codigo
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
FROM CTE_Cartera;
GO
