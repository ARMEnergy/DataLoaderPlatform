using System.Data;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DataLoader.Genscape.Tests;

/// <summary>
/// The TVP contract gate.
///
/// <para>
/// A table-valued parameter binds BY POSITION. If the DataTable the sink builds and the
/// <c>CREATE TYPE</c> in <c>sql/Genscape/002</c> disagree on column order or type, SQL
/// Server does not complain — it writes every row into the wrong columns. The repo's
/// <c>tvp-contract-check</c> skill exists because that failure is silent.
/// </para>
/// <para>
/// These tests close the loop by PARSING THE REAL .sql FILES and comparing them to
/// <see cref="GenscapeDescriptors"/>, which is also what the sink builds its DataTable
/// from AND what the feed parsers fill positionally. Editing one side without the other
/// fails the build, so the contract is enforced continuously rather than at review time.
/// </para>
/// </summary>
public sealed class GenscapeTvpContractTests
{
    private static readonly string TvpSql = File.ReadAllText(RepoPaths.TvpScript);
    private static readonly string SchemaSql = File.ReadAllText(RepoPaths.SchemaScript);
    private static readonly string ProcSql = File.ReadAllText(RepoPaths.ProceduresScript);
    private static readonly string DropSql = File.ReadAllText(RepoPaths.DropScript);

    public static TheoryData<string> AllTableNames()
    {
        var data = new TheoryData<string>();
        foreach (var table in GenscapeDescriptors.AllTables) data.Add(table.TableName);
        return data;
    }

    private static GenscapeTableDescriptor Table(string tableName) =>
        GenscapeDescriptors.AllTables.Single(t => t.TableName == tableName);

    /// <summary>Exposes the protected BuildTable without opening a connection.</summary>
    private sealed class ProbeSink : GenscapeTvpSink
    {
        public ProbeSink(GenscapeTableDescriptor table)
            : base(table, "Server=(local);Database=Genscape;Integrated Security=SSPI;", NullLogger.Instance) { }

        public DataTable Build(IReadOnlyList<GenscapeRow> rows) => BuildTable(rows);
    }

    // --------------------------------------------------- descriptor vs the .sql

    [Theory]
    [MemberData(nameof(AllTableNames))]
    public void Tvp_matches_descriptor_by_name_order_and_type(string tableName)
    {
        var table = Table(tableName);
        var sqlColumns = TvpParser.Parse(TvpSql, table.TvpType);

        Assert.True(sqlColumns.Count > 0, $"No CREATE TYPE found for {table.TvpType} in 002.");
        Assert.Equal(table.Columns.Count, sqlColumns.Count);

        for (var i = 0; i < table.Columns.Count; i++)
        {
            var expected = table.Columns[i];
            var actual = sqlColumns[i];

            Assert.True(
                string.Equals(expected.Name, actual.Name, StringComparison.OrdinalIgnoreCase),
                $"{table.TvpType} column {i}: descriptor says '{expected.Name}', 002 says '{actual.Name}'.");

            Assert.True(
                NormalizeType(expected.SqlType) == NormalizeType(actual.SqlType),
                $"{table.TvpType} column {i} '{expected.Name}': descriptor says {expected.SqlType}, " +
                $"002 says {actual.SqlType}.");

            Assert.True(
                expected.Required == actual.NotNull,
                $"{table.TvpType} column {i} '{expected.Name}': descriptor Required={expected.Required}, " +
                $"002 NOT NULL={actual.NotNull}.");
        }
    }

