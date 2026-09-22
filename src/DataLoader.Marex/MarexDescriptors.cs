namespace DataLoader.Marex;

/// <summary>
/// One TVP column. <see cref="SqlType"/> is carried so the contract test can compare
/// this list against the real <c>sql/Marex/002</c> text, and <see cref="MaxLength"/> so
/// the sink can truncate rather than let SqlClient abort a whole batch with
/// "String or binary data would be truncated".
/// </summary>
/// <param name="Name">Column name, exactly as spelled in the table and the TVP.</param>
/// <param name="ClrType">The DataTable column type. Nullable columns still use the
/// underlying value type; <see cref="DBNull"/> carries the null.</param>
/// <param name="SqlType">Normalised SQL type text, e.g. <c>VARCHAR(50)</c>.</param>
/// <param name="MaxLength">Character cap for string columns; null for non-strings.</param>
/// <param name="IsKey">
/// True for a primary key component. The sink TRUNCATES an oversized non-key string but
/// must never truncate one of these — a clipped key value does not fail, it merges into
/// a DIFFERENT row.
/// </param>
internal sealed record MarexColumn(
    string Name, Type ClrType, string SqlType, int? MaxLength = null, bool IsKey = false);

/// <summary>One target table, its TVP type and its merge proc.</summary>
internal sealed record MarexTableDescriptor(
    string FeedId,
    string TableName,
    string TvpType,
    string MergeProc,
    IReadOnlyList<MarexColumn> Columns);

/// <summary>
/// A mapped row, already projected into TVP column order.
///
/// <para>The mapper fills <see cref="Values"/> by walking the SAME
/// <see cref="MarexTableDescriptor.Columns"/> list the sink builds its DataTable from,
/// so the two cannot drift from one another. What still could drift is this file versus
/// <c>sql/Marex/002</c> — and <c>MarexTvpContractTests</c> parses the real .sql and
/// asserts name + order + type against these descriptors, so that fails the build.</para>
/// </summary>
internal sealed class MarexRow
{
    public required object[] Values { get; init; }
}

/// <summary>
/// The five tables this loader writes, and the column order that IS the TVP contract.
///
/// <para><b>Order is load-bearing.</b> A TVP binds BY POSITION, not by name. Every list
/// below follows the TABLE's column declaration order. <c>arm.MarketStatistic</c> is the
/// dangerous one: it has ten consecutive interchangeable <c>DECIMAL(18,8)</c> columns
/// (<c>Average</c> through <c>SettlementPrice</c>) and, a little further on, four
/// consecutive <c>VARCHAR(50)</c>s. A one-position slip anywhere in either run
/// type-checks perfectly and loads garbage without raising anything.</para>
///
/// <para>Every list is the table's columns LESS <c>ModifiedAtUtc</c>, which is stamped
/// by the merge proc with <c>SYSUTCDATETIME()</c> and is never a TVP column.</para>
/// </summary>
internal static class MarexDescriptors
{
    internal const string ClosingPriceFeedId = "ClosingPrice";
    internal const string MarketStatisticFeedId = "MarketStatistic";
    internal const string PeriodFeedId = "Period";
    internal const string PeriodGroupFeedId = "PeriodGroup";
    internal const string ProductFeedId = "Product";

