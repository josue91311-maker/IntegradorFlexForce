-- ==========================================================================================
-- ARCHIVO: 07_SP_Vincular_Cliente_Ruta.sql
-- PROYECTO: IntegradorFlexForce
-- BASE DE DATOS: BDForceGVD
-- OBJETIVO: Vincula el cliente con su Vendedor, Ruta, Zona y Día de Visita en Force Distribución
--           respetando los esquemas exactos de:
--           - vnd_Vendedor     (Codigo, NombreCompleto, DocumentoIdentidad, Estado, Eliminado)
--           - cat_Zona         (Codigo, Descripcion, EsVenta, EsReparto, Estado) [SIN Eliminado]
--           - rut_Ruta         (Codigo, Nombre, IDVendedor, Estado, Eliminado) [Usa 'Nombre', NO 'Descripcion']
--           - rut_RutaZonaDia  (IDZona, IDRuta, IDDia, OrdenVisita, Estado) [Usa 'OrdenVisita', NO 'Secuencia']
--           - dir_DireccionZona(IDDireccion, IDZona, EsPrincipalVenta, EsPrincipalReparto, Estado)
-- ==========================================================================================

USE [BDForceGVD]
GO

CREATE OR ALTER PROCEDURE dbo.usp_Sync_VincularCarteraRuta
    @ID_Cliente_Origen          int,            -- p.PKID del ERP viejo
    @ID_Direccion_Origen        int,            -- dc.PKID / dd.PKID del ERP viejo
    @CodigoZonaVenta            nvarchar(50),   -- z.Codigo (ej: 0101LJA)
    @NombreZonaVenta            nvarchar(150),  -- z.Descripcion
    @CodigoZonaReparto          nvarchar(50) = NULL, -- zr.Codigo
    @CodigoRuta                 nvarchar(20),   -- rr.Codigo (ej: 01A012)
    @CodigoVendedor             nvarchar(50),   -- pv.Codigo (ej: 605)
    @NombreVendedor             nvarchar(200) = '',
    @DiaDeSemana                int = 1,        -- 1=Lunes .. 7=Domingo (FK rut_DiaSemana)
    @OrdenVisita                int = 0,        -- Orden de visita / parada
    @Estado                     bit = 1
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRY
        BEGIN TRANSACTION;

        -- -------------------------------------------------------------
        -- 1. OBTENER ID CLIENTE E ID DIRECCION EN FORCE POR SUS IDExtra
        -- -------------------------------------------------------------
        DECLARE @IDCliente int, @IDDireccion int;
        SELECT TOP 1 @IDCliente = ID FROM cli_Cliente WHERE IDExtra = @ID_Cliente_Origen;
        SELECT TOP 1 @IDDireccion = ID FROM dir_Direccion WHERE IDExtra = @ID_Direccion_Origen;

        -- Si no se encuentra la dirección por IDExtra, buscar cualquier dirección vinculada al cliente
        IF @IDDireccion IS NULL AND @IDCliente IS NOT NULL
        BEGIN
            SELECT TOP 1 @IDDireccion = IDDireccion 
            FROM dir_EntidadDireccion 
            WHERE IDEntidad = @IDCliente AND TipoEntidad = 'CLIENTE'
            ORDER BY EsPrincipal DESC, Estado DESC, ID DESC;
        END

        -- Si el cliente existe pero aún no tenía ninguna dirección creada en Force, crearla
        IF @IDDireccion IS NULL AND @IDCliente IS NOT NULL
        BEGIN
            DECLARE @IDTipoDirDefault int;
            SELECT TOP 1 @IDTipoDirDefault = ID FROM cat_TipoDireccion WHERE Estado = 1;

            INSERT INTO dir_Direccion (
                IDTipoDireccion, Direccion, Referencia, Ubigeo,
                IDPais, Estado, FechaCreacion, FechaModificacion, Eliminado, IDExtra
            )
            VALUES (
                ISNULL(@IDTipoDirDefault, 1), 'DIRECCION PRINCIPAL RUTA', '', '150101',
                1, 1, GETDATE(), GETDATE(), 0, @ID_Direccion_Origen
            );
            SET @IDDireccion = SCOPE_IDENTITY();
        END

        IF @IDCliente IS NULL OR @IDDireccion IS NULL
        BEGIN
            ROLLBACK TRANSACTION;
            RETURN;
        END

        -- Garantizar que la dirección no esté marcada como eliminada y que exista el vínculo activo en dir_EntidadDireccion
        UPDATE dir_Direccion 
        SET Estado = 1, Eliminado = 0, FechaModificacion = GETDATE()
        WHERE ID = @IDDireccion AND (Estado = 0 OR Eliminado = 1);

        IF NOT EXISTS (SELECT 1 FROM dir_EntidadDireccion WHERE TipoEntidad = 'CLIENTE' AND IDEntidad = @IDCliente AND IDDireccion = @IDDireccion)
        BEGIN
            DECLARE @EsPrinc bit = CASE WHEN EXISTS (SELECT 1 FROM dir_EntidadDireccion WHERE TipoEntidad = 'CLIENTE' AND IDEntidad = @IDCliente AND EsPrincipal = 1) THEN 0 ELSE 1 END;
            INSERT INTO dir_EntidadDireccion (
                TipoEntidad, IDEntidad, IDDireccion, EsPrincipal, Estado,
                FechaCreacion, FechaModificacion, IDExtra
            )
            VALUES (
                'CLIENTE', @IDCliente, @IDDireccion, @EsPrinc, 1,
                GETDATE(), GETDATE(), @ID_Direccion_Origen
            );
        END
        ELSE
        BEGIN
            UPDATE dir_EntidadDireccion
            SET Estado = 1, FechaModificacion = GETDATE()
            WHERE TipoEntidad = 'CLIENTE' AND IDEntidad = @IDCliente AND IDDireccion = @IDDireccion AND Estado = 0;
        END

        -- -------------------------------------------------------------
        -- 2. RESOLVER O CREAR VENDEDOR (vnd_Vendedor)
        -- NOTA: DocumentoIdentidad es NOT NULL en vnd_Vendedor
        -- -------------------------------------------------------------
        DECLARE @IDVendedor int;
        SELECT TOP 1 @IDVendedor = ID FROM vnd_Vendedor WHERE Codigo = @CodigoVendedor;

        IF @IDVendedor IS NULL AND @CodigoVendedor IS NOT NULL AND LTRIM(RTRIM(@CodigoVendedor)) <> ''
        BEGIN
            INSERT INTO vnd_Vendedor (
                Codigo, NombreCompleto, DocumentoIdentidad, 
                Estado, FechaCreacion, FechaModificacion, Eliminado
            )
            VALUES (
                @CodigoVendedor, 
                CASE WHEN LTRIM(RTRIM(@NombreVendedor)) = '' THEN 'VENDEDOR ' + @CodigoVendedor ELSE @NombreVendedor END,
                @CodigoVendedor,
                1, GETDATE(), GETDATE(), 0
            );
            SET @IDVendedor = SCOPE_IDENTITY();
        END
        ELSE IF @IDVendedor IS NOT NULL
        BEGIN
            UPDATE vnd_Vendedor
            SET Estado = 1, Eliminado = 0, FechaModificacion = GETDATE()
            WHERE ID = @IDVendedor AND (Estado = 0 OR Eliminado = 1);
        END

        -- Si aún no hay vendedor, asignamos el primer vendedor activo por defecto
        IF @IDVendedor IS NULL
        BEGIN
            SELECT TOP 1 @IDVendedor = ID FROM vnd_Vendedor WHERE Estado = 1 AND Eliminado = 0;
        END

        -- -------------------------------------------------------------
        -- 3. RESOLVER O CREAR ZONA (cat_Zona)
        -- NOTA: cat_Zona NO tiene columna 'Eliminado'
        -- -------------------------------------------------------------
        DECLARE @IDZona int;
        SELECT TOP 1 @IDZona = ID FROM cat_Zona WHERE Codigo = @CodigoZonaVenta;

        IF @IDZona IS NULL AND @CodigoZonaVenta IS NOT NULL AND LTRIM(RTRIM(@CodigoZonaVenta)) <> ''
        BEGIN
            INSERT INTO cat_Zona (
                Codigo, Descripcion, EsVenta, EsReparto, 
                Estado, FechaCreacion, FechaModificacion
            )
            VALUES (
                @CodigoZonaVenta, 
                CASE WHEN LTRIM(RTRIM(@NombreZonaVenta)) = '' THEN 'ZONA ' + @CodigoZonaVenta ELSE @NombreZonaVenta END,
                1, 0, 
                1, GETDATE(), GETDATE()
            );
            SET @IDZona = SCOPE_IDENTITY();
        END
        ELSE IF @IDZona IS NOT NULL
        BEGIN
            UPDATE cat_Zona
            SET Estado = 1, FechaModificacion = GETDATE()
            WHERE ID = @IDZona AND Estado = 0;
        END

        -- -------------------------------------------------------------
        -- 4. RESOLVER O CREAR RUTA (rut_Ruta) Y ASIGNAR VENDEDOR
        -- NOTA: rut_Ruta usa la columna 'Nombre' (NO 'Descripcion')
        -- -------------------------------------------------------------
        DECLARE @IDRuta int;
        SELECT TOP 1 @IDRuta = ID FROM rut_Ruta WHERE Codigo = @CodigoRuta;

        IF @IDRuta IS NULL AND @CodigoRuta IS NOT NULL AND LTRIM(RTRIM(@CodigoRuta)) <> ''
        BEGIN
            INSERT INTO rut_Ruta (
                Codigo, Nombre, IDVendedor, 
                Estado, FechaCreacion, FechaModificacion, Eliminado
            )
            VALUES (
                @CodigoRuta, 
                'RUTA ' + @CodigoRuta, 
                @IDVendedor, 
                1, GETDATE(), GETDATE(), 0
            );
            SET @IDRuta = SCOPE_IDENTITY();
        END
        ELSE IF @IDRuta IS NOT NULL
        BEGIN
            -- Si la ruta ya existía, actualizamos vendedor y garantizamos que esté activa
            UPDATE rut_Ruta 
            SET IDVendedor = ISNULL(@IDVendedor, IDVendedor), 
                Estado = 1, 
                Eliminado = 0, 
                FechaModificacion = GETDATE() 
            WHERE ID = @IDRuta 
              AND (IDVendedor <> @IDVendedor OR Estado = 0 OR Eliminado = 1);
        END

        -- -------------------------------------------------------------
        -- 5. VINCULAR ZONA + RUTA + DÍA EN rut_RutaZonaDia
        -- NOTA: rut_RutaZonaDia usa 'OrdenVisita' (NO 'Secuencia')
        -- -------------------------------------------------------------
        IF @IDZona IS NOT NULL AND @IDRuta IS NOT NULL
        BEGIN
            -- Validar que @DiaDeSemana esté en rango 1..7
            IF @DiaDeSemana < 1 OR @DiaDeSemana > 7 SET @DiaDeSemana = 1;

            -- Desactivar cualquier otra ruta o día antiguo que haya tenido esta misma Zona en el pasado
            UPDATE rut_RutaZonaDia
            SET Estado = 0, FechaModificacion = GETDATE()
            WHERE IDZona = @IDZona
              AND (IDRuta <> @IDRuta OR IDDia <> @DiaDeSemana)
              AND Estado = 1;

            IF NOT EXISTS (SELECT 1 FROM rut_RutaZonaDia WHERE IDZona = @IDZona AND IDRuta = @IDRuta AND IDDia = @DiaDeSemana)
            BEGIN
                INSERT INTO rut_RutaZonaDia (
                    IDZona, IDRuta, IDDia, OrdenVisita, 
                    Estado, FechaCreacion, FechaModificacion
                )
                VALUES (
                    @IDZona, @IDRuta, @DiaDeSemana, @OrdenVisita, 
                    @Estado, GETDATE(), GETDATE()
                );
            END
            ELSE
            BEGIN
                UPDATE rut_RutaZonaDia 
                SET Estado = @Estado, OrdenVisita = @OrdenVisita, FechaModificacion = GETDATE()
                WHERE IDZona = @IDZona AND IDRuta = @IDRuta AND IDDia = @DiaDeSemana;
            END
        END

        -- -------------------------------------------------------------
        -- 6. VINCULAR LA DIRECCIÓN DEL CLIENTE CON LA ZONA EN dir_DireccionZona
        -- NOTA: dir_DireccionZona tiene el índice único UQ_DireccionZona_Venta
        --       que permite solo UNA fila con EsPrincipalVenta = 1 por IDDireccion.
        -- -------------------------------------------------------------
        IF @IDDireccion IS NOT NULL AND @IDZona IS NOT NULL
        BEGIN
            -- Desmarcar EsPrincipalVenta = 1 en cualquier otra zona previa de esta misma dirección
            UPDATE dir_DireccionZona
            SET EsPrincipalVenta = 0, FechaModificacion = GETDATE()
            WHERE IDDireccion = @IDDireccion 
              AND IDZona <> @IDZona 
              AND EsPrincipalVenta = 1;

            IF NOT EXISTS (SELECT 1 FROM dir_DireccionZona WHERE IDDireccion = @IDDireccion AND IDZona = @IDZona)
            BEGIN
                INSERT INTO dir_DireccionZona (
                    IDDireccion, IDZona, EsPrincipalVenta, EsPrincipalReparto, 
                    Estado, FechaCreacion, FechaModificacion
                )
                VALUES (
                    @IDDireccion, @IDZona, 1, 0, 
                    @Estado, GETDATE(), GETDATE()
                );
            END
            ELSE
            BEGIN
                UPDATE dir_DireccionZona 
                SET Estado = @Estado, EsPrincipalVenta = 1, FechaModificacion = GETDATE()
                WHERE IDDireccion = @IDDireccion AND IDZona = @IDZona;
            END
        END

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH
END
GO
