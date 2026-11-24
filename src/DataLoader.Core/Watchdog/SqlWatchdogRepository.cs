using DataLoader.Core.Configuration;
using DataLoader.Core.Resilience;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace DataLoader.Core.Watchdog;

/// <summary>
/// Reads <c>core.LoaderRun_Summary</c> and <c>core.LoaderRun</c> — and, for the
/// <c>DATA_VOLUME</c> exclusion set only, the watchdog's own <c>core.WatchdogFinding</c>.
///
/// <para>⚠ Inline SELECTs rather than stored procedures, unlike the rest of
/// this platform. The READ side adds no SQL objects, because
/// <c>core.LoaderRun_Summary</c> already carries exactly the per-loader outcomes
/// the watchdog needs. Everything here is read-only and parameterised; the
/// watchdog's one write lives in <see cref="SqlWatchdogFindingSink"/>.</para>
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
        await conn.OpenWithRetryAsync(cancellationToken).ConfigureAwait(false);
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
        //
        // The loader name: core.LoaderRun has none, so it comes from the run's
        // core.LoaderRun_Summary row — which production's usp_BeginLoadLog inserts when the
        // run's FIRST unit begins, not at completion, so a killed run has it. That table is
        // keyed on LoaderRunId alone, so it holds ONE name per run (the first loader to begin
        // a unit); production runs one loader per invocation, so that is the loader.
        // Fallback, only when there is no summary name: the run's first core.LoadLog row.
        // The `s.LoaderRunId IS NULL` inside the APPLY is load-bearing for cost, not logic —
        // an unconditional LoadLog lookup made this query 3.4 s instead of ~60 ms on
        // production (2026-11-23, 5 open runs). A run killed before its first unit has
        // neither and comes back with a NULL name.
        const string Sql = @"
SELECT r.RunId, r.StartedAtUtc, r.HostName, COALESCE(s.LoaderName, l.LoaderId) AS LoaderName
FROM core.LoaderRun r
LEFT JOIN core.LoaderRun_Summary s
       ON s.LoaderRunId = r.LoaderRunId AND LTRIM(RTRIM(s.LoaderName)) <> ''
OUTER APPLY (
    SELECT TOP (1) ll.LoaderId
    FROM core.LoadLog ll
    WHERE ll.RunId = r.RunId AND s.LoaderRunId IS NULL
    ORDER BY ll.LoadLogId
) l
WHERE r.CompletedAtUtc IS NULL
  AND r.StartedAtUtc <  DATEADD(hour, -@StuckAfterHours, SYSUTCDATETIME())
  AND r.StartedAtUtc >= DATEADD(day,  -@LookbackDays,    SYSUTCDATETIME())
  AND (@HostName IS NULL OR r.HostName = @HostName)
