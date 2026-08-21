using System.Data;
using System.Reflection;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DataLoader.IIR.Tests;

/// <summary>
/// The three per-endpoint sinks' <c>BuildTable</c> — the C# side of the load-bearing TVP contract.
/// Column NAME + ORDER + TYPE in every DataTable must match <c>sql/IIR/002_CreateIirTvpTypes.sql</c>
/// EXACTLY, with <c>FileLogId</c> FIRST (the TVP binds BY POSITION; a silent reorder corrupts every
/// loaded row). No <c>PlantPoint</c> / <c>ModifiedAtUtc</c> column crosses the TVP; lat/long are plain
/// <c>double</c>; <c>OfflineEvent.RunDate</c> is NOT a TVP column (it is the scalar <c>@RunDate</c> proc
/// param). BuildTable/MergeKey/AddScalarParameters are protected on sealed types, so they are invoked
/// via reflection (the CWG/AGSI/IHS posture). Expected orders/types are transcribed from the 002 TVP defs.
///
/// NOTE — a genuine testability gap (reported to CODER): the batch de-dup (last wins) and the
/// MergeChunkSize chunk-splitting live INSIDE <c>WriteAsync</c>, after <c>SqlConnection.OpenAsync</c>,
/// so they cannot be exercised without a live DB. Here we cover everything reachable without a
/// connection: the DataTable contract, the merge-key selector, the chunk-size clamp, and the fact that
/// BuildTable itself does NOT collapse duplicates (proving the de-dup must run earlier, in WriteAsync).
/// </summary>
public class SinkTests
{
    private static IOptions<IirSettings> Opt(int chunkSize = 10000) =>
        Options.Create(new IirSettings { ConnectionString = "unused", MergeChunkSize = chunkSize });

    private static MethodInfo Method(object o, string name)
    {
        for (var t = o.GetType(); t is not null; t = t.BaseType)
        {
            var m = t.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (m is not null) return m;
        }
        throw new InvalidOperationException($"{name} not found on {o.GetType().Name}");
    }

    private static FieldInfo Field(object o, string name)
    {
        for (var t = o.GetType(); t is not null; t = t.BaseType)
        {
            var f = t.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (f is not null) return f;
        }
        throw new InvalidOperationException($"{name} not found on {o.GetType().Name}");
    }

    private static DataTable BuildTable(object sink, object rows) =>
        (DataTable)Method(sink, "BuildTable").Invoke(sink, new[] { rows })!;

    private static int MergeKey(object sink, object row) =>
        (int)Method(sink, "MergeKey").Invoke(sink, new[] { row })!;

    private static string[] Names(DataTable t) => t.Columns.Cast<DataColumn>().Select(c => c.ColumnName).ToArray();
    private static Type[] Types(DataTable t) => t.Columns.Cast<DataColumn>().Select(c => c.DataType).ToArray();

    // ================================================================ Plant (66 cols)

    private static IirPlantRow PlantRow() => new()
    {
        FileLogId = 55, PlantId = 3207542, PlantName = "Acme", SecondaryFuel = null,
        Longitude = -95.363, Latitude = 29.760, StartupDate = new DateTime(1998, 5, 1)
    };

