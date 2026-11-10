-- ==========================================================================================
-- ARCHIVO: 10_SP_Upsert_Producto_Force.sql
-- PROYECTO: IntegradorFlexForce
-- BASE DE DATOS: BDForceGVD
-- OBJETIVO: Stored Procedures para sincronizar el Módulo de Productos, Catálogo y Precios:
--           1. dbo.usp_Sync_UpsertProductoMaestro (Marca, Línea, Proveedor, SubProveedor, Producto)
--           2. dbo.usp_Sync_UpsertProductoUnidad  (UnidadMedida y Presentación/Factor en prd_ProductoUnidad)
--           3. dbo.usp_Sync_UpsertCatalogoProducto(Portafolio en ctg_Catalogo y relación en ctg_CatalogoProducto)
--           4. dbo.usp_Sync_UpsertPrecioEscala    (ListaPrecio, ItemPrecioProducto, PrecioProductoUnidad, PrecioEscala)
-- ==========================================================================================

USE [BDForceGVD]
GO

-- ==========================================================================================
-- 1. SP: UPSERT DE PRODUCTO Y SUS MAESTROS DE CLASIFICACIÓN (MARCA, LÍNEA, PROVEEDOR, SUBPROVEEDOR)
-- ==========================================================================================
CREATE OR ALTER PROCEDURE dbo.usp_Sync_UpsertProductoMaestro
    @ID_Producto_Origen         int,
    @CodigoProducto             nvarchar(50),
    @NombreProducto             nvarchar(200),
    @DescripcionProducto        nvarchar(max) = NULL,
    @EstadoProducto             bit = 1,
    -- Marca
    @ID_Marca_Origen            int = 0,
    @CodigoMarca                nvarchar(100) = '',
    @NombreMarca                nvarchar(100) = '',
    @EstadoMarca                bit = 1,
    -- Línea (ClaseProductoServicio)
    @ID_Linea_Origen            int = 0,
    @CodigoLinea                nvarchar(100) = '',
    @NombreLinea                nvarchar(100) = '',
    @EstadoLinea                bit = 1,
    -- Proveedor
    @ID_Proveedor_Origen        int = 0,
    @CodigoProveedor            nvarchar(100) = '',
    @RazonSocialProveedor       nvarchar(200) = '',
    @RUCProveedor               nvarchar(20) = '0',
    @EstadoProveedor            bit = 1,
    -- SubProveedor (Caracteristica)
    @ID_SubProveedor_Origen     int = 0,
    @NombreSubProveedor         nvarchar(200) = '',
    -- Salida
    @IDProductoGenerado         int OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRY
        BEGIN TRANSACTION;

        -- -------------------------------------------------------------
        -- A. RESOLVER O CREAR MARCA (prd_Marca)
        -- -------------------------------------------------------------
        DECLARE @IDMarca int;
        IF @ID_Marca_Origen > 0
        BEGIN
            SELECT TOP 1 @IDMarca = ID FROM dbo.prd_Marca WHERE IDExtra = @ID_Marca_Origen;

            IF @IDMarca IS NULL AND LTRIM(RTRIM(@NombreMarca)) <> ''
            BEGIN
                DECLARE @CodMarcaFmt nvarchar(100) = LEFT(
                    LTRIM(RTRIM(ISNULL(@CodigoMarca, '0000'))) + '-' + 
                    LEFT(LTRIM(RTRIM(@NombreMarca)), 3) + '-' + 
                    CAST(@ID_Marca_Origen AS varchar(15)), 100);

                SELECT TOP 1 @IDMarca = ID FROM dbo.prd_Marca WHERE Codigo = @CodMarcaFmt OR Codigo = LTRIM(RTRIM(@CodigoMarca));

                IF @IDMarca IS NULL
                BEGIN
                    INSERT INTO dbo.prd_Marca (Codigo, Nombre, Estado, FechaCreacion, FechaModificacion, IDExtra, Eliminado)
                    VALUES (@CodMarcaFmt, LEFT(LTRIM(RTRIM(@NombreMarca)), 100), @EstadoMarca, GETDATE(), GETDATE(), @ID_Marca_Origen, 0);
                    SET @IDMarca = SCOPE_IDENTITY();
                END
                ELSE
                BEGIN
                    UPDATE dbo.prd_Marca
                    SET IDExtra = @ID_Marca_Origen, Nombre = LEFT(LTRIM(RTRIM(@NombreMarca)), 100), Estado = @EstadoMarca, FechaModificacion = GETDATE()
                    WHERE ID = @IDMarca;
                END
            END
            ELSE IF @IDMarca IS NOT NULL AND LTRIM(RTRIM(@NombreMarca)) <> ''
            BEGIN
                UPDATE dbo.prd_Marca
                SET Nombre = LEFT(LTRIM(RTRIM(@NombreMarca)), 100),
                    Estado = @EstadoMarca,
                    Eliminado = 0,
                    FechaModificacion = GETDATE()
                WHERE ID = @IDMarca
                  AND (Nombre <> LEFT(LTRIM(RTRIM(@NombreMarca)), 100) OR ISNULL(Estado, 1) <> @EstadoMarca);
            END
        END

        IF @IDMarca IS NULL
            SELECT TOP 1 @IDMarca = ID FROM dbo.prd_Marca WHERE Estado = 1 AND Eliminado = 0 ORDER BY ID;

        -- -------------------------------------------------------------
        -- B. RESOLVER O CREAR LÍNEA DE PRODUCTO (prd_LineaProducto)
        -- -------------------------------------------------------------
        DECLARE @IDLinea int;
        IF @ID_Linea_Origen > 0
        BEGIN
            SELECT TOP 1 @IDLinea = ID FROM dbo.prd_LineaProducto WHERE IDExtra = @ID_Linea_Origen;

            IF @IDLinea IS NULL AND LTRIM(RTRIM(@CodigoLinea)) <> ''
            BEGIN
                SELECT TOP 1 @IDLinea = ID FROM dbo.prd_LineaProducto WHERE Codigo = LTRIM(RTRIM(@CodigoLinea));

                IF @IDLinea IS NULL
                BEGIN
                    INSERT INTO dbo.prd_LineaProducto (Codigo, Nombre, Estado, FechaCreacion, FechaModificacion, IDExtra)
                    VALUES (LEFT(LTRIM(RTRIM(@CodigoLinea)), 100), LEFT(LTRIM(RTRIM(ISNULL(@NombreLinea, @CodigoLinea))), 100), @EstadoLinea, GETDATE(), GETDATE(), @ID_Linea_Origen);
                    SET @IDLinea = SCOPE_IDENTITY();
                END
                ELSE
                BEGIN
                    UPDATE dbo.prd_LineaProducto
                    SET IDExtra = @ID_Linea_Origen, Nombre = LEFT(LTRIM(RTRIM(ISNULL(@NombreLinea, @CodigoLinea))), 100), Estado = @EstadoLinea, FechaModificacion = GETDATE()
                    WHERE ID = @IDLinea;
                END
            END
            ELSE IF @IDLinea IS NOT NULL AND LTRIM(RTRIM(@NombreLinea)) <> ''
            BEGIN
                UPDATE dbo.prd_LineaProducto
                SET Nombre = LEFT(LTRIM(RTRIM(@NombreLinea)), 100),
                    Estado = @EstadoLinea,
                    FechaModificacion = GETDATE()
                WHERE ID = @IDLinea
                  AND (Nombre <> LEFT(LTRIM(RTRIM(@NombreLinea)), 100) OR ISNULL(Estado, 1) <> @EstadoLinea);
            END
        END

        IF @IDLinea IS NULL
            SELECT TOP 1 @IDLinea = ID FROM dbo.prd_LineaProducto WHERE Estado = 1 ORDER BY ID;

        -- -------------------------------------------------------------
        -- C. RESOLVER CATEGORÍA POR DEFECTO (prd_CategoriaProducto)
        -- -------------------------------------------------------------
        DECLARE @IDCategoria int;
        SELECT TOP 1 @IDCategoria = ID FROM dbo.prd_CategoriaProducto WHERE ID = 9;
        IF @IDCategoria IS NULL
            SELECT TOP 1 @IDCategoria = ID FROM dbo.prd_CategoriaProducto WHERE Estado = 1 ORDER BY ID;

        -- -------------------------------------------------------------
        -- D. RESOLVER O CREAR PROVEEDOR (prd_Proveedor)
        -- -------------------------------------------------------------
        DECLARE @IDProveedor int;
        IF @ID_Proveedor_Origen > 0
        BEGIN
            SELECT TOP 1 @IDProveedor = ID FROM dbo.prd_Proveedor WHERE IDExtra = @ID_Proveedor_Origen;

            IF @IDProveedor IS NULL AND LTRIM(RTRIM(@CodigoProveedor)) <> ''
            BEGIN
                SELECT TOP 1 @IDProveedor = ID FROM dbo.prd_Proveedor WHERE Codigo = LTRIM(RTRIM(@CodigoProveedor));

                IF @IDProveedor IS NULL
                BEGIN
                    INSERT INTO dbo.prd_Proveedor (
                        Codigo, RazonSocial, RUC, Direccion, Estado,
                        FechaCreacion, FechaModificacion, IDExtra, Eliminado
                    )
                    VALUES (
                        LEFT(LTRIM(RTRIM(@CodigoProveedor)), 100),
                        LEFT(LTRIM(RTRIM(ISNULL(@RazonSocialProveedor, @CodigoProveedor))), 200),
                        LEFT(LTRIM(RTRIM(ISNULL(@RUCProveedor, '0'))), 20),
                        'Sin Dirección',
                        @EstadoProveedor,
                        GETDATE(), GETDATE(), @ID_Proveedor_Origen, 0
                    );
                    SET @IDProveedor = SCOPE_IDENTITY();
                END
                ELSE
                BEGIN
                    UPDATE dbo.prd_Proveedor
                    SET IDExtra = @ID_Proveedor_Origen,
                        RazonSocial = LEFT(LTRIM(RTRIM(ISNULL(@RazonSocialProveedor, RazonSocial))), 200),
                        Estado = @EstadoProveedor,
                        FechaModificacion = GETDATE()
                    WHERE ID = @IDProveedor;
                END
            END
            ELSE IF @IDProveedor IS NOT NULL AND LTRIM(RTRIM(@RazonSocialProveedor)) <> ''
            BEGIN
                UPDATE dbo.prd_Proveedor
                SET RazonSocial = LEFT(LTRIM(RTRIM(@RazonSocialProveedor)), 200),
                    Estado = @EstadoProveedor,
                    Eliminado = 0,
                    FechaModificacion = GETDATE()
                WHERE ID = @IDProveedor
                  AND (RazonSocial <> LEFT(LTRIM(RTRIM(@RazonSocialProveedor)), 200) OR ISNULL(Estado, 1) <> @EstadoProveedor);
            END
        END

        IF @IDProveedor IS NULL
        BEGIN
            SELECT TOP 1 @IDProveedor = ID FROM dbo.prd_Proveedor WHERE ID = 25881;
            IF @IDProveedor IS NULL
                SELECT TOP 1 @IDProveedor = ID FROM dbo.prd_Proveedor WHERE Estado = 1 AND Eliminado = 0 ORDER BY ID DESC;
        END

        -- -------------------------------------------------------------
        -- E. RESOLVER O CREAR SUBPROVEEDOR (prd_SubProveedor)
        -- -------------------------------------------------------------
        DECLARE @IDSubProveedor int = NULL;
        IF @ID_SubProveedor_Origen > 0
        BEGIN
            SELECT TOP 1 @IDSubProveedor = ID FROM dbo.prd_SubProveedor WHERE IDExtra = @ID_SubProveedor_Origen;

            IF @IDSubProveedor IS NULL AND LTRIM(RTRIM(@NombreSubProveedor)) <> ''
            BEGIN
                DECLARE @IDProvPadreSub int;
                SELECT TOP 1 @IDProvPadreSub = ID FROM dbo.prd_Proveedor WHERE ID = 23020;
                IF @IDProvPadreSub IS NULL SET @IDProvPadreSub = @IDProveedor;

                DECLARE @CodSubProv nvarchar(50) = RTRIM(CAST(@ID_SubProveedor_Origen AS varchar(15))) + '_' + LEFT(LTRIM(RTRIM(@NombreSubProveedor)), 6);

                INSERT INTO dbo.prd_SubProveedor (
                    IDProveedor, Codigo, Nombre, Estado,
                    FechaCreacion, FechaModificacion, IDExtra, Eliminado
                )
                VALUES (
                    @IDProvPadreSub, @CodSubProv, LEFT(LTRIM(RTRIM(@NombreSubProveedor)), 200), 1,
                    GETDATE(), GETDATE(), @ID_SubProveedor_Origen, 0
                );
                SET @IDSubProveedor = SCOPE_IDENTITY();
            END
            ELSE IF @IDSubProveedor IS NOT NULL AND LTRIM(RTRIM(@NombreSubProveedor)) <> ''
            BEGIN
                UPDATE dbo.prd_SubProveedor
                SET Nombre = LEFT(LTRIM(RTRIM(@NombreSubProveedor)), 200),
                    Estado = 1,
                    Eliminado = 0,
                    FechaModificacion = GETDATE()
                WHERE ID = @IDSubProveedor
                  AND (Nombre <> LEFT(LTRIM(RTRIM(@NombreSubProveedor)), 200) OR ISNULL(Estado, 1) = 0);
            END
        END

        IF @IDSubProveedor IS NULL
            SELECT TOP 1 @IDSubProveedor = ID FROM dbo.prd_SubProveedor WHERE ID = 11;

        -- -------------------------------------------------------------
        -- F. INSERTAR O ACTUALIZAR PRODUCTO (prd_Producto)
        -- -------------------------------------------------------------
        DECLARE @IDProducto int;
        SELECT TOP 1 @IDProducto = ID FROM dbo.prd_Producto WHERE IDExtra = @ID_Producto_Origen;

        IF @IDProducto IS NULL AND LTRIM(RTRIM(@CodigoProducto)) <> ''
        BEGIN
            SELECT TOP 1 @IDProducto = ID FROM dbo.prd_Producto WHERE Codigo = LTRIM(RTRIM(@CodigoProducto)) AND Eliminado = 0;
        END

        DECLARE @NomProdLimpio nvarchar(200) = LEFT(LTRIM(RTRIM(ISNULL(@NombreProducto, @CodigoProducto))), 200);
        DECLARE @DescProdLimpia nvarchar(max) = ISNULL(NULLIF(LTRIM(RTRIM(@DescripcionProducto)), ''), @NomProdLimpio);

        IF @IDProducto IS NULL
        BEGIN
            INSERT INTO dbo.prd_Producto (
                Nombre, Descripcion, IDMarca, IDCategoria, IDLinea,
                IDProveedor, IDSubProveedor, Estado, FechaCreacion, FechaModificacion,
                IDExtra, Eliminado, Codigo
            )
            VALUES (
                @NomProdLimpio, @DescProdLimpia, @IDMarca, @IDCategoria, @IDLinea,
                @IDProveedor, @IDSubProveedor, @EstadoProducto, GETDATE(), GETDATE(),
                @ID_Producto_Origen, 0, LEFT(LTRIM(RTRIM(@CodigoProducto)), 50)
            );
            SET @IDProducto = SCOPE_IDENTITY();
        END
        ELSE
        BEGIN
            UPDATE dbo.prd_Producto
            SET Nombre = @NomProdLimpio,
                Descripcion = @DescProdLimpia,
                IDMarca = ISNULL(@IDMarca, IDMarca),
                IDLinea = ISNULL(@IDLinea, IDLinea),
                IDProveedor = ISNULL(@IDProveedor, IDProveedor),
                IDSubProveedor = ISNULL(@IDSubProveedor, IDSubProveedor),
                Estado = @EstadoProducto,
                IDExtra = @ID_Producto_Origen,
                Codigo = LEFT(LTRIM(RTRIM(@CodigoProducto)), 50),
                Eliminado = 0,
                FechaModificacion = GETDATE()
            WHERE ID = @IDProducto;
        END

        SET @IDProductoGenerado = @IDProducto;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH
