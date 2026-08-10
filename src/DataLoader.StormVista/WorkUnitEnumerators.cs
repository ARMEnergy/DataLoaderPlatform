using System.Globalization;
using DataLoader.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.StormVista;

/// <summary>
/// Builds the two-zone resume keys (design §4). Settled init dates
/// (<c>ageDays &gt; settledAfterDays</c>) get a stable key → skipped forever once
/// loaded; hot init dates get a run-varying key → always re-pulled.
/// </summary>
internal static class StormVistaKeys
{
    public static string DailyKey(string model, DateOnly initDate, string cycle, string type, int ageDays, int settledAfterDays, string hotSuffix)
    {
        var baseKey = $"sv:daily:{model}:{Ymd(initDate)}:{cycle}:{type}";
        return ageDays > settledAfterDays ? baseKey : $"{baseKey}:run={hotSuffix}";
    }

    public static string RegionalKey(string model, DateOnly initDate, string cycle, string type, string regionSetCode, int ageDays, int settledAfterDays, string hotSuffix)
    {
        var baseKey = $"sv:regional:{model}:{Ymd(initDate)}:{cycle}:{type}:reg{regionSetCode}";
        return ageDays > settledAfterDays ? baseKey : $"{baseKey}:run={hotSuffix}";
    }

    private static string Ymd(DateOnly d) => d.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
}

/// <summary>
/// Trivial provider that returns a precomputed list — the window adapter that lets
/// a standard <see cref="Core.Pipeline.LoaderPipelineBase{TUnit,TItem,TRow}"/> run
/// exactly one window's units (design §6.3).
/// </summary>
internal sealed class StaticWorkUnitProvider<TUnit> : IWorkUnitProvider<TUnit> where TUnit : WorkUnit
{
    private readonly IReadOnlyList<TUnit> _units;
    public StaticWorkUnitProvider(IReadOnlyList<TUnit> units) => _units = units;
    public Task<IReadOnlyList<TUnit>> GetWorkUnitsAsync(LoaderRunContext context) => Task.FromResult(_units);
}

/// <summary>Enumerates one date window's work units from the reference matrix (design §5/§6.3).</summary>
internal interface IStormVistaUnitEnumerator<TUnit> where TUnit : WorkUnit
{
    Task<IReadOnlyList<TUnit>> EnumerateAsync(
        DateOnly windowStart, DateOnly windowEnd, LoaderRunContext context, CancellationToken cancellationToken);
}

/// <summary>Shared enumeration helpers.</summary>
internal static class StormVistaEnumeration
{
    /// <summary>Daily national WDD types (API fact — no <c>pw_hdd</c> for daily; that is ISO-regional only).</summary>
    public static readonly string[] DailyTypes = { "ew_cdd", "gw_hdd", "pw_cdd" };

    /// <summary>Earliest archived init date (design §6.2 / API doc).</summary>
    public static readonly DateOnly EarliestArchive = new(2018, 7, 8);

    public static bool Included(string value, string[]? filter) =>
        filter is null || filter.Length == 0 || filter.Contains(value, StringComparer.OrdinalIgnoreCase);

    public static string HotSuffix(HotZoneKeyStrategy strategy, LoaderRunContext context, DateOnly todayUtc) =>
        strategy == HotZoneKeyStrategy.RunDate
            ? todayUtc.ToString("yyyyMMdd", CultureInfo.InvariantCulture)
            : context.RunId.ToString("N");
}

