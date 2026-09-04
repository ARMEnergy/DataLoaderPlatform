using System.Text.RegularExpressions;
using Xunit;

namespace DataLoader.Criterion.Tests;

/// <summary>
/// The generated SELECT and the connection string.
///
/// <para>
/// The SELECT is generated from the descriptors rather than hand-written per feed,
/// which is what keeps the reader's column order identical to the sink's DataTable
/// and to the TVP. These tests pin the properties that make that safe — and the
/// injection and partition-pruning properties that a hand-written query would be free
/// to get wrong.
/// </para>
/// </summary>
public sealed class SourceSqlTests
{
    private static CriterionFeedDescriptor Feed(string id) => CriterionDescriptors.Find(id)!;

    private static string Sql(string feedId) => NpgsqlCriterionSource.BuildSelect(Feed(feedId));

    // ------------------------------------------------------------ column order

    /// <summary>
    /// ⚠ THE CONTRACT. The SELECT list must be in descriptor order, because the reader
    /// fills row values by ORDINAL. A reordered SELECT would put every value in the
    /// wrong column with no error anywhere.
    /// </summary>
    [Theory]
    [InlineData(CriterionFeed.FinancialMetadata)]
    [InlineData(CriterionFeed.FinancialSeries)]
    [InlineData(CriterionFeed.MiscPeriod)]
    [InlineData(CriterionFeed.MiscUnit)]
    [InlineData(CriterionFeed.PipelinesMetadata)]
    [InlineData(CriterionFeed.PipelinesNominationPoint)]
    [InlineData(CriterionFeed.PipelinesPointflows)]
    [InlineData(CriterionFeed.PipelinesRegion)]
    public void Select_list_is_in_descriptor_order(string feedId)
    {
        var feed = Feed(feedId);
        var sql = Sql(feedId);

        var searchFrom = 0;
        foreach (var column in feed.Table.Columns)
        {
            var alias = $"AS \"{column.Name}\"";
            var index = sql.IndexOf(alias, searchFrom, StringComparison.Ordinal);

            Assert.True(index >= 0,
                $"{feedId}: alias '{alias}' missing or out of order in the generated SELECT.\n{sql}");

            searchFrom = index + alias.Length;
        }
    }

    /// <summary>
    /// Aliases are DOUBLE-QUOTED so PostgreSQL preserves the target's PascalCase.
    /// Unquoted identifiers fold to lower case in PostgreSQL, which would not break the
    /// ordinal reads but would make every debug dump of the query misleading.
    /// </summary>
    [Fact]
    public void Aliases_are_double_quoted_to_preserve_casing()
    {
        Assert.Contains("AS \"MetadataId\"", Sql(CriterionFeed.PipelinesMetadata));
        Assert.Contains("AS \"EIA_PADD_Regions\"", Sql(CriterionFeed.PipelinesRegion));
    }

    // ------------------------------------------------------------ the windowing

