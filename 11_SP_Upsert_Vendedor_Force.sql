-- ==========================================================================================
-- ARCHIVO: 11_SP_Upsert_Vendedor_Force.sql
-- PROYECTO: IntegradorFlexForce
-- BASE DE DATOS: BDForceGVD
-- OBJETIVO: Procedimientos Almacenados para la sincronización completa del Módulo de Vendedores:
--           1. dbo.usp_Sync_UpsertSucursal
--           2. dbo.usp_Sync_UpsertCanalVenta
--           3. dbo.usp_Sync_UpsertJefeVenta
--           4. dbo.usp_Sync_UpsertSupervisor
--           5. dbo.usp_Sync_UpsertFuerzaVenta
--           6. dbo.usp_Sync_UpsertGrupoVentas
--           7. dbo.usp_Sync_UpsertVendedorMaestro (vnd_Vendedor + per_Persona + ctg_CatalogoVendedor)
-- ==========================================================================================

USE [BDForceGVD]
GO

-- ------------------------------------------------------------------------------------------
-- 1. SP: UPSERT DE SUCURSAL (cat_Sucursal)
-- ------------------------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE dbo.usp_Sync_UpsertSucursal
    @ID_Sucursal_Origen     int,
    @CodigoSucursal         nvarchar(50),
    @DescripcionSucursal    nvarchar(255),
    @EstadoSucursal         bit = 1
AS
BEGIN
    SET NOCOUNT ON;
    BEGIN TRY
        DECLARE @IDSucursal int;
        IF @ID_Sucursal_Origen > 0
            SELECT TOP 1 @IDSucursal = ID FROM dbo.cat_Sucursal WHERE IDExtra = @ID_Sucursal_Origen;

        IF @IDSucursal IS NULL AND LTRIM(RTRIM(@CodigoSucursal)) <> ''
            SELECT TOP 1 @IDSucursal = ID FROM dbo.cat_Sucursal WHERE Codigo = LTRIM(RTRIM(@CodigoSucursal));

        IF @IDSucursal IS NULL
        BEGIN
            INSERT INTO dbo.cat_Sucursal (Codigo, Descripcion, Estado, FechaCreacion, FechaModificacion, IDExtra)
            VALUES (
                LEFT(LTRIM(RTRIM(@CodigoSucursal)), 50),
                LEFT(LTRIM(RTRIM(ISNULL(@DescripcionSucursal, @CodigoSucursal))), 255),
                @EstadoSucursal,
                GETDATE(), GETDATE(),
                @ID_Sucursal_Origen
            );
            SET @IDSucursal = SCOPE_IDENTITY();
        END
        ELSE
        BEGIN
            UPDATE dbo.cat_Sucursal
            SET IDExtra = @ID_Sucursal_Origen,
                Descripcion = LEFT(LTRIM(RTRIM(ISNULL(@DescripcionSucursal, Descripcion))), 255),
                Estado = @EstadoSucursal,
                FechaModificacion = GETDATE()
            WHERE ID = @IDSucursal;
        END

        SELECT @IDSucursal AS IDSucursal;
    END TRY
    BEGIN CATCH
        THROW;
    END CATCH
END
GO

-- ------------------------------------------------------------------------------------------
-- 2. SP: UPSERT DE CANAL (vnd_Canal)
-- ------------------------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE dbo.usp_Sync_UpsertCanalVenta
    @CodigoCanal            nvarchar(50),
    @DescripcionCanal       nvarchar(255),
    @EstadoCanal            bit = 1
AS
BEGIN
    SET NOCOUNT ON;
    BEGIN TRY
        DECLARE @IDCanal int;
        SELECT TOP 1 @IDCanal = ID FROM dbo.vnd_Canal WHERE Codigo = LTRIM(RTRIM(@CodigoCanal));

        IF @IDCanal IS NULL
        BEGIN
            INSERT INTO dbo.vnd_Canal (Codigo, Descripcion, Estado, FechaCreacion, FechaModificacion, IDExtra)
            VALUES (
                LEFT(LTRIM(RTRIM(@CodigoCanal)), 50),
                LEFT(LTRIM(RTRIM(ISNULL(@DescripcionCanal, @CodigoCanal))), 255),
                @EstadoCanal,
                GETDATE(), GETDATE(), NULL
            );
            SET @IDCanal = SCOPE_IDENTITY();
        END
        ELSE
        BEGIN
            UPDATE dbo.vnd_Canal
            SET Descripcion = LEFT(LTRIM(RTRIM(ISNULL(@DescripcionCanal, Descripcion))), 255),
                Estado = @EstadoCanal,
                FechaModificacion = GETDATE()
            WHERE ID = @IDCanal;
        END

        SELECT @IDCanal AS IDCanal;
    END TRY
    BEGIN CATCH
        THROW;
    END CATCH
