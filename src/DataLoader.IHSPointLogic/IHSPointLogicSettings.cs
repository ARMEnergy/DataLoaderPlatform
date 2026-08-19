using DataLoader.Core.Configuration;

namespace DataLoader.IHSPointLogic;

/// <summary>
/// IHSPointLogic (S&amp;P Global / IHS Markit PointLogic) loader settings, bound from
/// <c>Loaders:IHSPointLogic</c> (design §8). Inherits the common bits (connection
/// string, retry, concurrency, per-unit timeout) and adds the API, windowing and
/// resume-heuristic fields.
///
/// <para>
/// <see cref="ClientId"/> / <see cref="ClientSecret"/> default to the <c>SEE_DB</c>
/// sentinel and are resolved at run time from the platform DB (<c>core.Param</c>) by
/// <c>AddLoaderSettings&lt;IHSPointLogicSettings&gt;</c>, or overridden via the
/// environment variables <c>DATALOADER_Loaders__IHSPointLogic__ClientId</c> /
/// <c>…__ClientSecret</c>. They form the static HTTP Basic header
/// (<c>Authorization: Basic base64(ClientId:ClientSecret)</c>) stamped on every call
/// and are <b>never written to logs</b>.
/// </para>
/// </summary>
public sealed class IHSPointLogicSettings : LoaderSettingsBase
{
    /// <summary>API base host (paths under <c>cs/v1/…</c>).</summary>
    public string BaseUrl { get; set; } = "https://api.connect.ihsmarkit.com";

    /// <summary>Basic-auth username (PAT user id). Resolved from <c>core.Param</c>; never logged.</summary>
    public string ClientId { get; set; } = "SEE_DB";

    /// <summary>Basic-auth password (PAT secret). Resolved from <c>core.Param</c>; never logged.</summary>
    public string ClientSecret { get; set; } = "SEE_DB";

    /// <summary>Per-request timeout on the shared <see cref="HttpClient"/>.</summary>
    public int HttpTimeoutSeconds { get; set; } = 60;

    /// <summary>Endpoints to run this pass; matched case-insensitively against pipeline endpoint ids. Defaults to all 25.</summary>
    public string[] EnabledEndpoints { get; set; } = PlDescriptors.AllIds;

    /// <summary>reportDate window (days back from the run's UTC date) for the two SD-by-region/subregion endpoints; all others ignore it. Default 21.</summary>
    public int DaysBack { get; set; } = 21;

    /// <summary>
    /// Requested-date age boundary (days) separating the settled zone (stable key → loaded once then
    /// skipped forever) from the hot zone (run-varying key → re-pulled every run) for archetype D
    /// (design §4). Default 7 (&lt; the 21-day window, so the settled tail is non-empty).
    /// </summary>
    public int SettledAfterDays { get; set; } = 7;

    /// <summary>
    /// Hot-zone key suffix strategy — snapshots + the hot dated window (design §4 / §B.4). Defaults to
    /// <see cref="PlHotKeyStrategy.RunHour"/> so a scheduled hourly endpoint re-pulls each UTC hour
    /// while a same-hour re-run still idempotently skips.
    /// </summary>
    public PlHotKeyStrategy HotZoneKeyStrategy { get; set; } = PlHotKeyStrategy.RunHour;

    /// <summary>
    /// Optional global client-side throttle (requests/second). <c>null</c> or ≤ 0 = unlimited. The
    /// probe surfaced no hard limit — pace gently and back off on 429 (design §8). Default 2.
    /// </summary>
    public double? RequestsPerSecond { get; set; } = 2;

    /// <summary><c>pointIds</c> chunk size for the PointVolume batched fact (design §4 E). Default 50.</summary>
    public int PointVolumeBatchSize { get; set; } = 50;
}
