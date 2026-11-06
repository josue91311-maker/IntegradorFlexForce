-- ==========================================================================================
-- ARCHIVO: 05_Vista_Creditos_Catalogos.sql
-- PROYECTO: IntegradorFlexForce
-- BASE DE DATOS ORIGEN: ERP Antiguo (Solo Lectura)
-- OBJETIVO: Vista de línea de crédito aprobada y modalidad de pago por catálogo/portafolio
-- TABLA DESTINO EN FORCE: ctg_ClienteCatalogo, ctg_ModalidadCredito
-- ==========================================================================================

CREATE OR ALTER VIEW dbo.v_Extraccion_Creditos_Catalogo_Force
AS
SELECT 
    -- -------------------------------------------------------------
    -- 1. IDENTIFICADORES Y VÍNCULOS
    -- -------------------------------------------------------------
    cc.ID                               AS ID_ClienteCredito,   -- Clave primaria del crédito en ERP viejo (va a ctg_ClienteCatalogo.IDExtra)
    c.ID                                AS ID_Cliente,          -- Clave primaria del cliente (cli_Cliente.IDExtra)
    LTRIM(RTRIM(c.Codigo))              AS CodigoCliente,
    
    -- -------------------------------------------------------------
    -- 2. CATÁLOGO / PORTAFOLIO DE PRODUCTOS
    -- -------------------------------------------------------------
    ISNULL(LTRIM(RTRIM(cat.Codigo)), 'GENERAL') AS CodigoCatalogo, -- ctg_Catalogo.Codigo
    ISNULL(LTRIM(RTRIM(cat.Nombre)), 'CATALOGO GENERAL') AS NombreCatalogo,
    
    -- -------------------------------------------------------------
    -- 3. CONDICIÓN Y LÍMITE DE CRÉDITO
    -- -------------------------------------------------------------
    ISNULL(LTRIM(RTRIM(mc.Codigo)), 'CRE15')    AS CodigoModalidadCredito, -- ctg_ModalidadCredito.Codigo
    ISNULL(cc.MontoCreditoTotal, 0.00)          AS MontoCreditoTotal,      -- Línea autorizada
    ISNULL(cc.DiasCredito, 15)                  AS DiasCreditoPermitidos,  -- Días de financiamiento
    ISNULL(cc.Estado, 1)                        AS Estado,                 -- 1 = Crédito Activo, 0 = Bloqueado/Suspendido
    
    -- -------------------------------------------------------------
    -- 4. HUELLA DIGITAL SHA-256 (RowHash)
    -- Detecta de inmediato si le aumentan/bajan la línea de crédito o cambian días de pago
    -- -------------------------------------------------------------
    HASHBYTES('SHA2_256', 
        CONCAT_WS('|',
            cc.ID,
            c.ID,
            ISNULL(LTRIM(RTRIM(cat.Codigo)), 'GENERAL'),
            ISNULL(LTRIM(RTRIM(mc.Codigo)), 'CRE15'),
            ISNULL(cc.MontoCreditoTotal, 0.00),
            ISNULL(cc.DiasCredito, 15),
            ISNULL(cc.Estado, 1)
        )
    ) AS RowHash

FROM dbo.CreditosClienteAntiguo cc WITH (NOLOCK)
INNER JOIN dbo.ClientesAntiguo c WITH (NOLOCK) ON c.ID = cc.IDCliente
LEFT JOIN dbo.CatalogosAntiguo cat WITH (NOLOCK) ON cat.ID = cc.IDCatalogo
LEFT JOIN dbo.ModalidadesCreditoAntiguo mc WITH (NOLOCK) ON mc.ID = cc.IDModalidadCredito;
GO
