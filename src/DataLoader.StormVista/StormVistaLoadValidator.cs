using System.Data;
using DataLoader.Core.Abstractions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.StormVista;

/// <summary>
/// Module-level post-load quality check (design §10). Calls
/// <c>dbo.usp_ValidateLoad(@InitDateFrom, @InitDateTo)</c> for the run's init-date
/// range and logs the returned anomaly report. Purely observational — it never
/// throws out (a normally-sparse day with many legitimate 404s must not fail an
/// otherwise-good load); the deeper reconciliation is DATA_QUALITY_VALIDATOR's job.
/// </summary>
public sealed class StormVistaLoadValidator
{
    private readonly StormVistaSettings _settings;
    private readonly ILogger<StormVistaLoadValidator> _logger;

    public StormVistaLoadValidator(IOptions<StormVistaSettings> settings, ILogger<StormVistaLoadValidator> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task ValidateAsync(LoaderRunContext context, CancellationToken cancellationToken)
    {
        var todayUtc = DateOnly.FromDateTime(context.StartedAtUtc);
        if (!StormVistaDateRange.TryResolveRaw(_settings, todayUtc, out var start, out var end))
        {
            _logger.LogWarning("StormVista validation skipped: backfill range not configured");
            return;
        }
        (start, end) = StormVistaDateRange.Clamp(start, end, todayUtc);
        if (start > end)
        {
            _logger.LogWarning("StormVista validation skipped: empty range {Start:yyyy-MM-dd}..{End:yyyy-MM-dd}", start, end);
            return;
        }

        try
        {
            await using var conn = new SqlConnection(_settings.ConnectionString);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var cmd = new SqlCommand("dbo.usp_ValidateLoad", conn) { CommandType = CommandType.StoredProcedure };
            cmd.Parameters.Add("@InitDateFrom", SqlDbType.Date).Value = start.ToDateTime(TimeOnly.MinValue);
            cmd.Parameters.Add("@InitDateTo", SqlDbType.Date).Value = end.ToDateTime(TimeOnly.MinValue);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            var checkOrd = reader.GetOrdinal("CheckName");
            var feedOrd = reader.GetOrdinal("Feed");
            var scopeOrd = reader.GetOrdinal("Scope");
            var expectedOrd = reader.GetOrdinal("ExpectedCount");
            var actualOrd = reader.GetOrdinal("ActualCount");
            var detailOrd = reader.GetOrdinal("Detail");

            int anomalies = 0, informational = 0;
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var check = reader.IsDBNull(checkOrd) ? "?" : reader.GetString(checkOrd);
                var feed = reader.IsDBNull(feedOrd) ? null : reader.GetString(feedOrd);
                var scope = reader.IsDBNull(scopeOrd) ? null : reader.GetString(scopeOrd);
                int? expected = reader.IsDBNull(expectedOrd) ? null : reader.GetInt32(expectedOrd);
                var actual = reader.IsDBNull(actualOrd) ? 0L : reader.GetInt64(actualOrd);
                var detail = reader.IsDBNull(detailOrd) ? null : reader.GetString(detailOrd);

                // An "expected" value that the actual count fails to match is a real
                // anomaly (FlagDomainViolation/Orphan* expect 0; RegionCoverageMismatch
                // rows are only returned when they differ). The rest are informational.
                if (expected.HasValue && expected.Value != actual)
                {
                    anomalies++;
                    _logger.LogWarning(
                        "StormVista validation anomaly: {Check} feed={Feed} scope={Scope} expected={Expected} actual={Actual} {Detail}",
                        check, feed ?? "-", scope ?? "-", expected, actual, detail ?? string.Empty);
                }
                else
                {
                    informational++;
                    _logger.LogInformation(
                        "StormVista validation: {Check} feed={Feed} scope={Scope} count={Actual} {Detail}",
                        check, feed ?? "-", scope ?? "-", actual, detail ?? string.Empty);
                }
            }

            _logger.LogInformation(
                "StormVista validation complete for {Start:yyyy-MM-dd}..{End:yyyy-MM-dd}: {Anomalies} anomaly row(s), {Info} informational row(s)",
                start, end, anomalies, informational);
        }
        catch (OperationCanceledException)
        {
            // A cancelled run must surface as cancellation, not be masked as a
            // non-fatal validation failure.
            throw;
        }
        catch (Exception ex)
        {
            // Observational only: a validation failure must not fail the load.
            _logger.LogError(ex, "StormVista post-load validation failed to run (non-fatal)");
        }
    }
}
