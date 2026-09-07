using DataLoader.Core.Configuration;

namespace DataLoader.EOX;

/// <summary>
/// How the work-unit resume key varies between runs for a HOT curve date.
///
/// <para>All tokens are stamped in <b>UTC</b>. <see cref="RunHour"/>'s
/// <c>yyyyMMddHH</c> is strictly monotonic only in UTC — <c>01:00</c> Central
/// occurs twice on a fall-back night, so a local-zone hour token would repeat, the
/// key would go backwards, and an already-recorded success would suppress a
/// legitimate re-pull for an hour.</para>
/// </summary>
public enum EoxHotKeyStrategy
{
    /// <summary>
    /// Suffix the key with the UTC run date <c>yyyyMMdd</c> → one re-pull per UTC
    /// day. <b>The default</b>: EOX publishes one 14:30-Central snap per trading
    /// day, so a second run the same day has nothing new to find.
    /// </summary>
    RunDate,

    /// <summary>Suffix with UTC <c>yyyyMMddHH</c> → one re-pull per clock hour, for intraday schedules.</summary>
    RunHour,

    /// <summary>Suffix with the run id → <b>every</b> invocation re-pulls (diagnostics / a forced re-pull).</summary>
    RunId
}

/// <summary>
/// EOX FTP loader settings, bound from <c>Loaders:EOX</c> via
/// <c>AddLoaderSettings&lt;EoxSettings&gt;</c> (which also wires the <c>SEE_DB</c>
/// indirection). Inherits the common bits (connection string, retry, concurrency,
/// per-unit timeout) and adds the FTP connection, the date window and the file
/// naming.
///
/// <para>
/// <see cref="Username"/> and <see cref="Password"/> ship as the literal sentinel
/// <c>"SEE_DB"</c> and resolve at run time from
/// <c>core.Param(LoaderName='EOX', ParamName='Username'|'Password')</c>, or from
/// <c>DATALOADER_Loaders__EOX__Username</c> / <c>__Password</c>. They are never
/// logged and never written to <c>arm.FileLog.RequestPath</c>.
/// </para>
/// </summary>
public sealed class EoxSettings : LoaderSettingsBase
{
    // ---------------------------------------------------------------- connection

    public string FtpHost { get; set; } = "ftp.eoxlive.com";
    public int FtpPort { get; set; } = 21;

    /// <summary>FTP user. <c>"SEE_DB"</c> in appsettings; resolved from core.Param.</summary>
    public string Username { get; set; } = "SEE_DB";

    /// <summary>FTP password. <c>"SEE_DB"</c> in appsettings; resolved from core.Param.</summary>
    public string Password { get; set; } = "SEE_DB";

    /// <summary>
    /// Directory holding the daily files. EOX drops everything in the ROOT — all
    /// 18,486 files, every series and every format, in one flat directory.
    /// </summary>
    public string RemoteDirectory { get; set; } = "/";

    /// <summary>
    /// Explicit FTPS (AUTH TLS over the same port). <b>On by default</b>, unlike
    /// the OPIS and Argus drivers.
    ///
    /// <para>
    /// The EOX server is Pure-FTPd and advertises <c>AUTH TLS</c>; listing and
    /// downloading over explicit TLS were both verified live on 2026-09-04 with a
    /// certificate-validating client. Plain FTP sends the account password in
    /// cleartext, so there is no reason to prefer it here.
    /// </para>
    /// </summary>
    public bool UseFtps { get; set; } = true;

    /// <summary>
    /// Accept any server certificate when <see cref="UseFtps"/> is on.
    ///
    /// <para>
    /// Defaults to <c>false</c> — strict validation — because the live certificate
    /// validated cleanly. It was verified with curl's CA bundle, while .NET on
    /// Windows validates against the machine store, so if a deployment host trips
    /// on the chain this is the escape hatch: it keeps the channel encrypted while
    /// dropping authentication of the peer.
    /// </para>
    /// </summary>
    public bool ValidateAnyCertificate { get; set; }

