using DataLoader.Core.Configuration;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace DataLoader.Core.Watchdog;

/// <summary>
/// Reads <c>core.LoaderRun_Summary</c> and <c>core.LoaderRun</c>.
///
/// <para>⚠ Inline SELECTs rather than stored procedures, unlike the rest of
/// this platform. The design commits to adding NO SQL objects to production,
/// because <c>core.LoaderRun_Summary</c> already carries exactly the per-loader
/// outcomes the watchdog needs. Everything here is read-only and parameterised.</para>
///
/// <para>⚠ The two tables disagree about time zones.
/// <c>LoaderRun_Summary.StartTime</c> is already CENTRAL (production's
/// <c>usp_CompleteLoaderRun</c> converts before storing);
/// <c>LoaderRun.StartedAtUtc</c> is UTC. Each query below compares against the
/// matching clock, which is why the stuck-run window is applied in SQL with
/// <c>SYSUTCDATETIME()</c> while the failure look-back takes a Central
/// parameter from the caller.</para>
/// </summary>
public sealed class SqlWatchdogRepository : IWatchdogRepository
{
    private readonly PlatformSettings _settings;

    public SqlWatchdogRepository(IOptions<PlatformSettings> settings) => _settings = settings.Value;

    // Each method binds `new SqlConnection(...)` to its own `await using`
    // BEFORE awaiting OpenAsync, matching SqlLoadLogRepository.CreateConnection.
    // An earlier shape here constructed-and-opened inside a helper that
    // returned the (opened) connection; if Open threw, the helper never
    // returned, so the caller's `await using var conn = await Helper(...)`
    // never bound and the connection leaked. The database being briefly
    // unreachable is exactly the moment the watchdog runs and exactly what it
    // exists to notice, so this failure mode matters more here than elsewhere.

    private SqlCommand Command(SqlConnection conn, string sql)
    {
        var cmd = new SqlCommand(sql, conn);
        if (_settings.LoadLogCommandTimeoutSeconds > 0)
            cmd.CommandTimeout = _settings.LoadLogCommandTimeoutSeconds;
        return cmd;
    }

    public async Task<IReadOnlyList<LoaderLastRun>> GetLastRunsAsync(CancellationToken cancellationToken)
    {
        // The blank-LoaderName row is real: a run that produced no core.LoadLog
        // rows leaves the summary's LoaderName NULL, because that column comes
        // from a LEFT JOIN onto LoadLog.
        const string Sql = @"
SELECT LoaderName, MAX(StartTime) AS LastStart
FROM core.LoaderRun_Summary
WHERE LoaderName IS NOT NULL AND LTRIM(RTRIM(LoaderName)) <> ''
  AND StartTime IS NOT NULL
GROUP BY LoaderName;";

        await using var conn = new SqlConnection(_settings.LoadLogConnectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = Command(conn, Sql);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var rows = new List<LoaderLastRun>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            rows.Add(new LoaderLastRun(reader.GetString(0), reader.GetDateTime(1)));
        return rows;
    }

    public async Task<IReadOnlyList<StuckRun>> GetStuckRunsAsync(
        int stuckAfterHours, int lookbackDays, string? hostName, CancellationToken cancellationToken)
    {
        // Bounded two ways: the look-back keeps ancient orphans (nothing sweeps
        // core.LoaderRun — see WatchdogSettings.StuckLookbackDays) from being
        // re-reported on every pass forever, and the optional host filter keeps
        // a dev box pointed at this database from showing up as a production
        // incident (see WatchdogSettings.RestrictStuckRunsToThisHost).
        const string Sql = @"
SELECT RunId, StartedAtUtc, HostName
FROM core.LoaderRun
WHERE CompletedAtUtc IS NULL
  AND StartedAtUtc <  DATEADD(hour, -@StuckAfterHours, SYSUTCDATETIME())
  AND StartedAtUtc >= DATEADD(day,  -@LookbackDays,    SYSUTCDATETIME())
  AND (@HostName IS NULL OR HostName = @HostName)
ORDER BY StartedAtUtc;";

        await using var conn = new SqlConnection(_settings.LoadLogConnectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = Command(conn, Sql);
        cmd.Parameters.AddWithValue("@StuckAfterHours", Math.Max(1, stuckAfterHours));
        cmd.Parameters.AddWithValue("@LookbackDays", Math.Max(1, lookbackDays));
        cmd.Parameters.AddWithValue("@HostName", (object?)hostName ?? DBNull.Value);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var rows = new List<StuckRun>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new StuckRun(
                reader.GetGuid(0),
                reader.GetDateTime(1),
                reader.IsDBNull(2) ? null : reader.GetString(2)));
        }
        return rows;
    }

