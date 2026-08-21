using System.Globalization;
using DataLoader.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace DataLoader.IHSPointLogic;

/// <summary>
/// One IHSPointLogic request = one work unit, shared by all 25 endpoints (design §4).
/// Carries the fully-substituted <see cref="RequestPath"/> (relative, no host, no
/// <c>pageIndex</c>), the injected/stamped fields the row factory copies
/// (<see cref="ParamId"/>, <see cref="SecondaryId"/>, <see cref="ReportDate"/>), the
/// FileLog natural-key slots (<see cref="Variant"/>, <see cref="RepresentativeDate"/>)
/// and the precomputed resume <see cref="KeyValue"/>.
/// </summary>
public sealed class PlWorkUnit : WorkUnit
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public required string EndpointId { get; init; }

    /// <summary>Fully-substituted RELATIVE path incl. <c>{id}</c> + <c>?reportDate</c>/<c>?pointIds</c> — NO host, NO pageIndex.</summary>
    public required string RequestPath { get; init; }

    /// <summary>Injected path param (StateId | PointTypeId | RegionId | SubRegionId); null for A/B/E.</summary>
    public int? ParamId { get; init; }

    /// <summary>SD-by-subregion's parent RegionId (from the map); null otherwise.</summary>
    public int? SecondaryId { get; init; }

    /// <summary>D: the reportDate param; B-stamped: the run's UTC date; null otherwise.</summary>
    public DateOnly? ReportDate { get; init; }

    /// <summary>PointVolume batch label — start-date-stamped <c>{startDate:yyyyMMdd}-{subIndex:D4}</c> (e.g. "20200101-0000"); null otherwise.</summary>
    public string? BatchToken { get; init; }

    /// <summary>FileLog sub-slot (param-kind label / "Batch"); null for A/B.</summary>
    public string? Variant { get; init; }

    /// <summary>FileLog representative date (design §6); null for A / C.</summary>
    public DateOnly? RepresentativeDate { get; init; }

    public required string KeyValue { get; init; }

    public override string Key => KeyValue;

    /// <summary>
    /// FileLog <c>ParamKey</c> slot (design §6): the numeric parent id (C/D) or the batch token (E);
    /// null for the no-param dimensions/snapshots (A/B).
    /// </summary>
    public string? ParamKey => BatchToken ?? ParamId?.ToString(Inv);

    public override string DisplayName => RepresentativeDate.HasValue
        ? $"{EndpointId} {ParamKey ?? "-"} {RepresentativeDate.Value:yyyy-MM-dd}"
        : $"{EndpointId} {ParamKey ?? "latest"}";
}

/// <summary>
/// Centralized hot-zone resume-key token derivation (design §B.4) — shared by all four work-unit
/// providers so they stay in lockstep. <see cref="PlHotKeyStrategy.RunHour"/> (the default) yields
/// hour granularity (<c>yyyyMMddHH</c>) so a scheduled hourly endpoint re-pulls each UTC hour while a
/// same-hour re-run still idempotently skips; <see cref="PlHotKeyStrategy.RunDate"/> keeps a daily
/// (<c>yyyyMMdd</c>) cadence; <see cref="PlHotKeyStrategy.RunId"/> varies on every invocation. The
/// hot token is UTC throughout (the loader's locked stamping basis, design §4).
/// </summary>
internal static class PlResumeKey
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string HotToken(PlHotKeyStrategy strategy, LoaderRunContext context) => strategy switch
    {
        PlHotKeyStrategy.RunDate => DateOnly.FromDateTime(context.StartedAtUtc).ToString("yyyyMMdd", Inv),
        PlHotKeyStrategy.RunId => context.RunId.ToString("N"),
        _ => context.StartedAtUtc.ToString("yyyyMMddHH", Inv), // RunHour (default)
    };
}