    /// <summary>Passive mode. Required behind most firewalls and what EOX expects.</summary>
    public bool UsePassiveMode { get; set; } = true;

    /// <summary>
    /// FTP connect/read timeout in milliseconds. Higher than the OPIS default
    /// because the NaturalGas file is ~4 MB rather than 14 KB, and because listing
    /// the root means listing 18k entries (~1.7 s observed).
    /// </summary>
    public int FtpTimeoutMs { get; set; } = 180_000;

    // ---------------------------------------------------------------- window

    /// <summary>
    /// How many days back from today (US Central) to enumerate curve dates. The
    /// window is <c>[today - DaysBack, today]</c> inclusive.
    ///
    /// <para>
    /// ⚠ This is the setting that governs how long a run takes. One work unit is
    /// one file download, and the three feeds together average ~2.5 MB per date, so
    /// the shipped 30 gives 3 × 31 = 93 units ≈ 230 MB on a run where every date is
    /// hot. See <see cref="SettledAfterDays"/> for how to shrink that.
    /// </para>
    /// </summary>
    public int DaysBack { get; set; } = 30;

    /// <summary>
    /// A curve date older than this many days is treated as SETTLED and gets a
    /// resume key that varies only with the file's own content stamp — so it is
    /// loaded once and then skipped for as long as EOX leaves the file alone. A
    /// newer date is HOT and gets an additional run-varying component, so it is
    /// re-pulled even when the stamp has not moved.
    ///
    /// <para>
    /// <b>With the shipped 30/30 defaults the settled zone is EMPTY and every date
    /// is hot</b>: the oldest date in the window is exactly 30 days old and
    /// <c>age &gt; 30</c> is false. That is deliberate and matches ICE (30/30),
    /// EvolutionMarkets (30/30) and NGI (60/60) — a stable key would freeze a
    /// corrected curve at its original value while the loader reported clean runs.
    /// </para>
    /// <para>
    /// It also means the whole 30-day window is re-downloaded on every run day.
    /// This loader's resume key already embeds the server-reported last-modified
    /// stamp and size, which detects a republished file on its own, so
    /// <b>lowering this to 2 or 3 makes steady-state runs download only what
    /// actually changed</b> without losing revision detection. Setting it BELOW
    /// <see cref="DaysBack"/> is logged at startup so the choice is visible in the
    /// run log rather than only in config.
    /// </para>
    /// </summary>
    public int SettledAfterDays { get; set; } = 30;

    /// <summary>How a hot date's key varies between runs. See <see cref="EoxHotKeyStrategy"/>.</summary>
    public EoxHotKeyStrategy HotKeyStrategy { get; set; } = EoxHotKeyStrategy.RunDate;

    /// <summary>
    /// When true, the run id is folded into EVERY work-unit key, hot or settled, so
    /// <c>core.LoadLog</c> cannot skip anything.
    ///
    /// <para>
    /// The default (<c>false</c>) still enumerates the whole window on every run; it
    /// just skips dates whose file has not changed since the last successful load.
    /// Since re-merging identical content is a no-op the database ends in the same
    /// state either way. Turn this on for a forced re-merge, e.g. after changing a
    /// merge proc.
    /// </para>
    /// </summary>
    public bool ForceReprocess { get; set; }

    // ---------------------------------------------------------------- files

    /// <summary>
    /// The fixed time token in every file name — <c>EOD_CSV_NG_20260904_<b>1430</b>.csv</c>.
    /// 14:30 US Central is EOX's end-of-day snap. Every one of the 10,083 in-scope
    /// files carries this token and no other, so it is a constant rather than a
    /// pattern; it is configurable only so a future second daily snap does not need
    /// a code change.
    /// </summary>
    public string FileNameTimeToken { get; set; } = "1430";

    /// <summary>
    /// Feed ids to load. Defaults to all three. Removing one skips that series
    /// entirely; an unknown id is logged as a warning and ignored.
    /// </summary>
    public string[] EnabledFeeds { get; set; } =
        EoxDescriptors.All.Select(f => f.FeedId).ToArray();
}
