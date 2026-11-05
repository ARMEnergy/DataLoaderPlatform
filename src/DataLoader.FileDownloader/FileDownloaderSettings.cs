using DataLoader.Core.Configuration;

namespace DataLoader.FileDownloader;

/// <summary>
/// Settings for the FileDownloader loader, bound from <c>Loaders:FileDownloader</c> via
/// <c>AddLoaderSettings&lt;FileDownloaderSettings&gt;</c>.
///
/// <para>This loader is the DataLoaderPlatform port of the standalone
/// <c>etl-tools/FileDownloader</c> console app (GitLab
/// <c>arm-energy/armfin/etl-tools</c>). It is a FETCHER: its product is files on the
/// AssetRisk shares, which <c>ExcelParser</c> and <c>API_Loader</c> consume. The rows it
/// merges are a manifest of what was fetched, not the file contents.</para>
///
/// <para><see cref="LoaderSettingsBase.ConnectionString"/> points at <b>Mako</b>, whose
/// <c>download</c> schema already holds this app's catalogue
/// (<c>download.FileDownloader_Info</c>), work-queue views
/// (<c>download.v_*_Not_Downloaded</c>) and per-source document tables. None of those are
/// modified by this port.</para>
/// </summary>
public sealed class FileDownloaderSettings : LoaderSettingsBase
{
    /// <summary>
    /// Which acquisition modes this invocation runs. Replaces the original app's
    /// single command-line argument (<c>1</c>=Link, <c>2</c>=Click, <c>3</c>=SFTP,
    /// <c>4</c>=FTP) and its practice of enabling sources by commenting lines in and out
    /// of <c>SFTPDownload()</c> / <c>FTPDownload()</c> / <c>ClickDownload()</c>.
    ///
    /// <para><c>Browser</c> is excluded by default: it is the only mode that needs a
    /// headless browser installed on the host, so it must be opted into once Playwright
    /// is provisioned on ARMH-OPSDB01 rather than failing every run until then.</para>
    /// </summary>
    public List<string> EnabledModes { get; set; } = new() { "Link", "Sftp", "Ftp" };

    /// <summary>
    /// Keep the original app's <c>File.Exists(destination)</c> check as a second guard,
    /// in addition to the platform's <c>core.LoadLog</c> work-unit skip.
    ///
    /// <para>⚠ Defaults to true and should STAY true through the parallel run. The
    /// platform's idempotency is the work-unit key, and <c>core.LoadLog</c> starts EMPTY
    /// for a loader that has never run — so with this off, the first run would re-fetch
    /// every file the standalone app has ever downloaded. Modes Sftp/Ftp/Browser have no
    /// SQL record at all today (only Link does, via the <c>v_*_Not_Downloaded</c> views),
    /// so for those three this check is currently the ONLY thing preventing that.</para>
    ///
    /// <para>⚠ It is a weak check and always was: it compares existence, not size,
    /// checksum or timestamp, so a truncated file from a failed run is treated as
    /// complete forever. <see cref="MinimumFileBytes"/> narrows that a little.</para>
    /// </summary>
    public bool SkipIfFileExists { get; set; } = true;

    /// <summary>
    /// A downloaded file smaller than this is treated as a failed fetch rather than a
    /// legitimate empty file: it is not kept, and the unit fails so the next run retries.
    ///
    /// <para>Aimed at the original app's silent-truncation hole — vendor sites commonly
    /// answer HTTP 200 with a login page or an error stub, which
    /// <see cref="SkipIfFileExists"/> would then treat as done forever. Set to 0 to
    /// accept any file, including empty ones.</para>
    /// </summary>
    public long MinimumFileBytes { get; set; } = 1;

    /// <summary>
    /// Seconds to wait for a single file transfer. Separate from
    /// <c>WorkUnitTimeoutSeconds</c>, which bounds the whole unit.
    /// </summary>
    public int TransferTimeoutSeconds { get; set; } = 300;

