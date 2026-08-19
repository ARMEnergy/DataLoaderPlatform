using System.Data;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DataLoader.IHSPointLogic.Tests;

/// <summary>
/// The per-endpoint sinks' <c>BuildTable</c> — the C# side of the load-bearing TVP contract. Column
/// NAME + ORDER + TYPE in every DataTable must match <c>sql/IHSPointLogic/002</c> EXACTLY, with
/// <c>FileLogId</c> FIRST (the TVP binds BY POSITION; a silent reorder corrupts every loaded row). Each
/// sink also de-dups the batch on its merge key (last wins) so an in-batch duplicate cannot break the
/// server MERGE. BuildTable is pure (no DB) but protected on a sealed type, so it is invoked via
/// reflection (the CWG/AGSI/Platts posture). Expected orders are transcribed from the 002 TVP defs.
/// </summary>
public class SinkTests
{
    private static DataTable BuildTable(object sink, object rows)
    {
        var method = sink.GetType().GetMethod("BuildTable", BindingFlags.Instance | BindingFlags.NonPublic)
                     ?? throw new InvalidOperationException("BuildTable not found");
        return (DataTable)method.Invoke(sink, new[] { rows })!;
    }

    private static IOptions<IHSPointLogicSettings> Opt() =>
        Options.Create(new IHSPointLogicSettings { ConnectionString = "unused" });

    private static ILogger<T> Log<T>() => NullLogger<T>.Instance;

    private static string[] Names(DataTable t) => t.Columns.Cast<DataColumn>().Select(c => c.ColumnName).ToArray();
    private static Type[] Types(DataTable t) => t.Columns.Cast<DataColumn>().Select(c => c.DataType).ToArray();

    // ---------------------------------------------------------------- widest: PointMetadata (23 cols)

    private static PointMetadataRow MetaRow(int pointId = 33071, decimal? designCapacity = 0m) => new()
    {
        FileLogId = 55, PointId = pointId, PointLciId = "ACADA0001", Drn = null, PointName = "LAReg112",
        PointTypeId = 9, PointType = "Not Classified", StateId = 4536, State = "Louisiana", County = null,
        Region = null, PipelineId = 1, PipelineDisplayName = "Acadian Pipeline", PointIsActive = false,
        DesignCapacity = designCapacity, FlowDirectionId = 2, FlowDirection = "Delivery",
        DisplayName = "Total 112", LocProp = null, PointLatitude = 0m, PointLongitude = 0m,
        CountyId = null, RegionId = null
    };

    [Fact]
    public void PointMetadata_BuildTable_Matches23ColTvpOrder_FileLogIdFirst()
    {
        var sink = new PointMetadataSqlSink(Opt(), Log<PointMetadataSqlSink>());
        var t = BuildTable(sink, new List<PointMetadataRow> { MetaRow() });

        Assert.Equal(new[]
        {
            "FileLogId", "PointId", "PointLciId", "Drn", "PointName", "PointTypeId", "PointType", "StateId",
            "State", "County", "Region", "PipelineId", "PipelineDisplayName", "PointIsActive", "DesignCapacity",
            "FlowDirectionId", "FlowDirection", "DisplayName", "LocProp", "PointLatitude", "PointLongitude",
            "CountyId", "RegionId"
        }, Names(t));
        Assert.Equal(23, t.Columns.Count);
        Assert.Equal("FileLogId", Names(t)[0]);

        Assert.Equal(new[]
        {
            typeof(int), typeof(int), typeof(string), typeof(string), typeof(string), typeof(int), typeof(string),
            typeof(int), typeof(string), typeof(string), typeof(string), typeof(int), typeof(string), typeof(bool),
            typeof(decimal), typeof(int), typeof(string), typeof(string), typeof(string), typeof(decimal),
            typeof(decimal), typeof(int), typeof(int)
        }, Types(t));

        var r = t.Rows[0];
        Assert.Equal(55, r["FileLogId"]);
        Assert.Equal(0m, r["DesignCapacity"]);
        Assert.Equal(false, r["PointIsActive"]);
        Assert.Equal(DBNull.Value, r["Drn"]);    // null → DBNull
        Assert.Equal(DBNull.Value, r["County"]);
        Assert.Equal(DBNull.Value, r["CountyId"]);
    }

    [Fact]
    public void PointMetadata_DedupsOnPointId_KeepingLast()
    {
        var sink = new PointMetadataSqlSink(Opt(), Log<PointMetadataSqlSink>());
        var t = BuildTable(sink, new List<PointMetadataRow>
        {
            MetaRow(designCapacity: 100m),
            MetaRow(designCapacity: 200m) // same PointId → collapses, last wins
        });
        var kept = Assert.Single(t.Rows.Cast<DataRow>());
        Assert.Equal(200m, kept["DesignCapacity"]);
    }

    // ---------------------------------------------------------------- 16-measure: MarketBalances

