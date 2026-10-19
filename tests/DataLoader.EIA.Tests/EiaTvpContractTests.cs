using System.Data;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DataLoader.EIA.Tests;

/// <summary>
/// The TVP contract gate.
///
/// <para>
/// A table-valued parameter binds BY POSITION. If the DataTable
/// <see cref="EiaSink"/> builds and the <c>CREATE TYPE</c> in <c>sql/EIA/002</c>
/// disagree on column order or type, SQL Server does not complain — it writes every
/// row into the wrong columns. Here that would put <c>FacetJSON</c> into
/// <c>DataJSON</c> on ~90 million rows. The repo's <c>tvp-contract-check</c> skill
/// exists because the failure is silent.
/// </para>
/// <para>
/// These tests close the loop by PARSING THE REAL .sql FILES, so editing one side
/// without the other fails the build rather than waiting for review.
/// </para>
/// </summary>
public sealed class EiaTvpContractTests
{
    private const string TvpType = "arm.SeriesDataTvp";
    private const string Table = "arm.SeriesData";
    private const string MergeProc = "arm.usp_BulkMergeSeriesData";

    private static readonly string TvpSql = File.ReadAllText(RepoPaths.TvpScript);
    private static readonly string SchemaSql = File.ReadAllText(RepoPaths.SchemaScript);
    private static readonly string ProcSql = File.ReadAllText(RepoPaths.ProceduresScript);
    private static readonly string DropSql = File.ReadAllText(RepoPaths.DropScript);

    /// <summary>Exposes the protected BuildTable without opening a connection.</summary>
    private sealed class ProbeSink : EiaSink
    {
        public ProbeSink()
            : base(Options.Create(new EiaSettings
                   {
                       ConnectionString = "Server=(local);Database=EIAv2;Integrated Security=SSPI;"
                   }),
                   NullLogger<EiaSink>.Instance)
        {
        }

        public DataTable Build(IReadOnlyList<EiaRow> rows) => BuildTable(rows);
    }

    private static DataTable BuiltTable() => new ProbeSink().Build(Array.Empty<EiaRow>());

    // ------------------------------------------------- DataTable vs the .sql

    [Fact]
    public void DataTable_matches_the_tvp_by_name_and_order()
    {
        var sqlColumns = TvpParser.Parse(TvpSql, TvpType);
        var table = BuiltTable();

        Assert.True(sqlColumns.Count > 0, $"No CREATE TYPE found for {TvpType} in 002.");
        Assert.Equal(sqlColumns.Count, table.Columns.Count);

        for (var i = 0; i < sqlColumns.Count; i++)
        {
            Assert.True(
                string.Equals(sqlColumns[i].Name, table.Columns[i].ColumnName, StringComparison.OrdinalIgnoreCase),
                $"{TvpType} column {i}: 002 says '{sqlColumns[i].Name}', the sink says '{table.Columns[i].ColumnName}'.");
        }
    }

    [Fact]
    public void DataTable_clr_types_match_the_tvp_sql_types()
    {
        var sqlColumns = TvpParser.Parse(TvpSql, TvpType);
        var table = BuiltTable();

        for (var i = 0; i < sqlColumns.Count; i++)
        {
            var sqlType = sqlColumns[i].SqlType.ToUpperInvariant();
            var expected = sqlType.StartsWith("VARCHAR", StringComparison.Ordinal) ? typeof(string)
                         : sqlType.StartsWith("INT", StringComparison.Ordinal) ? typeof(int)
                         : throw new InvalidOperationException($"Unmapped SQL type '{sqlColumns[i].SqlType}'.");

            Assert.True(
                expected == table.Columns[i].DataType,
                $"Column '{sqlColumns[i].Name}': 002 says {sqlColumns[i].SqlType} " +
                $"(expected CLR {expected.Name}) but the sink builds {table.Columns[i].DataType.Name}.");
        }
    }

    // ------------------------------------------------------ TVP vs the table

