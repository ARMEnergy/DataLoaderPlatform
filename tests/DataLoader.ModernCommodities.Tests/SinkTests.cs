using System.Data;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DataLoader.ModernCommodities.Tests;

/// <summary>
/// *** THE MOST VALUABLE TEST IN THIS PROJECT. ***
///
/// <para>The three sinks (two of which share one <c>BuildTable</c>) are the C# side of the
/// load-bearing TVP contract. A TVP binds <b>BY POSITION</b>, so a column NAME / ORDER / TYPE drift
/// between <c>BuildTable</c> and <c>sql/ModernCommodities/002_CreateModernCommoditiesTvpTypes.sql</c>
/// produces <b>no compile error, no SQL error and no warning</b> - it just writes every value into
/// the wrong column of every loaded row. This test is the only thing that catches it.</para>
///
/// <para>The expected arrays below are transcribed <b>literally</b> from 002:</para>
/// <code>
/// arm.TradesTvp - 34 columns (shared by arm.usp_BulkMergeAllTrades AND arm.usp_BulkMergeMyTrades):
///      1. TradeNumber            INT           NOT NULL   &lt;- merge key
///      2. State                  VARCHAR(50)   NULL
///      3. Product                VARCHAR(50)   NULL
///      4. Location               VARCHAR(50)   NULL
///      5. PipelineTerminal       VARCHAR(256)  NULL       (spelled CORRECTLY here)
///      6. PriceBasis             VARCHAR(256)  NULL
///      7. Term                   VARCHAR(256)  NULL
///      8. TermStart              DATE          NULL
///      9. TermEnd                DATE          NULL
///     10. Price                  DECIMAL(9,2)  NULL       SIGNED
///     11. Volume                 DECIMAL(9,2)  NULL
///     12. UnitOfMeasure          VARCHAR(50)   NULL
///     13. Executed               DATETIME2(0)  NULL
///     14. LastUpdated            DATETIME2(0)  NULL       &lt;- MERGE recency guard (D14)
///     15. TradeType              VARCHAR(50)   NULL
///     16. Side                   VARCHAR(256)  NULL
///     17. BidTrader              VARCHAR(256)  NULL
///     18. BidLegalName           VARCHAR(256)  NULL
///     19. BidAddress             VARCHAR(256)  NULL
///     20. BidCommission          DECIMAL(9,2)  NULL
///     21. OfferTrader            VARCHAR(256)  NULL
///     22. OfferLegalName         VARCHAR(256)  NULL
///     23. OfferAddress           VARCHAR(256)  NULL
///     24. OfferCommission        DECIMAL(9,2)  NULL
///     25. SpreadTradeNumber      VARCHAR(50)   NULL       TEXT, not INT
///     26. ApportionmentProtected BIT           NULL
///     27. ClearingID             VARCHAR(50)   NULL
///     28. SettlementCurrency     VARCHAR(50)   NULL
///     29. ContractTerms          VARCHAR(256)  NULL
///     30. GTandC                 VARCHAR(256)  NULL       source header is 'GT&amp;C'
///     31. Notes                  VARCHAR(8000) NULL
///     32. InIndex                BIT           NULL
///     33. ClickAndTrade          BIT           NULL
///     34. ProductType            VARCHAR(50)   NULL
///
/// arm.SettlementsTvp - 9 columns:
///      1. SettlementDate         DATE          NOT NULL   &lt;- merge key 1/6
///      2. Product                VARCHAR(50)   NOT NULL   &lt;- merge key 2/6
///      3. Location               VARCHAR(50)   NOT NULL   &lt;- merge key 3/6
///      4. PieplineTerminal       VARCHAR(50)   NOT NULL   &lt;- merge key 4/6  *** SIC ***
///      5. PriceBasis             VARCHAR(100)  NOT NULL   &lt;- merge key 5/6
///      6. Term                   VARCHAR(50)   NOT NULL   &lt;- merge key 6/6
///      7. TermStart              DATE          NULL
///      8. TermEnd                DATE          NULL
///      9. Price                  DECIMAL(9,2)  NULL       SIGNED (74% negative live)
/// </code>
///
/// <para><c>BuildTable</c> is pure (no DB) but <c>protected</c> on <c>sealed</c> types, so it is
/// invoked by reflection - the established CWG/AGSI/IHS/IIR/NGI posture. Production accessibility is
/// NOT widened to suit a test.</para>
/// </summary>
public class SinkTests
{
    // ---- the literal transcription of sql/ModernCommodities/002 --------------------------------