ORDER BY r.StartedAtUtc, r.RunId;";

        await using var conn = new SqlConnection(_settings.LoadLogConnectionString);
        await conn.OpenWithRetryAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = Command(conn, Sql);
        cmd.Parameters.AddWithValue("@StuckAfterHours", Math.Max(1, stuckAfterHours));
        cmd.Parameters.AddWithValue("@LookbackDays", Math.Max(1, lookbackDays));
        cmd.Parameters.AddWithValue("@HostName", (object?)hostName ?? DBNull.Value);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var rows = new List<StuckRunRow>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new StuckRunRow(
                reader.GetGuid(0),
                reader.GetDateTime(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        }
        return GroupStuckRuns(rows);
    }

    internal readonly record struct StuckRunRow(Guid RunId, DateTime StartedAtUtc, string? HostName, string? LoaderName);

    /// <summary>
    /// Folds the stuck-run query's rows into one <see cref="StuckRun"/> per run, names
    /// de-duplicated case-insensitively, sorted and joined with ", " (null when the run has
    /// none), keeping the query's run order. Production's summary key makes the query return
    /// one row per run today; this stays so a schema where it does not (a summary keyed per
    /// loader, as the repo's own DDL would allow) cannot report one run twice.
    /// </summary>
    internal static IReadOnlyList<StuckRun> GroupStuckRuns(IEnumerable<StuckRunRow> rows) =>
        rows.GroupBy(r => r.RunId)
            .Select(g =>
            {
                var first = g.First();
                var names = g.Select(r => r.LoaderName)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Select(n => n!.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                return new StuckRun(first.RunId, first.StartedAtUtc, first.HostName,
                    names.Count == 0 ? null : string.Join(", ", names));
            })
            .ToList();

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
                .Select(c => new RecentFailure(c.RunId, c.LoaderName, c.StartTimeCentral, c.UnitsFailed, c.UnitsTotal, c.UnitsFailed))
                .ToList();
            return new RecentFailuresResult(asIs, Array.Empty<RecentFailure>());
        }

        var failedUnits = await GetFailedWorkUnitsAsync(candidates, cancellationToken).ConfigureAwait(false);
        var laterSuccesses = await GetLaterSuccessesAsync(failedUnits, cancellationToken).ConfigureAwait(false);

        // The actual matching rule — (LoaderId, WorkUnitDisplay), never WorkUnitKey — lives
        // in WatchdogFailureResolver, not here, so it is unit-testable without a database.
        return WatchdogFailureResolver.Resolve(candidates, failedUnits, laterSuccesses);
    }

    public async Task<RecentFailuresResult> ResolveFailuresByRunIdAsync(
        IReadOnlyCollection<Guid> runIds, CancellationToken cancellationToken)
    {
        if (runIds.Count == 0)
            return new RecentFailuresResult(Array.Empty<RecentFailure>(), Array.Empty<RecentFailure>());

        // Same shape as GetFailedRunCandidatesAsync, keyed on RunId instead of the
        // look-back window. Only the handful of currently-open FAILED findings ever
        // reach here, so one IN list is fine.
        var ids = runIds.Distinct().ToList();
        var paramNames = ids.Select((_, i) => $"@r{i}").ToArray();
        var sql = @"
SELECT lr.RunId, frs.LoaderName, frs.StartTime, ISNULL(frs.UnitsFail, 0), ISNULL(frs.UnitsTotal, 0)
FROM core.LoaderRun_Summary frs
JOIN core.LoaderRun lr ON lr.LoaderRunId = frs.LoaderRunId
WHERE frs.Success = 0
  AND frs.LoaderName IS NOT NULL AND LTRIM(RTRIM(frs.LoaderName)) <> ''
  AND lr.RunId IN (" + string.Join(",", paramNames) + ");";

        var candidates = new List<FailedRunCandidate>();
        await using (var conn = new SqlConnection(_settings.LoadLogConnectionString))
        {
            await conn.OpenWithRetryAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = Command(conn, sql);
            for (var i = 0; i < ids.Count; i++)
                cmd.Parameters.AddWithValue(paramNames[i], ids[i]);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                candidates.Add(new FailedRunCandidate(
                    reader.GetGuid(0), reader.GetString(1), reader.GetDateTime(2), reader.GetInt32(3), reader.GetInt32(4)));
            }
        }

        if (candidates.Count == 0)
            return new RecentFailuresResult(Array.Empty<RecentFailure>(), Array.Empty<RecentFailure>());

        var failedUnits = await GetFailedWorkUnitsAsync(candidates, cancellationToken).ConfigureAwait(false);
        var laterSuccesses = await GetLaterSuccessesAsync(failedUnits, cancellationToken).ConfigureAwait(false);
        return WatchdogFailureResolver.Resolve(candidates, failedUnits, laterSuccesses);
    }

    public async Task<IReadOnlyList<DailyVolume>> GetDailyVolumesAsync(
        DateOnly fromCentral, DateOnly toCentralInclusive, CancellationToken cancellationToken)
    {
        // One row per RUN, aggregated per (loader, day) in AggregateDailyVolumes rather than
        // with a SQL GROUP BY, because the finding lists the day's LoaderRunIds and this must
        // not depend on STRING_AGG being available. The volume is small: production holds
        // ~4,000 summary rows in total since 2026-07, and the span here is ~129 days.
        //
        // StartTime is CENTRAL (see the class comment), so the date parameters are Central
        // too. Half-open range on the raw column so the predicate stays sargable.
        // ⚠ ISNULL + BIGINT: TotalRecords is INT NULL, and one busy loader-day (ICE wrote
        // ~16.6M rows/day before 2026-11-11) summed with reruns can approach INT's ceiling.
        const string Sql = @"
SELECT LoaderRunId,
       LoaderName,
       CAST(StartTime AS date)                  AS DayCentral,
       CAST(ISNULL(TotalRecords, 0) AS BIGINT)  AS TotalRecords,
       CASE WHEN Success = 1 THEN 1 ELSE 0 END  AS Succeeded
FROM core.LoaderRun_Summary
WHERE LoaderName IS NOT NULL AND LTRIM(RTRIM(LoaderName)) <> ''
  AND StartTime >= @FromCentral
  AND StartTime <  DATEADD(day, 1, @ToCentral);";

        await using var conn = new SqlConnection(_settings.LoadLogConnectionString);
        await conn.OpenWithRetryAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = Command(conn, Sql);
        cmd.Parameters.Add("@FromCentral", System.Data.SqlDbType.Date).Value = fromCentral.ToDateTime(TimeOnly.MinValue);
        cmd.Parameters.Add("@ToCentral", System.Data.SqlDbType.Date).Value = toCentralInclusive.ToDateTime(TimeOnly.MinValue);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var runs = new List<VolumeRunRow>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            runs.Add(new VolumeRunRow(
                // Convert rather than GetInt32: the summary's LoaderRunId type is production's,
                // and the repo DDL has drifted from it (see CLAUDE.md).
                Convert.ToInt32(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture),
                reader.GetString(1),
                DateOnly.FromDateTime(reader.GetDateTime(2)),
                reader.GetInt64(3),
                reader.GetInt32(4) == 1));
        }

        return AggregateDailyVolumes(runs);
    }

    /// <summary>One <c>core.LoaderRun_Summary</c> row, as <see cref="GetDailyVolumesAsync"/> reads it.</summary>
    internal readonly record struct VolumeRunRow(int LoaderRunId, string LoaderName, DateOnly DayCentral, long Rows, bool Succeeded);

    /// <summary>
    /// Sums rows over EVERY run of a (loader, day) — failed ones included — and keeps only
    /// days with at least one successful run (design §A1; see <see cref="DailyVolume"/>).
    /// Loader names group case-insensitively, keeping the first spelling met.
    /// </summary>
    internal static IReadOnlyList<DailyVolume> AggregateDailyVolumes(IEnumerable<VolumeRunRow> runs) =>
        runs
            .Where(r => !string.IsNullOrWhiteSpace(r.LoaderName))
            .GroupBy(r => (Name: r.LoaderName.Trim().ToUpperInvariant(), r.DayCentral))
            .Where(g => g.Any(r => r.Succeeded))
            .Select(g => new DailyVolume(
                g.First().LoaderName.Trim(),
                g.Key.DayCentral,
                g.Sum(r => r.Rows),
                g.Count(r => r.Succeeded),
                g.Count(),
                g.Select(r => r.LoaderRunId).Distinct().OrderBy(i => i).ToList()))
            .OrderBy(v => v.LoaderName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(v => v.DayCentral)
            .ToList();

    public async Task<IReadOnlyList<VolumeLoaderDay>> GetVolumeExcludedDaysAsync(
        DateOnly fromCentral, DateOnly toCentralInclusive, CancellationToken cancellationToken)
    {
        // Open, or closed with anything but NotDetected (design §A5): AgedOutUnresolved,
        // LoaderDisabled and ConfigRemoved were never confirmed normal either. Only a rerun
        // that brought the day back into range makes it a sample again.
        //
        // The LastDetectedAtUtc bound is safe: a finding for day D can only be detected
        // after D has ended in Central, which is later still in UTC, so it never drops a
        // finding whose day is in range. Retention keeps resolved rows 180 days, longer than
        // the ~129-day span at the defaults.
        const string Sql = @"
SELECT FindingKey
FROM core.WatchdogFinding
WHERE Signal = 'DATA_VOLUME'
  AND (ResolvedAtUtc IS NULL OR ResolutionReason <> 'NotDetected')
  AND LastDetectedAtUtc >= @FromCentral;";

        await using var conn = new SqlConnection(_settings.LoadLogConnectionString);
        await conn.OpenWithRetryAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = Command(conn, Sql);
        cmd.Parameters.Add("@FromCentral", System.Data.SqlDbType.DateTime2).Value = fromCentral.ToDateTime(TimeOnly.MinValue);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var keys = new List<string>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            keys.Add(reader.GetString(0));

        return keys
            .Select(ParseVolumeFindingKey)
            .Where(k => k is { } d && d.DayCentral >= fromCentral && d.DayCentral <= toCentralInclusive)
            .Select(k => k!.Value)
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// <c>DATA_VOLUME|&lt;Loader&gt;|&lt;yyyy-MM-dd&gt;</c> → (loader, day), or null when the key
    /// is not one. The day is the LAST <c>|</c> segment and the loader everything between
    /// the first and the last, so the parse never depends on what a loader is called.
    /// </summary>
    public static VolumeLoaderDay? ParseVolumeFindingKey(string? key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        var first = key.IndexOf('|');
        var last = key.LastIndexOf('|');
        if (first < 0 || last <= first + 1) return null;
        if (!string.Equals(key[..first], WatchdogSignals.DataVolume, StringComparison.OrdinalIgnoreCase)) return null;

        var loader = key[(first + 1)..last];
        if (string.IsNullOrWhiteSpace(loader)) return null;
        if (!DateOnly.TryParseExact(key[(last + 1)..], "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var day))
            return null;
        return new VolumeLoaderDay(loader, day);
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
        await conn.OpenWithRetryAsync(cancellationToken).ConfigureAwait(false);
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
        await conn.OpenWithRetryAsync(cancellationToken).ConfigureAwait(false);

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
        await conn.OpenWithRetryAsync(cancellationToken).ConfigureAwait(false);

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
