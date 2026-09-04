using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace DataLoader.Criterion;

/// <summary>
/// The seam between the loader and the PostgreSQL source.
///
/// <para>
/// Everything that opens a socket lives behind this interface so the readers, the
/// work-unit provider and the SQL builder are all testable without a database. The
/// production implementation is <see cref="NpgsqlCriterionSource"/>.
/// </para>
/// </summary>
public interface ICriterionSource
{
    /// <summary>
    /// The source keys for one day of a <see cref="CriterionWindowMode.PagedDayWindow"/>
    /// feed, ordered so paging is stable between runs. Selects ONLY the key column —
    /// never the JSON payload.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetPageKeysAsync(
        CriterionFeedDescriptor feed, DateOnly day, CancellationToken cancellationToken);

    /// <summary>
    /// Reads and converts one work unit's rows into target rows, already aligned to
    /// the feed's descriptor column order.
    /// </summary>
    Task<CriterionReadResult> ReadAsync(CriterionWorkUnit unit, CancellationToken cancellationToken);
}

/// <summary>What one work unit's read produced, and what it cost.</summary>
/// <param name="Rows">Target rows, in descriptor column order.</param>
/// <param name="SourceRows">Rows the source returned, before any unpivot or drop.</param>
/// <param name="DroppedRequired">Rows dropped because a required (key) column was NULL.</param>
/// <param name="Truncated">String values shortened to fit their target column.</param>
/// <param name="Collapsed">
/// Observations discarded by the intraday date collapse. Non-zero only for
/// <c>FinancialSeriesData</c>; see <see cref="CriterionSeriesData"/>.
/// </param>
/// <param name="Unparseable">JSON observations whose date could not be read.</param>
public readonly record struct CriterionReadResult(
    IReadOnlyList<CriterionRow> Rows,
    int SourceRows,
    int DroppedRequired,
    int Truncated,
    int Collapsed,
    int Unparseable);

/// <summary>
/// Reads the Criterion PostgreSQL replica with Npgsql.
///
/// <para>
/// <b>Connections are opened per work unit and closed immediately.</b> Npgsql pools
/// them underneath, so this costs nothing, and it means a stalled unit cannot hold a
/// connection open for the whole run. The pool is capped in
/// <see cref="BuildConnectionString"/> at the loader's own concurrency so this
/// loader cannot exhaust the source's connection slots.
/// </para>
/// <para>
/// <b>The SELECT is generated from the descriptors</b> rather than hand-written per
/// feed. That is what makes the reader's output positionally identical to the sink's
/// DataTable and to the TVP: all three walk the same
/// <see cref="CriterionTableDescriptor.Columns"/> list in the same order, so they
/// cannot drift. The only non-generated fragment is the WHERE clause, and every
/// value in it is a PARAMETER — no user or source text is ever concatenated into
/// SQL.
/// </para>
/// </summary>
public sealed class NpgsqlCriterionSource : ICriterionSource
{
    private readonly IOptions<CriterionSettings> _options;
    private readonly ILogger<NpgsqlCriterionSource> _logger;

    /// <summary>
    /// Built once, lazily, on first use. Lazy because the <c>SEE_DB</c> credentials
    /// are resolved by an <c>IPostConfigureOptions</c> that runs at first settings
    /// access — building this in the constructor would capture the sentinels.
    /// </summary>
    private string? _connectionString;
    private readonly object _connectionStringLock = new();

    public NpgsqlCriterionSource(IOptions<CriterionSettings> options, ILogger<NpgsqlCriterionSource> logger)
    {
        _options = options;
        _logger = logger;
    }

    private CriterionSettings Settings => _options.Value;

    public async Task<IReadOnlyList<Guid>> GetPageKeysAsync(
        CriterionFeedDescriptor feed, DateOnly day, CancellationToken cancellationToken)
    {
        if (feed.WindowMode != CriterionWindowMode.PagedDayWindow)
            throw new InvalidOperationException(
                $"Criterion {feed.FeedId} is {feed.WindowMode}; only PagedDayWindow feeds enumerate page keys.");

        // ORDER BY is not decoration: it makes page boundaries reproducible, so a
        // re-run of page 3 covers the same series it covered last time. Without it
        // PostgreSQL may return rows in any order and a resume key would stop meaning
        // anything.
        var sql =
            $"SELECT {feed.KeyColumns}\n" +
            $"FROM {feed.FromClause}\n" +
            $"WHERE {feed.WindowColumn} = @day\n" +
            $"ORDER BY {feed.KeyColumns}";

        await using var conn = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = CommandTimeout };
        cmd.Parameters.AddWithValue("day", day);

