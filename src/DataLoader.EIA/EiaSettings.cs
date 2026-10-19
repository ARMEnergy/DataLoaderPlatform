using DataLoader.Core.Configuration;

namespace DataLoader.EIA;

/// <summary>How a hot window's resume key varies between runs.</summary>
public enum EiaHotKeyStrategy
{
    /// <summary>Suffix with the UTC run date <c>yyyyMMdd</c> → one re-pull per UTC day. <b>The default.</b></summary>
    RunDate,

    /// <summary>Suffix with UTC <c>yyyyMMddHH</c> → one re-pull per clock hour.</summary>
    RunHour,

    /// <summary>Suffix with the run id → <b>every</b> invocation re-pulls (diagnostics / forced re-pull).</summary>
    RunId
}

/// <summary>
/// EIA loader settings, bound from <c>Loaders:EIA</c>.
/// </summary>
public sealed class EiaSettings : LoaderSettingsBase
{
    /// <summary>
    /// EIA-specific defaults for the inherited retry/timeout knobs. These are set here,
    /// rather than left at <see cref="LoaderSettingsBase"/>'s, so the CLASS defaults
    /// agree with what <c>appsettings.json</c> ships — otherwise a missing config key
    /// would silently produce a different, untested shape.
    ///
    /// <para>
    /// ⚠ They are load-bearing together. Retries are serial and each attempt now gets
    /// its own <see cref="HttpTimeoutSeconds"/> budget, so one request's worst case is
    /// <c>(RetryCount + 1) × HttpTimeoutSeconds</c> plus backoff — 465s at these values.
    /// <see cref="LoaderSettingsBase.WorkUnitTimeoutSeconds"/> must stay comfortably
    /// above that or a unit is cancelled mid-retry and the real failure is replaced by a
    /// generic cancellation. <c>EiaResilienceTests</c> asserts the inequality, which is
    /// how the mismatch this constructor fixes was found.
    /// </para>
    /// </summary>
    public EiaSettings()
    {
        RetryCount = 4;
        RetryDelayMs = 1000;
        WorkUnitTimeoutSeconds = 3600;
    }

    /// <summary>
    /// 🔒 The API key. Ships as <c>SEE_DB</c> and resolves from
    /// <c>core.Param(LoaderName='EIA', ParamName='ApiKey')</c>.
    ///
    /// <para>
    /// EIA offers NO header form — the key travels in the QUERY STRING of every
    /// request, like OilX and EnergyAspects. That is why the named HttpClient calls
    /// <c>RemoveAllLoggers()</c> and why nothing derived from a URI is logged
    /// unredacted. See <see cref="EiaHttp"/>.
    /// </para>
    /// </summary>
    public string ApiKey { get; set; } = "SEE_DB";

    /// <summary>Base address. Must end in a slash for relative URIs to compose.</summary>
    public string BaseUrl { get; set; } = "https://api.eia.gov/v2/";

    /// <summary>
    /// Timeout for ONE HTTP attempt.
    ///
    /// <para>
    /// ⚠ This is applied by the Polly pipeline, NOT by
    /// <see cref="HttpClient.Timeout"/> — which is deliberately infinite. A client
    /// timeout bounds the whole retried operation, so setting it here would make five
    /// attempts share one budget; see <c>EiaHttp.BuildResiliencePipeline</c>.
    /// </para>
    /// <para>
    /// 90s rather than 120 because the api.data.gov gateway abandons a slow request
    /// itself at around 60s with a 503/504. A budget much above that just delays the
    /// retry of a request the vendor has already given up on. Measured on
    /// <c>electricity/rto/daily-fuel-type-data</c>, which answers anywhere between 1.4s
    /// and a 60s gateway timeout for the SAME request shape.
    /// </para>
    /// </summary>
    public int HttpTimeoutSeconds { get; set; } = 90;