END
GO

-- ------------------------------------------------------------------------------------------
-- 3. SP: UPSERT DE JEFE DE VENTA (vnd_JefeVenta)
-- ------------------------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE dbo.usp_Sync_UpsertJefeVenta
    @CodigoJefe             nvarchar(50),
    @DescripcionJefe        nvarchar(255),
    @EstadoJefe             bit = 1
AS
BEGIN
    SET NOCOUNT ON;
    BEGIN TRY
        DECLARE @IDJefe int;
        SELECT TOP 1 @IDJefe = ID FROM dbo.vnd_JefeVenta WHERE Codigo = LTRIM(RTRIM(@CodigoJefe));

        IF @IDJefe IS NULL
        BEGIN
            INSERT INTO dbo.vnd_JefeVenta (Codigo, Descripcion, Estado, FechaCreacion, FechaModificacion, IDExtra)
            VALUES (
                LEFT(LTRIM(RTRIM(@CodigoJefe)), 50),
                LEFT(LTRIM(RTRIM(ISNULL(@DescripcionJefe, @CodigoJefe))), 255),
                @EstadoJefe,
                GETDATE(), GETDATE(), NULL
            );
            SET @IDJefe = SCOPE_IDENTITY();
        END
        ELSE
        BEGIN
            UPDATE dbo.vnd_JefeVenta
            SET Descripcion = LEFT(LTRIM(RTRIM(ISNULL(@DescripcionJefe, Descripcion))), 255),
                Estado = @EstadoJefe,
                FechaModificacion = GETDATE()
            WHERE ID = @IDJefe;
        END

        SELECT @IDJefe AS IDJefe;
    END TRY
    BEGIN CATCH
        THROW;
    END CATCH
END
GO

-- ------------------------------------------------------------------------------------------
-- 4. SP: UPSERT DE SUPERVISOR (vnd_Supervisor)
-- ------------------------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE dbo.usp_Sync_UpsertSupervisor
    @CodigoSupervisor       nvarchar(50),
    @DescripcionSupervisor  nvarchar(255),
    @EstadoSupervisor       bit = 1
AS
BEGIN
    SET NOCOUNT ON;
    BEGIN TRY
        DECLARE @IDSupervisor int;
        SELECT TOP 1 @IDSupervisor = ID FROM dbo.vnd_Supervisor WHERE Codigo = LTRIM(RTRIM(@CodigoSupervisor));

        IF @IDSupervisor IS NULL
        BEGIN
            INSERT INTO dbo.vnd_Supervisor (Codigo, Descripcion, Estado, FechaCreacion, FechaModificacion, Eliminado)
            VALUES (
                LEFT(LTRIM(RTRIM(@CodigoSupervisor)), 50),
                LEFT(LTRIM(RTRIM(ISNULL(@DescripcionSupervisor, @CodigoSupervisor))), 255),
                @EstadoSupervisor,
                GETDATE(), GETDATE(), 0
            );
            SET @IDSupervisor = SCOPE_IDENTITY();
        END
        ELSE
        BEGIN
            UPDATE dbo.vnd_Supervisor
            SET Descripcion = LEFT(LTRIM(RTRIM(ISNULL(@DescripcionSupervisor, Descripcion))), 255),
                Estado = @EstadoSupervisor,
                Eliminado = 0,
                FechaModificacion = GETDATE()
            WHERE ID = @IDSupervisor;
        END

        SELECT @IDSupervisor AS IDSupervisor;
    END TRY
    BEGIN CATCH
        THROW;
    END CATCH
END
GO

-- ------------------------------------------------------------------------------------------
-- 5. SP: UPSERT DE FUERZA DE VENTA (vnd_FuerzaVenta)
-- ------------------------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE dbo.usp_Sync_UpsertFuerzaVenta
    @ID_FuerzaVenta_Origen  int,
    @CodigoFuerzaVenta      nvarchar(50),
    @DescripcionFuerzaVenta nvarchar(255),
    @CodigoCanalCalculado   nvarchar(50),
    @EstadoFuerzaVenta      bit = 1
