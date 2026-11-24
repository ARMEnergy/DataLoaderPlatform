using DataLoader.Core.Configuration;

namespace DataLoader.PJM;

/// <summary>
/// PJM Data Miner 2 loader settings, bound from <c>Loaders:PJM</c> (design §10).
///
/// <para>🔒 <b>No property ships as <c>SEE_DB</c>, on purpose (design §5.2).</b> The SEE_DB
/// resolver THROWS when <c>core.usp_GetParam</c> returns no row, so a <c>SEE_DB</c> key setting
/// would make every run fail until ARM registered its own PJM key. "Absent means use the public
/// key" is expressed instead by <see cref="PjmKeyProvider"/> calling
/// <see cref="ISeeDbParamStore.GetParam"/> directly, which returns <c>null</c> for no row.</para>
/// </summary>
public sealed class PjmSettings : LoaderSettingsBase
{
    /// <summary>The <c>rowCount</c> ceiling the API accepts (API doc §3).</summary>
    public const int MaxPageSize = 50000;

    public PjmSettings()
    {
        // Design §10 values for the inherited settings, so a missing appsettings entry cannot
        // silently fall back to the platform base defaults (4 / 500ms / 300s).
        MaxConcurrentWorkUnits = 2;
        RetryCount = 3;
        RetryDelayMs = 15000;
        WorkUnitTimeoutSeconds = 900;
    }

    /// <summary>Data API base. Always used, even if settings.json advertises another one.</summary>
    public string BaseUrl { get; set; } = "https://api.pjm.com/api/v1";

    /// <summary>Public key source: the Data Miner 2 website's own config (design §5).</summary>
    public string SettingsUrl { get; set; } = "https://dataminer2.pjm.com/config/settings.json";

    /// <summary>
    /// 🔒 ENVIRONMENT ONLY (<c>DATALOADER_Loaders__PJM__ApiKeyOverride</c>). Never a value in
    /// appsettings.json and never <c>SEE_DB</c> (design §5.2). Blank = no override.
    /// </summary>
    public string ApiKeyOverride { get; set; } = string.Empty;

    /// <summary>Per-attempt budget for a data request, applied inside the Polly policy.</summary>
    public int HttpTimeoutSeconds { get; set; } = 60;

    /// <summary>Per-attempt budget for the settings.json fetch.</summary>
    public int SettingsFetchTimeoutSeconds { get; set; } = 30;

    /// <summary>All eight feeds, in design §14.1 order: what <see cref="EffectiveEnabledFeeds"/> returns when <see cref="EnabledFeeds"/> is unset.</summary>
    public static readonly IReadOnlyList<string> DefaultEnabledFeeds = PjmFeeds.ExecutionOrder;

    /// <summary>
    /// Feed toggle as configured. Execution order is fixed (<c>inst_load</c> first) regardless of list order.
    /// ⚠ Defaults to <c>null</c>, NOT to both feeds: the configuration binder APPENDS to a pre-populated
    /// array (the ICE lesson), so a non-empty class default would make <c>["gen_by_fuel"]</c> bind as
    /// <c>[inst_load, gen_by_fuel, gen_by_fuel]</c> and the toggle could never switch a feed off.
    /// Read <see cref="EffectiveEnabledFeeds"/>.
    /// </summary>
    public string[]? EnabledFeeds { get; set; }

    /// <summary>The configured feeds, or <see cref="DefaultEnabledFeeds"/> when none are configured.</summary>
    public IReadOnlyList<string> EffectiveEnabledFeeds => EnabledFeeds is { Length: > 0 } f ? f : DefaultEnabledFeeds;

    /// <summary>Correction horizon (platform default 10; pinned by test).</summary>
    public int DaysBack { get; set; } = 10;

    /// <summary>Hot/settled boundary (platform default 10; pinned by test).</summary>
    public int SettledAfterDays { get; set; } = 10;

    /// <summary>Enumeration floor, settled-keyed: costs zero requests once each day succeeded (design §3.3).</summary>
    public int BackfillDays { get; set; } = 30;

    /// <summary>Retention ceiling for <c>inst_load</c> (design §3.3). Raise after API doc §11.6 if justified.</summary>
    public int InstLoadMaxDaysBack { get; set; } = 28;

    /// <summary>Zero-row failure band for <c>inst_load</c> (design §6.2). Must be &lt; <see cref="InstLoadMaxDaysBack"/>.</summary>
    public int InstLoadExpectRowsWithinDays { get; set; } = 25;

    /// <summary>
    /// Retention ceiling for <c>very_short_load_frcst</c> (30-day retention, design §14.3/§14.11). Mirrors
    /// <see cref="InstLoadMaxDaysBack"/> so each retention-capped feed can be tuned after its own measurement (V-8).
    /// </summary>
    public int VeryShortMaxDaysBack { get; set; } = 28;

