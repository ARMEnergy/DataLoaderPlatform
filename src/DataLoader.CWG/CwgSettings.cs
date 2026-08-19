using DataLoader.Core.Configuration;

namespace DataLoader.CWG;

/// <summary>
/// CWG (Commodity Weather Group) loader settings, bound from <c>Loaders:CWG</c>
/// (design §7). Inherits the common bits (connection string, retry, concurrency,
/// per-unit timeout) and adds the API, windowing and resume-heuristic fields.
///
/// <para>
/// <see cref="ApiKey"/> defaults to the <c>SEE_DB</c> sentinel and is resolved at
/// run time from the platform DB (<c>core.Param</c>) by
/// <c>AddLoaderSettings&lt;CwgSettings&gt;</c>, or overridden via the environment
/// variable <c>DATALOADER_Loaders__CWG__ApiKey</c>. It is passed as the
/// <c>?apikey=</c> query-string parameter and is never written to logs (request
/// URIs are logged without their query string).
/// </para>
/// </summary>
public sealed class CwgSettings : LoaderSettingsBase
{
    /// <summary>API base URL (host + <c>/v1</c>).</summary>
    public string BaseUrl { get; set; } = "https://api.commoditywx.com/v1";

    /// <summary>API key, passed as the <c>?apikey=</c> query-string parameter. Resolved from <c>core.Param</c>.</summary>
    public string ApiKey { get; set; } = "SEE_DB";

    /// <summary>Per-request timeout on the shared <see cref="HttpClient"/>.</summary>
    public int HttpTimeoutSeconds { get; set; } = 60;

    /// <summary>Endpoints to run this pass; matched case-insensitively against pipeline endpoint ids. Defaults to all 18.</summary>
    public string[] EnabledEndpoints { get; set; } = CwgDescriptors.AllIds;

    /// <summary>Dated-endpoint enumeration window in days back from the newest represented date (undated endpoints ignore it). Default 21.</summary>
    public int DaysBack { get; set; } = 21;

    /// <summary>
    /// Represented-date age boundary (days) separating the settled zone (stable key → loaded
    /// once then skipped forever) from the hot zone (run-varying key → re-pulled every run) —
    /// design §3.1. Days older than this within the <see cref="DaysBack"/> window settle after a
    /// successful load; the most recent <c>SettledAfterDays</c> days stay hot to catch
    /// not-yet-published / late / revised files. Default 7 (&lt; the 21-day window, so the
    /// settled zone is non-empty).
    /// </summary>
    public int SettledAfterDays { get; set; } = 7;

    /// <summary>Hot-zone key suffix strategy — undated "latest" files and the hot dated window (design §3.1).</summary>
    public HotKeyStrategy HotZoneKeyStrategy { get; set; } = HotKeyStrategy.RunDate;

    /// <summary>Filters <c>RegionKind == Geography</c> descriptors only (design §2.1 note). Default all three.</summary>
    public string[] Geographies { get; set; } = { "northamerica", "asia", "europe" };

    /// <summary>Optional global client-side throttle (requests/second). <c>null</c> or ≤ 0 = unlimited (design §8).</summary>
    public double? RequestsPerSecond { get; set; }
}