END
GO

-- ==========================================================================================
-- 2. SP: UPSERT DE PRESENTACIONES / UNIDADES Y FACTORES POR PRODUCTO (prd_ProductoUnidad)
-- ==========================================================================================
CREATE OR ALTER PROCEDURE dbo.usp_Sync_UpsertProductoUnidad
    @ID_Unidad_Origen           int,            -- u.PKID en digalimenta41
    @ID_Producto_Origen         int,            -- u.IDProductoServicio en digalimenta41
    @CodigoUnidad               nvarchar(10),   -- u.Codigo / u.Abreviacion
    @NombreUnidad               nvarchar(100),  -- u.Descripcion (CAJA, UNIDAD, BOLSA, DISPLAY)
    @Abreviacion                nvarchar(50),   -- u.Abreviacion
    @EsUnidadBase               bit,            -- u.Base
    @Factor                     decimal(18, 6), -- u.Factor
    @Peso                       decimal(10, 4) = 0,
    @Volumen                    decimal(12, 6) = 0,
    @Largo                      decimal(10, 4) = 0,
    @Ancho                      decimal(10, 4) = 0,
    @Alto                       decimal(10, 4) = 0,
    @Estado                     bit = 1,
    @CodigoProducto             nvarchar(50) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRY
        BEGIN TRANSACTION;

        -- 1. Obtener IDProducto en Force (por IDExtra o por Código)
        DECLARE @IDProducto int;
        SELECT TOP 1 @IDProducto = ID FROM dbo.prd_Producto WHERE IDExtra = @ID_Producto_Origen;

        IF @IDProducto IS NULL AND @CodigoProducto IS NOT NULL AND LTRIM(RTRIM(@CodigoProducto)) <> ''
        BEGIN
            SELECT TOP 1 @IDProducto = ID FROM dbo.prd_Producto WHERE Codigo = LTRIM(RTRIM(@CodigoProducto)) AND Eliminado = 0;
            IF @IDProducto IS NOT NULL
            BEGIN
                UPDATE dbo.prd_Producto SET IDExtra = @ID_Producto_Origen WHERE ID = @IDProducto;
            END
        END

        IF @IDProducto IS NULL
        BEGIN
            -- El producto no existe en Force (es un producto antiguo dado de baja en ERP antiguo)
            ROLLBACK TRANSACTION;
            RETURN;
        END

        -- 2. Resolver o crear Unidad de Medida maestra (prd_UnidadMedida)
        DECLARE @IDUnidadMedida int;
        DECLARE @AbrevLimpia nvarchar(50) = LEFT(LTRIM(RTRIM(ISNULL(NULLIF(@Abreviacion, ''), 'UN'))), 50);
        DECLARE @NomUnidadLimpio nvarchar(100) = LEFT(LTRIM(RTRIM(ISNULL(NULLIF(@NombreUnidad, ''), @AbrevLimpia))), 100);
        DECLARE @CodUnidadCorto nvarchar(10) = LEFT(LTRIM(RTRIM(ISNULL(NULLIF(@Abreviacion, ''), @NomUnidadLimpio))), 10);

        SELECT TOP 1 @IDUnidadMedida = ID
        FROM dbo.prd_UnidadMedida
        WHERE Nombre = @NomUnidadLimpio OR Codigo = @CodUnidadCorto;

        IF @IDUnidadMedida IS NULL
        BEGIN
            -- Garantizar unicidad de Codigo (nvarchar(10))
            IF EXISTS (SELECT 1 FROM dbo.prd_UnidadMedida WHERE Codigo = @CodUnidadCorto)
                SET @CodUnidadCorto = LEFT(@CodUnidadCorto, 5) + RIGHT(CAST(@ID_Unidad_Origen AS varchar(10)), 4);

            INSERT INTO dbo.prd_UnidadMedida (Codigo, Nombre, CodigoSunat, Descripcion, Estado, FechaCreacion, FechaModificacion, IDExtra)
            VALUES (@CodUnidadCorto, @NomUnidadLimpio, 'NIU', @NomUnidadLimpio, 1, GETDATE(), GETDATE(), @ID_Unidad_Origen);
            SET @IDUnidadMedida = SCOPE_IDENTITY();
        END

        -- 3. Insertar o actualizar prd_ProductoUnidad
        DECLARE @IDProductoUnidad int;
        SELECT TOP 1 @IDProductoUnidad = ID FROM dbo.prd_ProductoUnidad WHERE IDExtra = @ID_Unidad_Origen;

        IF @IDProductoUnidad IS NULL
        BEGIN
            SELECT TOP 1 @IDProductoUnidad = ID
            FROM dbo.prd_ProductoUnidad
            WHERE IDProducto = @IDProducto AND Abrev = @AbrevLimpia AND Factor = @Factor AND Eliminado = 0;
        END

        IF @IDProductoUnidad IS NULL
        BEGIN
            INSERT INTO dbo.prd_ProductoUnidad (
                IDProducto, IDUnidadMedida, EsUnidadBase, Factor, Abrev,
                Alto, Ancho, Largo, Volumen, PesoNeto, PesoBruto,
                UsadoEnAlmacenamiento, UsadoEnVenta, UsadoEnCompra, UsadoEnDespacho,
                Estado, FechaCreacion, FechaModificacion, IDExtra, Eliminado
            )
            VALUES (
                @IDProducto, @IDUnidadMedida, @EsUnidadBase, @Factor, @AbrevLimpia,
                @Alto, @Ancho, @Largo, @Volumen, @Peso, @Peso,
                1, 1, 1, 1,
                @Estado, GETDATE(), GETDATE(), @ID_Unidad_Origen, 0
            );
        END
        ELSE
        BEGIN
            UPDATE dbo.prd_ProductoUnidad
            SET IDProducto = @IDProducto,
                IDUnidadMedida = @IDUnidadMedida,
                EsUnidadBase = @EsUnidadBase,
                Factor = @Factor,
                Abrev = @AbrevLimpia,
                Alto = @Alto,
                Ancho = @Ancho,
                Largo = @Largo,
                Volumen = @Volumen,
                PesoNeto = @Peso,
                PesoBruto = @Peso,
                Estado = @Estado,
                IDExtra = @ID_Unidad_Origen,
                Eliminado = 0,
                FechaModificacion = GETDATE()
            WHERE ID = @IDProductoUnidad;
        END

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH
END
GO