    /// <summary>
    /// <c>arm.ClosingPrice</c> — 8 columns.
    ///
    /// <para><c>ExchangeDate</c> is NOT in <c>ClosingPriceDto</c>. It is the trading day
    /// the gateway pushes separately on <c>ExchangeDateSnapshot</c>, and it LEADS the
    /// primary key, so each trading day accumulates its own generation of the whole
    /// closing-price set rather than overwriting yesterday's. Getting it from anywhere
    /// else — the server clock, <c>SYSDATETIME()</c> in the proc, the loader's own
    /// "today" — forks the key whenever the two disagree, which they do outside the
    /// vendor's own timezone and across midnight.</para>
    ///
    /// <para><c>ClosingPriceId</c> is the DTO's <c>Id</c>, a composite
    /// <c>"{productId}:{periodId}"</c> string (observed max 9 chars against
    /// VARCHAR(50)).</para>
    /// </summary>
    internal static readonly MarexTableDescriptor ClosingPrice = new(
        FeedId: ClosingPriceFeedId,
        TableName: "arm.ClosingPrice",
        TvpType: "arm.ClosingPriceTvp",
        MergeProc: "arm.usp_BulkMergeClosingPrice",
        Columns: new[]
        {
            new MarexColumn("ExchangeDate",   typeof(DateTime), "DATE",          IsKey: true),
            new MarexColumn("ClosingPriceId", typeof(string),   "VARCHAR(50)", 50, IsKey: true),
            new MarexColumn("ProductId",      typeof(int),      "INT"),
            new MarexColumn("PeriodId",       typeof(long),     "BIGINT"),
            new MarexColumn("Price",          typeof(decimal),  "DECIMAL(18,8)"),
            new MarexColumn("Time",           typeof(DateTime), "DATETIME2(7)"),
            new MarexColumn("PreviousPrice",  typeof(decimal),  "DECIMAL(18,8)"),
            new MarexColumn("PreviousTime",   typeof(DateTime), "DATETIME2(7)"),
        });

    /// <summary>
    /// <c>arm.MarketStatistic</c> — 26 columns.
    ///
    /// <para><c>TradeDate</c> is the same <c>ExchangeDateSnapshot</c> value as
    /// <c>arm.ClosingPrice.ExchangeDate</c>, and leads the key for the same reason.</para>
    ///
    /// <para>Five columns hold ENUM NAMES, not the wire integers:
    /// <c>LastTradeEventSource</c> (<c>TradeEventSource</c>),
    /// <c>LastTradeAggressorSide</c> (<c>Side</c>), <c>State</c> (<c>MarketState</c>,
    /// whose DTO property is <c>State</c> but whose JSON name is <c>marketState</c>) and
    /// <c>UpdateReason</c> (<c>MarketStatisticsUpdateReason</c>). They are VARCHAR(50)
    /// in the supplied DDL, so the name is plainly what is wanted; the longest member
    /// across all four enums is 20 characters.</para>
    ///
    /// <para>The DTO's <c>Bids</c>, <c>Asks</c>, <c>PeriodIds</c> and
    /// <c>ExtensionValues</c> collections have no column in the supplied schema and are
    /// dropped. See docs/design/Marex.md.</para>
    /// </summary>
    internal static readonly MarexTableDescriptor MarketStatistic = new(
        FeedId: MarketStatisticFeedId,
        TableName: "arm.MarketStatistic",
        TvpType: "arm.MarketStatisticTvp",
        MergeProc: "arm.usp_BulkMergeMarketStatistic",
        Columns: new[]
        {
            new MarexColumn("TradeDate",              typeof(DateTime), "DATE",          IsKey: true),
            new MarexColumn("Id",                     typeof(long),     "BIGINT",        IsKey: true),
            new MarexColumn("CciIndex",               typeof(string),   "VARCHAR(50)", 50),
            new MarexColumn("Index",                  typeof(string),   "VARCHAR(50)", 50),
            new MarexColumn("TransactionCount",       typeof(long),     "BIGINT"),
            new MarexColumn("Average",                typeof(decimal),  "DECIMAL(18,8)"),
            new MarexColumn("PcChangeOnDay",          typeof(decimal),  "DECIMAL(18,8)"),
            new MarexColumn("ChangeOnDay",            typeof(decimal),  "DECIMAL(18,8)"),
            new MarexColumn("Change",                 typeof(decimal),  "DECIMAL(18,8)"),
            new MarexColumn("CumQty",                 typeof(decimal),  "DECIMAL(18,8)"),
            new MarexColumn("Open",                   typeof(decimal),  "DECIMAL(18,8)"),
            new MarexColumn("Low",                    typeof(decimal),  "DECIMAL(18,8)"),
            new MarexColumn("High",                   typeof(decimal),  "DECIMAL(18,8)"),
            new MarexColumn("MidPrice",               typeof(decimal),  "DECIMAL(18,8)"),
            new MarexColumn("SettlementPrice",        typeof(decimal),  "DECIMAL(18,8)"),
            new MarexColumn("LastTradeTime",          typeof(DateTime), "DATETIME2(7)"),
            new MarexColumn("LastTradeEventSource",   typeof(string),   "VARCHAR(50)", 50),
            new MarexColumn("LastTradeAggressorSide", typeof(string),   "VARCHAR(50)", 50),
            new MarexColumn("LastTradePrice",         typeof(decimal),  "DECIMAL(18,8)"),
            new MarexColumn("State",                  typeof(string),   "VARCHAR(50)", 50),
            new MarexColumn("Pipeline",               typeof(string),   "VARCHAR(50)", 50),
            new MarexColumn("Location",               typeof(string),   "VARCHAR(50)", 50),
            new MarexColumn("ProductId",              typeof(long),     "BIGINT"),
            new MarexColumn("Period",                 typeof(string),   "VARCHAR(50)", 50),
            new MarexColumn("Product",                typeof(string),   "VARCHAR(50)", 50),
            new MarexColumn("UpdateReason",           typeof(string),   "VARCHAR(50)", 50),
        });

