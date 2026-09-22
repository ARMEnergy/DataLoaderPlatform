using System.Reflection;
using DataLoader.Marex;
using Neon.API.Interfaces;
using SignalRClient.Contracts;
using Xunit;

namespace DataLoader.Marex.Tests;

/// <summary>
/// Pins the parts of the vendored SDK this loader depends on.
///
/// <para>The gateway reports <c>minVersion</c> 3.37.0.1522 while <c>lib/neon</c> holds
/// 3.15.1.22, so an SDK refresh is a question of when. Most of what this loader uses is
/// checked by the compiler — but three things are not, and each would be a silent
/// behaviour change rather than a build break:</para>
///
/// <list type="number">
///   <item>
///     <c>PasswordConnectionData.Domain</c> and <c>.ClientId</c>. The vendor's
///     integration note asserts these do NOT exist and recommends hand-rolling the
///     Auth0 call instead. They do exist, and the whole auth path depends on them.
///   </item>
///   <item>
///     The enum MEMBER NAMES. They are written into VARCHAR columns verbatim, so a
///     renamed member silently changes the data rather than failing the build.
///   </item>
///   <item>
///     The snapshot wrapper property names, which differ per entity
///     (<c>.Products</c>, <c>.Periods</c>, <c>.ClosingPrices</c>,
///     <c>.MarketStatistics</c>, <c>.PeriodGroups</c>) and are easy to get wrong when
///     re-wiring against a new SDK.
///   </item>
/// </list>
/// </summary>
public class MarexSdkContractTests
{
    [Fact]
    public void PasswordConnectionData_CarriesDomainAndClientId()
    {
        // ⚠ Contradicts the vendor's own integration note, which marks this [CONFIRM]
        // and says the Auth0 domain/client id are "not parameters of NeonApiClient or
        // PasswordConnectionData". They are, and using them is what makes the explicit
        // /oauth/token implementation the note recommends unnecessary.
        var type = typeof(PasswordConnectionData);

        foreach (var name in new[] { "Domain", "ClientId", "UserName", "Password" })
        {
            var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            Assert.True(property is not null, $"PasswordConnectionData.{name} has gone");
            Assert.Equal(typeof(string), property!.PropertyType);
            Assert.True(property.CanWrite, $"PasswordConnectionData.{name} is no longer settable");
        }
    }

    [Fact]
    public void NeonApiConfig_StillHasTheFourFieldsTheSessionSets()
    {
        var type = typeof(NeonApiConfig);

        Assert.NotNull(type.GetProperty("URL"));
        Assert.NotNull(type.GetProperty("Token"));
        // Name IS the SignalR hub name. If this ever stops being a string, or gains a
        // sibling that is the "real" hub name, the session needs rewriting.
        Assert.Equal(typeof(string), type.GetProperty("Name")!.PropertyType);
        Assert.Equal(typeof(bool), type.GetProperty("EnableTracing")!.PropertyType);
    }

    [Fact]
    public void ApiConnectionState_StillHasTheTwoFatalStates()
    {
        // The session short-circuits its wait on these; if either is renamed the loader
        // would sit out the whole snapshot timeout on a doomed connection.
        Assert.True(Enum.IsDefined(ApiConnectionState.Unauthorized));
        Assert.True(Enum.IsDefined(ApiConnectionState.IncompatibleVersion));
    }

    [Fact]
    public void INeonApiClient_StillExposesTheSixSnapshotEvents()
    {
        var type = typeof(INeonApiClient);

        var expected = new Dictionary<string, Type>
        {
            ["ExchangeDateSnapshot"] = typeof(EventHandler<DateTime>),
            ["PeriodGroupSnapshot"] = typeof(EventHandler<PeriodGroupSnapshotDto>),
            ["PeriodSnapshot"] = typeof(EventHandler<PeriodSnapshotDto>),
            ["ProductSnapshot"] = typeof(EventHandler<ProductSnapshotDto>),
            ["ClosingPriceSnapshot"] = typeof(EventHandler<ClosingPriceSnapshotDto>),
            // Note the singular "MarketStatistic" on the EVENT against the plural
            // "MarketStatistics" on the DTO — the vendor is inconsistent here.
            ["MarketStatisticSnapshot"] = typeof(EventHandler<MarketStatisticsSnapshotDto>)
        };

        foreach (var (name, handlerType) in expected)
        {
            var e = type.GetEvent(name);
            Assert.True(e is not null, $"INeonApiClient.{name} has gone");
            Assert.Equal(handlerType, e!.EventHandlerType);
        }
    }

    [Fact]
    public void SnapshotWrappers_StillExposeTheirOwnCollectionNames()
    {
        Assert.NotNull(typeof(PeriodGroupSnapshotDto).GetProperty("PeriodGroups"));
        Assert.NotNull(typeof(PeriodSnapshotDto).GetProperty("Periods"));
        Assert.NotNull(typeof(ProductSnapshotDto).GetProperty("Products"));
        Assert.NotNull(typeof(ClosingPriceSnapshotDto).GetProperty("ClosingPrices"));
        Assert.NotNull(typeof(MarketStatisticsSnapshotDto).GetProperty("MarketStatistics"));
    }