-- ==========================================================================================
-- 3. SP: UPSERT DE CATÁLOGO (PORTAFOLIO) Y ASIGNACIÓN DE PRODUCTOS (ctg_CatalogoProducto)
-- ==========================================================================================
CREATE OR ALTER PROCEDURE dbo.usp_Sync_UpsertCatalogoProducto
    @ID_Portafolio_Origen       int,            -- PortafolioProducto.PKID
    @CodigoPortafolio           nvarchar(50),   -- PortafolioProducto.Codigo
    @NombrePortafolio           nvarchar(100),  -- PortafolioProducto.Nombre
    @ObservacionPortafolio      nvarchar(max) = NULL,
    @EstadoPortafolio           bit = 1,
    @ID_Producto_Origen         int = 0,        -- ItemPortafolioProductoProducto.IDProducto (0 si solo se actualiza cabecera)
    @EstadoAsignacion           bit = 1
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRY
        BEGIN TRANSACTION;

        -- 1. Resolver o crear el Catálogo en ctg_Catalogo
        DECLARE @IDCatalogo int;
        SELECT TOP 1 @IDCatalogo = ID FROM dbo.ctg_Catalogo WHERE IDExtra = @ID_Portafolio_Origen AND Eliminado = 0;

        IF @IDCatalogo IS NULL AND LTRIM(RTRIM(@CodigoPortafolio)) <> ''
        BEGIN
            SELECT TOP 1 @IDCatalogo = ID FROM dbo.ctg_Catalogo WHERE Codigo = LTRIM(RTRIM(@CodigoPortafolio)) AND Eliminado = 0;
        END

        IF @IDCatalogo IS NULL
        BEGIN
            INSERT INTO dbo.ctg_Catalogo (
                Codigo, Nombre, Descripcion, Estado,
                FechaCreacion, FechaModificacion, IDExtra, Eliminado
            )
            VALUES (
                LEFT(LTRIM(RTRIM(ISNULL(@CodigoPortafolio, CAST(@ID_Portafolio_Origen AS varchar(20))))), 50),
                LEFT(LTRIM(RTRIM(ISNULL(@NombrePortafolio, 'PORTAFOLIO'))), 100),
                @ObservacionPortafolio,
                @EstadoPortafolio,
                GETDATE(), GETDATE(), @ID_Portafolio_Origen, 0
            );
            SET @IDCatalogo = SCOPE_IDENTITY();
        END
        ELSE
        BEGIN
            UPDATE dbo.ctg_Catalogo
            SET Codigo = LEFT(LTRIM(RTRIM(ISNULL(@CodigoPortafolio, Codigo))), 50),
                Nombre = LEFT(LTRIM(RTRIM(ISNULL(@NombrePortafolio, Nombre))), 100),
                Descripcion = ISNULL(@ObservacionPortafolio, Descripcion),
                Estado = @EstadoPortafolio,
                IDExtra = @ID_Portafolio_Origen,
                Eliminado = 0,
                FechaModificacion = GETDATE()
            WHERE ID = @IDCatalogo;
        END

        -- 2. Si se indicó un producto, vincularlo en ctg_CatalogoProducto
        IF @ID_Producto_Origen > 0 AND @IDCatalogo IS NOT NULL
        BEGIN
            DECLARE @IDProducto int;
            SELECT TOP 1 @IDProducto = ID FROM dbo.prd_Producto WHERE IDExtra = @ID_Producto_Origen;

            IF @IDProducto IS NOT NULL
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM dbo.ctg_CatalogoProducto WHERE IDCatalogo = @IDCatalogo AND IDProducto = @IDProducto)
                BEGIN
                    INSERT INTO dbo.ctg_CatalogoProducto (IDCatalogo, IDProducto, Estado, FechaCreacion, FechaModificacion)
                    VALUES (@IDCatalogo, @IDProducto, @EstadoAsignacion, GETDATE(), GETDATE());
                END
                ELSE
                BEGIN
                    UPDATE dbo.ctg_CatalogoProducto
                    SET Estado = @EstadoAsignacion,
                        FechaModificacion = GETDATE()
                    WHERE IDCatalogo = @IDCatalogo AND IDProducto = @IDProducto;
                END
            END
        END

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH
END
GO