/// <summary>
/// Archetypes A &amp; B (design §4): emits ONE hot unit per run. The request path is the
/// descriptor's plain template (no param, no query). B (fact snapshots) key on the run's UTC
/// date and stamp <see cref="PlWorkUnit.RepresentativeDate"/>; the two demand-forecast endpoints
/// additionally stamp <see cref="PlWorkUnit.ReportDate"/> = the run's UTC date. A (dimensions)
/// carry no date. Paged endpoints are still one unit — the reader pages internally.
/// </summary>
public sealed class PlSnapshotWorkUnitProvider : IWorkUnitProvider<PlWorkUnit>
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly PlEndpointDescriptor _d;
    private readonly IHSPointLogicSettings _settings;
    private readonly ILogger _logger;

    public PlSnapshotWorkUnitProvider(PlEndpointDescriptor descriptor, IHSPointLogicSettings settings, ILogger logger)
    {
        _d = descriptor;
        _settings = settings;
        _logger = logger;
    }

    public Task<IReadOnlyList<PlWorkUnit>> GetWorkUnitsAsync(LoaderRunContext context)
    {
        var runDate = DateOnly.FromDateTime(context.StartedAtUtc); // UTC calendar date — the locked basis (design §4)
        var hot = PlResumeKey.HotToken(_settings.HotZoneKeyStrategy, context); // §B.4: RunHour default

        var isFact = _d.Archetype == PlArchetype.GoForwardSnapshot;
        var reportDate = _d.ReportDateBasis == PlReportDateBasis.StampUtcRunDate ? (DateOnly?)runDate : null;

        var unit = new PlWorkUnit
        {
            EndpointId = _d.EndpointId,
            RequestPath = _d.PathTemplate,
            ReportDate = reportDate,
            RepresentativeDate = isFact ? runDate : null,
            KeyValue = isFact
                ? $"pl:{_d.EndpointId}:{runDate.ToString("yyyyMMdd", Inv)}:run={hot}"
                : $"pl:{_d.EndpointId}:run={hot}"
        };

        _logger.LogDebug("[IHSPointLogic {Endpoint}] enumerated 1 snapshot work unit (key={Key})", _d.EndpointId, unit.KeyValue);
        return Task.FromResult<IReadOnlyList<PlWorkUnit>>(new[] { unit });
    }
}

/// <summary>
/// Archetype C (design §4): one hot unit per parent id, given the descriptor's reference-provider
/// id list. The parent id is substituted into <c>{id}</c> and injected onto every row (it is NOT
/// echoed in the body). <see cref="PlWorkUnit.Variant"/> carries the param-kind label ("State" …).
/// </summary>
public sealed class PlDiscoveryLookupWorkUnitProvider : IWorkUnitProvider<PlWorkUnit>
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly PlEndpointDescriptor _d;
    private readonly IHSPointLogicSettings _settings;
    private readonly Func<CancellationToken, Task<IReadOnlyList<int>>> _parentIds;
    private readonly string _variantLabel;
    private readonly ILogger _logger;

    public PlDiscoveryLookupWorkUnitProvider(
        PlEndpointDescriptor descriptor,
        IHSPointLogicSettings settings,
        Func<CancellationToken, Task<IReadOnlyList<int>>> parentIds,
        string variantLabel,
        ILogger logger)
    {
        _d = descriptor;
        _settings = settings;
        _parentIds = parentIds;
        _variantLabel = variantLabel;
        _logger = logger;
    }

    public async Task<IReadOnlyList<PlWorkUnit>> GetWorkUnitsAsync(LoaderRunContext context)
    {
        var ids = await _parentIds(context.CancellationToken).ConfigureAwait(false);

        var hot = PlResumeKey.HotToken(_settings.HotZoneKeyStrategy, context); // §B.4: RunHour default

        var units = new List<PlWorkUnit>(ids.Count);
        foreach (var id in ids)
        {
            units.Add(new PlWorkUnit
            {
                EndpointId = _d.EndpointId,
                RequestPath = _d.PathTemplate.Replace("{id}", id.ToString(Inv)),
                ParamId = id,
                Variant = _variantLabel,
                RepresentativeDate = null,
                KeyValue = $"pl:{_d.EndpointId}:{id}:run={hot}"
            });
        }

        _logger.LogDebug("[IHSPointLogic {Endpoint}] enumerated {Count} discovery unit(s)", _d.EndpointId, units.Count);
        return units;
    }
}

