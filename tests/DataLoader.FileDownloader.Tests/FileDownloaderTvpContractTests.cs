using System.Data;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DataLoader.FileDownloader.Tests;

/// <summary>
/// The TVP contract gate.
///
/// <para>A table-valued parameter binds BY POSITION. If the DataTable
/// <see cref="FileDownloaderFileSink"/> builds and the <c>CREATE TYPE</c> in
/// <c>sql/FileDownloader/002</c> disagree on column order, SQL Server does not complain —
/// it writes a destination path into the status column and a size into a date. These
/// tests parse the REAL .sql file rather than restating its columns, so editing one side
/// without the other fails the build.</para>
/// </summary>
public sealed class FileDownloaderTvpContractTests
{
    private const string TvpTypeName = "download.FileDownloaderFileTvp";
    private const string TableName = "download.FileDownloader_File";

    private static readonly string TvpSql = File.ReadAllText(RepoPaths.TvpScript);
    private static readonly string SchemaSql = File.ReadAllText(RepoPaths.SchemaScript);

    private static DataTable BuildTable(IReadOnlyList<FileDownloadRow> rows)
    {
        var sink = new FileDownloaderFileSink(
            Options.Create(new FileDownloaderSettings { ConnectionString = "unused" }),
            NullLogger<FileDownloaderFileSink>.Instance);

        // BuildTable is pure (no DB) but protected on a sealed type — the CWG/Platts/
        // AGSI posture is to reach it by reflection rather than widen the API for tests.
        var method = typeof(FileDownloaderFileSink)
            .GetMethod("BuildTable", BindingFlags.Instance | BindingFlags.NonPublic)!;

        return (DataTable)method.Invoke(sink, new object[] { rows })!;
    }

