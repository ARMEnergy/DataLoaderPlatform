-- =============================================================================
-- 004_CreateParamStore.sql
-- Key/value parameter store for loader configuration. Lives in the platform
-- database (whichever connection string Platform:LoadLogConnectionString
-- points at).
--
-- A loader whose appsettings.json value is the sentinel "SEE_DB" resolves the
-- real value at runtime by calling core.usp_GetParam. This keeps secrets
-- (API keys, passwords, connection strings) out of appsettings.json.
--
-- Additive: this script does NOT modify 001-003. Run it against the platform
-- database AFTER 001-003. Idempotent / re-runnable.
-- =============================================================================

-- ----------------------------------------------------------------------------
-- core.Param — one row per (loader, parameter). The (LoaderName, ParamName)
-- pair is the natural key; the UNIQUE constraint guarantees at most one Value
-- per pair, so usp_GetParam can be read with ExecuteScalar.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('core.Param', 'U') IS NULL
BEGIN
    CREATE TABLE core.Param
    (
        Id            INT IDENTITY(1,1) NOT NULL
                      CONSTRAINT PK_Param PRIMARY KEY,
        DateCreated   DATETIME          NOT NULL
                      CONSTRAINT DF_Param_DateCreated DEFAULT GETDATE(),
        LoaderName    VARCHAR(150)      NOT NULL,
        ParamName     VARCHAR(150)      NOT NULL,
        [Value]       NVARCHAR(4000)    NOT NULL,
        ModifiedAtUtc DATETIME2(3)      NULL,
        CONSTRAINT UQ_Param_LoaderName_ParamName UNIQUE (LoaderName, ParamName)
    );
END
GO

-- ----------------------------------------------------------------------------
-- core.usp_GetParam — resolve one config value.
--
-- Returns a single scalar result set: one column ([Value]), at most one row.
-- The UNIQUE constraint on (LoaderName, ParamName) guarantees at most one row.
-- If the (loader, param) pair is absent, no rows are returned, so the C#
-- caller's ExecuteScalar yields null -> the loader treats that as
-- "not found -> fail fast".
--
-- Never logs/prints the value (it may be a secret).
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE core.usp_GetParam
    @LoaderName VARCHAR(150),
    @ParamName  VARCHAR(150)
AS
BEGIN
    SET NOCOUNT ON;

    IF @LoaderName IS NULL OR @ParamName IS NULL
    BEGIN
        RAISERROR('core.usp_GetParam: @LoaderName and @ParamName are required.', 16, 1);
        RETURN;
    END

    SELECT [Value]
      FROM core.Param
     WHERE LoaderName = @LoaderName
       AND ParamName  = @ParamName;
END
GO