/// <summary>
/// Archetype D (design §4): crosses each parent id × each reportDate in the trailing
/// <see cref="IHSPointLogicSettings.DaysBack"/> window (newest = run's UTC date), emitting one unit
/// per (parent id × reportDate) with the StormVista/AGSI two-zone resume key. Neither the id nor the
/// date is in the body — both are injected. For SD-by-subregion the parent <c>RegionId</c> is
/// resolved from the SubRegionId→RegionId map and stamped as <see cref="PlWorkUnit.SecondaryId"/>.
/// </summary>
public sealed class PlDiscoveryDatedFactWorkUnitProvider : IWorkUnitProvider<PlWorkUnit>
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly PlEndpointDescriptor _d;
    private readonly IHSPointLogicSettings _settings;
    private readonly Func<CancellationToken, Task<IReadOnlyList<int>>> _parentIds;
    private readonly Func<CancellationToken, Task<IReadOnlyDictionary<int, int>>>? _subToRegionMap;
    private readonly string _variantLabel;
    private readonly ILogger _logger;

    public PlDiscoveryDatedFactWorkUnitProvider(
        PlEndpointDescriptor descriptor,
        IHSPointLogicSettings settings,
        Func<CancellationToken, Task<IReadOnlyList<int>>> parentIds,
        Func<CancellationToken, Task<IReadOnlyDictionary<int, int>>>? subToRegionMap,
        string variantLabel,
        ILogger logger)
    {
        _d = descriptor;
        _settings = settings;
        _parentIds = parentIds;
        _subToRegionMap = subToRegionMap;
        _variantLabel = variantLabel;
        _logger = logger;
    }

    public async Task<IReadOnlyList<PlWorkUnit>> GetWorkUnitsAsync(LoaderRunContext context)
    {
        var ids = await _parentIds(context.CancellationToken).ConfigureAwait(false);
        var map = _subToRegionMap is null ? null : await _subToRegionMap(context.CancellationToken).ConfigureAwait(false);

        var runDate = DateOnly.FromDateTime(context.StartedAtUtc);
        var hot = PlResumeKey.HotToken(_settings.HotZoneKeyStrategy, context); // §B.4: RunHour default (settled-zone key unaffected)

        var daysBack = Math.Max(1, _settings.DaysBack);
        var units = new List<PlWorkUnit>(ids.Count * daysBack);

        foreach (var id in ids)
        {
            int? secondary = null;
            if (map is not null)
            {
                if (!map.TryGetValue(id, out var region))
                {
                    // RegionId is a NOT NULL key column for SD-by-subregion — skip an unmapped id loudly.
                    _logger.LogWarning("[IHSPointLogic {Endpoint}] sub-region id {Id} has no parent RegionId in the map; skipping", _d.EndpointId, id);
                    continue;
                }
                secondary = region;
            }

            for (var k = 0; k < daysBack; k++)
            {
                var d = runDate.AddDays(-k);
                var ageDays = runDate.DayNumber - d.DayNumber;
                var dateStr = d.ToString("yyyy-MM-dd", Inv);
                var baseKey = $"pl:{_d.EndpointId}:{id}:{d.ToString("yyyyMMdd", Inv)}";

                units.Add(new PlWorkUnit
                {
                    EndpointId = _d.EndpointId,
                    RequestPath = $"{_d.PathTemplate.Replace("{id}", id.ToString(Inv))}?reportDate={dateStr}",
                    ParamId = id,
                    SecondaryId = secondary,
                    ReportDate = d,
                    Variant = _variantLabel,
                    RepresentativeDate = d,
                    // Two-zone key: settled (age > SettledAfterDays) → stable; hot → run-varying.
                    KeyValue = ageDays > _settings.SettledAfterDays ? baseKey : $"{baseKey}:run={hot}"
                });
            }
        }

        _logger.LogDebug("[IHSPointLogic {Endpoint}] enumerated {Count} dated-fact unit(s) over {Ids} id(s) × {Days} day(s)",
            _d.EndpointId, units.Count, ids.Count, daysBack);
        return units;
    }
}