-- ==========================================================================================
-- 4. SP: UPSERT DE LISTAS DE PRECIOS, UNIDAD PRECIO Y ESCALAS (prc_PrecioEscala)
-- ==========================================================================================
CREATE OR ALTER PROCEDURE dbo.usp_Sync_UpsertPrecioEscala
    @ID_Escala_Origen               int,            -- Escala.PKID
    @ID_UnidadItemLista_Origen      int,            -- UnidadItemListaPrecios.PKID
    @ID_ItemLista_Origen            int,            -- ItemListaPrecios.PKID
    @ID_ListaPrecio_Origen          int,            -- ListaPrecios.PKID
    @NombreListaPrecio              nvarchar(150),  -- ListaPrecios.Descripcion
    @EstadoListaPrecio              bit,            -- ListaPrecios.Activo
    @ID_Producto_Origen             int,            -- ItemListaPrecios.IDProducto
    @ID_Unidad_Origen               int,            -- UnidadItemListaPrecios.IDUnidad (apunta a unidad.PKID)
    @DesactivadoItem                bit = 0,        -- ItemListaPrecios.Desactivado
    @Credito                        bit = 1,
    @Contado                        bit = 1,
    @PermiteVentaUnidad             bit = 1,        -- UnidadItemListaPrecios.Venta
    @Desde                          decimal(18, 4) = 0,
    @Hasta                          decimal(18, 4) = 999999,
    @Precio                         decimal(18, 6) = 0,
    @PrecioIncImpto                 decimal(18, 6) = 0,
    @UtilidadMonto                  decimal(18, 6) = 0,
    @UtilidadPorcentual             decimal(18, 6) = 0,
    @EsVigente                      bit = 1
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRY
        BEGIN TRANSACTION;

        -- 0. Si la Escala ya existe por IDExtra en prc_PrecioEscala (caso UPDATE de precio vigente),
        -- actualizar de inmediato precio neto, con IGV y vigencia sin riesgo de descarte
        DECLARE @IDEscalaDirecta int;
        SELECT TOP 1 @IDEscalaDirecta = ID FROM dbo.prc_PrecioEscala WHERE IDExtra = @ID_Escala_Origen;

        IF @IDEscalaDirecta IS NOT NULL
        BEGIN
            IF @Precio < 0 SET @Precio = 0;
            IF @PrecioIncImpto < 0 SET @PrecioIncImpto = 0;

            UPDATE dbo.prc_PrecioEscala
            SET Precio = @Precio,
                PrecioIncImpto = @PrecioIncImpto,
                UtilidadMonto = @UtilidadMonto,
                UtilidadPorcentual = @UtilidadPorcentual,
                Desde = @Desde,
                Hasta = @Hasta,
                Esvigente = @EsVigente,
                Eliminado = 0,
                FechaUltimaModificacion = GETDATE(),
                FechaModificacion = GETDATE()
            WHERE ID = @IDEscalaDirecta;

            COMMIT TRANSACTION;
            RETURN;
        END

        -- 1. Resolver Producto y ProductoUnidad en Force
        DECLARE @IDProducto int, @IDProductoUnidad int;
        SELECT TOP 1 @IDProducto = ID FROM dbo.prd_Producto WHERE IDExtra = @ID_Producto_Origen;
        SELECT TOP 1 @IDProductoUnidad = ID FROM dbo.prd_ProductoUnidad WHERE IDExtra = @ID_Unidad_Origen;

        IF @IDProducto IS NULL OR @IDProductoUnidad IS NULL
        BEGIN
            ROLLBACK TRANSACTION;
            RETURN;
        END

        -- 2. Resolver o crear Lista de Precios en prc_ListaPrecio
        DECLARE @IDListaPrecio int;
        SELECT TOP 1 @IDListaPrecio = ID FROM dbo.prc_ListaPrecio WHERE IDExtra = @ID_ListaPrecio_Origen;

        IF @IDListaPrecio IS NULL
        BEGIN
            DECLARE @IDTipoListaDefault int;
            SELECT TOP 1 @IDTipoListaDefault = ID FROM dbo.prc_TipoListaPrecio WHERE Estado = 1 ORDER BY ID;

            DECLARE @CodLista nvarchar(50) = LEFT(LTRIM(RTRIM(ISNULL(@NombreListaPrecio, 'LISTA'))) + '-' + CAST(@ID_ListaPrecio_Origen AS varchar(15)), 50);

            INSERT INTO dbo.prc_ListaPrecio (
                Codigo, Nombre, IDTipo, UsaRestriccionGrupoVentas, UsaRestriccionSucursal,
                Prioridad, Estado, FechaCreacion, FechaModificacion, IDExtra, Eliminado
            )
            VALUES (
                @CodLista, LEFT(LTRIM(RTRIM(ISNULL(@NombreListaPrecio, 'LISTA'))), 150), ISNULL(@IDTipoListaDefault, 1), 0, 0,
                100, @EstadoListaPrecio, GETDATE(), GETDATE(), @ID_ListaPrecio_Origen, 0
            );
            SET @IDListaPrecio = SCOPE_IDENTITY();
        END
        ELSE
        BEGIN
            UPDATE dbo.prc_ListaPrecio
            SET Nombre = LEFT(LTRIM(RTRIM(ISNULL(@NombreListaPrecio, Nombre))), 150),
                Estado = @EstadoListaPrecio,
                Eliminado = 0,
                FechaModificacion = GETDATE()
            WHERE ID = @IDListaPrecio
              AND (Nombre <> LEFT(LTRIM(RTRIM(ISNULL(@NombreListaPrecio, Nombre))), 150) OR ISNULL(Estado, 1) <> @EstadoListaPrecio);
        END

        -- 3. Resolver o crear Item en prc_ItemPrecioProducto (UNIQUE: IDListaPrecios, IDProducto)
        DECLARE @IDItemPrecio int;
        SELECT TOP 1 @IDItemPrecio = ID
        FROM dbo.prc_ItemPrecioProducto
        WHERE IDListaPrecios = @IDListaPrecio AND IDProducto = @IDProducto;

        DECLARE @EstadoItem bit = CASE WHEN @DesactivadoItem = 1 THEN 0 ELSE 1 END;

        IF @IDItemPrecio IS NULL
        BEGIN
            INSERT INTO dbo.prc_ItemPrecioProducto (
                IDListaPrecios, IDProducto, Desactivado, TieneDescuentoPorVale, PorcentajeDescuentoPorVale,
                Credito, Contado, Estado, FechaCreacion, FechaModificacion, IDExtra, Eliminado
            )
            VALUES (
                @IDListaPrecio, @IDProducto, @DesactivadoItem, 0, 0,
                @Credito, @Contado, @EstadoItem, GETDATE(), GETDATE(), @ID_ItemLista_Origen, 0
            );
            SET @IDItemPrecio = SCOPE_IDENTITY();
        END
        ELSE
        BEGIN
            UPDATE dbo.prc_ItemPrecioProducto
            SET Desactivado = @DesactivadoItem,
                Credito = @Credito,
                Contado = @Contado,
                Estado = @EstadoItem,
                IDExtra = ISNULL(IDExtra, @ID_ItemLista_Origen),
                Eliminado = 0,
                FechaModificacion = GETDATE()
            WHERE ID = @IDItemPrecio;
        END

        -- 4. Resolver o crear Unidad de Precio en prc_PrecioProductoUnidad
        DECLARE @IDPrecioUnidad int;
        SELECT TOP 1 @IDPrecioUnidad = ID FROM dbo.prc_PrecioProductoUnidad WHERE IDExtra = @ID_UnidadItemLista_Origen;

        IF @IDPrecioUnidad IS NULL
        BEGIN
            SELECT TOP 1 @IDPrecioUnidad = ID
            FROM dbo.prc_PrecioProductoUnidad
            WHERE IDItemPrecioProducto = @IDItemPrecio AND IDUnidad = @IDProductoUnidad;
        END

        IF @IDPrecioUnidad IS NULL
        BEGIN
            INSERT INTO dbo.prc_PrecioProductoUnidad (
                IDItemPrecioProducto, IDUnidad, Inafecto, PermitePedido,
                Estado, FechaCreacion, FechaModificacion, IDExtra
            )
            VALUES (
                @IDItemPrecio, @IDProductoUnidad, 0, @PermiteVentaUnidad,
                @PermiteVentaUnidad, GETDATE(), GETDATE(), @ID_UnidadItemLista_Origen
            );
            SET @IDPrecioUnidad = SCOPE_IDENTITY();
        END
        ELSE
        BEGIN
            UPDATE dbo.prc_PrecioProductoUnidad
            SET IDItemPrecioProducto = @IDItemPrecio,
                IDUnidad = @IDProductoUnidad,
                PermitePedido = @PermiteVentaUnidad,
                Estado = @PermiteVentaUnidad,
                IDExtra = @ID_UnidadItemLista_Origen,
                FechaModificacion = GETDATE()
            WHERE ID = @IDPrecioUnidad;
        END

        -- 5. Resolver o crear Escala de Precio en prc_PrecioEscala
        IF @Precio < 0 SET @Precio = 0;
        IF @PrecioIncImpto < 0 SET @PrecioIncImpto = 0;

        DECLARE @IDEscala int;
        -- A. Buscar primero por clave natural de negocio (Unidad del Item + Rango de Escala Desde-Hasta)
        SELECT TOP 1 @IDEscala = ID 
        FROM dbo.prc_PrecioEscala 
        WHERE IDUnidadItemListaPrecios = @IDPrecioUnidad 
          AND Desde = @Desde 
          AND Hasta = @Hasta 
          AND Eliminado = 0;

        -- B. Fallback: Buscar por IDExtra origen si no existía por rango
        IF @IDEscala IS NULL
            SELECT TOP 1 @IDEscala = ID FROM dbo.prc_PrecioEscala WHERE IDExtra = @ID_Escala_Origen;

        IF @IDEscala IS NULL
        BEGIN
            INSERT INTO dbo.prc_PrecioEscala (
                IDUnidadItemListaPrecios, Desde, Hasta, Precio, PrecioIncImpto,
                UtilidadMonto, UtilidadPorcentual, FechaUltimaModificacion,
                Esvigente, FechaCreacion, FechaModificacion, IDExtra, Eliminado
            )
            VALUES (
                @IDPrecioUnidad, @Desde, @Hasta, @Precio, @PrecioIncImpto,
                @UtilidadMonto, @UtilidadPorcentual, GETDATE(),
                @EsVigente, GETDATE(), GETDATE(), @ID_Escala_Origen, 0
            );
        END
        ELSE
        BEGIN
            UPDATE dbo.prc_PrecioEscala
            SET IDUnidadItemListaPrecios = @IDPrecioUnidad,
                Desde = @Desde,
                Hasta = @Hasta,
                Precio = @Precio,
                PrecioIncImpto = @PrecioIncImpto,
                UtilidadMonto = @UtilidadMonto,
                UtilidadPorcentual = @UtilidadPorcentual,
                Esvigente = @EsVigente,
                IDExtra = @ID_Escala_Origen,
                Eliminado = 0,
                FechaUltimaModificacion = GETDATE(),
                FechaModificacion = GETDATE()
            WHERE ID = @IDEscala;
        END

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH
END
GO
