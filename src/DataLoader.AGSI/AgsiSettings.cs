using DataLoader.Core.Configuration;

namespace DataLoader.AGSI;

/// <summary>
/// How the hot-zone / undated "latest" work-unit resume key varies between runs
/// (design §3.3). Mirrors CWG's <c>HotKeyStrategy</c> semantics.
/// </summary>
public enum AgsiHotKeyStrategy
{
    /// <summary>Suffix the key with the CET run date → one re-pull per calendar day (lighter).</summary>
    RunDate,

    /// <summary>Suffix the key with the run id → every invocation re-pulls.</summary>
    RunId
}

/// <summary>
/// GIE AGSI loader settings, bound from <c>Loaders:AGSI</c> (design §9). Inherits
/// the common bits (connection string, retry, concurrency, per-unit timeout) and
/// adds the API, windowing and resume-heuristic fields.
///
/// <para>
/// <see cref="ApiKey"/> defaults to the <c>SEE_DB</c> sentinel and is resolved at
/// run time from the platform DB (<c>core.Param</c>) by
/// <c>AddLoaderSettings&lt;AgsiSettings&gt;</c>, or overridden via the environment
/// variable <c>DATALOADER_Loaders__AGSI__ApiKey</c>. It is sent as the HTTP request
/// header <c>x-key</c> to endpoint 2 ONLY (endpoint 1 <c>/api/about</c> is public)
/// and is <b>never written to logs</b> — request URLs carry no secret.
/// </para>
/// </summary>
public sealed class AgsiSettings : LoaderSettingsBase
{
    /// <summary>API host (paths <c>/api/about</c> and <c>/api</c>).</summary>
    public string BaseUrl { get; set; } = "https://agsi.gie.eu";

    /// <summary>API key, sent as the <c>x-key</c> header to endpoint 2 only. Resolved from <c>core.Param</c>.</summary>
    public string ApiKey { get; set; } = "SEE_DB";

    /// <summary>Per-request timeout on the shared <see cref="HttpClient"/>.</summary>
    public int HttpTimeoutSeconds { get; set; } = 60;

    /// <summary>Endpoints to run this pass; matched case-insensitively against pipeline endpoint ids. Defaults to both.</summary>
    public string[] EnabledEndpoints { get; set; } = { "About", "Storage" };

    /// <summary>Storage trailing-window length in days back from the newest requestable gas day (About ignores it). Default 21.</summary>
    public int DaysBack { get; set; } = 21;

    /// <summary>
    /// Requested-date age boundary (days) separating the settled zone (stable key → loaded once
    /// then skipped forever) from the hot zone (run-varying key → re-pulled every run) — design §3.3.
    /// Default 21 (= <see cref="DaysBack"/>), so the whole window is hot and re-pulled each run
    /// (the intended incremental posture for this small European daily feed). Set below
    /// <see cref="DaysBack"/> to grow a cheap settled tail.
    /// </summary>
    public int SettledAfterDays { get; set; } = 21;

    /// <summary>Hot-zone key suffix strategy — the hot storage window and the undated entities re-pull cadence (design §3.3).</summary>
    public AgsiHotKeyStrategy HotZoneKeyStrategy { get; set; } = AgsiHotKeyStrategy.RunDate;

    /// <summary>
    /// Optional global client-side throttle (requests/second). <c>null</c> or ≤ 0 = unlimited.
    /// GIE publishes no hard number — pace gently and back off on 429 (design §9 / API doc). Default 1.
    /// </summary>
    public double? RequestsPerSecond { get; set; } = 1;
}