/// <summary>
/// Archetype E — PointVolume incremental backfill (design Section C). Reads each active point with its
/// watermark (<c>arm.PointMetadata.MaxDateQueued</c>), resolves a per-point start date
/// <c>S = MaxDateQueued ?? DefaultStartDateForPointVolume</c>, GROUPS points by <c>S</c> (collapsing the
/// NULL group and any point already at the default into one group, so byte-identical <c>startDate</c>
/// queries share a group and tokens stay unique), then splits each group into
/// ≤ <see cref="IHSPointLogicSettings.PointVolumeBatchSize"/>-id contiguous sub-chunks. Each slice emits
/// one hot unit with <c>?pointIds={csv}&amp;startDate={S:yyyy-MM-dd}</c>; the batch token is
/// start-date-stamped (<c>{S:yyyyMMdd}-{subIndex:D4}</c>). Each <c>Data[]</c> row carries its own
/// <c>id</c>, so a multi-point batch splits cleanly per row (no unit-side attribution needed).
/// </summary>
public sealed class PlBatchedFactWorkUnitProvider : IWorkUnitProvider<PlWorkUnit>
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly PlEndpointDescriptor _d;
    private readonly IHSPointLogicSettings _settings;
    private readonly IPlPointProvider _pointProvider;
    private readonly ILogger _logger;

    public PlBatchedFactWorkUnitProvider(
        PlEndpointDescriptor descriptor,
        IHSPointLogicSettings settings,
        IPlPointProvider pointProvider,
        ILogger logger)
    {
        _d = descriptor;
        _settings = settings;
        _pointProvider = pointProvider;
        _logger = logger;
    }

    public async Task<IReadOnlyList<PlWorkUnit>> GetWorkUnitsAsync(LoaderRunContext context)
    {
        var points = await _pointProvider.GetPointsAsync(context.CancellationToken).ConfigureAwait(false);

        var runDate = DateOnly.FromDateTime(context.StartedAtUtc);
        var hot = PlResumeKey.HotToken(_settings.HotZoneKeyStrategy, context); // §B.4: RunHour default

        var defaultStart = ParseDefaultStartDate(_settings.DefaultStartDateForPointVolume);
        var batchSize = Math.Max(1, _settings.PointVolumeBatchSize);

        // Group by the RESOLVED start date S (Section C.4-step-3). Sort by PointId FIRST (OrderBy is
        // stable) so within-group order — and therefore batch membership and the ordinal-in-group
        // token/resume key — is deterministic regardless of the row order arm.usp_GetPointIds returns.
        // Order the groups by S for legibility.
        var groups = points
            .OrderBy(p => p.PointId)
            .GroupBy(p => p.MaxDateQueued ?? defaultStart)
            .OrderBy(g => g.Key);

        var units = new List<PlWorkUnit>();
        foreach (var group in groups)
        {
            var start = group.Key;
            var startPath = start.ToString("yyyy-MM-dd", Inv);
            var startStamp = start.ToString("yyyyMMdd", Inv);
            var ids = group.Select(p => p.PointId).ToList();

            var subIndex = 0;
            for (var i = 0; i < ids.Count; i += batchSize)
            {
                var csv = string.Join(",", ids.GetRange(i, Math.Min(batchSize, ids.Count - i)).Select(id => id.ToString(Inv)));
                var token = $"{startStamp}-{subIndex.ToString("D4", Inv)}"; // start-date-stamped + in-group index

                units.Add(new PlWorkUnit
                {
                    EndpointId = _d.EndpointId,
                    RequestPath = $"{_d.PathTemplate}?pointIds={csv}&startDate={startPath}",
                    BatchToken = token,
                    Variant = "Batch",
                    RepresentativeDate = runDate,
                    KeyValue = $"pl:{_d.EndpointId}:{token}:run={hot}"
                });
                subIndex++;
            }
        }

        _logger.LogDebug("[IHSPointLogic {Endpoint}] enumerated {Batches} batch unit(s) over {Points} point(s)",
            _d.EndpointId, units.Count, points.Count);
        return units;
    }

    /// <summary>
    /// Parses <see cref="IHSPointLogicSettings.DefaultStartDateForPointVolume"/> (invariant
    /// <c>yyyy-MM-dd</c>) FAIL-FAST (design Section C.11): a global backfill-floor typo has no safe
    /// default, so surface it loudly rather than silently backfill from an unexpected date.
    /// </summary>
    private static DateOnly ParseDefaultStartDate(string value)
    {
        if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", Inv, DateTimeStyles.None, out var parsed))
            throw new InvalidOperationException(
                $"IHSPointLogic setting 'DefaultStartDateForPointVolume' (\"{value}\") is not a valid yyyy-MM-dd date — " +
                "fix the configuration; the PointVolume backfill floor has no safe default.");
        return parsed;
    }
}
