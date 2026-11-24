namespace DataLoader.PJM;

/// <summary>Whether a feed is read by date window or as a whole-feed snapshot (design §14.2, §14.8).</summary>
public enum PjmFeedKind
{
    /// <summary>Filtered on <see cref="PjmFeed.FilterField"/>, one unit per UTC day or block.</summary>
    Window,

    /// <summary>No filter; one unit per run, keyed on the publication slot (<c>load_frcstd_7_day</c>).</summary>
    Snapshot
}

/// <summary>How a timestamp pair is normalised and checked (design §14.6, §14.14).</summary>
public enum PjmStampMode
{
    /// <summary>
    /// Truncate UTC and EPT to the minute; fail when the dropped seconds exceed
    /// <c>TimestampJitterToleranceSeconds</c> or the minute is off the
    /// <see cref="PjmStampRule.IntervalMinutes"/> boundary (the original §4.4 rule).
    /// </summary>
    Slot,

    /// <summary>
    /// Truncate to the minute with the same jitter tolerance, but NO boundary check. Used for
    /// <c>load_frcstd_hist</c>'s <c>evaluated_at</c>, which sits on <c>:45</c> with zero seconds
    /// (§14.14 V-4: truncation is harmless, an hourly boundary check would fail every row).
    /// </summary>
    Minute,

    /// <summary>
    /// Whole seconds kept AS RECEIVED: no truncation, no boundary check (fractions still fail in
    /// the parser). ⚠ Mandatory for <c>load_frcstd_7_day</c>'s <c>evaluated_at_datetime</c>, which
    /// carries REAL seconds (e.g. <c>22:47:12</c>): truncating would rewrite the key.
    /// </summary>
    Exact
}

/// <summary>One UTC/EPT column pair and its rule. EPT must equal Eastern(UTC) after the rule (design §14.6).</summary>
public sealed record PjmStampRule(string UtcField, string EptField, PjmStampMode Mode, int IntervalMinutes = 0);

/// <summary><c>EndUtc − BeginUtc == Minutes</c> on every row, checked on the UTC pair ONLY (§14.14 V-2: EPT is naive wall clock).</summary>
public sealed record PjmEndOffset(string BeginUtcField, string EndUtcField, int Minutes);

/// <summary>Completeness rule (warnings, never failures; design §14.7).</summary>
public enum PjmCompletenessMode
{
    None,

    /// <summary>Distinct <c>SlotUtc</c> across the unit &lt; expected (gen_by_fuel; DST-aware).</summary>
    DistinctSlotsTotal,

    /// <summary>Any member with distinct <c>SlotUtc</c> &lt; expected.</summary>
    DistinctSlotsPerMember,

    /// <summary>Any member whose ROW count ≠ expected (the 7_day snapshot: 168 per area).</summary>
    RowsPerMemberExact
}

/// <summary>
/// Completeness descriptor. Runs when <c>unit.Age ≥ FromAge</c> and, for units under 24 hours,
/// only once the unit has left the Recent zone.
/// </summary>
public sealed record PjmCompleteness(PjmCompletenessMode Mode, string SlotField, int ExpectedPerUnit, int FromAge)
{
    public static readonly PjmCompleteness None = new(PjmCompletenessMode.None, string.Empty, 0, int.MaxValue);
}

/// <summary>
/// Immutable per-feed descriptor (design §2, generalised in §14.2). Retention, filter, key shape,
/// timestamp rules, unit size and empty-read behaviour live here as DATA, not in
/// <c>if (feed == …)</c> branches (the NUCORE descriptor precedent).
/// </summary>
public sealed record PjmFeed
{
    /// <summary>URL path segment and pipeline id.</summary>
    public required string Name { get; init; }

    public PjmFeedKind Kind { get; init; } = PjmFeedKind.Window;

    /// <summary>Enumeration FLOOR (days back from today, UTC).</summary>
    public required int BackfillDays { get; init; }

    /// <summary>Enumeration CEILING — vendor retention. <c>null</c> = none.</summary>
    public int? MaxDaysBack { get; init; }

