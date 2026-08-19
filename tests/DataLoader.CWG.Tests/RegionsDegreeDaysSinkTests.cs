using System.Data;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DataLoader.CWG.Tests;

/// <summary>
/// The three new degree-day sinks' <c>BuildTable</c> — the C#-side of the TVP
/// contract. Column ORDER, COUNT and types must match <c>sql/CWG/002</c> exactly
/// (<c>FileLogId</c> first; for 5/9region the tail is IsForecast then the three
/// weights; ISO ends at IsForecast). Each sink de-dups a batch on its merge key
/// <c>(RunDate, Dates, RegionName)</c>, last-wins. BuildTable is pure (no DB) but
/// protected on a sealed type, so it is invoked via reflection (SinkTests posture).
/// </summary>
public class RegionsDegreeDaysSinkTests
{
    private static DataTable BuildTable(object sink, object rows)
    {
        var method = sink.GetType().GetMethod("BuildTable", BindingFlags.Instance | BindingFlags.NonPublic)
                     ?? throw new InvalidOperationException("BuildTable not found");
        return (DataTable)method.Invoke(sink, new[] { rows })!;
    }

    private static IOptions<CwgSettings> Opt() =>
        Options.Create(new CwgSettings { ConnectionString = "unused" });

    private static string[] Names(DataTable t) => t.Columns.Cast<DataColumn>().Select(c => c.ColumnName).ToArray();
    private static Type[] Types(DataTable t) => t.Columns.Cast<DataColumn>().Select(c => c.DataType).ToArray();

    private static readonly DateOnly Run = new(2026, 8, 11);
    private static readonly DateOnly Day = new(2026, 8, 4);

    private static Regions5DegreeDaysRow Row5(int fileLogId, string region, decimal ngHdd) => new()
    {
        FileLogId = fileLogId, RunDate = Run, Dates = Day, RegionName = region,
        NgHdd = ngHdd, NgHdd30y = 0.1m, NgHdd10y = 0.02m, NgHddLastY = 0.19m,
        PopCdd = 9.83m, PopCdd30y = 6.77m, PopCdd10y = 8.19m, PopCddLastY = 3.95m,
        ElecCdd = 9.01m, ElecCdd30y = 6.35m, ElecCdd10y = 7.77m, ElecCddLastY = 3.53m,
        IsForecast = false, GasWeight = 0.1715m, ElctWeight = 0.1100m, PopWeight = 0.1564m
    };

    private static Regions9DegreeDaysRow Row9(int fileLogId, string region, decimal ngHdd) => new()
    {
        FileLogId = fileLogId, RunDate = Run, Dates = Day, RegionName = region,
        NgHdd = ngHdd, NgHdd30y = 0.2m, NgHdd10y = 0.15m, NgHddLastY = 0.01m,
        PopCdd = 6.72m, PopCdd30y = 6.65m, PopCdd10y = 7.81m, PopCddLastY = 5.16m,
        ElecCdd = 6.71m, ElecCdd30y = 6.66m, ElecCdd10y = 7.82m, ElecCddLastY = 5.16m,
        IsForecast = false, GasWeight = 0.0396m, ElctWeight = 0.0349m, PopWeight = 0.0459m
    };

    private static ISODegreeDaysRow RowIso(int fileLogId, string region, decimal popHdd) => new()
    {
        FileLogId = fileLogId, RunDate = Run, Dates = Day, RegionName = region,
        PopHdd = popHdd, PopHdd30y = 0.0175m, PopHdd10y = 0m, PopHddLastY = 0m,
        PopCdd = 23.96m, PopCdd30y = 21.27m, PopCdd10y = 22.53m, PopCddLastY = 17.21m,
        IsForecast = false
    };

    private static readonly string[] Region5And9Columns =
    {
        "FileLogId", "RunDate", "Dates", "RegionName",
        "NgHdd", "NgHdd30y", "NgHdd10y", "NgHddLastY",
        "PopCdd", "PopCdd30y", "PopCdd10y", "PopCddLastY",
        "ElecCdd", "ElecCdd30y", "ElecCdd10y", "ElecCddLastY",
        "IsForecast", "GasWeight", "ElctWeight", "PopWeight"
    };

    private static readonly Type[] Region5And9Types =
    {
        typeof(int), typeof(DateTime), typeof(DateTime), typeof(string),
        typeof(decimal), typeof(decimal), typeof(decimal), typeof(decimal),
        typeof(decimal), typeof(decimal), typeof(decimal), typeof(decimal),
        typeof(decimal), typeof(decimal), typeof(decimal), typeof(decimal),
        typeof(bool), typeof(decimal), typeof(decimal), typeof(decimal)
    };

    // ---------------------------------------------------------------- Regions5

    [Fact]
    public void Regions5Sink_BuildTable_Matches20ColTvpOrderAndTypes_FileLogIdFirst_WeightsLast()
    {
        var sink = new Regions5DegreeDaysSqlSink(Opt(), NullLogger<Regions5DegreeDaysSqlSink>.Instance);

        var t = BuildTable(sink, new List<Regions5DegreeDaysRow> { Row5(5, "Pacific", 0.0071m) });

        Assert.Equal(Region5And9Columns, Names(t));
        Assert.Equal(Region5And9Types, Types(t));
        Assert.Equal("FileLogId", Names(t)[0]);
        Assert.Equal("PopWeight", Names(t)[^1]);  // tail is IsForecast, GasWeight, ElctWeight, PopWeight

        var r = t.Rows[0];
        Assert.Equal(5, r["FileLogId"]);
        Assert.Equal(new DateTime(2026, 8, 11), r["RunDate"]);   // DateOnly → midnight DateTime
        Assert.Equal(new DateTime(2026, 8, 4), r["Dates"]);
        Assert.Equal("Pacific", r["RegionName"]);
        Assert.Equal(0.0071m, r["NgHdd"]);
        Assert.Equal(false, r["IsForecast"]);
        Assert.Equal(0.1715m, r["GasWeight"]);
    }