    [Fact]
    public void Tvp_is_the_table_minus_only_the_db_stamped_column()
    {
        var tableColumns = TvpParser.ParseTable(SchemaSql, Table);
        var tvpColumns = TvpParser.Parse(TvpSql, TvpType);

        var expected = tableColumns
            .Where(c => !string.Equals(c.Name, "ModifiedAtUtc", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.Equal(expected.Count, tvpColumns.Count);

        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].Name, tvpColumns[i].Name, ignoreCase: true);
            Assert.Equal(
                Normalise(expected[i].SqlType),
                Normalise(tvpColumns[i].SqlType));
        }

        static string Normalise(string sqlType) => Regex.Replace(sqlType, @"\s+", string.Empty).ToUpperInvariant();
    }

    /// <summary>
    /// ModifiedAtUtc is DB-stamped and must never be a TVP column. The table's DEFAULT
    /// is <c>sysdatetime()</c> (LOCAL time, despite the column's name), so the merge
    /// proc sets it explicitly with <c>SYSUTCDATETIME()</c> — and if the loader started
    /// sending it, the two would fight.
    /// </summary>
    [Fact]
    public void ModifiedAtUtc_is_never_a_tvp_column()
    {
        Assert.DoesNotContain(
            TvpParser.Parse(TvpSql, TvpType),
            c => string.Equals(c.Name, "ModifiedAtUtc", StringComparison.OrdinalIgnoreCase));

        Assert.DoesNotContain(
            BuiltTable().Columns.Cast<DataColumn>(),
            c => string.Equals(c.ColumnName, "ModifiedAtUtc", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_four_key_columns_are_not_null_in_the_tvp()
    {
        var byName = TvpParser.Parse(TvpSql, TvpType)
                              .ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var key in new[] { "SeriesId", "FrequencyId", "Period", "FacetKey" })
            Assert.True(byName[key].NotNull, $"{key} must be NOT NULL in {TvpType} — it is part of the primary key.");
    }

    // -------------------------------------------------------- the row type

    [Fact]
    public void Sink_writes_every_row_value_into_the_matching_column()
    {
        var row = new EiaRow(
            "coal/market-sales-price", "annual", "2021", -1622649321,
            """{"marketTypeId":"OM"}""", """{"price":"w"}""", -764207628);

        var table = new ProbeSink().Build(new[] { row });

        Assert.Single(table.Rows);
        Assert.Equal("coal/market-sales-price", table.Rows[0]["SeriesId"]);
        Assert.Equal("annual", table.Rows[0]["FrequencyId"]);
        Assert.Equal("2021", table.Rows[0]["Period"]);
        Assert.Equal(-1622649321, table.Rows[0]["FacetKey"]);
        Assert.Equal("""{"marketTypeId":"OM"}""", table.Rows[0]["FacetJSON"]);
        Assert.Equal("""{"price":"w"}""", table.Rows[0]["DataJSON"]);
        Assert.Equal(-764207628, table.Rows[0]["DataHash"]);
    }

    // ------------------------------------------------------ the merge proc

    [Fact]
    public void Merge_proc_takes_the_tvp_and_returns_a_row_count()
    {
        var body = TvpParser.StripComments(TvpParser.ProcedureBody(ProcSql, MergeProc));

        Assert.Contains($"@Records {TvpType} READONLY", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SELECT @@ROWCOUNT AS RecordsProcessed", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Merge_proc_matches_on_all_four_key_columns()
    {
        var body = TvpParser.StripComments(TvpParser.ProcedureBody(ProcSql, MergeProc));

        foreach (var key in new[] { "SeriesId", "FrequencyId", "Period", "FacetKey" })
            Assert.Matches(new Regex($@"tgt\.\[{key}\]\s*=\s*s\.\[{key}\]", RegexOptions.IgnoreCase), body);
    }

    /// <summary>
    /// The change guard is what keeps ModifiedAtUtc meaningful. Without it every
    /// re-verified window re-stamps every row and the column degrades into "time of
    /// last run".
    /// </summary>
    [Fact]
    public void Merge_proc_guards_the_update_on_the_data_hash()
    {
        var body = TvpParser.StripComments(TvpParser.ProcedureBody(ProcSql, MergeProc));

        Assert.Matches(new Regex(@"WHEN\s+MATCHED\s+AND", RegexOptions.IgnoreCase), body);
        Assert.Matches(new Regex(@"tgt\.\[DataHash\]\s*<>\s*s\.\[DataHash\]", RegexOptions.IgnoreCase), body);
    }

    /// <summary>
    /// MERGE aborts the whole batch on a duplicated source key, so the de-dup is a
    /// availability guard, not tidiness: EIA's offset paging can re-serve a row.
    /// </summary>
    [Fact]
    public void Merge_proc_de_duplicates_its_source_batch()
    {
        var body = TvpParser.StripComments(TvpParser.ProcedureBody(ProcSql, MergeProc));

        Assert.Matches(new Regex(@"ROW_NUMBER\(\)\s*OVER", RegexOptions.IgnoreCase), body);
        Assert.Matches(new Regex(@"WHERE\s+rn\s*=\s*1", RegexOptions.IgnoreCase), body);
    }

    [Fact]
    public void Merge_proc_stamps_utc_explicitly()
    {
        var body = TvpParser.StripComments(TvpParser.ProcedureBody(ProcSql, MergeProc));

        Assert.Contains("SYSUTCDATETIME()", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SYSDATETIME()", body.Replace("SYSUTCDATETIME()", string.Empty, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A batch is one window of one series. Deleting rows absent from it would wipe
    /// every other window of that series.
    /// </summary>
    [Fact]
    public void Merge_proc_never_deletes_by_absence()
    {
        var body = TvpParser.StripComments(TvpParser.ProcedureBody(ProcSql, MergeProc));

        Assert.DoesNotMatch(new Regex(@"NOT\s+MATCHED\s+BY\s+SOURCE", RegexOptions.IgnoreCase), body);
    }

    // ------------------------------------------------- the USE-database rule

    [Theory]
    [InlineData("001_CreateEiaSchema.sql")]
    [InlineData("002_CreateEiaTvpTypes.sql")]
    [InlineData("003_CreateEiaProcedures.sql")]
    [InlineData("999_DropEiaObjects.sql")]
    public void Every_script_declares_its_target_database(string fileName)
    {
        var sql = File.ReadAllText(Path.Combine(RepoPaths.SqlDirectory, fileName));

        Assert.Matches(new Regex(@"^\s*USE\s+EIAv2\s*;\s*$\s*^\s*GO\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Multiline), sql);
    }

    /// <summary>
    /// 🔒 The single most dangerous property of this loader's SQL. The same database's
    /// <c>dbo</c> schema holds the live incumbent dataset (~94 million rows) in a table
    /// with the IDENTICAL name, so an unqualified DROP in the tear-down script would
    /// destroy it.
    /// </summary>
    [Fact]
    public void The_drop_script_only_ever_names_arm_objects()
    {
        var body = TvpParser.StripComments(DropSql);

        var drops = Regex.Matches(body, @"DROP\s+(TABLE|TYPE|PROCEDURE)\s+IF\s+EXISTS\s+(?<name>[^\s;]+)",
                                  RegexOptions.IgnoreCase);

        Assert.NotEmpty(drops);
        foreach (Match drop in drops)
        {
            var name = drop.Groups["name"].Value;
            Assert.StartsWith("arm.", name, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// The catalog tables are INPUT to this loader. 001 does not create them and 999
    /// must not drop them, or a tear-down would take out data the loader cannot
    /// rebuild.
    /// </summary>
    [Fact]
    public void The_drop_script_leaves_the_catalog_tables_alone()
    {
        var body = TvpParser.StripComments(DropSql);

        foreach (var catalog in new[] { "arm.Series", "arm.SeriesFrequency", "arm.SeriesDataType" })
            Assert.DoesNotMatch(
                new Regex($@"DROP\s+TABLE\s+(IF\s+EXISTS\s+)?{Regex.Escape(catalog)}\b", RegexOptions.IgnoreCase),
                body);
    }

    [Fact]
    public void The_schema_script_does_not_create_the_catalog_tables()
    {
        var body = TvpParser.StripComments(SchemaSql);

        foreach (var catalog in new[] { "arm.Series", "arm.SeriesFrequency", "arm.SeriesDataType" })
        {
            // The trailing "]? \s* \(" matters: without it, arm.Series would also match
            // the CREATE TABLE for arm.SeriesData, which this script DOES create.
            var pattern = $@"CREATE\s+TABLE\s+\[?{catalog.Replace(".", @"\]?\s*\.\s*\[?")}\]?\s*\(";
            Assert.DoesNotMatch(new Regex(pattern, RegexOptions.IgnoreCase), body);
        }
    }

    [Fact]
    public void The_schema_script_does_create_the_one_table_this_loader_owns()
    {
        Assert.Matches(
            new Regex(@"CREATE\s+TABLE\s+\[?arm\]?\s*\.\s*\[?SeriesData\]?\s*\(", RegexOptions.IgnoreCase),
            TvpParser.StripComments(SchemaSql));
    }

    // --------------------------------------- the catalog-read procs exist

    [Theory]
    [InlineData("arm.usp_GetActiveSeries")]
    [InlineData("arm.usp_GetSeriesFrequencies")]
    [InlineData("arm.usp_GetSeriesDataTypes")]
    [InlineData("arm.usp_UpdateSeriesEndPeriod")]
    [InlineData("arm.usp_ValidateLoad")]
    public void Every_procedure_the_loader_calls_is_defined(string procName)
    {
        Assert.Matches(
            new Regex($@"CREATE\s+OR\s+ALTER\s+PROCEDURE\s+{Regex.Escape(procName)}\b", RegexOptions.IgnoreCase),
            ProcSql);
    }

    /// <summary>
    /// The loader request asked for stored procedures "so the actual queries are not
    /// hardcoded in the software". This asserts the queries really are in the proc, by
    /// checking each one selects from the catalog table it is supposed to.
    /// </summary>
    [Theory]
    [InlineData("arm.usp_GetActiveSeries", "arm.Series")]
    [InlineData("arm.usp_GetSeriesFrequencies", "arm.SeriesFrequency")]
    [InlineData("arm.usp_GetSeriesDataTypes", "arm.SeriesDataType")]
    public void Each_catalog_read_proc_queries_its_table(string procName, string table)
    {
        var body = TvpParser.StripComments(TvpParser.ProcedureBody(ProcSql, procName));

        Assert.Matches(new Regex($@"FROM\s+{Regex.Escape(table)}\b", RegexOptions.IgnoreCase), body);
    }

    /// <summary>
    /// The active-series filter is exactly the one the loader request supplied.
    /// </summary>
    [Fact]
    public void The_active_series_proc_filters_on_enabled_and_is_data_series()
    {
        var body = TvpParser.StripComments(TvpParser.ProcedureBody(ProcSql, "arm.usp_GetActiveSeries"));

        Assert.Matches(
            new Regex(@"WHERE\s+Enabled\s*=\s*1\s+AND\s+IsDataSeries\s*=\s*1", RegexOptions.IgnoreCase),
            body);
    }

    /// <summary>
    /// ⚠ The frequency and data-type reads must NOT filter on Enabled: the column is
    /// NULL on every row of the live catalog (371 and 390 rows), so a filter would load
    /// nothing at all. The request's queries do not have one either.
    /// </summary>
    [Theory]
    [InlineData("arm.usp_GetSeriesFrequencies")]
    [InlineData("arm.usp_GetSeriesDataTypes")]
    public void The_per_series_reads_do_not_filter_on_the_all_null_enabled_column(string procName)
    {
        var body = TvpParser.StripComments(TvpParser.ProcedureBody(ProcSql, procName));

        Assert.DoesNotMatch(new Regex(@"Enabled\s*=\s*1", RegexOptions.IgnoreCase), body);
    }

    /// <summary>
    /// EndPeriod must only ever move forward. A partial run or a forecast series that
    /// retracts its horizon must not walk it backwards.
    /// </summary>
    [Fact]
    public void The_end_period_stamp_is_advance_only_and_never_touches_start_period()
    {
        var body = TvpParser.StripComments(TvpParser.ProcedureBody(ProcSql, "arm.usp_UpdateSeriesEndPeriod"));

        Assert.Matches(new Regex(@"EndPeriod\s+IS\s+NULL\s+OR\s+EndPeriod\s*<\s*@EndPeriod", RegexOptions.IgnoreCase), body);
        Assert.DoesNotMatch(new Regex(@"SET\s+[^;]*\bStartPeriod\s*=", RegexOptions.IgnoreCase), body);
    }
}
