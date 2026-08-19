using System.Data;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DataLoader.CWG.Tests;

/// <summary>
/// The SQL sinks' <c>BuildTable</c> — the C#-side of the TVP contract. Column
/// order and types must match <c>sql/CWG/002_CreateCwgTvpTypes.sql</c> EXACTLY,
/// with <c>FileLogId</c> always first. Each sink also de-dups the batch on its
/// merge key. BuildTable is pure (no DB) but protected on a sealed type, so it is
/// invoked via reflection (Platts/StormVista posture).
/// </summary>
public class SinkTests
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

    // ---------------------------------------------------------------- SolarForecast (byte HourOfDay; dedup on Region+InitDate+ForecastDate+HourOfDay)

    [Fact]
    public void SolarForecastSink_BuildTable_MatchesTvpOrderAndTypes()
    {
        var sink = new SolarForecastSqlSink(Opt(), NullLogger<SolarForecastSqlSink>.Instance);
        var row = new SolarForecastRow
        {
            FileLogId = 5, Region = "ERCOT", InitDate = new DateOnly(2026, 8, 11),
            ForecastDate = new DateOnly(2026, 8, 12), HourLabel = "12:00 PM", HourOfDay = 12, ValueMw = 30147m
        };

        var t = BuildTable(sink, new List<SolarForecastRow> { row });

        // arm.SolarForecastTvp: (FileLogId, Region, InitDate, ForecastDate, HourLabel, HourOfDay, ValueMw).
        Assert.Equal(new[] { "FileLogId", "Region", "InitDate", "ForecastDate", "HourLabel", "HourOfDay", "ValueMw" }, Names(t));
        Assert.Equal(new[] { typeof(int), typeof(string), typeof(DateTime), typeof(DateTime), typeof(string), typeof(byte), typeof(decimal) }, Types(t));

        var r = t.Rows[0];
        Assert.Equal(5, r["FileLogId"]);
        Assert.Equal(new DateTime(2026, 8, 11), r["InitDate"]);   // DateOnly -> midnight DateTime
        Assert.Equal((byte)12, r["HourOfDay"]);
        Assert.Equal(30147m, r["ValueMw"]);
    }

    [Fact]
    public void SolarForecastSink_BuildTable_DedupsOnMergeKey_KeepingLast()
    {
        var sink = new SolarForecastSqlSink(Opt(), NullLogger<SolarForecastSqlSink>.Instance);
        var key = new { Region = "ERCOT", Init = new DateOnly(2026, 8, 11), Fc = new DateOnly(2026, 8, 12), H = (byte)12 };
        var first = new SolarForecastRow
        {
            FileLogId = 1, Region = key.Region, InitDate = key.Init, ForecastDate = key.Fc,
            HourLabel = "12:00 PM", HourOfDay = key.H, ValueMw = 100m
        };
        var last = new SolarForecastRow
        {
            FileLogId = 2, Region = key.Region, InitDate = key.Init, ForecastDate = key.Fc,
            HourLabel = "12:00 PM", HourOfDay = key.H, ValueMw = 200m
        };

        var t = BuildTable(sink, new List<SolarForecastRow> { first, last });

        var kept = Assert.Single(t.Rows.Cast<DataRow>());
        Assert.Equal(200m, kept["ValueMw"]); // duplicate merge key collapses, last wins
    }

    // ---------------------------------------------------------------- WindTotalCapacityMW (nullable TotalCapacityMw -> DBNull; Block column)

    [Fact]
    public void WindTotalCapacityMWSink_BuildTable_MatchesTvpOrder_NullTotalCapacityIsDbNull()
    {
        var sink = new WindTotalCapacityMWSqlSink(Opt(), NullLogger<WindTotalCapacityMWSqlSink>.Instance);
        var change = new WindTotalCapacityMWRow
        {
            FileLogId = 8, ProductionDate = new DateOnly(2026, 8, 11), Block = "Change", Region = "ERCOT",
            TotalCapacityMw = null, Avg_1_5 = -337m, Avg_6_10 = -334m, Avg_11_15 = 1233m
        };

        var t = BuildTable(sink, new List<WindTotalCapacityMWRow> { change });

        // arm.WindTotalCapacityMWTvp: (FileLogId, ProductionDate, Block, Region, TotalCapacityMw, Avg_1_5, Avg_6_10, Avg_11_15).
        Assert.Equal(new[] { "FileLogId", "ProductionDate", "Block", "Region", "TotalCapacityMw", "Avg_1_5", "Avg_6_10", "Avg_11_15" }, Names(t));
        Assert.Equal(new[] { typeof(int), typeof(DateTime), typeof(string), typeof(string), typeof(decimal), typeof(decimal), typeof(decimal), typeof(decimal) }, Types(t));

        var r = t.Rows[0];
        Assert.Equal(DBNull.Value, r["TotalCapacityMw"]); // blank Change-block capacity -> NULL
        Assert.Equal("Change", r["Block"]);
        Assert.Equal(-337m, r["Avg_1_5"]);
    }

    [Fact]
    public void WindTotalCapacityMWSink_BuildTable_DedupsOnProductionDateBlockRegion()
    {
        var sink = new WindTotalCapacityMWSqlSink(Opt(), NullLogger<WindTotalCapacityMWSqlSink>.Instance);
        var a = new WindTotalCapacityMWRow
        {
            FileLogId = 1, ProductionDate = new DateOnly(2026, 8, 11), Block = "Current", Region = "MISO",
            TotalCapacityMw = 33687m, Avg_1_5 = 1m, Avg_6_10 = 1m, Avg_11_15 = 1m
        };
        var b = new WindTotalCapacityMWRow
        {
            FileLogId = 2, ProductionDate = new DateOnly(2026, 8, 11), Block = "Current", Region = "MISO",
            TotalCapacityMw = 33687m, Avg_1_5 = 9m, Avg_6_10 = 9m, Avg_11_15 = 9m
        };

        var t = BuildTable(sink, new List<WindTotalCapacityMWRow> { a, b });

        var kept = Assert.Single(t.Rows.Cast<DataRow>());
        Assert.Equal(9m, kept["Avg_1_5"]); // last wins on the (ProductionDate, Block, Region) key
    }

    // ---------------------------------------------------------------- Station (11 cols; nullable strings -> DBNull; NVARCHAR Name)

    [Fact]
    public void StationSink_BuildTable_Matches11ColTvpOrder_NullableStringsAreDbNull()
    {
        var sink = new StationSqlSink(Opt(), NullLogger<StationSqlSink>.Instance);
        var row = new StationRow
        {
            FileLogId = 3, Region = "northamerica", Identifier = "CWKD", WmoId = "71383",
            Wban = "99999", Ghcnd = null, Lat = 50.733m, Lon = -71.017m, Name = "Bonnard", State = null, Country = "CA"
        };

        var t = BuildTable(sink, new List<StationRow> { row });

        // arm.StationTvp: (FileLogId, Region, Identifier, WmoId, Wban, Ghcnd, Lat, Lon, Name, State, Country).
        Assert.Equal(new[] { "FileLogId", "Region", "Identifier", "WmoId", "Wban", "Ghcnd", "Lat", "Lon", "Name", "State", "Country" }, Names(t));
        Assert.Equal(new[]
        {
            typeof(int), typeof(string), typeof(string), typeof(string), typeof(string), typeof(string),
            typeof(decimal), typeof(decimal), typeof(string), typeof(string), typeof(string)
        }, Types(t));

        var r = t.Rows[0];
        Assert.Equal("99999", r["Wban"]);           // literal kept
        Assert.Equal(DBNull.Value, r["Ghcnd"]);     // null -> DBNull
        Assert.Equal(DBNull.Value, r["State"]);
        Assert.Equal(-71.017m, r["Lon"]);
    }

    // ---------------------------------------------------------------- CityForecast (SMALLINT Hdd/Cdd; FileLogId first; dedup)

    [Fact]
    public void CityForecastSink_BuildTable_MatchesTvpOrder_ShortHddCdd_FileLogIdFirst()
    {
        var sink = new CityForecastSqlSink(Opt(), NullLogger<CityForecastSqlSink>.Instance);
        var row = new CityForecastRow
        {
            FileLogId = 4, Region = "northamerica", ProductionDate = new DateOnly(2026, 8, 11),
            ForecastDate = new DateOnly(2026, 8, 11), Station = "KABR",
            FcstMin = 62m, FcstMax = 83m, FcstAvg = 72.5m, NormMin = 57.9m, NormMax = 83.8m, Hdd = 0, Cdd = 8,
            Units = "F"
        };

        var t = BuildTable(sink, new List<CityForecastRow> { row });

        Assert.Equal(new[]
        {
            "FileLogId", "Region", "ProductionDate", "ForecastDate", "Station",
            "FcstMin", "FcstMax", "FcstAvg", "NormMin", "NormMax", "Hdd", "Cdd", "Units"
        }, Names(t));
        Assert.Equal("FileLogId", Names(t)[0]); // FileLogId is ALWAYS the first TVP column
        Assert.Equal("Units", Names(t)[^1]);    // Units is ALWAYS the last TVP column
        Assert.Equal(typeof(short), t.Columns["Hdd"]!.DataType); // SMALLINT
        Assert.Equal(typeof(short), t.Columns["Cdd"]!.DataType);

        var r = t.Rows[0];
        Assert.Equal((short)8, r["Cdd"]);
        Assert.Equal(83.8m, r["NormMax"]);
        Assert.Equal("F", r["Units"]);   // na unit -> 'F'
    }

    [Fact]
    public void CityForecastSink_BuildTable_EuropeUnitsC_PreservesFullDecimalScale_UnitsLast()
    {
        // europe unit: Units='C' (last column) and the _C normals carry up to 5 dp. The five temp
        // columns are DECIMAL(8,5): BuildTable must pass them through UNROUNDED (no clamp to (5,1)).
        var sink = new CityForecastSqlSink(Opt(), NullLogger<CityForecastSqlSink>.Instance);
        var row = new CityForecastRow
        {
            FileLogId = 6, Region = "europe", ProductionDate = new DateOnly(2026, 8, 11),
            ForecastDate = new DateOnly(2026, 8, 11), Station = "BIAR",
            FcstMin = 7m, FcstMax = 16m, FcstAvg = 11.5m,
            NormMin = 7.74478m, NormMax = 15.4283m, Hdd = 7, Cdd = 0,
            Units = "C"
        };

        var t = BuildTable(sink, new List<CityForecastRow> { row });

        Assert.Equal("Units", Names(t)[^1]);       // Units is ALWAYS the last TVP column
        Assert.Equal(typeof(decimal), t.Columns["NormMin"]!.DataType);
        Assert.Equal(typeof(decimal), t.Columns["NormMax"]!.DataType);

        var r = t.Rows[0];
        Assert.Equal("C", r["Units"]);             // europe unit -> 'C'
        Assert.Equal(7.74478m, r["NormMin"]);      // 5-dp normal emitted UNROUNDED
        Assert.Equal(15.4283m, r["NormMax"]);
        Assert.NotEqual(Math.Round(7.74478m, 1), (decimal)r["NormMin"]); // would be 7.7m if clamped to (5,1)
    }
}