    /// <summary>Zero-row failure band for <c>very_short_load_frcst</c> (design §14.7). Must be &lt; <see cref="VeryShortMaxDaysBack"/>.</summary>
    public int VeryShortExpectRowsWithinDays { get; set; } = 25;

    /// <summary>How long yesterday stays run-hour keyed after UTC midnight (design §3.3).</summary>
    public int RecentHours { get; set; } = 2;

    /// <summary>Shared sliding-window limit for the data API (design §10.1).</summary>
    public int RequestsPerMinute { get; set; } = 5;

    /// <summary><c>rowCount</c>; 1..50,000.</summary>
    public int PageSize { get; set; } = MaxPageSize;

    /// <summary>Sends <c>isActiveMetadata=true</c> on <c>inst_load</c>, as both third-party clients do.</summary>
    public bool InstLoadIsActiveMetadata { get; set; } = true;

    /// <summary>
    /// Seconds past the minute a slot timestamp may carry before the row fails (design §4.4). The
    /// seconds are truncated away (the slot start is stored); LIVE-VERIFIED jitter is 0–2 s.
    /// </summary>
    public int TimestampJitterToleranceSeconds { get; set; } = 10;

    /// <summary>Run-guard trip on repeated same-class unit failures (design §6.3); ≤ 0 disables.</summary>
    public int ConsecutiveFailureTripCount { get; set; } = 3;

    /// <summary>Backoff base for the settings.json fetch: the data backoff, capped at 2 s.</summary>
    public static int SettingsRetryDelayMs(int retryDelayMs) => Math.Min(Math.Max(0, retryDelayMs), 2000);

    /// <summary>
    /// Worst-case wall-clock seconds one unit's single read can take (design §10.2). It is pinned
    /// below <see cref="LoaderSettingsBase.WorkUnitTimeoutSeconds"/> by <see cref="Validate"/>:
    /// <list type="bullet">
    ///   <item><b>read cycle</b>: <c>(RetryCount+1) × (HttpTimeoutSeconds + L)</c> + <c>Σ max(backoff_i, 60)</c>,
    ///     where <c>L = 60 × MaxConcurrentWorkUnits / RequestsPerMinute</c> is the limiter wait (the limiter
    ///     sits outside the per-attempt timeout) and a <c>Retry-After</c> above 60 s trips instead of waiting;</item>
    ///   <item><b>one post-401 re-send</b>: a SINGLE attempt, <c>HttpTimeoutSeconds + L</c>;</item>
    ///   <item><b>the settings.json re-fetch</b>: <c>(RetryCount+1) × SettingsFetchTimeoutSeconds</c> + its backoff
    ///     (no limiter).</item>
    /// </list>
    /// A snapshot re-read is NOT included (rare; the unit then fails and the next run re-attempts it).
    /// </summary>
    public double WorstCaseUnitSeconds()
    {
        var attempts = Math.Max(0, RetryCount) + 1;
        var limiterWait = 60.0 * Math.Max(1, MaxConcurrentWorkUnits) / Math.Max(1, RequestsPerMinute);
        var retryAfterCap = PjmHttpPolicy.MaxRetryAfter.TotalSeconds;

        var readCycle = attempts * (Math.Max(1, HttpTimeoutSeconds) + limiterWait);
        var settingsBackoff = 0.0;
        for (var i = 1; i <= Math.Max(0, RetryCount); i++)
        {
            readCycle += Math.Max(PjmHttpPolicy.Backoff(i, RetryDelayMs).TotalSeconds, retryAfterCap);
            settingsBackoff += PjmHttpPolicy.Backoff(i, SettingsRetryDelayMs(RetryDelayMs)).TotalSeconds;
        }

        var resend = Math.Max(1, HttpTimeoutSeconds) + limiterWait;
        var refetch = attempts * Math.Max(1, SettingsFetchTimeoutSeconds) + settingsBackoff;
        return readCycle + resend + refetch;
    }

    /// <summary>
    /// Startup validation (design §10). Returns one message per violated rule; empty when valid.
    /// The module fails the run, before any request, when this is non-empty.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (TimestampJitterToleranceSeconds < 0 || TimestampJitterToleranceSeconds > 59)
            errors.Add($"Loaders:PJM:TimestampJitterToleranceSeconds must be between 0 and 59 (was {TimestampJitterToleranceSeconds}).");
        if (DaysBack < 0)
            errors.Add($"Loaders:PJM:DaysBack must be 0 or more (was {DaysBack}).");
        if (RecentHours < 0 || RecentHours > 24)
            errors.Add($"Loaders:PJM:RecentHours must be between 0 and 24 (was {RecentHours}).");
        if (SettledAfterDays > InstLoadMaxDaysBack)
            errors.Add(
                $"Loaders:PJM:SettledAfterDays ({SettledAfterDays}) must not exceed InstLoadMaxDaysBack ({InstLoadMaxDaysBack}), " +
                "or inst_load would never get a final settled read.");
        if (WorkUnitTimeoutSeconds > 0)
        {
            var worst = WorstCaseUnitSeconds();
            if (worst >= WorkUnitTimeoutSeconds)
                errors.Add(
                    $"Loaders:PJM:WorkUnitTimeoutSeconds ({WorkUnitTimeoutSeconds}) must exceed the worst-case unit read ({worst:0}s: " +
                    "retry cycle + one 401 re-send + settings.json re-fetch + limiter waits; design §10.2).");
        }