    /// <summary>
    /// A past unit this recent (age up to N) that returns 0 rows FAILS (design §6.2). <c>null</c> =
    /// every past unit outside <see cref="EmptyOkThroughAge"/> is expected to have rows. Older units
    /// inside the enumeration answer warning + success (the retention edge).
    /// </summary>
    public int? ExpectRowsWithinDays { get; init; }

    /// <summary>
    /// The UTC column filtered AND echo-checked (design §14.2). <c>null</c> for a snapshot.
    /// </summary>
    public string? FilterField { get; init; }

    /// <summary>
    /// The <c>sort=</c> column — <see cref="FilterField"/> for window feeds. Every request carries one
    /// (the NUCORE lesson), and it must be a column metadata marks sortable (§14.14 V-5).
    /// </summary>
    public required string SortField { get; init; }

    /// <summary>
    /// Window size in hours; divides 24. Units under 24 hours are keyed per block (§14.3).
    /// ⚠ FROZEN once data exists: it is baked into settled resume keys (the EIA WindowSteps lesson).
    /// </summary>
    public int UnitHours { get; init; } = 24;

    /// <summary>False for feeds that post past days only: age 0 is never enumerated.</summary>
    public bool IncludeToday { get; init; } = true;

    /// <summary>FLOOR on the loader-level <c>RecentHours</c> for this feed (keeps yesterday run-hour keyed until the daily post lands).</summary>
    public int? MinRecentHours { get; init; }

    /// <summary>FLOOR on <c>SettledAfterDays</c> (the NUCORE SPIP precedent): the global lever can only widen it.</summary>
    public int? MinSettledAfterDays { get; init; }

    /// <summary>
    /// ⚠ CEILING on <c>SettledAfterDays</c> — a documented, deliberate EXCEPTION to the user's
    /// "per-dataset exceptions are floors" rule (design §14.4 D1 = B, §14.14), justified exactly as
    /// OilX's <c>SettledAfterDays = 1</c>: an issued forecast is immutable, so re-reading it for ten
    /// days changes nothing and burns the shared 5/min pool. Set ONLY on
    /// <c>very_short_load_frcst</c> (1) and <c>load_frcstd_hist</c> (2). Never set this on a feed that
    /// restates — widening the global <c>SettledAfterDays</c> would then silently not widen it.
    /// </summary>
    public int? SettledAfterDaysCeiling { get; init; }

    /// <summary>
    /// The vendor fact that licenses <see cref="SettledAfterDaysCeiling"/>: an issued forecast is never
    /// restated. <c>Validate</c> rejects a ceiling on any feed without it, so the exception cannot reach
    /// a restating feed by accident. True ONLY for <c>very_short_load_frcst</c> and <c>load_frcstd_hist</c>.
    /// </summary>
    public bool IssuedForecastsImmutable { get; init; }

    /// <summary>
    /// Page cap per unit: <c>totalRows &gt; PageSize × MaxPagesPerUnit</c> fails the unit as a contract
    /// breach before any further page is read (design §14.3). <c>null</c> = unbounded paging.
    /// </summary>
    public int? MaxPagesPerUnit { get; init; }

    /// <summary>
    /// The largest row count one unit is designed to hold (very_short: 72 × 25 × 24 = 43,200). With
    /// <see cref="MaxPagesPerUnit"/>, <c>Validate</c> requires <c>PageSize × MaxPagesPerUnit ≥</c> this.
    /// </summary>
    public int? MaxRowsPerUnit { get; init; }

    /// <summary>A unit this young may read 0 rows, or a partial unit, as success (design §14.7). 0 = only today.</summary>
    public int EmptyOkThroughAge { get; init; }

    /// <summary>Timestamp pairs, each normalised and EPT-checked (design §14.6).</summary>
    public required IReadOnlyList<PjmStampRule> Stamps { get; init; }

    /// <summary>UTC end-offset checks (design §14.6, §14.14 V-2).</summary>
    public IReadOnlyList<PjmEndOffset> EndOffsets { get; init; } = Array.Empty<PjmEndOffset>();

    /// <summary>Key fields in key order: the timestamps first, the member last. Used in messages.</summary>
    public required IReadOnlyList<string> KeyFields { get; init; }

    public required PjmCompleteness Completeness { get; init; }