    public async Task<RecentFailuresResult> GetRecentFailuresAsync(
        int lookbackHours, DateTime nowCentral, bool skipAlertIfResolved, CancellationToken cancellationToken)
    {
        var candidates = await GetFailedRunCandidatesAsync(lookbackHours, nowCentral, cancellationToken)
            .ConfigureAwait(false);

        if (!skipAlertIfResolved || candidates.Count == 0)
        {
            // Disabled (or nothing to resolve either way): report every candidate exactly
            // as the pre-resolution query did. UnresolvedUnits mirrors UnitsFailed because
            // nothing was evaluated — "unresolved" here means "not checked", never
            // "confirmed still broken".
            var asIs = candidates
                .Select(c => new RecentFailure(c.LoaderName, c.StartTimeCentral, c.UnitsFailed, c.UnitsTotal, c.UnitsFailed))
                .ToList();
            return new RecentFailuresResult(asIs, Array.Empty<RecentFailure>());
        }

        var failedUnits = await GetFailedWorkUnitsAsync(candidates, cancellationToken).ConfigureAwait(false);
        var laterSuccesses = await GetLaterSuccessesAsync(failedUnits, cancellationToken).ConfigureAwait(false);

        // The actual matching rule — (LoaderId, WorkUnitDisplay), never WorkUnitKey — lives
        // in WatchdogFailureResolver, not here, so it is unit-testable without a database.
        return WatchdogFailureResolver.Resolve(candidates, failedUnits, laterSuccesses);
    }

