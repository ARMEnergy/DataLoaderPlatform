using System.Reflection;
using Common.Core.Utility;
using DataLoader.Marex;
using Microsoft.Extensions.Logging;
using Neon.API.Interfaces;
using SignalRClient.Contracts;
using SignalRClient.Interfaces;

namespace DataLoader.Marex.Tests;

/// <summary>Locates the repository's <c>sql/Marex</c> folder from the test binaries.</summary>
internal static class RepoPaths
{
    internal static string SqlDirectory
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                var candidate = Path.Combine(dir.FullName, "sql", "Marex");
                if (Directory.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }

            throw new DirectoryNotFoundException(
                "Could not locate sql/Marex above " + AppContext.BaseDirectory);
        }
    }

    internal static string ReadSql(string fileName) =>
        File.ReadAllText(Path.Combine(SqlDirectory, fileName));
}

/// <summary>Captures log records so tests can assert on what was and was not logged.</summary>
internal sealed class RecordingLogger : ILogger
{
    internal List<(LogLevel Level, string Message)> Records { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        lock (Records) Records.Add((logLevel, formatter(state, exception)));
    }

    internal bool Contains(string fragment) =>
        Records.Any(r => r.Message.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    internal IEnumerable<string> Messages => Records.Select(r => r.Message);
}

/// <summary>Builders for the vendor DTOs, so the tests read as data rather than as setup.</summary>
internal static class Dto
{
    /// <summary>
    /// Note there is no `id` parameter: <c>ClosingPriceDto.Id</c> is a COMPUTED property,
    /// <c>$"{ProductId}:{PeriodId}"</c>, not a settable field. That is why
    /// <c>arm.ClosingPrice.ClosingPriceId</c> can never be null or blank, and why its
    /// width is bounded at 32 characters (int + long + a colon) against VARCHAR(50).
    /// </summary>
    internal static ClosingPriceDto ClosingPrice(
        int productId = 1001, long periodId = 5001,
        decimal? price = 12.5m, DateTime? time = null,
        decimal? previousPrice = 11.5m, DateTime? previousTime = null) => new()
    {
        ProductId = productId,
        PeriodId = periodId,
        Price = price,
        Time = time ?? new DateTime(2026, 9, 18, 23, 0, 0, DateTimeKind.Utc),
        PreviousPrice = previousPrice,
        PreviousTime = previousTime ?? new DateTime(2026, 9, 17, 23, 0, 0, DateTimeKind.Utc)
    };

    internal static MarketStatisticsDto MarketStatistic(
        long id = 17428, long productId = 3862, string? index = null, string? cciIndex = null,
        string? product = "ARV", string? period = "2027",
        string? location = "", string? pipeline = "") => new()
    {
        Id = id,
        ProductId = productId,
        Index = index,
        CciIndex = cciIndex,
        Product = product,
        Period = period,
        Location = location,
        Pipeline = pipeline,
        TransactionCount = 3,
        CumQty = 1000m,
        SettlementPrice = -8.75m,
        State = SignalRClient.Contracts.MarketState.Open,
        LastTradeAggressorSide = Common.Core.Enums.Side.Buy,
        LastTradeEventSource = Common.Core.Enums.TradeEventSource.TradeMatched,
        UpdateReason = Common.Core.Enums.MarketStatisticsUpdateReason.Snapshot
    };

