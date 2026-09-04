using DataLoader.Core.Configuration;

namespace DataLoader.Criterion;

/// <summary>
/// How the work-unit resume key varies between runs for a HOT day.
///
/// <para>All tokens are stamped in <b>UTC</b>. <see cref="RunHour"/>'s
/// <c>yyyyMMddHH</c> is strictly monotonic only in UTC — <c>01:00</c> local occurs
/// twice on a fall-back night, so a local-zone hour token would repeat, the key
/// would go backwards, and an already-recorded success would suppress a legitimate
/// re-pull for an hour.</para>
/// </summary>
public enum CriterionHotKeyStrategy
{
    /// <summary>
    /// Suffix the key with the UTC run date <c>yyyyMMdd</c> → one re-pull per UTC
    /// day. <b>The default.</b>
    /// </summary>
    RunDate,

    /// <summary>
    /// Suffix with UTC <c>yyyyMMddHH</c> → one re-pull per clock hour.
    ///
    /// <para>
    /// Worth considering for this source: <c>pipelines.nomination_points</c> is
    /// revised through the gas day as intraday cycles are posted (cycle_desc values
    /// such as <c>INTRDY_2026-09-03_1800</c> appear during the day), so an hourly
    /// cadence picks up revisions the same day rather than the next. It costs a
    /// full re-read of the window each hour, which is why it is not the default.
    /// </para>
    /// </summary>
    RunHour,

    /// <summary>Suffix with the run id → <b>every</b> invocation re-pulls (diagnostics / a forced re-pull).</summary>
    RunId
}

/// <summary>
/// Criterion loader settings, bound from <c>Loaders:Criterion</c> via
/// <c>AddLoaderSettings&lt;CriterionSettings&gt;</c> (which also wires the
/// <c>SEE_DB</c> indirection). Inherits the common bits (destination connection
/// string, retry, concurrency, per-unit timeout) and adds the PostgreSQL source
/// connection, the window and the paging.
///
/// <para>
/// <see cref="SourceUsername"/> and <see cref="SourcePassword"/> ship as the
/// literal sentinel <c>"SEE_DB"</c> and resolve at run time from
/// <c>core.Param(LoaderName='Criterion', ParamName='SourceUsername'|'SourcePassword')</c>,
/// or from <c>DATALOADER_Loaders__Criterion__SourceUsername</c> /
/// <c>__SourcePassword</c>. They are never logged: the loader logs
/// <see cref="SourceHost"/> and <see cref="SourceDatabase"/> only, and the Npgsql
/// connection string it builds is never written out.
/// </para>
/// </summary>
public sealed class CriterionSettings : LoaderSettingsBase
{
    // ---------------------------------------------------------------- source

    /// <summary>PostgreSQL host. Criterion's data-delivery replica.</summary>
    public string SourceHost { get; set; } = "dda.criterionrsch.com";

    /// <summary>
    /// PostgreSQL port. <b>443</b>, not 5432 — Criterion fronts the database on the
    /// HTTPS port so it survives egress firewalls that allow only 80/443. It still
    /// speaks the PostgreSQL wire protocol, not HTTP.
    /// </summary>
    public int SourcePort { get; set; } = 443;

    /// <summary>Source database name.</summary>
    public string SourceDatabase { get; set; } = "production";

    /// <summary>Source user. <c>"SEE_DB"</c> in appsettings; resolved from core.Param.</summary>
    public string SourceUsername { get; set; } = "SEE_DB";

    /// <summary>Source password. <c>"SEE_DB"</c> in appsettings; resolved from core.Param.</summary>
    public string SourcePassword { get; set; } = "SEE_DB";

    /// <summary>
    /// Npgsql <c>SslMode</c>. Criterion requires SSL, so anything below
    /// <c>Require</c> fails the handshake; the module warns rather than throws if
    /// this is weakened, because a deployment behind a TLS-terminating proxy could
    /// legitimately need <c>Prefer</c>.
    /// </summary>
    public string SourceSslMode { get; set; } = "Require";

    /// <summary>Seconds to wait for the source connection to open.</summary>
    public int SourceConnectTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Seconds a single source query may run. Generous by default: a paged
    /// <c>FinancialSeriesData</c> unit pulls tens of megabytes of JSON, and the
    /// nomination window scans hundreds of thousands of rows.
    /// </summary>
    public int SourceCommandTimeoutSeconds { get; set; } = 600;

    // ---------------------------------------------------------------- window

