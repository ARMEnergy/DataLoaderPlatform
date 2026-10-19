using System.Globalization;
using DataLoader.Core.Abstractions;
using DataLoader.Core.Concurrency;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.EIA;

/// <summary>
/// One unit of work: <b>one series, one frequency, one period window</b>.
///
/// <para>
/// Not one unit per series: <c>electricity/rto/fuel-type-data</c> hourly is 27.7
/// million rows, which at the API's 5,000-row ceiling is ~5,540 requests — not
/// something that can be resumed, reported on, or fitted inside a work-unit timeout.
/// </para>
/// <para>
/// Not one unit per period either: most series are small, and a unit costs a
/// <c>core.LoadLog</c> round trip whether or not it does any work.
/// </para>
/// <para>
/// Units are independent — different (series, frequency, window) triples touch
/// disjoint primary keys — so they run in parallel safely. They still serialise on the
/// single merge proc inside the sink, which is deliberate (see <see cref="EiaSink"/>).
/// </para>
/// </summary>
internal sealed class EiaWorkUnit : WorkUnit
{
    public required EiaSeriesPlan Plan { get; init; }
    public required EiaFrequency Frequency { get; init; }

    /// <summary>
    /// First period of the window, in the frequency's own mask. This is the window's
    /// IDENTITY — it appears in <see cref="Key"/> — and is not necessarily where the
    /// read starts. See <see cref="ReadStart"/>.
    /// </summary>
    public required EiaPeriod WindowStart { get; init; }

    /// <summary>
    /// Where the read actually begins. Equal to <see cref="WindowStart"/> except on a
    /// HOT window that has already been read in full at least once, where it is moved
    /// forward to the revisable tail.
    ///
    /// <para>
    /// ⚠ <b>Why this is separate from <see cref="WindowStart"/>.</b> The sparse
    /// frequencies use wide windows — <c>annual</c> is 25 years — and the final window
    /// of every axis ends in the future, so it can never settle. Without this, every
    /// annual series would re-read 25 years of data daily to catch changes in the
    /// current year. Measured on 2026-10-17: 186 hot units per day costing ~1.4 hours,
    /// of which annual (67 units) was the largest group.
    /// </para>
    /// <para>
    /// The key is deliberately built from <see cref="WindowStart"/>, not from this, so
    /// narrowing does NOT change a window's identity: the same window keeps the same
    /// resume key whether it is read in full or in part, and nothing is re-downloaded
    /// when the narrowing is turned on or off.
    /// </para>
    /// </summary>
    public required EiaPeriod ReadStart { get; init; }

    /// <summary>True when <see cref="ReadStart"/> has been moved forward of the window start.</summary>
    public bool IsNarrowed => WindowStart.IsBefore(ReadStart);

    /// <summary>
    /// Last period of the window. <b>Never clamped to the series' end</b> — see
    /// <see cref="EiaWorkUnitProvider"/> for why.
    /// </summary>
    public required EiaPeriod WindowEnd { get; init; }

    /// <summary>
    /// True when the window is old enough that it will not be revised again, so the
    /// unit carries a STABLE key and is loaded exactly once, ever. See
    /// <see cref="EiaSettings.SettledAfterDays"/>.
    /// </summary>
    public required bool Settled { get; init; }

    /// <summary>Run-varying token appended to a HOT window's key. Null when settled.</summary>
    public string? HotToken { get; init; }

    /// <summary>
    /// Resume key.
    ///
    /// <para>
    /// A SETTLED window's key is a pure function of (series, frequency, window
    /// boundaries), and the boundaries are a pure function of
    /// (<c>arm.Series.StartPeriod</c>, the frequency's mask, the configured step
    /// count). Nothing in it depends on today's date, the API's current end period, or
    /// a row count — which is exactly what lets <c>core.LoadLog</c> skip it forever
    /// after the first run, and is why the backfill does not repeat itself.
    /// </para>
    /// <para>
    /// ⚠ Two things therefore invalidate settled keys and cause a one-off re-fetch:
    /// changing <c>Loaders:EIA:WindowSteps</c> for that frequency, and a change to
    /// <c>arm.Series.StartPeriod</c>. Neither loses data — the merge is idempotent —
    /// but both re-download. StartPeriod is the reason the request's "StartPeriod
    /// shouldn't change" is honoured in code as well as in the proc.
    /// </para>
    /// </summary>
    public override string Key
    {
        get
        {
            var baseKey =
                $"eia:series={Plan.Series.SeriesId};freq={Frequency.FrequencyId};" +
                $"from={WindowStart.Format()};to={WindowEnd.Format()}";

            return Settled ? baseKey : $"{baseKey};run={HotToken}";
        }
    }

