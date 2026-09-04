using DataLoader.Core.Configuration;

namespace DataLoader.ICE;

/// <summary>
/// How the work-unit resume key varies between runs for a HOT trade date.
///
/// <para>All tokens are stamped in <b>UTC</b>. <see cref="RunHour"/>'s <c>yyyyMMddHH</c>
/// is strictly monotonic only in UTC — <c>01:00</c> Central occurs twice on a
/// fall-back night, so a local-zone hour token would repeat, the key would go
/// backwards, and an already-recorded success would suppress a legitimate re-pull
/// for an hour.</para>
/// </summary>
public enum IceHotKeyStrategy
{
    /// <summary>
    /// Suffix the key with the UTC run date <c>yyyyMMdd</c> → one re-pull per UTC day.
    /// <b>The default</b>: ICE publishes settlement files once per trading day, so a
    /// second run the same day has nothing new to find and idempotently skips.
    /// </summary>
    RunDate,

    /// <summary>Suffix with UTC <c>yyyyMMddHH</c> → one re-pull per clock hour, for intraday schedules.</summary>
    RunHour,

    /// <summary>Suffix with the run id → <b>every</b> invocation re-pulls (diagnostics / a forced re-pull).</summary>
    RunId
}

/// <summary>
/// ICE loader settings, bound from <c>Loaders:ICE</c> via
/// <c>AddLoaderSettings&lt;IceSettings&gt;</c> (which also wires the <c>SEE_DB</c>
/// indirection). Inherits the common bits (connection string, retry, concurrency,
/// per-unit timeout) and adds the SSO credentials, the window and the file cache.
///
/// <para>
/// <see cref="UserId"/> and <see cref="Password"/> ship as the literal sentinel
/// <c>"SEE_DB"</c> and resolve at run time from
/// <c>core.Param(LoaderName='ICE', ParamName='UserId'|'Password')</c>, or from
/// <c>DATALOADER_Loaders__ICE__UserId</c> / <c>__Password</c>. They are never
/// logged and never written to <c>arm.FileLog.RequestPath</c>.
/// </para>
/// </summary>
public sealed class IceSettings : LoaderSettingsBase
{
    // ---------------------------------------------------------------- auth

    /// <summary>SSO sign-on endpoint. Note the <c>theice.com</c> host — the downloads host is <c>ice.com</c>.</summary>
    public string SsoUrl { get; set; } = "https://sso.theice.com/api/authenticateTfa";

    /// <summary>Base for every file download. Feed paths are appended to this.</summary>
    public string DownloadBaseUrl { get; set; } = "https://downloads.ice.com/";

    /// <summary>SSO user. <c>"SEE_DB"</c> in appsettings; resolved from core.Param.</summary>
    public string UserId { get; set; } = "SEE_DB";

    /// <summary>SSO password. <c>"SEE_DB"</c> in appsettings; resolved from core.Param.</summary>
    public string Password { get; set; } = "SEE_DB";

    /// <summary>SSO application key. Not a secret — it selects the entitlement set.</summary>
    public string AppKey { get; set; } = "ICEDOWNLOADS";

    /// <summary>Timeout for one SSO or download request.</summary>
    public int HttpTimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// Client-side pacing, in requests per minute. Zero or negative disables pacing.
    ///
    /// <para>
    /// <b>ICE enforces a hard limit of 30 requests per minute</b>, stated verbatim in
    /// the body of its own <c>429</c> response (verified live 2026-09-01):
    /// <i>"You are limited to 30 requests per minute … Please retry after 1 min."</i>
    /// It is a RATE limit, not a concurrency gate — 20 simultaneous requests all
    /// succeeded, while exceeding 30 in a minute blocked every request for 60 seconds.
    /// </para>
    /// <para>
    /// The default is <b>25</b>, not 30, deliberately. Cloudflare counts over a
    /// sliding window, so pacing at exactly the limit puts a boundary request on
    /// either side of the line depending on clock skew and network jitter; 25 costs
    /// about four extra minutes on a full cold run and removes that whole class of
    /// failure. <see cref="IceHttpPolicy"/> still backs off on any <c>429</c> that
    /// slips through — for instance if something else is using the same ICE account
    /// at the same time, since the budget is account-wide and this limiter cannot see
    /// those requests.
    /// </para>
    /// <para>
    /// ⚠ <b>This is the setting that governs how long a run takes.</b> A cold run is
    /// 18 feeds × (DaysBack + 1) dates ≈ 558 requests ≈ 22 minutes at 25/min. Warm
    /// runs are far shorter because the local file cache serves anything already
    /// downloaded — see <see cref="DownloadDirectory"/> and
    /// <see cref="FileRetentionDays"/>.
    /// </para>
    /// </summary>
    public int RequestsPerMinute { get; set; } = 25;

