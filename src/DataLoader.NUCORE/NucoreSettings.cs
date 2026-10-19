using DataLoader.Core.Configuration;

namespace DataLoader.NUCORE;

/// <summary>
/// How a HOT unit's resume key varies between runs.
///
/// <para>All tokens are stamped in <b>UTC</b>. <see cref="RunHour"/>'s <c>yyyyMMddHH</c> is
/// strictly monotonic only in UTC - <c>01:00</c> local happens twice on a fall-back night,
/// so a local-zone hour token would repeat, the key would go backwards, and an
/// already-recorded success would suppress a legitimate re-pull for an hour.</para>
/// </summary>
internal enum NucoreHotKeyStrategy
{
    /// <summary>Suffix with the UTC run date <c>yyyyMMdd</c> -&gt; one re-pull per UTC day. <b>Default.</b></summary>
    RunDate,

    /// <summary>Suffix with UTC <c>yyyyMMddHH</c> -&gt; one re-pull per clock hour.</summary>
    RunHour,

    /// <summary>Suffix with the run id -&gt; EVERY invocation re-pulls (diagnostics / forced re-pull).</summary>
    RunId
}

/// <summary>
/// NUCORE loader settings, bound from <c>Loaders:NUCORE</c> via
/// <c>AddLoaderSettings&lt;NucoreSettings&gt;</c> (which also wires the <c>SEE_DB</c>
/// indirection). Inherits the common bits - destination connection string, retry,
/// concurrency, per-unit timeout - and adds the API connection, the window and the batching.
/// </summary>
internal sealed class NucoreSettings : LoaderSettingsBase
{
    public NucoreSettings()
    {
        // ⚠ The base default is 300s, which is SHORTER than this loader's own worst-case
        // retry budget of (RetryCount + 1) x HttpTimeoutSeconds = 4 x 300 = 1200s. Left
        // alone, a unit that hit its retries would be cancelled by the per-unit budget and
        // reported as "work unit timed out", sending the investigation to the wrong setting
        // while the vendor error that actually caused it never surfaced. This is the trap
        // EIA hit (see CLAUDE.md); NucoreSinkAndPolicyTests asserts the two stay consistent.
        //
        // 1800s also covers the real work: a PositionDetails trade month can be several
        // pages of ~30s each.
        WorkUnitTimeoutSeconds = 1800;
    }

    // ------------------------------------------------------------------------- the API

    /// <summary>Base URL of the NUCORE integration gateway, without a trailing slash.</summary>
    public string BaseUrl { get; set; } = "https://prodarmapigateway.azurewebsites.net";

    /// <summary>
    /// The <c>X-Api-Key</c> header value. <c>"SEE_DB"</c> in appsettings; resolved at run
    /// time from <c>core.Param(LoaderName='NUCORE', ParamName='ApiKey')</c> or from the
    /// environment override <c>DATALOADER_Loaders__NUCORE__ApiKey</c>.
    ///
    /// <para>
    /// 🔒 Never put the real key in appsettings, in source, or in a commit. It travels as a
    /// request HEADER, so it does not appear in URLs, but <see cref="NucoreHttp.Redact"/>
    /// still scrubs anything that could carry it out of an exception message.
    /// </para>
    /// </summary>
    public string ApiKey { get; set; } = "SEE_DB";

    /// <summary>
    /// Seconds before ONE HTTP attempt times out.
    ///
    /// <para>
    /// ⚠ This is enforced INSIDE the Polly policy, per attempt, with
    /// <c>HttpClient.Timeout</c> left infinite. <c>HttpClient.Timeout</c> bounds a whole
    /// RETRIED operation rather than a single attempt, so setting it instead would let one
    /// slow attempt consume the budget for all of them and surface a vendor 504 as a client
    /// timeout - the trap EIA hit (see CLAUDE.md).
    /// </para>
    /// <para>
    /// It must be generous. A single <c>PositionDetails</c> page of 20,000 rows takes
    /// ~30 s, and <c>Volatilities</c> returns 147,908 rows in one response.
    /// </para>
    /// </summary>
    public int HttpTimeoutSeconds { get; set; } = 300;

    // ---------------------------------------------------------------------- the window

    /// <summary>
    /// First business date to load, inclusive, as <c>yyyy-MM-dd</c>. Empty means "use
    /// <see cref="DaysBack"/> instead".
    ///
    /// <para>
    /// Shipped as 2025-01-01 to backfill the full available history. The vendor returns
    /// HTTP 200 with zero rows for any date before its data starts, so an over-wide floor
    /// costs one cheap request per missing day rather than failing.
    /// </para>
    /// </summary>
    public string StartDate { get; set; } = "2025-01-01";

    /// <summary>
    /// Used only when <see cref="StartDate"/> is empty: the window is
    /// <c>[today - DaysBack, today]</c> inclusive.
    /// </summary>
    public int DaysBack { get; set; } = 30;

