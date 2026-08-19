using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.IHSPointLogic;

// =============================================================================
// The 5 reference providers (design §3) — the discovery hand-off Tier 0 → 1 → 2.
// Each is the AGSI IAgsiCountryProvider pattern: registered SINGLETON (= one
// instance per process = per run), loads once behind a double-checked
// SemaphoreSlim, then serves the cache — so a whole tier reads each list exactly
// once across all parallel work units. Fail fast (throw) if empty. Ids flow THROUGH
// the database: each tier MERGEs its table, the next tier's provider reads it back
// via an arm.usp_Get<X>Ids read proc (a plain read, no SqlWriteGate).
// =============================================================================

public interface IPlRegionProvider { Task<IReadOnlyList<int>> GetRegionIdsAsync(CancellationToken cancellationToken); }
public interface IPlStateProvider { Task<IReadOnlyList<int>> GetStateIdsAsync(CancellationToken cancellationToken); }
public interface IPlPointTypeProvider { Task<IReadOnlyList<int>> GetPointTypeIdsAsync(CancellationToken cancellationToken); }
public interface IPlPointProvider { Task<IReadOnlyList<int>> GetPointIdsAsync(CancellationToken cancellationToken); }

/// <summary>
/// Sub-region reference provider — surfaces BOTH the <c>SubRegionId</c> list (to enumerate work
/// units) AND the <c>SubRegionId → RegionId</c> map (SD-by-subregion needs the parent RegionId for
/// its PK, and the response body does not carry it) from ONE cached read of <c>arm.Subregion</c>.
/// </summary>
public interface IPlSubregionProvider
{
    Task<IReadOnlyList<int>> GetSubRegionIdsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<int, int>> GetSubRegionToRegionMapAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Shared load-once/fail-fast base for the four single-column id caches (design §3). The
/// consuming pipeline's first call happens — per the §1.4 tier barriers — AFTER the feeding tier
/// committed its MERGE, so the read sees the freshly refreshed list within the same run.
/// </summary>
public abstract class SqlPlIntIdProvider
{
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private IReadOnlyList<int>? _cache;

    protected readonly IHSPointLogicSettings Settings;
    protected readonly ILogger Logger;

    protected SqlPlIntIdProvider(IHSPointLogicSettings settings, ILogger logger)
    {
        Settings = settings;
        Logger = logger;
    }

    /// <summary>The <c>arm.usp_Get&lt;X&gt;Ids</c> read proc returning one INT column, ordered ascending.</summary>
    protected abstract string ReadProc { get; }

    /// <summary>Human label for the fail-fast message.</summary>
    protected abstract string EntityLabel { get; }

    protected async Task<IReadOnlyList<int>> GetOrLoadAsync(CancellationToken ct)
    {
        if (_cache is not null) return _cache;

        await _loadLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cache is not null) return _cache;
            _cache = await LoadAsync(ct).ConfigureAwait(false);
            return _cache;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private async Task<IReadOnlyList<int>> LoadAsync(CancellationToken ct)
    {
        await using var conn = new SqlConnection(Settings.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = new SqlCommand(ReadProc, conn) { CommandType = CommandType.StoredProcedure };
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        var ids = new List<int>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (!reader.IsDBNull(0)) ids.Add(reader.GetInt32(0));
        }

        if (ids.Count == 0)
            throw new InvalidOperationException(
                $"IHSPointLogic {EntityLabel} reference is empty (via {ReadProc}) — run the feeding tier first, or check the discovery load.");

        Logger.LogInformation("IHSPointLogic {Entity} reference loaded: {Count} id(s)", EntityLabel, ids.Count);
        return ids;
    }
}

/// <summary>RegionId list (feeds Subregion T1 + SD-by-region T2). Reads <c>arm.usp_GetRegionIds</c>.</summary>
public sealed class SqlPlRegionProvider : SqlPlIntIdProvider, IPlRegionProvider
{
    public SqlPlRegionProvider(IOptions<IHSPointLogicSettings> settings, ILogger<SqlPlRegionProvider> logger)
        : base(settings.Value, logger) { }

    protected override string ReadProc => "arm.usp_GetRegionIds";
    protected override string EntityLabel => "Region";