    private static MarketBalancesUsLower48Row MbRow(DateOnly period, decimal wellhead = 1m) => new()
    {
        FileLogId = 7, TimePeriod = period, Wellhead = wellhead, ProductionLoss = 2m, DryGas = 3m,
        CanadaImports = 4m, LngSendout = 5m, TotalSupply = 6m, Power = 7m, Industrial = 8m,
        ResidentialCommercial = 9m, Subtotal = 10m, MexicoExports = 11m, LngFeedGas = 12m, PipeLoss = 13m,
        TotalDemand = 14m, Storage = 15m, BalancingItem = -16m
    };

    [Fact]
    public void MarketBalances_BuildTable_Matches18ColTvpOrder()
    {
        var sink = new MarketBalancesUsLower48SqlSink(Opt(), Log<MarketBalancesUsLower48SqlSink>());
        var t = BuildTable(sink, new List<MarketBalancesUsLower48Row> { MbRow(new DateOnly(2026, 8, 18)) });

        Assert.Equal(new[]
        {
            "FileLogId", "TimePeriod", "Wellhead", "ProductionLoss", "DryGas", "CanadaImports", "LngSendout",
            "TotalSupply", "Power", "Industrial", "ResidentialCommercial", "Subtotal", "MexicoExports",
            "LngFeedGas", "PipeLoss", "TotalDemand", "Storage", "BalancingItem"
        }, Names(t));
        Assert.Equal(18, t.Columns.Count);
        Assert.Equal("FileLogId", Names(t)[0]);
        Assert.Equal(new DateTime(2026, 8, 18), t.Rows[0]["TimePeriod"]); // DateOnly → midnight DateTime
        Assert.Equal(-16m, t.Rows[0]["BalancingItem"]);                    // signed preserved
    }

    [Fact]
    public void MarketBalances_DedupsOnTimePeriod_KeepingLast()
    {
        var sink = new MarketBalancesUsLower48SqlSink(Opt(), Log<MarketBalancesUsLower48SqlSink>());
        var period = new DateOnly(2026, 8, 18);
        var t = BuildTable(sink, new List<MarketBalancesUsLower48Row>
        {
            MbRow(period, wellhead: 100m),
            MbRow(period, wellhead: 200m)
        });
        var kept = Assert.Single(t.Rows.Cast<DataRow>());
        Assert.Equal(200m, kept["Wellhead"]);
    }

    // ---------------------------------------------------------------- 16-measure: SupplyAndDemand (keyed by Date)

    [Fact]
    public void SupplyAndDemand_BuildTable_Matches18ColTvpOrder_KeyedByDate()
    {
        var sink = new SupplyAndDemandSqlSink(Opt(), Log<SupplyAndDemandSqlSink>());
        var row = new SupplyAndDemandRow
        {
            FileLogId = 3, Date = new DateOnly(2026, 6, 19), Wellhead = 1m, ProductionLoss = 2m, DryGas = 3m,
            CanadaImports = 4m, LngSendout = 5m, TotalSupply = 6m, Power = 7m, Industrial = 8m,
            ResidentialCommercial = 9m, Subtotal = 10m, MexicoExports = 11m, LngFeedGas = 12m, PipeLoss = 13m,
            TotalDemand = 14m, Storage = 15m, BalancingItem = 16m
        };
        var t = BuildTable(sink, new List<SupplyAndDemandRow> { row });

        Assert.Equal(new[]
        {
            "FileLogId", "Date", "Wellhead", "ProductionLoss", "DryGas", "CanadaImports", "LngSendout",
            "TotalSupply", "Power", "Industrial", "ResidentialCommercial", "Subtotal", "MexicoExports",
            "LngFeedGas", "PipeLoss", "TotalDemand", "Storage", "BalancingItem"
        }, Names(t));
        Assert.Equal(18, t.Columns.Count);
        Assert.Equal(new DateTime(2026, 6, 19), t.Rows[0]["Date"]);
    }

    // ---------------------------------------------------------------- UsImportsExportsByPointsAggregate (12 cols)

    [Fact]
    public void UsImportsExports_BuildTable_Matches12ColTvpOrder_NullableGeographyToDbNull()
    {
        var sink = new UsImportsExportsByPointsAggregateSqlSink(Opt(), Log<UsImportsExportsByPointsAggregateSqlSink>());
        var row = new UsImportsExportsByPointsAggregateRow
        {
            FileLogId = 9, RunDate = new DateOnly(2026, 8, 18), FlowDate = new DateOnly(2026, 8, 18),
            PointName = "Freeport", PipelineName = "Fossil Energy", LedgerSide = "Delivery", Volume = -1113.0385m,
            State = null, County = null, Type = null, PointGroupName = null, DistrictName = null
        };
        var t = BuildTable(sink, new List<UsImportsExportsByPointsAggregateRow> { row });

        Assert.Equal(new[]
        {
            "FileLogId", "RunDate", "FlowDate", "PointName", "PipelineName", "LedgerSide", "Volume",
            "State", "County", "Type", "PointGroupName", "DistrictName"
        }, Names(t));
        Assert.Equal(12, t.Columns.Count);
        var r = t.Rows[0];
        Assert.Equal(-1113.0385m, r["Volume"]);
        Assert.Equal(DBNull.Value, r["State"]);
        Assert.Equal(DBNull.Value, r["DistrictName"]);
    }