AS
BEGIN
    SET NOCOUNT ON;
    BEGIN TRY
        -- 1. Resolver IDCanal
        DECLARE @IDCanal int;
        SELECT TOP 1 @IDCanal = ID FROM dbo.vnd_Canal WHERE Codigo = LTRIM(RTRIM(@CodigoCanalCalculado));
        IF @IDCanal IS NULL
            SELECT TOP 1 @IDCanal = ID FROM dbo.vnd_Canal WHERE Estado = 1 ORDER BY ID;

        -- 2. Resolver IDJefeVenta (default 1)
        DECLARE @IDJefeVenta int = 1;
        IF NOT EXISTS (SELECT 1 FROM dbo.vnd_JefeVenta WHERE ID = 1)
            SELECT TOP 1 @IDJefeVenta = ID FROM dbo.vnd_JefeVenta ORDER BY ID;

        -- 3. Upsert en vnd_FuerzaVenta
        DECLARE @IDFuerza int;
        IF @ID_FuerzaVenta_Origen > 0
            SELECT TOP 1 @IDFuerza = ID FROM dbo.vnd_FuerzaVenta WHERE IDExtra = @ID_FuerzaVenta_Origen;

        IF @IDFuerza IS NULL AND LTRIM(RTRIM(@CodigoFuerzaVenta)) <> ''
            SELECT TOP 1 @IDFuerza = ID FROM dbo.vnd_FuerzaVenta WHERE Codigo = LTRIM(RTRIM(@CodigoFuerzaVenta));

        IF @IDFuerza IS NULL
        BEGIN
            INSERT INTO dbo.vnd_FuerzaVenta (
                Descripcion, IDCanal, IDJefeVenta, Estado,
                FechaCreacion, FechaModificacion, IDExtra, Eliminado
            )
            VALUES (
                LEFT(LTRIM(RTRIM(ISNULL(@DescripcionFuerzaVenta, @CodigoFuerzaVenta))), 255),
                @IDCanal, @IDJefeVenta, @EstadoFuerzaVenta,
                GETDATE(), GETDATE(), @ID_FuerzaVenta_Origen, 0
            );
            SET @IDFuerza = SCOPE_IDENTITY();
        END
        ELSE
        BEGIN
            UPDATE dbo.vnd_FuerzaVenta
            SET IDExtra = @ID_FuerzaVenta_Origen,
                Descripcion = LEFT(LTRIM(RTRIM(ISNULL(@DescripcionFuerzaVenta, Descripcion))), 255),
                IDCanal = ISNULL(@IDCanal, IDCanal),
                IDJefeVenta = ISNULL(@IDJefeVenta, IDJefeVenta),
                Estado = @EstadoFuerzaVenta,
                Eliminado = 0,
                FechaModificacion = GETDATE()
            WHERE ID = @IDFuerza;
        END

        SELECT @IDFuerza AS IDFuerzaVenta;
    END TRY
    BEGIN CATCH
        THROW;
    END CATCH
END
GO

-- ------------------------------------------------------------------------------------------
-- 6. SP: UPSERT DE GRUPO DE VENTAS (vnd_GrupoVentas)
-- ------------------------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE dbo.usp_Sync_UpsertGrupoVentas
    @ID_GrupoVenta_Origen   int,
    @CodigoGrupoVenta       nvarchar(50),
    @NombreGrupoVenta       nvarchar(255),
    @ID_FuerzaVenta_Origen  int = 0,
    @CodigoSupervisor       nvarchar(50) = '',
    @ObservacionGrupoVenta  nvarchar(255) = '',
    @ID_Sucursal_Origen     int = 0,
    @EstadoGrupoVenta       bit = 1