    public Task<IReadOnlyList<int>> GetRegionIdsAsync(CancellationToken cancellationToken) => GetOrLoadAsync(cancellationToken);
}

/// <summary>StateId list (feeds County T1). Reads <c>arm.usp_GetStateIds</c>.</summary>
public sealed class SqlPlStateProvider : SqlPlIntIdProvider, IPlStateProvider
{
    public SqlPlStateProvider(IOptions<IHSPointLogicSettings> settings, ILogger<SqlPlStateProvider> logger)
        : base(settings.Value, logger) { }

    protected override string ReadProc => "arm.usp_GetStateIds";
    protected override string EntityLabel => "State";

    public Task<IReadOnlyList<int>> GetStateIdsAsync(CancellationToken cancellationToken) => GetOrLoadAsync(cancellationToken);
}

/// <summary>PointTypeId list (feeds Facility T1). Reads <c>arm.usp_GetPointTypeIds</c>.</summary>
public sealed class SqlPlPointTypeProvider : SqlPlIntIdProvider, IPlPointTypeProvider
{
    public SqlPlPointTypeProvider(IOptions<IHSPointLogicSettings> settings, ILogger<SqlPlPointTypeProvider> logger)
        : base(settings.Value, logger) { }

    protected override string ReadProc => "arm.usp_GetPointTypeIds";
    protected override string EntityLabel => "PointType";

    public Task<IReadOnlyList<int>> GetPointTypeIdsAsync(CancellationToken cancellationToken) => GetOrLoadAsync(cancellationToken);
}

/// <summary>Active PointId list (feeds PointVolume T2 batching). Reads <c>arm.usp_GetPointIds</c>.</summary>
public sealed class SqlPlPointProvider : SqlPlIntIdProvider, IPlPointProvider
{
    public SqlPlPointProvider(IOptions<IHSPointLogicSettings> settings, ILogger<SqlPlPointProvider> logger)
        : base(settings.Value, logger) { }

    protected override string ReadProc => "arm.usp_GetPointIds";
    protected override string EntityLabel => "Point";

    public Task<IReadOnlyList<int>> GetPointIdsAsync(CancellationToken cancellationToken) => GetOrLoadAsync(cancellationToken);
}

/// <summary>
/// Sub-region reference cache (design §3) — loads the DISTINCT <c>(SubRegionId, RegionId)</c> pairs
/// from <c>arm.usp_GetSubRegionIds</c> ONCE behind a double-checked <see cref="SemaphoreSlim"/> and
/// serves BOTH the id list and the SubRegionId→RegionId map from that single read. Fail fast if empty.
/// </summary>
public sealed class SqlPlSubregionProvider : IPlSubregionProvider
{
    private const string ReadProc = "arm.usp_GetSubRegionIds";

    private readonly IHSPointLogicSettings _settings;
    private readonly ILogger<SqlPlSubregionProvider> _logger;
    private readonly SemaphoreSlim _loadLock = new(1, 1);

    private IReadOnlyList<int>? _ids;
    private IReadOnlyDictionary<int, int>? _map;

    public SqlPlSubregionProvider(IOptions<IHSPointLogicSettings> settings, ILogger<SqlPlSubregionProvider> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<int>> GetSubRegionIdsAsync(CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        return _ids!;
    }

    public async Task<IReadOnlyDictionary<int, int>> GetSubRegionToRegionMapAsync(CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        return _map!;
    }

    private async Task EnsureLoadedAsync(CancellationToken ct)
    {
        if (_ids is not null) return;

        await _loadLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_ids is not null) return;
            await LoadAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        await using var conn = new SqlConnection(_settings.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = new SqlCommand(ReadProc, conn) { CommandType = CommandType.StoredProcedure };
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        var ids = new List<int>();
        var map = new Dictionary<int, int>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (reader.IsDBNull(0) || reader.IsDBNull(1)) continue;
            var sub = reader.GetInt32(0);
            var region = reader.GetInt32(1);
            if (map.TryAdd(sub, region)) ids.Add(sub); // one region per sub-region (arm.Subregion PK guarantees it)
        }

        if (ids.Count == 0)
            throw new InvalidOperationException(
                $"IHSPointLogic SubRegion reference is empty (via {ReadProc}) — run Tier 1 (Subregion) first, or check the discovery load.");

        _ids = ids;
        _map = map;
        _logger.LogInformation("IHSPointLogic SubRegion reference loaded: {Count} sub-region/region pair(s)", ids.Count);
    }
}
