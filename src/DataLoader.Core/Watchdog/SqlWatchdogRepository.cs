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

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var conn = new SqlConnection(_settings.LoadLogConnectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        return conn;
    }

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

        await using var conn = await OpenAsync(cancellationToken).ConfigureAwait(false);
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

        await using var conn = await OpenAsync(cancellationToken).ConfigureAwait(false);
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

    public async Task<IReadOnlyList<RecentFailure>> GetRecentFailuresAsync(
        int lookbackHours, DateTime nowCentral, CancellationToken cancellationToken)
    {
        const string Sql = @"
SELECT LoaderName, StartTime, ISNULL(UnitsFail, 0), ISNULL(UnitsTotal, 0)
FROM core.LoaderRun_Summary
WHERE Success = 0
  AND LoaderName IS NOT NULL AND LTRIM(RTRIM(LoaderName)) <> ''
  AND StartTime >= DATEADD(hour, -@LookbackHours, @NowCentral)
ORDER BY StartTime DESC;";

        await using var conn = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = Command(conn, Sql);
        cmd.Parameters.AddWithValue("@LookbackHours", Math.Max(1, lookbackHours));
        cmd.Parameters.AddWithValue("@NowCentral", nowCentral);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var rows = new List<RecentFailure>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new RecentFailure(
                reader.GetString(0), reader.GetDateTime(1), reader.GetInt32(2), reader.GetInt32(3)));
        }
        return rows;
    }
}
