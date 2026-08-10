using System.Data;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DataLoader.StormVista.Tests;

/// <summary>
/// The SQL sinks' <c>BuildTable</c> — the C#-side of the TVP contract. Column order
/// and types must match <c>002_CreateStormVistaTvpTypes.sql</c> EXACTLY. BuildTable is
/// pure (no DB), but it is protected on a sealed type, so it is invoked via reflection.
/// </summary>
public class SinkTests
{
    private static DataTable BuildTable(object sink, object rows)
    {
        var method = sink.GetType().GetMethod("BuildTable", BindingFlags.Instance | BindingFlags.NonPublic)
                     ?? throw new InvalidOperationException("BuildTable not found");
        return (DataTable)method.Invoke(sink, new[] { rows })!;
    }

    private static DailyWddSqlSink NewDailySink() =>
        new(Options.Create(new StormVistaSettings { ConnectionString = "unused" }),
            NullLogger<DailyWddSqlSink>.Instance);

    private static RegionalWddSqlSink NewRegionalSink() =>
        new(Options.Create(new StormVistaSettings { ConnectionString = "unused" }),
            NullLogger<RegionalWddSqlSink>.Instance);

    // ------------------------------------------------------------------ Daily TVP

    [Fact]
    public void DailySink_BuildTable_HasExactTvpColumnOrderAndTypes()
    {
        var row = new DailyWddRow
        {
            FileLogId = 777, ValidDate = new DateOnly(2024, 8, 4), FlagCode = 1, Value = 15.5m
        };

        var table = BuildTable(NewDailySink(), new List<DailyWddRow> { row });

        // dbo.DailyWddTvp: (FileLogId, ValidDate, FlagCode, Value).
        Assert.Equal(new[] { "FileLogId", "ValidDate", "FlagCode", "Value" },
            table.Columns.Cast<DataColumn>().Select(c => c.ColumnName).ToArray());
        Assert.Equal(new[] { typeof(int), typeof(DateTime), typeof(int), typeof(decimal) },
            table.Columns.Cast<DataColumn>().Select(c => c.DataType).ToArray());

        var r = table.Rows[0];
        Assert.Equal(777, r["FileLogId"]);
        Assert.Equal(new DateTime(2024, 8, 4), r["ValidDate"]); // DateOnly -> midnight DateTime
        Assert.Equal(1, r["FlagCode"]);
        Assert.Equal(15.5m, r["Value"]);
    }

    [Fact]
    public void DailySink_BuildTable_NullValue_IsDbNull()
    {
        var row = new DailyWddRow { FileLogId = 1, ValidDate = new DateOnly(2024, 8, 4), FlagCode = 0, Value = null };

        var table = BuildTable(NewDailySink(), new List<DailyWddRow> { row });

        Assert.Equal(DBNull.Value, table.Rows[0]["Value"]);
    }

    // ------------------------------------------------------------------ Regional TVP

    [Fact]
    public void RegionalSink_BuildTable_HasExactTvpColumnOrderAndTypes()
    {
        var row = new RegionalWddRow
        {
            FileLogId = 55, RegionName = "West", ValidDate = new DateOnly(2024, 8, 4), Value = 12.96m
        };

        var table = BuildTable(NewRegionalSink(), new List<RegionalWddRow> { row });

        // dbo.RegionalWddTvp: (FileLogId, RegionName, ValidDate, Value).
        Assert.Equal(new[] { "FileLogId", "RegionName", "ValidDate", "Value" },
            table.Columns.Cast<DataColumn>().Select(c => c.ColumnName).ToArray());
        Assert.Equal(new[] { typeof(int), typeof(string), typeof(DateTime), typeof(decimal) },
            table.Columns.Cast<DataColumn>().Select(c => c.DataType).ToArray());

        var r = table.Rows[0];
        Assert.Equal(55, r["FileLogId"]);
        Assert.Equal("West", r["RegionName"]);
        Assert.Equal(new DateTime(2024, 8, 4), r["ValidDate"]);
        Assert.Equal(12.96m, r["Value"]);
    }

    [Fact]
    public void RegionalSink_BuildTable_NullValue_IsDbNull()
    {
        var row = new RegionalWddRow { FileLogId = 1, RegionName = "East", ValidDate = new DateOnly(2024, 8, 4), Value = null };

        var table = BuildTable(NewRegionalSink(), new List<RegionalWddRow> { row });

        Assert.Equal(DBNull.Value, table.Rows[0]["Value"]);
    }
}