    /// <summary>arm.TradesTvp, column names in TVP order. Transcribed literally from 002.</summary>
    private static readonly string[] TradesTvpColumns =
    {
        "TradeNumber", "State", "Product", "Location", "PipelineTerminal", "PriceBasis", "Term",
        "TermStart", "TermEnd", "Price", "Volume", "UnitOfMeasure", "Executed", "LastUpdated",
        "TradeType", "Side", "BidTrader", "BidLegalName", "BidAddress", "BidCommission",
        "OfferTrader", "OfferLegalName", "OfferAddress", "OfferCommission", "SpreadTradeNumber",
        "ApportionmentProtected", "ClearingID", "SettlementCurrency", "ContractTerms", "GTandC",
        "Notes", "InIndex", "ClickAndTrade", "ProductType"
    };

    /// <summary>arm.TradesTvp, CLR types in TVP order (DATE and DATETIME2(0) both bind as DateTime).</summary>
    private static readonly Type[] TradesTvpTypes =
    {
        typeof(int), typeof(string), typeof(string), typeof(string), typeof(string), typeof(string),
        typeof(string), typeof(DateTime), typeof(DateTime), typeof(decimal), typeof(decimal),
        typeof(string), typeof(DateTime), typeof(DateTime), typeof(string), typeof(string),
        typeof(string), typeof(string), typeof(string), typeof(decimal), typeof(string),
        typeof(string), typeof(string), typeof(decimal), typeof(string), typeof(bool),
        typeof(string), typeof(string), typeof(string), typeof(string), typeof(string),
        typeof(bool), typeof(bool), typeof(string)
    };

    /// <summary>
    /// arm.SettlementsTvp, column names in TVP order. <b><c>PieplineTerminal</c> is misspelled on
    /// purpose</b> - it is the user DDL spelling and it is IN THE PRIMARY KEY (decision D2).
    /// Renaming it on either side breaks the positional binding silently.
    /// </summary>
    private static readonly string[] SettlementsTvpColumns =
    {
        "SettlementDate", "Product", "Location", "PieplineTerminal", "PriceBasis", "Term",
        "TermStart", "TermEnd", "Price"
    };

    private static readonly Type[] SettlementsTvpTypes =
    {
        typeof(DateTime), typeof(string), typeof(string), typeof(string), typeof(string),
        typeof(string), typeof(DateTime), typeof(DateTime), typeof(decimal)
    };

    // ---- reflection helpers (copied from the AGSI/IIR/NGI test projects) -----------------------

    private static DataTable BuildTable(object sink, object rows)
    {
        var type = sink.GetType();
        while (type is not null)
        {
            var method = type.GetMethod("BuildTable", BindingFlags.Instance | BindingFlags.NonPublic);
            if (method is not null) return (DataTable)method.Invoke(sink, new[] { rows })!;
            type = type.BaseType;
        }
        throw new InvalidOperationException("BuildTable not found");
    }

