namespace DataLoader.NUCORE;

/// <summary>How an endpoint's work is divided into resumable units.</summary>
internal enum NucorePartition
{
    /// <summary>
    /// One unit per business DATE, driven by the endpoint's own date parameter. Four of the
    /// five endpoints work this way.
    /// </summary>
    Date,

    /// <summary>
    /// One unit per TRADE MONTH.
    ///
    /// <para>
    /// Only <c>PositionDetails</c>, and not by preference: that endpoint has NO date
    /// parameter at all (the vendor's OpenAPI document lists only <c>TradeMonthFrom</c>,
    /// <c>TradeMonthTo</c>, <c>TradeStatus</c>, <c>Page</c>, <c>PageSize</c> and
    /// <c>SortFields</c>), so a day cannot be asked for. An unfiltered call returns the
    /// entire history - 2,468,453 rows and roughly 10 GB of JSON - and answers HTTP 504 at
    /// <c>pageSize=500000</c>.
    /// </para>
    /// <para>
    /// Trade month is the only partition the vendor offers, and it is LOSSLESS: the
    /// 2025..2031 yearly census summed to exactly 2,468,453, the unfiltered total. Combined
    /// with <c>SortFields=-systemDate</c> and a per-trade-month watermark, this turns a
    /// 10 GB full refresh into an incremental read of whatever system dates are new.
    /// </para>
    /// </summary>
    TradeMonth
}

/// <summary>
/// How an endpoint's rows are written.
///
/// <para>
/// âš  Which mode an endpoint gets is decided by EVIDENCE. A key is used only where it was
/// shown unique over COMPLETE populations - whole days, whole trade months - never over a
/// sample. Four of the five endpoints were originally given natural keys on the strength of
/// one sample each; for <c>PositionDetails</c> that sample was the first 100,000 rows of a
/// trade-ordered response, covering only 130 distinct trades, and the key it "verified"
/// failed in production with a primary key violation. Re-verified properly, two of the four
/// lost their keys. Do not reinstate a key without redoing that verification.
/// </para>
/// </summary>
internal enum NucoreWriteMode
{
    /// <summary>
    /// Natural primary key; rows are MERGEd, so <c>ModifiedAtUtc</c> tracks real change.
    ///
    /// <para>
    /// <c>VarResults</c> (unique over 7 complete days) and <c>Volatilities</c> (unique over
    /// 5 complete days totalling 712,004 rows).
    /// </para>
    /// </summary>
    Merge,

    /// <summary>
    /// Delete every row for the dates in the batch, then insert. The unit is one date, so
    /// the whole unit is written as ONE batch.
    ///
    /// <para>
    /// <c>PositionSummary</c>: no natural key exists - a 30-column candidate including
    /// measures still left 6 collisions in a 13,555-row day.
    /// </para>
    /// <para>
    /// <c>PnlExplain</c>: its 5-column key is unique on most days but not all - 2025-10-15
    /// carries 19 byte-identical copies of one row. Those duplicates are exact, so
    /// de-duplicating would be lossless today, but that was observed on one day and betting
    /// on it is the mistake that produced the PositionDetails outage.
    /// </para>
    /// </summary>
    ReplaceByDate,

    /// <summary>
    /// Delete every row for the (scope) tuples in the batch, then insert - where the scope
    /// is wider than a date because the unit is not a date.
    ///
    /// <para>
    /// <c>PositionDetails</c> only. Its unit is a trade month spanning many system dates, so
    /// rows are replaced per <c>(TradeMonth, SystemDate)</c> and batches are flushed only on
    /// a <c>SystemDate</c> boundary, so a scope is never split across two
    /// delete-and-insert calls.
    /// </para>
    /// <para>
    /// No key could rescue this one. Over five complete trade months (443,477 rows) the
    /// original 4-column key had 45,151 duplicates; adding <c>specId</c> left 2,189; adding
    /// <c>priceType</c> and <c>settlementIndex</c> left 129; adding <c>productId</c> changed
    /// nothing. The survivors are NOT exact duplicates - one group holds 17 distinct
    /// settlement rows at 17 different contract prices, differing only in measures, so
    /// de-duplicating would destroy real realized P&amp;L.
    /// </para>
    /// </summary>
    ReplaceByScope
}