        if (PageSize < 1 || PageSize > MaxPageSize)
            errors.Add($"Loaders:PJM:PageSize must be between 1 and {MaxPageSize} (was {PageSize}).");
        if (RequestsPerMinute < 1)
            errors.Add($"Loaders:PJM:RequestsPerMinute must be at least 1 (was {RequestsPerMinute}).");
        if (InstLoadExpectRowsWithinDays >= InstLoadMaxDaysBack)
            errors.Add(
                $"Loaders:PJM:InstLoadExpectRowsWithinDays ({InstLoadExpectRowsWithinDays}) must be less than InstLoadMaxDaysBack ({InstLoadMaxDaysBack}).");
        if (SettledAfterDays > Math.Max(DaysBack, BackfillDays))
            errors.Add(
                $"Loaders:PJM:SettledAfterDays ({SettledAfterDays}) must not exceed max(DaysBack, BackfillDays) ({Math.Max(DaysBack, BackfillDays)}).");
        if (VeryShortExpectRowsWithinDays >= VeryShortMaxDaysBack)
            errors.Add(
                $"Loaders:PJM:VeryShortExpectRowsWithinDays ({VeryShortExpectRowsWithinDays}) must be less than VeryShortMaxDaysBack ({VeryShortMaxDaysBack}).");

        // Design §14.11: per-feed rules over the descriptors (vendor facts live there, not in settings).
        var enabled = new HashSet<string>(EffectiveEnabledFeeds, StringComparer.OrdinalIgnoreCase);
        foreach (var feed in PjmFeeds.All(this))
        {
            if (feed.UnitHours <= 0 || 24 % feed.UnitHours != 0)
                errors.Add($"PJM feed {feed.Name}: UnitHours ({feed.UnitHours}) must divide 24.");

            // The OilX-style settle CEILING is an exception for immutable data only (design §14.14):
            // never combined with a floor, never on a feed that restates.
            if (feed.SettledAfterDaysCeiling is not null && feed.MinSettledAfterDays is not null)
                errors.Add($"PJM feed {feed.Name}: sets both MinSettledAfterDays and SettledAfterDaysCeiling; a feed takes a floor or a ceiling, never both.");
            if (feed.SettledAfterDaysCeiling is not null && !feed.IssuedForecastsImmutable)
                errors.Add($"PJM feed {feed.Name}: SettledAfterDaysCeiling is allowed only on a feed marked IssuedForecastsImmutable; a restating feed must never settle early.");

            // Single-page units (design §14.3): the page must hold a whole unit.
            if (enabled.Contains(feed.Name) && feed.MaxPagesPerUnit is { } maxPages && feed.MaxRowsPerUnit is { } maxRows &&
                (long)PageSize * maxPages < maxRows)
                errors.Add(
                    $"Loaders:PJM:PageSize ({PageSize}) must be at least {(maxRows + maxPages - 1) / maxPages} while {feed.Name} is enabled: " +
                    $"each unit holds up to {maxRows} rows and is read in at most {maxPages} page(s).");

            if (feed.Kind != PjmFeedKind.Window) continue;
            var depth = feed.EffectiveDepth(DaysBack);

            if (feed.EmptyOkThroughAge >= depth)
                errors.Add(
                    $"PJM feed {feed.Name}: EmptyOkThroughAge ({feed.EmptyOkThroughAge}) must be less than its enumeration depth ({depth} days).");

            // Generalises the two SettledAfterDays rules above: every enabled window feed must get its
            // final settled read inside its own depth (very_short inside retention, metered's floor 30).
            var settled = feed.EffectiveSettledAfterDays(SettledAfterDays);
            if (enabled.Contains(feed.Name) && settled > depth)
                errors.Add(
                    $"PJM feed {feed.Name}: effective SettledAfterDays ({settled}) must not exceed its enumeration depth ({depth} days), " +
                    "or it would never get a final settled read; widen BackfillDays/DaysBack or lower the setting.");
        }

        if (string.IsNullOrWhiteSpace(BaseUrl) || !Uri.TryCreate(BaseUrl, UriKind.Absolute, out _))
            errors.Add("Loaders:PJM:BaseUrl must be an absolute URL.");
        if (string.IsNullOrWhiteSpace(SettingsUrl) || !Uri.TryCreate(SettingsUrl, UriKind.Absolute, out _))
            errors.Add("Loaders:PJM:SettingsUrl must be an absolute URL.");

        return errors;
    }
}