    private static string ProtectedString(object sink, string propertyName)
    {
        var type = sink.GetType();
        while (type is not null)
        {
            var prop = type.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.NonPublic);
            if (prop is not null) return (string)prop.GetValue(sink)!;
            type = type.BaseType;
        }
        throw new InvalidOperationException(propertyName + " not found");
    }

    private static IOptions<ModComSettings> Opt() =>
        Options.Create(new ModComSettings { ConnectionString = "unused-in-tests" });

    private static string[] Names(DataTable t) => t.Columns.Cast<DataColumn>().Select(c => c.ColumnName).ToArray();
    private static Type[] Types(DataTable t) => t.Columns.Cast<DataColumn>().Select(c => c.DataType).ToArray();

    private static AllTradesSqlSink AllTrades() => new(Opt(), NullLogger<AllTradesSqlSink>.Instance);
    private static MyTradesSqlSink MyTrades() => new(Opt(), NullLogger<MyTradesSqlSink>.Instance);
    private static SettlementsSqlSink Settlements() => new(Opt(), NullLogger<SettlementsSqlSink>.Instance);

    // ---- row builders (TradeRow / SettlementRow are sealed CLASSES, not records - no `with`) ----

    private static TradeRow FullTrade(int tradeNumber = 68043, DateTime? lastUpdated = null) => new()
    {
        TradeNumber = tradeNumber,
        State = "Finalized",
        Product = "WCS",
        Location = "Hardisty",
        PipelineTerminal = "Husky",
        PriceBasis = "WTI CMA",
        Term = "SEP-26",
        TermStart = new DateOnly(2026, 9, 1),
        TermEnd = new DateOnly(2026, 9, 30),
        Price = -16.65m,
        Volume = 300000m,
        UnitOfMeasure = "m3/month",
        Executed = new DateTime(2026, 8, 24, 13, 44, 41),
        LastUpdated = lastUpdated ?? new DateTime(2026, 8, 24, 13, 54, 34),
        TradeType = "Outright",
        Side = "Sell",
        BidTrader = "Dana Whitfield",
        BidLegalName = "Northwind Petroleum Marketing Inc.",
        BidAddress = Samples.CommaAddress,
        BidCommission = null,                       // blank commission -> NULL, never 0
        OfferTrader = "Robin Alcott",
        OfferLegalName = Samples.CommaLegalName,
        OfferAddress = "4500 Beechwood Parkway, 12th Floor, Houston, TX 77099",
        OfferCommission = 0.01m,
        SpreadTradeNumber = "66966",
        ApportionmentProtected = false,
        ClearingID = null,
        SettlementCurrency = "USD",
        ContractTerms = "Northwind Petroleum Marketing Inc.",
        GTandC = "MASTER 1998 with 2011 Amends",
        Notes = "Verbal confirm pending",
        InIndex = true,
        ClickAndTrade = null,                       // allTrades leaves it blank -> NULL, NOT false
        ProductType = "Physical"
    };

    private static SettlementRow Settlement(
        DateOnly? settlementDate = null,
        string product = "AHS",
        string location = "Edmonton",
        string piepline = "Enb T@S",
        string priceBasis = "WTI CMA",
        string term = "AUG-26",
        DateOnly? termStart = null,
        DateOnly? termEnd = null,
        decimal? price = -14.30m,
        bool blankTerms = false) => new()
    {
        SettlementDate = settlementDate ?? new DateOnly(2026, 8, 21),
        Product = product,
        Location = location,
        PieplineTerminal = piepline,
        PriceBasis = priceBasis,
        Term = term,
        TermStart = blankTerms ? null : termStart ?? new DateOnly(2026, 8, 1),
        TermEnd = blankTerms ? null : termEnd ?? new DateOnly(2026, 8, 31),
        Price = price
    };

    // ============================================================ arm.TradesTvp (34 columns)

    [Fact]
    public void AllTradesSink_BuildTable_MatchesTradesTvp_NameOrderType_ThirtyFourColumns()
    {
        var t = BuildTable(AllTrades(), new List<TradeRow> { FullTrade() });

        Assert.Equal(34, t.Columns.Count);
        Assert.Equal(TradesTvpColumns, Names(t));   // NAME + ORDER
        Assert.Equal(TradesTvpTypes, Types(t));     // TYPE
    }

    [Fact]
    public void MyTradesSink_BuildTable_MatchesTradesTvp_NameOrderType_ThirtyFourColumns()
    {
        var t = BuildTable(MyTrades(), new List<TradeRow> { FullTrade() });

        Assert.Equal(34, t.Columns.Count);
        Assert.Equal(TradesTvpColumns, Names(t));
        Assert.Equal(TradesTvpTypes, Types(t));
    }

    [Fact]
    public void BothTradesSinks_ShareOneIdenticalTableShape_BecauseTheyShareOneTvp()
    {
        // The allTrades and myTrades headers are byte-identical and both target tables have
        // identical column lists, so ONE TVP (arm.TradesTvp) serves both. If these two ever
        // diverge, one of the two merge procs is being fed a mis-ordered table.
        var all = BuildTable(AllTrades(), new List<TradeRow> { FullTrade() });
        var mine = BuildTable(MyTrades(), new List<TradeRow> { FullTrade() });

        Assert.Equal(Names(all), Names(mine));
        Assert.Equal(Types(all), Types(mine));
        Assert.Equal("arm.TradesTvp", ProtectedString(AllTrades(), "TableValuedParameterType"));
        Assert.Equal("arm.TradesTvp", ProtectedString(MyTrades(), "TableValuedParameterType"));
    }

    [Fact]
    public void TradesTvp_StartsAtTheFirstPayloadColumn_NoFileLogId_NoIdentity_NoDateCreated()
    {
        // A deliberate, stated break from the house "FileLogId is column 1" rule (decision D1): the
        // user DDL gives the three fact tables no FileLogId, no surrogate Id and no DateCreated, so
        // provenance lives in arm.FileLog ALONE. Adding FileLogId to a TVP alone would shift the
        // positional binding by one and land every value in the wrong column.
        var names = Names(BuildTable(AllTrades(), new List<TradeRow> { FullTrade() }));

        Assert.Equal("TradeNumber", names[0]);
        Assert.DoesNotContain("FileLogId", names);
        Assert.DoesNotContain("Id", names);
        Assert.DoesNotContain("DateCreated", names);
    }

    [Fact]
    public void TradesTvp_CarriesNoModifiedAtUtc_NoComputedColumn_AndNoPerBatchScalar()
    {
        // ModifiedAtUtc is stamped by the MERGE proc; there is no computed column here; and NOTHING
        // about the request window is persisted on a fact row (no @RunDate / @WindowStart /
        // @RunToken - both trades procs take the TVP and nothing else).
        var names = Names(BuildTable(MyTrades(), new List<TradeRow> { FullTrade() }));

        Assert.DoesNotContain("ModifiedAtUtc", names);
        Assert.DoesNotContain("RunDate", names);
        Assert.DoesNotContain("WindowStart", names);
        Assert.DoesNotContain("WindowEnd", names);
        Assert.DoesNotContain("RunToken", names);
        Assert.DoesNotContain("Checksum", names);   // decision U2: removed from all three tables
    }

    [Fact]
    public void TradesTvp_SpellsPipelineTerminalCORRECTLY_UnlikeTheSettlementsType()
    {
        var trades = Names(BuildTable(AllTrades(), new List<TradeRow> { FullTrade() }));
        var settlements = Names(BuildTable(Settlements(), new List<SettlementRow> { Settlement() }));

        Assert.Contains("PipelineTerminal", trades);          // correct spelling on the trades type
        Assert.DoesNotContain("PieplineTerminal", trades);
        Assert.Contains("PieplineTerminal", settlements);     // [sic] on the settlements type
        Assert.DoesNotContain("PipelineTerminal", settlements);
    }

    [Fact]
    public void Sinks_ProcAndTvpNames_MatchTheDescriptorsAndTheProcsIn003()
    {
        Assert.Equal("arm.usp_BulkMergeAllTrades", ProtectedString(AllTrades(), "StoredProcedureName"));
        Assert.Equal("arm.usp_BulkMergeMyTrades", ProtectedString(MyTrades(), "StoredProcedureName"));
        Assert.Equal("arm.usp_BulkMergeSettlements", ProtectedString(Settlements(), "StoredProcedureName"));
        Assert.Equal("arm.SettlementsTvp", ProtectedString(Settlements(), "TableValuedParameterType"));

        // ... and the descriptors advertise exactly the same target objects.
        Assert.Equal(ModComDescriptors.AllTrades.TargetProc, ProtectedString(AllTrades(), "StoredProcedureName"));
        Assert.Equal(ModComDescriptors.MyTrades.TargetProc, ProtectedString(MyTrades(), "StoredProcedureName"));
        Assert.Equal(ModComDescriptors.Settlements.TargetProc, ProtectedString(Settlements(), "StoredProcedureName"));
        Assert.Equal(ModComDescriptors.AllTrades.TargetTvp, ProtectedString(AllTrades(), "TableValuedParameterType"));
        Assert.Equal(ModComDescriptors.MyTrades.TargetTvp, ProtectedString(MyTrades(), "TableValuedParameterType"));
        Assert.Equal(ModComDescriptors.Settlements.TargetTvp, ProtectedString(Settlements(), "TableValuedParameterType"));
    }

    [Fact]
    public void TradesSink_BuildTable_BindsEveryValueToItsOwnColumn()
    {
        var t = BuildTable(AllTrades(), new List<TradeRow> { FullTrade() });
        var r = t.Rows[0];

        Assert.Equal(68043, r["TradeNumber"]);
        Assert.Equal("Finalized", r["State"]);
        Assert.Equal("WCS", r["Product"]);
        Assert.Equal("Hardisty", r["Location"]);
        Assert.Equal("Husky", r["PipelineTerminal"]);
        Assert.Equal("WTI CMA", r["PriceBasis"]);
        Assert.Equal("SEP-26", r["Term"]);
        Assert.Equal(new DateTime(2026, 9, 1), r["TermStart"]);      // DATE -> midnight
        Assert.Equal(new DateTime(2026, 9, 30), r["TermEnd"]);
        Assert.Equal(-16.65m, r["Price"]);                           // SIGNED
        Assert.Equal(300000m, r["Volume"]);
        Assert.Equal("m3/month", r["UnitOfMeasure"]);
        Assert.Equal(new DateTime(2026, 8, 24, 13, 44, 41), r["Executed"]);
        Assert.Equal(new DateTime(2026, 8, 24, 13, 54, 34), r["LastUpdated"]);
        Assert.Equal("Outright", r["TradeType"]);
        Assert.Equal("Sell", r["Side"]);
        Assert.Equal("Dana Whitfield", r["BidTrader"]);
        Assert.Equal("Northwind Petroleum Marketing Inc.", r["BidLegalName"]);
        Assert.Equal(Samples.CommaAddress, r["BidAddress"]);
        Assert.Equal(DBNull.Value, r["BidCommission"]);              // blank -> NULL, never 0
        Assert.Equal("Robin Alcott", r["OfferTrader"]);
        Assert.Equal(Samples.CommaLegalName, r["OfferLegalName"]);
        Assert.Equal(0.01m, r["OfferCommission"]);
        Assert.Equal("66966", r["SpreadTradeNumber"]);               // TEXT, not INT
        Assert.Equal(false, r["ApportionmentProtected"]);
        Assert.Equal(DBNull.Value, r["ClearingID"]);
        Assert.Equal("USD", r["SettlementCurrency"]);
        Assert.Equal("MASTER 1998 with 2011 Amends", r["GTandC"]);
        Assert.Equal("Verbal confirm pending", r["Notes"]);
        Assert.Equal(true, r["InIndex"]);
        Assert.Equal(DBNull.Value, r["ClickAndTrade"]);              // blank -> NULL, NOT false
        Assert.Equal("Physical", r["ProductType"]);
    }

    [Fact]
    public void TradesSink_BuildTable_NullEverythingOptional_BindsDBNull_NotDefaults()
    {
        var t = BuildTable(AllTrades(), new List<TradeRow> { new() { TradeNumber = 1 } });
        var r = t.Rows[0];

        Assert.Equal(1, r["TradeNumber"]);
        foreach (var column in TradesTvpColumns.Skip(1))
            Assert.Equal(DBNull.Value, r[column]);
    }

    [Fact]
    public void TradesSink_BuildTable_DedupsOnTradeNumber_LastUpdatedWins_NotFileOrder()
    {
        // The proc de-dups with ROW_NUMBER ... ORDER BY LastUpdated DESC; BuildTable must apply the
        // SAME rule, or the two disagree about which revision of a trade is stored.
        var stale = FullTrade(68043, new DateTime(2026, 8, 24, 10, 0, 0));
        var newest = FullTrade(68043, new DateTime(2026, 8, 24, 18, 0, 0));

        var t = BuildTable(AllTrades(), new List<TradeRow> { newest, stale });   // newest arrives FIRST

        var row = Assert.Single(t.Rows.Cast<DataRow>());
        Assert.Equal(new DateTime(2026, 8, 24, 18, 0, 0), row["LastUpdated"]);
    }

    [Fact]
    public void TradesSink_BuildTable_NullLastUpdatedNeverBeatsADatedCopy()
    {
        // Decision D14: a row whose timestamp failed to parse may insert but must never clobber a
        // row we KNOW is newer. A NULL sorts lowest, so the dated copy wins regardless of order.
        var dated = FullTrade(68043, new DateTime(2026, 8, 24, 12, 0, 0));
        var undated = new TradeRow { TradeNumber = 68043, State = "Cancelled", LastUpdated = null };

        var t = BuildTable(AllTrades(), new List<TradeRow> { dated, undated });

        var row = Assert.Single(t.Rows.Cast<DataRow>());
        Assert.Equal(new DateTime(2026, 8, 24, 12, 0, 0), row["LastUpdated"]);
        Assert.Equal("Finalized", row["State"]);   // the DATED copy is the one written
    }

    [Fact]
    public void TradesSink_BuildTable_KeepsDistinctTradeNumbers()
    {
        var t = BuildTable(MyTrades(), new List<TradeRow>
        {
            FullTrade(66966), FullTrade(66967), FullTrade(66968)
        });

        Assert.Equal(3, t.Rows.Count);
        Assert.Equal(new[] { 66966, 66967, 66968 },
            t.Rows.Cast<DataRow>().Select(r => (int)r["TradeNumber"]).OrderBy(n => n).ToArray());
    }

    // ============================================================ arm.SettlementsTvp (9 columns)

    [Fact]
    public void SettlementsSink_BuildTable_MatchesSettlementsTvp_NameOrderType_NineColumns()
    {
        var t = BuildTable(Settlements(), new List<SettlementRow> { Settlement() });

        Assert.Equal(9, t.Columns.Count);
        Assert.Equal(SettlementsTvpColumns, Names(t));   // NAME + ORDER (incl. PieplineTerminal [sic])
        Assert.Equal(SettlementsTvpTypes, Types(t));     // TYPE
    }

    [Fact]
    public void SettlementsTvp_StartsAtSettlementDate_NoFileLogId_NoModifiedAtUtc_NoScalarColumn()
    {
        var names = Names(BuildTable(Settlements(), new List<SettlementRow> { Settlement() }));

        Assert.Equal("SettlementDate", names[0]);
        Assert.DoesNotContain("FileLogId", names);
        Assert.DoesNotContain("Id", names);
        Assert.DoesNotContain("DateCreated", names);
        Assert.DoesNotContain("ModifiedAtUtc", names);
        Assert.DoesNotContain("RunDate", names);
        Assert.DoesNotContain("Checksum", names);
    }

    [Fact]
    public void SettlementsSink_BuildTable_KeepsTheLiteralDashVerbatimInBothKeyColumns()
    {
        // 82 of 1,443 live rows carry '-' in Location AND PieplineTerminal, always as a pair. It is
        // a KEY VALUE inside a NOT NULL 6-column PK, never a null sentinel: the five text keys are
        // passed through directly, NOT through NullIfEmpty.
        var t = BuildTable(Settlements(), new List<SettlementRow>
        {
            Settlement(product: "Sweet Guernsey Blend", location: "-", piepline: "-", price: 5.05m)
        });

        var r = t.Rows[0];
        Assert.Equal("-", r["Location"]);
        Assert.Equal("-", r["PieplineTerminal"]);
        Assert.NotEqual(DBNull.Value, r["Location"]);
        Assert.NotEqual(DBNull.Value, r["PieplineTerminal"]);
        Assert.Equal(5.05m, r["Price"]);
    }

    [Fact]
    public void SettlementsSink_BuildTable_KeepsUsdDollarPriceBasisUnnormalised()
    {
        // 'USD $' contains a space and a '$' and is a KEY - no trimming, stripping or normalisation.
        var t = BuildTable(Settlements(), new List<SettlementRow>
        {
            Settlement(priceBasis: "USD $", price: 2.65m)
        });

        Assert.Equal("USD $", t.Rows[0]["PriceBasis"]);
    }

    [Fact]
    public void SettlementsSink_BuildTable_BindsEveryValueToItsOwnColumn_AndDateToMidnight()
    {
        var t = BuildTable(Settlements(), new List<SettlementRow> { Settlement() });
        var r = t.Rows[0];

        Assert.Equal(new DateTime(2026, 8, 21), r["SettlementDate"]);
        Assert.Equal("AHS", r["Product"]);
        Assert.Equal("Edmonton", r["Location"]);
        Assert.Equal("Enb T@S", r["PieplineTerminal"]);
        Assert.Equal("WTI CMA", r["PriceBasis"]);
        Assert.Equal("AUG-26", r["Term"]);
        Assert.Equal(new DateTime(2026, 8, 1), r["TermStart"]);
        Assert.Equal(new DateTime(2026, 8, 31), r["TermEnd"]);
        Assert.Equal(-14.30m, r["Price"]);   // SIGNED - 74% of live rows are negative
    }

    [Fact]
    public void SettlementsSink_BuildTable_NullPriceAndTerms_BindDBNull()
    {
        var t = BuildTable(Settlements(), new List<SettlementRow> { Settlement(price: null, blankTerms: true) });

        var r = t.Rows[0];
        Assert.Equal(DBNull.Value, r["Price"]);
        Assert.Equal(DBNull.Value, r["TermStart"]);
        Assert.Equal(DBNull.Value, r["TermEnd"]);
    }

    [Fact]
    public void SettlementsSink_BuildTable_DedupsOnTheSixColumnKey_LastWins_CaseInsensitively()
    {
        var first = Settlement(price: 1.00m);
        var second = Settlement(location: "EDMONTON", price: 2.00m);   // same key, different casing

        var t = BuildTable(Settlements(), new List<SettlementRow> { first, second });

        var row = Assert.Single(t.Rows.Cast<DataRow>());
        Assert.Equal(2.00m, row["Price"]);            // last wins
        Assert.Equal("EDMONTON", row["Location"]);    // and it is the LAST row that is written
    }

    [Fact]
    public void SettlementsSink_BuildTable_KeepsRowsThatDifferInAnySingleKeyColumn()
    {
        var rows = new List<SettlementRow>
        {
            Settlement(),
            Settlement(term: "SEP-26"),
            Settlement(location: "-", piepline: "-"),
            Settlement(settlementDate: new DateOnly(2026, 8, 20)),
            Settlement(priceBasis: "USD $"),
            Settlement(product: "Sweet Guernsey Blend")
        };

        var t = BuildTable(Settlements(), rows);
        Assert.Equal(6, t.Rows.Count);
    }
}
