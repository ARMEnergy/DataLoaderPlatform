using DataLoader.Core.Configuration;

namespace DataLoader.Ftp;

/// <summary>
/// FTP-pull loader settings.
///
/// Credentials should be supplied via environment variables in production
/// (e.g. <c>DATALOADER_Loaders__Ftp__FtpPassword</c>) rather than
/// committed in appsettings.json.
/// </summary>
public sealed class FtpSettings : LoaderSettingsBase
{
    public string FtpHost { get; set; } = string.Empty;
    public int FtpPort { get; set; } = 21;
    public string FtpUsername { get; set; } = string.Empty;
    public string FtpPassword { get; set; } = string.Empty;
    public bool UseSsl { get; set; } = false;

    /// <summary>Remote directory on the FTP server.</summary>
    public string RemoteDirectory { get; set; } = "/";

    /// <summary>Optional local archive directory to download files into.</summary>
    public string LocalArchiveDirectory { get; set; } = string.Empty;

    /// <summary>Glob-style filter applied to the remote listing.</summary>
    public string FilePattern { get; set; } = "*.csv";

    /// <summary>If true, the first row of each file is treated as headers.</summary>
    public bool HasHeaderRow { get; set; } = true;
}
