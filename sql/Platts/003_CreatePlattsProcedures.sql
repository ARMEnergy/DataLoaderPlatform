-- =============================================================================
-- 003_CreatePlattsProcedures.sql
-- Stored procedures called by the Platts sinks.
-- The two bulk-merge procs return SELECT @@ROWCOUNT AS RecordsProcessed so the
-- platform's SqlSinkBase.ProcedureReturnsRowCount = true can read the count.
-- Procedures are drop-if-exists then CREATE (EnergyAspects convention) so the
-- script is safely re-runnable.
-- =============================================================================

-- ----------------------------------------------------------------------------
-- arm.usp_BulkMergeSymbolData
-- Merge key: (Symbol, Bate, Date, Action). MDC and SourcePath are payload.
-- Latest ActionDate wins: an existing row is updated only when
-- src.ActionDate >= tgt.ActionDate, which makes re-applying a file idempotent.
-- The sink de-dups the batch (keeping max ActionDate) before calling this.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.usp_BulkMergeSymbolData', 'P') IS NOT NULL
    DROP PROCEDURE arm.usp_BulkMergeSymbolData;
GO
CREATE PROCEDURE arm.usp_BulkMergeSymbolData
    @Records arm.SymbolDataTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    MERGE arm.SymbolData AS tgt
    USING @Records AS src
       ON tgt.Symbol   = src.Symbol
      AND tgt.Bate     = src.Bate
      AND tgt.[Date]   = src.[Date]
      AND tgt.[Action] = src.[Action]
    WHEN MATCHED AND src.ActionDate >= tgt.ActionDate THEN UPDATE SET
        [Value]       = src.[Value],
        MDC           = src.MDC,
        ActionDate    = src.ActionDate,
        SourcePath    = src.SourcePath,
        ModifiedAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (MDC, Symbol, Bate, [Date], [Action], [Value], ActionDate, SourcePath)
        VALUES (src.MDC, src.Symbol, src.Bate, src.[Date], src.[Action], src.[Value], src.ActionDate, src.SourcePath);

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- arm.usp_BulkMergeSymbol
-- Merge key: Symbol (PK). Upserts every reference column.
-- The Symbol key itself is not re-assigned on MATCH (it is the join column);
-- all other 13 columns + ModifiedAtUtc are updated. INSERT carries all 14.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.usp_BulkMergeSymbol', 'P') IS NOT NULL
    DROP PROCEDURE arm.usp_BulkMergeSymbol;
GO
CREATE PROCEDURE arm.usp_BulkMergeSymbol
    @Records arm.SymbolTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    MERGE arm.Symbol AS tgt
    USING @Records AS src
       ON tgt.Symbol = src.Symbol
    WHEN MATCHED THEN UPDATE SET
        MDC           = src.MDC,
        Trans         = src.Trans,
        Bates         = src.Bates,
        Freq          = src.Freq,
        Curr          = src.Curr,
        UOM           = src.UOM,
        [DEC]         = src.[DEC],
        Conv          = src.Conv,
        Flag          = src.Flag,
        To_UOM        = src.To_UOM,
        Earliest      = src.Earliest,
        Latest        = src.Latest,
        [Description] = src.[Description],
        ModifiedAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (MDC, Trans, Symbol, Bates, Freq, Curr, UOM, [DEC], Conv, Flag, To_UOM, Earliest, Latest, [Description])
        VALUES (src.MDC, src.Trans, src.Symbol, src.Bates, src.Freq, src.Curr, src.UOM, src.[DEC], src.Conv,
                src.Flag, src.To_UOM, src.Earliest, src.Latest, src.[Description]);

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- arm.usp_UpsertFileLog — per-file audit, merged by (Feed, SourcePath).
-- Called once per file by the sink (scalar params, no result set).
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.usp_UpsertFileLog', 'P') IS NOT NULL
    DROP PROCEDURE arm.usp_UpsertFileLog;
GO
CREATE PROCEDURE arm.usp_UpsertFileLog
    @Feed            NVARCHAR(20),
    @SourcePath      NVARCHAR(500),
    @FileName        NVARCHAR(260),
    @LastModifiedUtc DATETIME2(3),
    @SizeBytes       BIGINT,
    @RowCount        INT,
    @Status          NVARCHAR(20)
AS
BEGIN
    SET NOCOUNT ON;

    -- Validate the merge-key inputs (the sink always supplies these).
    IF @Feed IS NULL OR @SourcePath IS NULL
    BEGIN
        RAISERROR('usp_UpsertFileLog: @Feed and @SourcePath are required.', 16, 1);
        RETURN;
    END

    MERGE arm.FileLog AS tgt
    USING (SELECT @Feed AS Feed, @SourcePath AS SourcePath) AS src
       ON tgt.Feed = src.Feed AND tgt.SourcePath = src.SourcePath
    WHEN MATCHED THEN UPDATE SET
        FileName        = @FileName,
        LastModifiedUtc = @LastModifiedUtc,
        SizeBytes       = @SizeBytes,
        [RowCount]      = @RowCount,
        Status          = @Status,
        ProcessedAtUtc  = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (Feed, SourcePath, FileName, LastModifiedUtc, SizeBytes, [RowCount], Status)
        VALUES (@Feed, @SourcePath, @FileName, @LastModifiedUtc, @SizeBytes, @RowCount, @Status);
END
GO