AS
BEGIN
    SET NOCOUNT ON;
    BEGIN TRY
        -- A. Resolver IDFuerzaVenta
        DECLARE @IDFuerzaVenta int;
        IF @ID_FuerzaVenta_Origen > 0
            SELECT TOP 1 @IDFuerzaVenta = ID FROM dbo.vnd_FuerzaVenta WHERE IDExtra = @ID_FuerzaVenta_Origen;
        IF @IDFuerzaVenta IS NULL
            SELECT TOP 1 @IDFuerzaVenta = ID FROM dbo.vnd_FuerzaVenta WHERE Estado = 1 AND Eliminado = 0 ORDER BY ID;

        -- B. Resolver IDSupervisor
        DECLARE @IDSupervisor int;
        IF LTRIM(RTRIM(@CodigoSupervisor)) <> ''
            SELECT TOP 1 @IDSupervisor = ID FROM dbo.vnd_Supervisor WHERE Codigo = LTRIM(RTRIM(@CodigoSupervisor));
        IF @IDSupervisor IS NULL
            SELECT TOP 1 @IDSupervisor = ID FROM dbo.vnd_Supervisor WHERE Estado = 1 AND Eliminado = 0 ORDER BY ID;

        -- C. Resolver IDJefeVenta
        DECLARE @IDJefeVenta int;
        DECLARE @LlaveJefe nvarchar(100) = CONCAT(REPLACE(LTRIM(RTRIM(@ObservacionGrupoVenta)), ' ', ''), 'JEFE');
        IF @LlaveJefe <> '' AND @LlaveJefe <> 'JEFE'
            SELECT TOP 1 @IDJefeVenta = ID FROM dbo.vnd_JefeVenta WHERE Codigo = @LlaveJefe;
        IF @IDJefeVenta IS NULL
            SELECT TOP 1 @IDJefeVenta = ID FROM dbo.vnd_JefeVenta WHERE ID = 1;
        IF @IDJefeVenta IS NULL
            SELECT TOP 1 @IDJefeVenta = ID FROM dbo.vnd_JefeVenta WHERE Estado = 1 ORDER BY ID;

        -- D. Resolver IDSucursal
        DECLARE @IDSucursal int;
        IF @ID_Sucursal_Origen > 0
            SELECT TOP 1 @IDSucursal = ID FROM dbo.cat_Sucursal WHERE IDExtra = @ID_Sucursal_Origen;
        IF @IDSucursal IS NULL
            SELECT TOP 1 @IDSucursal = ID FROM dbo.cat_Sucursal WHERE Estado = 1 ORDER BY ID;

        -- E. Upsert en vnd_GrupoVentas
        DECLARE @IDGrupo int;
        IF @ID_GrupoVenta_Origen > 0
            SELECT TOP 1 @IDGrupo = ID FROM dbo.vnd_GrupoVentas WHERE IDExtra = @ID_GrupoVenta_Origen;

        IF @IDGrupo IS NULL AND LTRIM(RTRIM(@CodigoGrupoVenta)) <> ''
            SELECT TOP 1 @IDGrupo = ID FROM dbo.vnd_GrupoVentas WHERE Codigo = LTRIM(RTRIM(@CodigoGrupoVenta));

        IF @IDGrupo IS NULL
        BEGIN
            INSERT INTO dbo.vnd_GrupoVentas (
                Nombre, IDFuerzaVenta, IDSupervisor, Estado,
                FechaCreacion, FechaModificacion, IDJefeVenta, IDExtra, Eliminado, IDSucursal
            )
            VALUES (
                LEFT(LTRIM(RTRIM(ISNULL(@NombreGrupoVenta, @CodigoGrupoVenta))), 255),
                @IDFuerzaVenta, @IDSupervisor, @EstadoGrupoVenta,
                GETDATE(), GETDATE(), @IDJefeVenta, @ID_GrupoVenta_Origen, 0, @IDSucursal
            );
            SET @IDGrupo = SCOPE_IDENTITY();
        END
        ELSE
        BEGIN
            UPDATE dbo.vnd_GrupoVentas
            SET IDExtra = @ID_GrupoVenta_Origen,
                Nombre = LEFT(LTRIM(RTRIM(ISNULL(@NombreGrupoVenta, Nombre))), 255),
                IDFuerzaVenta = ISNULL(@IDFuerzaVenta, IDFuerzaVenta),
                IDSupervisor = ISNULL(@IDSupervisor, IDSupervisor),
                IDJefeVenta = ISNULL(@IDJefeVenta, IDJefeVenta),
                IDSucursal = ISNULL(@IDSucursal, IDSucursal),
                Estado = @EstadoGrupoVenta,
                Eliminado = 0,
                FechaModificacion = GETDATE()
            WHERE ID = @IDGrupo;
        END

        SELECT @IDGrupo AS IDGrupoVentas;
    END TRY
    BEGIN CATCH
        THROW;
    END CATCH
END
GO

