using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace DataLoader.NGX;

/// <summary>
/// The list of index ids to request, read from <c>dbo.[Index]</c> in the NGX database.
///
/// <para>
/// <b>Why the catalog is a table and not a setting.</b> The index universe is curated by
/// the desk in <c>dbo.[Index]</c> — the same table the incumbent loader drives from —
/// so a new index starts loading the moment it is added there, with no deployment. The
/// endpoint also publishes its own catalogue at <c>/ngxcs/priceIndex.xml</c> (1,071
/// indices), but that is the whole exchange rather than the 47 this desk pays for, and
/// requesting one the account is not entitled to is actively harmful — see below.
/// </para>
/// <para>
/// <b>Why the IndexType filter is load-bearing.</b> <c>dbo.[Index]</c> holds 219 rows:
/// 47 <c>IndexPrice</c> and 172 <c>CrudeIndexPrice</c>. Only the 47 are entitled on
/// <c>indexPrice.xml</c>; every crude id was verified to return 403. Because the vendor
/// fails the WHOLE request when any one id in it is unentitled, a single crude id in a
/// batch of ten costs all ten — which is why this query filters rather than letting the
/// reader discover the problem per request.
/// </para>
/// <para>
/// The query is read-only and touches no table this loader writes.
/// </para>
/// </summary>
/// <remarks>
/// Not sealed, and <see cref="GetIndexIdsAsync"/> is virtual, for the same reason
/// <c>NgxTvpSink</c> is not sealed: without a seam here
/// <see cref="NgxIndexPriceWorkUnitProvider"/> cannot be exercised at all, which would
/// leave the id-batching — the 10-id vendor ceiling, the single most consequential
/// constraint in this loader — with no test coverage above the arithmetic.
/// </remarks>
internal class NgxIndexCatalog
{
    private readonly string _connectionString;
    private readonly string _indexTypeFilter;
    private readonly ILogger _logger;

    internal NgxIndexCatalog(string connectionString, string indexTypeFilter, ILogger logger)
    {
        _connectionString = connectionString;
        _indexTypeFilter = indexTypeFilter;
        _logger = logger;
    }

    /// <summary>
    /// The entitled index ids, ascending and distinct. Ordering is stable on purpose:
    /// the batches the provider cuts from this list appear in work-unit resume keys, so
    /// an unstable order would reshuffle the batches — and therefore the keys — on every
    /// run, defeating the load log.
    /// </summary>
    internal virtual async Task<IReadOnlyList<int>> GetIndexIdsAsync(CancellationToken cancellationToken)
    {
        const string sql = @"
SELECT DISTINCT IndexId
FROM dbo.[Index]
WHERE IndexType = @IndexType
ORDER BY IndexId;";

        var ids = new List<int>();

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@IndexType", System.Data.SqlDbType.VarChar, 50).Value = _indexTypeFilter;

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            ids.Add(reader.GetInt32(0));

        if (ids.Count == 0)
            _logger.LogWarning(
                "NGX: dbo.[Index] returned no rows for IndexType '{IndexType}'. The IndexPrice feed " +
                "will produce no work units. Check the filter spelling — the table holds 'IndexPrice' " +
                "and 'CrudeIndexPrice'",
                _indexTypeFilter);
        else
            _logger.LogInformation(
                "NGX: {Count} index id(s) from dbo.[Index] where IndexType = '{IndexType}' ({First}..{Last})",
                ids.Count, _indexTypeFilter, ids[0], ids[^1]);

        return ids;
    }
}
