using System.Text.RegularExpressions;
using Xunit;

namespace DataLoader.Criterion.Tests;

/// <summary>
/// The TVP contract gate.
///
/// <para>
/// A table-valued parameter binds BY POSITION. If the DataTable the sink builds and
/// the <c>CREATE TYPE</c> in <c>sql/Criterion/002</c> disagree on column order or
/// type, SQL Server does not complain — it writes every row into the wrong columns.
/// The repo's <c>tvp-contract-check</c> skill exists because that failure is silent.
/// </para>
/// <para>
/// These tests close the loop by PARSING THE REAL .sql FILES and comparing them to
/// <see cref="CriterionDescriptors"/>, which is also what the sink builds its
/// DataTable from AND what the reader generates its SELECT from. Editing one side
/// without the other fails the build, so the contract is enforced continuously rather
/// than at review time.
/// </para>
/// </summary>
public sealed class CriterionTvpContractTests
{
    private static readonly string TvpSql = File.ReadAllText(RepoPaths.TvpScript);
    private static readonly string SchemaSql = File.ReadAllText(RepoPaths.SchemaScript);
    private static readonly string ProcSql = File.ReadAllText(RepoPaths.ProceduresScript);
    private static readonly string DropSql = File.ReadAllText(RepoPaths.DropScript);

    public static TheoryData<string> AllTableNames()
    {
        var data = new TheoryData<string>();
        foreach (var table in CriterionDescriptors.AllTables) data.Add(table.TableName);
        return data;
    }

