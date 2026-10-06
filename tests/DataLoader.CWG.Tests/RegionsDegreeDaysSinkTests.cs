using System.Data;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DataLoader.CWG.Tests;

/// <summary>
/// The three degree-day sinks' <c>BuildTable</c> — the C#-side of the TVP contract.
/// Column NAME, ORDER, COUNT and type must match <c>sql/CWG/002</c> exactly: the
/// key columns first, then the SOURCE column spellings verbatim (including the
/// digit-leading <c>30Y_</c>/<c>10Y_</c> names and the <c>ELCT</c> spelling), and
/// neither RecordHash nor ModifiedAtUtc (the merge proc computes both). Each sink
/// de-dups a batch on its merge key <c>(ObservationDate, Date, Region)</c>,
/// last-wins. BuildTable is pure (no DB) but protected on a sealed type, so it is
/// invoked via reflection (SinkTests posture).
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

    private static Regions5DegreeDaysRow Row5(string region, decimal ngHdd) => new()
    {
        ObservationDate = Run, Date = Day, Region = region,
        NgHdd = ngHdd, NgHdd30y = 0.1m, NgHdd10y = 0.02m, NgHddLastY = 0.19m,
        PopCdd = 9.83m, PopCdd30y = 6.77m, PopCdd10y = 8.19m, PopCddLastY = 3.95m,
        ElecCdd = 9.01m, ElecCdd30y = 6.35m, ElecCdd10y = 7.77m, ElecCddLastY = 3.53m,
        IsForecast = false, GasWeight = 0.1715m, ElctWeight = 0.1100m, PopWeight = 0.1564m
    };

    private static Regions9DegreeDaysRow Row9(string region, decimal ngHdd) => new()
    {
        ObservationDate = Run, Date = Day, Region = region,
        NgHdd = ngHdd, NgHdd30y = 0.2m, NgHdd10y = 0.15m, NgHddLastY = 0.01m,
        PopCdd = 6.72m, PopCdd30y = 6.65m, PopCdd10y = 7.81m, PopCddLastY = 5.16m,
        ElecCdd = 6.71m, ElecCdd30y = 6.66m, ElecCdd10y = 7.82m, ElecCddLastY = 5.16m,
        IsForecast = false, GasWeight = 0.0396m, ElctWeight = 0.0349m, PopWeight = 0.0459m
    };

    private static ISODegreeDaysRow RowIso(string region, decimal popHdd) => new()
    {
        ObservationDate = Run, Date = Day, Region = region,
        PopHdd = popHdd, PopHdd30y = 0.0175m, PopHdd10y = 0m, PopHddLastY = 0m,
        PopCdd = 23.96m, PopCdd30y = 21.27m, PopCdd10y = 22.53m, PopCddLastY = 17.21m,
        IsForecast = false
    };

    private static readonly string[] Region5And9Columns =
    {
        "ObservationDate", "Date", "Region",
        "NG_HDD", "30Y_NG_HDD", "10Y_NG_HDD", "LAST_Y_NG_HDD",
        "POP_CDD", "30Y_POP_CDD", "10Y_POP_CDD", "LAST_Y_POP_CDD",
        "ELEC_CDD", "30Y_ELEC_CDD", "10Y_ELEC_CDD", "LAST_Y_ELEC_CDD",
        "IS_FORECAST", "GAS_WEIGHT", "ELCT_WEIGHT", "POP_WEIGHT"
    };

    private static readonly Type[] Region5And9Types =
    {
        typeof(DateTime), typeof(DateTime), typeof(string),
        typeof(decimal), typeof(decimal), typeof(decimal), typeof(decimal),
        typeof(decimal), typeof(decimal), typeof(decimal), typeof(decimal),
        typeof(decimal), typeof(decimal), typeof(decimal), typeof(decimal),
        typeof(bool), typeof(decimal), typeof(decimal), typeof(decimal)
    };

    // ---------------------------------------------------------------- Regions5

    [Fact]
    public void Regions5Sink_BuildTable_Matches19ColTvpOrderAndTypes_KeysFirst_WeightsLast()
    {
        var sink = new Regions5DegreeDaysSqlSink(Opt(), NullLogger<Regions5DegreeDaysSqlSink>.Instance);

        var t = BuildTable(sink, new List<Regions5DegreeDaysRow> { Row5("Pacific", 0.0071m) });

        Assert.Equal(Region5And9Columns, Names(t));
        Assert.Equal(Region5And9Types, Types(t));
        Assert.Equal("ObservationDate", Names(t)[0]);
        Assert.Equal("POP_WEIGHT", Names(t)[^1]);  // tail is IS_FORECAST then the three weights
        Assert.DoesNotContain("FileLogId", Names(t));
        Assert.DoesNotContain("RecordHash", Names(t));
        Assert.DoesNotContain("ModifiedAtUtc", Names(t));

        var r = t.Rows[0];
        Assert.Equal(new DateTime(2026, 8, 11), r["ObservationDate"]);   // DateOnly → midnight DateTime
        Assert.Equal(new DateTime(2026, 8, 4), r["Date"]);
        Assert.Equal("Pacific", r["Region"]);
        Assert.Equal(0.0071m, r["NG_HDD"]);
        Assert.Equal(false, r["IS_FORECAST"]);
        Assert.Equal(0.1715m, r["GAS_WEIGHT"]);
        Assert.Equal(0.1100m, r["ELCT_WEIGHT"]);   // source spells it ELCT
    }

    [Fact]
    public void Regions5Sink_BuildTable_DedupsOnObservationDateDateRegion_KeepingLast()
    {
        var sink = new Regions5DegreeDaysSqlSink(Opt(), NullLogger<Regions5DegreeDaysSqlSink>.Instance);
        var first = Row5("Pacific", 0.1000m);
        var last = Row5("Pacific", 0.9999m);   // same (ObservationDate, Date, Region) → collapses

        var t = BuildTable(sink, new List<Regions5DegreeDaysRow> { first, last });

        var kept = Assert.Single(t.Rows.Cast<DataRow>());
        Assert.Equal(0.9999m, kept["NG_HDD"]);      // last wins
    }

    [Fact]
    public void Regions5Sink_BuildTable_DistinctRegionsAreNotCollapsed()
    {
        var sink = new Regions5DegreeDaysSqlSink(Opt(), NullLogger<Regions5DegreeDaysSqlSink>.Instance);

        var t = BuildTable(sink, new List<Regions5DegreeDaysRow>
        {
            Row5("Pacific", 0.01m),
            Row5("East", 0.02m)   // same ObservationDate+Date but different Region → kept separately
        });

        Assert.Equal(2, t.Rows.Count);
    }

    [Fact]
    public void Regions5Sink_BuildTable_NullMeasuresAreDbNull()
    {
        var sink = new Regions5DegreeDaysSqlSink(Opt(), NullLogger<Regions5DegreeDaysSqlSink>.Instance);
        var row = new Regions5DegreeDaysRow { ObservationDate = Run, Date = Day, Region = "Pacific" };

        var t = BuildTable(sink, new List<Regions5DegreeDaysRow> { row });

        var r = t.Rows[0];
        Assert.Equal(DBNull.Value, r["NG_HDD"]);
        Assert.Equal(DBNull.Value, r["IS_FORECAST"]);
        Assert.Equal(DBNull.Value, r["POP_WEIGHT"]);
    }

    // ---------------------------------------------------------------- Regions9 (identical column contract)

    [Fact]
    public void Regions9Sink_BuildTable_Matches19ColTvpOrderAndTypes_IdenticalToRegions5()
    {
        var sink = new Regions9DegreeDaysSqlSink(Opt(), NullLogger<Regions9DegreeDaysSqlSink>.Instance);

        var t = BuildTable(sink, new List<Regions9DegreeDaysRow> { Row9("NEW ENGLAND", 0.0000m) });

        Assert.Equal(Region5And9Columns, Names(t));
        Assert.Equal(Region5And9Types, Types(t));

        var r = t.Rows[0];
        Assert.Equal("NEW ENGLAND", r["Region"]);
        Assert.Equal(0.0459m, r["POP_WEIGHT"]);
    }

    [Fact]
    public void Regions9Sink_BuildTable_DedupsOnObservationDateDateRegion_KeepingLast()
    {
        var sink = new Regions9DegreeDaysSqlSink(Opt(), NullLogger<Regions9DegreeDaysSqlSink>.Instance);

        var t = BuildTable(sink, new List<Regions9DegreeDaysRow>
        {
            Row9("PACIFIC", 0.1m),
            Row9("PACIFIC", 0.5m)
        });

        var kept = Assert.Single(t.Rows.Cast<DataRow>());
        Assert.Equal(0.5m, kept["NG_HDD"]);
    }

    // ---------------------------------------------------------------- ISO (divergent 12-col contract; ends at IS_FORECAST)

    [Fact]
    public void IsoSink_BuildTable_Matches12ColTvpOrderAndTypes_EndsAtIsForecast_NoElecOrWeights()
    {
        var sink = new ISODegreeDaysSqlSink(Opt(), NullLogger<ISODegreeDaysSqlSink>.Instance);

        var t = BuildTable(sink, new List<ISODegreeDaysRow> { RowIso("ERCOT", 0.0000m) });

        Assert.Equal(new[]
        {
            "ObservationDate", "Date", "Region",
            "POP_HDD", "30Y_POP_HDD", "10Y_POP_HDD", "LAST_Y_POP_HDD",
            "POP_CDD", "30Y_POP_CDD", "10Y_POP_CDD", "LAST_Y_POP_CDD", "IS_FORECAST"
        }, Names(t));
        Assert.Equal(new[]
        {
            typeof(DateTime), typeof(DateTime), typeof(string),
            typeof(decimal), typeof(decimal), typeof(decimal), typeof(decimal),
            typeof(decimal), typeof(decimal), typeof(decimal), typeof(decimal), typeof(bool)
        }, Types(t));
        Assert.Equal("IS_FORECAST", Names(t)[^1]);   // ISO ends at IS_FORECAST — no ELEC/weight tail

        var r = t.Rows[0];
        Assert.Equal("ERCOT", r["Region"]);
        Assert.Equal(23.96m, r["POP_CDD"]);
        Assert.Equal(false, r["IS_FORECAST"]);
    }

    [Fact]
    public void IsoSink_BuildTable_DedupsOnObservationDateDateRegion_KeepingLast()
    {
        var sink = new ISODegreeDaysSqlSink(Opt(), NullLogger<ISODegreeDaysSqlSink>.Instance);

        var t = BuildTable(sink, new List<ISODegreeDaysRow>
        {
            RowIso("ERCOT", 0.0m),
            RowIso("ERCOT", 4.4m)
        });

        var kept = Assert.Single(t.Rows.Cast<DataRow>());
        Assert.Equal(4.4m, kept["POP_HDD"]);
    }
}
