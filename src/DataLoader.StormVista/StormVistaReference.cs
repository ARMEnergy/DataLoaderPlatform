using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.StormVista;

/// <summary>
/// The seeded <c>dbo.*</c> reference data, loaded once per run. Source of truth
/// for enumeration (§5) and for validating the wide regional header (§8.2).
/// </summary>
public sealed class StormVistaReference
{
    public required IReadOnlyList<ModelRef> Models { get; init; }
    public required IReadOnlyList<CycleRef> Cycles { get; init; }
    public required IReadOnlyList<string> WddTypes { get; init; }
    public required IReadOnlyList<RegionSetRef> RegionSets { get; init; }

    /// <summary>RegionSetCode → the WDD types applicable to it (the <c>dbo.RegionSetWddType</c> bridge).</summary>
    public required IReadOnlyDictionary<string, IReadOnlyList<string>> RegionSetTypes { get; init; }

    /// <summary>RegionSetCode → the ordered region names (the wide-CSV column order).</summary>
    public required IReadOnlyDictionary<string, IReadOnlyList<string>> Regions { get; init; }

    public IEnumerable<ModelRef> DailyModels => Models.Where(m => m.SupportsDaily);
    public IEnumerable<ModelRef> RegionalModels => Models.Where(m => m.SupportsRegional);
    public IEnumerable<string> DailyCycles => Cycles.Where(c => c.SupportsDaily).Select(c => c.CycleCode);
    public IEnumerable<string> RegionalCycles => Cycles.Where(c => c.SupportsRegional).Select(c => c.CycleCode);

    /// <summary>Ordered region names for a set, or empty if the set is unknown.</summary>
    public IReadOnlyList<string> RegionsFor(string regionSetCode) =>
        Regions.TryGetValue(regionSetCode, out var r) ? r : Array.Empty<string>();
}

/// <summary>Loads and caches the seeded <c>dbo.*</c> reference tables for a run.</summary>
public interface IStormVistaReferenceProvider
{
    Task<StormVistaReference> GetAsync(CancellationToken cancellationToken);
}

/// <summary>
/// SQL implementation of <see cref="IStormVistaReferenceProvider"/>. Registered as
/// a singleton and loads once per process (= per run), then serves the cache.
/// Reads all six reference tables via the single read proc <c>dbo.usp_GetReference</c>
/// (six result sets in a fixed order, consumed positionally). Fails fast if a
/// required reference table is empty — a missing seed is a deployment error, not a
/// silent no-op (§5).
/// </summary>
public sealed class SqlStormVistaReferenceProvider : IStormVistaReferenceProvider
{
    private readonly StormVistaSettings _settings;
    private readonly ILogger<SqlStormVistaReferenceProvider> _logger;
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private StormVistaReference? _cache;

    public SqlStormVistaReferenceProvider(IOptions<StormVistaSettings> settings, ILogger<SqlStormVistaReferenceProvider> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<StormVistaReference> GetAsync(CancellationToken cancellationToken)
    {
        if (_cache is not null) return _cache;

        await _loadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cache is not null) return _cache;
            _cache = await LoadAsync(cancellationToken).ConfigureAwait(false);
            return _cache;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private async Task<StormVistaReference> LoadAsync(CancellationToken ct)
    {
        await using var conn = new SqlConnection(_settings.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        // One read proc returns the six reference result sets in a fixed order.
        // Consume them positionally, advancing with NextResult between each.
        await using var cmd = new SqlCommand("dbo.usp_GetReference", conn) { CommandType = CommandType.StoredProcedure };
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        // 1) Models: ModelSlug, DisplayName (nullable), SupportsDaily, SupportsRegional, IsExperimental.
        var models = new List<ModelRef>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
            models.Add(new ModelRef(r.GetString(0), r.GetBoolean(2), r.GetBoolean(3), r.GetBoolean(4)));

        // 2) Cycles: CycleCode, SupportsDaily, SupportsRegional.
        await r.NextResultAsync(ct).ConfigureAwait(false);
        var cycles = new List<CycleRef>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
            cycles.Add(new CycleRef(r.GetString(0).Trim(), r.GetBoolean(1), r.GetBoolean(2)));

        // 3) WDD types: TypeSlug (Weighting/Metric unused here).
        await r.NextResultAsync(ct).ConfigureAwait(false);
        var wddTypes = new List<string>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
            wddTypes.Add(r.GetString(0));

        // 4) Region sets: RegionSetCode, Kind (Description unused here).
        await r.NextResultAsync(ct).ConfigureAwait(false);
        var regionSets = new List<RegionSetRef>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
            regionSets.Add(new RegionSetRef(r.GetString(0), r.GetString(1)));

        // 5) Region-set → WDD-type bridge: RegionSetCode, TypeSlug.
        await r.NextResultAsync(ct).ConfigureAwait(false);
        var bridgeMap = new Dictionary<string, List<string>>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var code = r.GetString(0);
            if (!bridgeMap.TryGetValue(code, out var types)) { types = new List<string>(); bridgeMap[code] = types; }
            types.Add(r.GetString(1));
        }

        // 6) Regions (Ordinal order preserves the wide-CSV column order): RegionSetCode, RegionName, Ordinal.
        await r.NextResultAsync(ct).ConfigureAwait(false);
        var regionMap = new Dictionary<string, List<string>>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var code = r.GetString(0);
            if (!regionMap.TryGetValue(code, out var names)) { names = new List<string>(); regionMap[code] = names; }
            names.Add(r.GetString(1));
        }

        var bridge = bridgeMap.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value);
        var regions = regionMap.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value);

        RequireNonEmpty("dbo.Model", models.Count);
        RequireNonEmpty("dbo.Cycle", cycles.Count);
        RequireNonEmpty("dbo.WddType", wddTypes.Count);
        RequireNonEmpty("dbo.RegionSet", regionSets.Count);
        RequireNonEmpty("dbo.RegionSetWddType", bridge.Count);
        RequireNonEmpty("dbo.Region", regions.Count);

        _logger.LogInformation(
            "StormVista reference loaded: {Models} models ({Daily} daily / {Regional} regional), {Cycles} cycles, {Types} types, {Sets} region-sets, {Regions} region rows",
            models.Count, models.Count(m => m.SupportsDaily), models.Count(m => m.SupportsRegional),
            cycles.Count, wddTypes.Count, regionSets.Count, regions.Values.Sum(r => r.Count));

        return new StormVistaReference
        {
            Models = models,
            Cycles = cycles,
            WddTypes = wddTypes,
            RegionSets = regionSets,
            RegionSetTypes = bridge,
            Regions = regions
        };
    }

    private static void RequireNonEmpty(string table, int count)
    {
        if (count == 0)
            throw new InvalidOperationException(
                $"StormVista reference table {table} is empty — run the dbo reference seeds (sql/StormVista/001) before loading.");
    }
}