    [Fact]
    public void Plant_BuildTable_Matches66ColTvpOrder_FileLogIdFirst_NoGeographyNoModifiedAt()
    {
        var sink = new IirPlantSqlSink(Opt(), NullLogger<IirPlantSqlSink>.Instance);
        var t = BuildTable(sink, new List<IirPlantRow> { PlantRow() });

        Assert.Equal(new[]
        {
            "FileLogId", "PlantId", "PlantName", "PlantStatusDesc", "NoEmployees", "StartupDate", "LiveDate",
            "ReleaseDate", "OperationsLaborPreference", "PrimaryFuel", "SecondaryFuel", "IndustryCode",
            "IndustryCodeDesc", "PrimarySicId", "PrimarySicDesc", "PecZone", "MarketRegionId", "MarketRegionName",
            "ConfirmationStatus", "NercRegion", "NercSubRegionName", "ElectricalConnectionName", "TradingRegionId",
            "TradingRegionName", "CogenChp", "Metallurgical", "Thermal", "Placer", "OpenPit", "Quarry", "Strip",
            "Auger", "Dredging", "Drift", "Shaft", "Slope", "Longwall", "RoomPillar", "CutFill", "Caving", "Stoping",
            "InSituSolution", "Longitude", "Latitude", "WorldRegionId", "WorldRegionName", "Offshore",
            "MailingAddressLine1", "MailingCity", "MailingStateName", "MailingPostalCode", "MailingCountryName",
            "PhysicalAddressLine1", "PhysicalCity", "PhysicalStateName", "PhysicalPostalCode", "PhysicalCountryName",
            "PhysicalCountyName", "PhoneCC", "PhoneNumber", "ParentCompanyId", "ParentCompanyName",
            "ParentCompanyWebsite", "OperatorCompanyId", "OperatorCompanyName", "OperatorCompanyWebsite"
        }, Names(t));
        Assert.Equal(66, t.Columns.Count);
        Assert.Equal("FileLogId", Names(t)[0]);
        Assert.False(t.Columns.Contains("PlantPoint"));    // geography built in-proc, never in the TVP
        Assert.False(t.Columns.Contains("ModifiedAtUtc")); // DB-stamped, never in the TVP

        Assert.Equal(new[]
        {
            // FileLogId, PlantId
            typeof(int), typeof(int),
            // PlantName, PlantStatusDesc
            typeof(string), typeof(string),
            // NoEmployees
            typeof(int),
            // StartupDate, LiveDate, ReleaseDate
            typeof(DateTime), typeof(DateTime), typeof(DateTime),
            // OperationsLaborPreference
            typeof(int),
            // PrimaryFuel … ElectricalConnectionName (13 strings)
            typeof(string), typeof(string), typeof(string), typeof(string), typeof(string), typeof(string),
            typeof(string), typeof(string), typeof(string), typeof(string), typeof(string), typeof(string),
            typeof(string),
            // TradingRegionId, TradingRegionName
            typeof(int), typeof(string),
            // CogenChp … InSituSolution (18 flag ints)
            typeof(int), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int),
            typeof(int), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int),
            typeof(int), typeof(int),
            // Longitude, Latitude
            typeof(double), typeof(double),
            // WorldRegionId, WorldRegionName, Offshore
            typeof(int), typeof(string), typeof(int),
            // MailingAddressLine1 … OperatorCompanyWebsite (19 strings)
            typeof(string), typeof(string), typeof(string), typeof(string), typeof(string), typeof(string),
            typeof(string), typeof(string), typeof(string), typeof(string), typeof(string), typeof(string),
            typeof(string), typeof(string), typeof(string), typeof(string), typeof(string), typeof(string),
            typeof(string)
        }, Types(t));

        var r = t.Rows[0];
        Assert.Equal(55, r["FileLogId"]);
        Assert.Equal(-95.363, r["Longitude"]);            // lat/long carried as double
        Assert.Equal(29.760, r["Latitude"]);
        Assert.Equal(new DateTime(1998, 5, 1), r["StartupDate"]);
        Assert.Equal(DBNull.Value, r["SecondaryFuel"]);   // null → DBNull
        Assert.Equal(DBNull.Value, r["Longwall"]);        // absent flag → DBNull
    }

    [Fact]
    public void Plant_MergeKey_IsPlantId() =>
        Assert.Equal(3207542, MergeKey(new IirPlantSqlSink(Opt(), NullLogger<IirPlantSqlSink>.Instance), PlantRow()));

    // ================================================================ Unit (45 cols)

    private static IirUnitRow UnitRow() => new()
    {
        FileLogId = 7, UnitId = 55001, PlantId = 3207542, UnitName = "CDU-1",
        Capacity = 125000.5, PlantLatitude = 29.76, PlantLongitude = -95.36,
        ReleaseDate = new DateTime(2021, 3, 1)
    };

    [Fact]
    public void Unit_BuildTable_Matches45ColTvpOrder_FileLogIdFirst_LatLongDouble()
    {
        var sink = new IirUnitSqlSink(Opt(), NullLogger<IirUnitSqlSink>.Instance);
        var t = BuildTable(sink, new List<IirUnitRow> { UnitRow() });

        Assert.Equal(new[]
        {
            "FileLogId", "UnitId", "UnitName", "PlantId", "PlantName", "PlantStatusDesc", "PlantAddressLine1",
            "PlantCity", "PlantStateName", "PlantPostalCode", "PlantCountryName", "PlantCountyName", "MarketRegionId",
            "MarketRegionName", "WorldRegionId", "WorldRegionName", "TradingRegionId", "TradingRegionName",
            "UnitStatusDesc", "UnitStatusGroup", "HeaterCount", "UnitTypeId", "UnitTypeDesc", "UnitTypeGroup",
            "CapacityProductId", "Capacity", "CapacityUom", "PrimarySicId", "PrimarySicDesc", "AreaId", "AreaName",
            "PlantLatitude", "PlantLongitude", "Offshore", "IndustryCode", "IndustryCodeDesc", "Technology",
            "Renewable", "CogenChp", "PlantOperatorName", "PlantOwnerName", "PlantParentName", "PlantPhone",
            "ReleaseDate", "LiveDate"
        }, Names(t));
        Assert.Equal(45, t.Columns.Count);
        Assert.Equal("FileLogId", Names(t)[0]);
        Assert.False(t.Columns.Contains("PlantPoint"));
        Assert.False(t.Columns.Contains("ModifiedAtUtc"));

        Assert.Equal(new[]
        {
            typeof(int), typeof(int), typeof(string), typeof(int), typeof(string), typeof(string), typeof(string),
            typeof(string), typeof(string), typeof(string), typeof(string), typeof(string), typeof(string),
            typeof(string), typeof(int), typeof(string), typeof(int), typeof(string), typeof(string), typeof(string),
            typeof(int), typeof(string), typeof(string), typeof(string), typeof(string), typeof(double),
            typeof(string), typeof(string), typeof(string), typeof(int), typeof(string), typeof(double),
            typeof(double), typeof(int), typeof(string), typeof(string), typeof(string), typeof(int), typeof(int),
            typeof(string), typeof(string), typeof(string), typeof(string), typeof(DateTime), typeof(DateTime)
        }, Types(t));

        var r = t.Rows[0];
        Assert.Equal(125000.5, r["Capacity"]);
        Assert.Equal(29.76, r["PlantLatitude"]);
        Assert.Equal(-95.36, r["PlantLongitude"]);
        Assert.Equal(DBNull.Value, r["Technology"]); // null → DBNull
    }

    [Fact]
    public void Unit_MergeKey_IsUnitId() =>
        Assert.Equal(55001, MergeKey(new IirUnitSqlSink(Opt(), NullLogger<IirUnitSqlSink>.Instance), UnitRow()));

    // ================================================================ OfflineEvent (60 cols; RunDate NOT a TVP column)

    private static IirOfflineEventRow EventRow() => new()
    {
        FileLogId = 9, RunDate = new DateOnly(2026, 8, 19), EventId = 987654, EventKind = "O",
        PlantLatitude = 29.76, PlantLongitude = -95.36, UnitCapacity = 125000, OfflineCapacity = 50000,
        EventStartDate = new DateTime(2026, 8, 1)
    };

    [Fact]
    public void OfflineEvent_BuildTable_Matches60ColTvpOrder_FileLogIdFirst_NoRunDateColumn()
    {
        var sink = new IirOfflineEventSqlSink(Opt(), NullLogger<IirOfflineEventSqlSink>.Instance);
        var t = BuildTable(sink, new List<IirOfflineEventRow> { EventRow() });

        Assert.Equal(new[]
        {
            "FileLogId", "EventId", "EventKind", "EventType", "EventCause", "EventStatusDesc", "UnitId", "UnitName",
            "UnitStatusDesc", "IndustryCode", "IndustryCodeDesc", "PlantId", "PlantName", "PlantParentName",
            "PlantOwnerName", "PlantOperatorName", "PlantAddressLine1", "PlantCity", "PlantState", "PlantPostalCode",
            "PlantCountry", "PlantCounty", "PlantLatitude", "PlantLongitude", "AreaId", "AreaName", "Offshore",
            "GasRegionId", "GasRegionName", "MarketRegionId", "MarketRegionName", "TradingRegionId",
            "TradingRegionName", "PowerTradeRegion", "WorldRegionId", "WorldRegionName", "PecZone", "PrimarySicId",
            "UnitClassification", "Derate", "IsDerated", "ProductId", "ProductDescription", "UnitCapacity",
            "OfflineCapacity", "OfflineCapacityUOM", "EventStartDate", "EventEndDate", "EventDuration",
            "PrevStartDate", "PrevEndDate", "UnitTypeId", "UnitTypeDesc", "EventConfirmationStatus", "CogenChp",
            "EventDatePrecision", "KickoffSlippage", "EventComments", "LiveDate", "ReleaseDate"
        }, Names(t));
        Assert.Equal(60, t.Columns.Count);
        Assert.Equal("FileLogId", Names(t)[0]);
        Assert.False(t.Columns.Contains("RunDate"));       // RunDate is the scalar @RunDate param, NOT a TVP column
        Assert.False(t.Columns.Contains("PlantPoint"));
        Assert.False(t.Columns.Contains("ModifiedAtUtc"));

        Assert.Equal(new[]
        {
            typeof(int), typeof(int), typeof(string), typeof(string), typeof(string), typeof(string), typeof(int),
            typeof(string), typeof(string), typeof(string), typeof(string), typeof(int), typeof(string),
            typeof(string), typeof(string), typeof(string), typeof(string), typeof(string), typeof(string),
            typeof(string), typeof(string), typeof(string), typeof(double), typeof(double), typeof(int),
            typeof(string), typeof(int), typeof(string), typeof(string), typeof(string), typeof(string), typeof(int),
            typeof(string), typeof(string), typeof(int), typeof(string), typeof(string), typeof(string),
            typeof(string), typeof(double), typeof(int), typeof(int), typeof(string), typeof(double), typeof(double),
            typeof(string), typeof(DateTime), typeof(DateTime), typeof(int), typeof(DateTime), typeof(DateTime),
            typeof(string), typeof(string), typeof(string), typeof(int), typeof(string), typeof(int), typeof(string),
            typeof(DateTime), typeof(DateTime)
        }, Types(t));

        var r = t.Rows[0];
        Assert.Equal(29.76, r["PlantLatitude"]);
        Assert.Equal(125000d, r["UnitCapacity"]);
        Assert.Equal(new DateTime(2026, 8, 1), r["EventStartDate"]);
    }

    [Fact]
    public void OfflineEvent_MergeKey_IsEventId() =>
        Assert.Equal(987654, MergeKey(new IirOfflineEventSqlSink(Opt(), NullLogger<IirOfflineEventSqlSink>.Instance), EventRow()));

    // ---------------------------------------------------------------- OfflineEvent @RunDate scalar wiring

    [Fact]
    public void OfflineEvent_AddScalarParameters_SetsRunDateScalar_AsSqlDate()
    {
        var sink = new IirOfflineEventSqlSink(Opt(), NullLogger<IirOfflineEventSqlSink>.Instance);
        using var cmd = new SqlCommand("arm.usp_UpsertOfflineEvent");
        Method(sink, "AddScalarParameters").Invoke(sink, new object[] { cmd, new List<IirOfflineEventRow> { EventRow() } });

        Assert.True(cmd.Parameters.Contains("@RunDate"));
        var p = cmd.Parameters["@RunDate"];
        Assert.Equal(SqlDbType.Date, p.SqlDbType);
        Assert.Equal(new DateTime(2026, 8, 19), p.Value); // DateOnly RunDate → midnight DateTime
    }

    [Fact]
    public void Plant_AddScalarParameters_AddsNothing()
    {
        // Plant/Unit inherit the base no-op — no extra scalar rides the merge call.
        var sink = new IirPlantSqlSink(Opt(), NullLogger<IirPlantSqlSink>.Instance);
        using var cmd = new SqlCommand("arm.usp_UpsertPlant");
        Method(sink, "AddScalarParameters").Invoke(sink, new object[] { cmd, new List<IirPlantRow> { PlantRow() } });
        Assert.Equal(0, cmd.Parameters.Count);
    }

    // ---------------------------------------------------------------- chunk-size clamp + BuildTable does NOT de-dup

    [Theory]
    [InlineData(10000, 10000)]
    [InlineData(0, 1)]    // Math.Max(1, MergeChunkSize)
    [InlineData(-5, 1)]
    public void ChunkSize_IsClampedToAtLeastOne(int configured, int expected)
    {
        var sink = new IirPlantSqlSink(Opt(configured), NullLogger<IirPlantSqlSink>.Instance);
        Assert.Equal(expected, (int)Field(sink, "_chunkSize").GetValue(sink)!);
    }

    // ================================================================ Summary (id-catalog census) TVP

    private static IirSummarySqlSink CensusSink() =>
        new("arm.usp_UpsertPlantSummary", "arm.PlantSummaryTvp", Opt(), NullLogger<IirSummarySqlSink>.Instance);

    [Fact]
    public void Census_BuildTable_MatchesTvpOrder_IdOnly_NoFileLogId()
    {
        var sink = CensusSink();
        var t = BuildTable(sink, new List<IirSummaryRow>
        {
            new() { RunDate = new DateOnly(2026, 8, 20), EntityId = 3207542, Latitude = 29.76, Longitude = -95.36 }
        });

        // sql/IIR/002 census TVP: (<Id>) — ID-ONLY, NO FileLogId, NO RunDate column.
        Assert.Equal(new[] { "EntityId" }, Names(t));
        Assert.Equal(new[] { typeof(int) }, Types(t));
        Assert.False(t.Columns.Contains("FileLogId"));
        Assert.False(t.Columns.Contains("RunDate"));

        Assert.Equal(3207542, t.Rows[0]["EntityId"]);
    }

    [Fact]
    public void Census_LatLong_AreNotPersisted_CarryForwardOnly()
    {
        // The row still CARRIES lat/long (it seeds the reader's §6.3 carry-forward map), but the
        // census TVP no longer has those columns — they must not reach the DB.
        var sink = CensusSink();
        var t = BuildTable(sink, new List<IirSummaryRow>
        {
            new() { RunDate = new DateOnly(2026, 8, 20), EntityId = 10, Latitude = 29.76, Longitude = -95.36 }
        });
        Assert.False(t.Columns.Contains("Latitude"));
        Assert.False(t.Columns.Contains("Longitude"));
        Assert.Single(t.Columns);
    }

    [Fact]
    public void Census_MergeKey_IsEntityId_AndRunDateScalarWired()
    {
        var sink = CensusSink();
        var row = new IirSummaryRow { RunDate = new DateOnly(2026, 8, 20), EntityId = 42 };
        Assert.Equal(42, MergeKey(sink, row));

        using var cmd = new SqlCommand("arm.usp_UpsertPlantSummary");
        Method(sink, "AddScalarParameters").Invoke(sink, new object[] { cmd, new List<IirSummaryRow> { row } });
        Assert.True(cmd.Parameters.Contains("@RunDate"));
        Assert.Equal(SqlDbType.Date, cmd.Parameters["@RunDate"].SqlDbType);
        Assert.Equal(new DateTime(2026, 8, 20), cmd.Parameters["@RunDate"].Value);
    }

    // ================================================================ BuildTable does not de-dup

    [Fact]
    public void BuildTable_DoesNotDeDup_ItselfNeitherCollapsesDuplicates_DeDupLivesInDedupAndChunk()
    {
        // IIR's de-dup lives in the pure IirSqlSinkBase.DedupAndChunk seam (asserted in
        // DedupAndChunkTests), NOT in BuildTable — so BuildTable faithfully emits one DataRow per input
        // row it is handed. This pins that separation of concerns.
        var sink = new IirPlantSqlSink(Opt(), NullLogger<IirPlantSqlSink>.Instance);
        var dupA = new IirPlantRow { FileLogId = 1, PlantId = 3207542, PlantName = "first" };
        var dupB = new IirPlantRow { FileLogId = 2, PlantId = 3207542, PlantName = "second" };

        var t = BuildTable(sink, new List<IirPlantRow> { dupA, dupB });

        Assert.Equal(2, t.Rows.Count); // no collapse here — same PlantId kept twice
    }
}