    /// <summary>Snapshot only: a WARNING (never a failure) when the evaluation is older than this (design §14.8).</summary>
    public int? SnapshotStaleAfterHours { get; init; }

    /// <summary>
    /// True when the feed omits the repeated Eastern hour on a DST fall-back day (LIVE-VERIFIED
    /// 2026-11-23 for <c>gen_by_fuel</c>: 23 hours on 2026-11-01, UTC 06:00 absent). The
    /// <see cref="PjmCompletenessMode.DistinctSlotsTotal"/> warning then expects one hour fewer.
    /// </summary>
    public bool DropsRepeatedFallBackHour { get; init; }

    /// <summary>The key member field (<c>fuel_type</c> / <c>area</c> / <c>load_area</c> / <c>forecast_area</c>).</summary>
    public required string MemberField { get; init; }

    /// <summary>Missing on any item → fail the unit.</summary>
    public required IReadOnlySet<string> RequiredFields { get; init; }

    /// <summary>May be absent; absent → NULL.</summary>
    public required IReadOnlySet<string> OptionalFields { get; init; }

    /// <summary>Extra query string (without a leading <c>&amp;</c>), e.g. <c>isActiveMetadata=true</c>; <c>null</c> = none.</summary>
    public string? ExtraQuery { get; init; }

    /// <summary>Message fragment for a zero-row unit inside the expectation band (design §6.2).</summary>
    public required string EmptyPastDayReason { get; init; }

    /// <summary>Required ∪ optional — anything else on an item is an "unexpected field" (design §4.3).</summary>
    public bool IsKnownField(string name) => RequiredFields.Contains(name) || OptionalFields.Contains(name);

    /// <summary>
    /// Effective enumeration depth: the floor rule, then the hard vendor ceiling (design §2):
    /// <c>min(max(DaysBack, BackfillDays), MaxDaysBack ?? ∞)</c>.
    /// </summary>
    public int EffectiveDepth(int daysBack) =>
        Math.Min(Math.Max(daysBack, BackfillDays), MaxDaysBack ?? int.MaxValue);

    /// <summary>
    /// <c>min(max(SettledAfterDays, MinSettledAfterDays ?? 0), SettledAfterDaysCeiling ?? ∞)</c> (design §14.3).
    /// </summary>
    public int EffectiveSettledAfterDays(int settledAfterDays) =>
        Math.Min(Math.Max(settledAfterDays, MinSettledAfterDays ?? 0), SettledAfterDaysCeiling ?? int.MaxValue);

    /// <summary><c>max(RecentHours, MinRecentHours ?? 0)</c> (design §14.3).</summary>
    public int EffectiveRecentHours(int recentHours) => Math.Max(recentHours, MinRecentHours ?? 0);
}

/// <summary>The eight feed descriptors, built from settings (design §2, §14.1).</summary>
public static class PjmFeeds
{
    public const string InstLoadName = "inst_load";
    public const string GenByFuelName = "gen_by_fuel";
    public const string LoadMeteredName = "hrl_load_metered";
    public const string LoadPrelimName = "hrl_load_prelim";
    public const string LoadEstimatedName = "hrl_load_estimated";
    public const string LoadForecastHistName = "load_frcstd_hist";
    public const string LoadForecastVeryShortName = "very_short_load_frcst";
    public const string LoadForecast7DayName = "load_frcstd_7_day";

    // Original two feeds.
    public const string DatetimeBeginningUtc = "datetime_beginning_utc";
    public const string DatetimeBeginningEpt = "datetime_beginning_ept";
    public const string FuelType = "fuel_type";
    public const string Mw = "mw";
    public const string FuelPercentageOfTotal = "fuel_percentage_of_total";
    public const string IsRenewable = "is_renewable";
    public const string Area = "area";
    public const string InstantaneousLoad = "instantaneous_load";

    // hrl_* (API doc §12.2-§12.4).
    public const string DatetimeEndingUtc = "datetime_ending_utc";
    public const string DatetimeEndingEpt = "datetime_ending_ept";
    public const string NercRegion = "nerc_region";
    public const string MktRegion = "mkt_region";
    public const string Zone = "zone";
    public const string LoadArea = "load_area";
    public const string IsVerified = "is_verified";
    public const string PrelimLoadAvgHourly = "prelim_load_avg_hourly";
    public const string EstimatedLoadHourly = "estimated_load_hourly";

