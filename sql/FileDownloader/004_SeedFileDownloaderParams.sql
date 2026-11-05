/*
    FileDownloader loader - 004 - seed core.Param credentials.

    Target database: DataLoaderPlatform  (NOT Mako - 001-003 target Mako; core.Param
    lives in the platform database alongside core.LoadLog and core.LoaderRun).

    ============================================================================
    *** DO NOT COMMIT REAL VALUES INTO THIS FILE. ***
    Fill the placeholders in your SSMS session, run it, then close the window
    WITHOUT saving. The guard below refuses to run while a placeholder is
    unchanged, so a half-filled script cannot write a literal like
    '<SPP_FTP_PASSWORD>' into core.Param and make a loader fail with a password
    that looks almost right.
    ============================================================================

    WHY ONLY TWO ROWS

    The standalone etl-tools/FileDownloader carries four hardcoded credential pairs.
    Verified 2026-11-05 by comparing SHA2_256 of each literal against
    HASHBYTES('SHA2_256', Value) of the existing rows (values never displayed):

      Program.cs L123-124  Platts SFTP user/pass  == core.Param Platts/SftpUsername,
                                                     Platts/SftpPassword      IDENTICAL
      Program.cs L403-404  Argus  FTP user/pass   == core.Param Argus/Username,
                                                     Argus/Password           IDENTICAL

    FileDownloader uses the SAME vendor accounts as this platform's own Platts and
    Argus loaders. Those four secrets are therefore already in core.Param and must not
    be duplicated here - see the OPTIONAL block at the bottom for why, and for what to
    do if you choose duplication anyway.

    Two literals have no existing row and genuinely need one:

      Program.cs L328  SPP FTP password          -> FileDownloader/SppFtpPassword
      Program.cs L77   Wells Fargo SFTP username -> FileDownloader/WellsFargoSftpUsername

    Two more are NOT secrets and belong in appsettings.json, not here:

      Program.cs L327  SPP FTP username = the literal 'anonymous'
      Program.cs L79   Wells Fargo private-key PATH. The path is not a secret; the key
                       FILE it names is, and it stays on the share under ACL.

    ROTATE FIRST. All of these appear in 7 commits of the etl-tools GitLab history, so
    they are already disclosed. Seeding them unrotated moves a known secret rather than
    protecting it. Rotate at the vendor, then seed the NEW values here - and remember the
    Platts/Argus rotation must also update their existing rows, which this platform's
    live Platts and Argus loaders depend on.
*/

USE DataLoaderPlatform;
GO

SET ANSI_NULLS ON;
GO
SET QUOTED_IDENTIFIER ON;
GO
SET XACT_ABORT ON;
GO

DECLARE @SppFtpPassword           NVARCHAR(4000) = N'<SPP_FTP_PASSWORD>';
DECLARE @WellsFargoSftpUsername   NVARCHAR(4000) = N'<WELLS_FARGO_SFTP_USERNAME>';

-- Fail closed. An unfilled placeholder must never reach core.Param: the loader would
-- authenticate with a plausible-looking string and the vendor would answer with a
-- generic auth failure, which is a slow thing to diagnose.
IF @SppFtpPassword         LIKE N'<%>' COLLATE Latin1_General_BIN2
OR @WellsFargoSftpUsername LIKE N'<%>' COLLATE Latin1_General_BIN2
    THROW 50010, 'Placeholder value still present - fill every placeholder before running.', 1;

IF @SppFtpPassword = N'' OR @WellsFargoSftpUsername = N''
    THROW 50011, 'Empty credential - core.Param.Value is NOT NULL and a blank value is never correct.', 1;

BEGIN TRANSACTION;

    /*
        MERGE, not INSERT: re-running after a rotation must update in place. A second
        row for the same (LoaderName, ParamName) would violate the unique constraint
        that lets core.usp_GetParam return at most one row.
    */
    MERGE core.Param AS target
    USING
    (
        VALUES
            ('FileDownloader', 'SppFtpPassword',         @SppFtpPassword),
            ('FileDownloader', 'WellsFargoSftpUsername', @WellsFargoSftpUsername)
    ) AS source (LoaderName, ParamName, [Value])
        ON  target.LoaderName = source.LoaderName
        AND target.ParamName  = source.ParamName

    WHEN MATCHED AND target.[Value] <> source.[Value] THEN
        UPDATE SET target.[Value] = source.[Value],
                   target.ModifiedAtUtc = SYSUTCDATETIME()

    WHEN NOT MATCHED BY TARGET THEN
        INSERT (DateCreated, LoaderName, ParamName, [Value], ModifiedAtUtc)
        VALUES (GETDATE(), source.LoaderName, source.ParamName, source.[Value], SYSUTCDATETIME());

COMMIT TRANSACTION;
GO

/*
    Verification. Names and lengths only - never select Value, because a result grid is
    saved in .sql session files, screenshotted and pasted into tickets.
*/
SELECT
    LoaderName,
    ParamName,
    ValueLength   = LEN([Value]),
    ModifiedAtUtc
FROM core.Param
WHERE LoaderName = 'FileDownloader'
ORDER BY ParamName;
GO

/*
    ------------------------------------------------------------------------------
    OPTIONAL - and NOT recommended. Only if you decide FileDownloader should carry
    its own copies of the Platts and Argus credentials rather than sharing the rows
    the Platts and Argus loaders already use.

    The platform's SEE_DB mechanism resolves a setting by calling
    core.usp_GetParam(<LoaderId>, <PropertyName>), so a loader can only read its OWN
    rows: 'FileDownloader' cannot read 'Platts/SftpPassword'. Duplication is the
    zero-code way to give it access.

    The cost is a silent-divergence trap: one vendor password would then live in two
    rows, and a rotation that updates only one leaves the other authenticating with a
    dead credential until that loader next runs. The better fix is to let the sentinel
    name a source - SEE_DB:Platts:SftpPassword - which keeps one row per secret; that
    is a small additive change to SeeDbSettingsResolver, not a schema change.

    MERGE core.Param AS target
    USING
    (
        SELECT 'FileDownloader', 'PlattsSftpUsername', [Value] FROM core.Param WHERE LoaderName='Platts' AND ParamName='SftpUsername'
        UNION ALL
        SELECT 'FileDownloader', 'PlattsSftpPassword', [Value] FROM core.Param WHERE LoaderName='Platts' AND ParamName='SftpPassword'
        UNION ALL
        SELECT 'FileDownloader', 'ArgusFtpUsername',   [Value] FROM core.Param WHERE LoaderName='Argus'  AND ParamName='Username'
        UNION ALL
        SELECT 'FileDownloader', 'ArgusFtpPassword',   [Value] FROM core.Param WHERE LoaderName='Argus'  AND ParamName='Password'
    ) AS source (LoaderName, ParamName, [Value])
        ON target.LoaderName = source.LoaderName AND target.ParamName = source.ParamName
    WHEN MATCHED AND target.[Value] <> source.[Value] THEN
        UPDATE SET target.[Value] = source.[Value], target.ModifiedAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (DateCreated, LoaderName, ParamName, [Value], ModifiedAtUtc)
        VALUES (GETDATE(), source.LoaderName, source.ParamName, source.[Value], SYSUTCDATETIME());

    (Copying row-to-row like this never displays a value, which is why it is written
    as a SELECT from the existing rows rather than as typed literals.)
    ------------------------------------------------------------------------------
*/