    [Fact]
    public void ClosingPriceDto_TimeAndPreviousTime_AreStillNonNullable()
    {
        // This is WHY MarexMapper.NullIfUnset exists: because the SDK types these as
        // non-nullable, the vendor has to send 0001-01-01 to mean "no value". If a new
        // SDK makes them Nullable<DateTime>, the guard becomes redundant rather than
        // wrong — but the reason should be re-read at that point.
        Assert.Equal(typeof(DateTime), typeof(ClosingPriceDto).GetProperty("Time")!.PropertyType);
        Assert.Equal(typeof(DateTime), typeof(ClosingPriceDto).GetProperty("PreviousTime")!.PropertyType);
    }

    [Theory]
    // The exact strings that land in arm.MarketStatistic.State / .UpdateReason /
    // .LastTradeAggressorSide / .LastTradeEventSource and arm.Product.ProductType /
    // .TradeChartDataSource. A rename here changes loaded DATA, silently.
    [InlineData(typeof(SignalRClient.Contracts.MarketState), "Open,Closed")]
    [InlineData(typeof(Common.Core.Enums.Side), "Unknown,Buy,Sell")]
    [InlineData(typeof(Common.Core.Enums.ProductType), "None,Outright,PeriodSpread,Regrade")]
    [InlineData(typeof(Common.Core.Enums.TradeChartDataSource), "Iris,Nbos")]
    [InlineData(typeof(Common.Core.Enums.TradeEventSource),
        "Unknown,TradeMatched,TradeModified,ManuallyEnteredTrade,TradeCancelled,MarketInitialised,SnapshotRequest")]
    public void EnumMemberNames_AreUnchanged(Type enumType, string expected)
    {
        Assert.Equal(expected.Split(','), Enum.GetNames(enumType));
    }

    [Fact]
    public void MarketStatisticsUpdateReason_MembersAreUnchanged()
    {
        // Listed separately because it is long enough to be unreadable as an InlineData.
        Assert.Equal(new[]
        {
            "Snapshot", "InstrumentDeleted", "InstrumentUpdated", "InstrumentAdded",
            "MarketSummaryAdd", "MarketSummaryUpdated", "AccountChanged", "CompanyChanged",
            "OrderAdded", "OrderDeleted", "OrderUpdated", "OrderFilled",
            "IndexAdded", "IndexUpdated", "IndexDeleted",
            "SetAdded", "SetUpdated", "SetDeleted"
        }, Enum.GetNames<Common.Core.Enums.MarketStatisticsUpdateReason>());
    }

    [Fact]
    public void VendoredAssemblies_AreTheExpectedVersion()
    {
        // Not a hard gate — an upgrade is expected eventually — but the version this
        // loader was verified against is worth recording where a change is visible.
        var version = typeof(INeonApiClient).Assembly.GetName().Version;

        Assert.Equal(new Version(3, 15, 1, 22), version);
    }

    [Fact]
    public void EveryDtoPropertyTheMapperReads_StillExists()
    {
        // A compile-time guarantee already, but this enumerates them in one place so an
        // SDK upgrade has a checklist rather than a scavenger hunt through the mapper.
        AssertProperties(typeof(ClosingPriceDto), "Id", "ProductId", "PeriodId", "Price", "Time", "PreviousPrice", "PreviousTime");
        AssertProperties(typeof(MarketStatisticsDto),
            "Id", "CciIndex", "Index", "TransactionCount", "Average", "PcChangeOnDay", "ChangeOnDay",
            "Change", "CumQty", "Open", "Low", "High", "MidPrice", "SettlementPrice", "LastTradeTime",
            "LastTradeEventSource", "LastTradeAggressorSide", "LastTradePrice", "State", "Pipeline",
            "Location", "ProductId", "Period", "Product", "UpdateReason");
        AssertProperties(typeof(PeriodDto),
            "Id", "Name", "DeliveryStart", "DeliveryEnd", "TradingStart", "TradingEnd",
            "NoticeOfShipment", "PeriodGroupId", "ExternalId", "ExternalSourceId");
        AssertProperties(typeof(PeriodGroupDto), "Id", "Name", "DisplayName", "IsHidden", "ExternalId", "ExternalSourceId");
        AssertProperties(typeof(ProductDto),
            "Id", "ExternalSourceId", "ExternalId", "PipelineName", "PipelineId", "IndexName", "IndexId",
            "LocationName", "LocationId", "GradeName", "GradeId", "CounterpartyDetailsVisible",
            "GroupName", "GroupId", "ClearingHouseId", "MarketName", "MarketId", "ProductType",
            "Name", "TradeChartDataSource");

        static void AssertProperties(Type type, params string[] names)
        {
            foreach (var name in names)
                Assert.True(type.GetProperty(name) is not null, $"{type.Name}.{name} has gone");
        }
    }

    [Fact]
    public void MapperReadsExactlyAsManyDtoPropertiesAsEachTableHasColumns()
    {
        // Keeps the checklist above honest against the descriptors.
        Assert.Equal(7,  MarexDescriptors.ClosingPrice.Columns.Count - 1);     // less ExchangeDate (loader-supplied)
        Assert.Equal(25, MarexDescriptors.MarketStatistic.Columns.Count - 1);  // less TradeDate    (loader-supplied)
        Assert.Equal(10, MarexDescriptors.Period.Columns.Count);
        Assert.Equal(6,  MarexDescriptors.PeriodGroup.Columns.Count);
        Assert.Equal(20, MarexDescriptors.Product.Columns.Count);
    }
}
