namespace DataLoader.Core.Configuration;

/// <summary>
/// Host-level settings. Bound from the <c>Platform</c> section of
/// <c>appsettings.json</c>.
/// </summary>
public sealed class PlatformSettings
{
    public const string SectionName = "Platform";

    /// <summary>
    /// LoaderIds the host should run on this invocation. Loaders not listed
    /// are skipped even if their plugin assemblies are present. Empty list
    /// = run every discovered loader.
    /// </summary>
    public List<string> EnabledLoaders { get; set; } = new();

    /// <summary>
    /// How many loaders may run concurrently. Defaults to 1 (sequential) —
    /// most operational deployments want predictable resource usage.
    /// </summary>
    public int MaxConcurrentLoaders { get; set; } = 1;

    /// <summary>
    /// Connection string for the shared platform database that hosts
    /// <c>core.LoaderRun</c> and <c>core.LoadLog</c>. A loader's own data
    /// lives in its own database (configured in its own section).
    /// </summary>
    public string LoadLogConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Optional default date window. Loaders that don't define their own
    /// dates fall back to these. Loaders that are not date-windowed ignore
    /// them.
    /// </summary>
    public int? DefaultDaysBackStart { get; set; }
    public int? DefaultDaysBackEnd { get; set; }
}
