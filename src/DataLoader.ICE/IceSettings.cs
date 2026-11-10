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
    RunId,

    /// <summary>
    /// Suffix with the FILE's own version, read from a <c>HEAD</c>'s <c>Last-Modified</c>
    /// → a date is re-pulled exactly when ICE publishes or re-publishes its file, and
    /// skipped (no download) otherwise. <b>What production ships</b> (since 2026-11-10).
    ///
    /// <para>
    /// The other three strategies re-pull on a CLOCK, which has two costs this one
    /// avoids. A run that reaches today's file before ICE publishes it (~17:00 ET)
    /// records it done with 0 rows, and under <see cref="RunDate"/> nothing asks again
    /// until the UTC date rolls at 18:00 Central — so a file published at 16:05
    /// Central loaded at 18:33. And a clock-driven re-pull is served from the local
    /// cache, so a file ICE corrects days later (observed: the 09-30 gas file was
    /// replaced on 10-08) was never actually re-downloaded.
    /// </para>
    /// <para>
    /// Each <c>HEAD</c> costs one request of ICE's 30/minute budget, so only the
    /// <see cref="IceSettings.VersionCheckDaysBack"/> most recent days are checked on
    /// an ordinary run, and the whole window on a run starting in one of
    /// <see cref="IceSettings.FullVersionCheckHoursCentral"/>.
    /// </para>
    /// </summary>
    LastModified
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
    /// 21 feeds × (DaysBack + 1) dates ≈ 651 requests ≈ 26 minutes at 25/min. Warm
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

    /// <summary>
    /// <see cref="IceHotKeyStrategy.LastModified"/> only: on an ORDINARY run, the
    /// version of every hot date in <c>[today - VersionCheckDaysBack, today]</c>
    /// (US Central) is checked with a <c>HEAD</c>; older hot dates are not enumerated
    /// at all and wait for the next full check.
    ///
    /// <para>
    /// Ships <b>3</b>: four dates per feed, 84 requests (~3.5 min at 25/min) — cheap
    /// enough for a 30-minute schedule. Three, not one, so that a MONDAY run still
    /// reaches back to Friday and catches a Friday file corrected over the weekend.
    /// Clamped to <c>[0, DaysBack]</c>.
    /// </para>
    /// </summary>
    public int VersionCheckDaysBack { get; set; } = 3;

    /// <summary>
    /// <see cref="IceHotKeyStrategy.LastModified"/> only: a run whose start falls in
    /// one of these US Central clock hours (0–23) checks the version of EVERY hot date
    /// in the <see cref="DaysBack"/> window — 651 <c>HEAD</c>s, ~26 min at 25/min.
    ///
    /// <para>
    /// Ships <b>[21]</b>, matching the daily 21:30 Central run, which has nothing
    /// scheduled after it until 15:03 the next afternoon. This is what catches a file
    /// ICE re-publishes days after its trade date (the 09-30 and 10-02 gas files were
    /// replaced 8 and 5 days later). An EMPTY list means no run ever looks past
    /// <see cref="VersionCheckDaysBack"/>, which is warned about at startup.
    /// </para>
    /// <para>
    /// Central, not UTC: this is an operator-facing schedule matched against the Task
    /// Scheduler trigger, not a resume key, so the fall-back-night ambiguity that keeps
    /// key tokens in UTC does not apply — at worst one run's check is widened.
    /// </para>
    /// <para>
    /// ⚠ <b>The class default is deliberately EMPTY</b>; the <c>[21]</c> lives in
    /// <c>appsettings.json</c>. The configuration binder APPENDS to a list that already
    /// has elements, so a non-empty initializer would turn a configured <c>[20]</c>
    /// into <c>[21, 20]</c> and make <c>[]</c> impossible to express.
    /// </para>
    /// </summary>
    public List<int> FullVersionCheckHoursCentral { get; set; } = new();

    // ---------------------------------------------------------------- file cache

    /// <summary>
    /// Where downloaded files are kept, laid out <c>&lt;dir&gt;/&lt;FeedId&gt;/&lt;filename&gt;</c>.
    /// A relative path resolves against the host's base directory; an absolute one
    /// is used as given.
    ///
    /// <para>⚠ <b>Production configures this ABSOLUTE, OUTSIDE the deployment folder,
    /// and it must stay that way.</b> When it lived under the deployed folder
    /// (<c>files/ICE</c>, relative), every deploy destroyed the cache:
    /// <c>Publish-ToProd.ps1</c> MOVES the target's contents into
    /// <c>Old\&lt;timestamp&gt;\</c>, so the whole cache went with it. Measured
    /// 2026-10-28 — each archive held ~1.7 GB of orphaned cache across 12 archives
    /// (14.4 GB total), and the run after each deploy re-downloaded all 462 files
    /// in one burst (all 22 dates × 21 feeds written within six minutes).</para>
    ///
    /// <para>That is expensive, not merely untidy: the ICE account is capped at
    /// 30 requests/minute <b>account-wide</b>, so a full rebuild is 15+ minutes of
    /// solid re-fetching that competes with real work. It also made
    /// <see cref="FileRetentionDays"/> look broken — nothing was ever older than
    /// 7 days because the clock kept being reset.</para>
    ///
    /// <para>A developer wanting files somewhere else should override this in
    /// <c>appsettings.Local.json</c>, which the host already loads as an optional
    /// overlay and which is git-ignored.</para>
    /// </summary>
    public string DownloadDirectory { get; set; } = "files/ICE";

    /// <summary>
    /// Delete cached files older than this many days. Swept once per run, before
    /// enumeration. Zero or negative disables the sweep (files are kept forever).
    ///
    /// <para>
    /// Retention shorter than <see cref="DaysBack"/> means dates between the two
    /// re-download each time they go hot — a bandwidth/disk trade-off, not a
    /// correctness issue.
    ///
    /// <para>⚠ Ships <b>31</b>, deliberately one more than <see cref="DaysBack"/>,
    /// so the whole window stays cached and nothing is ever re-fetched merely
    /// because it expired. Bandwidth is the scarcer resource here: the ICE account
    /// is capped at 30 requests/minute <b>account-wide</b>, shared across all 21
    /// feeds, while the full cache is only ~1.7 GB.</para>
    ///
    /// <para>It shipped as 7 until 2026-10-28, chosen to favour disk. That was the
    /// wrong trade, and the evidence had been invisible: the cache lived inside the
    /// deployed folder, so every deploy destroyed it (see
    /// <see cref="DownloadDirectory"/>) and nothing ever survived long enough for a
    /// 7-day sweep to delete. Retention only became observable once the cache moved
    /// outside the deployment.</para>
    /// </summary>
    public int FileRetentionDays { get; set; } = 31;

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
        "GasOptions", "OilOptions",
        "IcePhysEnv", "NgxPhysEnv", "IcePhysEnvOptions"
    };
}
