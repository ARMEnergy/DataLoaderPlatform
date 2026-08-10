using DataLoader.Core.Configuration;

namespace DataLoader.StormVista;

/// <summary>Run mode: rolling recent window vs. an operator-supplied historical range.</summary>
public enum StormVistaMode
{
    /// <summary>Re-pull the last <see cref="StormVistaSettings.DaysBack"/> days each run (scheduled default).</summary>
    Incremental,

    /// <summary>Walk a bounded [BackfillStart, BackfillEnd] range, chunked by <see cref="StormVistaSettings.ChunkDays"/>.</summary>
    Backfill
}

/// <summary>How the volatile "hot zone" work-unit key varies between runs (design §4).</summary>
public enum HotZoneKeyStrategy
{
    /// <summary>Suffix the key with the run id → every invocation re-pulls the hot window (catches intraday cycles).</summary>
    RunId,

    /// <summary>Suffix the key with the run date → one re-pull per calendar day (lighter; misses intraday cycles).</summary>
    RunDate
}

/// <summary>
/// StormVista WDD loader settings, bound from <c>Loaders:StormVista</c>.
///
/// Inherits the common bits (connection string, retry, concurrency, per-unit
/// timeout) and adds the API, windowing and resume-heuristic fields (design §11).
///
/// <para>
/// <see cref="ApiKey"/> must be supplied via the environment variable
/// <c>DATALOADER_Loaders__StormVista__ApiKey</c> in production — never committed
/// to appsettings.json — and is never written to logs (request URIs are logged
/// without their query string).
/// </para>
/// </summary>
public sealed class StormVistaSettings : LoaderSettingsBase
{
    /// <summary>API base URL (the dedicated <c>api.</c> subdomain with the <c>/v1</c> prefix).</summary>
    public string BaseUrl { get; set; } = "https://api.stormvistawxmodels.com/v1";

    /// <summary>API key, passed as the <c>?apikey=</c> query-string parameter. Environment only.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Per-request timeout on the shared <see cref="HttpClient"/>.</summary>
    public int HttpTimeoutSeconds { get; set; } = 60;

    /// <summary>Feeds to run this pass; matched case-insensitively against pipeline feed ids.</summary>
    public string[] EnabledFeeds { get; set; } = { "Daily", "Regional" };

    /// <summary>Selects the date range and chunking (design §6).</summary>
    public StormVistaMode Mode { get; set; } = StormVistaMode.Incremental;

    /// <summary>Incremental window length in days (defaults to <see cref="SettledAfterDays"/> so it fully covers the hot zone).</summary>
    public int DaysBack { get; set; } = 21;

    /// <summary>Init-date age boundary (days) separating the settled zone (stable key) from the hot zone (volatile key) — design §4.</summary>
    public int SettledAfterDays { get; set; } = 21;

    /// <summary>Backfill range start (required in Backfill mode; archive begins 2018-07-08).</summary>
    public DateTime? BackfillStart { get; set; }

    /// <summary>Backfill range end (defaults to today UTC).</summary>
    public DateTime? BackfillEnd { get; set; }

    /// <summary>Backfill window size in days (design §6.2); windows are walked oldest → newest.</summary>
    public int ChunkDays { get; set; } = 30;

    /// <summary>Optional global client-side throttle (requests/second). <c>null</c> or ≤ 0 = unlimited (design §7).</summary>
    public double? RequestsPerSecond { get; set; }

    /// <summary>Hot-zone key suffix strategy (design §4 / §13.1).</summary>
    public HotZoneKeyStrategy HotZoneKeyStrategy { get; set; } = HotZoneKeyStrategy.RunId;

    /// <summary>
    /// When <c>true</c>, models flagged <c>IsExperimental</c> in the reference are
    /// excluded from enumeration. Default <c>false</c> keeps the current
    /// "everything" scope unchanged.
    /// </summary>
    public bool ExcludeExperimental { get; set; }

    // ---- Optional matrix filters (narrow the reference-driven enumeration; null = "everything"). ----
    public string[]? Models { get; set; }
    public string[]? Cycles { get; set; }
    public string[]? Types { get; set; }
    public string[]? RegionSets { get; set; }
}