    [Fact]
    public void UsImportsExports_DedupsOn5PartKey_KeepingLast()
    {
        var sink = new UsImportsExportsByPointsAggregateSqlSink(Opt(), Log<UsImportsExportsByPointsAggregateSqlSink>());
        UsImportsExportsByPointsAggregateRow Row(decimal v) => new()
        {
            FileLogId = 9, RunDate = new DateOnly(2026, 8, 18), FlowDate = new DateOnly(2026, 8, 18),
            PointName = "Freeport", PipelineName = "Fossil Energy", LedgerSide = "Delivery", Volume = v
        };
        var t = BuildTable(sink, new List<UsImportsExportsByPointsAggregateRow> { Row(1m), Row(2m) });
        var kept = Assert.Single(t.Rows.Cast<DataRow>()); // same (RunDate,FlowDate,PointName,PipelineName,LedgerSide)
        Assert.Equal(2m, kept["Volume"]);
    }

    // ---------------------------------------------------------------- a discovery table: County

    [Fact]
    public void County_BuildTable_MatchesTvpOrder_DedupsOnCountyAndState()
    {
        var sink = new CountySqlSink(Opt(), Log<CountySqlSink>());
        var t = BuildTable(sink, new List<CountyRow>
        {
            new() { FileLogId = 1, CountyId = 14117, StateId = 4520, Name = "Autauga" },
            new() { FileLogId = 2, CountyId = 14117, StateId = 4520, Name = "Autauga (updated)" }, // same key → last
            new() { FileLogId = 3, CountyId = 14126, StateId = 4520, Name = "Baldwin" }
        });

        Assert.Equal(new[] { "FileLogId", "CountyId", "StateId", "Name" }, Names(t));
        Assert.Equal(new[] { typeof(int), typeof(int), typeof(int), typeof(string) }, Types(t));
        Assert.Equal(2, t.Rows.Count); // the duplicate (CountyId,StateId) collapsed
        var autauga = Assert.Single(t.Rows.Cast<DataRow>(), r => (int)r["CountyId"] == 14117);
        Assert.Equal("Autauga (updated)", autauga["Name"]); // last wins
    }

    // ---------------------------------------------------------------- an SD table: SupplyAndDemandBySubRegion (6 cols)

    [Fact]
    public void SdBySubRegion_BuildTable_Matches6ColTvpOrder_DedupsOnFullKey()
    {
        var sink = new SupplyAndDemandBySubRegionSqlSink(Opt(), Log<SupplyAndDemandBySubRegionSqlSink>());
        SupplyAndDemandBySubRegionRow Row(decimal v) => new()
        {
            FileLogId = 4, SubRegionId = 26252, RegionId = 26105, Date = new DateOnly(2026, 8, 18),
            Product = "Plains Production", VolumeMmcfd = v
        };
        var t = BuildTable(sink, new List<SupplyAndDemandBySubRegionRow> { Row(10m), Row(20m) });

        Assert.Equal(new[] { "FileLogId", "SubRegionId", "RegionId", "Date", "Product", "VolumeMmcfd" }, Names(t));
        Assert.Equal(new[] { typeof(int), typeof(int), typeof(int), typeof(DateTime), typeof(string), typeof(decimal) }, Types(t));
        var kept = Assert.Single(t.Rows.Cast<DataRow>()); // same (SubRegionId,RegionId,Date,Product)
        Assert.Equal(20m, kept["VolumeMmcfd"]);
        Assert.Equal(new DateTime(2026, 8, 18), kept["Date"]);
        Assert.Equal(26252, kept["SubRegionId"]);
        Assert.Equal(26105, kept["RegionId"]);
    }

    // ---------------------------------------------------------------- Pipeline (nullable LegacyName)

    [Fact]
    public void PlPipeline_BuildTable_NullLegacyName_IsDbNull()
    {
        var sink = new PipelineSqlSink(Opt(), Log<PipelineSqlSink>());
        var t = BuildTable(sink, new List<PipelineRow>
        {
            new() { FileLogId = 1, PipelineId = 2, Name = "Algonquin Gas Transmission", LegacyName = "Algonquin" },
            new() { FileLogId = 1, PipelineId = 3, Name = "Alliance Pipeline", LegacyName = null }
        });
        Assert.Equal(new[] { "FileLogId", "PipelineId", "Name", "LegacyName" }, Names(t));
        var noLegacy = Assert.Single(t.Rows.Cast<DataRow>(), r => (int)r["PipelineId"] == 3);
        Assert.Equal(DBNull.Value, noLegacy["LegacyName"]);
    }
}
