using DataLoader.Core.Configuration;

namespace DataLoader.OPIS;

/// <summary>
/// OPIS FTP loader settings, bound from <c>Loaders:OPIS</c> via
/// <c>AddLoaderSettings&lt;OpisSettings&gt;</c> (which also wires the
/// <c>SEE_DB</c> indirection). Inherits the common bits (connection string,
/// retry, concurrency, per-unit timeout) and adds the FTP connection and
/// file-layout fields.
///
/// <para>
/// <see cref="Username"/> and <see cref="Password"/> ship as the literal
/// sentinel <c>"SEE_DB"</c> and are resolved at run time from
/// <c>core.Param(LoaderName='OPIS', ParamName='Username'|'Password')</c>, or
/// overridden by <c>DATALOADER_Loaders__OPIS__Username</c> /
/// <c>__Password</c>. They are never written to a log or to the FileLog's
/// RequestPath.
/// </para>
/// </summary>
public sealed class OpisSettings : LoaderSettingsBase
{
    public string FtpHost { get; set; } = "ftp.opisnet.com";
    public int FtpPort { get; set; } = 21;

    /// <summary>FTP user. <c>"SEE_DB"</c> in appsettings; resolved from core.Param.</summary>
    public string Username { get; set; } = "SEE_DB";

    /// <summary>FTP password. <c>"SEE_DB"</c> in appsettings; resolved from core.Param.</summary>
    public string Password { get; set; } = "SEE_DB";

    /// <summary>Directory holding the daily report files. The OPIS drop puts them in the root.</summary>
    public string RemoteDirectory { get; set; } = "/";

    /// <summary>Glob for the daily LP report files.</summary>
    public string FilePattern { get; set; } = "*LP.csv";

    /// <summary>
    /// Regex matched against a file NAME to extract its publication date. Group 1
    /// must capture a <c>yyyyMMdd</c> token. A file that does not match is skipped
    /// with a warning — the date is the merge ordering guard, so it is required.
    /// </summary>
    public string FileNameDatePattern { get; set; } = @"^(\d{8})LP\.csv$";

    /// <summary>
    /// Optional recency filter. <c>0</c> (the default) processes EVERY file the
    /// server offers — the feed already self-limits to about a rolling month.
    /// A positive value keeps only files dated within that many days of today.
    /// </summary>
    public int DaysBack { get; set; } = 0;

    /// <summary>Use FTPS (explicit TLS over the same port). OPIS's drop is plain FTP.</summary>
    public bool UseFtps { get; set; }

    /// <summary>Passive mode. Required behind most firewalls and what OPIS expects.</summary>
    public bool UsePassiveMode { get; set; } = true;

    /// <summary>FTP connect/read timeout in milliseconds.</summary>
    public int FtpTimeoutMs { get; set; } = 60_000;

    /// <summary>
    /// Candidate <c>Date</c> column formats, tried in order with
    /// <c>InvariantCulture</c>. The live feed publishes <c>MM/dd/yy</c>; the
    /// four-digit variants are accepted defensively.
    /// </summary>
    public string[] DateFormats { get; set; } =
        { "MM/dd/yy", "M/d/yy", "MM/dd/yyyy", "M/d/yyyy", "yyyy-MM-dd" };
}
