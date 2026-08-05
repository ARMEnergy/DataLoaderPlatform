using DataLoader.Core.Configuration;

namespace DataLoader.Platts;

/// <summary>
/// Platts SFTP loader settings, bound from <c>Loaders:Platts</c>. Inherits the
/// common bits (connection string, retry, concurrency, per-unit timeout) and
/// adds the SFTP connection and layout fields.
///
/// <para>
/// <see cref="SftpPassword"/> must be supplied via an environment variable in
/// production (<c>DATALOADER_Loaders__Platts__SftpPassword</c>) rather than
/// committed to appsettings.json, and is never written to logs.
/// </para>
/// </summary>
public sealed class PlattsSettings : LoaderSettingsBase
{
    public string SftpHost { get; set; } = string.Empty;
    public int SftpPort { get; set; } = 22;
    public string SftpUsername { get; set; } = string.Empty;
    public string SftpPassword { get; set; } = string.Empty;

    /// <summary>Root directory that holds the per-date <c>yyyymmdd</c> folders.</summary>
    public string RootDirectory { get; set; } = "/";

    /// <summary>Regex matched against a folder name to treat it as a date folder.</summary>
    public string DateFolderPattern { get; set; } = @"^\d{8}$";

    /// <summary>Glob for the daily market data files inside each date folder.</summary>
    public string MarketDataFilePattern { get; set; } = "*.ftp";

    /// <summary>Directory holding the reference-metadata CSV files.</summary>
    public string SymbolsDirectory { get; set; } = "symbols/csv-version";

    /// <summary>Glob for the reference-metadata CSV files.</summary>
    public string SymbolFilePattern { get; set; } = "*.csv";

    /// <summary>Feeds to run this pass; matched case-insensitively against pipeline feed ids.</summary>
    public string[] EnabledFeeds { get; set; } = { "SymbolData", "Symbol" };
}