    // -----------------------------------------------------------------------------
    // Credentials.
    //
    // ⚠ FLAT, TOP-LEVEL STRING PROPERTIES BY NECESSITY — do not "tidy" these into a
    // nested Endpoints dictionary. SeeDbSettingsResolver resolves the "SEE_DB" sentinel
    // by reflecting over the settings type's own public read/write STRING properties
    // (SeeDbSettingsResolver.PostConfigure); it does not recurse into nested objects,
    // collections or dictionaries. A credential moved one level down would silently stay
    // the literal string "SEE_DB" and be sent to the vendor as the password.
    //
    // Non-secret per-endpoint configuration (hosts, directories, patterns) is free to
    // nest, and does — see FileDownloaderEndpoint.
    //
    // 🔒 The standalone app carries these as plaintext literals in Program.cs (lines
    // 77, 123-124, 327-328, 403-404) and they are present in 7 commits of the etl-tools
    // history. Those vendor credentials must be ROTATED as part of this port; copying
    // them into core.Param without rotating only moves an already-disclosed secret.
    // -----------------------------------------------------------------------------

    /// <summary>Platts SFTP user. Resolved from <c>core.Param('FileDownloader','PlattsSftpUsername')</c>.</summary>
    public string PlattsSftpUsername { get; set; } = "SEE_DB";

    /// <summary>Platts SFTP password. Resolved from core.Param.</summary>
    public string PlattsSftpPassword { get; set; } = "SEE_DB";

    /// <summary>Argus FTP user. Resolved from core.Param.</summary>
    public string ArgusFtpUsername { get; set; } = "SEE_DB";

    /// <summary>Argus FTP password. Resolved from core.Param.</summary>
    public string ArgusFtpPassword { get; set; } = "SEE_DB";

    /// <summary>SPP (Wells Fargo) FTP user. Resolved from core.Param.</summary>
    public string SppFtpUsername { get; set; } = "SEE_DB";

    /// <summary>SPP (Wells Fargo) FTP password. Resolved from core.Param.</summary>
    public string SppFtpPassword { get; set; } = "SEE_DB";

    /// <summary>Wells Fargo SFTP user. Resolved from core.Param.</summary>
    public string WellsFargoSftpUsername { get; set; } = "SEE_DB";

    /// <summary>
    /// Filesystem path to the Wells Fargo SFTP private key. A path, not a secret, so it
    /// stays in config — but the FILE it names is key material and lives on the share
    /// under ACL, never in this repo.
    /// </summary>
    public string WellsFargoSftpPrivateKeyPath { get; set; } = string.Empty;

    /// <summary>
    /// Per-endpoint non-secret configuration, keyed by endpoint name
    /// (<c>Platts</c>, <c>Argus</c>, <c>Spp</c>, <c>WellsFargo</c>, …). Safe to nest
    /// precisely because none of it is a credential.
    /// </summary>
    public Dictionary<string, FileDownloaderEndpoint> Endpoints { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>One remote endpoint's non-secret settings. See the credential note on
/// <see cref="FileDownloaderSettings"/> for why usernames and passwords are NOT here.</summary>
public sealed class FileDownloaderEndpoint
{
    /// <summary>Hostname. Empty disables the endpoint.</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>Port. 21 for FTP, 22 for SFTP.</summary>
    public int Port { get; set; } = 21;

    /// <summary>Remote directory to enumerate.</summary>
    public string RemoteDirectory { get; set; } = "/";

    /// <summary>UNC or local directory the files land in.</summary>
    public string DestinationPath { get; set; } = string.Empty;

    /// <summary>
    /// Filename filter using the original app's <c>%</c> convention, carried over
    /// verbatim so the existing per-feed filters keep their meaning:
    /// <c>PREFIX%</c> = starts-with, <c>%SUFFIX</c> = ends-with, bare = exact match.
    /// Empty accepts every file in the directory.
    /// </summary>
    public string FilePattern { get; set; } = string.Empty;

    /// <summary>How many transfers to run at once against this endpoint.</summary>
    public int MaxConcurrentTransfers { get; set; } = 1;

    /// <summary>Set false to leave the endpoint configured but inactive.</summary>
    public bool Enabled { get; set; } = true;
}