    private static CriterionTableDescriptor Table(string tableName) =>
        CriterionDescriptors.AllTables.Single(t => t.TableName == tableName);

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
    /// <c>ModifiedAtUtc</c> and <c>DateCreated</c> are DB-stamped defaults. A TVP that
    /// carried them would either overwrite the server's value or shift every
    /// following column.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllTableNames))]
    public void Tvp_never_carries_db_stamped_columns(string tableName)
    {
        var table = Table(tableName);

        Assert.DoesNotContain(table.Columns, c =>
            c.Name.Equals("ModifiedAtUtc", StringComparison.OrdinalIgnoreCase) ||
            c.Name.Equals("DateCreated", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// ⚠ THE COLUMNS WITH NO SOURCE. Four target columns have no PostgreSQL column
    /// behind them, and each must stay out of its TVP or the merge would clear a value
    /// somebody set by hand (or, for the IDENTITY columns, fail outright):
    ///
    /// <list type="bullet">
    ///   <item><c>arm.Financial_Metadata.Enabled</c> — ARM curation flag.</item>
    ///   <item><c>arm.Pipelines_NominationPoint.IsLatest</c> — ARM curation flag.</item>
    ///   <item><c>arm.Pipelines_Metadata.MappingId</c> — server-side IDENTITY.</item>
    ///   <item><c>arm.Pipelines_Pointflows.Id</c> — server-side IDENTITY.</item>
    /// </list>
    ///
    /// <c>arm.Pipelines_Metadata.Point</c> is a fifth: it is DERIVED in the proc from
    /// the Latitude/Longitude TVP columns rather than shipped over the wire.
    /// </summary>
    [Theory]
    [InlineData("arm.Financial_Metadata", "Enabled")]
    [InlineData("arm.Pipelines_NominationPoint", "IsLatest")]
    [InlineData("arm.Pipelines_Metadata", "MappingId")]
    [InlineData("arm.Pipelines_Metadata", "Point")]
    [InlineData("arm.Pipelines_Pointflows", "Id")]
    public void Columns_without_a_source_are_absent_from_the_tvp(string tableName, string columnName)
    {
        var table = Table(tableName);

        Assert.DoesNotContain(table.Columns, c =>
            c.Name.Equals(columnName, StringComparison.OrdinalIgnoreCase));

        // ...and the .sql agrees, so the two cannot drift apart.
        var sqlColumns = TvpParser.Parse(TvpSql, table.TvpType);
        Assert.DoesNotContain(sqlColumns, c =>
            c.Name.Equals(columnName, StringComparison.OrdinalIgnoreCase));

        // ...but the column DOES exist on the target table — the point is that it is
        // unsourced, not that it was forgotten.
        var body = TvpParser.CreateTableBody(SchemaSql, tableName);
        Assert.Contains(columnName, body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The merge procs must never write the unsourced columns. This parses the actual
    /// proc body, so adding one to an INSERT or UPDATE list fails the build.
    /// </summary>
    [Theory]
    [InlineData("arm.usp_BulkMergeFinancialMetadata", "Enabled")]
    [InlineData("arm.usp_BulkMergePipelinesNominationPoint", "IsLatest")]
    [InlineData("arm.usp_BulkMergePipelinesMetadata", "MappingId")]
    [InlineData("arm.usp_BulkMergePipelinesPointflows", "Id")]
    public void Merge_procedures_never_write_the_unsourced_columns(string procName, string columnName)
    {
        var body = TvpParser.ProcedureBody(ProcSql, procName);

        // Strip comments first: these column names are discussed at length in the
        // proc headers, and a substring match would trip over the prose.
        var code = TvpParser.StripComments(body);

        Assert.False(
            Regex.IsMatch(code, $@"\b{Regex.Escape(columnName)}\b\s*=", RegexOptions.IgnoreCase),
            $"{procName} assigns to '{columnName}', which has no source column and must never be written.");

        // "Id" is a substring of many identifiers (MetadataId, CycleId...), so the
        // INSERT-list check uses a delimiter-anchored pattern rather than Contains.
        Assert.False(
            Regex.IsMatch(code, $@"[(,]\s*{Regex.Escape(columnName)}\s*[,)]", RegexOptions.IgnoreCase),
            $"{procName} names '{columnName}' in a column list, which has no source column.");
    }

    /// <summary>Every type declared in 002 must belong to a table — no orphans left by an edit.</summary>
    [Fact]
    public void Every_type_in_the_script_belongs_to_a_table()
    {
        var declared = TvpParser.DeclaredTypeNames(TvpSql);
        var expected = CriterionDescriptors.AllTables.Select(t => t.TvpType)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Equal(CriterionDescriptors.AllTables.Count, declared.Count);
        foreach (var name in declared)
            Assert.True(expected.Contains(name), $"002 declares {name}, which no table descriptor uses.");
    }

    /// <summary>Every merge procedure the descriptors name must exist in 003.</summary>
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

    /// <summary>Every target table the descriptors name must exist in 001.</summary>
    [Theory]
    [MemberData(nameof(AllTableNames))]
    public void Target_table_is_created_in_the_schema_script(string tableName)
    {
        Assert.Contains($"CREATE TABLE {tableName}", SchemaSql, StringComparison.OrdinalIgnoreCase);
    }

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
            // ([Status], [Date], [Value], [Version], [Point], [Enabled]).
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
        var tables = CriterionDescriptors.AllTables;

        Assert.Equal(tables.Count, tables.Select(t => t.TableName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(tables.Count, tables.Select(t => t.TvpType).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(tables.Count, tables.Select(t => t.MergeProc).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    /// <summary>
    /// Every merge proc must de-duplicate its source. The source genuinely ships
    /// duplicate keys (118 exact duplicates in a 30-day nomination window), and MERGE
    /// errors the whole batch on a duplicated source key.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllTableNames))]
    public void Merge_procedure_deduplicates_its_source(string tableName)
    {
        var body = TvpParser.ProcedureBody(ProcSql, Table(tableName).MergeProc);

        Assert.Contains("ROW_NUMBER() OVER", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("rn = 1", body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// No merge may delete by absence. Each work unit carries ONE slice — one gas day,
    /// one post date, one page of series — so a <c>WHEN NOT MATCHED BY SOURCE THEN
    /// DELETE</c> would wipe every other slice's rows on every unit.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllTableNames))]
    public void Merge_procedure_never_deletes_by_absence(string tableName)
    {
        var body = TvpParser.ProcedureBody(ProcSql, Table(tableName).MergeProc);

        Assert.DoesNotContain("NOT MATCHED BY SOURCE", body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// ⚠ The Pointflows merge must key on the NATURAL key, not the declared PK.
    /// <c>Id</c> is IDENTITY: no inbound row can carry one, so matching on it would make
    /// every run insert a fresh duplicate rather than update. Pinned so it cannot be
    /// "fixed" back to the PK by someone reading 001 alone.
    /// </summary>
    [Fact]
    public void Pointflows_merges_on_the_natural_key_not_the_identity_pk()
    {
        var body = TvpParser.StripComments(
            TvpParser.ProcedureBody(ProcSql, "arm.usp_BulkMergePipelinesPointflows"));

        Assert.Matches(new Regex(@"ON\s+tgt\.MetadataId\s*=\s*s\.MetadataId", RegexOptions.IgnoreCase), body);
        Assert.Matches(new Regex(@"AND\s+tgt\.EffGasDay\s*=\s*s\.EffGasDay", RegexOptions.IgnoreCase), body);
        Assert.Matches(new Regex(@"AND\s+tgt\.CycleId\s*=\s*s\.CycleId", RegexOptions.IgnoreCase), body);

        Assert.DoesNotMatch(new Regex(@"tgt\.Id\s*=", RegexOptions.IgnoreCase), body);

        // ...and 001 must enforce that the natural key really is unique, or the merge
        // would eventually hit "attempted to update the same row more than once".
        Assert.Contains("UQ_ARM_Pointflows_NaturalKey", SchemaSql, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// ⚠ <c>geography::Point</c> THROWS on an out-of-range coordinate, which would abort
    /// the whole batch. The range guard is load-bearing, not decorative.
    /// </summary>
    [Fact]
    public void Pipelines_metadata_merge_guards_the_geography_construction()
    {
        var body = TvpParser.StripComments(
            TvpParser.ProcedureBody(ProcSql, "arm.usp_BulkMergePipelinesMetadata"));

        Assert.Contains("geography::Point", body, StringComparison.OrdinalIgnoreCase);
        Assert.Matches(new Regex(@"Latitude\s+BETWEEN\s+-90\s+AND\s+90", RegexOptions.IgnoreCase), body);
        Assert.Matches(new Regex(@"Longitude\s+BETWEEN\s+-180\s+AND\s+180", RegexOptions.IgnoreCase), body);

        // SRID 4326 (WGS 84), matching arm.PlantPoint in the IIR loader.
        Assert.Contains("4326", body);
    }

    /// <summary>
    /// Every merge must stamp true UTC rather than leaning on the table's
    /// <c>sysdatetime()</c> default, which is server LOCAL time in a column named
    /// <c>...Utc</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllTableNames))]
    public void Merge_procedure_stamps_modified_at_utc_explicitly(string tableName)
    {
        var body = TvpParser.ProcedureBody(ProcSql, Table(tableName).MergeProc);

        Assert.Contains("SYSUTCDATETIME()", body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The drop script must cover everything 001-003 create, or a rebuild leaves
    /// orphans behind — and a table type cannot be ALTERed, so a stale type silently
    /// keeps the old column list.
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

    /// <summary>Finds a column name in a CREATE TABLE body, bracketed or bare.</summary>
    private static int IndexOfColumn(string body, string columnName, int from)
    {
        var bare = body.IndexOf(columnName, from, StringComparison.OrdinalIgnoreCase);
        var bracketed = body.IndexOf($"[{columnName}]", from, StringComparison.OrdinalIgnoreCase);

        if (bare < 0) return bracketed;
        if (bracketed < 0) return bare;
        return Math.Min(bare, bracketed);
    }

    /// <summary>
    /// Collapses whitespace and strips brackets so <c>VARCHAR(100)</c> and
    /// <c>varchar (100)</c> compare equal.
    /// </summary>
    private static string NormalizeType(string sqlType) =>
        Regex.Replace(sqlType, @"\s+", string.Empty)
             .Replace("[", string.Empty)
             .Replace("]", string.Empty)
             .ToUpperInvariant();
}

/// <summary>
/// Minimal T-SQL slicing, enough to pull a <c>CREATE TYPE</c> / <c>CREATE TABLE</c>
/// body or one procedure out of a script. Deliberately not a real parser — it only
/// has to handle the shapes this repo's own scripts use.
/// </summary>
internal static class TvpParser
{
    internal readonly record struct Column(string Name, string SqlType, bool NotNull);

    public static IReadOnlyList<Column> Parse(string sql, string typeName)
    {
        var header = new Regex(
            $@"CREATE\s+TYPE\s+{Regex.Escape(typeName)}\s+AS\s+TABLE\s*\(",
            RegexOptions.IgnoreCase);

        var match = header.Match(sql);
        if (!match.Success) return Array.Empty<Column>();

        var open = match.Index + match.Length - 1;   // index of the '('
        return SplitTopLevel(ReadBalanced(sql, open)).Select(ParseColumn).ToList();
    }

    public static IReadOnlyList<string> DeclaredTypeNames(string sql) =>
        Regex.Matches(sql, @"CREATE\s+TYPE\s+([A-Za-z0-9_\.\[\]]+)\s+AS\s+TABLE", RegexOptions.IgnoreCase)
             .Select(m => m.Groups[1].Value.Replace("[", string.Empty).Replace("]", string.Empty))
             .ToList();

    /// <summary>Body of a <c>CREATE TABLE</c>, for order checks against the descriptor.</summary>
    public static string CreateTableBody(string sql, string tableName)
    {
        var header = new Regex($@"CREATE\s+TABLE\s+{Regex.Escape(tableName)}\s*\(", RegexOptions.IgnoreCase);
        var match = header.Match(sql);

        if (!match.Success)
            throw new InvalidOperationException($"No CREATE TABLE found for {tableName}.");

        return ReadBalanced(sql, match.Index + match.Length - 1);
    }

    /// <summary>Text of one procedure, from its CREATE to the next one (or end of file).</summary>
    public static string ProcedureBody(string sql, string procName)
    {
        var header = new Regex(
            $@"CREATE\s+OR\s+ALTER\s+PROCEDURE\s+{Regex.Escape(procName)}\b",
            RegexOptions.IgnoreCase);

        var match = header.Match(sql);
        if (!match.Success)
            throw new InvalidOperationException($"No procedure found named {procName}.");

        var next = Regex.Match(
            sql[(match.Index + match.Length)..],
            @"CREATE\s+OR\s+ALTER\s+PROCEDURE\s+", RegexOptions.IgnoreCase);

        var length = next.Success ? next.Index : sql.Length - match.Index - match.Length;

        return sql.Substring(match.Index, match.Length + length);
    }

    /// <summary>
    /// Removes <c>--</c> line comments and <c>/* */</c> blocks. The procs in 003 discuss
    /// their own column names at length, so a Contains-style assertion has to look at
    /// code rather than prose.
    /// </summary>
    public static string StripComments(string sql)
    {
        var withoutBlocks = Regex.Replace(sql, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        return Regex.Replace(withoutBlocks, @"--[^\r\n]*", " ");
    }

    /// <summary>Content between <paramref name="openIndex"/>'s paren and its match.</summary>
    private static string ReadBalanced(string sql, int openIndex)
    {
        var depth = 0;
        for (var i = openIndex; i < sql.Length; i++)
        {
            if (sql[i] == '(') depth++;
            else if (sql[i] == ')')
            {
                depth--;
                if (depth == 0) return sql[(openIndex + 1)..i];
            }
        }

        throw new InvalidOperationException("Unbalanced parentheses in body.");
    }

    /// <summary>Split on commas at depth 0, so DECIMAL(13,10) stays intact.</summary>
    private static IEnumerable<string> SplitTopLevel(string body)
    {
        var depth = 0;
        var start = 0;

        for (var i = 0; i < body.Length; i++)
        {
            if (body[i] == '(') depth++;
            else if (body[i] == ')') depth--;
            else if (body[i] == ',' && depth == 0)
            {
                yield return body[start..i];
                start = i + 1;
            }
        }

        if (start < body.Length) yield return body[start..];
    }

    private static Column ParseColumn(string declaration)
    {
        var text = StripComments(declaration).Trim();

        var notNull = Regex.IsMatch(text, @"\bNOT\s+NULL\b", RegexOptions.IgnoreCase);

        // Strip the nullability tail so what remains is "<name> <type>".
        text = Regex.Replace(text, @"\b(NOT\s+)?NULL\b", string.Empty, RegexOptions.IgnoreCase).Trim();

        var match = Regex.Match(text, @"^\[?(?<name>[A-Za-z0-9_]+)\]?\s+(?<type>.+)$", RegexOptions.Singleline);
        if (!match.Success)
            throw new InvalidOperationException($"Could not parse TVP column declaration: '{declaration}'.");

        return new Column(
            match.Groups["name"].Value.Trim(),
            Regex.Replace(match.Groups["type"].Value, @"\s+", " ").Trim(),
            notNull);
    }
}

/// <summary>Locates <c>sql/Criterion</c> by walking up from the test binaries.</summary>
internal static class RepoPaths
{
    public static string SqlDirectory { get; } = FindSqlDirectory();

    public static string SchemaScript => Path.Combine(SqlDirectory, "001_CreateCriterionSchema.sql");
    public static string TvpScript => Path.Combine(SqlDirectory, "002_CreateCriterionTvpTypes.sql");
    public static string ProceduresScript => Path.Combine(SqlDirectory, "003_CreateCriterionProcedures.sql");
    public static string DropScript => Path.Combine(SqlDirectory, "999_DropCriterionObjects.sql");

    private static string FindSqlDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "sql", "Criterion");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate sql/Criterion by walking up from '{AppContext.BaseDirectory}'.");
    }
}