/// <summary>
/// Everything that distinguishes one NUCORE endpoint from another. One descriptor, one
/// table, one pipeline - so adding an endpoint is a descriptor plus its SQL rather than
/// new plumbing.
/// </summary>
/// <param name="SortFields">
/// The <c>SortFields</c> value sent on EVERY request for this endpoint.
///
/// <para>
/// âš  Load-bearing, and not an optimisation. Paging this API without a sort is NOT stable:
/// pages 1 and 2 of one Volatilities day, fetched unsorted at <c>pageSize=20000</c>, shared
/// <b>111 rows</b> - which also means ~111 rows were never returned at all. With a sort the
/// same two pages overlap by zero. An unsorted paged read therefore silently duplicates
/// some rows and silently loses others.
/// </para>
/// <para>
/// Each value is the most discriminating column list the endpoint offers, so the server's
/// ordering is as close to a total order as the data allows. Ties remain theoretically
/// possible, which is why <see cref="NucoreSourceReader"/> additionally reconciles the rows
/// it read against the envelope's own <c>totalRecords</c>.
/// </para>
/// </param>
internal sealed record NucoreEndpoint(
    string Name,
    string Path,
    string? DateParam,
    string DateColumn,
    NucorePartition Partition,
    NucoreWriteMode WriteMode,
    IReadOnlyList<NucoreColumn> Columns,
    IReadOnlyList<string> ScopeColumns,
    string SortFields)
{
    public string TableName => $"dbo.{Name}";
    public string TvpType   => $"dbo.{Name}Type";
    public string MergeProc => $"dbo.usp_Merge{Name}";

    /// <summary>
    /// The key columns in PRIMARY KEY order, which is NOT the column list's order - see
    /// <see cref="NucoreColumn.KeyOrdinal"/>. Empty for
    /// <see cref="NucoreWriteMode.ReplaceByDate"/>.
    /// </summary>
    public IReadOnlyList<NucoreColumn> KeyColumns { get; } =
        Columns.Where(c => c.IsKey).OrderBy(c => c.KeyOrdinal).ToArray();

    /// <summary>
    /// Index of the column carrying this endpoint's own date, used to verify that the
    /// vendor echoed back the date that was asked for. -1 if absent, which would be a bug.
    /// </summary>
    public int DateColumnIndex { get; } =
        IndexOfColumn(Columns, DateColumn);

    /// <summary>Index of <c>TradeMonth</c>, used by the trade-month partition. -1 if absent.</summary>
    public int TradeMonthColumnIndex { get; } =
        IndexOfColumn(Columns, "TradeMonth");

    /// <summary>True when rows are replaced rather than merged.</summary>
    public bool IsReplaceMode =>
        WriteMode is NucoreWriteMode.ReplaceByDate or NucoreWriteMode.ReplaceByScope;

    /// <summary>
    /// Column indexes of the replace scope, in declaration order. Empty for
    /// <see cref="NucoreWriteMode.Merge"/>.
    ///
    /// <para>
    /// The reader uses these to build a scope token per row, so it can guarantee a scope is
    /// never split across two delete-and-insert calls - the one assumption the generated
    /// procedures rely on.
    /// </para>
    /// </summary>
    public IReadOnlyList<int> ScopeColumnIndexes { get; } =
        ScopeColumns.Select(s => IndexOfColumn(Columns, s)).ToArray();

    private static int IndexOfColumn(IReadOnlyList<NucoreColumn> columns, string name)
    {
        for (var i = 0; i < columns.Count; i++)
            if (string.Equals(columns[i].Name, name, StringComparison.Ordinal))
                return i;
        return -1;
    }
}

