using SignalRClient.Contracts;

namespace DataLoader.Marex;

/// <summary>
/// Projects vendor DTOs into TVP rows.
///
/// <para>Every method here walks its descriptor's column list IN ORDER. Adding a column
/// to <see cref="MarexDescriptors"/> without adding a value here (or the other way
/// round) is caught by the sink's length check and by
/// <c>MarexMapperTests.EveryRow_HasExactlyTheDescriptorsColumnCount</c>, not left to
/// load shifted.</para>
/// </summary>
internal static class MarexMapper
{
    /// <summary>
    /// SQL Server's <c>DATETIME2</c> accepts year 1, so a default <c>DateTime</c> would
    /// store happily and silently — as a real timestamp in the year 1.
    ///
    /// <para>This is not hypothetical. <c>ClosingPriceDto.Time</c> and
    /// <c>.PreviousTime</c> are non-nullable <c>DateTime</c> in the SDK even though the
    /// values are genuinely optional, so the vendor sends <c>0001-01-01T00:00:00Z</c>
    /// for "none" — observed live on the rows whose <c>PreviousPrice</c> is null. Both
    /// target columns are nullable, so NULL is the honest representation.</para>
    ///
    /// <para>The test is on the YEAR rather than equality with <c>DateTime.MinValue</c>
    /// so that a value carrying a stray offset conversion (<c>0001-01-01T01:00:00</c>)
    /// is caught too.</para>
    /// </summary>
    internal static object NullIfUnset(DateTime value) =>
        value.Year <= 1 ? DBNull.Value : value;

    /// <inheritdoc cref="NullIfUnset(DateTime)"/>
    internal static object NullIfUnset(DateTime? value) =>
        value is null || value.Value.Year <= 1 ? DBNull.Value : value.Value;

    /// <summary>
    /// Date part only, for the <c>DATE</c> columns whose DTO property is a
    /// <c>DateTime</c>.
    /// </summary>
    internal static object DateOrNull(DateTime value) =>
        value.Year <= 1 ? DBNull.Value : value.Date;

    /// <summary>
    /// An enum column's value is its NAME — every such column in this schema is
    /// VARCHAR, so the name is what was asked for.
    ///
    /// <para>A value the shipped SDK's enum does not define (the gateway is on a newer
    /// build than <c>lib/neon</c>) stringifies to its number instead of throwing, which
    /// lands a visible "37" in the column rather than failing the batch or, worse,
    /// silently mapping it onto a neighbouring name.</para>
    /// </summary>
    internal static object EnumName<TEnum>(TEnum value) where TEnum : struct, Enum =>
        value.ToString();

    /// <summary>Empty and whitespace-only vendor strings become NULL, not ''.</summary>
    private static object Str(string? value) =>
        string.IsNullOrWhiteSpace(value) ? DBNull.Value : value;

    private static object Num<T>(T? value) where T : struct => value ?? (object)DBNull.Value;

    // ---------------------------------------------------------------- ClosingPrice

    /// <summary>
    /// <c>ClosingPriceDto</c> -> <c>arm.ClosingPrice</c>, stamped with the run's
    /// exchange date.
    /// </summary>
    internal static MarexRow Map(ClosingPriceDto dto, DateTime exchangeDate) => new()
    {
        Values = new[]
        {
            exchangeDate.Date,          // ExchangeDate   (key)
            (object)dto.Id,             // ClosingPriceId (key)
            dto.ProductId,              // ProductId
            dto.PeriodId,               // PeriodId
            Num(dto.Price),             // Price
            NullIfUnset(dto.Time),      // Time
            Num(dto.PreviousPrice),     // PreviousPrice
            NullIfUnset(dto.PreviousTime) // PreviousTime
        }
    };

    // ------------------------------------------------------------- MarketStatistic

