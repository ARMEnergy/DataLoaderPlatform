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

    /// <summary>
    /// Whether to call <c>core.usp_RunAdditionalProcesses</c> after each loader
    /// finishes. On by default. Turn it off for local or debug runs that point at
    /// production-shaped config but must not trigger downstream post-processing.
    /// </summary>
    public bool RunAdditionalProcesses { get; set; } = true;

    /// <summary>
    /// Command timeout for <c>core.usp_RunAdditionalProcesses</c>, in seconds.
    /// Deliberately NOT ADO.NET's 30-second default: that proc fans out to
    /// post-load work across several databases.
    /// </summary>
    public int AdditionalProcessesTimeoutSeconds { get; set; } = 600;

    /// <summary>
    /// Command timeout for the <c>core.LoadLog</c> / <c>core.LoaderRun</c> commands, in seconds.
    ///
    /// <para>Deliberately NOT ADO.NET's 30-second default. Every work unit opens its own
    /// connection to call <c>usp_BeginLoadLog</c>, and a large loader fans hundreds of them
    /// through a few concurrent slots — CWG alone runs 476 units. A client-side timeout there
    /// is uniquely damaging: the INSERT has already committed, so the row exists while the
    /// client never learns its id, leaving a row that nothing can close
    /// (see <c>sql/Core/006_CreateLoadLogSweeper.sql</c>). A longer timeout makes that race
    /// far rarer; the sweeper cleans up whatever still slips through.</para>
    /// </summary>
    public int LoadLogCommandTimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Whether to close never-closed <c>core.LoadLog</c> rows left behind by EARLIER runs at the
    /// start of each invocation (<c>core.usp_AbandonStaleLoadLogs</c>). On by default. A row is
    /// only closed when its own run is definitively over, so a concurrently-running invocation is
    /// never disturbed.
    /// </summary>
    public bool AbandonStaleLoadLogs { get; set; } = true;

    /// <summary>
    /// How long a run that never wrote its own <c>CompletedAtUtc</c> (i.e. the process was killed)
    /// must have been started before its open rows are swept. Runs that DID complete are swept
    /// immediately regardless of this. Default 24 hours.
    /// </summary>
    public int AbandonStaleAfterHours { get; set; } = 24;
}
