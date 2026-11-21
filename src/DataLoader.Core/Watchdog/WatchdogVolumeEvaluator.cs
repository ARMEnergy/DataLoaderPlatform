using System.Globalization;

namespace DataLoader.Core.Watchdog;

/// <summary>
/// The <c>DATA_VOLUME</c> rule: did a loader that ran successfully write an unusual number
/// of rows on a recent Central day, compared with the median of the same weekday in
/// earlier weeks? Pure — no clock, no database — so every rule is unit-testable; the
/// caller supplies <c>todayCentral</c> and the two reads.
///
/// <para>Rules (design 2026-11-20, §R.4 as amended by §A1/§A2/§A5):</para>
/// <list type="number">
/// <item>Judged days: <c>today − WindowDays … today − 1</c>, shifted back one day until the
/// Central clock passes <c>SettleHours</c> (yesterday's late runs have not written their
/// summary rows yet). Never today — it is not over.</item>
/// <item>A day's rows are the sum over ALL its runs; the day exists only with ≥ 1
/// successful run (see <see cref="DailyVolume"/>).</item>
/// <item>Samples: walk back D−7, D−14, … to D − 7 × <c>MaxBaselineLookbackWeeks</c>,
/// skipping missing days and EXCLUDED days, stopping at <c>BaselineFromCentral</c>; keep
/// the most recent <c>BaselineWeeks</c>.</item>
/// <item>Unjudged when samples &lt; <c>MinSamples</c>, median &lt; effective
/// <c>MinMedianRows</c>, or median = 0. Otherwise Zero → Low → High, first match wins.</item>
/// </list>
///
/// <para>⚠ The exclusion set is what stops a learned baseline raising its own goalposts:
/// a day that was reported (open, or closed with anything but <c>NotDetected</c>) is never
/// a sample. Without it, a loader that halved for six weeks would by week seven have a
/// halved median and fall silent while still broken.</para>
/// </summary>
public static class WatchdogVolumeEvaluator
{
    /// <summary>
    /// Why the whole section is unusable, or null when it is valid. An invalid section
    /// skips the check entirely — no query, DATA_VOLUME neither checked nor degraded.
    /// </summary>
    public static string? ValidateSection(WatchdogDataVolumeSettings? s)
    {
        if (s is null) return "Platform:Watchdog:DataVolume failed to bind";
        var problems = new List<string>();
        if (s.WindowDays is < 1 or > 7) problems.Add($"WindowDays={s.WindowDays} must be in [1, 7]");
        if (s.SettleHours is < 0 or > 23) problems.Add($"SettleHours={s.SettleHours} must be in [0, 23]");
        // ⚠ The exclusion set lives in core.WatchdogFinding, whose resolved rows are purged
        // after 180 days (sql/Core/007). A span reaching past that would let a purged,
        // once-reported day back in as a normal sample.
        if ((long)s.WindowDays + 7L * s.MaxBaselineLookbackWeeks >= MaxSpanDays)
            problems.Add($"WindowDays + 7 x MaxBaselineLookbackWeeks = {(long)s.WindowDays + 7L * s.MaxBaselineLookbackWeeks} " +
                         $"must be < {MaxSpanDays} (the resolved-finding retention)");
        if (s.BaselineWeeks < 1) problems.Add($"BaselineWeeks={s.BaselineWeeks} must be >= 1");
        if (s.MinSamples < 1) problems.Add($"MinSamples={s.MinSamples} must be >= 1");
        if (s.MinSamples > s.BaselineWeeks)
            problems.Add($"MinSamples={s.MinSamples} must be <= BaselineWeeks={s.BaselineWeeks}");
        if (s.MaxBaselineLookbackWeeks < s.BaselineWeeks)
            problems.Add($"MaxBaselineLookbackWeeks={s.MaxBaselineLookbackWeeks} must be >= BaselineWeeks={s.BaselineWeeks}");
        if (s.MinMedianRows < 0) problems.Add($"MinMedianRows={s.MinMedianRows} must be >= 0");
        if (s.LowPercent is < 1 or > 99) problems.Add($"LowPercent={s.LowPercent} must be in [1, 99]");
        if (!(s.HighMultiple > 1)) problems.Add($"HighMultiple={s.HighMultiple.ToString(CultureInfo.InvariantCulture)} must be > 1");
        return problems.Count == 0 ? null : string.Join("; ", problems);
    }