    [Fact]
    public void Snapshot_feeds_have_no_where_clause()
    {
        foreach (var feed in CriterionDescriptors.All.Where(f => f.WindowMode == CriterionWindowMode.Snapshot))
            Assert.DoesNotContain("WHERE", NpgsqlCriterionSource.BuildSelect(feed), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Day_window_feeds_filter_on_their_window_column_by_parameter()
    {
        foreach (var feed in CriterionDescriptors.All.Where(f => f.WindowMode == CriterionWindowMode.DayWindow))
        {
            var sql = NpgsqlCriterionSource.BuildSelect(feed);
            Assert.Contains($"WHERE {feed.WindowColumn} = @day", sql);
        }
    }

    /// <summary>
    /// ⚠ PARTITION PRUNING. <c>financial_json_latest</c> is RANGE-partitioned on
    /// <c>post_date</c> across ~150 partitions. The paged feed's keys alone would
    /// identify the rows, but without the day predicate PostgreSQL must probe every
    /// partition. Keeping both is a performance contract, not redundancy.
    /// </summary>
    [Fact]
    public void Paged_feed_keeps_the_day_predicate_for_partition_pruning()
    {
        var sql = Sql(CriterionFeed.FinancialSeriesData);

        Assert.Contains("WHERE post_date = @day", sql);
        Assert.Contains("AND financial_json_uuid = ANY(@keys)", sql);
    }

    /// <summary>
    /// ⚠ NO SQL INJECTION SURFACE. Every runtime value is a parameter; nothing from the
    /// source or from configuration is concatenated into the statement. The only
    /// interpolated text is descriptor constants compiled into the assembly.
    /// </summary>
    [Theory]
    [InlineData(CriterionFeed.FinancialMetadata)]
    [InlineData(CriterionFeed.FinancialSeries)]
    [InlineData(CriterionFeed.FinancialSeriesData)]
    [InlineData(CriterionFeed.MiscPeriod)]
    [InlineData(CriterionFeed.MiscUnit)]
    [InlineData(CriterionFeed.PipelinesMetadata)]
    [InlineData(CriterionFeed.PipelinesNominationPoint)]
    [InlineData(CriterionFeed.PipelinesPointflows)]
    [InlineData(CriterionFeed.PipelinesRegion)]
    public void Every_runtime_value_is_a_parameter(string feedId)
    {
        var sql = Sql(feedId);

        // The ONLY string literal permitted is the empty one in NULLIF(TRIM(x), ''),
        // which is a compile-time constant in the descriptor. A NON-EMPTY literal would
        // mean some value reached the statement as text rather than as a parameter —
        // that is the injection surface this test exists to keep at zero.
        foreach (Match literal in Regex.Matches(sql, @"'(?<body>[^']*)'"))
            Assert.True(literal.Groups["body"].Value.Length == 0,
                $"{feedId}: generated SQL carries the non-empty literal {literal.Value}. " +
                "Every runtime value must be a parameter.\n" + sql);

        // ...and the only parameters are the two the window uses.
        foreach (Match placeholder in Regex.Matches(sql, @"@\w+"))
            Assert.Contains(placeholder.Value, new[] { "@day", "@keys" });
    }

    /// <summary>
    /// The guard above is only meaningful if it would actually catch a concatenated
    /// value. This proves it does — without it, a regex that never matches would let
    /// the real test pass vacuously.
    /// </summary>
    [Fact]
    public void The_injection_guard_would_catch_a_concatenated_literal()
    {
        const string Concatenated = "SELECT x FROM t WHERE name = 'Robert'); DROP TABLE t;--'";

        var literals = Regex.Matches(Concatenated, @"'(?<body>[^']*)'")
            .Select(m => m.Groups["body"].Value)
            .ToList();

        Assert.Contains(literals, body => body.Length > 0);
    }

    // ------------------------------------------------------- the paged special case

    /// <summary>
    /// The paged feed selects the KEY plus the raw <c>data</c> column — its other two
    /// descriptor columns come out of the JSON, so they must not appear in the SELECT.
    /// </summary>
    [Fact]
    public void Paged_feed_selects_the_key_and_the_raw_json_only()
    {
        var sql = Sql(CriterionFeed.FinancialSeriesData);

        Assert.StartsWith("SELECT financial_json_uuid, data", sql);
        Assert.DoesNotContain("AS \"Date\"", sql);
        Assert.DoesNotContain("AS \"Value\"", sql);
    }

    /// <summary>
    /// The key-enumeration query must never select <c>data</c>. It runs once per day
    /// during work-unit enumeration; pulling the 165 KB payload there would read the
    /// whole window twice.
    /// </summary>
    [Fact]
    public void The_paged_feeds_key_column_is_just_the_uuid()
    {
        Assert.Equal("financial_json_uuid", Feed(CriterionFeed.FinancialSeriesData).KeyColumns);
    }

    // -------------------------------------------------------------- the join feed

    /// <summary>
    /// ⚠ LEFT JOIN, not INNER. 25 of 19,431 nomination rows on 2026-09-02 reference a
    /// metadata_id with no catalog row; an INNER JOIN would drop those flows silently.
    /// </summary>
    [Fact]
    public void Pointflows_uses_a_left_join_so_orphan_flows_survive()
    {
        var sql = Sql(CriterionFeed.PipelinesPointflows);

        Assert.Contains("LEFT JOIN pipelines.metadata", sql);
        Assert.DoesNotContain("INNER JOIN", sql, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Both joined relations carry <c>metadata_id</c> and <c>state_name</c>, so every
    /// column in this feed must be table-qualified or the query is ambiguous.
    /// </summary>
    [Fact]
    public void Pointflows_columns_are_all_table_qualified()
    {
        foreach (var column in Feed(CriterionFeed.PipelinesPointflows).Table.Columns)
            Assert.True(
                column.SourceExpression.StartsWith("n.", StringComparison.Ordinal) ||
                column.SourceExpression.StartsWith("m.", StringComparison.Ordinal),
                $"Pointflows column '{column.Name}' has unqualified source '{column.SourceExpression}'; " +
                "both joined relations carry metadata_id and state_name.");
    }

    /// <summary>The window column belongs to the nomination table — the metadata side has no gas day.</summary>
    [Fact]
    public void Pointflows_windows_on_the_nomination_side()
    {
        Assert.Equal("n.eff_gas_day", Feed(CriterionFeed.PipelinesPointflows).WindowColumn);
    }

    // ------------------------------------------------------- bpchar trimming

    /// <summary>
    /// ⚠ PostgreSQL <c>character(n)</c> is BLANK-PADDED. Every such column must be
    /// wrapped, or <c>'IA   '</c> lands in the target and compares unequal to
    /// <c>'IA'</c> everywhere downstream. The <c>NULLIF</c> half matters too: 60 rows
    /// in a 30-day window carry 24 spaces rather than NULL for <c>unit_id</c>.
    /// </summary>
    [Theory]
    [InlineData(CriterionFeed.FinancialMetadata, "MongoId")]
    [InlineData(CriterionFeed.FinancialMetadata, "EntityId")]
    [InlineData(CriterionFeed.FinancialSeries, "PeriodId")]
    [InlineData(CriterionFeed.FinancialSeries, "UnitId")]
    [InlineData(CriterionFeed.MiscPeriod, "MongoId")]
    [InlineData(CriterionFeed.MiscUnit, "MongoId")]
    [InlineData(CriterionFeed.PipelinesMetadata, "AssetId")]
    [InlineData(CriterionFeed.PipelinesMetadata, "LocQtiShort")]
    [InlineData(CriterionFeed.PipelinesMetadata, "StorageCalcFlag")]
    [InlineData(CriterionFeed.PipelinesMetadata, "TspShort")]
    [InlineData(CriterionFeed.PipelinesNominationPoint, "TspShort")]
    [InlineData(CriterionFeed.PipelinesRegion, "RegionId")]
    [InlineData(CriterionFeed.PipelinesRegion, "StateId")]
    [InlineData(CriterionFeed.PipelinesRegion, "StateAbb")]
    public void Blank_padded_source_columns_are_trimmed(string feedId, string columnName)
    {
        var column = Feed(feedId).Table.Columns.Single(c => c.Name == columnName);

        Assert.Contains("TRIM(", column.SourceExpression, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NULLIF(", column.SourceExpression, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------- connection string

    /// <summary>
    /// Built with NpgsqlConnectionStringBuilder, so a password containing <c>;</c> or
    /// <c>'</c> is escaped rather than terminating the string — a concatenated one
    /// would either fail to connect or silently connect with different options.
    /// </summary>
    [Fact]
    public void Connection_string_escapes_awkward_passwords()
    {
        var settings = TestHelpers.Settings(s => s.SourcePassword = "p;a'ss\"word=1");
        var built = NpgsqlCriterionSource.BuildConnectionString(settings);

        var parsed = new Npgsql.NpgsqlConnectionStringBuilder(built);
        Assert.Equal("p;a'ss\"word=1", parsed.Password);
    }

    [Fact]
    public void Connection_string_carries_the_configured_endpoint()
    {
        var built = NpgsqlCriterionSource.BuildConnectionString(TestHelpers.Settings());
        var parsed = new Npgsql.NpgsqlConnectionStringBuilder(built);

        Assert.Equal("dda.criterionrsch.com", parsed.Host);
        Assert.Equal(443, parsed.Port);
        Assert.Equal("production", parsed.Database);
        Assert.Equal(Npgsql.SslMode.Require, parsed.SslMode);
    }

    /// <summary>
    /// An unparseable SslMode falls back to Require rather than to Npgsql's default —
    /// the source requires SSL, and silently disabling it would send credentials in
    /// plaintext.
    /// </summary>
    [Fact]
    public void An_unrecognised_ssl_mode_falls_back_to_require()
    {
        var settings = TestHelpers.Settings(s => s.SourceSslMode = "nonsense");
        var parsed = new Npgsql.NpgsqlConnectionStringBuilder(
            NpgsqlCriterionSource.BuildConnectionString(settings));

        Assert.Equal(Npgsql.SslMode.Require, parsed.SslMode);
    }

    /// <summary>
    /// The pool is capped at the loader's own concurrency. The source is a shared
    /// vendor replica; a default 100-connection pool would be antisocial.
    /// </summary>
    [Fact]
    public void Connection_pool_is_capped_at_the_loaders_concurrency()
    {
        var settings = TestHelpers.Settings(s => s.MaxConcurrentWorkUnits = 4);
        var parsed = new Npgsql.NpgsqlConnectionStringBuilder(
            NpgsqlCriterionSource.BuildConnectionString(settings));

        Assert.Equal(5, parsed.MaxPoolSize);
    }

    [Fact]
    public void Connection_pool_never_drops_below_two()
    {
        var settings = TestHelpers.Settings(s => s.MaxConcurrentWorkUnits = 0);
        var parsed = new Npgsql.NpgsqlConnectionStringBuilder(
            NpgsqlCriterionSource.BuildConnectionString(settings));

        Assert.True(parsed.MaxPoolSize >= 2);
    }
}