    [Fact]
    public void Regions5Sink_BuildTable_DedupsOnRunDateDatesRegionName_KeepingLast()
    {
        var sink = new Regions5DegreeDaysSqlSink(Opt(), NullLogger<Regions5DegreeDaysSqlSink>.Instance);
        var first = Row5(1, "Pacific", 0.1000m);
        var last = Row5(2, "Pacific", 0.9999m);   // same (RunDate, Dates, RegionName) → collapses

        var t = BuildTable(sink, new List<Regions5DegreeDaysRow> { first, last });

        var kept = Assert.Single(t.Rows.Cast<DataRow>());
        Assert.Equal(0.9999m, kept["NgHdd"]);      // last wins
        Assert.Equal(2, kept["FileLogId"]);
    }

    [Fact]
    public void Regions5Sink_BuildTable_DistinctRegionNamesAreNotCollapsed()
    {
        var sink = new Regions5DegreeDaysSqlSink(Opt(), NullLogger<Regions5DegreeDaysSqlSink>.Instance);

        var t = BuildTable(sink, new List<Regions5DegreeDaysRow>
        {
            Row5(1, "Pacific", 0.01m),
            Row5(2, "East", 0.02m)   // same RunDate+Dates but different RegionName → kept separately
        });

        Assert.Equal(2, t.Rows.Count);
    }

    // ---------------------------------------------------------------- Regions9 (identical column contract to #16)

    [Fact]
    public void Regions9Sink_BuildTable_Matches20ColTvpOrderAndTypes_IdenticalToRegions5()
    {
        var sink = new Regions9DegreeDaysSqlSink(Opt(), NullLogger<Regions9DegreeDaysSqlSink>.Instance);

        var t = BuildTable(sink, new List<Regions9DegreeDaysRow> { Row9(7, "NEW ENGLAND", 0.0000m) });

        Assert.Equal(Region5And9Columns, Names(t));
        Assert.Equal(Region5And9Types, Types(t));

        var r = t.Rows[0];
        Assert.Equal("NEW ENGLAND", r["RegionName"]);
        Assert.Equal(0.0459m, r["PopWeight"]);
    }

    [Fact]
    public void Regions9Sink_BuildTable_DedupsOnRunDateDatesRegionName_KeepingLast()
    {
        var sink = new Regions9DegreeDaysSqlSink(Opt(), NullLogger<Regions9DegreeDaysSqlSink>.Instance);

        var t = BuildTable(sink, new List<Regions9DegreeDaysRow>
        {
            Row9(1, "PACIFIC", 0.1m),
            Row9(2, "PACIFIC", 0.5m)
        });

        var kept = Assert.Single(t.Rows.Cast<DataRow>());
        Assert.Equal(0.5m, kept["NgHdd"]);
    }

    // ---------------------------------------------------------------- ISO (divergent 13-col contract; ends at IsForecast)

    [Fact]
    public void IsoSink_BuildTable_Matches13ColTvpOrderAndTypes_EndsAtIsForecast_NoElecOrWeights()
    {
        var sink = new ISODegreeDaysSqlSink(Opt(), NullLogger<ISODegreeDaysSqlSink>.Instance);

        var t = BuildTable(sink, new List<ISODegreeDaysRow> { RowIso(3, "ERCOT", 0.0000m) });

        Assert.Equal(new[]
        {
            "FileLogId", "RunDate", "Dates", "RegionName",
            "PopHdd", "PopHdd30y", "PopHdd10y", "PopHddLastY",
            "PopCdd", "PopCdd30y", "PopCdd10y", "PopCddLastY", "IsForecast"
        }, Names(t));
        Assert.Equal(new[]
        {
            typeof(int), typeof(DateTime), typeof(DateTime), typeof(string),
            typeof(decimal), typeof(decimal), typeof(decimal), typeof(decimal),
            typeof(decimal), typeof(decimal), typeof(decimal), typeof(decimal), typeof(bool)
        }, Types(t));
        Assert.Equal("IsForecast", Names(t)[^1]);   // ISO ends at IsForecast — no ELEC/weight tail

        var r = t.Rows[0];
        Assert.Equal("ERCOT", r["RegionName"]);
        Assert.Equal(23.96m, r["PopCdd"]);
        Assert.Equal(false, r["IsForecast"]);
    }

    [Fact]
    public void IsoSink_BuildTable_DedupsOnRunDateDatesRegionName_KeepingLast()
    {
        var sink = new ISODegreeDaysSqlSink(Opt(), NullLogger<ISODegreeDaysSqlSink>.Instance);

        var t = BuildTable(sink, new List<ISODegreeDaysRow>
        {
            RowIso(1, "ERCOT", 0.0m),
            RowIso(2, "ERCOT", 4.4m)
        });

        var kept = Assert.Single(t.Rows.Cast<DataRow>());
        Assert.Equal(4.4m, kept["PopHdd"]);
    }
}