    /// <summary>
    /// Rows per request. <b>5,000 is the API's hard ceiling</b> — asking for more
    /// returns 5,000 anyway plus a <c>"parameter out of range: length"</c> warning, so
    /// a higher value here is silently ignored and the loader treats it as a
    /// configuration error rather than pretending it worked.
    /// </summary>
    public int PageSize { get; set; } = 5000;

    /// <summary>
    /// Safety stop on the total number of HTTP requests ONE work unit may make —
    /// counting both offset pages and the requests spent subdividing
    /// (<see cref="MaxRowsPerWindow"/>). Exceeding it throws rather than looping forever
    /// against a vendor that keeps saying "there is more".
    ///
    /// <para>
    /// Sized well above normal: a window at <see cref="MaxRowsPerWindow"/> needs ~5
    /// requests, and even a badly oversized window that subdivides all the way down
    /// stays in the low hundreds. Hitting 2,000 means something is wrong, not that the
    /// series is big.
    /// </para>
    /// </summary>
    public int MaxPagesPerWindow { get; set; } = 2000;

    /// <summary>
    /// Rows per TVP merge. The reader streams and flushes at this size rather than
    /// materialising a whole window — the largest windows are hundreds of thousands of
    /// rows and there are <see cref="LoaderSettingsBase.MaxConcurrentWorkUnits"/> of
    /// them in flight.
    /// </summary>
    public int MergeBatchRows { get; set; } = 5000;

    /// <summary>
    /// 🔒 Client-side request throttle, shared across all concurrent work units.
    ///
    /// <para>
    /// api.data.gov (the <c>api-umbrella</c> gateway in front of EIA) rate-limits per
    /// KEY, not per connection, and the documented consequence of exceeding it is that
    /// <b>the key is temporarily suspended</b> — which would stop every other EIA job
    /// using the same key, not just this run. A full backfill is on the order of tens
    /// of thousands of requests, so this is the setting that decides whether the
    /// backfill completes or gets the key locked. Raise it only with evidence.
    /// </para>
    /// </summary>
    public double RequestsPerSecond { get; set; } = 5;

