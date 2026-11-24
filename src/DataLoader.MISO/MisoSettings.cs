using DataLoader.Core.Configuration;

namespace DataLoader.MISO;

/// <summary>
/// MISO Data Exchange (LGI API) loader settings, bound from <c>Loaders:MISO</c> (design §8).
///
/// <para>🔒 <see cref="ApiKey"/> ships as <c>SEE_DB</c> and is resolved from
/// <c>core.Param('MISO','ApiKey')</c> by <c>AddLoaderSettings</c>, or overridden by the
/// environment variable <c>DATALOADER_Loaders__MISO__ApiKey</c>. It travels only in the
/// <c>Ocp-Apim-Subscription-Key</c> header — never in a URL, a log line or an exception.</para>
/// </summary>
public sealed class MisoSettings : LoaderSettingsBase
{
    public const string SeeDbSentinel = "SEE_DB";

    /// <summary>The vendor's documented maximum <c>init</c>→<c>date</c> distance (API doc §5.3).</summary>
    public const int MaxMtlfHorizonDays = 7;

    public MisoSettings()
    {
        // Design §8 values for the inherited settings, so a missing appsettings entry cannot
        // silently fall back to the platform base defaults (4 / 3 / 500ms / 300s).
        MaxConcurrentWorkUnits = 4;
        RetryCount = 3;
        RetryDelayMs = 2000;
        WorkUnitTimeoutSeconds = 600;
    }

    public string BaseUrl { get; set; } = "https://apim.misoenergy.org/lgi/v1";

    private string _apiKey = SeeDbSentinel;

    /// <summary>
    /// 🔒 Subscription key. <c>SEE_DB</c> → <c>core.Param('MISO','ApiKey')</c>. Never logged. Trimmed on
    /// assignment (a pasted key often carries a trailing newline), whether it comes from config, the
    /// environment or the SEE_DB resolver.
    /// </summary>
    public string ApiKey
    {
        get => _apiKey;
        set => _apiKey = value?.Trim() ?? string.Empty;
    }

    /// <summary>Per-ATTEMPT budget, applied inside the Polly retry (design §7).</summary>
    public int HttpTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Feed toggle as configured. ⚠ Defaults to <c>null</c>, not to the four feeds: the
    /// configuration binder APPENDS to a pre-populated array (the ICE lesson), so a non-empty
    /// class default could never be switched off. Read <see cref="EffectiveEnabledFeeds"/>.
    /// </summary>
    public string[]? EnabledFeeds { get; set; }

    /// <summary>The configured feeds, or all four (design §1 order) when none are configured.</summary>
    public IReadOnlyList<string> EffectiveEnabledFeeds => EnabledFeeds is { Length: > 0 } f ? f : MisoFeeds.ExecutionOrder;

    /// <summary>Enumeration floor (the earliest market date the vendor accepted on 2026-11-24).</summary>
    public DateOnly StartDate { get; set; } = new(2023, 11, 20);

    /// <summary>Rolling-lookback clamp: <c>Start = max(StartDate, todayEst − MaxLookbackDays)</c>. 5-day margin under the observed ~1,100.</summary>
    public int MaxLookbackDays { get; set; } = 1095;

    /// <summary>Correction horizon: the last <c>DaysBack</c> days are always enumerated (platform default 10).</summary>
    public int DaysBack { get; set; } = 10;

    /// <summary>Settled when <c>age &gt; SettledAfterDays</c> (platform default 10).</summary>
    public int SettledAfterDays { get; set; } = 10;

    /// <summary>A 404 "Empty data" on page 1 is a legitimate empty read only up to this age (design §5).</summary>
    public int ExpectRowsWithinDays { get; set; } = 3;

    /// <summary>MTLF forecast dates per init: <c>D = I+1 … I+MtlfHorizonDays</c>; 1..7.</summary>
    public int MtlfHorizonDays { get; set; } = MaxMtlfHorizonDays;

    /// <summary>
    /// The vendor quota: APIM answers 429 <c>Rate limit is exceeded. Try again in N seconds.</c> (with
    /// <c>Retry-After</c>) past 100 calls inside 60 s, per subscription key [LIVE 2026-11-24].
    /// </summary>
    public const int VendorRequestsPerMinute = 100;

    /// <summary>
    /// Shared sliding-window limit for every MISO request (design §7). Must stay BELOW
    /// <see cref="VendorRequestsPerMinute"/>: a sliding N-per-60s limit keeps every vendor window at or
    /// under N, and the margin absorbs network jitter. The quota is per KEY, so a second process using
    /// the same key (a dev run beside prod) shares it.
    /// </summary>
    public int RequestsPerMinute { get; set; } = 90;