    // ---------------------------------------------------------------- window

    /// <summary>
    /// How many days back from today (US Central) to enumerate trade dates.
    /// The window is <c>[today - DaysBack, today]</c> inclusive.
    /// </summary>
    public int DaysBack { get; set; } = 30;

    /// <summary>
    /// A trade date older than this many days is treated as SETTLED and gets a
    /// stable resume key — loaded once, then skipped forever. A newer one is HOT
    /// and gets a run-varying key so revised settlements are re-pulled.
    ///
    /// <para>
    /// <b>With the shipped 30/30 defaults the settled zone is EMPTY and every date
    /// is hot</b>: the oldest date in the window is exactly 30 days old and
    /// <c>age &gt; 30</c> is false. That is deliberate — ICE republishes corrected
    /// settlements, and a stable key would freeze a corrected price at its original
    /// value while the loader reported clean runs. It matches EvolutionMarkets
    /// (30/30) and NGI (60/60).
    /// </para>
    /// <para>
    /// Setting this BELOW <see cref="DaysBack"/> creates a real settled zone and is
    /// logged as a warning at startup, because it is nearly always a
    /// misconfiguration rather than an intent.
    /// </para>
    /// </summary>
    public int SettledAfterDays { get; set; } = 30;

    /// <summary>How a hot date's key varies between runs. See <see cref="IceHotKeyStrategy"/>.</summary>
    public IceHotKeyStrategy HotKeyStrategy { get; set; } = IceHotKeyStrategy.RunDate;

    // ---------------------------------------------------------------- file cache

    /// <summary>
    /// Where downloaded files are kept, laid out <c>&lt;dir&gt;/&lt;FeedId&gt;/&lt;filename&gt;</c>.
    /// A relative path resolves against the host's base directory, so the default
    /// keeps files with the deployment rather than in a system temp folder.
    /// </summary>
    public string DownloadDirectory { get; set; } = "files/ICE";

    /// <summary>
    /// Delete cached files older than this many days. Swept once per run, before
    /// enumeration. Zero or negative disables the sweep (files are kept forever).
    ///
    /// <para>
    /// Retention shorter than <see cref="DaysBack"/> means dates between the two
    /// re-download each time they go hot — a bandwidth/disk trade-off, not a
    /// correctness issue. The shipped 7/30 pair favours disk.
    /// </para>
    /// </summary>
    public int FileRetentionDays { get; set; } = 7;

    /// <summary>
    /// When false (the default), a file already on disk and non-empty is REUSED
    /// instead of being downloaded again. When true, every work unit re-downloads.
    /// </summary>
    public bool ForceDownload { get; set; } = false;

    // ---------------------------------------------------------------- selection

    /// <summary>
    /// Feed ids to run. An unknown id is warned about and ignored rather than
    /// throwing — a stale config entry should not stop the whole loader.
    /// </summary>
    public List<string> EnabledFeeds { get; set; } = new()
    {
        "EnvFutures", "EnvOptions",
        "NgxGas", "NgxPower", "IceGas", "IceNgl", "IceOil", "IceOilCa",
        "CrudeIndex", "CrudeIndexTrades",
        "PowerFutures", "PowerOptions",
        "FcaOptions", "FusFinOptions", "FusSoftOptions",
        "IfllOptions",
        "GasOptions", "OilOptions"
    };
}