    // Forecasts (API doc §12.5-§12.7). ⚠ Trap 7: the names differ between feeds — mirror each exactly.
    public const string EvaluatedAtUtc = "evaluated_at_utc";
    public const string EvaluatedAtEpt = "evaluated_at_ept";
    public const string EvaluatedAtDatetimeUtc = "evaluated_at_datetime_utc";
    public const string EvaluatedAtDatetimeEpt = "evaluated_at_datetime_ept";
    public const string ForecastHourBeginningUtc = "forecast_hour_beginning_utc";
    public const string ForecastHourBeginningEpt = "forecast_hour_beginning_ept";
    public const string ForecastDatetimeBeginningUtc = "forecast_datetime_beginning_utc";
    public const string ForecastDatetimeBeginningEpt = "forecast_datetime_beginning_ept";
    public const string ForecastDatetimeEndingUtc = "forecast_datetime_ending_utc";
    public const string ForecastDatetimeEndingEpt = "forecast_datetime_ending_ept";
    public const string ForecastArea = "forecast_area";
    public const string ForecastLoadMw = "forecast_load_mw";

    /// <summary>Rows in one very_short 6-hour block: 72 evaluations × 25 areas × 24 steps (API doc §12.6).</summary>
    public const int VeryShortRowsPerBlock = 43_200;

    /// <summary>
    /// Fixed execution order (design §14.1, revised by review 2026-11-23): the <c>load_frcstd_7_day</c>
    /// snapshot FIRST — one request, and the only feed with no retention at all, so a guard trip later
    /// in the run can never cost it — then the two 30-day-retention feeds, then the recoverable history
    /// feeds, with <c>hrl_load_estimated</c> LAST because it is the feed most likely to produce
    /// empty-past-day failures.
    /// </summary>
    public static readonly IReadOnlyList<string> ExecutionOrder = new[]
    {
        LoadForecast7DayName,
        InstLoadName,
        LoadForecastVeryShortName,
        GenByFuelName,
        LoadMeteredName,
        LoadPrelimName,
        LoadForecastHistName,
        LoadEstimatedName,
    };

    /// <summary>Every descriptor, in <see cref="ExecutionOrder"/>.</summary>
    public static IReadOnlyList<PjmFeed> All(PjmSettings s) => new[]
    {
        LoadForecast7Day(s), InstLoad(s), LoadForecastVeryShort(s), GenByFuel(s),
        LoadMetered(s), LoadPrelim(s), LoadForecastHist(s), LoadEstimated(s),
    };