    /// <summary>
    /// Worst-case wall-clock seconds one single-page unit read can take (design §7 coupling):
    /// <c>(RetryCount+1) × (HttpTimeoutSeconds + L) + Σ max(backoff_i, MaxRetryAfter)</c>, where
    /// <c>L = 60 × MaxConcurrentWorkUnits / RequestsPerMinute</c> is the worst limiter wait (the
    /// limiter sits outside the per-attempt timeout) and a <c>Retry-After</c> can stretch each wait
    /// up to <see cref="MisoHttpPolicy.MaxRetryAfter"/>. Every feed fits on one page (API doc §2).
    /// This is stricter than the design's literal <c>(RetryCount+1) × HttpTimeoutSeconds + backoff</c>.
    /// </summary>
    public double WorstCaseUnitSeconds()
    {
        var attempts = Math.Max(0, RetryCount) + 1;
        var limiterWait = 60.0 * Math.Max(1, MaxConcurrentWorkUnits) / Math.Max(1, RequestsPerMinute);
        var total = attempts * (Math.Max(1, HttpTimeoutSeconds) + limiterWait);
        for (var i = 1; i <= Math.Max(0, RetryCount); i++)
            total += Math.Max(MisoHttpPolicy.Backoff(i, RetryDelayMs).TotalSeconds, MisoHttpPolicy.MaxRetryAfter.TotalSeconds);
        return total;
    }

    /// <summary>Startup validation (design §8). One message per violated rule; empty when valid. Never quotes the key.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(BaseUrl) || !Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            errors.Add("Loaders:MISO:BaseUrl must be an absolute https URL.");

        // 🔒 None of these messages quotes the key, not even partly.
        if (string.IsNullOrEmpty(ApiKey) || string.Equals(ApiKey, SeeDbSentinel, StringComparison.Ordinal))
            errors.Add("Loaders:MISO:ApiKey is not configured; set core.Param(LoaderName='MISO', ParamName='ApiKey') or DATALOADER_Loaders__MISO__ApiKey.");
        else if (ApiKey.Any(char.IsControl))
            errors.Add("Loaders:MISO:ApiKey contains control characters (for example an embedded line break); re-enter the value in core.Param(MISO, ApiKey).");

        if (string.IsNullOrWhiteSpace(ConnectionString))
            errors.Add("Loaders:MISO:ConnectionString must be set.");

        if (MtlfHorizonDays < 1 || MtlfHorizonDays > MaxMtlfHorizonDays)
            errors.Add($"Loaders:MISO:MtlfHorizonDays must be between 1 and {MaxMtlfHorizonDays} (was {MtlfHorizonDays}).");

        Positive(errors, nameof(HttpTimeoutSeconds), HttpTimeoutSeconds);
        Positive(errors, nameof(MaxLookbackDays), MaxLookbackDays);
        Positive(errors, nameof(DaysBack), DaysBack);
        Positive(errors, nameof(SettledAfterDays), SettledAfterDays);
        Positive(errors, nameof(ExpectRowsWithinDays), ExpectRowsWithinDays);
        Positive(errors, nameof(RequestsPerMinute), RequestsPerMinute);
        Positive(errors, nameof(MaxConcurrentWorkUnits), MaxConcurrentWorkUnits);
        Positive(errors, nameof(WorkUnitTimeoutSeconds), WorkUnitTimeoutSeconds);
        if (RetryCount < 0) errors.Add($"Loaders:MISO:RetryCount must be 0 or more (was {RetryCount}).");
        if (RetryDelayMs < 0) errors.Add($"Loaders:MISO:RetryDelayMs must be 0 or more (was {RetryDelayMs}).");

        // The never-settle-a-not-yet invariant (design §6): a young empty read (age ≤
        // ExpectRowsWithinDays) must always fall in the hot zone (age ≤ SettledAfterDays).
        if (SettledAfterDays < ExpectRowsWithinDays)
            errors.Add($"Loaders:MISO:SettledAfterDays ({SettledAfterDays}) must be at least ExpectRowsWithinDays ({ExpectRowsWithinDays}), " +
                       "or a not-yet-published day could be recorded as settled with zero rows.");

        if (DaysBack > MaxLookbackDays)
            errors.Add($"Loaders:MISO:DaysBack ({DaysBack}) must not exceed MaxLookbackDays ({MaxLookbackDays}).");

        if (EnabledFeeds is not null)
            foreach (var id in EnabledFeeds.Where(id => MisoFeeds.ById(id) is null))
                errors.Add($"Loaders:MISO:EnabledFeeds contains an unknown feed '{id}'; known feeds: {string.Join(", ", MisoFeeds.ExecutionOrder)}.");

        if (WorkUnitTimeoutSeconds > 0)
        {
            var worst = WorstCaseUnitSeconds();
            if (worst >= WorkUnitTimeoutSeconds)
                errors.Add($"Loaders:MISO:WorkUnitTimeoutSeconds ({WorkUnitTimeoutSeconds}) must exceed the worst-case unit read ({worst:0}s: " +
                           "(RetryCount+1) x (HttpTimeoutSeconds + limiter wait) + backoff; design §7).");
        }

        return errors;
    }

    private static void Positive(List<string> errors, string name, int value)
    {
        if (value < 1) errors.Add($"Loaders:MISO:{name} must be at least 1 (was {value}).");
    }
}