/// <summary>
/// The five endpoints this loader reads.
///
/// <para>
/// The column lists come from <see cref="NucoreColumns"/>, which is GENERATED from the
/// vendor's own OpenAPI document. What lives here is the part that is a decision rather
/// than a fact: the URL path, the date parameter's real name, how work is partitioned and
/// how rows are written. Those must match the matching block in
/// <c>scripts/Generate-NucoreSchema.ps1</c>, and <c>NucoreDescriptorTests</c> asserts they
/// do.
/// </para>
/// </summary>
internal static class NucoreDescriptors
{
    public const string VarResults      = "VarResults";
    public const string PositionSummary = "PositionSummary";
    public const string PnlExplain      = "PnlExplain";
    public const string PositionDetails = "PositionDetails";
    public const string Volatilities    = "Volatilities";

    public static readonly IReadOnlyList<NucoreEndpoint> All = new[]
    {
        new NucoreEndpoint(
            Name:       VarResults,
            Path:       "/Integration/api/risk/VarResults/Get",
            DateParam:  "VarDate",
            DateColumn: "VarDate",
            Partition:  NucorePartition.Date,
            WriteMode:  NucoreWriteMode.Merge,
            Columns:    NucoreColumns.VarResults,
            ScopeColumns: Array.Empty<string>(),
            SortFields: "varDate,varBucket,legalEntity,confidenceLevelId,holdingPeriod,tradeBook,tradeMonth,productSpec"),

        new NucoreEndpoint(
            Name:       PositionSummary,
            Path:       "/Integration/api/risk/PositionSummary/Get",
            DateParam:  "SystemDate",
            DateColumn: "SystemDate",
            Partition:  NucorePartition.Date,
            WriteMode:  NucoreWriteMode.ReplaceByDate,
            Columns:    NucoreColumns.PositionSummary,
            ScopeColumns: new[] { "SystemDate" },
            SortFields: "systemDate,tradeId,tradeMonth,productSpec,settlementIndex"),

        new NucoreEndpoint(
            Name:       PnlExplain,
            Path:       "/Integration/api/risk/PnlExplain/Get",
            // âš  NOT "SystemDate". The endpoint ACCEPTS systemDate, returns HTTP 200, and
            // silently ignores it - the same latest run (curDate 2026-10-15, 11,376 rows)
            // came back for 2026-10-14, 2025-10-15 and 2024-01-10 alike. Identical
            // totalRecords across unrelated dates is the only tell. docs/apis/NUCORE.md Â§2.1.
            DateParam:  "CurDate",
            DateColumn: "CurDate",
            Partition:  NucorePartition.Date,
            // âš  Was Merge until 2026-10-18. See NucoreWriteMode.ReplaceByDate.
            WriteMode:  NucoreWriteMode.ReplaceByDate,
            Columns:    NucoreColumns.PnlExplain,
            ScopeColumns: new[] { "CurDate" },
            SortFields: "curDate,tradeId,detailId,tradeMonth,productSpec"),

        new NucoreEndpoint(
            Name:       PositionDetails,
            Path:       "/Integration/api/risk/PositionDetails/Get",
            // âš  There is NO date parameter. See NucorePartition.TradeMonth.
            DateParam:  null,
            DateColumn: "SystemDate",
            Partition:  NucorePartition.TradeMonth,
            // âš  Was Merge until 2026-10-18, when it failed in production with a primary key
            // violation. See NucoreWriteMode.ReplaceByScope.
            WriteMode:  NucoreWriteMode.ReplaceByScope,
            Columns:    NucoreColumns.PositionDetails,
            ScopeColumns: new[] { "TradeMonth", "SystemDate" },
            // Leading -systemDate is what lets the watermark end the unit early.
            SortFields: "-systemDate,tradeId,detailId,tradeMonth"),

        new NucoreEndpoint(
            Name:       Volatilities,
            Path:       "/Integration/api/risk/Volatilities/Get",
            DateParam:  "SystemDate",
            DateColumn: "SystemDate",
            Partition:  NucorePartition.Date,
            WriteMode:  NucoreWriteMode.Merge,
            Columns:    NucoreColumns.Volatilities,
            ScopeColumns: Array.Empty<string>(),
            SortFields: "systemDate,productSpec,instrumentType,startTradeMonth,endTradeMonth,putCallFlag,strikeType,strikePrice")
    };