    internal static PeriodDto Period(
        long id = 1001, string name = "Jun-16", int periodGroupId = 1001,
        DateTime? deliveryStart = null, DateTime? deliveryEnd = null,
        DateTime? tradingStart = null, DateTime? tradingEnd = null,
        DateTime? noticeOfShipment = null) => new()
    {
        Id = id,
        Name = name,
        PeriodGroupId = periodGroupId,
        DeliveryStart = deliveryStart ?? new DateTime(2016, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        DeliveryEnd = deliveryEnd ?? new DateTime(2016, 7, 1, 0, 0, 0, DateTimeKind.Utc),
        TradingStart = tradingStart ?? new DateTime(2016, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        TradingEnd = tradingEnd ?? new DateTime(2016, 7, 1, 0, 0, 0, DateTimeKind.Utc),
        NoticeOfShipment = noticeOfShipment
    };

    internal static PeriodGroupDto PeriodGroup(
        long id = 1001, string name = "Months", string displayName = "Months",
        bool isHidden = false) => new()
    {
        Id = id, Name = name, DisplayName = displayName, IsHidden = isHidden
    };

    internal static ProductDto Product(
        long id = 4601, string name = "SW CSW", long marketId = 1002,
        string marketName = "Crude Oil Physical", int? clearingHouseId = null) => new()
    {
        Id = id,
        Name = name,
        MarketId = marketId,
        MarketName = marketName,
        ClearingHouseId = clearingHouseId,
        GroupId = 1015,
        GroupName = "Sweet",
        GradeId = 106,
        GradeName = "SW",
        LocationId = 22,
        LocationName = "Edmonton",
        IndexId = 142,
        IndexName = "WTI CMA",
        PipelineId = 9,
        PipelineName = "Central Alberta",
        ProductType = Common.Core.Enums.ProductType.Outright,
        TradeChartDataSource = Common.Core.Enums.TradeChartDataSource.Nbos,
        CounterpartyDetailsVisible = true
    };
}

/// <summary>
/// A stand-in for the vendor client.
///
/// <para>Only the members this loader touches do anything; the rest of
/// <see cref="INeonApiClient"/> is the vendor's trading surface (create/modify/cancel
/// orders and trades, historical requests) which this loader deliberately never calls,
/// so they throw if something ever starts to.</para>
/// </summary>
internal sealed class FakeNeonApiClient : INeonApiClient
{
    internal bool TokenSucceeds { get; set; } = true;
    internal string TokenMessage { get; set; } = "";
    internal string TokenValue { get; set; } = "a.fake.token";
    internal bool ConnectSucceeds { get; set; } = true;
    internal string ConnectMessage { get; set; } = "";

    internal PasswordConnectionData? TokenRequest { get; private set; }
    internal NeonApiConfig? ConnectConfig { get; private set; }
    internal int DisconnectCount { get; private set; }
    internal int DisposeCount { get; private set; }

    /// <summary>How many snapshot handlers were attached at the moment Connect was called.</summary>
    internal int HandlersAttachedAtConnect { get; private set; } = -1;

    /// <summary>Raised when Connect is called, so a test can push a snapshot at exactly that point.</summary>
    internal event Action? OnConnect;

    public ApiConnectionState ConnectionStatus { get; private set; } = ApiConnectionState.Disconnected;

    public Task<BoolResult<string>> GetToken(PasswordConnectionData auth0connectionParams)
    {
        TokenRequest = auth0connectionParams;
        return Task.FromResult(new BoolResult<string>
        {
            IsSuccess = TokenSucceeds,
            Message = TokenMessage,
            Data = TokenSucceeds ? TokenValue : null!
        });
    }

    public BoolResult<string> Connect(NeonApiConfig connectionParams)
    {
        ConnectConfig = connectionParams;
        HandlersAttachedAtConnect = CountSnapshotHandlers();
        ConnectionStatus = ApiConnectionState.Connecting;
        RaiseState(ApiConnectionState.Connecting);
        OnConnect?.Invoke();
        return new BoolResult<string>
        {
            IsSuccess = ConnectSucceeds, Message = ConnectMessage, Data = ""
        };
    }

    public void Disconnect()
    {
        DisconnectCount++;
        ConnectionStatus = ApiConnectionState.Disconnected;
    }

    public void Dispose() => DisposeCount++;

    private int CountSnapshotHandlers() =>
        (ExchangeDateSnapshot?.GetInvocationList().Length ?? 0)
        + (PeriodGroupSnapshot?.GetInvocationList().Length ?? 0)
        + (PeriodSnapshot?.GetInvocationList().Length ?? 0)
        + (ProductSnapshot?.GetInvocationList().Length ?? 0)
        + (ClosingPriceSnapshot?.GetInvocationList().Length ?? 0)
        + (MarketStatisticSnapshot?.GetInvocationList().Length ?? 0);

    // ------------------------------------------------------------------ raisers

    internal void RaiseState(ApiConnectionState state, string message = "")
    {
        ConnectionStatus = state;
        ConnectionStateChanged?.Invoke(this, new GatewayClientStateArgs(state, message));
    }

    internal void RaiseExchangeDate(DateTime date) => ExchangeDateSnapshot?.Invoke(this, date);

    internal void RaisePeriodGroups(params PeriodGroupDto[] items) =>
        PeriodGroupSnapshot?.Invoke(this, new PeriodGroupSnapshotDto { PeriodGroups = items.ToList() });

    internal void RaisePeriods(params PeriodDto[] items) =>
        PeriodSnapshot?.Invoke(this, new PeriodSnapshotDto { Periods = items.ToList() });

    internal void RaiseProducts(params ProductDto[] items) =>
        ProductSnapshot?.Invoke(this, new ProductSnapshotDto { Products = items.ToList() });

    internal void RaiseClosingPrices(params ClosingPriceDto[] items) =>
        ClosingPriceSnapshot?.Invoke(this, new ClosingPriceSnapshotDto { ClosingPrices = items.ToList() });

    internal void RaiseMarketStatistics(params MarketStatisticsDto[] items) =>
        MarketStatisticSnapshot?.Invoke(this, new MarketStatisticsSnapshotDto { MarketStatistics = items.ToList() });

    /// <summary>A complete, valid opening snapshot.</summary>
    internal void RaiseFullSnapshot(DateTime? exchangeDate = null)
    {
        RaiseExchangeDate(exchangeDate ?? new DateTime(2026, 9, 21, 0, 0, 0, DateTimeKind.Utc));
        RaisePeriodGroups(Dto.PeriodGroup());
        RaisePeriods(Dto.Period());
        RaiseProducts(Dto.Product());
        RaiseClosingPrices(Dto.ClosingPrice());
        RaiseMarketStatistics(Dto.MarketStatistic());
    }

    // ------------------------------------------------------------------- events

    public event EventHandler<GatewayClientStateArgs>? ConnectionStateChanged;
    public event EventHandler<DateTime>? ExchangeDateSnapshot;
    public event EventHandler<PeriodGroupSnapshotDto>? PeriodGroupSnapshot;
    public event EventHandler<PeriodSnapshotDto>? PeriodSnapshot;
    public event EventHandler<ProductSnapshotDto>? ProductSnapshot;
    public event EventHandler<ClosingPriceSnapshotDto>? ClosingPriceSnapshot;
    public event EventHandler<MarketStatisticsSnapshotDto>? MarketStatisticSnapshot;

    // Declared by the interface; this loader subscribes to none of them.
#pragma warning disable CS0067
    public event EventHandler<InstrumentSnapshotDto>? InstrumentSnapshot;
    public event EventHandler<InstrumentUpdateDto>? InstrumentUpdate;
    public event EventHandler<PeriodGroupUpdateDto>? PeriodGroupUpdate;
    public event EventHandler<PeriodUpdateDto>? PeriodUpdate;
    public event EventHandler<ProductUpdateDto>? ProductUpdate;
    public event EventHandler<OrderSnapshotDto>? OrderSnapshot;
    public event EventHandler<OrderUpdateDto>? OrderUpdate;
    public event EventHandler<AccountSnapshotDto>? AccountSnapshot;
    public event EventHandler<AccountUpdateDto>? AccountUpdate;
    public event EventHandler<TradeSnapshotDto>? TradeSnapshot;
    public event EventHandler<TradeUpdateDto>? TradeUpdate;
    public event EventHandler<MarketStatisticsUpdateDto>? MarketStatisticUpdate;
    public event EventHandler<ClosingPriceUpdateDto>? ClosingPriceUpdate;
    public event EventHandler<AlertNotificationDto>? AlertNotification;
#pragma warning restore CS0067

    // ---------------------------------------------------- never called by Marex

    private static T Never<T>([System.Runtime.CompilerServices.CallerMemberName] string member = "") =>
        throw new InvalidOperationException(
            $"The Marex loader must never call INeonApiClient.{member} — it is read-only against the " +
            "gateway's snapshot. If this fires, a change has started using the vendor's trading or " +
            "historical surface and needs reviewing.");

    public IRequestContext<TradeResponseDto> RequestTrades(TradeRequestDto request) => Never<IRequestContext<TradeResponseDto>>();
    public IRequestContext<HistoricIndexPricesResponseDto> RequestIndexPrices(HistoricIndexPricesRequestDto request) => Never<IRequestContext<HistoricIndexPricesResponseDto>>();
    public IRequestContext<EnterSettlementPricesResponseDto> EnterSettlementPrices(EnterSettlementPricesRequestDto request) => Never<IRequestContext<EnterSettlementPricesResponseDto>>();
    public IRequestContext<HistoricClosingPriceResponseDto> RequestClosingPrices(HistoricClosingPriceRequestDto request) => Never<IRequestContext<HistoricClosingPriceResponseDto>>();
    public IRequestContext<HistoricTradeResponseDto> RequestHistoricTrades(HistoricTradeRequestDto request) => Never<IRequestContext<HistoricTradeResponseDto>>();
    public IRequestContext<HistoricChartDataResponseDto> RequestHistoricChartData(HistoricChartDataRequestDto request) => Never<IRequestContext<HistoricChartDataResponseDto>>();
    public IRequestContext<AlertResponseDto> RequestAlert(AlertRequestDto request) => Never<IRequestContext<AlertResponseDto>>();
    public IRequestContext<LayoutResponseDto> RequestLayout(LayoutRequestDto request) => Never<IRequestContext<LayoutResponseDto>>();
    public IRequestContext<HistoricStaticDataResponseDto> RequestIndexStaticData() => Never<IRequestContext<HistoricStaticDataResponseDto>>();
    public IRequestContext<HistoricStaticDataResponseDto> RequestMarketStaticData(Common.Core.Enums.StaticDataCategory category, MarketStaticDataRequestDto request) => Never<IRequestContext<HistoricStaticDataResponseDto>>();
    public IRequestContext<HistoricStaticDataResponseDto> RequestProductStaticData(Common.Core.Enums.StaticDataCategory category, ProductStaticDataRequestDto request) => Never<IRequestContext<HistoricStaticDataResponseDto>>();
    public IRequestContext<HistoricStaticDataResponseDto> RequestPeriodStaticData(Common.Core.Enums.StaticDataCategory category, PeriodStaticDataRequestDto request) => Never<IRequestContext<HistoricStaticDataResponseDto>>();
    public IRequestContext<HistoricStaticDataResponseDto> RequestPeriodGroupStaticData(Common.Core.Enums.StaticDataCategory category, PeriodGroupStaticDataRequestDto request) => Never<IRequestContext<HistoricStaticDataResponseDto>>();
    public IRequestContext<EnterIndexResponseDto> EnterIndex(EnterIndexRequestDto request) => Never<IRequestContext<EnterIndexResponseDto>>();
    public IRequestContext<AlertNotificationResponseDto> RequestAlertNotification(AlertNotificationRequestDto request) => Never<IRequestContext<AlertNotificationResponseDto>>();
    public IRequestContext<CreateOrderResponseDto> CreateOrder(CreateOrderRequestDto request) => Never<IRequestContext<CreateOrderResponseDto>>();
    public IRequestContext<ModifyOrderResponseDto> ModifyOrder(ModifyOrderRequestDto request) => Never<IRequestContext<ModifyOrderResponseDto>>();
    public IRequestContext<CancelOrderResponseDto> CancelOrder(CancelOrderRequestDto request) => Never<IRequestContext<CancelOrderResponseDto>>();
    public IRequestContext<CreateTradeResponseDto> CreateTrade(CreateTradeRequestDto request) => Never<IRequestContext<CreateTradeResponseDto>>();
    public IRequestContext<TradeModifyResponseDto> ModifyTrade(TradeModifyRequestDto request) => Never<IRequestContext<TradeModifyResponseDto>>();
    public IRequestContext<TradeCancelResponseDto> CancelTrade(TradeCancelRequestDto request) => Never<IRequestContext<TradeCancelResponseDto>>();
}

/// <summary>Hands the session a <see cref="FakeNeonApiClient"/>.</summary>
internal sealed class FakeNeonClientFactory : IMarexNeonClientFactory
{
    internal FakeNeonApiClient Client { get; }

    internal FakeNeonClientFactory(FakeNeonApiClient client) => Client = client;

    public INeonApiClient Create() => Client;
}
