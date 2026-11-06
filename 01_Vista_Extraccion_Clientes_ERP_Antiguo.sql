-- ==========================================================================================
-- ARCHIVO: 01_Vista_Extraccion_Clientes_ERP_Antiguo.sql
-- PROYECTO: IntegradorFlexForce (ERP Antiguo -> Force Distribución ERP)
-- BASE DE DATOS ORIGEN: ERP Antiguo (Solo Lectura)
-- TABLAS REALES USADAS: dbo.Cliente c, dbo.Persona pp, dbo.Direccion d
-- CARACTERÍSTICA: Incluye RowHash (SHA-256) para sincronización incremental ultra rápida (230k filas)
--                 SIN dependencia de CodigoRutaVenta / pp.Web
-- ==========================================================================================

CREATE OR ALTER VIEW dbo.v_Extraccion_Clientes_Force
AS
SELECT 
    -- -------------------------------------------------------------
    -- 1. IDENTIFICADORES Y CLAVES (cli_Cliente.IDExtra y Codigo)
    -- -------------------------------------------------------------
    c.PKID                                      AS ID_Cliente,          -- Clave primaria del ERP viejo (va a cli_Cliente.IDExtra)
    LTRIM(RTRIM(pp.Codigo))                     AS CodigoCliente,       -- Código visible del cliente en Force
    
    -- -------------------------------------------------------------
    -- 2. CLASIFICACIÓN FISCAL Y NOMBRES
    -- -------------------------------------------------------------
    3                                           AS TipoPersona,         -- 3 = Persona Natural (o 'NAT' / 'JUR' según catálogo)
    
    CASE 
        WHEN LEN(RTRIM(pp.DocIdentidad)) = 11 THEN '6'  -- RUC
        WHEN LEN(RTRIM(pp.DocIdentidad)) = 8  THEN '1'  -- DNI
        ELSE '4'                                        -- Carnet de Extranjería
    END                                         AS TipoDocIdentidad,
    
    LTRIM(RTRIM(pp.DocIdentidad))               AS DocIdentidad,
    LTRIM(RTRIM(pp.Nombre))                     AS NombreCompleto,
    LTRIM(RTRIM(pp.Nombre))                     AS NombreComercial,
    LTRIM(RTRIM(ISNULL(pp.PrimerNombre, '')))   AS PrimerNombre,
    LTRIM(RTRIM(ISNULL(pp.SegundoNombre, '')))  AS SegundoNombre,
    LTRIM(RTRIM(ISNULL(pp.ApellidoPaterno, ''))) AS ApellidoPaterno,
    LTRIM(RTRIM(ISNULL(pp.ApellidoMaterno, ''))) AS ApellidoMaterno,
    
    -- -------------------------------------------------------------
    -- 3. CLASIFICACIÓN COMERCIAL (IDs Numéricos del ERP Viejo para Auto-Aprovisionamiento)
    -- -------------------------------------------------------------
    c.IDCategoriaCliente                        AS CodigoCategoriaCliente, -- Ej: 7, 11 (se auto-crea en cat_CategoriaCliente si no existe)
    c.IDTipoCliente                             AS CodigoTipoCliente,      -- Ej: 65, 64, 12 (se auto-crea en cat_TipoCliente si no existe)
    ISNULL(c.IDCaracteristica1, 0)              AS CodigoTipoNegocio,      -- Ej: 600004635 (cat_TipoNegocio)
    
    -- -------------------------------------------------------------
    -- 4. CONTACTO Y REGLAS DE INTEGRACIÓN FORCE
    -- -------------------------------------------------------------
    -- REGLA FORCE: Si está vacío debe enviarse NULL (jamás '') para no romper el check CHK_Cliente_Email
    NULLIF(LTRIM(RTRIM(pp.Email)), '')          AS Email,
    ''                                          AS Telefono,
    ''                                          AS Contacto,
    ''                                          AS PuestoContacto,
    
    ISNULL(pp.ExceptuadoDePercepcion, 0)        AS ExceptuadoPercepcion,
    LTRIM(RTRIM(ISNULL(pp.Observacion, '')))    AS Observacion,
    CAST(pp.Activo AS bit)                      AS EstadoCliente,

    -- -------------------------------------------------------------
    -- 5. DIRECCIÓN PRINCIPAL (dir_Direccion / dir_EntidadDireccion)
    -- -------------------------------------------------------------
    ISNULL(d.PKID, c.PKID)                      AS ID_Direccion,        -- Clave de dirección ERP viejo (va a dir_Direccion.IDExtra)
    CAST(ISNULL(d.DireccionPrincipal, 1) AS bit) AS EsPrincipal,        -- 1 = Dirección principal activa
    'FIS'                                       AS CodigoTipoDireccion, -- FIS = Fiscal / ENT = Entrega
    LTRIM(RTRIM(ISNULL(d.Descripcion, 'SIN DIRECCION'))) AS Direccion,
    ''                                          AS Referencia,
    
    -- Ubigeo y Departamentos
    RIGHT('0' + CAST(ISNULL(d.IDDepartamento, 15) AS varchar(2)), 2) AS CodigoDepartamento,
    CAST(ISNULL(d.IDProvincia, 0) AS varchar(10)) AS CodigoProvincia,
    CAST(ISNULL(d.IDDistrito, 0) AS varchar(10))  AS CodigoDistrito,
    '150101'                                    AS Ubigeo,
    0.00000000                                  AS Latitud,
    0.00000000                                  AS Longitud,
    CAST(ISNULL(d.Activo, 1) AS bit)            AS EstadoDireccion,

    -- -------------------------------------------------------------
    -- 6. HUELLA DIGITAL SHA-256 (RowHash)
    -- Recalculada en tiempo real por SQL Server sobre las 230,000 filas.
    -- Detecta cambios de nombre, dirección, categoría, tipo, RUC/DNI o estado.
    -- (NO incluye pp.Web por indicación expresa).
    -- -------------------------------------------------------------
    HASHBYTES('SHA2_256', 
        CONCAT_WS('|',
            c.PKID,
            LTRIM(RTRIM(pp.Codigo)),
            LTRIM(RTRIM(pp.DocIdentidad)),
            LTRIM(RTRIM(pp.Nombre)),
            LTRIM(RTRIM(ISNULL(pp.PrimerNombre, ''))),
            LTRIM(RTRIM(ISNULL(pp.ApellidoPaterno, ''))),
            LTRIM(RTRIM(ISNULL(pp.ApellidoMaterno, ''))),
            c.IDCategoriaCliente,
            c.IDTipoCliente,
            NULLIF(LTRIM(RTRIM(pp.Email)), ''),
            CAST(pp.Activo AS bit),
            LTRIM(RTRIM(ISNULL(pp.Observacion, ''))),
            LTRIM(RTRIM(ISNULL(d.Descripcion, ''))),
            CAST(ISNULL(d.Activo, 1) AS bit)
        )
    ) AS RowHash

FROM dbo.Cliente c WITH (NOLOCK)
INNER JOIN dbo.Persona pp WITH (NOLOCK) 
    ON pp.PKID = c.PKID
LEFT JOIN dbo.Direccion d WITH (NOLOCK) 
    ON d.IDPersona = c.PKID AND d.DireccionPrincipal = 1;
GO