/// <summary>
/// Single source of truth for the run's init-date range, shared by the feed
/// pipeline and the post-load validator. The loader's own Mode / DaysBack /
/// backfill settings drive it; <c>context.DateFrom/DateTo</c> are intentionally
/// not applied (see the note on <see cref="StormVistaFeedPipeline{TUnit,TRow}"/>).
/// </summary>
internal static class StormVistaDateRange
{
    /// <summary>Resolves the raw (pre-clamp) [start, end] from settings. Returns false if backfill lacks a start.</summary>
    public static bool TryResolveRaw(StormVistaSettings settings, DateOnly todayUtc, out DateOnly start, out DateOnly end)
    {
        if (settings.Mode == StormVistaMode.Backfill)
        {
            if (!settings.BackfillStart.HasValue)
            {
                start = default;
                end = default;
                return false;
            }
            start = DateOnly.FromDateTime(settings.BackfillStart.Value);
            end = settings.BackfillEnd.HasValue ? DateOnly.FromDateTime(settings.BackfillEnd.Value) : todayUtc;
        }
        else
        {
            end = todayUtc;
            start = todayUtc.AddDays(-Math.Max(0, settings.DaysBack));
        }
        return true;
    }

    /// <summary>Clamps a range to the archive floor and to "today" (future init dates 404 anyway).</summary>
    public static (DateOnly Start, DateOnly End) Clamp(DateOnly start, DateOnly end, DateOnly todayUtc)
    {
        if (start < StormVistaEnumeration.EarliestArchive) start = StormVistaEnumeration.EarliestArchive;
        if (end > todayUtc) end = todayUtc;
        return (start, end);
    }
}

/// <summary>Window-scoped daily enumerator: init-date × daily model × daily cycle × daily type (design §5).</summary>
internal sealed class DailyWorkUnitProvider : IStormVistaUnitEnumerator<DailyWorkUnit>
{
    private readonly IStormVistaReferenceProvider _reference;
    private readonly StormVistaSettings _settings;
    private readonly ILogger<DailyWorkUnitProvider> _logger;