    private static FileDownloadRow SampleRow() => new()
    {
        SourceName = "Platts",
        Mode = nameof(FileAcquisitionMode.Sftp),
        FileName = "Pipeline Flows_2026_Nov.csv",
        DestinationPath = @"\\arm.local\data\AssetRisk\Models\Platts\PipelineFlows",
        FileDate = new DateTime(2026, 11, 4),
        SizeBytes = 4096,
        Status = nameof(FileDownloadStatus.Downloaded),
        DownloadedAtUtc = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc),
        InfoId = null,
        SourceLocator = "/outbound/Pipeline Flows_2026_Nov.csv"
    };

    [Fact]
    public void BuildTable_matches_the_Tvp_by_name_and_order()
    {
        var sqlColumns = TvpParser.Parse(TvpSql, TvpTypeName);
        Assert.True(sqlColumns.Count > 0, $"No CREATE TYPE found for {TvpTypeName} in 002.");

        var table = BuildTable(new[] { SampleRow() });

        Assert.Equal(sqlColumns.Count, table.Columns.Count);
        for (var i = 0; i < sqlColumns.Count; i++)
        {
            Assert.Equal(sqlColumns[i].Name, table.Columns[i].ColumnName);
        }
    }

    [Theory]
    [InlineData("VARCHAR", typeof(string))]
    [InlineData("NVARCHAR", typeof(string))]
    [InlineData("DATE", typeof(DateTime))]
    [InlineData("DATETIME2", typeof(DateTime))]
    [InlineData("BIGINT", typeof(long))]
    [InlineData("INT", typeof(int))]
    public void TypeMap_is_the_one_this_test_asserts_with(string sqlType, Type clrType)
    {
        // Pins the mapping the next test relies on, so a wrong expectation there is a
        // failing assertion rather than a silently permissive one.
        Assert.Equal(clrType, ClrTypeFor(sqlType));
    }

    [Fact]
    public void BuildTable_matches_the_Tvp_by_type()
    {
        var sqlColumns = TvpParser.Parse(TvpSql, TvpTypeName);
        var table = BuildTable(new[] { SampleRow() });

        for (var i = 0; i < sqlColumns.Count; i++)
        {
            var baseType = sqlColumns[i].SqlType.Split('(')[0].Trim();
            Assert.Equal(ClrTypeFor(baseType), table.Columns[i].DataType);
        }
    }

    /// <summary>
    /// The TVP must not carry <c>FileId</c> (IDENTITY) or <c>ModifiedAtUtc</c>
    /// (DB-stamped default). Either one would shift every following column.
    /// </summary>
    [Fact]
    public void Tvp_never_carries_identity_or_db_stamped_columns()
    {
        var names = TvpParser.Parse(TvpSql, TvpTypeName).Select(c => c.Name).ToList();

        Assert.DoesNotContain("FileId", names);
        Assert.DoesNotContain("ModifiedAtUtc", names);
    }

    /// <summary>
    /// Every TVP column must exist on the target table with the same type — the merge
    /// procedure's INSERT names them one-for-one.
    /// </summary>
    [Fact]
    public void Every_Tvp_column_exists_on_the_target_table_with_the_same_type()
    {
        var tvp = TvpParser.Parse(TvpSql, TvpTypeName);
        var table = TvpParser.ParseTable(SchemaSql, TableName)
            .ToDictionary(c => c.Name, c => c.SqlType, StringComparer.OrdinalIgnoreCase);

        foreach (var column in tvp)
        {
            Assert.True(table.ContainsKey(column.Name),
                $"TVP column '{column.Name}' has no counterpart in {TableName} (001).");
            Assert.True(
                string.Equals(table[column.Name], column.SqlType, StringComparison.OrdinalIgnoreCase),
                $"'{column.Name}': 001 says {table[column.Name]}, 002 says {column.SqlType}.");
        }
    }

    /// <summary>
    /// ⚠ Mako is a shared production database with 43 schemas. Every statement in these
    /// scripts must be download.-qualified — an unqualified CREATE/ALTER/DROP would land
    /// in whatever schema the connection defaults to.
    /// </summary>
    [Fact]
    public void Every_script_object_is_download_qualified()
    {
        foreach (var path in new[] { RepoPaths.SchemaScript, RepoPaths.TvpScript, RepoPaths.ProceduresScript })
        {
            var code = TvpParser.StripComments(File.ReadAllText(path));

            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(
                         code,
                         @"\b(?:CREATE|ALTER|DROP)\s+(?:TABLE|TYPE|PROCEDURE|VIEW)\s+(?<name>\[?[A-Za-z0-9_\.\[\]]+)",
                         System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                var name = m.Groups["name"].Value.Replace("[", string.Empty).Replace("]", string.Empty);
                Assert.True(name.StartsWith("download.", StringComparison.OrdinalIgnoreCase),
                    $"{Path.GetFileName(path)}: '{name}' is not download.-qualified.");
            }
        }
    }

    /// <summary>
    /// Every script must declare its target database. The repo rule is absolute, and this
    /// loader's database (<c>Mako</c>) is NOT the folder name — these scripts are run by
    /// hand in SSMS against a server holding every loader's database side by side.
    /// </summary>
    [Fact]
    public void Every_script_declares_USE_Mako()
    {
        foreach (var path in new[] { RepoPaths.SchemaScript, RepoPaths.TvpScript, RepoPaths.ProceduresScript })
        {
            var code = TvpParser.StripComments(File.ReadAllText(path));
            Assert.True(
                System.Text.RegularExpressions.Regex.IsMatch(
                    code, @"^\s*USE\s+\[?Mako\]?\s*;?\s*$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                    System.Text.RegularExpressions.RegexOptions.Multiline),
                $"{Path.GetFileName(path)} does not declare USE Mako.");
        }
    }

    private static Type ClrTypeFor(string sqlType) => sqlType.ToUpperInvariant() switch
    {
        "VARCHAR" or "NVARCHAR" or "CHAR" or "NCHAR" => typeof(string),
        "DATE" or "DATETIME2" or "DATETIME" => typeof(DateTime),
        "BIGINT" => typeof(long),
        "INT" => typeof(int),
        "BIT" => typeof(bool),
        _ => throw new InvalidOperationException($"Unmapped SQL type '{sqlType}' — extend the map.")
    };
}
