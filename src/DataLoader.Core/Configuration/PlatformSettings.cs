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
    ///
    /// <para>⚠ THIS IS ONE BUDGET FOR THE WHOLE CHAIN, NOT PER CHILD PROC. The proc
    /// loops over every enabled row in <c>core.AdditionalProcesses</c> for the loader
    /// inside a single batch, so this value has to cover all of them added together.
    /// Platts runs three (<c>Mako.dlp.merge_platts</c> →
    /// <c>merge_platts_update_cash_vol</c> → <c>..._v2</c>) averaging 201 + 212 + 158
    /// = 571 s, which is why 600 was not enough: 12 of 42 runs (29%) were killed
    /// mid-chain between 2026-10-06 and 2026-10-20. Raised to 1800 on 2026-10-20.</para>
    ///
    /// <para>A timeout here is SILENT by design — <see cref="Persistence.SqlAdditionalProcessRunner"/>
    /// swallows the exception so post-processing cannot fail a good load, so the process
    /// still exits 0 and the run still records <c>Success = 1</c>. The only evidence is the
    /// <c>fail:</c> line in the loader's log and, in the database,
    /// <c>core.LoaderRun_Summary.AddProcStartTime IS NOT NULL AND AddProcEndTime IS NULL</c>
    /// (that proc stamps the start first and all the closing columns last, so an aborted
    /// batch never reaches them) plus a <c>core.AdditionalProcesses_Log</c> row with no
    /// <c>EndTime</c>. Note a SQL error in a child proc CANNOT produce that state: each
    /// child has its own TRY/CATCH that records <c>ErrorMessage</c> and lets the loop carry
    /// on, so an unclosed row means the batch was killed from outside the T-SQL.</para>
    ///
    /// <para>⚠ 0 does NOT mean "no limit" here. The runner applies
    /// <c>Math.Max(1, …)</c>, so 0 becomes a ONE-SECOND timeout.</para>
    /// </summary>
    public int AdditionalProcessesTimeoutSeconds { get; set; } = 1800;

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
