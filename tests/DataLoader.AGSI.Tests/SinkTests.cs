using System.Data;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DataLoader.AGSI.Tests;

/// <summary>
/// The two SQL sinks' <c>BuildTable</c> — the C# side of the load-bearing TVP
/// contract. Column ORDER and types must match <c>sql/AGSI/002_CreateAgsiTvpTypes.sql</c>
/// EXACTLY. CRITICAL: the two TVPs place <c>FileLogId</c> DIFFERENTLY —
/// <b>LAST</b> for <c>arm.GasStorageEntityTvp</c>, <b>FIRST</b> for
/// <c>arm.GasStorageTvp</c>. A silent reorder here corrupts every loaded row.
/// BuildTable is pure (no DB) but protected on a sealed type, so it is invoked via
/// reflection (the CWG/Platts/StormVista posture).
/// </summary>
public class SinkTests
{
    private static DataTable BuildTable(object sink, object rows)
    {
        var method = sink.GetType().GetMethod("BuildTable", BindingFlags.Instance | BindingFlags.NonPublic)
                     ?? throw new InvalidOperationException("BuildTable not found");
        return (DataTable)method.Invoke(sink, new[] { rows })!;
    }

    private static IOptions<AgsiSettings> Opt() =>
        Options.Create(new AgsiSettings { ConnectionString = "unused" });

    private static string[] Names(DataTable t) => t.Columns.Cast<DataColumn>().Select(c => c.ColumnName).ToArray();
    private static Type[] Types(DataTable t) => t.Columns.Cast<DataColumn>().Select(c => c.DataType).ToArray();

    // ---------------------------------------------------------------- GasStorageEntity — FileLogId LAST

    [Fact]
    public void EntitySink_BuildTable_MatchesTvpOrder_FileLogIdLast()
    {
        var sink = new GasStorageEntitySqlSink(Opt(), NullLogger<GasStorageEntitySqlSink>.Instance);
        var row = new GasStorageEntityRow
        {
            FileLogId = 9, Code = "AT", Name = "Austria", ParentCode = "EU", ParentName = "Europe"
        };

        var t = BuildTable(sink, new List<GasStorageEntityRow> { row });

        // arm.GasStorageEntityTvp: (Code, Name, ParentCode, ParentName, FileLogId) — FileLogId LAST.
        Assert.Equal(new[] { "Code", "Name", "ParentCode", "ParentName", "FileLogId" }, Names(t));
        Assert.Equal(new[] { typeof(string), typeof(string), typeof(string), typeof(string), typeof(int) }, Types(t));
        Assert.Equal("FileLogId", Names(t)[^1]); // FileLogId is the LAST entity TVP column

        var r = t.Rows[0];
        Assert.Equal("AT", r["Code"]);
        Assert.Equal("Austria", r["Name"]);
        Assert.Equal("EU", r["ParentCode"]);
        Assert.Equal("Europe", r["ParentName"]);
        Assert.Equal(9, r["FileLogId"]);
    }

    [Fact]
    public void EntitySink_BuildTable_DedupsOnCode_KeepingLast()
    {
        var sink = new GasStorageEntitySqlSink(Opt(), NullLogger<GasStorageEntitySqlSink>.Instance);
        var first = new GasStorageEntityRow { FileLogId = 1, Code = "AT", Name = "Austria", ParentCode = "EU", ParentName = "Europe" };
        var last = new GasStorageEntityRow { FileLogId = 2, Code = "at", Name = "Österreich", ParentCode = "EU", ParentName = "Europe" };

        var t = BuildTable(sink, new List<GasStorageEntityRow> { first, last });

        var kept = Assert.Single(t.Rows.Cast<DataRow>()); // case-insensitive dedup on Code
        Assert.Equal("Österreich", kept["Name"]);         // last wins
    }

    // ---------------------------------------------------------------- GasStorage — FileLogId FIRST, EntityId SECOND, 22 columns

    // GasStorageRow is a class (not a record), so each test builds a fresh instance. The
    // canonical fully-populated sample row; only GasInStorage varies (for the dedup test).
    // Name/Code/Url were normalized out to EntityId (design §2/§7.2).
    private static GasStorageRow FullStorageRow(decimal? gasInStorage = 121.1238m, int entityId = 77) => new()
    {
        FileLogId = 55,
        EntityId = entityId,
        Date = new DateOnly(2026, 8, 13),
        GasDay = new DateOnly(2026, 8, 16),
        UpdatedAt = new DateTime(2026, 8, 17, 8, 0, 55),
        GasDayStart = new DateOnly(2026, 8, 13),
        GasDayEnd = new DateOnly(2026, 8, 14),
        GasInStorage = gasInStorage, Consumption = 903.9m, ConsumptionFull = 13.4m,
        Injection = 543.71m, Withdrawal = 6.5m, NetWithdrawal = -537.3m,
        WorkingGasVolume = 246.489m, InjectionCapacity = 4292.58m, WithdrawalCapacity = 7067.36m,
        ContractedCapacity = 194.1057m, AvailableCapacity = 58.6043m, CoveredCapacity = 100m,
        Status = "C", Trend = 0.24m, Full = 49.14m
    };

