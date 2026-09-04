using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.ICE;

/// <summary>
/// Runs <c>arm.usp_ValidateLoad</c> after a run and logs whatever it reports.
///
/// <para>
/// <b>Observational only.</b> It swallows its own failures and never changes the
/// run's outcome — a validation query that cannot connect must not turn a
/// successful load into a failed one.
/// </para>
/// <para>
/// The findings that matter most here are the ones that would otherwise be
/// invisible: an <c>AuthExpired</c> file that got recorded rather than throwing, a
/// feed whose drop ratio moved, and contract codes that map to more than one
/// product id (the condition that forced <c>ProductId</c> into the
/// <c>arm.Futures</c> key).
/// </para>
/// </summary>
public sealed class IceLoadValidator
{
    private readonly IceSettings _settings;
    private readonly ILogger<IceLoadValidator> _logger;

    public IceLoadValidator(IOptions<IceSettings> settings, ILogger<IceLoadValidator> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    /// <param name="tradeDate">Scope to one trade date, or null to validate everything.</param>
    public async Task ValidateAsync(DateOnly? tradeDate, CancellationToken cancellationToken)
    {
        try
        {
            await using var conn = new SqlConnection(_settings.ConnectionString);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var cmd = new SqlCommand("arm.usp_ValidateLoad", conn) { CommandType = CommandType.StoredProcedure };
            cmd.Parameters.Add("@TradeDate", SqlDbType.Date).Value =
                tradeDate.HasValue ? tradeDate.Value.ToDateTime(TimeOnly.MinValue) : DBNull.Value;

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            var findings = 0;
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                findings++;

                var severity = reader["Severity"] as string ?? "INFO";
                var check = reader["Check"] as string ?? "(unnamed)";
                var detail = reader["Detail"] as string ?? string.Empty;
                var count = reader["Count"] is long c ? c : 0L;

                // Severity comes from the proc so the SQL owns what is alarming; the
                // loader only decides which log level carries it.
                switch (severity)
                {
                    case "ERROR":
                        _logger.LogError("ICE validation [{Check}] {Detail} (count {Count})", check, detail, count);
                        break;
                    case "WARN":
                        _logger.LogWarning("ICE validation [{Check}] {Detail} (count {Count})", check, detail, count);
                        break;
                    default:
                        _logger.LogInformation("ICE validation [{Check}] {Detail} (count {Count})", check, detail, count);
                        break;
                }
            }

            if (findings == 0)
                _logger.LogInformation("ICE validation: no findings{Scope}",
                    tradeDate.HasValue ? $" for {IceTime.Iso(tradeDate.Value)}" : string.Empty);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ICE: post-load validation could not run — run outcome is unaffected");
        }
    }
}