    /// <summary>
    /// <c>arm.Period</c> — 10 columns, keyed on the vendor's own <c>PeriodId</c> with no
    /// date component: this is a dimension, and a run REFRESHES it in place.
    ///
    /// <para>The four <c>DATE</c> columns come from DTO properties typed
    /// <c>DateTime</c> and are truncated to their date part. That is safe here and was
    /// checked rather than assumed: across all 724 live periods (2,896 date values) the
    /// only non-midnight values are intraday trading stamps and three
    /// <c>deliveryEnd</c>s at <c>23:59</c>, whose date part is already the intended day.
    /// Nothing sits at <c>23:00Z</c>, which is the pattern that would betray a
    /// London-midnight-expressed-as-UTC value and make truncation off by one.</para>
    /// </summary>
    internal static readonly MarexTableDescriptor Period = new(
        FeedId: PeriodFeedId,
        TableName: "arm.Period",
        TvpType: "arm.PeriodTvp",
        MergeProc: "arm.usp_BulkMergePeriod",
        Columns: new[]
        {
            new MarexColumn("PeriodId",         typeof(long),     "BIGINT", IsKey: true),
            new MarexColumn("Name",             typeof(string),   "VARCHAR(256)", 256),
            new MarexColumn("DeliveryStart",    typeof(DateTime), "DATE"),
            new MarexColumn("DeliveryEnd",      typeof(DateTime), "DATE"),
            new MarexColumn("TradingStart",     typeof(DateTime), "DATE"),
            new MarexColumn("TradingEnd",       typeof(DateTime), "DATE"),
            new MarexColumn("NoticeOfShipment", typeof(DateTime), "DATETIME2(7)"),
            new MarexColumn("PeriodGroupId",    typeof(int),      "INT"),
            new MarexColumn("ExternalId",       typeof(long),     "BIGINT"),
            new MarexColumn("ExternalSourceId", typeof(int),      "INT"),
        });

    /// <summary>
    /// <c>arm.PeriodGroup</c> — 6 columns. A dimension keyed on the vendor's own id.
    /// </summary>
    internal static readonly MarexTableDescriptor PeriodGroup = new(
        FeedId: PeriodGroupFeedId,
        TableName: "arm.PeriodGroup",
        TvpType: "arm.PeriodGroupTvp",
        MergeProc: "arm.usp_BulkMergePeriodGroup",
        Columns: new[]
        {
            new MarexColumn("PeriodGroupId",    typeof(long),   "BIGINT", IsKey: true),
            new MarexColumn("Name",             typeof(string), "VARCHAR(256)", 256),
            new MarexColumn("DisplayName",      typeof(string), "VARCHAR(256)", 256),
            new MarexColumn("IsHidden",         typeof(bool),   "BIT"),
            new MarexColumn("ExternalId",       typeof(long),   "BIGINT"),
            new MarexColumn("ExternalSourceId", typeof(int),    "INT"),
        });

