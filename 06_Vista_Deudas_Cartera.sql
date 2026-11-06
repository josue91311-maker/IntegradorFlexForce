-- ==========================================================================================
-- ARCHIVO: 06_Vista_Deudas_Cartera.sql
-- PROYECTO: IntegradorFlexForce
-- BASE DE DATOS ORIGEN: ERP Antiguo (Solo Lectura)
-- OBJETIVO: Vista de comprobantes y deudas pendientes por cobrar para cartera móvil de cobranza
-- TABLAS DESTINO EN FORCE: cob_DeudaCliente, Cp
-- ==========================================================================================

CREATE OR ALTER VIEW dbo.v_Extraccion_Deuda_Cobranzas_Force
AS
SELECT 
    -- -------------------------------------------------------------
    -- 1. IDENTIFICADORES Y VÍNCULOS
    -- -------------------------------------------------------------
    d.ID                                AS ID_Deuda,            -- Clave de la deuda en ERP viejo (va a cob_DeudaCliente.IDExtra)
    c.ID                                AS ID_Cliente,          -- Clave del cliente (cli_Cliente.IDExtra)
    LTRIM(RTRIM(c.Codigo))              AS CodigoCliente,
    
    -- -------------------------------------------------------------
    -- 2. COMPROBANTE DE VENTA
    -- -------------------------------------------------------------
    -- Tipo: 'FC' (Factura Crédito), 'BC' (Boleta Crédito), 'LC' (Letra)
    CASE 
        WHEN LEFT(d.TipoDocumento, 1) = 'F' THEN 'FC'
        WHEN LEFT(d.TipoDocumento, 1) = 'B' THEN 'BC'
        WHEN d.TipoDocumento LIKE '%LET%'   THEN 'LC'
        ELSE 'FC'
    END                                 AS TipoComprobante,
    
    LTRIM(RTRIM(d.NumeroDocumento))     AS NumeroDocumento,     -- Ej: F001-0023456 (Cp.NumCp)
    d.FechaEmision                      AS FechaEmision,
    d.FechaVencimiento                  AS FechaVencimiento,
    
    -- -------------------------------------------------------------
    -- 3. IMPORTES Y SALDOS
    -- -------------------------------------------------------------
    d.Total                             AS MontoTotal,          -- Importe total facturado
    d.Saldo                             AS MontoSaldo,          -- Saldo pendiente actual
    (d.Total - d.Saldo)                 AS MontoPagado,         -- Total amortizado previamente
    
    -- -------------------------------------------------------------
    -- 4. SEGMENTACIÓN Y ESTADO
    -- -------------------------------------------------------------
    ISNULL(LTRIM(RTRIM(v.Codigo)), 'SIN_VEND') AS CodigoVendedor, -- Vendedor comisionable
    ISNULL(LTRIM(RTRIM(cat.Codigo)), 'GENERAL') AS CodigoCatalogo, -- Portafolio / Catálogo
    
    CASE 
        WHEN d.Saldo <= 0 THEN 'CANCELADO'
        WHEN d.Saldo < d.Total THEN 'PARCIAL'
        ELSE 'PENDIENTE'
    END                                 AS EstadoDeuda,
    
    -- -------------------------------------------------------------
    -- 5. HUELLA DIGITAL SHA-256 (RowHash)
    -- Cada vez que el cliente hace un abono o se cancela la deuda, cambia el saldo y se detecta al vuelo
    -- -------------------------------------------------------------
    HASHBYTES('SHA2_256', 
        CONCAT_WS('|',
            d.ID,
            c.ID,
            LTRIM(RTRIM(d.NumeroDocumento)),
            d.FechaVencimiento,
            d.Total,
            d.Saldo,
            CASE 
                WHEN d.Saldo <= 0 THEN 'CANCELADO'
                WHEN d.Saldo < d.Total THEN 'PARCIAL'
                ELSE 'PENDIENTE'
            END
        )
    ) AS RowHash

FROM dbo.DeudasAntiguo d WITH (NOLOCK)
INNER JOIN dbo.ClientesAntiguo c WITH (NOLOCK) ON c.ID = d.IDCliente
LEFT JOIN dbo.VendedoresAntiguo v WITH (NOLOCK) ON v.ID = d.IDVendedor
LEFT JOIN dbo.CatalogosAntiguo cat WITH (NOLOCK) ON cat.ID = d.IDCatalogo
WHERE d.Saldo > 0; -- Solo deudas con saldo pendiente
GO
