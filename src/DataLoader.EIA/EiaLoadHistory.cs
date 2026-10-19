using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace DataLoader.EIA;

/// <summary>
/// Which of this loader's work units have EVER completed successfully.
///
/// <para>
/// Read once at the head of a run and used for exactly one decision: whether a HOT
/// window may have its read narrowed to the revisable tail
/// (<see cref="EiaSettings.NarrowHotWindowReads"/>). Narrowing a window that has never
/// been read in full would skip its older half permanently — the window is hot, so it
/// gets a fresh key every day and would be narrowed again tomorrow, and the day after,
/// until it finally settles with that half never loaded. So the narrowing is allowed
/// only where the full read is a matter of record.
/// </para>
/// <para>
/// ⚠ This reads <c>core.LoadLog</c> in the PLATFORM database, not <c>EIAv2</c> — hence
/// its own connection string, taken from <c>Platform:LoadLogConnectionString</c>. It is
/// strictly read-only. The alternative was adding a method to
/// <see cref="Core.Abstractions.ILoadLogRepository"/>, which would mean a new procedure
/// in the shared, deployed <c>sql/Core</c> scripts for the benefit of one loader.
/// </para>
/// <para>
/// One query returns every completed key for this loader (~7,000 strings today), which
/// is cheaper and far less chatty than a per-unit existence check across ~190 hot units.
/// </para>
/// </summary>
internal sealed class EiaLoadHistory
{
    private readonly string? _connectionString;
    private readonly ILogger<EiaLoadHistory> _logger;

    public EiaLoadHistory(string? loadLogConnectionString, ILogger<EiaLoadHistory> logger)
    {
        _connectionString = loadLogConnectionString;
        _logger = logger;
    }

    /// <summary>
    /// Every <c>WorkUnitKey</c> this loader has completed successfully.
    ///
    /// <para>
    /// Returns an EMPTY set if the load log cannot be read for any reason. That is the
    /// fail-safe direction: an empty history means nothing is eligible for narrowing, so
    /// every hot window is read in full — slower, never lossy.
    /// </para>
    /// </summary>
    public async Task<IReadOnlySet<string>> GetCompletedKeysAsync(CancellationToken ct)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            _logger.LogWarning(
                "EIA: no Platform:LoadLogConnectionString, so no load history is available — " +
                "every hot window will be read in full");
            return keys;
        }

        try
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);

            await using var cmd = new SqlCommand(
                "SELECT DISTINCT WorkUnitKey FROM core.LoadLog WHERE LoaderId = @LoaderId AND IsComplete = 1",
                conn) { CommandTimeout = 120 };
            cmd.Parameters.Add("@LoaderId", System.Data.SqlDbType.VarChar, 100).Value = EiaModule.Id;

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                keys.Add(reader.GetString(0));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "EIA: could not read the load history ({Message}). Every hot window will be read in " +
                "full this run — slower, but nothing is skipped", ex.Message);
            return new HashSet<string>(StringComparer.Ordinal);
        }

        return keys;
    }

    /// <summary>
    /// True when <paramref name="baseKey"/> — a hot window's key WITHOUT its
    /// <c>;run=…</c> suffix — has completed under some previous run token.
    ///
    /// <para>
    /// Matching on the prefix <c>"&lt;baseKey&gt;;run="</c> rather than on the bare base
    /// key, because a hot window's recorded keys always carry a token. The separator is
    /// included so <c>…;from=2024;to=2048</c> cannot be satisfied by a longer, unrelated
    /// key that merely starts with the same text.
    /// </para>
    /// </summary>
    public static bool HasEverCompleted(IReadOnlySet<string> completedKeys, string baseKey)
    {
        var prefix = baseKey + ";run=";

        foreach (var key in completedKeys)
            if (key.StartsWith(prefix, StringComparison.Ordinal))
                return true;

        return false;
    }
}
