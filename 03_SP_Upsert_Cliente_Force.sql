-- ==========================================================================================
-- ARCHIVO: 03_SP_Upsert_Cliente_Force.sql
-- PROYECTO: IntegradorFlexForce
-- BASE DE DATOS DESTINO: BDForceGVD
-- OBJETIVO: Insertar o actualizar en cascada un Cliente (cli_Cliente + per_Persona)
--           y su Dirección Principal (dir_Direccion + dir_EntidadDireccion)
--           respetando todas las restricciones CHECK y UNIQUE de BDForceGVD.
-- ==========================================================================================

USE [BDForceGVD]
GO

CREATE OR ALTER PROCEDURE dbo.usp_Sync_UpsertCliente
    -- Parámetros del Cliente
    @ID_Cliente_Origen          int,
    @CodigoCliente              nvarchar(50),
    @TipoPersona                nvarchar(20)  = '3',
    @TipoDocIdentidad           nvarchar(20)  = '1',
    @DocIdentidad               nvarchar(20)  = '',
    @NombreCompleto             nvarchar(255),
    @NombreComercial            nvarchar(255) = NULL,
    @PrimerNombre               nvarchar(100) = '',
    @SegundoNombre              nvarchar(100) = '',
    @ApellidoPaterno            nvarchar(100) = '',
    @ApellidoMaterno            nvarchar(100) = '',
    @CodigoTipoCliente          nvarchar(50)  = 'GENERAL',
    @CodigoCategoriaCliente     nvarchar(50)  = 'CAT_A',
    @CodigoTipoNegocio          nvarchar(50)  = '0',
    @Email                      nvarchar(150) = NULL,
    @Telefono                   nvarchar(50)  = '',
    @Contacto                   nvarchar(150) = '',
    @PuestoContacto             nvarchar(100) = '',
    @ExceptuadoPercepcion       bit           = 0,
    @Observacion                nvarchar(max) = '',
    @EstadoCliente              bit           = 1,
    
    -- Parámetros de la Dirección Principal
    @ID_Direccion_Origen        int,
    @EsPrincipal                bit           = 1,
    @CodigoTipoDireccion        nvarchar(20)  = 'FIS',
    @Direccion                  nvarchar(250) = 'SIN DIRECCION',
    @Referencia                 nvarchar(255) = '',
    @Ubigeo                     nvarchar(10)  = '150101',
    @CodigoDepartamento         nvarchar(10)  = '15',
    @CodigoProvincia            nvarchar(10)  = '01',
    @CodigoDistrito             nvarchar(10)  = '01',
    @Latitud                    decimal(10,8) = 0,
    @Longitud                   decimal(11,8) = 0,
    @EstadoDireccion            bit           = 1,
    
    -- Parámetro de salida
    @IDClienteGenerado          int OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRY
        BEGIN TRANSACTION;

        -- -------------------------------------------------------------
        -- PASO 1: RESOLUCIÓN DE CLAVES FORÁNEAS (CATÁLOGOS)
        -- -------------------------------------------------------------
        DECLARE @IDTipoPersona int;
        SELECT TOP 1 @IDTipoPersona = ID FROM cat_TipoPersona WHERE Codigo = @TipoPersona OR Descripcion LIKE @TipoPersona + '%';
        IF @IDTipoPersona IS NULL SELECT TOP 1 @IDTipoPersona = ID FROM cat_TipoPersona WHERE Estado = 1;

        DECLARE @IDTipoDocIdentidad int;
        SELECT TOP 1 @IDTipoDocIdentidad = ID FROM cat_TipoDocIdentidad WHERE Codigo = @TipoDocIdentidad OR Abreviacion = @TipoDocIdentidad;
        IF @IDTipoDocIdentidad IS NULL SELECT TOP 1 @IDTipoDocIdentidad = ID FROM cat_TipoDocIdentidad WHERE Estado = 1;

        -- RESOLVER / AUTO-CREAR CATEGORIA CLIENTE (cat_CategoriaCliente)
        DECLARE @IDCategoriaCliente int;
        SELECT TOP 1 @IDCategoriaCliente = ID 
        FROM cat_CategoriaCliente 
        WHERE IDExtra = TRY_CAST(@CodigoCategoriaCliente AS int) 
           OR Codigo = @CodigoCategoriaCliente;

        IF @IDCategoriaCliente IS NULL AND @CodigoCategoriaCliente IS NOT NULL
        BEGIN
            INSERT INTO cat_CategoriaCliente (Codigo, Descripcion, Estado, FechaCreacion, FechaModificacion, IDExtra)
            VALUES (@CodigoCategoriaCliente, 'CATEGORIA ' + @CodigoCategoriaCliente, 1, GETDATE(), GETDATE(), TRY_CAST(@CodigoCategoriaCliente AS int));
            SET @IDCategoriaCliente = SCOPE_IDENTITY();
        END
        IF @IDCategoriaCliente IS NULL SELECT TOP 1 @IDCategoriaCliente = ID FROM cat_CategoriaCliente WHERE Estado = 1;

        -- RESOLVER / AUTO-CREAR TIPO DE CLIENTE (cat_TipoCliente)
        DECLARE @IDTipoCliente int;
        SELECT TOP 1 @IDTipoCliente = ID 
        FROM cat_TipoCliente 
        WHERE IDExtra = TRY_CAST(@CodigoTipoCliente AS int) 
           OR Codigo = @CodigoTipoCliente;

        IF @IDTipoCliente IS NULL AND @CodigoTipoCliente IS NOT NULL
        BEGIN
            INSERT INTO cat_TipoCliente (Codigo, Descripcion, Estado, FechaCreacion, FechaModificacion, IDExtra)
            VALUES (@CodigoTipoCliente, 'TIPO CLIENTE ' + @CodigoTipoCliente, 1, GETDATE(), GETDATE(), TRY_CAST(@CodigoTipoCliente AS int));
            SET @IDTipoCliente = SCOPE_IDENTITY();
        END
        IF @IDTipoCliente IS NULL SELECT TOP 1 @IDTipoCliente = ID FROM cat_TipoCliente WHERE Estado = 1;

        -- RESOLVER / AUTO-CREAR TIPO DE NEGOCIO (cat_TipoNegocio)
        DECLARE @IDTipoNegocio int;
        SELECT TOP 1 @IDTipoNegocio = ID 
        FROM cat_TipoNegocio 
        WHERE IDExtra = TRY_CAST(@CodigoTipoNegocio AS int) 
           OR Codigo = @CodigoTipoNegocio;

        IF @IDTipoNegocio IS NULL AND @CodigoTipoNegocio IS NOT NULL AND @CodigoTipoNegocio <> '0'
        BEGIN
            INSERT INTO cat_TipoNegocio (Codigo, Descripcion, Estado, FechaCreacion, FechaModificacion, IDExtra)
            VALUES (@CodigoTipoNegocio, 'GIRO ' + @CodigoTipoNegocio, 1, GETDATE(), GETDATE(), TRY_CAST(@CodigoTipoNegocio AS int));
            SET @IDTipoNegocio = SCOPE_IDENTITY();
        END
        IF @IDTipoNegocio IS NULL SELECT TOP 1 @IDTipoNegocio = ID FROM cat_TipoNegocio WHERE Estado = 1;

        DECLARE @IDTipoDireccion int;
        SELECT TOP 1 @IDTipoDireccion = ID FROM cat_TipoDireccion WHERE Codigo = @CodigoTipoDireccion;
        IF @IDTipoDireccion IS NULL SELECT TOP 1 @IDTipoDireccion = ID FROM cat_TipoDireccion WHERE Estado = 1;

        -- -------------------------------------------------------------
        -- REGLA EMAIL (Cumplir estrictamente CHK_Cliente_Email: '%_@__%.__%')
        -- -------------------------------------------------------------
        SET @Email = LTRIM(RTRIM(@Email));
        IF @Email = '' OR @Email NOT LIKE '%_@__%.__%' OR @Email LIKE '% %'
        BEGIN
            SET @Email = NULL;
        END

        -- -------------------------------------------------------------
        -- REGLA DOCIDENTIDAD: Manejo de vacíos y unicidad (UQ_Cliente_DocIdentidad)
        -- -------------------------------------------------------------
        SET @DocIdentidad = LTRIM(RTRIM(ISNULL(@DocIdentidad, '')));
        IF @DocIdentidad IN ('', '0', '00', '00000000', '-', '.', 'SD')
        BEGIN
            SET @DocIdentidad = 'SD-' + CAST(@ID_Cliente_Origen AS varchar(16));
        END

        -- -------------------------------------------------------------
        -- PASO 2: INSERT O UPDATE EN cli_Cliente (POR IDExtra, Codigo O DocIdentidad)
        -- -------------------------------------------------------------
        DECLARE @IDCliente int;
        SELECT TOP 1 @IDCliente = ID 
        FROM cli_Cliente 
        WHERE IDExtra = @ID_Cliente_Origen OR Codigo = @CodigoCliente;

        -- Si no se encontró por IDExtra ni por Código, verificar si ya existe este DocIdentidad en Force
        IF @IDCliente IS NULL AND @DocIdentidad NOT LIKE 'SD-%'
        BEGIN
            SELECT TOP 1 @IDCliente = ID 
            FROM cli_Cliente 
            WHERE DocIdentidad = @DocIdentidad;
        END

        IF @IDCliente IS NULL
        BEGIN
            INSERT INTO cli_Cliente (
                IDTipoPersona, IDTipoDocIdentidad, DocIdentidad,
                ApellidoPaterno, ApellidoMaterno, PrimerNombre, SegundoNombre,
                NombreCompleto, NombreComercial, Codigo,
                IDTipoCliente, IDCategoriaCliente, IDTipoNegocio,
                Email, Exceptuadopercepcion, Observacion,
                Estado, FechaCreacion, FechaModificacion, Eliminado, IDExtra
            )
            VALUES (
                @IDTipoPersona, @IDTipoDocIdentidad, @DocIdentidad,
                @ApellidoPaterno, @ApellidoMaterno, @PrimerNombre, @SegundoNombre,
                @NombreCompleto, @NombreComercial, @CodigoCliente,
                @IDTipoCliente, @IDCategoriaCliente, @IDTipoNegocio,
                @Email, @ExceptuadoPercepcion, @Observacion,
                @EstadoCliente, GETDATE(), GETDATE(), 0, @ID_Cliente_Origen
            );

            SET @IDCliente = SCOPE_IDENTITY();
        END
        ELSE
        BEGIN
            -- Si el nuevo DocIdentidad ya le pertenece a OTRO IDCliente distinto en Force,
            -- mantenemos el DocIdentidad actual de este registro para no violar UQ_Cliente_DocIdentidad
            DECLARE @DocIdentidadSeguro nvarchar(20) = @DocIdentidad;
            IF EXISTS (SELECT 1 FROM cli_Cliente WHERE DocIdentidad = @DocIdentidad AND ID <> @IDCliente)
            BEGIN
                SELECT @DocIdentidadSeguro = DocIdentidad FROM cli_Cliente WHERE ID = @IDCliente;
            END

            UPDATE cli_Cliente
            SET 
                IDTipoPersona        = ISNULL(@IDTipoPersona, IDTipoPersona),
                IDTipoDocIdentidad   = ISNULL(@IDTipoDocIdentidad, IDTipoDocIdentidad),
                DocIdentidad         = @DocIdentidadSeguro,
                ApellidoPaterno      = CASE WHEN @ApellidoPaterno <> '' THEN @ApellidoPaterno ELSE ApellidoPaterno END,
                ApellidoMaterno      = CASE WHEN @ApellidoMaterno <> '' THEN @ApellidoMaterno ELSE ApellidoMaterno END,
                PrimerNombre         = CASE WHEN @PrimerNombre <> '' THEN @PrimerNombre ELSE PrimerNombre END,
                NombreCompleto       = @NombreCompleto,
                NombreComercial      = ISNULL(@NombreComercial, NombreComercial),
                IDTipoCliente        = ISNULL(@IDTipoCliente, IDTipoCliente),
                IDCategoriaCliente   = ISNULL(@IDCategoriaCliente, IDCategoriaCliente),
                IDTipoNegocio        = ISNULL(@IDTipoNegocio, IDTipoNegocio),
                Email                = CASE WHEN @Email IS NOT NULL THEN @Email ELSE Email END,
                Exceptuadopercepcion = @ExceptuadoPercepcion,
                Observacion          = ISNULL(@Observacion, Observacion),
                Estado               = @EstadoCliente,
                FechaModificacion    = GETDATE(),
                IDExtra              = ISNULL(IDExtra, @ID_Cliente_Origen)
            WHERE ID = @IDCliente;
        END

        SET @IDClienteGenerado = @IDCliente;

        -- -------------------------------------------------------------
        -- PASO 2.1: SINCRONIZAR TABLA per_Persona (TipoEntidad = 'CLIENTE')
        -- -------------------------------------------------------------
        IF EXISTS (SELECT 1 FROM per_Persona WHERE IDCliente = @IDCliente AND TipoEntidad = 'CLIENTE')
        BEGIN
            UPDATE per_Persona
            SET 
                NombreCompleto     = @NombreCompleto,
                NombreComercial    = @NombreComercial,
                IDTipoDocIdentidad = @IDTipoDocIdentidad,
                Email              = CASE WHEN @Email IS NOT NULL THEN @Email ELSE Email END,
                Observacion        = LEFT(ISNULL(@Observacion, ''), 500),
                Estado             = @EstadoCliente,
                FechaModificacion  = GETDATE()
            WHERE IDCliente = @IDCliente AND TipoEntidad = 'CLIENTE';
        END
        ELSE
        BEGIN
            INSERT INTO per_Persona (
                TipoEntidad, IDCliente, NombreCompleto, NombreComercial,
                DocIdentidad, IDTipoDocIdentidad, Telefono, Email,
                Observacion, Estado, FechaCreacion, FechaModificacion, Eliminado
            )
            VALUES (
                'CLIENTE', @IDCliente, @NombreCompleto, @NombreComercial,
                @DocIdentidad, @IDTipoDocIdentidad, NULLIF(@Telefono, ''), @Email,
                LEFT(ISNULL(@Observacion, ''), 500), @EstadoCliente, GETDATE(), GETDATE(), 0
            );
        END

        -- -------------------------------------------------------------
        -- PASO 3: INSERT O UPDATE EN dir_Direccion (POR IDExtra)
        -- -------------------------------------------------------------
        DECLARE @IDDireccion int;
        SELECT TOP 1 @IDDireccion = ID 
        FROM dir_Direccion 
        WHERE IDExtra = @ID_Direccion_Origen;

        -- Resolver IDs Geográficos
        DECLARE @IDDpto int, @IDProv int, @IDDist int;
        SELECT TOP 1 @IDDpto = ID FROM cat_Departamento WHERE Codigo = @CodigoDepartamento;
        SELECT TOP 1 @IDProv = ID FROM cat_Provincia WHERE Codigo = @CodigoProvincia AND IDDepartamento = @IDDpto;
        SELECT TOP 1 @IDDist = ID FROM cat_Distrito WHERE Codigo = @CodigoDistrito AND IDProvincia = @IDProv;

        IF @IDDireccion IS NULL
        BEGIN
            INSERT INTO dir_Direccion (
                IDTipoDireccion, Direccion, Referencia, Ubigeo,
                IDPais, IDDepartamento, IDProvincia, IDDistrito,
                Latitud, Longitud, Estado, FechaCreacion, FechaModificacion,
                Eliminado, IDExtra
            )
            VALUES (
                @IDTipoDireccion, @Direccion, @Referencia, @Ubigeo,
                1, @IDDpto, @IDProv, @IDDist,
                @Latitud, @Longitud, @EstadoDireccion, GETDATE(), GETDATE(),
                0, @ID_Direccion_Origen
            );

            SET @IDDireccion = SCOPE_IDENTITY();
        END
        ELSE
        BEGIN
            UPDATE dir_Direccion
            SET 
                IDTipoDireccion   = @IDTipoDireccion,
                Direccion         = @Direccion,
                Referencia        = @Referencia,
                Ubigeo            = @Ubigeo,
                IDDepartamento    = ISNULL(@IDDpto, IDDepartamento),
                IDProvincia       = ISNULL(@IDProv, IDProvincia),
                IDDistrito        = ISNULL(@IDDist, IDDistrito),
                Latitud           = @Latitud,
                Longitud          = @Longitud,
                Estado            = @EstadoDireccion,
                FechaModificacion = GETDATE()
            WHERE ID = @IDDireccion;
        END

        -- -------------------------------------------------------------
        -- PASO 4: VINCULACIÓN EN dir_EntidadDireccion (TipoEntidad = 'CLIENTE')
        -- NOTA: BDForceGVD tiene el índice único UQ_EntidadDireccion_Principal
        -- que restringe a un cliente a tener MÁXIMO UNA dirección con EsPrincipal = 1.
        -- Si esta dirección es principal (@EsPrincipal = 1), apagamos cualquier otra previa.
        -- -------------------------------------------------------------
        IF @EsPrincipal = 1
        BEGIN
            UPDATE dir_EntidadDireccion
            SET EsPrincipal = 0, FechaModificacion = GETDATE()
            WHERE TipoEntidad = 'CLIENTE' 
              AND IDEntidad = @IDCliente 
              AND IDDireccion <> @IDDireccion 
              AND EsPrincipal = 1;
        END

        DECLARE @IDEntidadDireccion int;
        SELECT TOP 1 @IDEntidadDireccion = ID 
        FROM dir_EntidadDireccion 
        WHERE TipoEntidad = 'CLIENTE' AND IDEntidad = @IDCliente AND IDDireccion = @IDDireccion;

        IF @IDEntidadDireccion IS NULL
        BEGIN
            INSERT INTO dir_EntidadDireccion (
                TipoEntidad, IDEntidad, IDDireccion, EsPrincipal, Estado,
                FechaCreacion, FechaModificacion, IDExtra
            )
            VALUES (
                'CLIENTE', @IDCliente, @IDDireccion, @EsPrincipal, @EstadoDireccion,
                GETDATE(), GETDATE(), @ID_Direccion_Origen
            );

            SET @IDEntidadDireccion = SCOPE_IDENTITY();
        END
        ELSE
        BEGIN
            UPDATE dir_EntidadDireccion
            SET 
                EsPrincipal       = @EsPrincipal,
                Estado            = @EstadoDireccion,
                FechaModificacion = GETDATE(),
                IDExtra           = @ID_Direccion_Origen
            WHERE ID = @IDEntidadDireccion;
        END

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH
END
GO