    // A row with every NULLable measure blanked (the status 'E'/'N' shape).
    private static GasStorageRow RowWithNullMeasures() => new()
    {
        FileLogId = 55,
        EntityId = 77,
        Date = new DateOnly(2026, 8, 13),
        GasDay = new DateOnly(2026, 8, 16),
        UpdatedAt = null,
        GasDayStart = new DateOnly(2026, 8, 13),
        GasDayEnd = new DateOnly(2026, 8, 14),
        GasInStorage = null, Consumption = null, ConsumptionFull = null,
        Injection = null, Withdrawal = null, NetWithdrawal = null,
        WorkingGasVolume = null, InjectionCapacity = null, WithdrawalCapacity = null,
        ContractedCapacity = null, AvailableCapacity = null, CoveredCapacity = null,
        Status = "N", Trend = null, Full = null
    };

    [Fact]
    public void StorageSink_BuildTable_Matches22ColTvpOrder_FileLogIdFirst_EntityIdSecond()
    {
        var sink = new GasStorageSqlSink(Opt(), NullLogger<GasStorageSqlSink>.Instance);

        var t = BuildTable(sink, new List<GasStorageRow> { FullStorageRow() });

        // arm.GasStorageTvp: FileLogId FIRST, EntityId SECOND, then the remaining 20 columns in table order.
        Assert.Equal(new[]
        {
            "FileLogId", "EntityId", "Date", "Gas_Day", "UpdatedAt", "GasDayStart", "GasDayEnd",
            "GasInStorage", "Consumption", "ConsumptionFull", "Injection", "Withdrawal", "NetWithdrawal",
            "WorkingGasVolume", "InjectionCapacity", "WithdrawalCapacity", "ContractedCapacity",
            "AvailableCapacity", "CoveredCapacity", "Status", "Trend", "Full"
        }, Names(t));
        Assert.Equal(22, t.Columns.Count);          // FileLogId + EntityId + 20 business columns
        Assert.Equal("FileLogId", Names(t)[0]);     // FileLogId is the FIRST storage TVP column
        Assert.Equal("EntityId", Names(t)[1]);      // EntityId is the SECOND

        Assert.Equal(new[]
        {
            typeof(int), typeof(int), typeof(DateTime), typeof(DateTime),
            typeof(DateTime), typeof(DateTime), typeof(DateTime),
            typeof(decimal), typeof(decimal), typeof(decimal), typeof(decimal), typeof(decimal), typeof(decimal),
            typeof(decimal), typeof(decimal), typeof(decimal), typeof(decimal), typeof(decimal), typeof(decimal),
            typeof(string), typeof(decimal), typeof(decimal)
        }, Types(t));

        var r = t.Rows[0];
        Assert.Equal(55, r["FileLogId"]);
        Assert.Equal(77, r["EntityId"]);
        Assert.Equal(new DateTime(2026, 8, 13), r["Date"]);          // DateOnly → midnight DateTime
        Assert.Equal(new DateTime(2026, 8, 16), r["Gas_Day"]);
        Assert.Equal(new DateTime(2026, 8, 17, 8, 0, 55), r["UpdatedAt"]);
        Assert.Equal(-537.3m, r["NetWithdrawal"]);                   // signed preserved through the TVP
        Assert.Equal("C", r["Status"]);
    }

    [Fact]
    public void StorageSink_BuildTable_NullMeasures_AreDbNull()
    {
        var sink = new GasStorageSqlSink(Opt(), NullLogger<GasStorageSqlSink>.Instance);

        var t = BuildTable(sink, new List<GasStorageRow> { RowWithNullMeasures() });

        var r = t.Rows[0];
        Assert.Equal(DBNull.Value, r["UpdatedAt"]);
        Assert.Equal(DBNull.Value, r["GasInStorage"]);
        Assert.Equal(DBNull.Value, r["NetWithdrawal"]);
        Assert.Equal(DBNull.Value, r["Trend"]);
        Assert.Equal(DBNull.Value, r["Full"]);
        Assert.Equal(DBNull.Value, r["CoveredCapacity"]);
        // NOT NULL business columns still carry their values.
        Assert.Equal(77, r["EntityId"]);
        Assert.Equal("N", r["Status"]);
    }

    [Fact]
    public void StorageSink_BuildTable_DedupsOnEntityAndGasDayStart_KeepingLast()
    {
        var sink = new GasStorageSqlSink(Opt(), NullLogger<GasStorageSqlSink>.Instance);
        var first = FullStorageRow(gasInStorage: 100m);
        var last = FullStorageRow(gasInStorage: 200m);

        var t = BuildTable(sink, new List<GasStorageRow> { first, last });

        var kept = Assert.Single(t.Rows.Cast<DataRow>()); // same (EntityId, GasDayStart) collapses
        Assert.Equal(200m, kept["GasInStorage"]);          // last wins
    }

    [Fact]
    public void StorageSink_BuildTable_SameGasDayStart_DifferentEntityId_KeptAsTwo()
    {
        var sink = new GasStorageSqlSink(Opt(), NullLogger<GasStorageSqlSink>.Instance);

        // Identical GasDayStart (2026-08-13) but two different EntityIds: proves EntityId is
        // part of the merge key, not GasDayStart alone — both rows must survive de-dup.
        var de = FullStorageRow(gasInStorage: 100m, entityId: 77);
        var at = FullStorageRow(gasInStorage: 200m, entityId: 78);

        var t = BuildTable(sink, new List<GasStorageRow> { de, at });

        Assert.Equal(2, t.Rows.Count);
        var entityIds = t.Rows.Cast<DataRow>().Select(r => (int)r["EntityId"]).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { 77, 78 }, entityIds);
        // Both GasDayStart values are the same day, confirming the key discriminated on EntityId.
        Assert.All(t.Rows.Cast<DataRow>(), r => Assert.Equal(new DateTime(2026, 8, 13), r["GasDayStart"]));
    }
}
