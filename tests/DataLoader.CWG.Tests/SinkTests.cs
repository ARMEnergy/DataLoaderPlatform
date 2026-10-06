using System.Data;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DataLoader.CWG.Tests;

/// <summary>
/// The SQL sinks' <c>BuildTable</c> — the C#-side of the TVP contract. Column NAME,
/// ORDER and TYPE must match <c>sql/CWG/002_CreateCwgTvpTypes.sql</c> EXACTLY: key
/// columns first, then value columns, and NEITHER RecordHash NOR ModifiedAtUtc
/// (the merge proc computes both). Each sink also de-dups the batch on its merge
/// key. BuildTable is pure (no DB) but protected on a sealed type, so it is
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

    /// <summary>No sink may emit the two proc-computed columns.</summary>
    private static void AssertNoComputedColumns(DataTable t)
    {
        Assert.DoesNotContain("RecordHash", Names(t));
        Assert.DoesNotContain("ModifiedAtUtc", Names(t));
        Assert.DoesNotContain("FileLogId", Names(t));   // gone from every fact since the reshape
    }

    // ---------------------------------------------------------------- SolarForecast

    [Fact]
    public void SolarForecastSink_BuildTable_MatchesTvpOrderAndTypes()
    {
        var sink = new SolarForecastSqlSink(Opt(), NullLogger<SolarForecastSqlSink>.Instance);
        var row = new SolarForecastRow
        {
            ForecastDate = new DateOnly(2026, 8, 11),
            UTCHourEnding = new DateTime(2026, 8, 12, 16, 0, 0),
            Region = "ERCOT",
            ForecastLabel = new DateOnly(2026, 8, 12),
            HourLabel = "12:00 PM",
            Value = 30147m
        };

        var t = BuildTable(sink, new List<SolarForecastRow> { row });

        // arm.SolarForecastTvp: (ForecastDate, UTCHourEnding, Region, ForecastLabel, HourLabel, Value).
        Assert.Equal(new[] { "ForecastDate", "UTCHourEnding", "Region", "ForecastLabel", "HourLabel", "Value" }, Names(t));
        Assert.Equal(new[] { typeof(DateTime), typeof(DateTime), typeof(string), typeof(DateTime), typeof(string), typeof(decimal) }, Types(t));
        AssertNoComputedColumns(t);

        var r = t.Rows[0];
        Assert.Equal(new DateTime(2026, 8, 11), r["ForecastDate"]);              // DateOnly -> midnight DateTime
        Assert.Equal(new DateTime(2026, 8, 12, 16, 0, 0), r["UTCHourEnding"]);   // full timestamp preserved
        Assert.Equal("12:00 PM", r["HourLabel"]);
        Assert.Equal(30147m, r["Value"]);
    }

    [Fact]
    public void SolarForecastSink_BuildTable_DedupsOnMergeKey_KeepingLast()
    {
        var sink = new SolarForecastSqlSink(Opt(), NullLogger<SolarForecastSqlSink>.Instance);
        SolarForecastRow Make(decimal v) => new()
        {
            ForecastDate = new DateOnly(2026, 8, 11),
            UTCHourEnding = new DateTime(2026, 8, 12, 16, 0, 0),
            Region = "ERCOT",
            ForecastLabel = new DateOnly(2026, 8, 12),
            HourLabel = "12:00 PM",
            Value = v
        };

        var t = BuildTable(sink, new List<SolarForecastRow> { Make(100m), Make(200m) });

        var kept = Assert.Single(t.Rows.Cast<DataRow>());
        Assert.Equal(200m, kept["Value"]); // duplicate merge key collapses, last wins
    }

    [Fact]
    public void SolarForecastSink_BuildTable_NullValueIsDbNull()
    {
        var sink = new SolarForecastSqlSink(Opt(), NullLogger<SolarForecastSqlSink>.Instance);
        var row = new SolarForecastRow
        {
            ForecastDate = new DateOnly(2026, 8, 11),
            UTCHourEnding = new DateTime(2026, 8, 12, 16, 0, 0),
            Region = "ERCOT",
            ForecastLabel = new DateOnly(2026, 8, 12),
            HourLabel = "12:00 PM",
            Value = null
        };

        var t = BuildTable(sink, new List<SolarForecastRow> { row });

        Assert.Equal(DBNull.Value, t.Rows[0]["Value"]);
    }

    // ---------------------------------------------------------------- WindTotalCapacityMW (no Block column)

    [Fact]
    public void WindTotalCapacityMWSink_BuildTable_MatchesTvpOrder_NoBlock_NullsAreDbNull()
    {
        var sink = new WindTotalCapacityMWSqlSink(Opt(), NullLogger<WindTotalCapacityMWSqlSink>.Instance);
        var row = new WindTotalCapacityMWRow
        {
            ForecastDate = new DateOnly(2026, 8, 11), Region = "ERCOT",
            TotalCapacity = null, OneToFiveDayAverage = -337m,
            SixToTenDayAverage = -334m, ElevenToFifteenDayAverage = null
        };

        var t = BuildTable(sink, new List<WindTotalCapacityMWRow> { row });

        // arm.WindTotalCapacityMWTvp: (ForecastDate, Region, TotalCapacity,
        //   OneToFiveDayAverage, SixToTenDayAverage, ElevenToFifteenDayAverage). No Block.
        Assert.Equal(new[]
        {
            "ForecastDate", "Region", "TotalCapacity",
            "OneToFiveDayAverage", "SixToTenDayAverage", "ElevenToFifteenDayAverage"
        }, Names(t));
        Assert.Equal(new[]
        {
            typeof(DateTime), typeof(string), typeof(decimal), typeof(decimal), typeof(decimal), typeof(decimal)
        }, Types(t));
        Assert.DoesNotContain("Block", Names(t));
        AssertNoComputedColumns(t);

        var r = t.Rows[0];
        Assert.Equal(DBNull.Value, r["TotalCapacity"]);
        Assert.Equal(DBNull.Value, r["ElevenToFifteenDayAverage"]);
        Assert.Equal(-337m, r["OneToFiveDayAverage"]);
    }

    [Fact]
    public void WindTotalCapacityMWSink_BuildTable_DedupsOnForecastDateRegion()
    {
        var sink = new WindTotalCapacityMWSqlSink(Opt(), NullLogger<WindTotalCapacityMWSqlSink>.Instance);
        WindTotalCapacityMWRow Make(decimal avg) => new()
        {
            ForecastDate = new DateOnly(2026, 8, 11), Region = "MISO",
            TotalCapacity = 33687m, OneToFiveDayAverage = avg,
            SixToTenDayAverage = avg, ElevenToFifteenDayAverage = avg
        };

        var t = BuildTable(sink, new List<WindTotalCapacityMWRow> { Make(1m), Make(9m) });

        var kept = Assert.Single(t.Rows.Cast<DataRow>());
        Assert.Equal(9m, kept["OneToFiveDayAverage"]); // last wins on the (ForecastDate, Region) key
    }

    // ---------------------------------------------------------------- Station (FLOAT lat/lon; no Point/MappingId)

    [Fact]
    public void StationSink_BuildTable_MatchesTvpOrder_NoPointOrMappingId_NullableStringsAreDbNull()
    {
        var sink = new StationSqlSink(Opt(), NullLogger<StationSqlSink>.Instance);
        var row = new StationRow
        {
            Identifier = "CWKD", WmoId = "71383", Wban = "99999", Ghcnd = null,
            Latitude = 50.733, Longitude = -71.017, Name = "Bonnard", State = null, Country = "CA"
        };

        var t = BuildTable(sink, new List<StationRow> { row });

        // arm.StationTvp: (Identifier, WMOID, WBAN, GHCND, Latitude, Longitude, Name, State, Country).
        Assert.Equal(new[] { "Identifier", "WMOID", "WBAN", "GHCND", "Latitude", "Longitude", "Name", "State", "Country" }, Names(t));
        Assert.Equal(new[]
        {
            typeof(string), typeof(string), typeof(string), typeof(string),
            typeof(double), typeof(double), typeof(string), typeof(string), typeof(string)
        }, Types(t));

        // Point is derived in the merge proc; MappingId is IDENTITY. Neither may be sent.
        Assert.DoesNotContain("Point", Names(t));
        Assert.DoesNotContain("MappingId", Names(t));
        AssertNoComputedColumns(t);

        var r = t.Rows[0];
        Assert.Equal("99999", r["WBAN"]);            // literal kept
        Assert.Equal(DBNull.Value, r["GHCND"]);      // null -> DBNull
        Assert.Equal(DBNull.Value, r["State"]);
        Assert.Equal(-71.017, r["Longitude"]);       // FLOAT
    }

    [Fact]
    public void StationSink_BuildTable_NullCoordinatesAreDbNull()
    {
        var sink = new StationSqlSink(Opt(), NullLogger<StationSqlSink>.Instance);
        var row = new StationRow { Identifier = "KABC", Latitude = null, Longitude = null };

        var t = BuildTable(sink, new List<StationRow> { row });

        Assert.Equal(DBNull.Value, t.Rows[0]["Latitude"]);
        Assert.Equal(DBNull.Value, t.Rows[0]["Longitude"]);
    }

    // ---------------------------------------------------------------- CityForecast

    [Fact]
    public void CityForecastSink_BuildTable_MatchesTvpOrder_NoRegionOrUnits()
    {
        var sink = new CityForecastSqlSink(Opt(), NullLogger<CityForecastSqlSink>.Instance);
        var row = new CityForecastRow
        {
            ProductionDate = new DateOnly(2026, 8, 11), Date = new DateOnly(2026, 8, 11), Station = "KABR",
            ForecastMin = 62m, ForecastMax = 83m, ForecastAvg = 72.5m,
            NormMin = 57.9m, NormMax = 83.8m, HDD = 0m, CDD = 8m
        };

        var t = BuildTable(sink, new List<CityForecastRow> { row });

        Assert.Equal(new[]
        {
            "ProductionDate", "Date", "Station",
            "ForecastMin", "ForecastMax", "ForecastAvg", "NormMin", "NormMax", "HDD", "CDD"
        }, Names(t));
        Assert.DoesNotContain("Region", Names(t));
        Assert.DoesNotContain("Units", Names(t));
        AssertNoComputedColumns(t);

        // HDD/CDD are DECIMAL(9,4) now, not SMALLINT.
        Assert.Equal(typeof(decimal), t.Columns["HDD"]!.DataType);
        Assert.Equal(typeof(decimal), t.Columns["CDD"]!.DataType);

        var r = t.Rows[0];
        Assert.Equal(8m, r["CDD"]);
        Assert.Equal(83.8m, r["NormMax"]);
    }

    [Fact]
    public void CityForecastSink_BuildTable_PreservesFullDecimalScale()
    {
        var sink = new CityForecastSqlSink(Opt(), NullLogger<CityForecastSqlSink>.Instance);
        var row = new CityForecastRow
        {
            ProductionDate = new DateOnly(2026, 8, 11), Date = new DateOnly(2026, 8, 11), Station = "BIAR",
            ForecastMin = 7m, ForecastMax = 16m, ForecastAvg = 11.5m,
            NormMin = 7.74478m, NormMax = 15.4283m, HDD = 7m, CDD = 0m
        };

        var t = BuildTable(sink, new List<CityForecastRow> { row });

        var r = t.Rows[0];
        Assert.Equal(7.74478m, r["NormMin"]);  // emitted UNROUNDED; the DECIMAL(9,4) column enforces scale
        Assert.Equal(15.4283m, r["NormMax"]);
    }

    [Fact]
    public void CityForecastSink_BuildTable_DedupsOnProductionDateDateStation()
    {
        var sink = new CityForecastSqlSink(Opt(), NullLogger<CityForecastSqlSink>.Instance);
        CityForecastRow Make(decimal min) => new()
        {
            ProductionDate = new DateOnly(2026, 8, 11), Date = new DateOnly(2026, 8, 11), Station = "KABR",
            ForecastMin = min, ForecastMax = 83m, ForecastAvg = 72.5m,
            NormMin = 57.9m, NormMax = 83.8m, HDD = 0m, CDD = 8m
        };

        var t = BuildTable(sink, new List<CityForecastRow> { Make(1m), Make(2m) });

        var kept = Assert.Single(t.Rows.Cast<DataRow>());
        Assert.Equal(2m, kept["ForecastMin"]);
    }

    // ---------------------------------------------------------------- Wide sinks

    [Fact]
    public void DailyNormalSink_BuildTable_IsWide_ColumnsNamedFromRowTypeRegionColumns()
    {
        var sink = new DailyNormalSqlSink(Opt(), NullLogger<DailyNormalSqlSink>.Instance);
        var row = new DailyNormalRow
        {
            Month = 1, Day = 1,
            Values = new decimal?[] { 3371m, 14560m, 15156m, 14219m, 4454m, 761m, 909m, 616m, 1924m, null }
        };

        var t = BuildTable(sink, new List<DailyNormalRow> { row });

        Assert.Equal(new[] { "Month", "Day" }.Concat(DailyNormalRow.RegionColumns).ToArray(), Names(t));
        Assert.Equal(typeof(byte), t.Columns["Month"]!.DataType);   // TINYINT
        Assert.Equal(typeof(byte), t.Columns["Day"]!.DataType);
        Assert.All(DailyNormalRow.RegionColumns, c => Assert.Equal(typeof(decimal), t.Columns[c]!.DataType));
        AssertNoComputedColumns(t);
        Assert.DoesNotContain("NW", Names(t));
        Assert.DoesNotContain("SW", Names(t));

        var r = t.Rows[0];
        Assert.Equal((byte)1, r["Month"]);
        Assert.Equal(3371m, r["CAISO"]);
        Assert.Equal(1924m, r["IESO"]);
        Assert.Equal(DBNull.Value, r["AESO"]);   // NULL measure -> DBNull
    }

    [Fact]
    public void SolarHourlySink_BuildTable_IsWide_TwoRegionColumns()
    {
        var sink = new SolarHourlySqlSink(Opt(), NullLogger<SolarHourlySqlSink>.Instance);
        var row = new SolarHourlyRow
        {
            UTCHourEnding = new DateTime(2020, 7, 26, 15, 0, 0),
            Values = new decimal?[] { 3100m, null }
        };

        var t = BuildTable(sink, new List<SolarHourlyRow> { row });

        Assert.Equal(new[] { "UTCHourEnding", "ERCOT", "CAISO" }, Names(t));
        AssertNoComputedColumns(t);

        var r = t.Rows[0];
        Assert.Equal(new DateTime(2020, 7, 26, 15, 0, 0), r["UTCHourEnding"]);
        Assert.Equal(3100m, r["ERCOT"]);
        Assert.Equal(DBNull.Value, r["CAISO"]);
    }

    [Fact]
    public void WindHourlySink_BuildTable_IsWide_TenRegionColumns_DedupsOnHour()
    {
        var sink = new WindHourlySqlSink(Opt(), NullLogger<WindHourlySqlSink>.Instance);
        WindHourlyRow Make(decimal first) => new()
        {
            UTCHourEnding = new DateTime(2026, 8, 6, 22, 0, 0),
            Values = new decimal?[] { first, 2m, 3m, 4m, 5m, 6m, 7m, 8m, 9m, 10m }
        };

        var t = BuildTable(sink, new List<WindHourlyRow> { Make(1m), Make(99m) });

        Assert.Equal(new[] { "UTCHourEnding" }.Concat(WindHourlyRow.RegionColumns).ToArray(), Names(t));
        Assert.Equal(11, t.Columns.Count);
        AssertNoComputedColumns(t);

        var kept = Assert.Single(t.Rows.Cast<DataRow>());
        Assert.Equal(99m, kept["CAISO"]); // last wins on the UTCHourEnding key
    }

    // ---------------------------------------------------------------- StateDegreeDayObservation

    [Fact]
    public void StateDegreeDayObservationSink_BuildTable_MatchesTvpOrder_SourceColumnNames()
    {
        var sink = new StateDegreeDayObservationSqlSink(Opt(), NullLogger<StateDegreeDayObservationSqlSink>.Instance);
        var row = new StateDegreeDayObservationRow
        {
            ObservationDate = new DateOnly(2026, 8, 11), RegionName = "NEW ENGLAND", State = "CONNECTICUT",
            PopHdd = 0m, PopHdd30y = 0.0257m, PopHdd10y = 0.1166m, PopHddLastY = 0m,
            PopCdd = 10.0358m, PopCdd30y = 7.1107m, PopCdd10y = 8.0242m, PopCddLastY = 9.6741m,
            IsForecast = false, GasWeight = 0.2159m, ElctWeight = 0.2519m, PopWeight = 0.2385m
        };

        var t = BuildTable(sink, new List<StateDegreeDayObservationRow> { row });

        // The digit-leading source spellings are the TVP column names, verbatim.
        Assert.Equal(new[]
        {
            "ObservationDate", "RegionName", "State",
            "POP_HDD", "30Y_POP_HDD", "10Y_POP_HDD", "LAST_Y_POP_HDD",
            "POP_CDD", "30Y_POP_CDD", "10Y_POP_CDD", "LAST_Y_POP_CDD",
            "IS_FORECAST", "GAS_WEIGHT", "ELCT_WEIGHT", "POP_WEIGHT"
        }, Names(t));
        Assert.Equal(typeof(bool), t.Columns["IS_FORECAST"]!.DataType);
        AssertNoComputedColumns(t);

        var r = t.Rows[0];
        Assert.Equal("CONNECTICUT", r["State"]);
        Assert.Equal(0.0257m, r["30Y_POP_HDD"]);
        Assert.Equal(false, r["IS_FORECAST"]);
        Assert.Equal(0.2519m, r["ELCT_WEIGHT"]);   // source spells it ELCT
    }

    [Fact]
    public void StateDegreeDayObservationSink_BuildTable_NullIsForecastIsDbNull()
    {
        var sink = new StateDegreeDayObservationSqlSink(Opt(), NullLogger<StateDegreeDayObservationSqlSink>.Instance);
        var row = new StateDegreeDayObservationRow
        {
            ObservationDate = new DateOnly(2026, 8, 11), RegionName = "PACIFIC", State = "OREGON",
            IsForecast = null
        };

        var t = BuildTable(sink, new List<StateDegreeDayObservationRow> { row });

        Assert.Equal(DBNull.Value, t.Rows[0]["IS_FORECAST"]);
        Assert.Equal(DBNull.Value, t.Rows[0]["POP_HDD"]);
    }
}
