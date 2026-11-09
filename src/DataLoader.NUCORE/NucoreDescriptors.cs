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
/// How an endpoint's date reaches the query string. The vendor is not consistent about
/// this, and each shape has its own way of going silently wrong.
/// </summary>
internal enum NucoreDateParamStyle
{
    /// <summary>
    /// One parameter carrying one date: <c>{DateParam}=yyyy-MM-dd</c>. The original four
    /// date-partitioned endpoints.
    /// </summary>
    SingleDate,

    /// <summary>
    /// A From/To pair over whole DATES: <c>{DateParam}From=yyyy-MM-dd&amp;{DateParam}To=yyyy-MM-dd</c>,
    /// both bounds inclusive. <c>ProductSpecCurves</c> only.
    ///
    /// <para>
    /// Day slicing was proven LOSSLESS before it was relied on: 2026-10-15 alone returned
    /// 44,351 rows, 2026-10-16 alone 44,388, and the two-day window exactly 88,739.
    /// </para>
    /// <para>
    /// ⚠ The vendor's spec says each bound "will default to the current system date if left
    /// blank". It does not. An unfiltered call returns the ENTIRE history - 10,551,909 rows.
    /// </para>
    /// </summary>
    DateRange,

    /// <summary>
    /// A From/To pair over DATETIMEs, spanning one whole day:
    /// <c>{DateParam}From=yyyy-MM-ddT00:00:00&amp;{DateParam}To=yyyy-MM-ddT23:59:59</c>.
    /// <c>SettlementPublicationIndexPrices</c> only.
    ///
    /// <para>
    /// ⚠ Two traps, and both lose data without any error.
    /// </para>
    /// <para>
    /// <b>A date-only bound means the midnight INSTANT, not the day.</b>
    /// <c>LocalTimeFrom=2026-10-15&amp;LocalTimeTo=2026-10-15</c> returns <b>56</b> rows -
    /// one per index at hour 0100 - where the real day holds <b>1,823</b>. The times are
    /// what make it a day, so they are not cosmetic.
    /// </para>
    /// <para>
    /// <b><c>To</c> is INCLUSIVE.</b> So the bound must be <c>23:59:59</c> and never the
    /// next day's midnight: <c>From=15, To=16</c> returns 1,879 rows spanning two days,
    /// because it swallows day 16's midnight row. That row would then be loaded again by
    /// day 16's own unit.
    /// </para>
    /// <para>
    /// With <c>00:00:00</c>/<c>23:59:59</c> the split is LOSSLESS: 1,823 + 1,823 = 3,646,
    /// exactly the two-day window. The data is 15-minute granularity (max observed 23:45),
    /// so nothing falls between 23:59:59 and the next midnight.
    /// </para>
    /// </summary>
    DateTimeRange,

