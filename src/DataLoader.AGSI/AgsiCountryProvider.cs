using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.AGSI;

/// <summary>One entity-dimension row the storage pipeline enumerates: its surrogate <c>Id</c> and its <c>Code</c>.</summary>
public readonly record struct AgsiCountry(int Id, string Code);

/// <summary>
/// The reference hand-off from pipeline 1 → pipeline 2 (design §2): the AGSI analog
/// of StormVista's <c>IStormVistaReferenceProvider</c>. The entity dimension flows
/// THROUGH the database — the entities pipeline MERGEs it into
/// <c>arm.GasStorageEntity</c>, then the storage provider reads the distinct
/// <c>(Id, Code)</c> pairs back out (once per run) and crosses them with the date
/// window. <c>Id</c> is the FK stamped onto each <c>arm.GasStorage</c> fact row;
/// <c>Code</c> drives the endpoint-2 query (lowercased) and the resume key.
/// </summary>
public interface IAgsiCountryProvider
{
    /// <summary>Distinct <c>(Id, Code)</c> entity pairs from <c>arm.GasStorageEntity</c>, cached for the run. Throws if empty.</summary>
    Task<IReadOnlyList<AgsiCountry>> GetCountriesAsync(CancellationToken cancellationToken);
}

/// <summary>
/// SQL implementation of <see cref="IAgsiCountryProvider"/>. Registered as a
/// singleton and loads once per process (= per run) behind a double-checked
/// <see cref="SemaphoreSlim"/> (the exact shape of
/// <c>SqlStormVistaReferenceProvider.GetAsync</c>), then serves the cache — so the
/// whole storage run reads the list exactly once across all parallel work units.
/// Per the §1.4 ordering the first call happens AFTER the entities pipeline has
/// committed its MERGE, so it sees the freshly refreshed list.
///
/// <para>
/// <b>Fail fast if empty.</b> A zero-row result throws — a storage-only first run
/// with no prior entities load is a configuration error, surfaced loudly rather
/// than silently loading nothing (identical posture to StormVista's
/// <c>RequireNonEmpty</c>).
/// </para>
/// </summary>
public sealed class SqlAgsiCountryProvider : IAgsiCountryProvider
{
    private readonly AgsiSettings _settings;
    private readonly ILogger<SqlAgsiCountryProvider> _logger;
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private IReadOnlyList<AgsiCountry>? _cache;

    public SqlAgsiCountryProvider(IOptions<AgsiSettings> settings, ILogger<SqlAgsiCountryProvider> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<AgsiCountry>> GetCountriesAsync(CancellationToken cancellationToken)
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

    private async Task<IReadOnlyList<AgsiCountry>> LoadAsync(CancellationToken ct)
    {
        // Keeps all DB access behind a stored procedure (platform convention): the read proc
        // arm.usp_GetGasStorageEntities returns two columns (Id INT, Code VARCHAR(16)) — the
        // DISTINCT non-blank entities ordered by Code (design §2 / sql/AGSI/003).
        await using var conn = new SqlConnection(_settings.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = new SqlCommand("arm.usp_GetGasStorageEntities", conn) { CommandType = CommandType.StoredProcedure };
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        var idOrd = reader.GetOrdinal("Id");
        var codeOrd = reader.GetOrdinal("Code");

        var countries = new List<AgsiCountry>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (reader.IsDBNull(idOrd) || reader.IsDBNull(codeOrd)) continue;
            var code = reader.GetString(codeOrd).Trim();
            if (code.Length > 0) countries.Add(new AgsiCountry(reader.GetInt32(idOrd), code));
        }

        if (countries.Count == 0)
            throw new InvalidOperationException(
                "arm.GasStorageEntity is empty — run the About pipeline first, or check the /api/about load.");

        _logger.LogInformation("AGSI country reference loaded: {Count} distinct entity/code pair(s)", countries.Count);
        return countries;
    }
}
