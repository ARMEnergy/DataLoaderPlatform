using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace DataLoader.NUCORE;

/// <summary>
/// Reads the newest system date already stored for a given trade month.
///
/// <para>
/// This is what makes <c>PositionDetails</c> incremental. That endpoint has no date
/// parameter, so the loader cannot ask for one day; instead it pages a trade month
/// newest-first (<c>SortFields=-systemDate</c>) and stops at this watermark. Without it,
/// every run would re-download the trade month's entire system-date history - for the whole
/// feed, ~2.47M rows and ~10 GB.
/// </para>
/// <para>
/// The watermark is read FROM THE TARGET TABLE rather than from a separate state table, so
/// it is self-healing: delete rows and they are simply re-fetched, with no state to get out
/// of step with the data. It is backed by
/// <c>IX_PositionDetails_TradeMonth_SystemDate</c>; without that index this is a scan of
/// the largest table in the database on every work unit.
/// </para>
/// </summary>
internal sealed class NucoreWatermarkStore
{
    private readonly string _connectionString;
    private readonly ILogger _logger;

    public NucoreWatermarkStore(string connectionString, ILogger logger)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

    /// <summary>
    /// The newest stored date for <paramref name="tradeMonth"/>, or <c>null</c> when the
    /// trade month has never been loaded (in which case the unit reads all of it).
    /// </summary>
    public async Task<DateTime?> GetAsync(
        NucoreEndpoint endpoint, string tradeMonth, CancellationToken ct)
    {
        try
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(ct).ConfigureAwait(false);

            await using var command = new SqlCommand(
                $"dbo.usp_Get{endpoint.Name}Watermark", connection)
            {
                CommandType = CommandType.StoredProcedure,
                // The default 30s is not a safe budget against a table this size, and a
                // timeout here is silent in the worst way: it would look like "no watermark"
                // and trigger a full re-read of the trade month.
                CommandTimeout = 120
            };
            command.Parameters.Add("@TradeMonth", SqlDbType.VarChar, 20).Value = tradeMonth;

            var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);

            return result is DateTime dt ? dt : null;
        }
        catch (SqlException ex)
        {
            // Deliberately NOT swallowed into "null". Returning null on failure would
            // silently re-download the whole trade month and look like a slow run rather
            // than a broken one.
            _logger.LogError(ex,
                "NUCORE {Endpoint}: failed to read the watermark for trade month {TradeMonth}",
                endpoint.Name, tradeMonth);
            throw;
        }
    }
}
