-- ==========================================================================================
-- ARCHIVO: 09_SP_Upsert_Cliente_Credito_Catalogo.sql
-- PROYECTO: IntegradorFlexForce
-- BASE DE DATOS DESTINO: BDForceGVD
-- TABLAS ORIGEN EN ERP ANTIGUO: LineaCreditoPorPortafolioProducto, PortafolioProducto
-- TABLAS DESTINO EN FORCE: ctg_Catalogo, ctg_ClienteCatalogo
-- OBJETIVO: Sincronizar líneas de crédito, días de gracia, montos y balances por catálogo
-- ==========================================================================================

USE [BDForceGVD]
GO

CREATE OR ALTER PROCEDURE dbo.usp_Sync_UpsertClienteCreditoCatalogo
    @ID_LineaCredito_Origen         int,            -- PKID de LineaCreditoPorPortafolioProducto
    @ID_Cliente_Origen              int,            -- IDCliente del ERP Antiguo (va a cli_Cliente.IDExtra)
    @ID_Portafolio_Origen           int,            -- IDPortafolioProducto (va a ctg_Catalogo.IDExtra)
    @CodigoPortafolio               nvarchar(50),   -- Codigo de PortafolioProducto
    @NombrePortafolio               nvarchar(150),  -- Nombre de PortafolioProducto
    @Monto                          decimal(18,4),  -- Monto de línea de crédito aprobada
    @BalanceSoles                   decimal(18,4),  -- Balance en Soles
    @BalanceDolares                 decimal(18,4),  -- Balance en Dólares
    @Saldo                          decimal(18,4),  -- Saldo de crédito
    @DiasGracia                     int,            -- Días de gracia
    @PorcentajeMaximoVencidoCredito decimal(18,4),  -- Porcentaje vencido crédito
    @PorcentajeMaximoVencidoContado decimal(18,4),  -- Porcentaje vencido contado
    @Estado                         bit             -- 1 = Activo, 0 = Inactivo
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRY
        BEGIN TRANSACTION;

        -- 1. Resolver ID de Cliente en BDForceGVD por IDExtra
        DECLARE @IDClienteForce int;
        SELECT TOP 1 @IDClienteForce = ID 
        FROM dbo.cli_Cliente 
        WHERE IDExtra = @ID_Cliente_Origen;

        -- Si no existe aún el cliente en Force, no podemos crear su línea de crédito
        IF @IDClienteForce IS NULL
        BEGIN
            ROLLBACK TRANSACTION;
            RETURN;
        END

        -- 2. Resolver o Auto-Crear Catálogo en ctg_Catalogo
        DECLARE @IDCatalogoForce int;
        SELECT TOP 1 @IDCatalogoForce = ID 
        FROM dbo.ctg_Catalogo 
        WHERE IDExtra = @ID_Portafolio_Origen OR Codigo = @CodigoPortafolio;

        IF @IDCatalogoForce IS NULL
        BEGIN
            INSERT INTO dbo.ctg_Catalogo (
                Codigo, Nombre, Descripcion, Estado, 
                FechaCreacion, FechaModificacion, IDExtra, Eliminado
            )
            VALUES (
                @CodigoPortafolio, 
                ISNULL(@NombrePortafolio, 'PORTAFOLIO ' + @CodigoPortafolio),
                'Portafolio integrado desde ERP Antiguo',
                1, GETDATE(), GETDATE(), @ID_Portafolio_Origen, 0
            );
            SET @IDCatalogoForce = SCOPE_IDENTITY();
        END
        ELSE
        BEGIN
            UPDATE dbo.ctg_Catalogo 
            SET IDExtra = ISNULL(IDExtra, @ID_Portafolio_Origen),
                Nombre = CASE WHEN @NombrePortafolio <> '' THEN @NombrePortafolio ELSE Nombre END,
                Estado = 1,
                Eliminado = 0,
                FechaModificacion = GETDATE()
            WHERE ID = @IDCatalogoForce;
        END

        -- 3. INSERT o UPDATE en ctg_ClienteCatalogo
        DECLARE @IDClienteCatalogo int;
        SELECT TOP 1 @IDClienteCatalogo = ID 
        FROM dbo.ctg_ClienteCatalogo 
        WHERE IDCliente = @IDClienteForce AND IDCatalogo = @IDCatalogoForce;

        IF @IDClienteCatalogo IS NULL
        BEGIN
            SELECT TOP 1 @IDClienteCatalogo = ID 
            FROM dbo.ctg_ClienteCatalogo 
            WHERE IDExtra = @ID_LineaCredito_Origen;
        END

        IF @IDClienteCatalogo IS NULL
        BEGIN
            INSERT INTO dbo.ctg_ClienteCatalogo (
                IDCliente, IDCatalogo, DiasDeGracia,
                MontoCreditoTotal, MontoCreditoUsado, MontoCreditoDisponible,
                Monto, BalanceSoles, BalanceDolares, Saldo,
                PorcentajeMaximoVencidoCredito, PorcentajeMaximoVencidoContado,
                Estado, FechaAsignacion, FechaCreacion, FechaModificacion,
                IDExtra, Eliminado
            )
            VALUES (
                @IDClienteForce, @IDCatalogoForce, @DiasGracia,
                @Monto, 0.00, @Monto,
                @Monto, @BalanceSoles, @BalanceDolares, @Saldo,
                @PorcentajeMaximoVencidoCredito, @PorcentajeMaximoVencidoContado,
                @Estado, GETDATE(), GETDATE(), GETDATE(),
                @ID_LineaCredito_Origen, 0
            );
        END
        ELSE
        BEGIN
            UPDATE dbo.ctg_ClienteCatalogo
            SET IDCliente                     = @IDClienteForce,
                IDCatalogo                    = @IDCatalogoForce,
                DiasDeGracia                  = @DiasGracia,
                MontoCreditoTotal             = @Monto,
                Monto                         = @Monto,
                BalanceSoles                  = @BalanceSoles,
                BalanceDolares                = @BalanceDolares,
                Saldo                         = @Saldo,
                PorcentajeMaximoVencidoCredito = @PorcentajeMaximoVencidoCredito,
                PorcentajeMaximoVencidoContado = @PorcentajeMaximoVencidoContado,
                Estado                        = @Estado,
                Eliminado                     = 0,
                FechaModificacion             = GETDATE(),
                IDExtra                       = ISNULL(IDExtra, @ID_LineaCredito_Origen)
            WHERE ID = @IDClienteCatalogo;
        END

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH
END
GO
