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

    /// <summary>
    /// <c>true</c> → every file found on the server is re-read and re-merged on
    /// every run, even one whose (folder, name, size, last-modified) version already
    /// completed. <c>false</c> (the default) → the <c>core.LoadLog</c> work-unit-key
    /// check applies and an already-loaded file version is skipped.
    ///
    /// <para>
    /// Mechanics: the run id is appended to each work unit's key, so
    /// <c>core.usp_BeginLoadLog</c> can never match a prior <c>IsComplete = 1</c>
    /// row and so never returns the "already loaded" short-circuit. The audit row
    /// is still written per unit per run — the log is bypassed as a <i>gate</i>,
    /// not as a <i>record</i>. This is the same run-token mechanism the other
    /// loaders use for their hot zones (<c>HotZoneKeyStrategy = RunId</c>).
    /// </para>
    /// <para>
    /// Cost: discovery is unfiltered — every <c>yyyymmdd</c> folder the server still
    /// retains is enumerated on every run. With this on, every one of those files is
    /// downloaded over SFTP and merged again, so a run grows from seconds to minutes
    /// with the vendor's retention window. Default <c>false</c>, which keeps the
    /// normal "load each file version once" behaviour. The merges are idempotent
    /// either way, so this changes cost and run time, never the loaded data.
    /// </para>
    /// </summary>
    public bool ReloadAllFiles { get; set; }

    /// <summary>
    /// How many days of <c>yyyymmdd</c> date folders to look at at all, counted back
    /// from the run's UTC date. A folder older than this is not even listed, so its
    /// files cost no SFTP round-trip.
    ///
    /// <para>
    /// <c>0</c> (the default) means no limit — every date folder the server still
    /// retains, which is the behaviour before this setting existed. Future-dated
    /// folders are always kept: the vendor's publication day can run ahead of UTC,
    /// and a run just after midnight UTC legitimately sees a folder named for
    /// "tomorrow".
    /// </para>
    /// <para>
    /// SymbolData only. The Symbol reference CSVs live in one flat directory with no
    /// date in their path, so neither this nor <see cref="SettledAfterDays"/> applies
    /// to them.
    /// </para>
    /// </summary>
    public int DaysBack { get; set; }

    /// <summary>
    /// How many days back to re-run files that already loaded. A date folder whose
    /// age in days is <c>&lt;=</c> this value is HOT — every file in it is re-read and
    /// re-merged on every run, whatever the load log says. An older folder is SETTLED:
    /// its files load once per version and are then skipped, though a file that really
    /// changes still gets a new key from its size/last-modified and is re-read.
    ///
    /// <para>
    /// <c>0</c> (the default) makes only the current UTC day's folder hot. <c>1</c>
    /// also covers yesterday — the useful setting here, because the run at 00:00 UTC
    /// is working on the previous UTC day's folder. A negative value leaves nothing
    /// hot, restoring pure "load each file version once" behaviour.
    /// </para>
    /// <para>
    /// Bounded by <see cref="DaysBack"/>: a folder outside the discovery window is
    /// never listed, so it cannot be hot. <see cref="ReloadAllFiles"/> overrides this
    /// and makes every discovered folder hot.
    /// </para>
    /// </summary>
    public int SettledAfterDays { get; set; }
}