    /// <summary>
    /// ⚠ KNOWN LIMITATION: excluding a NULL LoaderName is correct for THIS
    /// cadence query (RecentFailure.LoaderName is required, and the name
    /// comes from a LEFT JOIN onto core.LoadLog — see GetLastRunsAsync above)
    /// but it means a run that failed BEFORE writing a single LoadLog row is
    /// invisible to this safety net. Production has 3 such rows. Not a real
    /// blind spot overall: PlatformHost's in-process notifier (the primary
    /// channel; this query is the backstop for when that email could not be
    /// delivered) attributes that same case correctly via LoaderId, which
    /// this proc's LEFT JOIN has no way to recover after the fact.
    /// </summary>
    private async Task<IReadOnlyList<FailedRunCandidate>> GetFailedRunCandidatesAsync(
        int lookbackHours, DateTime nowCentral, CancellationToken cancellationToken)
    {
        // Joins to core.LoaderRun for RunId (GUID) rather than returning LoaderRunId (INT)
        // directly: core.LoadLog carries BOTH columns, but only RunId is indexed
        // (IX_LoadLog_RunId) — see GetFailedWorkUnitsAsync, which depends on this.
        const string Sql = @"
SELECT lr.RunId, frs.LoaderName, frs.StartTime, ISNULL(frs.UnitsFail, 0), ISNULL(frs.UnitsTotal, 0)
FROM core.LoaderRun_Summary frs
JOIN core.LoaderRun lr ON lr.LoaderRunId = frs.LoaderRunId
WHERE frs.Success = 0
  AND frs.LoaderName IS NOT NULL AND LTRIM(RTRIM(frs.LoaderName)) <> ''
  AND frs.StartTime >= DATEADD(hour, -@LookbackHours, @NowCentral)
ORDER BY frs.StartTime DESC;";

        await using var conn = new SqlConnection(_settings.LoadLogConnectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = Command(conn, Sql);
        cmd.Parameters.AddWithValue("@LookbackHours", Math.Max(1, lookbackHours));
        cmd.Parameters.AddWithValue("@NowCentral", nowCentral);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var rows = new List<FailedRunCandidate>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new FailedRunCandidate(
                reader.GetGuid(0), reader.GetString(1), reader.GetDateTime(2), reader.GetInt32(3), reader.GetInt32(4)));
        }
        return rows;
    }

    /// <summary>
    /// The failed (<c>IsComplete = 0</c>) <c>core.LoadLog</c> rows for <paramref name="candidates"/>.
    ///
    /// <para>Filters on <c>RunId</c> (GUID), NOT <c>LoaderRunId</c> (INT), even though both
    /// columns exist on <c>core.LoadLog</c> and either would be logically correct.
    /// Measured directly against production (4.19M rows): filtering by the unindexed
    /// <c>LoaderRunId</c> costs ~950ms (a full scan); filtering by the indexed
    /// <c>RunId</c> via <c>IX_LoadLog_RunId</c> costs ~70-250ms. No new index is added for
    /// this — <see cref="GetFailedRunCandidatesAsync"/> already has to join to
    /// <c>core.LoaderRun</c> for <c>RunId</c>, so this reuses an index that already
    /// exists rather than requesting one.</para>
    ///
    /// <para>Built as one <c>RunId IN (...)</c> query with one parameter per candidate
    /// rather than N round trips, because the candidate count here is the same small set
    /// <see cref="GetFailedRunCandidatesAsync"/> already bounded by the look-back window.</para>
    /// </summary>
    private async Task<IReadOnlyList<FailedWorkUnit>> GetFailedWorkUnitsAsync(
        IReadOnlyList<FailedRunCandidate> candidates, CancellationToken cancellationToken)
    {
        await using var conn = new SqlConnection(_settings.LoadLogConnectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        var paramNames = new string[candidates.Count];
        for (var i = 0; i < candidates.Count; i++)
            paramNames[i] = $"@r{i}";

        var sql =
            "SELECT RunId, LoaderId, WorkUnitKey, WorkUnitDisplay, StartedAtUtc " +
            "FROM core.LoadLog " +
            $"WHERE IsComplete = 0 AND RunId IN ({string.Join(",", paramNames)});";

        await using var cmd = Command(conn, sql);
        for (var i = 0; i < candidates.Count; i++)
            cmd.Parameters.AddWithValue(paramNames[i], candidates[i].RunId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var rows = new List<FailedWorkUnit>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new FailedWorkUnit(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                // Never observed NULL platform-wide (verified over the last 200,000
                // core.LoadLog rows), but a NULL here must still never MATCH a later
                // success rather than throw — see WatchdogFailureResolver.
                reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                reader.GetDateTime(4)));
        }
        return rows;
    }

    /// <summary>
    /// The successful (<c>IsComplete = 1</c>) <c>core.LoadLog</c> rows that could resolve
    /// <paramref name="failedUnits"/> — scoped to the loaders involved and bounded to
    /// <c>StartedAtUtc</c> at or after the earliest failure, so this stays proportional to
    /// the look-back window rather than scanning the table's full history.
    /// </summary>
    private async Task<IReadOnlyList<SuccessfulWorkUnit>> GetLaterSuccessesAsync(
        IReadOnlyList<FailedWorkUnit> failedUnits, CancellationToken cancellationToken)
    {
        if (failedUnits.Count == 0) return Array.Empty<SuccessfulWorkUnit>();

        var loaderIds = failedUnits.Select(f => f.LoaderId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var earliestFailure = failedUnits.Min(f => f.StartedAtUtc);

        await using var conn = new SqlConnection(_settings.LoadLogConnectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        var paramNames = new string[loaderIds.Count];
        for (var i = 0; i < loaderIds.Count; i++)
            paramNames[i] = $"@l{i}";

        var sql =
            "SELECT LoaderId, WorkUnitKey, WorkUnitDisplay, StartedAtUtc " +
            "FROM core.LoadLog " +
            $"WHERE IsComplete = 1 AND StartedAtUtc > @EarliestFailure AND LoaderId IN ({string.Join(",", paramNames)});";

        await using var cmd = Command(conn, sql);
        cmd.Parameters.AddWithValue("@EarliestFailure", earliestFailure);
        for (var i = 0; i < loaderIds.Count; i++)
            cmd.Parameters.AddWithValue(paramNames[i], loaderIds[i]);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var rows = new List<SuccessfulWorkUnit>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new SuccessfulWorkUnit(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                reader.GetDateTime(3)));
        }
        return rows;
    }
}