    public DailyWorkUnitProvider(
        IStormVistaReferenceProvider reference, IOptions<StormVistaSettings> settings, ILogger<DailyWorkUnitProvider> logger)
    {
        _reference = reference;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<DailyWorkUnit>> EnumerateAsync(
        DateOnly windowStart, DateOnly windowEnd, LoaderRunContext context, CancellationToken cancellationToken)
    {
        var reference = await _reference.GetAsync(cancellationToken).ConfigureAwait(false);
        var todayUtc = DateOnly.FromDateTime(context.StartedAtUtc);
        var hotSuffix = StormVistaEnumeration.HotSuffix(_settings.HotZoneKeyStrategy, context, todayUtc);

        var models = reference.DailyModels
            .Where(m => !_settings.ExcludeExperimental || !m.IsExperimental)
            .Where(m => StormVistaEnumeration.Included(m.ModelSlug, _settings.Models))
            .Select(m => m.ModelSlug).ToList();

        var cycles = reference.DailyCycles
            .Where(c => StormVistaEnumeration.Included(c, _settings.Cycles)).ToList();

        var types = StormVistaEnumeration.DailyTypes
            .Where(t => reference.WddTypes.Contains(t))
            .Where(t => StormVistaEnumeration.Included(t, _settings.Types)).ToList();

        foreach (var missing in StormVistaEnumeration.DailyTypes.Where(t => !reference.WddTypes.Contains(t)))
            _logger.LogWarning("Daily type '{Type}' is not present in dbo.WddType; it will not be enumerated", missing);

        var units = new List<DailyWorkUnit>();
        for (var d = windowStart; d <= windowEnd; d = d.AddDays(1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ageDays = todayUtc.DayNumber - d.DayNumber;

            foreach (var model in models)
            foreach (var cycle in cycles)
            foreach (var type in types)
            {
                units.Add(new DailyWorkUnit
                {
                    Model = model,
                    InitDate = d,
                    Cycle = cycle,
                    WddType = type,
                    KeyValue = StormVistaKeys.DailyKey(model, d, cycle, type, ageDays, _settings.SettledAfterDays, hotSuffix)
                });
            }
        }

        _logger.LogDebug("Daily window {From:yyyy-MM-dd}..{To:yyyy-MM-dd}: {Count} units", windowStart, windowEnd, units.Count);
        return units;
    }
}

/// <summary>
/// Window-scoped regional enumerator: init-date × regional model × model-kind-dependent
/// cycle (daily-type models → 00/06/12/18; weekly-only → 00/12) × region-set × (type from
/// the region-set→type bridge) (design §5).
/// </summary>
internal sealed class RegionalWorkUnitProvider : IStormVistaUnitEnumerator<RegionalWorkUnit>
{
    private readonly IStormVistaReferenceProvider _reference;
    private readonly StormVistaSettings _settings;
    private readonly ILogger<RegionalWorkUnitProvider> _logger;

    public RegionalWorkUnitProvider(
        IStormVistaReferenceProvider reference, IOptions<StormVistaSettings> settings, ILogger<RegionalWorkUnitProvider> logger)
    {
        _reference = reference;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<RegionalWorkUnit>> EnumerateAsync(
        DateOnly windowStart, DateOnly windowEnd, LoaderRunContext context, CancellationToken cancellationToken)
    {
        var reference = await _reference.GetAsync(cancellationToken).ConfigureAwait(false);
        var todayUtc = DateOnly.FromDateTime(context.StartedAtUtc);
        var hotSuffix = StormVistaEnumeration.HotSuffix(_settings.HotZoneKeyStrategy, context, todayUtc);

        var models = reference.RegionalModels
            .Where(m => !_settings.ExcludeExperimental || !m.IsExperimental)
            .Where(m => StormVistaEnumeration.Included(m.ModelSlug, _settings.Models))
            .ToList();

        // Regional cycle applicability is model-kind-dependent (verified live). The
        // daily-type models (SupportsDaily == true) serve regional at ALL FOUR cycles
        // (00/06/12/18 — e.g. gfs-ens-bc reg3 returns data at each), whereas weekly-only
        // regional models serve regional at 00/12 only. Enumerating a weekly model at
        // 06/18 would be a guaranteed 404 (phantom units + NotAvailable audit noise every
        // run), so the cycle set is chosen per model below. Materialize both lists once.
        var dailyCycles = reference.DailyCycles
            .Where(c => StormVistaEnumeration.Included(c, _settings.Cycles)).ToList();
        var regionalCycles = reference.RegionalCycles
            .Where(c => StormVistaEnumeration.Included(c, _settings.Cycles)).ToList();

        var regionSets = reference.RegionSets
            .Select(s => s.RegionSetCode)
            .Where(code => StormVistaEnumeration.Included(code, _settings.RegionSets)).ToList();

        var units = new List<RegionalWorkUnit>();
        for (var d = windowStart; d <= windowEnd; d = d.AddDays(1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ageDays = todayUtc.DayNumber - d.DayNumber;

            foreach (var model in models)
            {
                var modelCycles = model.SupportsDaily ? dailyCycles : regionalCycles;

                foreach (var cycle in modelCycles)
                foreach (var regionSet in regionSets)
                {
                    if (!reference.RegionSetTypes.TryGetValue(regionSet, out var setTypes))
                        continue; // region set with no bridge rows → nothing applicable

                    foreach (var type in setTypes.Where(t => StormVistaEnumeration.Included(t, _settings.Types)))
                    {
                        units.Add(new RegionalWorkUnit
                        {
                            WkModel = model.ModelSlug,
                            InitDate = d,
                            Cycle = cycle,
                            WddType = type,
                            RegionSetCode = regionSet,
                            KeyValue = StormVistaKeys.RegionalKey(model.ModelSlug, d, cycle, type, regionSet, ageDays, _settings.SettledAfterDays, hotSuffix)
                        });
                    }
                }
            }
        }

        _logger.LogDebug("Regional window {From:yyyy-MM-dd}..{To:yyyy-MM-dd}: {Count} units", windowStart, windowEnd, units.Count);
        return units;
    }
}