    /// <summary>
    /// Days a resolved finding is kept by <c>core.usp_RecordWatchdogPass</c>'s retention
    /// sweep (<c>sql/Core/007</c>). The query span must stay below it — see
    /// <see cref="WatchdogDataVolumeSettings.MaxBaselineLookbackWeeks"/>.
    /// </summary>
    public const int MaxSpanDays = 180;

    /// <summary>
    /// Whether yesterday has settled — every run that started yesterday has had time to
    /// complete and write its summary row. See <see cref="WatchdogDataVolumeSettings.SettleHours"/>.
    /// </summary>
    public static bool YesterdaySettled(WatchdogDataVolumeSettings s, DateTime nowCentral) =>
        nowCentral.TimeOfDay >= TimeSpan.FromHours(s.SettleHours);

    /// <summary>
    /// The judged days, oldest and newest: <c>today − WindowDays … today − 1</c> once
    /// yesterday has settled, else one day earlier (<c>… today − 2</c>), same length.
    /// </summary>
    public static (DateOnly Start, DateOnly End) Window(
        WatchdogDataVolumeSettings s, DateOnly todayCentral, bool yesterdaySettled)
    {
        var end = todayCentral.AddDays(yesterdaySettled ? -1 : -2);
        return (end.AddDays(1 - s.WindowDays), end);
    }

    /// <summary>
    /// The inclusive Central date range both reads must cover for this pass: the oldest
    /// sample of the oldest window day through the window's last day. ~129 days at the
    /// defaults. This overload treats yesterday as settled.
    /// </summary>
    public static (DateOnly FromCentral, DateOnly ToCentralInclusive) QueryRange(
        WatchdogDataVolumeSettings s, DateOnly todayCentral) =>
        QueryRange(s, todayCentral, yesterdaySettled: true);

    /// <summary>The read range for a pass at <paramref name="nowCentral"/> (US Central wall clock).</summary>
    public static (DateOnly FromCentral, DateOnly ToCentralInclusive) QueryRange(
        WatchdogDataVolumeSettings s, DateTime nowCentral) =>
        QueryRange(s, DateOnly.FromDateTime(nowCentral), YesterdaySettled(s, nowCentral));

    private static (DateOnly FromCentral, DateOnly ToCentralInclusive) QueryRange(
        WatchdogDataVolumeSettings s, DateOnly todayCentral, bool yesterdaySettled)
    {
        var (start, end) = Window(s, todayCentral, yesterdaySettled);
        return (start.AddDays(-7 * s.MaxBaselineLookbackWeeks), end);
    }

    /// <summary>
    /// The loaders the volume check applies to: <c>Loaders</c> entries with
    /// <c>Enabled</c> and <c>Volume.Enabled != false</c>, in config spelling.
    /// </summary>
    public static IReadOnlyList<string> MonitoredLoaders(WatchdogSettings settings) =>
        (settings.Loaders ?? new Dictionary<string, WatchdogLoaderSettings>())
            .Where(kv => kv.Value is { Enabled: true } && kv.Value.Volume?.Enabled != false)
            .Select(kv => kv.Key)
            .ToList();

    /// <summary>One loader's effective thresholds after its overrides, or the reason they are invalid.</summary>
    public sealed record EffectiveVolumeSettings(
        int LowPercent, double HighMultiple, int MinMedianRows, DateOnly? BaselineFromCentral);

