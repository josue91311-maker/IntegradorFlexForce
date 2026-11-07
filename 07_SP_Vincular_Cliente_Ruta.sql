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

        -- Si se encontró la dirección pero el cliente tenía IDExtra NULL en cli_Cliente, resolverlo desde dir_EntidadDireccion
        IF @IDCliente IS NULL AND @IDDireccion IS NOT NULL
        BEGIN
            SELECT TOP 1 @IDCliente = IDEntidad
            FROM dir_EntidadDireccion
            WHERE IDDireccion = @IDDireccion AND TipoEntidad = 'CLIENTE'
            ORDER BY EsPrincipal DESC, Estado DESC, ID DESC;

            IF @IDCliente IS NOT NULL
            BEGIN
                UPDATE cli_Cliente
                SET IDExtra = @ID_Cliente_Origen, FechaModificacion = GETDATE()
                WHERE ID = @IDCliente AND IDExtra IS NULL;
            END
        END

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
        -- 3. RESOLVER O CREAR RUTA (rut_Ruta) Y ASIGNAR VENDEDOR
        -- NOTA: Si un mismo CodigoRuta (ej. '01CALL046') es usado por dos vendedores distintos
        --       en distintas zonas (ej. 01CALL02 y 01CALL04), se evita que se pisen mutuamente.
        -- -------------------------------------------------------------
        DECLARE @IDRuta int;
        DECLARE @CodRutaVend nvarchar(20) = LEFT(LTRIM(RTRIM(@CodigoRuta)) + '-' + LTRIM(RTRIM(@CodigoVendedor)), 20);

        SELECT TOP 1 @IDRuta = ID 
        FROM rut_Ruta 
        WHERE (Codigo = @CodigoRuta OR Codigo = @CodRutaVend)
          AND IDVendedor = @IDVendedor;

        IF @IDRuta IS NULL AND @CodigoRuta IS NOT NULL AND LTRIM(RTRIM(@CodigoRuta)) <> ''
        BEGIN
            DECLARE @IDRutaExistente int;
            SELECT TOP 1 @IDRutaExistente = ID FROM rut_Ruta WHERE Codigo = @CodigoRuta;

            IF @IDRutaExistente IS NULL
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
            ELSE
            BEGIN
                -- Si existe pero con otro vendedor: verificar si hay clientes activos en otras zonas usándola
                IF NOT EXISTS (
                    SELECT 1 
                    FROM rut_RutaZonaDia rzd
                    INNER JOIN cat_Zona z ON z.ID = rzd.IDZona
                    WHERE rzd.IDRuta = @IDRutaExistente 
                      AND rzd.Estado = 1 
                      AND z.Codigo <> @CodigoZonaVenta
                )
                BEGIN
                    UPDATE rut_Ruta 
                    SET IDVendedor = ISNULL(@IDVendedor, IDVendedor), 
                        Estado = 1, 
                        Eliminado = 0, 
                        FechaModificacion = GETDATE() 
                    WHERE ID = @IDRutaExistente;
                    SET @IDRuta = @IDRutaExistente;
                END
                ELSE
                BEGIN
                    SELECT TOP 1 @IDRuta = ID FROM rut_Ruta WHERE Codigo = @CodRutaVend;
                    IF @IDRuta IS NULL
                    BEGIN
                        INSERT INTO rut_Ruta (
                            Codigo, Nombre, IDVendedor, 
                            Estado, FechaCreacion, FechaModificacion, Eliminado
                        )
                        VALUES (
                            @CodRutaVend, 
                            'RUTA ' + @CodRutaVend, 
                            @IDVendedor, 
                            1, GETDATE(), GETDATE(), 0
                        );
                        SET @IDRuta = SCOPE_IDENTITY();
                    END
                    ELSE
                    BEGIN
                        UPDATE rut_Ruta 
                        SET IDVendedor = ISNULL(@IDVendedor, IDVendedor), 
                            Estado = 1, 
                            Eliminado = 0, 
                            FechaModificacion = GETDATE() 
                        WHERE ID = @IDRuta;
                    END
                END
            END
        END
        ELSE IF @IDRuta IS NOT NULL
        BEGIN
            UPDATE rut_Ruta 
            SET Estado = 1, Eliminado = 0, FechaModificacion = GETDATE() 
            WHERE ID = @IDRuta AND (Estado = 0 OR Eliminado = 1);
        END

        -- -------------------------------------------------------------
        -- 4. RESOLVER O CREAR ZONA (cat_Zona)
        -- NOTA: Si una zona genérica (ej. '02999') es compartida por varios vendedores/rutas,
        --       cada vendedor usa su propia instancia de zona para no desactivar a los demás.
        -- -------------------------------------------------------------
        IF @DiaDeSemana < 1 OR @DiaDeSemana > 7 SET @DiaDeSemana = 1;

        DECLARE @IDZona int;
        DECLARE @CodZonaVend nvarchar(50) = LEFT(LTRIM(RTRIM(@CodigoZonaVenta)) + '-' + LTRIM(RTRIM(@CodigoVendedor)), 50);

        -- Primero buscar si ya existe la zona vinculada a esta misma ruta
        SELECT TOP 1 @IDZona = z.ID
        FROM cat_Zona z
        INNER JOIN rut_RutaZonaDia rzd ON rzd.IDZona = z.ID
        WHERE (z.Codigo = @CodigoZonaVenta OR z.Codigo = @CodZonaVend)
          AND rzd.IDRuta = @IDRuta
          AND rzd.IDDia = @DiaDeSemana;

        IF @IDZona IS NULL
        BEGIN
            DECLARE @IDZonaBase int;
            SELECT TOP 1 @IDZonaBase = ID FROM cat_Zona WHERE Codigo = @CodigoZonaVenta;

            IF @IDZonaBase IS NULL AND @CodigoZonaVenta IS NOT NULL AND LTRIM(RTRIM(@CodigoZonaVenta)) <> ''
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
            ELSE IF @IDZonaBase IS NOT NULL
            BEGIN
                -- Verificar si @IDZonaBase está siendo usada activamente por OTRA dirección con OTRA ruta
                IF NOT EXISTS (
                    SELECT 1
                    FROM dir_DireccionZona dz
                    INNER JOIN rut_RutaZonaDia rzd ON rzd.IDZona = dz.IDZona
                    WHERE dz.IDZona = @IDZonaBase
                      AND dz.IDDireccion <> @IDDireccion
                      AND dz.Estado = 1
                      AND rzd.Estado = 1
                      AND (rzd.IDRuta <> @IDRuta OR rzd.IDDia <> @DiaDeSemana)
                )
                BEGIN
                    SET @IDZona = @IDZonaBase;
                    UPDATE cat_Zona SET Estado = 1, FechaModificacion = GETDATE() WHERE ID = @IDZona AND Estado = 0;
                END
                ELSE
                BEGIN
                    SELECT TOP 1 @IDZona = ID FROM cat_Zona WHERE Codigo = @CodZonaVend;
                    IF @IDZona IS NULL
                    BEGIN
                        INSERT INTO cat_Zona (
                            Codigo, Descripcion, EsVenta, EsReparto, 
                            Estado, FechaCreacion, FechaModificacion
                        )
                        VALUES (
                            @CodZonaVend, 
                            CASE WHEN LTRIM(RTRIM(@NombreZonaVenta)) = '' THEN 'ZONA ' + @CodZonaVend ELSE @NombreZonaVenta END,
                            1, 0, 
                            1, GETDATE(), GETDATE()
                        );
                        SET @IDZona = SCOPE_IDENTITY();
                    END
                    ELSE
                    BEGIN
                        UPDATE cat_Zona SET Estado = 1, FechaModificacion = GETDATE() WHERE ID = @IDZona AND Estado = 0;
                    END
                END
            END
        END
        ELSE
        BEGIN
            UPDATE cat_Zona SET Estado = 1, FechaModificacion = GETDATE() WHERE ID = @IDZona AND Estado = 0;
        END

        -- -------------------------------------------------------------
        -- 5. VINCULAR ZONA + RUTA + DÍA EN rut_RutaZonaDia
        -- NOTA: rut_RutaZonaDia usa 'OrdenVisita' (NO 'Secuencia')
        -- -------------------------------------------------------------
        IF @IDZona IS NOT NULL AND @IDRuta IS NOT NULL
        BEGIN
            -- Desactivar cualquier otra ruta o día antiguo que haya tenido esta misma Zona exclusiva
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
        -- NOTA: Una misma dirección puede tener múltiples zonas activas para
        --       distintos vendedores o días. Solo garantizamos que esta zona
        --       quede activa para la dirección.
        -- -------------------------------------------------------------
        IF @IDDireccion IS NOT NULL AND @IDZona IS NOT NULL
        BEGIN
            -- Si no tiene ninguna zona con EsPrincipalVenta = 1, marcar esta como principal
            DECLARE @EsPrincVenta bit = CASE WHEN EXISTS (SELECT 1 FROM dir_DireccionZona WHERE IDDireccion = @IDDireccion AND EsPrincipalVenta = 1 AND IDZona <> @IDZona) THEN 0 ELSE 1 END;

            IF NOT EXISTS (SELECT 1 FROM dir_DireccionZona WHERE IDDireccion = @IDDireccion AND IDZona = @IDZona)
            BEGIN
                INSERT INTO dir_DireccionZona (
                    IDDireccion, IDZona, EsPrincipalVenta, EsPrincipalReparto, 
                    Estado, FechaCreacion, FechaModificacion
                )
                VALUES (
                    @IDDireccion, @IDZona, @EsPrincVenta, 0, 
                    @Estado, GETDATE(), GETDATE()
                );
            END
            ELSE
            BEGIN
                UPDATE dir_DireccionZona 
                SET Estado = @Estado, FechaModificacion = GETDATE()
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