    /// <summary>
    /// <c>arm.Product</c> — 20 columns. A dimension keyed on the vendor's own id.
    ///
    /// <para>The column order here is the supplied DDL's, which is NOT the DTO's
    /// alphabetical property order and NOT grouped the way it reads — <c>PipelineName</c>
    /// precedes <c>PipelineId</c>, <c>IndexName</c> precedes <c>IndexId</c>, and so on
    /// for Location/Grade/Group, but then <c>MarketName</c> precedes <c>MarketId</c>
    /// while <c>ClearingHouseId</c> stands alone with no name column. Follow the list,
    /// not the pattern.</para>
    ///
    /// <para><c>ProductType</c> and <c>TradeChartDataSource</c> hold ENUM NAMES
    /// (VARCHAR(256) in the supplied DDL). The DTO's nested <c>ClearingHouse</c>,
    /// <c>MarketProperties</c>, <c>TradingUnit</c>, <c>StrategyLegs</c>,
    /// <c>ExtensionValues</c> and <c>PeriodGroupIds</c> have no columns in the supplied
    /// schema and are dropped — only the scalar <c>ClearingHouseId</c> survives. See
    /// docs/design/Marex.md.</para>
    /// </summary>
    internal static readonly MarexTableDescriptor Product = new(
        FeedId: ProductFeedId,
        TableName: "arm.Product",
        TvpType: "arm.ProductTvp",
        MergeProc: "arm.usp_BulkMergeProduct",
        Columns: new[]
        {
            new MarexColumn("ProductId",                  typeof(long),   "BIGINT", IsKey: true),
            new MarexColumn("ExternalSourceId",           typeof(int),    "INT"),
            new MarexColumn("ExternalId",                 typeof(long),   "BIGINT"),
            new MarexColumn("PipelineName",               typeof(string), "VARCHAR(256)", 256),
            new MarexColumn("PipelineId",                 typeof(int),    "INT"),
            new MarexColumn("IndexName",                  typeof(string), "VARCHAR(256)", 256),
            new MarexColumn("IndexId",                    typeof(int),    "INT"),
            new MarexColumn("LocationName",               typeof(string), "VARCHAR(256)", 256),
            new MarexColumn("LocationId",                 typeof(int),    "INT"),
            new MarexColumn("GradeName",                  typeof(string), "VARCHAR(256)", 256),
            new MarexColumn("GradeId",                    typeof(int),    "INT"),
            new MarexColumn("CounterpartyDetailsVisible", typeof(bool),   "BIT"),
            new MarexColumn("GroupName",                  typeof(string), "VARCHAR(256)", 256),
            new MarexColumn("GroupId",                    typeof(int),    "INT"),
            new MarexColumn("ClearingHouseId",            typeof(int),    "INT"),
            new MarexColumn("MarketName",                 typeof(string), "VARCHAR(256)", 256),
            new MarexColumn("MarketId",                   typeof(long),   "BIGINT"),
            new MarexColumn("ProductType",                typeof(string), "VARCHAR(256)", 256),
            new MarexColumn("Name",                       typeof(string), "VARCHAR(256)", 256),
            new MarexColumn("TradeChartDataSource",       typeof(string), "VARCHAR(256)", 256),
        });

    /// <summary>Every table, in the order <c>MarexModule</c> runs them.</summary>
    internal static readonly IReadOnlyList<MarexTableDescriptor> All = new[]
    {
        PeriodGroup, Period, Product, ClosingPrice, MarketStatistic
    };
}