    /// <summary>
    /// Applies <paramref name="overrides"/> to the platform values and validates the result.
    /// Returns null and sets <paramref name="reason"/> when invalid.
    /// </summary>
    public static EffectiveVolumeSettings? Effective(
        WatchdogDataVolumeSettings platform, WatchdogLoaderVolumeSettings? overrides, out string? reason)
    {
        var low = overrides?.LowPercent ?? platform.LowPercent;
        var high = overrides?.HighMultiple ?? platform.HighMultiple;
        var minMedian = overrides?.MinMedianRows ?? platform.MinMedianRows;

        var problems = new List<string>();
        if (low is < 1 or > 99) problems.Add($"LowPercent={low} must be in [1, 99]");
        if (!(high > 1)) problems.Add($"HighMultiple={high.ToString(CultureInfo.InvariantCulture)} must be > 1");
        if (minMedian < 0) problems.Add($"MinMedianRows={minMedian} must be >= 0");

        DateOnly? baseline = null;
        if (!string.IsNullOrWhiteSpace(overrides?.BaselineFromCentral))
        {
            if (DateOnly.TryParseExact(overrides.BaselineFromCentral.Trim(), "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                baseline = parsed;
            else
                // The value itself is config, not a secret, but keep it short in a log line.
                problems.Add($"BaselineFromCentral='{Truncate(overrides.BaselineFromCentral, 40)}' is not yyyy-MM-dd");
        }

        if (problems.Count > 0)
        {
            reason = string.Join("; ", problems);
            return null;
        }

        reason = null;
        return new EffectiveVolumeSettings(low, high, minMedian, baseline);
    }

    /// <summary>
    /// Evaluates one pass at <paramref name="nowCentral"/> (US CENTRAL wall clock — the
    /// date picks "today", the time of day decides whether yesterday has settled; see
    /// <see cref="WatchdogDataVolumeSettings.SettleHours"/>). This is the overload the
    /// runner uses.
    /// </summary>
    public static WatchdogVolumeResult Evaluate(
        IReadOnlyList<DailyVolume> volumes,
        IReadOnlyCollection<VolumeLoaderDay> excludedDays,
        WatchdogSettings settings,
        DateTime nowCentral) =>
        Evaluate(volumes, excludedDays, settings, DateOnly.FromDateTime(nowCentral),
            YesterdaySettled(settings.DataVolume, nowCentral));

    /// <summary>
    /// Evaluates one pass with yesterday treated as settled — for callers that have no
    /// time of day (it behaves like a pass after <see cref="WatchdogDataVolumeSettings.SettleHours"/>).
    /// </summary>
    public static WatchdogVolumeResult Evaluate(
        IReadOnlyList<DailyVolume> volumes,
        IReadOnlyCollection<VolumeLoaderDay> excludedDays,
        WatchdogSettings settings,
        DateOnly todayCentral) =>
        Evaluate(volumes, excludedDays, settings, todayCentral, yesterdaySettled: true);

    /// <summary>
    /// Evaluates one pass. <paramref name="settings"/>.DataVolume must already be valid
    /// (<see cref="ValidateSection"/>); the caller checks that before running the reads.
    /// <paramref name="excludedDays"/> loader names are matched case-insensitively.
    /// </summary>
    public static WatchdogVolumeResult Evaluate(
        IReadOnlyList<DailyVolume> volumes,
        IReadOnlyCollection<VolumeLoaderDay> excludedDays,
        WatchdogSettings settings,
        DateOnly todayCentral,
        bool yesterdaySettled)
    {
        var platform = settings.DataVolume;
        var (windowStart, windowEnd) = Window(platform, todayCentral, yesterdaySettled);

        // Per loader, per day. Duplicates (two spellings of one name, should SQL ever be
        // case-sensitive) are merged rather than one silently winning.
        var byLoader = volumes
            .Where(v => !string.IsNullOrWhiteSpace(v.LoaderName))
            .GroupBy(v => v.LoaderName.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.GroupBy(v => v.DayCentral).ToDictionary(d => d.Key, d => Merge(d.ToList())),
                StringComparer.OrdinalIgnoreCase);

        var excluded = new HashSet<(string, DateOnly)>(
            excludedDays.Select(e => (e.LoaderName.ToUpperInvariant(), e.DayCentral)));

        var anomalies = new List<VolumeAnomaly>();
        var judged = new List<VolumeLoaderDay>();
        var unjudged = new List<UnjudgedVolumeDay>();
        var skipped = new List<SkippedVolumeLoader>();
        var baselines = new Dictionary<string, DateOnly>(StringComparer.OrdinalIgnoreCase);

        foreach (var loader in MonitoredLoaders(settings))
        {
            var eff = Effective(platform, settings.Loaders[loader].Volume, out var reason);
            if (eff is null)
            {
                skipped.Add(new SkippedVolumeLoader(loader, reason!));
                continue;
            }
            if (eff.BaselineFromCentral is { } bf) baselines[loader] = bf;

            byLoader.TryGetValue(loader, out var days);
            var upper = loader.ToUpperInvariant();

            for (var day = windowStart; day <= windowEnd; day = day.AddDays(1))
            {
                if (days is null || !days.TryGetValue(day, out var today))
                {
                    unjudged.Add(new UnjudgedVolumeDay(loader, day, "no successful run that day"));
                    continue;
                }

                var (samples, excludedSamples) = CollectSamples(
                    day, days, d => excluded.Contains((upper, d)), eff.BaselineFromCentral,
                    platform.BaselineWeeks, platform.MaxBaselineLookbackWeeks);

                if (samples.Count < platform.MinSamples)
                {
                    unjudged.Add(new UnjudgedVolumeDay(loader, day,
                        $"{samples.Count} baseline sample(s), need {platform.MinSamples}"));
                    continue;
                }

                var median = Median(samples.Select(s => s.Rows).ToList());

                // ⚠ median = 0 is unjudged even when MinMedianRows is 0: every rule divides or
                // multiplies by it, so "anything above 0 is High" would be the only outcome.
                if (median <= 0)
                {
                    unjudged.Add(new UnjudgedVolumeDay(loader, day, "median is 0"));
                    continue;
                }

                if (median < eff.MinMedianRows)
                {
                    unjudged.Add(new UnjudgedVolumeDay(loader, day,
                        $"median {median.ToString("0.#", CultureInfo.InvariantCulture)} below MinMedianRows {eff.MinMedianRows}"));
                    continue;
                }

                VolumeDirection? direction =
                    today.Rows == 0 ? VolumeDirection.Zero
                    : today.Rows < median * eff.LowPercent / 100.0 ? VolumeDirection.Low
                    : today.Rows > median * eff.HighMultiple ? VolumeDirection.High
                    : null;

                if (direction is null)
                {
                    judged.Add(new VolumeLoaderDay(loader, day));
                    continue;
                }

                anomalies.Add(new VolumeAnomaly(
                    loader, day, direction.Value, today.Rows, today.SuccessfulRuns, today.TotalRuns,
                    median, today.Rows / median, eff.LowPercent, eff.HighMultiple,
                    samples, excludedSamples, eff.BaselineFromCentral, today.LoaderRunIds));
            }
        }

        return new WatchdogVolumeResult(windowStart, windowEnd, anomalies, judged, unjudged, skipped, baselines);
    }

    /// <summary>
    /// Walks back over same-weekday days from <paramref name="day"/> − 7, collecting the
    /// most recent <paramref name="baselineWeeks"/> qualifying samples (newest first).
    /// Excluded days met on the way are returned too (they go into the finding's
    /// <c>excludedSampleDays</c>); missing days are simply stepped over.
    /// </summary>
    internal static (IReadOnlyList<VolumeSample> Samples, IReadOnlyList<DateOnly> Excluded) CollectSamples(
        DateOnly day,
        IReadOnlyDictionary<DateOnly, DailyVolume> days,
        Func<DateOnly, bool> isExcluded,
        DateOnly? baselineFromCentral,
        int baselineWeeks,
        int maxLookbackWeeks)
    {
        var samples = new List<VolumeSample>();
        var excluded = new List<DateOnly>();

        for (var week = 1; week <= maxLookbackWeeks && samples.Count < baselineWeeks; week++)
        {
            var candidate = day.AddDays(-7 * week);

            // Walking backwards, so everything from here on is before the declared level change.
            if (baselineFromCentral is { } bf && candidate < bf) break;

            if (isExcluded(candidate))
            {
                excluded.Add(candidate);
                continue;
            }

            if (days.TryGetValue(candidate, out var v))
                samples.Add(new VolumeSample(candidate, v.Rows));
        }

        return (samples, excluded);
    }

    /// <summary>True median: the middle value, or the mean of the middle two.</summary>
    public static double Median(IReadOnlyList<long> values)
    {
        if (values.Count == 0) return 0;
        var sorted = values.OrderBy(v => v).ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + (double)sorted[mid]) / 2.0;
    }

    private static DailyVolume Merge(IReadOnlyList<DailyVolume> parts) =>
        parts.Count == 1
            ? parts[0]
            : new DailyVolume(
                parts[0].LoaderName,
                parts[0].DayCentral,
                parts.Sum(p => p.Rows),
                parts.Sum(p => p.SuccessfulRuns),
                parts.Sum(p => p.TotalRuns),
                parts.SelectMany(p => p.LoaderRunIds).Distinct().OrderBy(i => i).ToList());

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