    /// <summary>
    /// <c>MarketStatisticsDto</c> -> <c>arm.MarketStatistic</c>, stamped with the run's
    /// exchange date as <c>TradeDate</c>.
    ///
    /// <para><c>CumQty</c> and <c>TransactionCount</c> are non-nullable on the DTO and
    /// are written as-is; every other numeric is <c>Nullable</c> and keeps its null.</para>
    /// </summary>
    internal static MarexRow Map(MarketStatisticsDto dto, DateTime exchangeDate) => new()
    {
        Values = new object[]
        {
            exchangeDate.Date,                      // TradeDate (key)
            dto.Id,                                 // Id        (key)
            Str(dto.CciIndex),                      // CciIndex
            Str(dto.Index),                         // Index
            dto.TransactionCount,                   // TransactionCount
            Num(dto.Average),                       // Average
            Num(dto.PcChangeOnDay),                 // PcChangeOnDay
            Num(dto.ChangeOnDay),                   // ChangeOnDay
            Num(dto.Change),                        // Change
            dto.CumQty,                             // CumQty
            Num(dto.Open),                          // Open
            Num(dto.Low),                           // Low
            Num(dto.High),                          // High
            Num(dto.MidPrice),                      // MidPrice
            Num(dto.SettlementPrice),               // SettlementPrice
            NullIfUnset(dto.LastTradeTime),         // LastTradeTime
            EnumName(dto.LastTradeEventSource),     // LastTradeEventSource
            EnumName(dto.LastTradeAggressorSide),   // LastTradeAggressorSide
            Num(dto.LastTradePrice),                // LastTradePrice
            EnumName(dto.State),                    // State
            Str(dto.Pipeline),                      // Pipeline
            Str(dto.Location),                      // Location
            dto.ProductId,                          // ProductId
            Str(dto.Period),                        // Period
            Str(dto.Product),                       // Product
            EnumName(dto.UpdateReason)              // UpdateReason
        }
    };

    // ---------------------------------------------------------------------- Period

    /// <summary>
    /// <c>PeriodDto</c> -> <c>arm.Period</c>.
    ///
    /// <para>The four <c>DATE</c> columns truncate a <c>DateTime</c>; see the
    /// descriptor for why that is safe against this source. <c>NoticeOfShipment</c> keeps
    /// its time — the live values are all <c>23:59:59</c>, which a <c>DATE</c> column
    /// would have thrown away.</para>
    /// </summary>
    internal static MarexRow Map(PeriodDto dto) => new()
    {
        Values = new object[]
        {
            dto.Id,                             // PeriodId (key)
            Str(dto.Name),                      // Name
            DateOrNull(dto.DeliveryStart),      // DeliveryStart
            DateOrNull(dto.DeliveryEnd),        // DeliveryEnd
            DateOrNull(dto.TradingStart),       // TradingStart
            DateOrNull(dto.TradingEnd),         // TradingEnd
            NullIfUnset(dto.NoticeOfShipment),  // NoticeOfShipment
            dto.PeriodGroupId,                  // PeriodGroupId
            Num(dto.ExternalId),                // ExternalId
            Num(dto.ExternalSourceId)           // ExternalSourceId
        }
    };

    // ----------------------------------------------------------------- PeriodGroup

    /// <summary><c>PeriodGroupDto</c> -> <c>arm.PeriodGroup</c>.</summary>
    internal static MarexRow Map(PeriodGroupDto dto) => new()
    {
        Values = new object[]
        {
            dto.Id,                     // PeriodGroupId (key)
            Str(dto.Name),              // Name
            Str(dto.DisplayName),       // DisplayName
            dto.IsHidden,               // IsHidden
            Num(dto.ExternalId),        // ExternalId
            Num(dto.ExternalSourceId)   // ExternalSourceId
        }
    };

    // --------------------------------------------------------------------- Product

    /// <summary>
    /// <c>ProductDto</c> -> <c>arm.Product</c>.
    ///
    /// <para>Only the scalars the supplied schema has columns for. The nested
    /// <c>ClearingHouse</c>, <c>MarketProperties</c>, <c>TradingUnit</c>,
    /// <c>StrategyLegs</c>, <c>ExtensionValues</c> and <c>PeriodGroupIds</c> are dropped
    /// — <c>ClearingHouseId</c> is read from the scalar property, NOT from
    /// <c>ClearingHouse.Id</c>, because the scalar is populated (and nullable) even when
    /// the nested object is null.</para>
    /// </summary>
    internal static MarexRow Map(ProductDto dto) => new()
    {
        Values = new object[]
        {
            dto.Id,                                 // ProductId (key)
            Num(dto.ExternalSourceId),              // ExternalSourceId
            Num(dto.ExternalId),                    // ExternalId
            Str(dto.PipelineName),                  // PipelineName
            Num(dto.PipelineId),                    // PipelineId
            Str(dto.IndexName),                     // IndexName
            Num(dto.IndexId),                       // IndexId
            Str(dto.LocationName),                  // LocationName
            Num(dto.LocationId),                    // LocationId
            Str(dto.GradeName),                     // GradeName
            dto.GradeId,                            // GradeId
            dto.CounterpartyDetailsVisible,         // CounterpartyDetailsVisible
            Str(dto.GroupName),                     // GroupName
            dto.GroupId,                            // GroupId
            Num(dto.ClearingHouseId),               // ClearingHouseId
            Str(dto.MarketName),                    // MarketName
            dto.MarketId,                           // MarketId
            EnumName(dto.ProductType),              // ProductType
            Str(dto.Name),                          // Name
            EnumName(dto.TradeChartDataSource)      // TradeChartDataSource
        }
    };
}