    /// <summary>No date parameter exists. <c>PositionDetails</c> only.</summary>
    None
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
/// <param name="DateParamStyle">
/// How <paramref name="DateParam"/> is rendered into the query string. See
/// <see cref="NucoreDateParamStyle"/> - the shapes are not interchangeable and picking the
/// wrong one loses rows silently.
/// </param>
/// <param name="PublishesOnWeekends">
/// True when the vendor publishes seven days a week for this endpoint, so
/// <see cref="NucoreSettings.SkipWeekends"/> must NOT apply to it.
///
/// <para>
/// ⚠ False for six of the seven endpoints, and that is what makes it dangerous.
/// <c>SettlementPublicationIndexPrices</c> carries a FULL day of settlement prices on
/// Saturdays and Sundays - 1,823 rows each, the same as any weekday - because power flows
/// every day. Letting the platform-wide weekend skip apply to it would silently discard
/// two days in every seven, and nothing would fail: an unvisited day raises no error and
/// leaves no gap anyone would notice without counting.
/// </para>
/// </param>
/// <param name="DaysAhead">
/// How many days past today this endpoint still has data for, so the window is enumerated
/// that far forward.
///
/// <para>
/// Zero for everything that reports on a closed business day.
/// <c>SettlementPublicationIndexPrices</c> is the exception: it publishes DAY-AHEAD prices,
/// so tomorrow already holds rows (456 of an eventual 1,823 when measured) and its rows are
/// created up to 14 days BEFORE the flow day they describe. Enumerating only up to today
/// would leave tomorrow to be picked up a day late, every day.
/// </para>
/// </param>
/// <param name="MinSettledAfterDays">
/// A FLOOR on <see cref="NucoreSettings.SettledAfterDays"/> for this endpoint, measured from
/// how long the vendor keeps restating it. The effective threshold is the larger of the two,
/// so the operator's global lever can still widen the hot window but cannot narrow it past
/// what this endpoint demonstrably needs.
///
/// <para>
/// ⚠ <c>SettlementPublicationIndexPrices</c> is restated for up to <b>21 days</b> after the
/// flow day (measured: <c>modifiedAt</c> runs to flow day + 21 on 2026-10-15 and + 20 on
/// 2026-06-10). The platform default of 3 would mark a day settled two and a half weeks
/// before the vendor stops correcting it, and every later correction would be skipped
/// without a request ever being made.
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
    string SortFields,
    NucoreDateParamStyle DateParamStyle = NucoreDateParamStyle.SingleDate,
    bool PublishesOnWeekends = false,
    int DaysAhead = 0,
    int MinSettledAfterDays = 0)
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
/// The seven endpoints this loader reads.
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
    public const string SettlementPublicationIndexPrices = "SettlementPublicationIndexPrices";
    public const string ProductSpecCurves                = "ProductSpecCurves";

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
            SortFields: "-systemDate,tradeId,detailId,tradeMonth",
            DateParamStyle: NucoreDateParamStyle.None),

        new NucoreEndpoint(
            Name:       Volatilities,
            Path:       "/Integration/api/risk/Volatilities/Get",
            DateParam:  "SystemDate",
            DateColumn: "SystemDate",
            Partition:  NucorePartition.Date,
            WriteMode:  NucoreWriteMode.Merge,
            Columns:    NucoreColumns.Volatilities,
            ScopeColumns: Array.Empty<string>(),
            SortFields: "systemDate,productSpec,instrumentType,startTradeMonth,endTradeMonth,putCallFlag,strikeType,strikePrice"),

        new NucoreEndpoint(
            Name:       SettlementPublicationIndexPrices,
            Path:       "/Integration/api/risk/SettlementPublicationIndexPrices/Get",
            // ⚠ Sent as LocalTimeFrom / LocalTimeTo with explicit times - see
            // NucoreDateParamStyle.DateTimeRange. A date-only bound returns the midnight
            // instant only: 56 rows where the day holds 1,823.
            DateParam:  "LocalTime",
            DateColumn: "LocalTime",
            Partition:  NucorePartition.Date,
            WriteMode:  NucoreWriteMode.Merge,
            Columns:    NucoreColumns.SettlementPublicationIndexPrices,
            ScopeColumns: Array.Empty<string>(),
            // Sorted on the full key: on a DST fall-back day localTime repeats, so a sort
            // without beginningUtc leaves ties whose order can differ between pages.
            SortFields: "localTime,beginningUtc,spiSettlementPublicationIndexId,spifSettlementPublicationIndexFrequencyId",
            DateParamStyle: NucoreDateParamStyle.DateTimeRange,
            // ⚠ Power flows on weekends and so do these prices: Saturday and Sunday each
            // carry a full 1,823 rows. The platform-wide weekend skip must not apply.
            PublishesOnWeekends: true,
            // Day-ahead prices: tomorrow already has rows.
            DaysAhead: 2,
            // ⚠ Restated for up to 21 days after the flow day. 3 would settle it far too early.
            MinSettledAfterDays: 25),

        new NucoreEndpoint(
            Name:       ProductSpecCurves,
            Path:       "/Integration/api/risk/ProductSpecCurves/Get",
            // Sent as SystemDateFrom / SystemDateTo, both inclusive.
            DateParam:  "SystemDate",
            DateColumn: "SystemDate",
            Partition:  NucorePartition.Date,
            WriteMode:  NucoreWriteMode.Merge,
            Columns:    NucoreColumns.ProductSpecCurves,
            ScopeColumns: Array.Empty<string>(),
            SortFields: "systemDate,prsSpecId,tradeMonth,startFlowDate,localStartFlowHour",
            DateParamStyle: NucoreDateParamStyle.DateRange)
            // Weekends really are empty here (verified 0 rows on both 2026-10-17 and -18),
            // and a closed day stops changing within a day, so the platform defaults for
            // SkipWeekends / DaysAhead / SettledAfterDays are all correct as they stand.
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

            // The style and the parameter have to agree in both directions. A style other
            // than None with no parameter name emits a bare "From=..." ; the None style with
            // a parameter name means someone added a date filter the vendor does not have,
            // which this API answers with 200 and the latest day rather than an error.
            if (e.DateParamStyle == NucoreDateParamStyle.None)
            {
                if (!string.IsNullOrWhiteSpace(e.DateParam))
                    throw new InvalidOperationException(
                        $"NUCORE {e.Name}: date param style None but declares DateParam " +
                        $"'{e.DateParam}'. This endpoint has no date filter; sending one " +
                        "would be accepted, ignored, and would load the wrong rows.");
            }
            else if (string.IsNullOrWhiteSpace(e.DateParam))
            {
                throw new InvalidOperationException(
                    $"NUCORE {e.Name}: date param style {e.DateParamStyle} requires a DateParam.");
            }

            if (e.DaysAhead < 0)
                throw new InvalidOperationException(
                    $"NUCORE {e.Name}: DaysAhead is {e.DaysAhead}; it may not be negative.");

            if (e.MinSettledAfterDays < 0)
                throw new InvalidOperationException(
                    $"NUCORE {e.Name}: MinSettledAfterDays is {e.MinSettledAfterDays}; it may not be negative.");

            // An endpoint that publishes into the future must keep those days hot for at
            // least as long as they are still filling in. A future day that settled before
            // the vendor finished writing it would be skipped forever holding a partial read.
            if (e.DaysAhead > 0 && e.MinSettledAfterDays <= e.DaysAhead)
                throw new InvalidOperationException(
                    $"NUCORE {e.Name}: DaysAhead is {e.DaysAhead} but MinSettledAfterDays is " +
                    $"{e.MinSettledAfterDays}. A day enumerated ahead of today must stay hot " +
                    "longer than it is in the future, or it settles while still incomplete.");
        }
    }
}