    private static IReadOnlySet<string> Set(params string[] names) =>
        new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);

    public static PjmFeed GenByFuel(PjmSettings s) => new()
    {
        Name = GenByFuelName,
        BackfillDays = s.BackfillDays,
        MaxDaysBack = null,
        ExpectRowsWithinDays = null, // = the full enumeration depth: every past day must have rows
        FilterField = DatetimeBeginningUtc,
        SortField = DatetimeBeginningUtc,
        Stamps = new[] { new PjmStampRule(DatetimeBeginningUtc, DatetimeBeginningEpt, PjmStampMode.Slot, 60) },
        KeyFields = new[] { DatetimeBeginningUtc, FuelType },
        Completeness = new PjmCompleteness(PjmCompletenessMode.DistinctSlotsTotal, DatetimeBeginningUtc, 24, FromAge: 1),
        DropsRepeatedFallBackHour = true,
        MemberField = FuelType,
        RequiredFields = Set(DatetimeBeginningUtc, DatetimeBeginningEpt, FuelType, Mw, IsRenewable),
        OptionalFields = Set(FuelPercentageOfTotal),
        ExtraQuery = null,
        EmptyPastDayReason = "0 rows for a past day; history is continuous since 2016, so the filter or the API is wrong",
    };

    public static PjmFeed InstLoad(PjmSettings s) => new()
    {
        Name = InstLoadName,
        BackfillDays = s.BackfillDays,
        MaxDaysBack = s.InstLoadMaxDaysBack,
        ExpectRowsWithinDays = s.InstLoadExpectRowsWithinDays,
        FilterField = DatetimeBeginningUtc,
        SortField = DatetimeBeginningUtc,
        Stamps = new[] { new PjmStampRule(DatetimeBeginningUtc, DatetimeBeginningEpt, PjmStampMode.Slot, 5) },
        KeyFields = new[] { DatetimeBeginningUtc, Area },
        Completeness = new PjmCompleteness(PjmCompletenessMode.DistinctSlotsPerMember, DatetimeBeginningUtc, 288, FromAge: 1),
        MemberField = Area,
        RequiredFields = Set(DatetimeBeginningUtc, DatetimeBeginningEpt, Area, InstantaneousLoad),
        OptionalFields = Set(),
        ExtraQuery = s.InstLoadIsActiveMetadata ? "isActiveMetadata=true" : null,
        EmptyPastDayReason = "0 rows inside retention (age {age} ≤ {limit}); likely a bad filter or a retention change",
    };

    /// <summary>
    /// <c>hrl_load_metered</c> → <c>dbo.pjm_load_metered</c>. ⚠ <c>is_verified</c> proves restatement
    /// with an UNKNOWN horizon (V-1 unmeasured), so the settle FLOOR is 30 (D2 = 30, §14.14).
    /// </summary>
    public static PjmFeed LoadMetered(PjmSettings s) => new()
    {
        Name = LoadMeteredName,
        BackfillDays = s.BackfillDays,
        FilterField = DatetimeBeginningUtc,
        SortField = DatetimeBeginningUtc,
        IncludeToday = false,
        MinRecentHours = 12,
        MinSettledAfterDays = 30,
        EmptyOkThroughAge = 1,
        Stamps = new[] { new PjmStampRule(DatetimeBeginningUtc, DatetimeBeginningEpt, PjmStampMode.Slot, 60) },
        KeyFields = new[] { DatetimeBeginningUtc, LoadArea },
        Completeness = new PjmCompleteness(PjmCompletenessMode.DistinctSlotsPerMember, DatetimeBeginningUtc, 24, FromAge: 2),
        MemberField = LoadArea,
        RequiredFields = Set(DatetimeBeginningUtc, DatetimeBeginningEpt, NercRegion, MktRegion, Zone, LoadArea, Mw, IsVerified),
        OptionalFields = Set(),
        EmptyPastDayReason = "0 rows for a past day (age {age}) after its daily post; history is continuous since 1993, so the filter or the API is wrong",
    };

    /// <summary><c>hrl_load_prelim</c> → <c>dbo.pjm_load_prelim</c> (default 10/10 horizon).</summary>
    public static PjmFeed LoadPrelim(PjmSettings s) => new()
    {
        Name = LoadPrelimName,
        BackfillDays = s.BackfillDays,
        FilterField = DatetimeBeginningUtc,
        SortField = DatetimeBeginningUtc,
        IncludeToday = false,
        MinRecentHours = 12,
        EmptyOkThroughAge = 1,
        Stamps = HourlyBeginEnd,
        EndOffsets = new[] { new PjmEndOffset(DatetimeBeginningUtc, DatetimeEndingUtc, 60) },
        KeyFields = new[] { DatetimeBeginningUtc, LoadArea },
        Completeness = new PjmCompleteness(PjmCompletenessMode.DistinctSlotsPerMember, DatetimeBeginningUtc, 24, FromAge: 2),
        MemberField = LoadArea,
        RequiredFields = Set(DatetimeBeginningUtc, DatetimeBeginningEpt, DatetimeEndingUtc, DatetimeEndingEpt, LoadArea, PrelimLoadAvgHourly),
        OptionalFields = Set(),
        EmptyPastDayReason = "0 rows for a past day (age {age}) after its daily post; history is continuous since 2011, so the filter or the API is wrong",
    };

    /// <summary>
    /// <c>hrl_load_estimated</c> → <c>dbo.pjm_load_estimated</c>. Lags up to ~a week and posts Tue/Fri,
    /// so an empty OR partial day is success through age 10 and the settle floor is 14 (D5, §14.14).
    /// </summary>
    public static PjmFeed LoadEstimated(PjmSettings s) => new()
    {
        Name = LoadEstimatedName,
        BackfillDays = s.BackfillDays,
        FilterField = DatetimeBeginningUtc,
        SortField = DatetimeBeginningUtc,
        IncludeToday = false,
        MinSettledAfterDays = 14,
        EmptyOkThroughAge = 10,
        Stamps = HourlyBeginEnd,
        EndOffsets = new[] { new PjmEndOffset(DatetimeBeginningUtc, DatetimeEndingUtc, 60) },
        KeyFields = new[] { DatetimeBeginningUtc, LoadArea },
        Completeness = new PjmCompleteness(PjmCompletenessMode.DistinctSlotsPerMember, DatetimeBeginningUtc, 24, FromAge: 11),
        MemberField = LoadArea,
        RequiredFields = Set(DatetimeBeginningUtc, DatetimeBeginningEpt, DatetimeEndingUtc, DatetimeEndingEpt, LoadArea, EstimatedLoadHourly),
        OptionalFields = Set(),
        EmptyPastDayReason = "0 rows at age {age}, beyond the 10-day posting-lag band; PJM may have stopped posting estimated load, or the filter is wrong",
    };

    /// <summary>
    /// <c>load_frcstd_hist</c> → <c>dbo.pjm_load_forecast_hist</c>. Filtered on <c>evaluated_at_utc</c>.
    /// ⚠ Settle CEILING 2 (D1 = B, §14.14): evaluated forecasts are immutable.
    /// </summary>
    public static PjmFeed LoadForecastHist(PjmSettings s) => new()
    {
        Name = LoadForecastHistName,
        BackfillDays = s.BackfillDays,
        FilterField = EvaluatedAtUtc,
        SortField = EvaluatedAtUtc,
        IncludeToday = false,
        MinRecentHours = 7,
        SettledAfterDaysCeiling = 2,
        IssuedForecastsImmutable = true,
        EmptyOkThroughAge = 1,
        Stamps = new[]
        {
            // §14.14 V-4: evaluated_at sits on :45 with zero seconds — truncate, NO hourly boundary check.
            new PjmStampRule(EvaluatedAtUtc, EvaluatedAtEpt, PjmStampMode.Minute),
            new PjmStampRule(ForecastHourBeginningUtc, ForecastHourBeginningEpt, PjmStampMode.Slot, 60),
        },
        KeyFields = new[] { EvaluatedAtUtc, ForecastHourBeginningUtc, ForecastArea },
        Completeness = PjmCompleteness.None, // evaluations per day not characterised (§14.7)
        MemberField = ForecastArea,
        RequiredFields = Set(EvaluatedAtUtc, EvaluatedAtEpt, ForecastHourBeginningUtc, ForecastHourBeginningEpt, ForecastArea, ForecastLoadMw),
        OptionalFields = Set(),
        EmptyPastDayReason = "0 evaluations for a past day (age {age}) after its daily post; history is continuous since 2011, so the filter or the API is wrong",
    };

    /// <summary>
    /// <c>very_short_load_frcst</c> → <c>dbo.pjm_load_forecast_very_short</c>. ⚠ 30-day retention;
    /// 6-hour <c>evaluated_at_utc</c> blocks, one page each (43,200 rows); capped at today − 28.
    /// ⚠ Settle CEILING 1 (D1 = B, §14.14): immutable once evaluated.
    /// </summary>
    public static PjmFeed LoadForecastVeryShort(PjmSettings s) => new()
    {
        Name = LoadForecastVeryShortName,
        BackfillDays = s.BackfillDays,
        MaxDaysBack = s.VeryShortMaxDaysBack,
        ExpectRowsWithinDays = s.VeryShortExpectRowsWithinDays,
        FilterField = EvaluatedAtUtc,
        // ⚠ One sort column only: the API documents `sort` as a single column name (API doc §3) and a
        // multi-column sort is unverified, so evaluated_at_utc alone is NOT a total order (600 rows
        // share each value). Paging on it is safe ONLY because MaxPagesPerUnit = 1 fails any
        // block that would need a second page.
        SortField = EvaluatedAtUtc,
        UnitHours = 6,
        SettledAfterDaysCeiling = 1,
        IssuedForecastsImmutable = true,
        MaxPagesPerUnit = 1,
        MaxRowsPerUnit = VeryShortRowsPerBlock,
        Stamps = new[]
        {
            new PjmStampRule(EvaluatedAtUtc, EvaluatedAtEpt, PjmStampMode.Slot, 5),
            new PjmStampRule(ForecastDatetimeBeginningUtc, ForecastDatetimeBeginningEpt, PjmStampMode.Slot, 5),
            new PjmStampRule(ForecastDatetimeEndingUtc, ForecastDatetimeEndingEpt, PjmStampMode.Slot, 5),
        },
        EndOffsets = new[] { new PjmEndOffset(ForecastDatetimeBeginningUtc, ForecastDatetimeEndingUtc, 5) },
        KeyFields = new[] { EvaluatedAtUtc, ForecastDatetimeBeginningUtc, ForecastArea },
        Completeness = new PjmCompleteness(PjmCompletenessMode.DistinctSlotsPerMember, EvaluatedAtUtc, 72, FromAge: 0),
        MemberField = ForecastArea,
        RequiredFields = Set(EvaluatedAtUtc, EvaluatedAtEpt, ForecastDatetimeBeginningUtc, ForecastDatetimeBeginningEpt,
            ForecastDatetimeEndingUtc, ForecastDatetimeEndingEpt, ForecastArea, ForecastLoadMw),
        OptionalFields = Set(),
        EmptyPastDayReason = "0 rows for a past block inside retention (age {age} ≤ {limit}); likely a bad filter or a retention change",
    };

    /// <summary>
    /// <c>load_frcstd_7_day</c> → <c>dbo.pjm_load_forecast_7day</c>: a SNAPSHOT of the latest evaluation
    /// only (retention "None"). No date filter — <c>evaluated_at_datetime</c> is neither sortable nor
    /// filterable (§14.14 V-5) — and sorted on <c>forecast_datetime_beginning_utc</c>.
    /// </summary>
    public static PjmFeed LoadForecast7Day(PjmSettings s) => new()
    {
        Name = LoadForecast7DayName,
        Kind = PjmFeedKind.Snapshot,
        BackfillDays = 0,
        FilterField = null,
        SortField = ForecastDatetimeBeginningUtc,
        Stamps = new[]
        {
            // ⚠ REAL seconds (e.g. 22:47:12): MUST be exempt from truncation and any boundary check.
            new PjmStampRule(EvaluatedAtDatetimeUtc, EvaluatedAtDatetimeEpt, PjmStampMode.Exact),
            new PjmStampRule(ForecastDatetimeBeginningUtc, ForecastDatetimeBeginningEpt, PjmStampMode.Slot, 60),
            new PjmStampRule(ForecastDatetimeEndingUtc, ForecastDatetimeEndingEpt, PjmStampMode.Slot, 60),
        },
        EndOffsets = new[] { new PjmEndOffset(ForecastDatetimeBeginningUtc, ForecastDatetimeEndingUtc, 60) },
        KeyFields = new[] { EvaluatedAtDatetimeUtc, ForecastDatetimeBeginningUtc, ForecastArea },
        Completeness = new PjmCompleteness(PjmCompletenessMode.RowsPerMemberExact, ForecastDatetimeBeginningUtc, 168, FromAge: 0),
        SnapshotStaleAfterHours = 6,
        MemberField = ForecastArea,
        RequiredFields = Set(EvaluatedAtDatetimeUtc, EvaluatedAtDatetimeEpt, ForecastDatetimeBeginningUtc, ForecastDatetimeBeginningEpt,
            ForecastDatetimeEndingUtc, ForecastDatetimeEndingEpt, ForecastArea, ForecastLoadMw),
        OptionalFields = Set(),
        EmptyPastDayReason = "an empty snapshot; the feed always holds the latest evaluation, so it can never legitimately be empty",
    };

    private static readonly PjmStampRule[] HourlyBeginEnd =
    {
        new(DatetimeBeginningUtc, DatetimeBeginningEpt, PjmStampMode.Slot, 60),
        new(DatetimeEndingUtc, DatetimeEndingEpt, PjmStampMode.Slot, 60),
    };
}
