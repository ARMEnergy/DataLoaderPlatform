using DataLoader.Core.Configuration;

namespace DataLoader.OilX;

/// <summary>
/// How a HOT day's work-unit resume key varies between runs.
///
/// <para>All tokens are stamped in <b>UTC</b>. <see cref="RunHour"/>'s <c>yyyyMMddHH</c>
/// is strictly monotonic only in UTC — <c>01:00</c> local occurs twice on a fall-back
/// night, so a local-zone hour token would repeat, the key would go backwards, and an
/// already-recorded success would suppress a legitimate re-pull for an hour.</para>
/// </summary>
public enum OilXHotKeyStrategy
{
    /// <summary>
    /// Suffix the key with the UTC run date <c>yyyyMMdd</c> → one re-pull per UTC day.
    /// <b>The default.</b>
    /// </summary>
    RunDate,

    /// <summary>
    /// Suffix with UTC <c>yyyyMMddHH</c> → one re-pull per clock hour. Worth choosing
    /// if the loader runs several times a day and should pick up each new intraday
    /// snapshot as it lands: the busiest feeds publish roughly every six hours.
    /// </summary>
    RunHour,

    /// <summary>Suffix with the run id → <b>every</b> invocation re-pulls (diagnostics / a forced re-pull).</summary>
    RunId
}

/// <summary>
/// OilX loader settings, bound from <c>Loaders:OilX</c> via
/// <c>AddLoaderSettings&lt;OilXSettings&gt;</c> (which also wires the <c>SEE_DB</c>
/// indirection). Inherits the common bits — destination connection string, retry,
/// concurrency, per-unit timeout — and adds the API connection, the window and the
/// batching.
/// </summary>
public sealed class OilXSettings : LoaderSettingsBase
{
    // ------------------------------------------------------------------- the API

    /// <summary>
    /// Base URL for the OilX CSV API, without a trailing slash. The manifest endpoint
    /// is <c>{BaseUrl}/csv/</c> and the feed catalogue is <c>{BaseUrl}/csv/list</c>.
    /// </summary>
    public string BaseUrl { get; set; } = "https://api.energyaspects.com/oilx/v2";

    /// <summary>
    /// The <c>api_key</c> value. <c>"SEE_DB"</c> in appsettings; resolved at run time
    /// from <c>core.Param(LoaderName='OilX', ParamName='ApiKey')</c> or from
    /// <c>DATALOADER_Loaders__OilX__ApiKey</c>.
    ///
    /// <para>
    /// 🔒 <b>Unlike every other HTTP loader here, this key travels in the QUERY
    /// STRING</b> — the vendor offers no header form. It is therefore present in every
    /// request URL, which is why the named client has <c>RemoveAllLoggers()</c>, why
    /// the loader logs feed/day/file instead of URLs, and why
    /// <see cref="OilXHttp.Redact"/> scrubs anything that could still carry one.
    /// </para>
    /// </summary>
    public string ApiKey { get; set; } = "SEE_DB";

    /// <summary>
    /// Seconds before one HTTP request times out.
    ///
    /// <para>
    /// <b>This must be generous.</b> It covers the whole download, and a single
    /// CargoTracking snapshot is ~208 MB. The 30–120 s that suits a JSON loader would
    /// fail every large file. The per-unit cap is
    /// <see cref="LoaderSettingsBase.WorkUnitTimeoutSeconds"/>, which bounds the
    /// several files a unit may fetch.
    /// </para>
    /// </summary>
    public int HttpTimeoutSeconds { get; set; } = 1800;

    // ---------------------------------------------------------------- the window

    /// <summary>
    /// How many days back from today (UTC) the window reaches. The window is
    /// <c>[today - DaysBack, today]</c> INCLUSIVE, so the shipped default of 30 covers
    /// the last <b>31 days</b>.
    ///
    /// <para>
    /// ⚠ Each day is a FULL snapshot of all history, so a day costs ~2.07 million rows
    /// across the eight feeds and ~210 MB of CSV. A 31-day first run is therefore
    /// ~64M rows and ~6.5 GB. After that, only days not yet recorded as loaded are
    /// fetched — see <see cref="SettledAfterDays"/>.
    /// </para>
    /// </summary>
    public int DaysBack { get; set; } = 30;

    /// <summary>
    /// A day older than this many days is treated as SETTLED and gets a stable resume
    /// key — loaded once, then skipped forever without so much as a manifest call. A
    /// newer one is HOT and gets a run-varying key so new snapshots are picked up.
    ///
    /// <para>
    /// <b>Shipped as 1, deliberately departing from this repo's usual all-hot
    /// default.</b> ICE, Genscape and EvolutionMarkets all ship
    /// <c>SettledAfterDays == DaysBack</c> because those vendors RESTATE recent data, so
    /// a stable key would freeze the first value seen. OilX does not restate: a day's
    /// published file is immutable, and the vendor revises by publishing a NEW day
    /// (<c>docs/apis/OilX.md</c> §2 Behaviour 8). What still changes is TODAY, whose
    /// file set grows through the day — hence 1 rather than 0.
    /// </para>
    /// <para>
    /// Raising this toward <see cref="DaysBack"/> re-downloads the whole window every
    /// run — ~6.5 GB — for data that provably cannot change. The module warns when it
    /// is set that way.
    /// </para>
    /// </summary>
    public int SettledAfterDays { get; set; } = 1;

    /// <summary>How a hot day's key varies between runs. See <see cref="OilXHotKeyStrategy"/>.</summary>
    public OilXHotKeyStrategy HotKeyStrategy { get; set; } = OilXHotKeyStrategy.RunDate;

    // ----------------------------------------------------------------- batching

    /// <summary>
    /// Rows per TVP merge call.
    ///
    /// <para>
    /// A work unit streams its file and merges in batches of this size, so peak memory
    /// is one batch rather than one file — which matters when the file is 396,866 rows.
    /// 20,000 keeps each <c>DataTable</c> small while amortising the round trip.
    /// </para>
    /// <para>
    /// Changing it does not change the final table state: every merge is idempotent on
    /// <c>(RunDate, RowId)</c> and batches of one file are disjoint by construction.
    /// </para>
    /// </summary>
    public int BatchSize { get; set; } = 20_000;

    /// <summary>
    /// How many per-row conversion failures to log in full per file before falling
    /// silent. The count is always reported; this only bounds the detail, so one
    /// malformed column in a 400,000-row file cannot flood the log with 400,000 lines.
    /// </summary>
    public int MaxRowErrorsLogged { get; set; } = 20;

    // ------------------------------------------------------------------ selection

    /// <summary>
    /// Feed ids to run — the vendor's own file names, which is also what the
    /// <c>files=</c> query parameter takes. An unknown id is warned about and ignored
    /// rather than throwing; a stale config entry should not stop the whole loader.
    ///
    /// <para>
    /// The vendor publishes four further feeds this loader does not load
    /// (<c>CargoTrackingCrudeRevisions</c>, <c>OilFieldsDailyProduction</c>,
    /// <c>TerminalsWeekly</c>, <c>USWeekly</c>) — no target table was specified for
    /// them. Adding one is a descriptor plus its SQL, not new plumbing.
    /// </para>
    /// </summary>
    public List<string> EnabledFeeds { get; set; } = new()
    {
        OilXDescriptors.CargoTracking,
        OilXDescriptors.FloatingStorageVessels,
        OilXDescriptors.Flows,
        OilXDescriptors.GlobalBalance,
        OilXDescriptors.OilFieldsProduction,
        OilXDescriptors.RegionalBalance,
        OilXDescriptors.SupplyDemand,
        OilXDescriptors.Terminals
    };
}