    /// <summary>
    /// How many days back from today (UTC) the day-windowed feeds enumerate. The
    /// window is <c>[today - DaysBack, today]</c> inclusive, so the shipped default
    /// of 30 produces 31 work units per day-windowed feed.
    ///
    /// <para>
    /// ⚠ <b>This is the setting that governs how long a run takes and how much it
    /// writes.</b> At 30 days <c>FinancialSeriesData</c> alone reads roughly 46,000
    /// source rows carrying ~7 GB of JSON and merges on the order of 110 million
    /// target rows. Raising it scales both linearly. The dimension feeds ignore it
    /// entirely — they are full snapshots.
    /// </para>
    /// </summary>
    public int DaysBack { get; set; } = 30;

    /// <summary>
    /// A day older than this many days is treated as SETTLED and gets a stable
    /// resume key — loaded once, then skipped forever. A newer one is HOT and gets
    /// a run-varying key so revisions are re-pulled.
    ///
    /// <para>
    /// <b>With the shipped 30/30 defaults the settled zone is EMPTY and every day
    /// is hot</b>: the oldest day in the window is exactly 30 days old and
    /// <c>age &gt; 30</c> is false. That is deliberate and matches ICE (30/30),
    /// EvolutionMarkets (30/30) and NGI (60/60). This source revises history —
    /// <c>financial_json_latest</c> republishes a series under a new version, and
    /// nomination cycles are restated during and after the gas day — so a stable
    /// key would freeze the first value seen while the loader reported clean runs.
    /// </para>
    /// <para>
    /// Setting this BELOW <see cref="DaysBack"/> creates a real settled zone and is
    /// logged as a warning at startup, because it is nearly always a
    /// misconfiguration rather than an intent.
    /// </para>
    /// </summary>
    public int SettledAfterDays { get; set; } = 30;

    /// <summary>How a hot day's key varies between runs. See <see cref="CriterionHotKeyStrategy"/>.</summary>
    public CriterionHotKeyStrategy HotKeyStrategy { get; set; } = CriterionHotKeyStrategy.RunDate;

    /// <summary>
    /// Snapshot feeds have no day to age, so they are always hot and re-read every
    /// run under a <see cref="HotKeyStrategy"/>-varying key. Set false to make them
    /// load exactly once and then skip forever — only sensible for a one-off
    /// backfill, since the dimensions do change.
    /// </summary>
    public bool AlwaysReloadSnapshots { get; set; } = true;

    // ---------------------------------------------------------------- paging

    /// <summary>
    /// How many SOURCE rows one <c>FinancialSeriesData</c> work unit reads.
    ///
    /// <para>
    /// This is a MEMORY control, not a throughput one. Each source row carries a
    /// JSON array averaging 165 KB and reaching 1.7 MB, which unpivots to ~2,400
    /// target rows (up to 17,496). A whole post_date is ~1,534 rows, so reading one
    /// unpivoted day at once would buffer several million rows — hundreds of
    /// megabytes, multiplied by <see cref="LoaderSettingsBase.MaxConcurrentWorkUnits"/>.
    /// </para>
    /// <para>
    /// At the default 250 a unit holds roughly 600,000 rows and ~40 MB of JSON, so
    /// four concurrent units stay comfortably inside a normal working set. Lower it
    /// if the host is memory-constrained; raising it above ~1,000 risks large object
    /// heap pressure with no gain, since the merge is the bottleneck.
    /// </para>
    /// </summary>
    public int SeriesPageSize { get; set; } = 250;

    /// <summary>
    /// Rows per TVP call. A work unit's rows are merged in batches of this size
    /// rather than one enormous table-valued parameter.
    ///
    /// <para>
    /// Relevant only to <c>FinancialSeriesData</c> in practice — every other feed's
    /// unit is far smaller than one batch. 50,000 keeps each MERGE's transaction and
    /// tempdb footprint bounded; a single 600,000-row TVP would hold locks on
    /// arm.Financial_SeriesData for the whole call.
    /// </para>
    /// </summary>
    public int MergeBatchSize { get; set; } = 50_000;

    // ---------------------------------------------------------------- selection

    /// <summary>
    /// Feed ids to run. An unknown id is warned about and ignored rather than
    /// throwing — a stale config entry should not stop the whole loader.
    /// </summary>
    public List<string> EnabledFeeds { get; set; } = new()
    {
        CriterionFeed.MiscPeriod,
        CriterionFeed.MiscUnit,
        CriterionFeed.PipelinesRegion,
        CriterionFeed.FinancialMetadata,
        CriterionFeed.PipelinesMetadata,
        CriterionFeed.FinancialSeries,
        CriterionFeed.FinancialSeriesData,
        CriterionFeed.PipelinesNominationPoint,
        CriterionFeed.PipelinesPointflows
    };
}