    public override string DisplayName =>
        $"{Plan.Series.SeriesId} {Frequency.FrequencyId} {WindowStart}..{WindowEnd}";
}

/// <summary>
/// Builds the run's work units.
///
/// <para>
/// Order of operations:
/// </para>
/// <list type="number">
///   <item><c>arm.usp_GetActiveSeries</c> — which series to load.</item>
///   <item>per series, <c>arm.usp_GetSeriesFrequencies</c> and
///         <c>arm.usp_GetSeriesDataTypes</c>.</item>
///   <item>per series, ONE call to the API's metadata route for the authoritative
///         <c>endPeriod</c>.</item>
///   <item><c>arm.usp_UpdateSeriesEndPeriod</c> — advance-only stamp.</item>
///   <item>tile each (series × frequency) axis into windows and classify each as
///         settled or hot.</item>
/// </list>
/// <para>
/// Step 3 is ~95 requests at the head of a run. That is a real cost, accepted for two
/// reasons: it is the only authoritative source for how far a series goes (a forecast
/// series such as <c>steo</c> publishes to <c>2027-Q4</c>, so "today" is not an upper
/// bound), and the request requires EndPeriod to track the latest available period.
/// Against the tens of thousands of data requests a backfill makes, it rounds to zero.
/// </para>
/// </summary>
internal sealed class EiaWorkUnitProvider : IWorkUnitProvider<EiaWorkUnit>
{
    private readonly EiaCatalogRepository _catalog;
    private readonly EiaApiClient _api;
    private readonly EiaLoadHistory _history;
    private readonly EiaSettings _settings;
    private readonly ILogger<EiaWorkUnitProvider> _logger;