-- ------------------------------------------------------------------------------------------
-- 7. SP: UPSERT DE VENDEDOR MAESTRO (vnd_Vendedor + per_Persona + ctg_CatalogoVendedor)
-- ------------------------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE dbo.usp_Sync_UpsertVendedorMaestro
    @ID_Vendedor_Origen     int,
    @CodigoVendedor         nvarchar(50),
    @NombreCompleto         nvarchar(200),
    @NombreCorto            nvarchar(100) = NULL,
    @DocumentoIdentidad     nvarchar(20) = '0',
    @IDTipoDocIdentidad     int = 19,
    @FechaNacimiento        date = NULL,
    @Email                  nvarchar(150) = NULL,
    @Telefono               nvarchar(50) = NULL,
    @EstadoVendedor         bit = 1,
    @ID_GrupoVenta_Origen   int = 0,
    @ID_Sucursal_Origen     int = 0
AS
BEGIN
    SET NOCOUNT ON;
    BEGIN TRY
        BEGIN TRANSACTION;

        -- A. Resolver IDGrupoVenta
        DECLARE @IDGrupoVenta int;
        IF @ID_GrupoVenta_Origen > 0
            SELECT TOP 1 @IDGrupoVenta = ID FROM dbo.vnd_GrupoVentas WHERE IDExtra = @ID_GrupoVenta_Origen;
        IF @IDGrupoVenta IS NULL
            SELECT TOP 1 @IDGrupoVenta = ID FROM dbo.vnd_GrupoVentas WHERE Estado = 1 AND Eliminado = 0 ORDER BY ID;

        -- B. Buscar Vendedor existente por IDExtra o Codigo
        DECLARE @IDVendedor int;
        IF @ID_Vendedor_Origen > 0
            SELECT TOP 1 @IDVendedor = ID FROM dbo.vnd_Vendedor WHERE IDExtra = @ID_Vendedor_Origen;

        IF @IDVendedor IS NULL AND LTRIM(RTRIM(@CodigoVendedor)) <> ''
            SELECT TOP 1 @IDVendedor = ID FROM dbo.vnd_Vendedor WHERE Codigo = LTRIM(RTRIM(@CodigoVendedor));

        -- C. Insertar o Actualizar en vnd_Vendedor
        IF @IDVendedor IS NULL
        BEGIN
            INSERT INTO dbo.vnd_Vendedor (
                NombreCompleto, NombreCorto, IDTipoVendedor, DocumentoIdentidad,
                IDTipoDocIdentidad, FechaNacimiento, Sexo, Telefono, Email,
                Observacion, FechaIngreso, FechaBaja, IDEstadoLaboral, Estado,
                FechaCreacion, FechaModificacion, IDGrupoVenta, Eliminado, Codigo, IDExtra
            )
            VALUES (
                LEFT(LTRIM(RTRIM(@NombreCompleto)), 200),
                LEFT(LTRIM(RTRIM(ISNULL(@NombreCorto, @NombreCompleto))), 100),
                1, -- Vendedor Estándar
                LEFT(LTRIM(RTRIM(ISNULL(@DocumentoIdentidad, @CodigoVendedor))), 20),
                ISNULL(@IDTipoDocIdentidad, 19),
                @FechaNacimiento,
                NULL,
                NULLIF(@Telefono, ''),
                NULLIF(@Email, ''),
                NULL, NULL, NULL,
                1, -- Estado Laboral Activo
                @EstadoVendedor,
                GETDATE(), GETDATE(),
                @IDGrupoVenta,
                0,
                LEFT(LTRIM(RTRIM(@CodigoVendedor)), 50),
                @ID_Vendedor_Origen
            );
            SET @IDVendedor = SCOPE_IDENTITY();
        END
        ELSE
        BEGIN
            UPDATE dbo.vnd_Vendedor
            SET IDExtra = @ID_Vendedor_Origen,
                NombreCompleto = LEFT(LTRIM(RTRIM(@NombreCompleto)), 200),
                NombreCorto = LEFT(LTRIM(RTRIM(ISNULL(@NombreCorto, @NombreCompleto))), 100),
                DocumentoIdentidad = LEFT(LTRIM(RTRIM(ISNULL(@DocumentoIdentidad, DocumentoIdentidad))), 20),
                IDTipoDocIdentidad = ISNULL(@IDTipoDocIdentidad, IDTipoDocIdentidad),
                FechaNacimiento = CASE WHEN @FechaNacimiento IS NOT NULL THEN @FechaNacimiento ELSE FechaNacimiento END,
                Telefono = CASE WHEN @Telefono IS NOT NULL THEN @Telefono ELSE Telefono END,
                Email = CASE WHEN @Email IS NOT NULL THEN @Email ELSE Email END,
                IDGrupoVenta = ISNULL(@IDGrupoVenta, IDGrupoVenta),
                Estado = @EstadoVendedor,
                Eliminado = 0,
                FechaModificacion = GETDATE()
            WHERE ID = @IDVendedor;
        END

        -- D. SINCRONIZAR per_Persona (TipoEntidad = 'VENDEDOR')
        IF @IDVendedor IS NOT NULL
        BEGIN
            IF EXISTS (SELECT 1 FROM dbo.per_Persona WHERE IDVendedor = @IDVendedor AND TipoEntidad = 'VENDEDOR')
            BEGIN
                UPDATE dbo.per_Persona
                SET NombreCompleto    = LEFT(LTRIM(RTRIM(@NombreCompleto)), 200),
                    NombreComercial   = LEFT(LTRIM(RTRIM(ISNULL(@NombreCorto, @NombreCompleto))), 200),
                    DocIdentidad      = LEFT(LTRIM(RTRIM(ISNULL(@DocumentoIdentidad, @CodigoVendedor))), 20),
                    IDTipoDocIdentidad= ISNULL(@IDTipoDocIdentidad, 19),
                    Telefono          = CASE WHEN @Telefono IS NOT NULL THEN @Telefono ELSE Telefono END,
                    Email             = CASE WHEN @Email IS NOT NULL THEN @Email ELSE Email END,
                    Estado            = @EstadoVendedor,
                    Eliminado         = 0,
                    FechaModificacion = GETDATE()
                WHERE IDVendedor = @IDVendedor AND TipoEntidad = 'VENDEDOR';
            END
            ELSE
            BEGIN
                INSERT INTO dbo.per_Persona (
                    TipoEntidad, IDCliente, IDProveedor, IDVendedor,
                    NombreCompleto, NombreComercial,
                    DocIdentidad, IDTipoDocIdentidad,
                    Telefono, Email, Observacion,
                    Estado, FechaCreacion, FechaModificacion, Eliminado
                )
                VALUES (
                    'VENDEDOR', NULL, NULL, @IDVendedor,
                    LEFT(LTRIM(RTRIM(@NombreCompleto)), 200),
                    LEFT(LTRIM(RTRIM(ISNULL(@NombreCorto, @NombreCompleto))), 200),
                    LEFT(LTRIM(RTRIM(ISNULL(@DocumentoIdentidad, @CodigoVendedor))), 20),
                    ISNULL(@IDTipoDocIdentidad, 19),
                    NULLIF(@Telefono, ''), NULLIF(@Email, ''), NULL,
                    @EstadoVendedor, GETDATE(), GETDATE(), 0
                );
            END
        END

        -- E. VINCULAR A CATÁLOGO ACTIVO (ctg_CatalogoVendedor)
        DECLARE @IDCatalogoDef int;
        SELECT TOP 1 @IDCatalogoDef = ID FROM dbo.ctg_Catalogo WHERE ID = 1;
        IF @IDCatalogoDef IS NULL
            SELECT TOP 1 @IDCatalogoDef = ID FROM dbo.ctg_Catalogo WHERE Estado = 1 ORDER BY ID;

        IF @IDCatalogoDef IS NOT NULL AND @IDVendedor IS NOT NULL
        BEGIN
            IF NOT EXISTS (SELECT 1 FROM dbo.ctg_CatalogoVendedor WHERE IDCatalogo = @IDCatalogoDef AND IDVendedor = @IDVendedor)
            BEGIN
                INSERT INTO dbo.ctg_CatalogoVendedor (IDCatalogo, IDVendedor, Estado, FechaCreacion, FechaModificacion)
                VALUES (@IDCatalogoDef, @IDVendedor, 1, GETDATE(), GETDATE());
            END
            ELSE
            BEGIN
                UPDATE dbo.ctg_CatalogoVendedor
                SET Estado = 1, FechaModificacion = GETDATE()
                WHERE IDCatalogo = @IDCatalogoDef AND IDVendedor = @IDVendedor AND Estado = 0;
            END
        END

        COMMIT TRANSACTION;

        SELECT @IDVendedor AS IDVendedor;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH
END
GO
