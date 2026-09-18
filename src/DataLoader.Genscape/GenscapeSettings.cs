using DataLoader.Core.Configuration;

namespace DataLoader.Genscape;

/// <summary>
/// How the work-unit resume key varies between runs for a HOT window chunk.
///
/// <para>All tokens are stamped in <b>UTC</b>. <see cref="RunHour"/>'s
/// <c>yyyyMMddHH</c> is strictly monotonic only in UTC — <c>01:00</c> local occurs
/// twice on a fall-back night, so a local-zone hour token would repeat, the key would
/// go backwards, and an already-recorded success would suppress a legitimate re-pull
/// for an hour.</para>
/// </summary>
public enum GenscapeHotKeyStrategy
{
    /// <summary>
    /// Suffix the key with the UTC run date <c>yyyyMMdd</c> → one re-pull per UTC day.
    /// <b>The default</b>, and the right one for a weekly feed.
    /// </summary>
    RunDate,

    /// <summary>
    /// Suffix with UTC <c>yyyyMMddHH</c> → one re-pull per clock hour. Overkill for a
    /// feed that publishes once a week; kept for parity with the other loaders.
    /// </summary>
    RunHour,

    /// <summary>Suffix with the run id → <b>every</b> invocation re-pulls (diagnostics / a forced re-pull).</summary>
    RunId
}

/// <summary>
/// Genscape loader settings, bound from <c>Loaders:Genscape</c> via
/// <c>AddLoaderSettings&lt;GenscapeSettings&gt;</c> (which also wires the
/// <c>SEE_DB</c> indirection). Inherits the common bits — destination connection
/// string, retry, concurrency, per-unit timeout — and adds the API connection, the
/// window and the response-cap guard.
/// </summary>
public sealed class GenscapeSettings : LoaderSettingsBase
{
    // ------------------------------------------------------------------- the API

    /// <summary>
    /// Base URL for the Genscape oil-fundamentals API, without a trailing slash. Each
    /// feed appends its own path (<c>crude-storage/weekly</c>,
    /// <c>crude-transportation/weekly</c>).
    /// </summary>
    public string BaseUrl { get; set; } = "https://api.genscape.com/oil-fundamentals/v1";

    /// <summary>
    /// The <c>Gen-Api-Key</c> header value. <c>"SEE_DB"</c> in appsettings; resolved at
    /// run time from <c>core.Param(LoaderName='Genscape', ParamName='ApiKey')</c> or
    /// from <c>DATALOADER_Loaders__Genscape__ApiKey</c>.
    ///
    /// <para>
    /// It travels in a HEADER, never in the query string, so it cannot appear in a
    /// logged URL, an exception message or an HTTP-client log line. It is never logged
    /// directly either.
    /// </para>
    /// </summary>
    public string ApiKey { get; set; } = "SEE_DB";

    /// <summary>
    /// The <c>revision</c> query parameter.
    ///
    /// <para>
    /// <b><c>revised</c> is the only value the API accepts</b> — verified live:
    /// <c>preliminary</c> and any other string come back <c>400 "Must have a value
    /// specified"</c>. It is a setting rather than a constant so a value the vendor
    /// adds later needs no code change, and the module warns at startup if it is
    /// changed away from <c>revised</c>.
    /// </para>
    /// <para>
    /// Because the feed is the REVISED series, the vendor restates recent weeks — which
    /// is exactly why the window is re-pulled rather than settled. See
    /// <see cref="SettledAfterDays"/>.
    /// </para>
    /// </summary>
    public string Revision { get; set; } = "revised";

    /// <summary>
    /// The REQUEST regions to enumerate. Both endpoints require one and answer
    /// <c>404</c> without it, so there is no "all regions" call.
    ///
    /// <para>
    /// ⚠ These are not the values stored in the <c>Region</c> column. Each response row
    /// carries its own finer-grained region — <c>NorthAmerica</c> returns Cushing,
    /// Patoka, Canada and others — and the two request regions OVERLAP:
    /// <c>Louisiana Gulf Coast</c> and <c>West Texas</c> come back from both. The
    /// overlapping rows were verified value-identical, so the duplication costs a
    /// harmless second merge of the same key and nothing else. Dropping
    /// <c>GulfCoast</c> would lose Beaumont-Nederland, Corpus Christi and Houston
    /// entirely.
    /// </para>
    /// </summary>
    public List<string> Regions { get; set; } = new() { "NorthAmerica", "GulfCoast" };