    /// <summary>
    /// <c>ModifiedAtUtc</c> is a DB-stamped default. A TVP that carried it would either
    /// overwrite the server's value or shift every following column.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllTableNames))]
    public void Tvp_never_carries_db_stamped_columns(string tableName)
    {
        Assert.DoesNotContain(Table(tableName).Columns, c =>
            c.Name.Equals("ModifiedAtUtc", StringComparison.OrdinalIgnoreCase) ||
            c.Name.Equals("DateCreated", StringComparison.OrdinalIgnoreCase));

        Assert.DoesNotContain(TvpParser.Parse(TvpSql, Table(tableName).TvpType), c =>
            c.Name.Equals("ModifiedAtUtc", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Every type declared in 002 must belong to a table — no orphans left by an edit.</summary>
    [Fact]
    public void Every_type_in_the_script_belongs_to_a_table()
    {
        var declared = TvpParser.DeclaredTypeNames(TvpSql);
        var expected = GenscapeDescriptors.AllTables.Select(t => t.TvpType)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Equal(GenscapeDescriptors.AllTables.Count, declared.Count);
        foreach (var name in declared)
            Assert.True(expected.Contains(name), $"002 declares {name}, which no table descriptor uses.");
    }

    [Theory]
    [MemberData(nameof(AllTableNames))]
    public void Merge_procedure_exists_and_takes_the_right_tvp(string tableName)
    {
        var table = Table(tableName);

        var pattern = new Regex(
            $@"CREATE\s+OR\s+ALTER\s+PROCEDURE\s+{Regex.Escape(table.MergeProc)}\s+@Records\s+{Regex.Escape(table.TvpType)}\s+READONLY",
            RegexOptions.IgnoreCase);

        Assert.True(pattern.IsMatch(ProcSql),
            $"003 has no '{table.MergeProc}' taking '@Records {table.TvpType} READONLY'.");
    }

    [Theory]
    [MemberData(nameof(AllTableNames))]
    public void Target_table_is_created_in_the_schema_script(string tableName) =>
        Assert.Contains($"CREATE TABLE {tableName}", SchemaSql, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Every descriptor column must be a real column of its target table in 001, in the
    /// same order. This catches the drift 002 alone cannot: a TVP and a descriptor that
    /// agree with each other but not with the table.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllTableNames))]
    public void Descriptor_columns_appear_in_the_table_in_order(string tableName)
    {
        var table = Table(tableName);
        var body = TvpParser.CreateTableBody(SchemaSql, tableName);

        var searchFrom = 0;
        foreach (var column in table.Columns)
        {
            // Column names are bracketed in 001 where they collide with a keyword
            // ([Year], [Week], [Type]).
            var index = IndexOfColumn(body, column.Name, searchFrom);

            Assert.True(index >= 0,
                $"001 CREATE TABLE {tableName} does not declare '{column.Name}' after position {searchFrom}. " +
                "Descriptor column order must match the table.");

            searchFrom = index + column.Name.Length;
        }
    }

    /// <summary>
    /// Tables must not share a merge proc or a TVP.
    /// <see cref="Core.Concurrency.SqlWriteGate"/> keys on the proc name, so sharing one
    /// would serialize unrelated tables — and a copy-paste error here would silently
    /// write one table's rows into another's.
    /// </summary>
    [Fact]
    public void Table_names_tvps_and_procs_are_all_distinct()
    {
        var tables = GenscapeDescriptors.AllTables;

        Assert.Equal(tables.Count, tables.Select(t => t.TableName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(tables.Count, tables.Select(t => t.TvpType).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(tables.Count, tables.Select(t => t.MergeProc).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Theory]
    [MemberData(nameof(AllTableNames))]
    public void Merge_procedure_deduplicates_its_source(string tableName)
    {
        var body = TvpParser.ProcedureBody(ProcSql, Table(tableName).MergeProc);

        Assert.Contains("ROW_NUMBER() OVER", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("rn = 1", body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// No merge may delete by absence. Each work unit carries ONE window of ONE region,
    /// so a <c>WHEN NOT MATCHED BY SOURCE THEN DELETE</c> would wipe every other
    /// window's and region's rows on every unit.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllTableNames))]
    public void Merge_procedure_never_deletes_by_absence(string tableName) =>
        Assert.DoesNotContain("NOT MATCHED BY SOURCE",
            TvpParser.ProcedureBody(ProcSql, Table(tableName).MergeProc), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Every merge must stamp true UTC rather than leaning on the table's
    /// <c>sysdatetime()</c> default, which is server LOCAL time in a column named
    /// <c>...Utc</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllTableNames))]
    public void Merge_procedure_stamps_modified_at_utc_explicitly(string tableName) =>
        Assert.Contains("SYSUTCDATETIME()",
            TvpParser.ProcedureBody(ProcSql, Table(tableName).MergeProc), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The drop script must cover everything 001-003 create, or a rebuild leaves orphans
    /// behind — and a table type cannot be ALTERed, so a stale type silently keeps the
    /// old column list.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllTableNames))]
    public void Drop_script_covers_every_object(string tableName)
    {
        var table = Table(tableName);

        Assert.Contains($"DROP PROCEDURE IF EXISTS {table.MergeProc}", DropSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"DROP TYPE IF EXISTS {table.TvpType}", DropSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"DROP TABLE IF EXISTS {table.TableName}", DropSql, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------- descriptor vs the DataTable

    [Theory]
    [MemberData(nameof(AllTableNames))]
    public void DataTable_columns_match_the_descriptor_by_name_order_and_clr_type(string tableName)
    {
        var table = Table(tableName);
        var built = new ProbeSink(table).Build(Array.Empty<GenscapeRow>());

        Assert.Equal(table.Columns.Count, built.Columns.Count);

        for (var i = 0; i < table.Columns.Count; i++)
        {
            Assert.Equal(table.Columns[i].Name, built.Columns[i].ColumnName);
            Assert.Equal(table.Columns[i].ClrType, built.Columns[i].DataType);
        }
    }

    /// <summary>
    /// A row whose length disagrees with the descriptor is a BUG, and it must throw
    /// rather than reach the server as silently shifted columns.
    /// </summary>
    [Fact]
    public void A_row_of_the_wrong_width_throws_rather_than_shifting_columns()
    {
        var sink = new ProbeSink(GenscapeDescriptors.CrudeTransportationWeeklyTable);

        var ex = Assert.Throws<InvalidOperationException>(
            () => sink.Build(new[] { new GenscapeRow(new object[] { DateTime.Today, (short)2026 }) }));

        Assert.Contains("declares", ex.Message);
    }

    [Fact]
    public void Rows_land_in_descriptor_order()
    {
        var table = GenscapeDescriptors.CrudeTransportationWeeklyTable;

        var built = new ProbeSink(table).Build(new[]
        {
            new GenscapeRow(new object[]
            {
                new DateTime(2026, 8, 28), (short)2026, (byte)35, "Cushing to Gulf Coast", "Pipeline", 1467871d
            })
        });

        var row = built.Rows[0];
        Assert.Equal(new DateTime(2026, 8, 28), row["ReportDate"]);
        Assert.Equal((short)2026, row["Year"]);
        Assert.Equal((byte)35, row["Week"]);
        Assert.Equal("Cushing to Gulf Coast", row["Region"]);
        Assert.Equal("Pipeline", row["Type"]);
        Assert.Equal(1467871d, row["FlowBPD"]);
    }

    [Fact]
    public void DBNull_survives_into_the_DataTable()
    {
        var table = GenscapeDescriptors.CrudeTransportationWeeklyTable;

        var built = new ProbeSink(table).Build(new[]
        {
            new GenscapeRow(new object[]
            {
                new DateTime(2026, 8, 28), (short)2026, (byte)35, "X", "Rail", DBNull.Value
            })
        });

        Assert.Equal(DBNull.Value, built.Rows[0]["FlowBPD"]);
    }

    [Fact]
    public async Task An_empty_write_never_touches_the_database()
    {
        var sink = new GenscapeTvpSink(
            GenscapeDescriptors.CrudeStorageWeeklyTable,
            "Server=(local);Database=Genscape;Integrated Security=SSPI;Connect Timeout=1;",
            NullLogger.Instance);

        // No connection is attempted for zero rows, so this returns rather than failing
        // to reach a server that is not there.
        Assert.Equal(0, await sink.WriteAsync(Array.Empty<GenscapeRow>(), CancellationToken.None));
    }

    // -------------------------------------------------------------- the helpers

    private static int IndexOfColumn(string body, string columnName, int from)
    {
        var bare = body.IndexOf(columnName, from, StringComparison.OrdinalIgnoreCase);
        var bracketed = body.IndexOf($"[{columnName}]", from, StringComparison.OrdinalIgnoreCase);

        if (bare < 0) return bracketed;
        if (bracketed < 0) return bare;
        return Math.Min(bare, bracketed);
    }

    /// <summary>
    /// Collapses whitespace and strips brackets so <c>VARCHAR(50)</c> and
    /// <c>varchar (50)</c> compare equal.
    /// </summary>
    private static string NormalizeType(string sqlType) =>
        Regex.Replace(sqlType, @"\s+", string.Empty)
             .Replace("[", string.Empty)
             .Replace("]", string.Empty)
             .ToUpperInvariant();
}
