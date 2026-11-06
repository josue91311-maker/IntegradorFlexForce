-- ==========================================================================================
-- ARCHIVO: 04_Vista_Rutas_Zonas_Cartera.sql
-- PROYECTO: IntegradorFlexForce
-- BASE DE DATOS ORIGEN: ERP Antiguo (Solo Lectura)
-- OBJETIVO: Vista de asignación de cliente a su zona, ruta, vendedor y día de semana (Cartera Radar)
-- TABLAS DESTINO EN FORCE: dir_DireccionZona, cat_Zona, rut_Ruta, rut_RutaZonaDia
-- ==========================================================================================

CREATE OR ALTER VIEW dbo.v_Extraccion_Cartera_Rutas_Force
AS
SELECT 
    -- -------------------------------------------------------------
    -- 1. IDENTIFICADORES Y VÍNCULOS
    -- -------------------------------------------------------------
    rz.ID                               AS ID_Asignacion,       -- Clave primaria de asignación en ERP viejo (va a dir_DireccionZona.IDExtra)
    c.ID                                AS ID_Cliente,          -- Clave primaria del cliente (cli_Cliente.IDExtra)
    LTRIM(RTRIM(c.Codigo))              AS CodigoCliente,
    ISNULL(d.ID, c.ID)                  AS ID_Direccion,        -- Clave primaria de la dirección (dir_Direccion.IDExtra)
    
    -- -------------------------------------------------------------
    -- 2. ZONA Y RUTA
    -- -------------------------------------------------------------
    LTRIM(RTRIM(z.Codigo))              AS CodigoZona,          -- cat_Zona.Codigo
    LTRIM(RTRIM(z.Nombre))              AS NombreZona,          -- cat_Zona.Nombre
    LTRIM(RTRIM(r.Codigo))              AS CodigoRuta,          -- rut_Ruta.Codigo
    LTRIM(RTRIM(r.Nombre))              AS NombreRuta,          -- rut_Ruta.Nombre
    
    -- -------------------------------------------------------------
    -- 3. VENDEDOR / PREVENTISTA
    -- -------------------------------------------------------------
    LTRIM(RTRIM(v.Codigo))              AS CodigoVendedor,      -- vnd_Vendedor.Codigo (ej: 605)
    LTRIM(RTRIM(v.NombreCompleto))      AS NombreVendedor,
    
    -- -------------------------------------------------------------
    -- 4. CALENDARIO DE VISITA (CARTERA RADAR)
    -- -------------------------------------------------------------
    -- 1 = Lunes, 2 = Martes, 3 = Miércoles, 4 = Jueves, 5 = Viernes, 6 = Sábado, 7 = Domingo
    ISNULL(rz.IDDia, 1)                 AS DiaDeSemana,         -- rut_RutaZonaDia.IDDia
    ISNULL(rz.Secuencia, 0)             AS SecuenciaVisita,     -- Orden de parada en la ruta
    ISNULL(rz.Estado, 1)                AS Estado,              -- 1 = Asignación Activa, 0 = Quitado de la ruta
    
    -- -------------------------------------------------------------
    -- 5. HUELLA DIGITAL SHA-256 (RowHash)
    -- Se altera si se cambia el vendedor, la ruta, el día de visita o se inactiva la asignación
    -- -------------------------------------------------------------
    HASHBYTES('SHA2_256', 
        CONCAT_WS('|',
            rz.ID,
            c.ID,
            ISNULL(d.ID, c.ID),
            LTRIM(RTRIM(z.Codigo)),
            LTRIM(RTRIM(r.Codigo)),
            LTRIM(RTRIM(v.Codigo)),
            ISNULL(rz.IDDia, 1),
            ISNULL(rz.Secuencia, 0),
            ISNULL(rz.Estado, 1)
        )
    ) AS RowHash

FROM dbo.RutasZonasClienteAntiguo rz WITH (NOLOCK)
INNER JOIN dbo.ClientesAntiguo c WITH (NOLOCK) ON c.ID = rz.IDCliente
LEFT JOIN dbo.DireccionesAntiguo d WITH (NOLOCK) ON d.ID = rz.IDDireccion
LEFT JOIN dbo.ZonasAntiguo z WITH (NOLOCK) ON z.ID = rz.IDZona
LEFT JOIN dbo.RutasAntiguo r WITH (NOLOCK) ON r.ID = rz.IDRuta
LEFT JOIN dbo.VendedoresAntiguo v WITH (NOLOCK) ON v.ID = r.IDVendedor;
GO