    /// <summary>
    /// How many periods one work unit covers, per frequency id. One "step" is one unit
    /// of that frequency's own mask (a year for annual, an hour for hourly), so the
    /// numbers are not comparable across rows — <c>weekly: 520</c> is 520 DAYS, because
    /// weekly shares the <c>YYYY-MM-DD</c> mask.
    ///
    /// <para>
    /// <b>⚠ CHANGING A VALUE HERE RE-TILES THAT FREQUENCY AND INVALIDATES ITS RESUME
    /// KEYS.</b> Window boundaries appear verbatim in the key, so every settled window
    /// of that frequency is re-fetched once under its new boundaries. That is correct
    /// but expensive; for the hourly series it is ~27.7 million rows. Nothing is lost
    /// (the merge is idempotent) — it is purely a re-download.
    /// </para>
    /// <para>
    /// <b>Sized from measured row density</b> so one window is ~25,000 rows ≈ 5 pages,
    /// which keeps <c>offset</c> at or below ~20,000.
    /// </para>
    /// <para>
    /// ⚠ <b>An earlier version of this note claimed a hard "offset cliff" — 0% failures
    /// to offset 100,000 and 100% (4/4 HTTP 503) at 400,000. That was RETRACTED on
    /// 2026-10-16.</b> It was measured during an EIA-side outage; once the endpoint
    /// recovered, offset 400,000 and 700,000 both answered in 6–9s. The sizing is kept
    /// because bounded windows are good practice and because small windows limit what a
    /// failed request costs — not because deep offsets are known to fail.
    /// </para>
    /// <para>
    /// Densities behind the numbers (live <c>total</c> ÷ span, 2026-10-16):
    /// hourly ≈ 406 rows/hour · daily ≈ 2,012 rows/day · monthly ≈ 2,649 rows/month
    /// (<c>crude-oil-imports</c>) · quarterly ≈ 1,300 rows/quarter (<c>steo</c>) ·
    /// annual ≈ 5,114 rows/year (<c>crude-oil-imports</c>) · weekly and
    /// four-week-average are small (8k–16k rows for ALL of history).
    /// </para>
    /// <para>
    /// Density varies by orders of magnitude WITHIN a frequency — <c>international</c>
    /// annual is 55,711 rows/year against <c>coal/market-sales-price</c>'s 156 — so a
    /// per-frequency number cannot fit everything. <see cref="WindowStepsBySeries"/>
    /// overrides the outliers, and <see cref="MaxRowsPerWindow"/> is the automatic
    /// backstop for anything still mis-sized.
    /// </para>
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Revised 2026-10-16 after the first real backfill.</b> Sizing every frequency
    /// for its DENSEST member over-fragmented the many sparse ones: `monthly: 6` produced
    /// <b>4,398 units across 49 mostly-tiny series</b>, and `annual: 5` produced 703
    /// across 77 — together 40% of a 12,755-unit backfill, most of them fetching a few
    /// thousand rows in a single request. That was the right trade BEFORE
    /// <see cref="MaxRowsPerWindow"/> subdivision existed; now that a too-big window
    /// splits itself (verified live on a 704,880-row window), the sparse frequencies can
    /// take much wider windows and let the dense outliers subdivide. monthly 6→60,
    /// annual 5→25, quarterly 12→40 cuts ~4,550 units.
    ///
    /// <para>
    /// <b>daily is deliberately NOT widened.</b> 9.7M rows are already loaded under its
    /// current boundaries; changing them would invalidate those settled resume keys and
    /// re-fetch the lot.
    /// </para>
    ///
    /// <para>
    /// ⚠ <b><c>hourly</c> was briefly set to 1 — one hour per request — on 2026-10-16,
    /// and has been REVERTED.</b> It was a response to persistent 504s that turned out
    /// to be an EIA-side outage rather than anything about our request shape. Once the
    /// endpoint recovered, 48 interleaved requests across six window/facet shapes — an
    /// unfaceted 8-day query covering 91,637 rows included — all succeeded at a 2.1–2.5s
    /// median, and offset 700,000 (which had failed 3/3 during the outage) returned in
    /// 8.6s.
    /// </para>
    /// <para>
    /// A narrow window is not cheaper anyway: latency is per-REQUEST, not per-row, so
    /// 1-hour tiles cost ~48 requests where 48-hour tiles cost ~5. And 1-hour tiles over
    /// the full 2019-onward axis would be <b>70,080 work units</b>, which is why they
    /// were paired with a lookback cap that abandoned ten months of history.
    /// </para>
    /// <para>
    /// The lesson is about measurement, not sizing: nearly every performance number
    /// gathered during that outage was an artefact of it. <c>docs/apis/EIA.md</c> §7
    /// records what survived re-testing and what was retracted.
    /// </para>
    /// </remarks>
    public Dictionary<string, int> WindowSteps { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["annual"]            = 25,    // 25 years — sparse; dense members subdivide
        ["quarterly"]         = 40,    // 10 years
        ["monthly"]           = 60,    // 5 years  — was 6, which made 4,398 tiny units
        ["weekly"]            = 1825,  // 5 years of days — these series are tiny
        ["four-week-average"] = 1825,  // 5 years of days
        ["daily"]             = 12,    // ⚠ do not change: 9.7M rows settled under this
        ["hourly"]            = 48,    // ⚠ do not change: 24.26M rows settled under this
        ["local-hourly"]      = 48
    };


