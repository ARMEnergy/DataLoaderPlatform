namespace DataLoader.Core.Configuration;

/// <summary>
/// Common settings every loader has. A loader's own settings class should
/// inherit from this and add its own properties (api keys, endpoints, FTP
/// hosts, drop directories, etc).
///
/// The host does not inspect loader-specific settings — only the loader
/// itself binds and uses them.
/// </summary>
public abstract class LoaderSettingsBase
{
    /// <summary>
    /// Connection string for THIS loader's destination database. Each loader
    /// can point to its own database — they do not have to share.
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// How many work units this loader processes concurrently. The host
    /// already bounds the number of concurrent loaders; this bounds work
    /// within a loader.
    /// </summary>
    public int MaxConcurrentWorkUnits { get; set; } = 4;

    /// <summary>
    /// Retry settings for transient failures inside a single work unit.
    /// </summary>
    public int RetryCount { get; set; } = 3;
    public int RetryDelayMs { get; set; } = 500;

    /// <summary>
    /// Per-work-unit timeout. The pipeline cancels a unit that runs longer.
    /// Zero or negative = no timeout.
    /// </summary>
    public int WorkUnitTimeoutSeconds { get; set; } = 300;
}