    /// <summary>
    /// A date older than this many days is SETTLED - it gets a stable resume key, so once
    /// loaded it is skipped forever without a request. A newer one is HOT and gets a
    /// run-varying key.
    ///
    /// <para>
    /// Shipped as 3. A closed risk day is normally frozen, but corrections do happen, so
    /// the last few business days are re-pulled every run. Raising it toward the window
    /// width re-pulls more history every run; lowering it to 0 trusts the first read of
    /// every day.
    /// </para>
    /// </summary>
    public int SettledAfterDays { get; set; } = 3;

    /// <summary>How a hot unit's key varies between runs. See <see cref="NucoreHotKeyStrategy"/>.</summary>
    public NucoreHotKeyStrategy HotKeyStrategy { get; set; } = NucoreHotKeyStrategy.RunDate;

    /// <summary>
    /// Skip Saturdays and Sundays when enumerating date work units.
    ///
    /// <para>
    /// NUCORE publishes on business days only - every weekend sampled returned HTTP 200
    /// with <c>totalRecords: 0</c>. Leaving this on removes ~29% of the units from a
    /// full-history backfill at no cost in data. It does NOT skip holidays, which still
    /// come back as legitimate empty reads.
    /// </para>
    /// </summary>
    public bool SkipWeekends { get; set; } = true;

    // --------------------------------------------------- the trade-month partition

    /// <summary>
    /// Earliest trade month to enumerate for <c>PositionDetails</c>, as <c>yyyyMM</c>.
    /// The observed data starts at trade month 2025-01.
    /// </summary>
    public string TradeMonthFrom { get; set; } = "202501";

    /// <summary>
    /// Months beyond the current one to enumerate for <c>PositionDetails</c>. Positions are
    /// held well into the future - live data reaches trade month 2031 - and a month with no
    /// rows is a cheap 200 with <c>totalRecords: 0</c>, so this is deliberately generous.
    /// </summary>
    public int TradeMonthsAhead { get; set; } = 84;

    /// <summary>
    /// Trade months older than this many months back are SETTLED: once loaded they get a
    /// stable resume key and cost nothing on later runs.
    ///
    /// <para>
    /// A past trade month stops accumulating new system dates once its positions are closed,
    /// so it is genuinely finished. Months within the window - and every FUTURE month, which
    /// is always within it - stay hot and are checked against the watermark each run.
    /// </para>
    /// </summary>
    public int SettledTradeMonthsBack { get; set; } = 3;

    // ------------------------------------------------------------------------ paging

    /// <summary>
    /// Rows requested per page.
    ///
    /// <para>
    /// ⚠ NOT the 500,000 the endpoints were originally specified with. At that size
    /// <c>PositionDetails</c> answers <b>HTTP 504</b> after four minutes, because one
    /// unfiltered call is 2,468,453 rows. 20,000 returns in ~30 s and pages cleanly;
    /// a page past the end returns 200 with <c>recordsReturned: 0</c>, which is the stop
    /// condition.
    /// </para>
    /// </summary>
    public int PageSize { get; set; } = 20_000;

    /// <summary>
    /// Hard cap on pages per work unit, so a vendor-side paging fault cannot spin forever.
    /// At the default <see cref="PageSize"/> this allows ~10 million rows in one unit.
    /// </summary>
    public int MaxPagesPerUnit { get; set; } = 500;

    /// <summary>
    /// Rows per TVP merge call. A unit streams its pages and merges in batches of this size,
    /// so peak memory is one batch rather than one whole response.
    /// </summary>
    public int BatchSize { get; set; } = 20_000;

    /// <summary>
    /// How many per-row conversion failures to log in full per work unit before falling
    /// silent. The COUNT is always reported; this only bounds the detail, so one malformed
    /// column cannot flood the log with a line per row.
    /// </summary>
    public int MaxRowErrorsLogged { get; set; } = 20;

    /// <summary>
    /// Safety cap on rows buffered for a single replace SCOPE.
    ///
    /// <para>
    /// A replace-mode endpoint deletes a scope and re-inserts it, which is only correct if
    /// the scope is written in ONE call - so a <c>ReplaceByDate</c> unit buffers its whole
    /// day. Today's days are small (PositionSummary ~13,600 rows, PnlExplain ~11,400), but
    /// splitting a scope would corrupt the load and buffering without bound would exhaust
    /// memory, so a scope larger than this fails loudly instead of doing either.
    /// </para>
    /// </summary>
    public int MaxUnitBufferRows { get; set; } = 500_000;

    // --------------------------------------------------------------------- selection

    /// <summary>
    /// Endpoint names to run. An unknown name is warned about and ignored rather than
    /// throwing - a stale config entry should not stop the whole loader.
    /// </summary>
    public List<string> EnabledEndpoints { get; set; } = new()
    {
        NucoreDescriptors.VarResults,
        NucoreDescriptors.PositionSummary,
        NucoreDescriptors.PnlExplain,
        NucoreDescriptors.PositionDetails,
        NucoreDescriptors.Volatilities
    };
}
