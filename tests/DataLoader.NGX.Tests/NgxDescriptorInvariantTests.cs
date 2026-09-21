using System.Data;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DataLoader.NGX.Tests;

/// <summary>
/// The descriptor's INTERNAL consistency, and the parts of the TVP contract that the
/// existing checks leave open.
///
/// <para>
/// <c>NgxTvpContractTests</c> compares <see cref="NgxColumn.SqlType"/> against the real
/// <c>sql/NGX/002</c>, and <c>NgxSinkTests</c> compares <see cref="NgxColumn.ClrType"/>
/// against the DataTable. Neither compares the descriptor's own three facts —
/// <c>ClrType</c>, <c>SqlType</c> and <c>MaxLength</c> — against EACH OTHER, so a
/// descriptor reading <c>("Cleared", typeof(string), "BIT")</c> or
/// <c>("HubName", typeof(string), "VARCHAR(50)", 500)</c> passes both of them.
/// </para>
/// <para>
/// The second of those is the dangerous one and it is silent: <c>MaxLength</c> is what
/// the sink truncates to. Too large and SqlClient aborts the whole TVP batch with
/// "String or binary data would be truncated", losing a work unit's rows; too small and
/// every long value is quietly clipped before it ever reaches the server.
/// </para>
/// </summary>
public class NgxDescriptorInvariantTests
{
    private static string Schema => File.ReadAllText(RepoPaths.SchemaScript);
    private static string Tvp => File.ReadAllText(RepoPaths.TvpScript);

    public static TheoryData<string> TableNames() => new()
    {
        NgxDescriptors.IndexPrice.TableName,
        NgxDescriptors.StripTradingSummary.TableName
    };

    private static NgxTableDescriptor Describe(string tableName) =>
        NgxDescriptors.All.Single(t => t.TableName == tableName);