        var keys = new List<Guid>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!reader.IsDBNull(0)) keys.Add(reader.GetGuid(0));
        }

        _logger.LogDebug("Criterion {Feed}: {Day} has {Count} source row(s) to page",
            feed.FeedId, CriterionTime.Iso(day), keys.Count);

        return keys;
    }

    public async Task<CriterionReadResult> ReadAsync(CriterionWorkUnit unit, CancellationToken cancellationToken)
    {
        var feed = unit.Feed;

        await using var conn = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(BuildSelect(feed), conn) { CommandTimeout = CommandTimeout };

        switch (feed.WindowMode)
        {
            case CriterionWindowMode.Snapshot:
                break;

            case CriterionWindowMode.DayWindow:
                cmd.Parameters.AddWithValue("day", unit.Day!.Value);
                break;

            case CriterionWindowMode.PagedDayWindow:
                cmd.Parameters.AddWithValue("day", unit.Day!.Value);
                // = ANY(@keys) rather than an IN list built by string concatenation:
                // one prepared plan for every page, and no way for a key to become SQL.
                cmd.Parameters.AddWithValue("keys", unit.Keys.ToArray());
                break;

            default:
                throw new NotSupportedException($"Unmapped window mode '{feed.WindowMode}'.");
        }

        // SequentialAccess: the FinancialSeriesData select carries a `data` column
        // averaging 165 KB and reaching 1.7 MB. Without this Npgsql buffers the whole
        // row before handing back any column.
        await using var reader = await cmd
            .ExecuteReaderAsync(System.Data.CommandBehavior.SequentialAccess, cancellationToken)
            .ConfigureAwait(false);

        return feed.FeedId == CriterionFeed.FinancialSeriesData
            ? await ReadSeriesDataAsync(feed, reader, cancellationToken).ConfigureAwait(false)
            : await ReadTabularAsync(feed, reader, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The straight column-for-column path used by eight of the nine feeds. Ordinals
    /// come from the descriptor's position, not from a name lookup per row, because
    /// the SELECT is generated in descriptor order.
    /// </summary>
    private static async Task<CriterionReadResult> ReadTabularAsync(
        CriterionFeedDescriptor feed, NpgsqlDataReader reader, CancellationToken cancellationToken)
    {
        var columns = feed.Table.Columns;
        var rows = new List<CriterionRow>();
        var sourceRows = 0;
        var dropped = 0;
        var truncated = 0;

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            sourceRows++;

            var values = new object[columns.Count];
            var drop = false;

            for (var i = 0; i < columns.Count; i++)
            {
                var raw = await reader.IsDBNullAsync(i, cancellationToken).ConfigureAwait(false)
                    ? null
                    : reader.GetValue(i);

                values[i] = CriterionConvert.Value(raw, columns[i], out var wasTruncated);
                if (wasTruncated) truncated++;

                // A NULL in a key column would merge the row under a blank key and
                // collide with every other such row. Dropping it is the only safe
                // option, and the count makes the drop visible.
                if (columns[i].Required && values[i] is DBNull) drop = true;
            }

            if (drop) { dropped++; continue; }

            rows.Add(new CriterionRow(values));
        }

        return new CriterionReadResult(rows, sourceRows, dropped, truncated, 0, 0);
    }

    /// <summary>
    /// The JSON-unpivot path, used only by <c>FinancialSeriesData</c>. Each source
    /// row yields one target row per calendar date in its <c>data</c> array.
    ///
    /// <para>
    /// The SELECT for this feed is special-cased in <see cref="BuildSelect"/>: its
    /// descriptor has one real column (<c>financial_json_uuid</c>) plus two derived
    /// ones, so the query adds <c>data</c> as a second physical column and this
    /// method reads the pair positionally.
    /// </para>
    /// </summary>
    private static async Task<CriterionReadResult> ReadSeriesDataAsync(
        CriterionFeedDescriptor feed, NpgsqlDataReader reader, CancellationToken cancellationToken)
    {
        var columns = feed.Table.Columns;
        var rows = new List<CriterionRow>();
        var sourceRows = 0;
        var dropped = 0;
        var collapsed = 0;
        var unparseable = 0;

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            sourceRows++;

            // Ordinal 0 is FinancialJsonId, ordinal 1 the raw JSON. SequentialAccess
            // requires reading them in this order.
            if (await reader.IsDBNullAsync(0, cancellationToken).ConfigureAwait(false))
            {
                dropped++;
                continue;
            }

            var financialJsonId = reader.GetGuid(0);

            var json = await reader.IsDBNullAsync(1, cancellationToken).ConfigureAwait(false)
                ? null
                : reader.GetString(1);

            var parsed = CriterionSeriesData.Parse(json);
            collapsed += parsed.Collapsed;
            unparseable += parsed.Unparseable;

            foreach (var (date, value) in parsed.Rows)
            {
                var values = new object[columns.Count];
                values[0] = financialJsonId;
                values[1] = date;
                values[2] = value.HasValue ? value.Value : DBNull.Value;
                rows.Add(new CriterionRow(values));
            }
        }

        return new CriterionReadResult(rows, sourceRows, dropped, 0, collapsed, unparseable);
    }

    /// <summary>
    /// Generates the SELECT for a feed from its descriptors.
    ///
    /// <para>
    /// The column list is <see cref="CriterionFeedDescriptor.SelectList"/> — the same
    /// ordered list the sink builds its DataTable from — so the reader cannot return
    /// columns in an order the TVP does not expect.
    /// </para>
    /// </summary>
    internal static string BuildSelect(CriterionFeedDescriptor feed)
    {
        var sql = new StringBuilder();

        if (feed.FeedId == CriterionFeed.FinancialSeriesData)
        {
            // Two physical columns: the key, then the payload. The descriptor's other
            // two columns (Date, Value) come out of the JSON, so they are not selected.
            sql.Append("SELECT ").Append(feed.KeyColumns).Append(", data\n");
        }
        else
        {
            sql.Append("SELECT ").Append(feed.SelectList).Append('\n');
        }

        sql.Append("FROM ").Append(feed.FromClause).Append('\n');

        switch (feed.WindowMode)
        {
            case CriterionWindowMode.Snapshot:
                break;

            case CriterionWindowMode.DayWindow:
                sql.Append("WHERE ").Append(feed.WindowColumn).Append(" = @day");
                break;

            case CriterionWindowMode.PagedDayWindow:
                // The day predicate is kept alongside the key predicate even though the
                // keys alone identify the rows: post_date is the source's RANGE
                // partition key, so without it PostgreSQL must probe every one of the
                // ~150 partitions instead of the one that holds the day.
                sql.Append("WHERE ").Append(feed.WindowColumn).Append(" = @day\n")
                   .Append("  AND ").Append(feed.KeyColumns).Append(" = ANY(@keys)");
                break;

            default:
                throw new NotSupportedException($"Unmapped window mode '{feed.WindowMode}'.");
        }

        return sql.ToString();
    }

    private int CommandTimeout => Math.Max(30, Settings.SourceCommandTimeoutSeconds);

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var conn = new NpgsqlConnection(GetConnectionString());
        try
        {
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            return conn;
        }
        catch
        {
            await conn.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private string GetConnectionString()
    {
        if (_connectionString is not null) return _connectionString;

        lock (_connectionStringLock)
        {
            return _connectionString ??= BuildConnectionString(Settings);
        }
    }

    /// <summary>
    /// Assembles the Npgsql connection string from settings.
    ///
    /// <para>
    /// Built with <see cref="NpgsqlConnectionStringBuilder"/> rather than string
    /// concatenation so a password containing <c>;</c> or <c>'</c> is escaped rather
    /// than terminating the string — a concatenated one would either fail to connect
    /// or, worse, silently connect with different options.
    /// </para>
    /// <para>
    /// The result is a SECRET and is never logged. Callers log
    /// <see cref="CriterionSettings.SourceHost"/> and
    /// <see cref="CriterionSettings.SourceDatabase"/> instead.
    /// </para>
    /// </summary>
    internal static string BuildConnectionString(CriterionSettings settings)
    {
        if (!Enum.TryParse<SslMode>(settings.SourceSslMode, ignoreCase: true, out var sslMode))
            sslMode = SslMode.Require;

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = settings.SourceHost,
            Port = settings.SourcePort,
            Database = settings.SourceDatabase,
            Username = settings.SourceUsername,
            Password = settings.SourcePassword,
            SslMode = sslMode,
            Timeout = Math.Max(5, settings.SourceConnectTimeoutSeconds),
            CommandTimeout = Math.Max(30, settings.SourceCommandTimeoutSeconds),

            // Cap the pool at this loader's own concurrency (plus one for the
            // work-unit provider's key enumeration). The source is a shared vendor
            // replica; a default 100-connection pool would be antisocial and could get
            // the account throttled.
            MaxPoolSize = Math.Max(2, settings.MaxConcurrentWorkUnits + 1),

            ApplicationName = "ARM DataLoader.Criterion"
        };

        return builder.ConnectionString;
    }
}
