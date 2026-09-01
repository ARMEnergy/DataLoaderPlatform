using DataLoader.Core.Configuration;

namespace DataLoader.Argus;

/// <summary>
/// Argus FTP loader settings, bound from <c>Loaders:Argus</c> via
/// <c>AddLoaderSettings&lt;ArgusSettings&gt;</c> (which also wires the
/// <c>SEE_DB</c> indirection). Inherits the common bits (connection string, retry,
/// concurrency, per-unit timeout) and adds the FTP connection and file layout.
///
/// <para>
/// <see cref="Username"/> and <see cref="Password"/> ship as the literal sentinel
/// <c>"SEE_DB"</c> and resolve at run time from
/// <c>core.Param(LoaderName='Argus', ParamName='Username'|'Password')</c>, or from
/// <c>DATALOADER_Loaders__Argus__Username</c> / <c>__Password</c>. They are never
/// logged and never written to the FileLog's RequestPath.
/// </para>
/// </summary>
public sealed class ArgusSettings : LoaderSettingsBase
{
    public string FtpHost { get; set; } = "ftp.argusmedia.com";
    public int FtpPort { get; set; } = 21;

    /// <summary>FTP user. <c>"SEE_DB"</c> in appsettings; resolved from core.Param.</summary>
    public string Username { get; set; } = "SEE_DB";

    /// <summary>FTP password. <c>"SEE_DB"</c> in appsettings; resolved from core.Param.</summary>
    public string Password { get; set; } = "SEE_DB";

    /// <summary>Directory holding the 15 reference snapshots.</summary>
    public string DocumentationDirectory { get; set; } = "/DOCUMENTATION";

    /// <summary>Directory holding the dated price files.</summary>
    public string TimeSeriesDirectory { get; set; } = "/DCRDEUS";

    /// <summary>
    /// Regex matched against a DCRDEUS file NAME. Group 1 must capture a
    /// <c>yyyyMMdd</c> token (the merge ordering guard) and group 2 the module
    /// suffix.
    ///
    /// <para>
    /// This single pattern is the whole exclusion mechanism: it rejects
    /// <c>latestdhc.csv</c> and <c>previousdhc.csv</c> (byte-identical aliases of
    /// dated files, which would otherwise double-merge) and <c>7667.csv</c> (an
    /// ad-hoc dump with no date, hence no ordering guard), while still picking up
    /// a NEW dated module file automatically.
    /// </para>
    /// </summary>
    public string FileNameDatePattern { get; set; } = @"^(\d{8})([A-Za-z][A-Za-z0-9]*)\.csv$";

    /// <summary>
    /// Reference feed ids to load. Defaults to all 15. Removing one skips that
    /// file entirely; an unknown id is logged as a warning and ignored.
    /// </summary>
    public string[] EnabledFeeds { get; set; } =
        ArgusDescriptors.Documentation.Select(f => f.FeedId).ToArray();

    /// <summary>Set false to skip the DCRDEUS pipeline entirely.</summary>
    public bool EnableTimeSeries { get; set; } = true;

    /// <summary>
    /// Optional recency filter on DCRDEUS files. <c>0</c> (the default) processes
    /// EVERY dated file the server offers — the drop already self-limits to about
    /// two weeks. A positive value keeps only files dated within that many days.
    /// </summary>
    public int DaysBack { get; set; }

    /// <summary>
    /// When true, the run date is folded into every work-unit key, so
    /// <c>core.LoadLog</c> cannot skip a file it has already processed.
    ///
    /// <para>
    /// The default (<c>false</c>) still ENUMERATES every dated file on every run;
    /// it just skips the ones whose bytes have not changed since last time. Since
    /// re-merging identical content is a no-op, the database ends in the same state
    /// either way — the skip only avoids wasted work. Turn this on for a forced
    /// re-merge, e.g. after changing a merge proc.
    /// </para>
    /// </summary>
    public bool ForceReprocess { get; set; }

    /// <summary>Use FTPS (explicit TLS on the same port). The Argus drop is plain FTP.</summary>
    public bool UseFtps { get; set; }

    /// <summary>Passive mode. Required behind most firewalls and what Argus expects.</summary>
    public bool UsePassiveMode { get; set; } = true;

    /// <summary>
    /// FTP connect/read timeout in milliseconds. Higher than the OPIS default
    /// because the largest file here is 10.5 MB rather than 14 KB.
    /// </summary>
    public int FtpTimeoutMs { get; set; } = 180_000;
}