    /// <summary>The key column names of a table's PRIMARY KEY, straight out of sql/NGX/001.</summary>
    private static IReadOnlyList<string> DeclaredKey(string tableName)
    {
        var body = TvpParser.CreateTableBody(Schema, tableName);

        var match = Regex.Match(body, @"PRIMARY\s+KEY\s+(?:CLUSTERED|NONCLUSTERED)?\s*\(([^)]*)\)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        Assert.True(match.Success, $"No PRIMARY KEY found for {tableName} in 001.");

        return match.Groups[1].Value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(c => Regex.Replace(c, @"\s+(ASC|DESC)$", string.Empty, RegexOptions.IgnoreCase))
            .Select(c => c.Replace("[", string.Empty).Replace("]", string.Empty).Trim())
            .ToList();
    }

    // ============================================= descriptor self-agreement ===

    /// <summary>
    /// <c>MaxLength</c> must equal the width in the column's own <c>SqlType</c>. This is
    /// the number the sink clips to, so a descriptor that says <c>VARCHAR(50)</c> but
    /// carries <c>MaxLength: 500</c> would let a 400-character value through and fail
    /// the ENTIRE batch at the server; the reverse would clip good data at 50 with only
    /// a warning.
    /// </summary>
    [Theory]
    [MemberData(nameof(TableNames))]
    public void MaxLength_EqualsTheWidthInItsOwnSqlType(string tableName)
    {
        foreach (var column in Describe(tableName).Columns)
        {
            var width = Regex.Match(column.SqlType, @"^\s*VARCHAR\s*\(\s*(\d+)\s*\)\s*$", RegexOptions.IgnoreCase);

            if (width.Success)
                Assert.True(
                    column.MaxLength == int.Parse(width.Groups[1].Value),
                    $"{tableName}.{column.Name}: SqlType is {column.SqlType} but MaxLength is " +
                    $"{column.MaxLength?.ToString() ?? "null"}.");
            else
                Assert.True(
                    column.MaxLength is null,
                    $"{tableName}.{column.Name}: MaxLength {column.MaxLength} is set on a " +
                    $"non-VARCHAR column ({column.SqlType}); the sink would try to clip a non-string.");
        }
    }

    /// <summary>
    /// Every string column must declare a cap, or it is the one column the sink cannot
    /// protect and the one that takes the batch down.
    /// </summary>
    [Theory]
    [MemberData(nameof(TableNames))]
    public void EveryStringColumn_DeclaresACap(string tableName) =>
        Assert.All(
            Describe(tableName).Columns.Where(c => c.ClrType == typeof(string)),
            c => Assert.True(c.MaxLength is > 0, $"{tableName}.{c.Name} has no MaxLength."));

    /// <summary>
    /// <c>ClrType</c> and <c>SqlType</c> must describe the same thing. A DataTable
    /// column of the wrong CLR type does not necessarily fail — SqlClient coerces some
    /// pairs — so this mismatch can reach the server as a converted, subtly different
    /// value rather than as an error.
    /// </summary>
    [Theory]
    [MemberData(nameof(TableNames))]
    public void ClrType_AgreesWithSqlType(string tableName)
    {
        foreach (var column in Describe(tableName).Columns)
        {
            var expected = column.SqlType.ToUpperInvariant() switch
            {
                "DATE" => typeof(DateTime),
                var t when t.StartsWith("DATETIME2") => typeof(DateTime),
                "INT" => typeof(int),
                "BIGINT" => typeof(long),
                "BIT" => typeof(bool),
                var t when t.StartsWith("DECIMAL") => typeof(decimal),
                var t when t.StartsWith("VARCHAR") || t.StartsWith("NVARCHAR") => typeof(string),
                _ => throw new InvalidOperationException(
                    $"{tableName}.{column.Name}: unmapped SQL type '{column.SqlType}' — extend this test " +
                    "rather than skipping it.")
            };

            Assert.True(
                column.ClrType == expected,
                $"{tableName}.{column.Name}: SqlType {column.SqlType} implies {expected.Name} but the " +
                $"descriptor declares {column.ClrType.Name}.");
        }
    }

    /// <summary>
    /// Column names must be unique within a table. A duplicate would make
    /// <c>DataTable.Columns.Add</c> throw at run time — on the first real batch, in
    /// production, not here.
    /// </summary>
    [Theory]
    [MemberData(nameof(TableNames))]
    public void ColumnNames_AreUnique(string tableName)
    {
        var names = Describe(tableName).Columns.Select(c => c.Name).ToList();

        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    // ================================================ descriptor vs the .sql ===

    /// <summary>
    /// The widths the sink clips to must be the widths the SERVER declares. Compared
    /// against the real <c>sql/NGX/002</c> text, so widening a column in the .sql
    /// without widening <see cref="NgxColumn.MaxLength"/> — which would leave values
    /// clipped at the old width with no error anywhere — fails here.
    /// </summary>
    [Theory]
    [MemberData(nameof(TableNames))]
    public void MaxLength_MatchesTheDeclaredWidthInTheTvpScript(string tableName)
    {
        var table = Describe(tableName);
        var declared = TvpParser.Parse(Tvp, table.TvpType);

        Assert.Equal(table.Columns.Count, declared.Count);

        for (var i = 0; i < table.Columns.Count; i++)
        {
            var width = Regex.Match(declared[i].SqlType, @"VARCHAR\s*\(\s*(\d+)\s*\)", RegexOptions.IgnoreCase);

            Assert.Equal(
                width.Success ? int.Parse(width.Groups[1].Value) : (int?)null,
                table.Columns[i].MaxLength);
        }
    }

    /// <summary>
    /// <see cref="NgxColumn.IsKey"/> must be exactly the PRIMARY KEY declared in
    /// <c>sql/NGX/001</c> — no more, no fewer.
    ///
    /// <para>It decides whether the sink truncates a value or discards the row, so a
    /// key column missing the flag gets CLIPPED and merged into a different row, and a
    /// non-key column carrying it gets rows thrown away for being long.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(TableNames))]
    public void IsKey_MatchesThePrimaryKeyInTheSchemaScript(string tableName)
    {
        var flagged = Describe(tableName).Columns.Where(c => c.IsKey).Select(c => c.Name)
            .OrderBy(n => n, StringComparer.Ordinal).ToList();

        var declared = DeclaredKey(tableName)
            .OrderBy(n => n, StringComparer.Ordinal).ToList();

        Assert.Equal(declared, flagged);
    }

    /// <summary>
    /// <b>The strip key is SEVEN columns.</b> <c>ExchangeReference</c> is recycled
    /// across dates and markets — <c>48000000003842</c> was observed on four separate
    /// trading days — so anything narrower merges genuinely different trades onto one
    /// row. Pinned as a number so a "simplification" of the key has to argue with a
    /// failing test.
    /// </summary>
    [Fact]
    public void StripKey_IsSevenColumns()
    {
        Assert.Equal(7, DeclaredKey(NgxDescriptors.StripTradingSummary.TableName).Count);
        Assert.Equal(7, NgxDescriptors.StripTradingSummary.Columns.Count(c => c.IsKey));

        Assert.Equal(
            new[]
            {
                "BeginDate", "EndDate", "ExchangeReference", "HubId",
                "MarketId", "StripType", "TradeDateTime"
            },
            NgxDescriptors.StripTradingSummary.Columns
                .Where(c => c.IsKey).Select(c => c.Name)
                .OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// The index key leads with the loader-supplied <c>ExecutionDate</c>: this feed is a
    /// daily SNAPSHOT, so each run writes a new generation of the same window rather
    /// than overwriting the last.
    /// </summary>
    [Fact]
    public void IndexKey_IsTheFourSnapshotColumns() =>
        Assert.Equal(
            new[] { "ExecutionDate", "IndexId", "PriceEffectiveEnd", "PriceEffectiveStart" },
            NgxDescriptors.IndexPrice.Columns
                .Where(c => c.IsKey).Select(c => c.Name)
                .OrderBy(n => n, StringComparer.Ordinal).ToArray());

    /// <summary>
    /// Every key column must be NOT NULL in the TVP. Derived from
    /// <see cref="NgxColumn.IsKey"/> rather than from a hand-written list, so adding a
    /// key column cannot leave a nullable hole behind.
    /// </summary>
    [Theory]
    [MemberData(nameof(TableNames))]
    public void EveryKeyColumn_IsNotNullInTheTvp(string tableName)
    {
        var table = Describe(tableName);
        var declared = TvpParser.Parse(Tvp, table.TvpType);

        foreach (var key in table.Columns.Where(c => c.IsKey))
        {
            var column = declared.Single(c => c.Name == key.Name);
            Assert.True(column.NotNull, $"{table.TvpType}.{key.Name} is a key column but is nullable.");
        }
    }

    /// <summary>
    /// The DB-stamped and server-generated columns must never appear in a TVP. Named
    /// individually so the list is explicit rather than implied by an equality check
    /// elsewhere.
    /// </summary>
    [Theory]
    [MemberData(nameof(TableNames))]
    public void ForbiddenColumns_AreAbsentFromTheDescriptorAndTheTvp(string tableName)
    {
        var table = Describe(tableName);
        var declared = TvpParser.Parse(Tvp, table.TvpType).Select(c => c.Name).ToList();

        foreach (var forbidden in new[] { "ModifiedAtUtc", "CreatedAtUtc", "RowId", "LoadId" })
        {
            Assert.DoesNotContain(table.Columns, c =>
                c.Name.Equals(forbidden, StringComparison.OrdinalIgnoreCase));

            Assert.DoesNotContain(declared, n =>
                n.Equals(forbidden, StringComparison.OrdinalIgnoreCase));
        }
    }

    // ================================================ the sink's use of IsKey ===

    private sealed class ProbeSink : NgxTvpSink
    {
        public ProbeSink(NgxTableDescriptor table, ILogger? logger = null)
            : base(table, "Server=(local);Database=NGX;Integrated Security=SSPI;",
                   logger ?? NullLogger.Instance) { }

        public DataTable Build(IReadOnlyList<NgxRow> rows) => BuildTable(rows);
    }

    /// <summary>A valid strip row in descriptor order, so a single column can be spoiled.</summary>
    private static object[] StripValues() => new object[]
    {
        new DateTime(2026, 9, 1, 7, 37, 6),     // TradeDateTime
        28,                                      // HubId
        1,                                       // MarketId
        "Yesterday",                             // StripType
        "48000000003842",                        // ExchangeReference
        new DateTime(2026, 8, 31),               // BeginDate
        new DateTime(2026, 8, 31),               // EndDate
        "AB-NIT",                                // HubName
        "NGX Phys, FP (CA/GJ), AB-NIT",          // MarketName
        "1-September-2026 (31-August-2026)",     // SettlementTitle
        true,                                    // Cleared
        2500m, "GJ", 2500m, "GJ", 2.5m,          // volumes
        1.2m, "CAD",                             // price
        DBNull.Value,                            // BrokerCompanyName
        false, true                              // the two indicators
    };

    /// <summary>
    /// <b>A key value that exceeds its width is NOT truncated — the row is discarded.</b>
    /// Clipping a key does not fail loudly; it rewrites the merge predicate and folds the
    /// row into a DIFFERENT existing one. With <c>ExchangeReference</c> already recycled
    /// by the vendor, a clipped one is genuinely likely to hit a real trade.
    /// </summary>
    [Fact]
    public void BuildTable_OversizedKeyValue_DropsTheRowRatherThanClippingIt()
    {
        var table = NgxDescriptors.StripTradingSummary;

        var good = new NgxRow { Values = StripValues() };

        var bad = StripValues();
        bad[table.Ordinal("ExchangeReference")] = new string('9', 300);   // VARCHAR(256)

        var built = new ProbeSink(table).Build(new[] { good, new NgxRow { Values = bad } });

        Assert.Equal(1, built.Rows.Count);
        Assert.Equal("48000000003842", built.Rows[0][table.Ordinal("ExchangeReference")]);
    }

    /// <summary>
    /// The contrast, and the reason the rule has to be per column rather than global: a
    /// NON-key string is still truncated and its row still loads. Losing a work unit's
    /// rows over one long label is the failure this behaviour exists to prevent.
    /// </summary>
    [Fact]
    public void BuildTable_OversizedNonKeyValue_StillLoadsTheRow()
    {
        var table = NgxDescriptors.StripTradingSummary;

        var values = StripValues();
        values[table.Ordinal("MarketName")] = new string('M', 400);   // VARCHAR(256)

        var row = new NgxRow { Values = values };
        var built = new ProbeSink(table).Build(new[] { row });

        Assert.Equal(1, built.Rows.Count);
        Assert.Equal(new string('M', 256), built.Rows[0][table.Ordinal("MarketName")]);

        // ...and the caller's array is untouched, because a retry re-sends these rows.
        Assert.Equal(400, ((string)row.Values[table.Ordinal("MarketName")]).Length);
    }

    /// <summary>
    /// A key that is exactly at its declared width is a valid key, not an overflow — the
    /// comparison must be strictly greater-than.
    /// </summary>
    [Fact]
    public void BuildTable_KeyValueExactlyAtTheLimit_IsKept()
    {
        var table = NgxDescriptors.StripTradingSummary;

        var values = StripValues();
        values[table.Ordinal("ExchangeReference")] = new string('7', 256);

        var built = new ProbeSink(table).Build(new[] { new NgxRow { Values = values } });

        Assert.Equal(1, built.Rows.Count);
        Assert.Equal(new string('7', 256), built.Rows[0][table.Ordinal("ExchangeReference")]);
    }

    /// <summary>
    /// A row dropped for key overflow must be logged at ERROR, not warning: it is
    /// missing data, not clipped data, and nothing downstream will notice on its own.
    /// </summary>
    [Fact]
    public void BuildTable_KeyOverflow_IsLoggedAsAnError()
    {
        var table = NgxDescriptors.StripTradingSummary;
        var logger = new CapturingLogger();

        var values = StripValues();
        values[table.Ordinal("StripType")] = new string('S', 300);   // VARCHAR(256), a key

        new ProbeSink(table, logger).Build(new[] { new NgxRow { Values = values } });

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("StripType"));
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
