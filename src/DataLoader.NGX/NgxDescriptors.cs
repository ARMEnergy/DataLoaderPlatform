namespace DataLoader.NGX;

/// <summary>
/// One TVP column. <see cref="SqlType"/> is carried so the contract test can compare
/// this list against the real <c>sql/NGX/002</c> text, and <see cref="MaxLength"/> so
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
/// must never truncate one of these: a clipped key value does not fail, it merges into a
/// DIFFERENT row. That is a live risk rather than a theoretical one — the vendor already
/// recycles <c>ExchangeReference</c> across dates and markets, so a truncated one is
/// genuinely likely to collide with a real trade.
/// </param>
internal sealed record NgxColumn(
    string Name, Type ClrType, string SqlType, int? MaxLength = null, bool IsKey = false);

/// <summary>One target table, its TVP type and its merge proc.</summary>
internal sealed record NgxTableDescriptor(
    string TableName,
    string TvpType,
    string MergeProc,
    IReadOnlyList<NgxColumn> Columns);

/// <summary>
/// A parsed row, already projected into TVP column order.
///
/// <para>The parser fills <see cref="Values"/> by walking the SAME
/// <see cref="NgxTableDescriptor.Columns"/> list the sink builds its DataTable from, so
/// the two cannot drift from one another. What still could drift is this file versus
/// <c>sql/NGX/002</c> — and <c>NgxTvpContractTests</c> parses the real .sql and asserts
/// name + order + type against these descriptors, so that fails the build.</para>
/// </summary>
internal sealed class NgxRow
{
    public required object[] Values { get; init; }
}

/// <summary>
/// The two tables this loader writes, and the column order that IS the TVP contract.
///
/// <para><b>Order is load-bearing.</b> A TVP binds BY POSITION, not by name. Both lists
/// below follow the TABLE's column declaration order, which for
/// <c>arm.StripTradingSummary</c> is NOT its primary key order (TradeDateTime is
/// declared first but keys fourth). Reordering either list without reordering
/// <c>sql/NGX/002</c> loads every row shifted, silently — the index table is especially
/// exposed because it has four interchangeably-typed <c>DECIMAL(18,8)</c> columns in a
/// row.</para>
/// </summary>
internal static class NgxDescriptors
{
    /// <summary>The feed id for the index-price pipeline, matched against <c>EnabledFeeds</c>.</summary>
    internal const string IndexPriceFeedId = "IndexPrice";

    /// <summary>The feed id for the strip pipeline.</summary>
    internal const string StripFeedId = "StripTradingSummary";

    /// <summary>
    /// <c>arm.IndexPrice</c> — 23 columns, the table's 24 less the DB-stamped
    /// <c>ModifiedAtUtc</c>.
    ///
    /// <para><c>ExecutionDate</c> and <c>CommodityType</c> are supplied by the LOADER,
    /// not read from the response: the first is the snapshot stamp (and a key
    /// component), the second a constant the endpoint has no element for. The four
    /// <c>AlternateTraded*</c> columns have no element here either and are always null
    /// — they exist for the crude endpoint, which shares this table shape.</para>
    /// </summary>
    internal static readonly NgxTableDescriptor IndexPrice = new(
        TableName: "arm.IndexPrice",
        TvpType: "arm.IndexPriceTvp",
        MergeProc: "arm.usp_BulkMergeIndexPrice",
        Columns: new[]
        {
            new NgxColumn("ExecutionDate",               typeof(DateTime), "DATE", IsKey: true),
            new NgxColumn("IndexId",                     typeof(int),      "INT",  IsKey: true),
            new NgxColumn("PriceEffectiveStart",         typeof(DateTime), "DATE", IsKey: true),
            new NgxColumn("PriceEffectiveEnd",           typeof(DateTime), "DATE", IsKey: true),
            new NgxColumn("SourceDataDeliveryStart",     typeof(DateTime), "DATE"),
            new NgxColumn("SourceDataDeliveryEnd",       typeof(DateTime), "DATE"),
            new NgxColumn("CommodityType",               typeof(string),   "VARCHAR(50)",   50),
            new NgxColumn("Id",                          typeof(string),   "VARCHAR(50)",   50),
            new NgxColumn("IndexName",                   typeof(string),   "VARCHAR(500)", 500),
            new NgxColumn("PriceAmount",                 typeof(decimal),  "DECIMAL(18,8)"),
            new NgxColumn("PriceCurrency",               typeof(string),   "VARCHAR(50)",   50),
            new NgxColumn("Duration",                    typeof(int),      "INT"),
            new NgxColumn("TradedAmount",                typeof(decimal),  "DECIMAL(18,8)"),
            new NgxColumn("TradedUnit",                  typeof(string),   "VARCHAR(50)",   50),
            new NgxColumn("TradedContractUnit",          typeof(string),   "VARCHAR(50)",   50),
            new NgxColumn("TradedTotalAmount",           typeof(decimal),  "DECIMAL(18,8)"),
            new NgxColumn("AlternateTradedAmount",       typeof(decimal),  "DECIMAL(18,8)"),
            new NgxColumn("AlternateTradedUnit",         typeof(string),   "VARCHAR(50)",   50),
            new NgxColumn("AlternateTradedContractUnit", typeof(string),   "VARCHAR(50)",   50),
            new NgxColumn("AlternateTradedTotalAmount",  typeof(decimal),  "DECIMAL(18,8)"),
            new NgxColumn("NumberOfTrades",              typeof(int),      "INT"),
            new NgxColumn("SettlementState",             typeof(string),   "VARCHAR(50)",   50),
            new NgxColumn("LastUpdatedDate",             typeof(DateTime), "DATETIME2(7)")
        });