    /// <summary>
    /// Per-frequency cap on how far back to load, in days. A frequency absent from this
    /// dictionary (the default for all of them) loads its whole history from
    /// <c>arm.Series.StartPeriod</c>.
    ///
    /// <para>
    /// ⚠ <b>The tiling grid does NOT move.</b> Windows are still laid out from
    /// StartPeriod in fixed steps; the cap only decides which of those fixed tiles get
    /// emitted. If the cutoff moved the grid, every boundary — and therefore every
    /// settled resume key — would shift each day and the loader would re-download
    /// continuously. A tile is skipped when its LAST period falls before the cutoff, so
    /// forecast tiles beyond today are always emitted.
    /// </para>
    /// <para>
    /// ⚠ <b>Capping a frequency abandons the history before the cutoff</b> — it is not
    /// deferred, it is simply never requested. Anything already loaded stays in
    /// <c>arm.SeriesData</c> untouched. To close a gap afterwards, raise the cap (or
    /// remove it) for one targeted run, optionally scoped with
    /// <see cref="EnabledSeries"/>.
    /// </para>
    /// <para>
    /// This exists because the two levers are coupled: a narrow window only makes sense
    /// with a bounded lookback. <c>hourly</c> at 1-hour windows over the full
    /// 2019-onward axis would be <b>70,080 work units</b>.
    /// </para>
    /// </summary>
    /// <remarks>
    /// <b>Ships EMPTY — nothing is capped, every frequency loads its full history.</b>
    /// It was briefly populated with <c>hourly: 7</c> on 2026-10-16 to escape persistent
    /// 504s, and reverted the same day once those proved to be an EIA-side outage; that
    /// cap had abandoned ten months of hourly history.
    ///
    /// <para>
    /// The mechanism is kept because it is the only safe way to bound a frequency if one
    /// is ever genuinely needed — but it is opt-in, because silently truncating any of
    /// the 154 axes is far worse than a slow backfill. Anything capped is abandoned, not
    /// deferred.
    /// </para>
    /// </remarks>
    public Dictionary<string, int> MaxBackfillDaysByFrequency { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Frequencies to skip entirely, by <c>FrequencyId</c>.
    ///
    /// <para>
    /// Ships excluding <c>local-hourly</c>. That frequency covers the SAME INSTANTS as
    /// <c>hourly</c> for <c>electricity/rto/fuel-type-data</c> — 27.7M rows, 1,460 work
    /// units, ~29% of the whole backfill — differing only in that each row is labelled in
    /// its respondent's local offset (<c>2026-10-15T02-04</c>) rather than UTC
    /// (<c>2026-10-15T06</c>). Excluded by operator decision on 2026-10-16 as duplicate
    /// for current needs; re-enable by removing it here and the windows load on the next
    /// run, since nothing else about them changes.
    /// </para>
    /// <para>
    /// ⚠ <b>This is an operational lever, not the architectural one.</b> Scope belongs in
    /// the catalog — <c>arm.SeriesFrequency.Enabled</c> — and the permanent fix is to
    /// populate that column (it is NULL on all 371 rows today) and add the predicate to
    /// <c>arm.usp_GetSeriesFrequencies</c>. This setting exists because that is a change
    /// to the shared catalog and to the query the loader request specified, neither of
    /// which should happen as a side effect of a throughput decision.
    /// </para>
    /// </summary>
    public string[] ExcludedFrequencies { get; set; } = new[] { "local-hourly" };

    /// <summary>
    /// Per-(series, frequency) overrides of <see cref="WindowSteps"/>, keyed
    /// <c>"&lt;SeriesId&gt;:&lt;FrequencyId&gt;"</c>. Falls back to the per-frequency
    /// value, then to <see cref="DefaultWindowSteps"/>.
    ///
    /// <para>
    /// Shipped pre-populated for the one measured series whose density is far outside
    /// its frequency's norm: <c>international</c> publishes 55,711 rows per YEAR
    /// (4,289,760 rows over 1949-2025), so the 5-year annual default would build
    /// 278,000-row windows. One year is already 11 pages there — and fortunately that
    /// endpoint's offset cost is FLAT (offset 0 and offset 50,000 both answered in 7.6s),
    /// so paging within a single year is fine.
    /// </para>
    /// <para>
    /// ⚠ Same warning as <see cref="WindowSteps"/>: an entry here re-tiles that axis and
    /// invalidates its settled resume keys once.
    /// </para>
    /// </summary>
    public Dictionary<string, int> WindowStepsBySeries { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["international:annual"] = 1
    };

    /// <summary>
    /// The automatic backstop. When a window's <c>response.total</c> exceeds this, the
    /// reader SPLITS the window in half and reads each half separately instead of paging
    /// deeper — recursively, down to a single period.
    ///
    /// <para>
    /// This is what makes the loader safe against a mis-sized <see cref="WindowSteps"/>
    /// and against a series whose density grows over time: a window that turns out to be
    /// ten times bigger than expected subdivides itself rather than walking an offset
    /// into the range where EIA starts returning 503s.
    /// </para>
    /// <para>
    /// Subdivision is INTERNAL to a work unit, so resume keys are unaffected — the key
    /// names the top-level window and the unit still succeeds or fails as a whole.
    /// </para>
    /// <para>
    /// Defaults to 5 × <see cref="PageSize"/>. Set to 0 to disable subdivision and page
    /// by offset however deep it goes (the pre-2026-10-16 behaviour; not recommended).
    /// </para>
    /// </summary>
    public int MaxRowsPerWindow { get; set; } = 25000;

    /// <summary>
    /// Window size for a frequency absent from <see cref="WindowSteps"/>. Deliberately
    /// small: an unknown frequency is an unknown row density, and an over-large window
    /// risks a unit that cannot finish inside
    /// <see cref="LoaderSettingsBase.WorkUnitTimeoutSeconds"/>.
    /// </summary>
    public int DefaultWindowSteps { get; set; } = 60;

    /// <summary>
    /// A window whose LAST period is older than this many days is SETTLED: it keeps a
    /// stable resume key and so is loaded exactly once, ever. Newer windows are HOT and
    /// get a run-varying key (see <see cref="HotKeyStrategy"/>).
    ///
    /// <para>
    /// This is what makes the first run a full backfill (~90M rows) and every later run
    /// cheap: settled units are skipped by <c>core.LoadLog</c> before a single request
    /// is made. It is also the only thing standing between this loader and re-pulling
    /// all of history daily.
    /// </para>
    /// <para>
    /// 45 days is chosen over OilX's 1 because EIA RESTATES: monthly series routinely
    /// revise the previous two publications, and weekly petroleum series revise the
    /// prior few weeks. It is NOT set to cover multi-year revisions — those are picked
    /// up with <see cref="ReloadAll"/> on demand, which is far cheaper than keeping
    /// years hot on every run.
    /// </para>
    /// </summary>
    public int SettledAfterDays { get; set; } = 45;

    /// <summary>How a hot window's key varies between runs.</summary>
    public EiaHotKeyStrategy HotKeyStrategy { get; set; } = EiaHotKeyStrategy.RunDate;

    /// <summary>
    /// On a HOT window that has already been read in full, re-read only the last
    /// <see cref="SettledAfterDays"/> days instead of the whole window.
    ///
    /// <para>
    /// ⚠ <b>What this fixes.</b> The sparse frequencies use wide windows — <c>annual</c>
    /// is 25 years, <c>monthly</c> 5 — and the FINAL window of every axis ends in the
    /// future, so it can never settle. Without this, every annual series re-reads 25
    /// years of data on every run to pick up changes in the current year. Measured
    /// 2026-10-17: <b>186 hot units per day, ~1.4 hours</b> at 4-way concurrency, of
    /// which annual alone was 67 units.
    /// </para>
    /// <para>
    /// It is sound for exactly the reason settling is: nothing older than
    /// <see cref="SettledAfterDays"/> is expected to be revised. If that assumption is
    /// wrong, it is already wrong for every settled window in the table.
    /// </para>
    /// <para>
    /// ⚠ <b>It never changes a window's identity.</b> The resume key is built from the
    /// window's own bounds, not from where the read starts, so turning this on or off
    /// re-downloads nothing and invalidates no settled key.
    /// </para>
    /// <para>
    /// A window is narrowed only if it has completed at least once before — the first
    /// read is always full, or the older half would be skipped today and again tomorrow
    /// under a fresh run token, and the window would settle having never loaded it. Set
    /// to <c>false</c> to re-read every hot window in full.
    /// </para>
    /// </summary>
    public bool NarrowHotWindowReads { get; set; } = true;

    /// <summary>
    /// Force EVERY window hot for one run — a full re-verify of all history against the
    /// API. Idempotent (the merge only updates rows whose DataHash moved) but very
    /// expensive: tens of thousands of requests. Use it after a vendor restatement
    /// announcement, not on a schedule.
    /// </summary>
    public bool ReloadAll { get; set; } = false;

    /// <summary>
    /// What a NULL <c>arm.Series.AllowIncremental</c> means. The column is NULL on every
    /// row of the live catalog, and reading NULL as <c>false</c> would put every series
    /// in full-reload mode — so the default here is <c>true</c>.
    ///
    /// <para>
    /// <c>AllowIncremental = 0</c> on a series makes all of its windows hot, exactly as
    /// <see cref="ReloadAll"/> does globally.
    /// </para>
    /// </summary>
    public bool DefaultAllowIncremental { get; set; } = true;

    /// <summary>
    /// Coerce a numeric-looking STRING to a JSON number in the canonical form. See the
    /// long remarks on <see cref="EiaCanonicalJson"/> — this is what makes the key
    /// stable across EIA's string/number type flips, and it is what the loader request's
    /// worked example specifies.
    ///
    /// <para><b>⚠ Flipping this on a populated table re-keys rows. It is a migration,
    /// not a toggle.</b></para>
    /// </summary>
    public bool CoerceNumericStrings { get; set; } = true;

    /// <summary>
    /// Send <c>sort[0][column]=period&amp;sort[0][direction]=asc</c> with every data
    /// request, so offset paging walks a defined order.
    ///
    /// <para>
    /// Paging was measured stable both with and without it (3,739 rows over four pages,
    /// zero duplicates, zero gaps, 2026-10-16), so this is belt-and-braces rather than a
    /// fix for an observed fault. It stays on because an undefined order is only safe
    /// until the vendor changes its default.
    /// </para>
    /// </summary>
    public bool SortByPeriod { get; set; } = true;

    /// <summary>
    /// Restrict the run to these series ids. Empty (the default) means "every series
    /// <c>arm.usp_GetActiveSeries</c> returns". Intended for a targeted backfill or for
    /// reproducing one series' problem, not for steady state — the catalog's
    /// <c>Enabled</c> flag is the supported way to choose what loads.
    /// </summary>
    public string[] EnabledSeries { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Stamp <c>arm.Series.EndPeriod</c> from the API's metadata route after reading it.
    /// Advance-only, in the proc. See <c>arm.usp_UpdateSeriesEndPeriod</c>.
    /// </summary>
    public bool UpdateEndPeriod { get; set; } = true;

    /// <summary>Run <c>arm.usp_ValidateLoad</c> after the run and log what it reports.</summary>
    public bool RunValidationAfterLoad { get; set; } = true;

    /// <summary>
    /// Command timeout for the post-load validator, in seconds. Separate from the
    /// default 30 because the table is large; every check inside the proc is bounded by
    /// SeriesId, which leads the clustered key, but 30s is still too tight a budget
    /// against a 90M-row table under concurrent merge load. Criterion's validator
    /// taught this the hard way — a timeout there was silent and took every other check
    /// down with it.
    /// </summary>
    public int ValidationTimeoutSeconds { get; set; } = 300;
}
