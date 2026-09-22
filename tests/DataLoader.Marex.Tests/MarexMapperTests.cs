using DataLoader.Marex;
using Xunit;

namespace DataLoader.Marex.Tests;

/// <summary>
/// The mapper turns vendor DTOs into positional TVP rows, so these tests are mostly
/// about the three ways that goes silently wrong: a value in the wrong slot, a
/// "no value" sentinel stored as real data, and an enum written as a number.
/// </summary>
public class MarexMapperTests
{
    private static readonly DateTime ExchangeDate = new(2026, 9, 21, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Reads a column by NAME, so a test never hard-codes an index.</summary>
    private static object Value(MarexRow row, MarexTableDescriptor table, string column)
    {
        var index = -1;
        for (var i = 0; i < table.Columns.Count; i++)
            if (table.Columns[i].Name == column) { index = i; break; }

        Assert.True(index >= 0, $"{table.TableName} has no column named {column}");
        return row.Values[index];
    }

    [Fact]
    public void EveryRow_HasExactlyTheDescriptorsColumnCount()
    {
        // The sink throws on a mismatch, but that is a run-time failure on a live load.
        // This catches it at build time.
        Assert.Equal(MarexDescriptors.ClosingPrice.Columns.Count,
            MarexMapper.Map(Dto.ClosingPrice(), ExchangeDate).Values.Length);
        Assert.Equal(MarexDescriptors.MarketStatistic.Columns.Count,
            MarexMapper.Map(Dto.MarketStatistic(), ExchangeDate).Values.Length);
        Assert.Equal(MarexDescriptors.Period.Columns.Count,
            MarexMapper.Map(Dto.Period()).Values.Length);
        Assert.Equal(MarexDescriptors.PeriodGroup.Columns.Count,
            MarexMapper.Map(Dto.PeriodGroup()).Values.Length);
        Assert.Equal(MarexDescriptors.Product.Columns.Count,
            MarexMapper.Map(Dto.Product()).Values.Length);
    }

    // ------------------------------------------------- the "no value" sentinel

    [Fact]
    public void ClosingPrice_YearOneTimestamp_BecomesNull()
    {
        // Observed live: the vendor sends 0001-01-01T00:00:00Z for PreviousTime on
        // exactly the rows whose PreviousPrice is null, because the SDK types the
        // property as a non-nullable DateTime. DATETIME2's range starts at year 1, so
        // this would otherwise store silently and read back as a real timestamp.
        var dto = Dto.ClosingPrice(previousPrice: null, previousTime: DateTime.MinValue);

        var row = MarexMapper.Map(dto, ExchangeDate);

        Assert.Equal(DBNull.Value, Value(row, MarexDescriptors.ClosingPrice, "PreviousTime"));
        Assert.Equal(DBNull.Value, Value(row, MarexDescriptors.ClosingPrice, "PreviousPrice"));
    }

    [Fact]
    public void ClosingPrice_RealTimestamp_IsPreservedExactly()
    {
        var time = new DateTime(2026, 8, 30, 23, 0, 0, DateTimeKind.Utc);
        var row = MarexMapper.Map(Dto.ClosingPrice(time: time), ExchangeDate);

        // Not truncated to a date: the column is DATETIME2(7) and the time is meaningful.
        Assert.Equal(time, Value(row, MarexDescriptors.ClosingPrice, "Time"));
    }

    [Theory]
    [InlineData(1)]   // DateTime.MinValue itself
    [InlineData(2)]   // and a value that picked up a stray offset conversion
    public void NullIfUnset_TreatsAnyYearOneValueAsUnset(int hour)
    {
        var value = new DateTime(1, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(hour - 1);
        Assert.Equal(DBNull.Value, MarexMapper.NullIfUnset(value));
    }

    [Fact]
    public void Period_NullNoticeOfShipment_StaysNull()
    {
        var row = MarexMapper.Map(Dto.Period(noticeOfShipment: null));
        Assert.Equal(DBNull.Value, Value(row, MarexDescriptors.Period, "NoticeOfShipment"));
    }

    [Fact]
    public void Period_NoticeOfShipment_KeepsItsTime()
    {
        // Live values are all 23:59:59 — a DATE column would have discarded that, which
        // is why the column is DATETIME2(7).
        var notice = new DateTime(2016, 11, 15, 23, 59, 59, DateTimeKind.Utc);
        var row = MarexMapper.Map(Dto.Period(noticeOfShipment: notice));

        Assert.Equal(notice, Value(row, MarexDescriptors.Period, "NoticeOfShipment"));
    }

    // ----------------------------------------------------------- date truncation

    [Fact]
    public void Period_DateColumns_KeepTheIntendedDay_ForEndOfDayValues()
    {
        // The three live DeliveryEnds that are not at midnight sit at 23:59, whose date
        // part is already the intended day. A value at 23:00 would be the dangerous
        // pattern (London midnight expressed as UTC) — none exists in this source, and
        // this test documents which case is actually covered.
        var endOfDay = new DateTime(2019, 6, 30, 23, 59, 0, DateTimeKind.Utc);
        var row = MarexMapper.Map(Dto.Period(deliveryEnd: endOfDay));

        Assert.Equal(new DateTime(2019, 6, 30), Value(row, MarexDescriptors.Period, "DeliveryEnd"));
    }

    [Fact]
    public void Period_DateColumns_DropAnIntradayTime()
    {
        var intraday = new DateTime(2019, 3, 1, 11, 8, 0, DateTimeKind.Utc);
        var row = MarexMapper.Map(Dto.Period(deliveryStart: intraday));

        Assert.Equal(new DateTime(2019, 3, 1), Value(row, MarexDescriptors.Period, "DeliveryStart"));
    }

    // ------------------------------------------------------------- the enum names

    [Fact]
    public void MarketStatistic_EnumColumns_HoldNamesNotNumbers()
    {
        var row = MarexMapper.Map(Dto.MarketStatistic(), ExchangeDate);
        var t = MarexDescriptors.MarketStatistic;

        Assert.Equal("Open", Value(row, t, "State"));
        Assert.Equal("Buy", Value(row, t, "LastTradeAggressorSide"));
        Assert.Equal("TradeMatched", Value(row, t, "LastTradeEventSource"));
        Assert.Equal("Snapshot", Value(row, t, "UpdateReason"));
    }

    [Fact]
    public void Product_EnumColumns_HoldNamesNotNumbers()
    {
        var row = MarexMapper.Map(Dto.Product());
        var t = MarexDescriptors.Product;

        Assert.Equal("Outright", Value(row, t, "ProductType"));
        Assert.Equal("Nbos", Value(row, t, "TradeChartDataSource"));
    }

    [Fact]
    public void EnumName_ForAValueTheSdkDoesNotDefine_StringifiesTheNumber()
    {
        // The gateway runs ahead of the vendored SDK (server minVersion 3.37 vs SDK
        // 3.15), so an unknown enum member is a real possibility. It must land as a
        // visible number rather than silently taking a neighbouring name — and it must
        // not throw, which would cost the whole batch.
        var unknown = (SignalRClient.Contracts.MarketState)37;

        Assert.Equal("37", MarexMapper.EnumName(unknown));
    }

    [Fact]
    public void EveryEnumName_FitsItsDeclaredColumnWidth()
    {
        // All four MarketStatistic enum columns are VARCHAR(50); a longer name would be
        // truncated by the sink rather than raising.
        AssertAllNamesFit<SignalRClient.Contracts.MarketState>(50);
        AssertAllNamesFit<Common.Core.Enums.Side>(50);
        AssertAllNamesFit<Common.Core.Enums.TradeEventSource>(50);
        AssertAllNamesFit<Common.Core.Enums.MarketStatisticsUpdateReason>(50);
        AssertAllNamesFit<Common.Core.Enums.ProductType>(256);
        AssertAllNamesFit<Common.Core.Enums.TradeChartDataSource>(256);

        static void AssertAllNamesFit<TEnum>(int width) where TEnum : struct, Enum
        {
            foreach (var name in Enum.GetNames<TEnum>())
                Assert.True(name.Length <= width,
                    $"{typeof(TEnum).Name}.{name} is {name.Length} chars, wider than the column's {width}");
        }
    }

    // ------------------------------------------------------------ key and scalars

    [Fact]
    public void ClosingPrice_IsStampedWithTheExchangeDate_NotAnyClock()
    {
        var row = MarexMapper.Map(Dto.ClosingPrice(), ExchangeDate);
        Assert.Equal(ExchangeDate.Date, Value(row, MarexDescriptors.ClosingPrice, "ExchangeDate"));
    }

    [Fact]
    public void MarketStatistic_TradeDate_IsTheSameExchangeDate()
    {
        var row = MarexMapper.Map(Dto.MarketStatistic(), ExchangeDate);
        Assert.Equal(ExchangeDate.Date, Value(row, MarexDescriptors.MarketStatistic, "TradeDate"));
    }

    [Fact]
    public void ExchangeDate_WithATimeComponent_IsTruncated()
    {
        // The gateway sends midnight, but the key column is DATE and a stray time would
        // otherwise be a type mismatch waiting to happen.
        var withTime = new DateTime(2026, 9, 21, 14, 30, 0, DateTimeKind.Utc);
        var row = MarexMapper.Map(Dto.ClosingPrice(), withTime);

        Assert.Equal(new DateTime(2026, 9, 21), Value(row, MarexDescriptors.ClosingPrice, "ExchangeDate"));
    }

    [Fact]
    public void BlankVendorStrings_BecomeNull_NotEmptyString()
    {
        // Live MarketStatistic rows carry "" for Location and Pipeline.
        var row = MarexMapper.Map(Dto.MarketStatistic(location: "", pipeline: "  "), ExchangeDate);
        var t = MarexDescriptors.MarketStatistic;

        Assert.Equal(DBNull.Value, Value(row, t, "Location"));
        Assert.Equal(DBNull.Value, Value(row, t, "Pipeline"));
    }

    [Fact]
    public void NullVendorStrings_BecomeNull()
    {
        var row = MarexMapper.Map(Dto.MarketStatistic(index: null, cciIndex: null), ExchangeDate);
        var t = MarexDescriptors.MarketStatistic;

        Assert.Equal(DBNull.Value, Value(row, t, "Index"));
        Assert.Equal(DBNull.Value, Value(row, t, "CciIndex"));
    }

    [Fact]
    public void Product_ClearingHouseId_ComesFromTheScalar_AndKeepsItsNull()
    {
        Assert.Equal(DBNull.Value,
            Value(MarexMapper.Map(Dto.Product(clearingHouseId: null)), MarexDescriptors.Product, "ClearingHouseId"));

        Assert.Equal(7,
            Value(MarexMapper.Map(Dto.Product(clearingHouseId: 7)), MarexDescriptors.Product, "ClearingHouseId"));
    }

    [Fact]
    public void Product_ScalarsLandInTheirOwnColumns()
    {
        // The Name/Id pairs alternate in a pattern that breaks halfway through the
        // table, which is exactly where a mapping slip would hide.
        var row = MarexMapper.Map(Dto.Product());
        var t = MarexDescriptors.Product;

        Assert.Equal(4601L, Value(row, t, "ProductId"));
        Assert.Equal("Central Alberta", Value(row, t, "PipelineName"));
        Assert.Equal(9, Value(row, t, "PipelineId"));
        Assert.Equal("WTI CMA", Value(row, t, "IndexName"));
        Assert.Equal(142, Value(row, t, "IndexId"));
        Assert.Equal("Edmonton", Value(row, t, "LocationName"));
        Assert.Equal(22, Value(row, t, "LocationId"));
        Assert.Equal("SW", Value(row, t, "GradeName"));
        Assert.Equal(106, Value(row, t, "GradeId"));
        Assert.Equal("Sweet", Value(row, t, "GroupName"));
        Assert.Equal(1015, Value(row, t, "GroupId"));
        Assert.Equal("Crude Oil Physical", Value(row, t, "MarketName"));
        Assert.Equal(1002L, Value(row, t, "MarketId"));
        Assert.Equal("SW CSW", Value(row, t, "Name"));
        Assert.Equal(true, Value(row, t, "CounterpartyDetailsVisible"));
    }

    [Fact]
    public void MarketStatistic_DecimalRun_LandsInTheRightSlots()
    {
        // Ten consecutive DECIMAL(18,8) columns: the single most shift-prone run in the
        // schema. Distinct values so a one-position slip cannot pass.
        var dto = Dto.MarketStatistic();
        dto.Average = 1m;
        dto.PcChangeOnDay = 2m;
        dto.ChangeOnDay = 3m;
        dto.Change = 4m;
        dto.CumQty = 5m;
        dto.Open = 6m;
        dto.Low = 7m;
        dto.High = 8m;
        dto.MidPrice = 9m;
        dto.SettlementPrice = 10m;

        var row = MarexMapper.Map(dto, ExchangeDate);
        var t = MarexDescriptors.MarketStatistic;

        Assert.Equal(1m, Value(row, t, "Average"));
        Assert.Equal(2m, Value(row, t, "PcChangeOnDay"));
        Assert.Equal(3m, Value(row, t, "ChangeOnDay"));
        Assert.Equal(4m, Value(row, t, "Change"));
        Assert.Equal(5m, Value(row, t, "CumQty"));
        Assert.Equal(6m, Value(row, t, "Open"));
        Assert.Equal(7m, Value(row, t, "Low"));
        Assert.Equal(8m, Value(row, t, "High"));
        Assert.Equal(9m, Value(row, t, "MidPrice"));
        Assert.Equal(10m, Value(row, t, "SettlementPrice"));
    }

    [Fact]
    public void MarketStatistic_NullPriceColumns_StayNull()
    {
        // A market with no trades today has nulls in every price column; that must read
        // back as "no price", not as zero.
        var dto = Dto.MarketStatistic();
        dto.SettlementPrice = null;
        dto.LastTradePrice = null;
        dto.LastTradeTime = null;

        var row = MarexMapper.Map(dto, ExchangeDate);
        var t = MarexDescriptors.MarketStatistic;

        Assert.Equal(DBNull.Value, Value(row, t, "SettlementPrice"));
        Assert.Equal(DBNull.Value, Value(row, t, "LastTradePrice"));
        Assert.Equal(DBNull.Value, Value(row, t, "LastTradeTime"));
    }

    [Fact]
    public void PeriodGroup_MapsAllSixColumns()
    {
        var row = MarexMapper.Map(Dto.PeriodGroup(id: 1203, name: "Day", displayName: "Day", isHidden: true));
        var t = MarexDescriptors.PeriodGroup;

        Assert.Equal(1203L, Value(row, t, "PeriodGroupId"));
        Assert.Equal("Day", Value(row, t, "Name"));
        Assert.Equal("Day", Value(row, t, "DisplayName"));
        Assert.Equal(true, Value(row, t, "IsHidden"));
        Assert.Equal(DBNull.Value, Value(row, t, "ExternalId"));
        Assert.Equal(DBNull.Value, Value(row, t, "ExternalSourceId"));
    }

    [Fact]
    public void EveryMappedValue_IsAssignableToItsDeclaredClrType()
    {
        // Catches a mapper writing an int where the DataTable column is long, which
        // DataTable would coerce silently but which signals a real confusion.
        Check(MarexDescriptors.ClosingPrice, MarexMapper.Map(Dto.ClosingPrice(), ExchangeDate));
        Check(MarexDescriptors.MarketStatistic, MarexMapper.Map(Dto.MarketStatistic(), ExchangeDate));
        Check(MarexDescriptors.Period, MarexMapper.Map(Dto.Period()));
        Check(MarexDescriptors.PeriodGroup, MarexMapper.Map(Dto.PeriodGroup()));
        Check(MarexDescriptors.Product, MarexMapper.Map(Dto.Product()));

        static void Check(MarexTableDescriptor table, MarexRow row)
        {
            for (var i = 0; i < table.Columns.Count; i++)
            {
                var value = row.Values[i];
                if (value is DBNull) continue;

                Assert.True(table.Columns[i].ClrType.IsInstanceOfType(value),
                    $"{table.TableName}.{table.Columns[i].Name} is declared {table.Columns[i].ClrType.Name} " +
                    $"but the mapper produced {value.GetType().Name}");
            }
        }
    }
}