    public static NucoreEndpoint? Find(string name) =>
        All.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Structural checks run once at startup, so an internally inconsistent registry fails
    /// before any request goes out rather than surfacing as shifted columns in a merge.
    /// </summary>
    public static void Validate()
    {
        foreach (var e in All)
        {
            if (e.Columns.Count == 0)
                throw new InvalidOperationException($"NUCORE {e.Name}: no columns.");

            if (e.DateColumnIndex < 0)
                throw new InvalidOperationException(
                    $"NUCORE {e.Name}: date column '{e.DateColumn}' is not one of its columns.");

            var duplicate = e.Columns.GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                                     .FirstOrDefault(g => g.Count() > 1);
            if (duplicate is not null)
                throw new InvalidOperationException(
                    $"NUCORE {e.Name}: duplicate column name '{duplicate.Key}'.");

            // A key column that is also nullable would break both the PRIMARY KEY and the
            // MERGE join. The generator forces the combination off; this proves it stuck.
            var nullableKey = e.Columns.FirstOrDefault(c => c.IsKey && c.Nullable);
            if (nullableKey is not null)
                throw new InvalidOperationException(
                    $"NUCORE {e.Name}: key column '{nullableKey.Name}' is marked nullable.");

            if (e.WriteMode == NucoreWriteMode.Merge)
            {
                if (e.KeyColumns.Count == 0)
                    throw new InvalidOperationException(
                        $"NUCORE {e.Name}: Merge write mode with no key columns.");

                if (e.ScopeColumns.Count > 0)
                    throw new InvalidOperationException(
                        $"NUCORE {e.Name}: Merge write mode must not declare scope columns.");
            }
            else
            {
                if (e.KeyColumns.Count > 0)
                    throw new InvalidOperationException(
                        $"NUCORE {e.Name}: {e.WriteMode} must not declare key columns.");

                // An empty scope would make the generated DELETE unconditional and empty
                // the whole table on every batch.
                if (e.ScopeColumns.Count == 0)
                    throw new InvalidOperationException(
                        $"NUCORE {e.Name}: {e.WriteMode} requires scope columns.");

                for (var i = 0; i < e.ScopeColumns.Count; i++)
                    if (e.ScopeColumnIndexes[i] < 0)
                        throw new InvalidOperationException(
                            $"NUCORE {e.Name}: scope column '{e.ScopeColumns[i]}' is not one of its columns.");

                // The scope must contain the unit's own date, or one unit's write would
                // delete another unit's rows.
                if (!e.ScopeColumns.Contains(e.DateColumn, StringComparer.Ordinal))
                    throw new InvalidOperationException(
                        $"NUCORE {e.Name}: scope must include the date column '{e.DateColumn}'.");
            }

            if (e.Partition == NucorePartition.TradeMonth && e.TradeMonthColumnIndex < 0)
                throw new InvalidOperationException(
                    $"NUCORE {e.Name}: partitioned by trade month but has no TradeMonth column.");

            // A date-partitioned endpoint without a date parameter would quietly load the
            // vendor's latest day under every date's resume key.
            if (e.Partition == NucorePartition.Date && string.IsNullOrWhiteSpace(e.DateParam))
                throw new InvalidOperationException(
                    $"NUCORE {e.Name}: partitioned by date but declares no date parameter.");
        }
    }
}
