-- ==========================================================================================
-- ARCHIVO: 01_Vista_Extraccion_Clientes_ERP_Antiguo.sql
-- PROYECTO: IntegradorFlexForce (ERP Antiguo -> Force Distribución ERP)
-- BASE DE DATOS ORIGEN: digalimenta41 (ERP Antiguo - Solo Lectura)
-- TABLAS REALES USADAS: dbo.Cliente c, dbo.Persona pp, dbo.Direccion d
-- CARACTERÍSTICA: Incluye RowHash (SHA-256) para sincronización incremental ultra rápida (230k filas)
--                 Detecta cambios en Cliente, Estado (Activo/Desactivado) y Dirección.
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
    3                                           AS TipoPersona,         -- 3 = Persona Natural / Jurídica según catálogo
    
    CASE 
        WHEN LEN(RTRIM(pp.DocIdentidad)) = 11 THEN '6'  -- RUC
        WHEN LEN(RTRIM(pp.DocIdentidad)) = 8  THEN '1'  -- DNI
        ELSE '4'                                        -- Carnet de Extranjería / Otros
    END                                         AS TipoDocIdentidad,
    
    LTRIM(RTRIM(ISNULL(pp.DocIdentidad, '')))   AS DocIdentidad,
    LTRIM(RTRIM(ISNULL(pp.Nombre, '')))         AS NombreCompleto,
    LTRIM(RTRIM(ISNULL(pp.Nombre, '')))         AS NombreComercial,
    LTRIM(RTRIM(ISNULL(pp.PrimerNombre, '')))   AS PrimerNombre,
    LTRIM(RTRIM(ISNULL(pp.SegundoNombre, '')))  AS SegundoNombre,
    LTRIM(RTRIM(ISNULL(pp.ApellidoPaterno, ''))) AS ApellidoPaterno,
    LTRIM(RTRIM(ISNULL(pp.ApellidoMaterno, ''))) AS ApellidoMaterno,
    
    -- -------------------------------------------------------------
    -- 3. CLASIFICACIÓN COMERCIAL (IDs Numéricos del ERP Viejo para Auto-Aprovisionamiento)
    -- -------------------------------------------------------------
    CAST(ISNULL(c.IDCategoriaCliente, 0) AS varchar(20)) AS CodigoCategoriaCliente,
    CAST(ISNULL(c.IDTipoCliente, 0) AS varchar(20))      AS CodigoTipoCliente,
    CAST(ISNULL(c.IDCaracteristica1, 0) AS varchar(20))  AS CodigoTipoNegocio,
    
    -- -------------------------------------------------------------
    -- 4. CONTACTO Y REGLAS DE INTEGRACIÓN FORCE
    -- -------------------------------------------------------------
    NULLIF(LTRIM(RTRIM(pp.Email)), '')          AS Email,
    ''                                          AS Telefono,
    ''                                          AS Contacto,
    ''                                          AS PuestoContacto,
    
    CAST(ISNULL(pp.ExceptuadoDePercepcion, 0) AS bit) AS ExceptuadoPercepcion,
    LTRIM(RTRIM(ISNULL(pp.Observacion, '')))    AS Observacion,
    CAST(ISNULL(pp.Activo, 0) AS bit)           AS EstadoCliente,       -- 1 = Activo, 0 = Desactivado

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
    CAST(0.00000000 AS decimal(10,8))           AS Latitud,
    CAST(0.00000000 AS decimal(11,8))           AS Longitud,
    CAST(ISNULL(d.Activo, 1) AS bit)            AS EstadoDireccion,

    -- -------------------------------------------------------------
    -- 6. HUELLA DIGITAL SHA-256 (RowHash)
    -- Recalculada en tiempo real por SQL Server sobre las 230,000 filas.
    -- Detecta cualquier cambio en: Datos del cliente, activación/desactivación,
    -- dirección física, departamento, provincia, distrito o estado de dirección.
    -- -------------------------------------------------------------
    HASHBYTES('SHA2_256', 
        CONCAT_WS('|',
            c.PKID,
            LTRIM(RTRIM(pp.Codigo)),
            LTRIM(RTRIM(ISNULL(pp.DocIdentidad, ''))),
            LTRIM(RTRIM(ISNULL(pp.Nombre, ''))),
            LTRIM(RTRIM(ISNULL(pp.PrimerNombre, ''))),
            LTRIM(RTRIM(ISNULL(pp.SegundoNombre, ''))),
            LTRIM(RTRIM(ISNULL(pp.ApellidoPaterno, ''))),
            LTRIM(RTRIM(ISNULL(pp.ApellidoMaterno, ''))),
            ISNULL(c.IDCategoriaCliente, 0),
            ISNULL(c.IDTipoCliente, 0),
            ISNULL(c.IDCaracteristica1, 0),
            NULLIF(LTRIM(RTRIM(pp.Email)), ''),
            CAST(ISNULL(pp.ExceptuadoDePercepcion, 0) AS bit),
            CAST(ISNULL(pp.Activo, 0) AS bit),
            LTRIM(RTRIM(ISNULL(pp.Observacion, ''))),
            ISNULL(d.PKID, 0),
            LTRIM(RTRIM(ISNULL(d.Descripcion, ''))),
            ISNULL(d.IDDepartamento, 0),
            ISNULL(d.IDProvincia, 0),
            ISNULL(d.IDDistrito, 0),
            CAST(ISNULL(d.Activo, 1) AS bit)
        )
    ) AS RowHash

FROM dbo.Cliente c WITH (NOLOCK)
INNER JOIN dbo.Persona pp WITH (NOLOCK) 
    ON pp.PKID = c.PKID
LEFT JOIN dbo.Direccion d WITH (NOLOCK) 
    ON d.IDPersona = c.PKID AND d.DireccionPrincipal = 1;
GO