    /// <summary>Seconds before one HTTP request times out.</summary>
    public int HttpTimeoutSeconds { get; set; } = 120;

    // ---------------------------------------------------------------- the window

    /// <summary>
    /// How many days back from today (UTC) the window reaches. The window is
    /// <c>[today - DaysBack, today]</c> INCLUSIVE, so the shipped default of 30 covers
    /// the last <b>31 days</b> — which is what was asked for.
    /// </summary>
    public int DaysBack { get; set; } = 30;

    /// <summary>
    /// A window chunk whose NEWEST day is older than this many days is treated as
    /// SETTLED and gets a stable resume key — loaded once, then skipped forever. A
    /// newer one is HOT and gets a run-varying key so revisions are re-pulled.
    ///
    /// <para>
    /// <b>With the shipped 30/30 defaults the settled zone is EMPTY and everything is
    /// hot</b>: the oldest day in the window is exactly 30 days old and
    /// <c>age &gt; 30</c> is false. That is deliberate and matches ICE (30/30),
    /// EvolutionMarkets (30/30) and Criterion (30/30). This feed is
    /// <c>revision=revised</c>, so the vendor restates recent weeks — a stable key
    /// would freeze the first value seen while the loader reported clean runs.
    /// </para>
    /// <para>
    /// Setting this BELOW <see cref="DaysBack"/> creates a real settled zone and is
    /// logged as a warning at startup, because it is nearly always a misconfiguration
    /// rather than an intent.
    /// </para>
    /// </summary>
    public int SettledAfterDays { get; set; } = 30;

    /// <summary>How a hot chunk's key varies between runs. See <see cref="GenscapeHotKeyStrategy"/>.</summary>
    public GenscapeHotKeyStrategy HotKeyStrategy { get; set; } = GenscapeHotKeyStrategy.RunDate;

    /// <summary>
    /// The largest span of days one request may cover. The window is split into chunks
    /// of this size, and each chunk × region is one work unit.
    ///
    /// <para>
    /// <b>This exists because of the API's undocumented 5,000-row response cap.</b>
    /// Beyond it the response is silently truncated with the OLDEST rows dropped, and
    /// nothing in the payload says so. At the observed density — roughly 460 rows per
    /// year for the busiest feed/region — 365 days leaves an order of magnitude of
    /// headroom while keeping the default 31-day window in a single request. The reader
    /// also bisects on its own if a chunk still hits the cap, so this is the first line
    /// of defence rather than the only one.
    /// </para>
    /// <para>
    /// It is also part of the resume key: changing it moves the chunk boundaries and
    /// re-loads the window under fresh keys.
    /// </para>
    /// </summary>
    public int WindowChunkDays { get; set; } = 365;

    /// <summary>
    /// The response row count treated as "truncated". <b>5,000</b>, measured live: a
    /// 2000-2026 request for either feed and either region returns exactly 5,000 rows,
    /// and narrowing the window to 2016-2026 returns 4,944 — so it is a hard cap, not a
    /// coincidence.
    ///
    /// <para>
    /// Lower it to exercise the bisection path. Raise it only if the vendor confirms a
    /// higher cap: setting it above the real one re-admits exactly the silent
    /// truncation it exists to catch.
    /// </para>
    /// </summary>
    public int MaxRowsPerResponse { get; set; } = 5000;

    // ------------------------------------------------------------------ selection

    /// <summary>
    /// Feed ids to run. An unknown id is warned about and ignored rather than throwing
    /// — a stale config entry should not stop the whole loader.
    /// </summary>
    public List<string> EnabledFeeds { get; set; } = new()
    {
        GenscapeFeed.CrudeStorageWeekly,
        GenscapeFeed.CrudeTransportationWeekly
    };
}