    /// <summary>
    /// <c>arm.StripTradingSummary</c> — 21 columns, the table's 22 less
    /// <c>ModifiedAtUtc</c>.
    ///
    /// <para>Note <c>HubName</c> is VARCHAR(50) while <c>MarketName</c> is VARCHAR(256);
    /// that asymmetry is the supplied DDL's, reproduced rather than normalised, and it
    /// is why the sink truncates per column rather than to one shared width.</para>
    /// </summary>
    internal static readonly NgxTableDescriptor StripTradingSummary = new(
        TableName: "arm.StripTradingSummary",
        TvpType: "arm.StripTradingSummaryTvp",
        MergeProc: "arm.usp_BulkMergeStripTradingSummary",
        Columns: new[]
        {
            new NgxColumn("TradeDateTime",            typeof(DateTime), "DATETIME2(0)", IsKey: true),
            new NgxColumn("HubId",                    typeof(int),      "INT",          IsKey: true),
            new NgxColumn("MarketId",                 typeof(int),      "INT",          IsKey: true),
            new NgxColumn("StripType",                typeof(string),   "VARCHAR(256)", 256, IsKey: true),
            new NgxColumn("ExchangeReference",        typeof(string),   "VARCHAR(256)", 256, IsKey: true),
            new NgxColumn("BeginDate",                typeof(DateTime), "DATE",         IsKey: true),
            new NgxColumn("EndDate",                  typeof(DateTime), "DATE",         IsKey: true),
            new NgxColumn("HubName",                  typeof(string),   "VARCHAR(50)",   50),
            new NgxColumn("MarketName",               typeof(string),   "VARCHAR(256)", 256),
            new NgxColumn("SettlementTitle",          typeof(string),   "VARCHAR(256)", 256),
            new NgxColumn("Cleared",                  typeof(bool),     "BIT"),
            new NgxColumn("TradedVolumeAmount",       typeof(decimal),  "DECIMAL(18,8)"),
            new NgxColumn("TradedVolumeUnit",         typeof(string),   "VARCHAR(50)",   50),
            new NgxColumn("TotalVolumeAmount",        typeof(decimal),  "DECIMAL(18,8)"),
            new NgxColumn("TotalVolumeUnit",          typeof(string),   "VARCHAR(50)",   50),
            new NgxColumn("TotalVolumeinTJ",          typeof(decimal),  "DECIMAL(18,8)"),
            new NgxColumn("PriceAmount",              typeof(decimal),  "DECIMAL(18,8)"),
            new NgxColumn("PriceCurrency",            typeof(string),   "VARCHAR(50)",   50),
            new NgxColumn("BrokerCompanyName",        typeof(string),   "VARCHAR(256)", 256),
            new NgxColumn("RequestForQuoteIndicator", typeof(bool),     "BIT"),
            new NgxColumn("IncludeInIndexIndicator",  typeof(bool),     "BIT")
        });

    internal static readonly IReadOnlyList<NgxTableDescriptor> All = new[] { IndexPrice, StripTradingSummary };
}
