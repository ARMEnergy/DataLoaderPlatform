using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.IHSPointLogic;

// =============================================================================
// Per-endpoint hourly run schedule (design §B.1/§B.2). Shaped like the 5 reference
// providers: registered SINGLETON (= load-once per run), reads the schedule ONCE
// behind a double-checked SemaphoreSlim and serves the cache thereafter. Unlike the
// id caches it does NOT fail-fast on empty — arm.Endpoint is deploy-seeded and always
// present, and a missing per-endpoint row simply falls through to every-hour.
// =============================================================================

/// <summary>
/// Decides whether an enabled endpoint should run on a given US Central hour, per its
/// <c>arm.Endpoint.RunHoursCST</c> schedule (design §B.2). <c>EnabledEndpoints</c> remains the hard
/// on/off master; this is the cadence gate among the enabled endpoints.
/// </summary>
public interface IPlEndpointSchedule
{
    Task<bool> ShouldRunAsync(string endpointId, DateTime runStartedUtc, CancellationToken cancellationToken);
}

/// <summary>
/// US Central time zone (CST in winter, CDT in summer). The schedule hours in
/// <c>arm.Endpoint.RunHoursCST</c> are Central, so the gate converts the UTC run-start to Central
/// before comparing the hour (design §B.1/§B.2). The hot resume key stays UTC — only the gate
/// converts. Resolved the same way the CWG loader resolves US Eastern
/// (<c>CwgWorkUnitProvider.ResolveEastern</c>): try the Windows id then the IANA id, cache once.
/// </summary>
internal static class PlCentralTimeZone
{
    internal static readonly TimeZoneInfo Central = Resolve();

    private static TimeZoneInfo Resolve()
    {
        foreach (var id in new[] { "Central Standard Time", "America/Chicago" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        return TimeZoneInfo.Utc; // last-resort fallback; keeps the gate working on an exotic host
    }
}

/// <summary>
/// Load-once schedule cache (design §B.2). Reads <c>arm.usp_GetEndpointSchedule</c> once, parses each
/// <c>RunHoursCST</c> (design §B.1 tolerance) into a per-endpoint <see cref="HourSet"/> and serves a
/// <c>Dictionary&lt;string, HourSet&gt;</c> keyed by EndpointId (case-insensitive). Fail-OPEN to
/// every-hour (with a WARN) on blank / all-invalid / all-out-of-range values, and on a missing row.
/// </summary>
public sealed class SqlPlEndpointSchedule : IPlEndpointSchedule
{
    private const string ReadProc = "arm.usp_GetEndpointSchedule";

    private readonly IHSPointLogicSettings _settings;
    private readonly ILogger<SqlPlEndpointSchedule> _logger;
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private IReadOnlyDictionary<string, HourSet>? _cache;

    public SqlPlEndpointSchedule(IOptions<IHSPointLogicSettings> settings, ILogger<SqlPlEndpointSchedule> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<bool> ShouldRunAsync(string endpointId, DateTime runStartedUtc, CancellationToken cancellationToken)
    {
        var schedule = await GetOrLoadAsync(cancellationToken).ConfigureAwait(false);

        // A missing per-endpoint row → every hour (fail-open, design §B.1/§B.2).
        if (!schedule.TryGetValue(endpointId, out var hours))
            return true;

        // RunHoursCST is US Central (DST-aware): convert the UTC run-start to Central before the
        // hour lookup (design §B.2). The hot resume key deliberately stays UTC — only the gate converts.
        var centralTime = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(runStartedUtc, DateTimeKind.Utc), PlCentralTimeZone.Central);
        return hours.Contains(centralTime.Hour);
    }

    private async Task<IReadOnlyDictionary<string, HourSet>> GetOrLoadAsync(CancellationToken ct)
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

    private async Task<IReadOnlyDictionary<string, HourSet>> LoadAsync(CancellationToken ct)
    {
        var map = new Dictionary<string, HourSet>(StringComparer.OrdinalIgnoreCase);

        await using var conn = new SqlConnection(_settings.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = new SqlCommand(ReadProc, conn) { CommandType = CommandType.StoredProcedure };
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (reader.IsDBNull(0)) continue;
            var name = reader.GetString(0);
            var raw = reader.IsDBNull(1) ? null : reader.GetString(1);
            map[name] = ParseHours(name, raw);
        }

        _logger.LogInformation("IHSPointLogic endpoint schedule loaded: {Count} endpoint row(s)", map.Count);
        return map;
    }

    /// <summary>
    /// Parses a <c>RunHoursCST</c> value into an <see cref="HourSet"/> (design §B.1 tolerance):
    /// trim/space-tolerant CSV of US Central hours 0–23; <c>'*'</c> = every hour; blank / all-invalid /
    /// all-out-of-range → fail-OPEN to every-hour with a WARN (a typo must never silently disable an
    /// endpoint). A partly-invalid list keeps its valid hours and WARNs.
    /// </summary>
    internal HourSet ParseHours(string endpointId, string? raw)
    {
        var value = raw?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            _logger.LogWarning("IHSPointLogic endpoint '{Endpoint}' has a blank RunHoursCST — running every hour", endpointId);
            return HourSet.EveryHour;
        }

        if (value == "*")
            return HourSet.EveryHour;

        var hours = new HashSet<int>();
        var anyInvalid = false;
        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hour) && hour is >= 0 and <= 23)
                hours.Add(hour);
            else
                anyInvalid = true;
        }

        if (hours.Count == 0)
        {
            _logger.LogWarning(
                "IHSPointLogic endpoint '{Endpoint}' RunHoursCST '{Value}' has no valid Central hours (0–23) — running every hour", endpointId, value);
            return HourSet.EveryHour;
        }

        if (anyInvalid)
            _logger.LogWarning(
                "IHSPointLogic endpoint '{Endpoint}' RunHoursCST '{Value}' has invalid/out-of-range entries — using the {Count} valid hour(s)", endpointId, value, hours.Count);

        return HourSet.Of(hours);
    }
}

/// <summary>
/// A parsed per-endpoint US-Central-hour schedule (design §B.2): either <b>every-hour</b> (the <c>'*'</c> /
/// fail-open marker) or a specific set of hours 0–23. <c>default(HourSet)</c> is every-hour.
/// </summary>
public readonly struct HourSet
{
    private readonly HashSet<int>? _hours; // null => every hour

    private HourSet(HashSet<int>? hours) => _hours = hours;

    /// <summary>Runs on every Central hour (the <c>'*'</c> / blank / invalid fallback marker).</summary>
    public static HourSet EveryHour => new((HashSet<int>?)null);

    /// <summary>Runs only on the given US Central hours (0–23).</summary>
    public static HourSet Of(HashSet<int> hours) => new(hours);

    /// <summary><c>true</c> when this schedule runs on every hour.</summary>
    public bool IsEveryHour => _hours is null;

    /// <summary><c>true</c> when the given Central hour is scheduled (always <c>true</c> for every-hour).</summary>
    public bool Contains(int hour) => _hours is null || _hours.Contains(hour);
}