    public EiaWorkUnitProvider(
        EiaCatalogRepository catalog,
        EiaApiClient api,
        EiaLoadHistory history,
        IOptions<EiaSettings> settings,
        ILogger<EiaWorkUnitProvider> logger)
    {
        _catalog = catalog;
        _api = api;
        _history = history;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<EiaWorkUnit>> GetWorkUnitsAsync(LoaderRunContext context)
    {
        var ct = context.CancellationToken;

        var series = await _catalog.GetActiveSeriesAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("EIA: {Count} active data series in arm.Series", series.Count);

        if (_settings.EnabledSeries.Length > 0)
        {
            var wanted = new HashSet<string>(_settings.EnabledSeries, StringComparer.OrdinalIgnoreCase);
            var before = series.Count;
            series = series.Where(s => wanted.Contains(s.SeriesId)).ToList();
            _logger.LogWarning(
                "EIA: Loaders:EIA:EnabledSeries restricts this run to {Count} of {Before} series",
                series.Count, before);
        }

        if (series.Count == 0) return Array.Empty<EiaWorkUnit>();

        var plans = await BuildPlansAsync(series, ct).ConfigureAwait(false);

        var hotToken = BuildHotToken(context);
        var asOfUtc = context.StartedAtUtc;

        // Which windows have ever been read in full. Consulted ONLY to decide whether a
        // hot window's read may be narrowed; an empty set (history unreadable) means
        // nothing is narrowed, which is the fail-safe direction.
        var completed = _settings.NarrowHotWindowReads
            ? await _history.GetCompletedKeysAsync(ct).ConfigureAwait(false)
            : new HashSet<string>(StringComparer.Ordinal);

        var units = new List<EiaWorkUnit>();
        var hot = 0;
        var narrowed = 0;

        var excluded = new HashSet<string>(
            _settings.ExcludedFrequencies ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var skippedByExclusion = 0;

        foreach (var plan in plans)
        {
            foreach (var frequency in plan.Frequencies)
            {
                if (excluded.Contains(frequency.FrequencyId))
                {
                    skippedByExclusion++;
                    continue;
                }

                if (!EiaPeriod.TryParseMask(frequency.Format, out var mask))
                {
                    // Refuse rather than guess: the mask determines the step size, and a
                    // wrong step mis-tiles the axis into overlaps or holes -- and the
                    // holes would be recorded in core.LoadLog as successfully loaded.
                    _logger.LogError(
                        "EIA {Series} {Frequency}: Format '{Format}' is not a mask this loader can " +
                        "parse, so its windows cannot be built. Skipping the frequency. Add the mask " +
                        "to EiaPeriod or correct arm.SeriesFrequency.",
                        plan.Series.SeriesId, frequency.FrequencyId, frequency.Format ?? "<null>");
                    continue;
                }

                var axis = ResolveAxis(plan, frequency, mask);
                if (axis is null) continue;

                var (first, last) = axis.Value;
                var steps = ResolveWindowSteps(plan.Series.SeriesId, frequency.FrequencyId);
                var forceHot = _settings.ReloadAll ||
                               !(plan.Series.AllowIncremental ?? _settings.DefaultAllowIncremental);

                // Per-frequency lookback cap. Applied as a FILTER over the fixed grid,
                // never by moving `first` — moving it would shift every boundary (and
                // so every settled resume key) each day.
                DateTime? cutoff = null;
                if (_settings.MaxBackfillDaysByFrequency is not null &&
                    _settings.MaxBackfillDaysByFrequency.TryGetValue(frequency.FrequencyId, out var maxDays) &&
                    maxDays > 0)
                {
                    cutoff = asOfUtc.Date.AddDays(-maxDays);
                }

                var skippedByCutoff = 0;

                var windowStart = first;
                while (windowStart.IsAtOrBefore(last))
                {
                    // NOT clamped to `last`. Boundaries stay a pure function of
                    // (StartPeriod, mask, steps), so a settled window's key never moves
                    // as the series grows. Asking the API for a range that runs past the
                    // available data simply returns fewer rows.
                    var windowEnd = windowStart.AddSteps(steps - 1);

                    // Skipped on the window's LAST period, so a tile straddling the
                    // cutoff is still loaded and a forecast tile beyond today always is.
                    if (cutoff is not null && windowEnd.Anchor < cutoff.Value)
                    {
                        skippedByCutoff++;
                        windowStart = windowEnd.AddSteps(1);
                        continue;
                    }

                    var settled = !forceHot &&
                                  windowEnd.DaysBefore(asOfUtc) > Math.Max(0, _settings.SettledAfterDays);

                    var readStart = settled
                        ? windowStart
                        : ResolveHotReadStart(plan, frequency, windowStart, windowEnd, mask, asOfUtc, completed);

                    units.Add(new EiaWorkUnit
                    {
                        Plan = plan,
                        Frequency = frequency,
                        WindowStart = windowStart,
                        ReadStart = readStart,
                        WindowEnd = windowEnd,
                        Settled = settled,
                        HotToken = settled ? null : hotToken
                    });

                    if (!settled) hot++;
                    if (windowStart.IsBefore(readStart)) narrowed++;
                    windowStart = windowEnd.AddSteps(1);
                }

                if (skippedByCutoff > 0)
                    _logger.LogWarning(
                        "EIA {Series} {Frequency}: {Skipped} window(s) before {Cutoff:yyyy-MM-dd} NOT loaded " +
                        "(MaxBackfillDaysByFrequency). History older than that is abandoned, not deferred — " +
                        "raise the cap for one run to close the gap",
                        plan.Series.SeriesId, frequency.FrequencyId, skippedByCutoff, cutoff!.Value);
            }
        }

        if (skippedByExclusion > 0)
            _logger.LogWarning(
                "EIA: {Count} (series × frequency) axis/axes skipped by Loaders:EIA:ExcludedFrequencies " +
                "[{Excluded}] — their data is NOT loaded",
                skippedByExclusion, string.Join(", ", excluded));

        _logger.LogInformation(
            "EIA: {Units} work unit(s) across {Series} series — {Hot} hot (token {Token}), {Settled} settled. " +
            "Settled units already in core.LoadLog are skipped before any request is made",
            units.Count, plans.Count, hot, hotToken, units.Count - hot);

        if (narrowed > 0)
            _logger.LogInformation(
                "EIA: {Narrowed} of {Hot} hot window(s) narrowed to the last {Days} day(s) — they have " +
                "been read in full before, so only the revisable tail is re-fetched",
                narrowed, hot, _settings.SettledAfterDays);
        else if (_settings.NarrowHotWindowReads && hot > 0)
            _logger.LogInformation(
                "EIA: no hot window was narrowed — each is being read in full, either because it is " +
                "the first read or because the load history was unavailable");

        return units;
    }

    // ------------------------------------------------------------- planning ----

    private async Task<IReadOnlyList<EiaSeriesPlan>> BuildPlansAsync(
        IReadOnlyList<EiaSeries> series, CancellationToken ct)
    {
        var plans = new List<EiaSeriesPlan>(series.Count);
        var gate = new object();
        var failed = 0;

        await ParallelRunner.RunAsync(
            series,
            Math.Max(1, _settings.MaxConcurrentWorkUnits),
            async (s, token) =>
            {
                var frequencies = await _catalog.GetFrequenciesAsync(s.SeriesId, token).ConfigureAwait(false);
                var dataTypes = await _catalog.GetDataTypesAsync(s.SeriesId, token).ConfigureAwait(false);

                if (frequencies.Count == 0)
                {
                    _logger.LogWarning(
                        "EIA {Series}: arm.SeriesFrequency has no rows for it — nothing to request. Skipping",
                        s.SeriesId);
                    return;
                }

                if (dataTypes.Count == 0)
                {
                    // Without data[] the API returns rows with facets and no values --
                    // every DataJSON would be '{}'. That is not a load, it is a silent
                    // nullification, so refuse it.
                    _logger.LogWarning(
                        "EIA {Series}: arm.SeriesDataType has no rows for it. Requesting without data[] " +
                        "would store rows whose DataJSON is '{{}}' — skipping the series instead",
                        s.SeriesId);
                    return;
                }

                EiaSeriesMetadata metadata = default;
                try
                {
                    metadata = await _api.GetSeriesMetadataAsync(s.SeriesId, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // Not fatal to the run. Without metadata the axis falls back to the
                    // catalog's own EndPeriod, which is stale but workable; the series
                    // still loads everything up to that point.
                    _logger.LogError(ex,
                        "EIA {Series}: metadata route failed ({Message}). Falling back to the catalog's " +
                        "EndPeriod '{EndPeriod}'; anything published since will not load this run",
                        s.SeriesId, EiaHttp.Redact(ex.Message), s.EndPeriod ?? "<null>");
                    Interlocked.Increment(ref failed);
                }

                if (_settings.UpdateEndPeriod && !string.IsNullOrWhiteSpace(metadata.EndPeriod))
                {
                    try
                    {
                        await _catalog.UpdateEndPeriodAsync(s.SeriesId, metadata.EndPeriod!, token)
                                      .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        // Observational. A failed stamp must not stop the data loading.
                        _logger.LogWarning(ex,
                            "EIA {Series}: could not stamp EndPeriod='{EndPeriod}'", s.SeriesId, metadata.EndPeriod);
                    }
                }

                WarnIfApiStartsEarlier(s, metadata);

                var plan = new EiaSeriesPlan
                {
                    Series = s,
                    Frequencies = frequencies,
                    DataTypes = dataTypes,
                    DataFieldNames = BuildDataFieldNames(dataTypes),
                    ApiStartPeriod = metadata.StartPeriod,
                    ApiEndPeriod = metadata.EndPeriod
                };

                lock (gate) plans.Add(plan);
            },
            ct).ConfigureAwait(false);

        if (failed > 0)
            _logger.LogWarning("EIA: {Failed} of {Total} series could not be described by the metadata route",
                failed, series.Count);

        // ParallelRunner gives no ordering; sort so logs and keys are reproducible.
        plans.Sort(static (a, b) => string.CompareOrdinal(a.Series.SeriesId, b.Series.SeriesId));
        return plans;
    }

    /// <summary>
    /// Where a HOT window's read should begin.
    ///
    /// <para>
    /// A hot window is re-fetched on every run, and with the sparse frequencies' wide
    /// windows that means re-reading up to 25 years to catch changes in the current
    /// period. Where the window has ALREADY been read in full under a previous run
    /// token, only the revisable tail needs re-reading: everything older than
    /// <see cref="EiaSettings.SettledAfterDays"/> is, by the same assertion that governs
    /// settling, not going to change.
    /// </para>
    /// <para>
    /// ⚠ <b>Three guards, each of which fails SAFE toward reading more:</b>
    /// </para>
    /// <list type="number">
    ///   <item>the feature must be on (<see cref="EiaSettings.NarrowHotWindowReads"/>);</item>
    ///   <item>the window must have completed at least once before. A first read is
    ///         always full — otherwise the older half would be skipped today, skipped
    ///         again tomorrow under a fresh token, and the window would eventually
    ///         settle having never loaded it;</item>
    ///   <item>the computed start must actually fall INSIDE the window. If the cutoff
    ///         lands before the window begins there is nothing to narrow, and the full
    ///         window is read.</item>
    /// </list>
    /// <para>
    /// The cutoff is truncated DOWN to the frequency's own mask, so the period
    /// containing the boundary is always included rather than half-read.
    /// </para>
    /// </summary>
    private EiaPeriod ResolveHotReadStart(
        EiaSeriesPlan plan,
        EiaFrequency frequency,
        EiaPeriod windowStart,
        EiaPeriod windowEnd,
        EiaPeriodMask mask,
        DateTime asOfUtc,
        IReadOnlySet<string> completed)
    {
        if (!_settings.NarrowHotWindowReads) return windowStart;

        var baseKey =
            $"eia:series={plan.Series.SeriesId};freq={frequency.FrequencyId};" +
            $"from={windowStart.Format()};to={windowEnd.Format()}";

        if (!EiaLoadHistory.HasEverCompleted(completed, baseKey)) return windowStart;

        var cutoffInstant = asOfUtc.Date.AddDays(-Math.Max(0, _settings.SettledAfterDays));
        if (!EiaPeriod.TryParse(cutoffInstant.ToString("yyyy-MM-dd'T'HH", CultureInfo.InvariantCulture),
                                mask, out var cutoff))
            return windowStart;

        // Nothing to gain, and never move the start PAST the window's own end.
        if (!windowStart.IsBefore(cutoff)) return windowStart;
        if (!cutoff.IsAtOrBefore(windowEnd)) return windowStart;

        return cutoff;
    }

    /// <summary>
    /// The field names that belong in <c>DataJSON</c>. See
    /// <see cref="EiaSeriesPlan.DataFieldNames"/> for the evidence behind the three
    /// rules.
    /// </summary>
    internal static IReadOnlySet<string> BuildDataFieldNames(IReadOnlyList<EiaDataType> dataTypes)
    {
        // Ordinal: the partition decides which JSON object a field lands in, and so
        // decides the row's primary key. It must not vary with the server's culture.
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var dt in dataTypes)
        {
            names.Add(dt.DataTypeId);
            names.Add(dt.DataTypeId + "-units");
        }

        // The v1-style routes (natural-gas/*, petroleum/*) name their units column
        // plainly "units" rather than "<datatype>-units". Note the SINGULAR "unit" and
        // "unitName" are NOT here: the `international` series carries both as facets.
        names.Add("units");

        return names;
    }

    /// <summary>
    /// The (first, last) period to tile, in the frequency's own mask.
    ///
    /// <para>
    /// The lower bound comes from <c>arm.Series.StartPeriod</c> — never from the API —
    /// because the request is explicit that StartPeriod must not change, and because it
    /// is what the settled resume keys are built from. It is re-expressed from the
    /// series' <c>DefaultDateFormat</c> into this frequency's mask: for
    /// <c>crude-oil-imports</c>, StartPeriod <c>2009-01</c> becomes <c>2009</c> for the
    /// annual frequency.
    /// </para>
    /// <para>
    /// The upper bound is the API's <c>endPeriod</c> when available, else the catalog's.
    /// </para>
    /// </summary>
    private (EiaPeriod First, EiaPeriod Last)? ResolveAxis(
        EiaSeriesPlan plan, EiaFrequency frequency, EiaPeriodMask mask)
    {
        var series = plan.Series;

        // The mask StartPeriod/EndPeriod are WRITTEN in. Fall back to the frequency's
        // own mask when the series does not declare one.
        var sourceMask = EiaPeriod.TryParseMask(series.DefaultDateFormat, out var dm) ? dm : mask;

        if (!EiaPeriod.TryParse(series.StartPeriod, sourceMask, out var start))
        {
            _logger.LogError(
                "EIA {Series} {Frequency}: StartPeriod '{StartPeriod}' cannot be read as '{Mask}'. " +
                "Skipping the frequency — guessing a start would re-key every window later",
                series.SeriesId, frequency.FrequencyId,
                series.StartPeriod ?? "<null>", EiaPeriod.MaskToFormat(sourceMask));
            return null;
        }

        var endText = plan.ApiEndPeriod ?? series.EndPeriod;
        if (!EiaPeriod.TryParse(endText, sourceMask, out var end))
        {
            _logger.LogError(
                "EIA {Series} {Frequency}: EndPeriod '{EndPeriod}' cannot be read as '{Mask}'. " +
                "Skipping the frequency",
                series.SeriesId, frequency.FrequencyId,
                endText ?? "<null>", EiaPeriod.MaskToFormat(sourceMask));
            return null;
        }

        var first = start.ToMask(mask);
        var last = end.ToMask(mask);

        if (last.IsBefore(first))
        {
            _logger.LogWarning(
                "EIA {Series} {Frequency}: EndPeriod {End} is before StartPeriod {Start}. Skipping",
                series.SeriesId, frequency.FrequencyId, last, first);
            return null;
        }

        return (first, last);
    }

    /// <summary>
    /// Window size for one axis: a per-(series, frequency) override first, then the
    /// per-frequency value, then <see cref="EiaSettings.DefaultWindowSteps"/>.
    ///
    /// <para>
    /// Exposed <c>internal</c> so the resolution ORDER is pinned by test — getting the
    /// precedence wrong would silently re-tile an axis that has an override.
    /// </para>
    /// </summary>
    internal static int ResolveWindowSteps(EiaSettings settings, string seriesId, string frequencyId)
    {
        if (settings.WindowStepsBySeries is not null &&
            settings.WindowStepsBySeries.TryGetValue($"{seriesId}:{frequencyId}", out var bySeries) &&
            bySeries > 0)
            return bySeries;

        if (settings.WindowSteps is not null &&
            settings.WindowSteps.TryGetValue(frequencyId, out var byFrequency) &&
            byFrequency > 0)
            return byFrequency;

        return Math.Max(1, settings.DefaultWindowSteps);
    }

    private int ResolveWindowSteps(string seriesId, string frequencyId) =>
        ResolveWindowSteps(_settings, seriesId, frequencyId);

    private void WarnIfApiStartsEarlier(EiaSeries series, EiaSeriesMetadata metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata.StartPeriod) ||
            string.IsNullOrWhiteSpace(series.StartPeriod))
            return;

        if (!EiaPeriod.TryParseMask(series.DefaultDateFormat, out var mask)) return;
        if (!EiaPeriod.TryParse(metadata.StartPeriod, mask, out var apiStart)) return;
        if (!EiaPeriod.TryParse(series.StartPeriod, mask, out var catalogStart)) return;
        if (!apiStart.IsBefore(catalogStart)) return;

        // Deliberately a warning and not an override: the request says StartPeriod must
        // not change, and silently widening the axis would re-key nothing but would
        // load history the catalog says is out of scope.
        _logger.LogWarning(
            "EIA {Series}: the API publishes from {ApiStart} but arm.Series.StartPeriod is {CatalogStart}. " +
            "The earlier history is NOT loaded — StartPeriod is treated as authoritative. Change the " +
            "catalog if it should be",
            series.SeriesId, apiStart, catalogStart);
    }

    /// <summary>
    /// The token appended to every hot window's key this run.
    ///
    /// <para>
    /// An unmapped strategy THROWS rather than quietly falling back to a stable key:
    /// the fallback would freeze the hot zone and silently stop picking up the
    /// revisions it exists to catch.
    /// </para>
    /// </summary>
    internal static string BuildHotToken(EiaHotKeyStrategy strategy, DateTime startedAtUtc, Guid runId) =>
        strategy switch
        {
            EiaHotKeyStrategy.RunDate => startedAtUtc.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
            EiaHotKeyStrategy.RunHour => startedAtUtc.ToString("yyyyMMddHH", CultureInfo.InvariantCulture),
            EiaHotKeyStrategy.RunId   => runId.ToString("N"),
            _ => throw new NotSupportedException($"Unmapped EiaHotKeyStrategy '{strategy}'.")
        };

    private string BuildHotToken(LoaderRunContext context) =>
        BuildHotToken(_settings.HotKeyStrategy, context.StartedAtUtc, context.RunId);
}
