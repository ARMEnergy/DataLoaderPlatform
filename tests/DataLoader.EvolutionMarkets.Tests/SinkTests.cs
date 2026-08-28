using System.Data;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DataLoader.EvolutionMarkets.Tests;

/// <summary>
/// *** THE MOST VALUABLE TEST IN THIS PROJECT. ***
///
/// <para><see cref="MarketDataSqlSink"/> is the C# side of the load-bearing TVP contract. A TVP binds
/// <b>BY POSITION</b>, so a column NAME / ORDER / TYPE drift between <c>BuildTable</c> and
/// <c>sql/EvolutionMarkets/002_CreateEvolutionMarketsTvpTypes.sql</c> produces <b>no compile error,
/// no SQL error and no warning</b> — it just writes every value into the wrong column of every
/// loaded row. This test is the only thing that catches it.</para>
///
/// <para><b>This TVP is unusually dangerous.</b> It carries four interleaved decimal/int pairs —
/// <c>Price</c>, <c>Ask</c>/<c>AskSize</c>, <c>Bid</c>/<c>BidSize</c>, <c>Mid</c>/<c>MidSize</c>.
/// Swapping <c>Ask</c> with <c>Bid</c>, or <c>AskSize</c> with <c>BidSize</c>, is
/// TYPE-COMPATIBLE: nothing would raise, and every row would silently carry inverted prices. So this
/// file asserts not only the column list but that each individual VALUE lands in its own named
/// column (<see cref="BuildTable_PutsEachValueInItsOwnColumn"/>).</para>
///
/// <para>The expected arrays below are transcribed <b>literally</b> from 002:</para>
/// <code>
/// arm.MarketDataTvp - 25 columns:
///      1. FileLogId             INT              NULL       &lt;- provenance, NEVER a merge key
///      2. MarketDataId          UNIQUEIDENTIFIER NOT NULL   &lt;- MERGE KEY / PK
///      3. Market                VARCHAR(250)     NULL
///      4. Term                  VARCHAR(50)      NULL
///      5. Term2                 VARCHAR(50)      NULL
///      6. Tenor                 VARCHAR(50)      NULL
///      7. InstrumentSourceName  VARCHAR(250)     NULL
///      8. InstrumentId          UNIQUEIDENTIFIER NULL
///      9. InstrumentName        VARCHAR(250)     NULL
///     10. PriceTS               DATETIME2(0)     NULL
///     11. BusinessDate          DATE             NULL
///     12. PriceType             VARCHAR(250)     NULL
///     13. [Size]                INT              NULL
///     14. Depth                 INT              NULL
///     15. Price                 DECIMAL(18,8)    NULL
///     16. Ask                   DECIMAL(18,8)    NULL
///     17. AskSize               INT              NULL
///     18. Bid                   DECIMAL(18,8)    NULL
///     19. BidSize               INT              NULL
///     20. Mid                   DECIMAL(18,8)    NULL
///     21. MidSize               INT              NULL
///     22. [Change]              DECIMAL(18,8)    NULL
///     23. PctRetDaily           DECIMAL(18,8)    NULL
///     24. Currency              VARCHAR(50)      NULL
///     25. [Checksum]            INT              NOT NULL
/// </code>
///
/// <para><c>BuildTable</c> is pure (no DB) but <c>protected</c> on a <c>sealed</c> type, so it is
/// invoked by reflection — the established CWG/AGSI/IHS/IIR/NGI/ModCom posture. Production
/// accessibility is NOT widened to suit a test.</para>
/// </summary>
public class SinkTests
{
    // ---- the literal transcription of sql/EvolutionMarkets/002 ---------------------------------

    /// <summary>
    /// arm.MarketDataTvp, column names in TVP order. Transcribed literally from 002.
    /// Note the names are UNBRACKETED here: the DataTable carries the bare identifier, while 002
    /// brackets Size/Change/Checksum only to keep T-SQL unambiguous.
    /// </summary>
    private static readonly string[] TvpColumns =
    {
        "FileLogId", "MarketDataId", "Market", "Term", "Term2", "Tenor", "InstrumentSourceName",
        "InstrumentId", "InstrumentName", "PriceTS", "BusinessDate", "PriceType", "Size", "Depth",
        "Price", "Ask", "AskSize", "Bid", "BidSize", "Mid", "MidSize", "Change", "PctRetDaily",
        "Currency", "Checksum"
    };

    /// <summary>
    /// arm.MarketDataTvp, CLR types in TVP order. <c>DATETIME2(0)</c> and <c>DATE</c> both bind as
    /// <see cref="DateTime"/>; <c>UNIQUEIDENTIFIER</c> binds as <see cref="Guid"/>.
    /// </summary>
    private static readonly Type[] TvpTypes =
    {
        typeof(int), typeof(Guid), typeof(string), typeof(string), typeof(string), typeof(string),
        typeof(string), typeof(Guid), typeof(string), typeof(DateTime), typeof(DateTime),
        typeof(string), typeof(int), typeof(int), typeof(decimal), typeof(decimal), typeof(int),
        typeof(decimal), typeof(int), typeof(decimal), typeof(int), typeof(decimal),
        typeof(decimal), typeof(string), typeof(int)
    };

    // ---- reflection helpers (copied from the AGSI/IIR/NGI/ModCom test projects) ----------------

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

    private static bool ProtectedBool(object sink, string propertyName)
    {
        var type = sink.GetType();
        while (type is not null)
        {
            var prop = type.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.NonPublic);
            if (prop is not null) return (bool)prop.GetValue(sink)!;
            type = type.BaseType;
        }
        throw new InvalidOperationException(propertyName + " not found");
    }

    private static IOptions<EvoSettings> Opt() =>
        Options.Create(new EvoSettings { ConnectionString = "unused-in-tests" });

    private static MarketDataSqlSink Sink() => new(Opt(), NullLogger<MarketDataSqlSink>.Instance);

    private static string[] Names(DataTable t) => t.Columns.Cast<DataColumn>().Select(c => c.ColumnName).ToArray();
    private static Type[] Types(DataTable t) => t.Columns.Cast<DataColumn>().Select(c => c.DataType).ToArray();

    // ---- row builders --------------------------------------------------------------------------

    /// <summary>
    /// A row where EVERY column carries a DISTINCT, recognisable value, so a positional swap between
    /// any two same-typed columns is detectable. The decimals are deliberately all different and the
    /// ints are deliberately all different.
    /// </summary>
    private static MarketDataRow FullRow(Guid? id = null) => new()
    {
        FileLogId = 4242,
        MarketDataId = id ?? Guid.Parse("6d11bc3c-a601-480a-8032-b6d42068b9ea"),
        Market = "US Natural Gas Index",
        Term = "Sep'26-Oct'26",
        Term2 = "TERM2-VALUE",
        Tenor = "3m",
        InstrumentSourceName = "SOURCE-NAME-VALUE",
        InstrumentId = Guid.Parse("997feae2-4c97-4e14-b090-203d625ff5a5"),
        InstrumentName = "Socal-Border Index Futures",
        PriceTs = new DateTime(2026, 8, 24, 0, 0, 0, DateTimeKind.Utc),
        BusinessDate = new DateOnly(2026, 8, 24),
        PriceType = "Indicative",
        Size = 11,
        Depth = 12,
        Price = 1.11111111m,
        Ask = 2.22222222m,
        AskSize = 13,
        Bid = 3.33333333m,
        BidSize = 14,
        Mid = 4.44444444m,
        MidSize = 15,
        Change = 5.55555555m,
        PctRetDaily = 6.66666666m,
        Currency = "USD",
        Checksum = 123456789
    };

    /// <summary>A row with every nullable payload column left NULL — the nine-always-NULL reality.</summary>
    private static MarketDataRow MinimalRow(Guid id) => new()
    {
        FileLogId = 7,
        MarketDataId = id,
        Checksum = 42
    };

    // ---- the contract ---------------------------------------------------------------------------

    [Fact]
    public void BuildTable_ColumnNames_MatchTvpExactlyAndInOrder()
    {
        var t = BuildTable(Sink(), new[] { FullRow() });
        Assert.Equal(TvpColumns, Names(t));
    }

    [Fact]
    public void BuildTable_ColumnTypes_MatchTvpExactlyAndInOrder()
    {
        var t = BuildTable(Sink(), new[] { FullRow() });
        Assert.Equal(TvpTypes, Types(t));
    }

    [Fact]
    public void BuildTable_HasExactlyTwentyFiveColumns()
    {
        // A guard against a column being added on one side only. 25 is the number in 002.
        var t = BuildTable(Sink(), new[] { FullRow() });
        Assert.Equal(25, t.Columns.Count);
        Assert.Equal(25, TvpColumns.Length);
        Assert.Equal(25, TvpTypes.Length);
    }

    [Fact]
    public void BuildTable_DoesNotCarryModifiedAtUtcOrDateCreated()
    {
        // Both are stamped server-side (the MERGE / the table DEFAULT) and must NEVER cross the TVP.
        var t = BuildTable(Sink(), new[] { FullRow() });
        Assert.DoesNotContain("ModifiedAtUtc", Names(t));
        Assert.DoesNotContain("DateCreated", Names(t));
    }

    /// <summary>
    /// *** THE BID/ASK INVERSION GUARD. ***
    /// Asserts every value lands in its OWN named column. The column-list tests above would still
    /// pass if BuildTable wrote Ask into the Bid slot and vice versa, because both are
    /// <c>decimal</c>; only checking values by name catches that.
    /// </summary>
    [Fact]
    public void BuildTable_PutsEachValueInItsOwnColumn()
    {
        var row = FullRow();
        var t = BuildTable(Sink(), new[] { row });
        var r = t.Rows[0];

        Assert.Equal(4242, r["FileLogId"]);
        Assert.Equal(row.MarketDataId, r["MarketDataId"]);
        Assert.Equal("US Natural Gas Index", r["Market"]);
        Assert.Equal("Sep'26-Oct'26", r["Term"]);
        Assert.Equal("TERM2-VALUE", r["Term2"]);
        Assert.Equal("3m", r["Tenor"]);
        Assert.Equal("SOURCE-NAME-VALUE", r["InstrumentSourceName"]);
        Assert.Equal(row.InstrumentId, r["InstrumentId"]);
        Assert.Equal("Socal-Border Index Futures", r["InstrumentName"]);
        Assert.Equal(new DateTime(2026, 8, 24, 0, 0, 0, DateTimeKind.Utc), r["PriceTS"]);
        Assert.Equal(new DateTime(2026, 8, 24), r["BusinessDate"]);
        Assert.Equal("Indicative", r["PriceType"]);

        // The interleaved numeric block — the whole point of this test.
        Assert.Equal(11, r["Size"]);
        Assert.Equal(12, r["Depth"]);
        Assert.Equal(1.11111111m, r["Price"]);
        Assert.Equal(2.22222222m, r["Ask"]);
        Assert.Equal(13, r["AskSize"]);
        Assert.Equal(3.33333333m, r["Bid"]);
        Assert.Equal(14, r["BidSize"]);
        Assert.Equal(4.44444444m, r["Mid"]);
        Assert.Equal(15, r["MidSize"]);
        Assert.Equal(5.55555555m, r["Change"]);
        Assert.Equal(6.66666666m, r["PctRetDaily"]);

        Assert.Equal("USD", r["Currency"]);
        Assert.Equal(123456789, r["Checksum"]);
    }

    [Fact]
    public void BuildTable_NullPayloadColumns_BecomeDbNull_NotZeroAndNotEmptyString()
    {
        // The nine always-NULL columns must arrive as DBNull. A 0 would be a fabricated price/size.
        var t = BuildTable(Sink(), new[] { MinimalRow(Guid.NewGuid()) });
        var r = t.Rows[0];

        foreach (var col in new[]
                 {
                     "Market", "Term", "Term2", "Tenor", "InstrumentSourceName", "InstrumentId",
                     "InstrumentName", "PriceTS", "BusinessDate", "PriceType", "Size", "Depth",
                     "Price", "Ask", "AskSize", "Bid", "BidSize", "Mid", "MidSize", "Change",
                     "PctRetDaily", "Currency"
                 })
            Assert.True(r.IsNull(col), $"{col} should be DBNull when the source value is null");

        // ...but the two non-nullable ones are still present.
        Assert.False(r.IsNull("MarketDataId"));
        Assert.False(r.IsNull("Checksum"));
    }

    [Fact]
    public void BuildTable_BlankStrings_BecomeDbNull()
    {
        // NullIfEmpty: a whitespace-only vendor value is absence, not content.
        var row = FullRow();
        var blanked = new MarketDataRow
        {
            FileLogId = row.FileLogId,
            MarketDataId = row.MarketDataId,
            Market = "   ",
            Term = "",
            Currency = " ",
            Checksum = 1
        };

        var t = BuildTable(Sink(), new[] { blanked });
        var r = t.Rows[0];
        Assert.True(r.IsNull("Market"));
        Assert.True(r.IsNull("Term"));
        Assert.True(r.IsNull("Currency"));
    }

    [Fact]
    public void BuildTable_DedupsOnMergeKey_LastWins()
    {
        // The proc de-dups identically (ROW_NUMBER ... ORDER BY FileLogId DESC). A MERGE errors if one
        // target row is matched twice, so this is not optional.
        var id = Guid.Parse("6d11bc3c-a601-480a-8032-b6d42068b9ea");
        var first = FullRow(id);
        var second = FullRow(id);
        second = new MarketDataRow
        {
            FileLogId = second.FileLogId,
            MarketDataId = id,
            Market = "SECOND",
            Checksum = 999
        };

        var t = BuildTable(Sink(), new[] { first, second });

        Assert.Equal(1, t.Rows.Count);
        Assert.Equal("SECOND", t.Rows[0]["Market"]);      // last wins
        Assert.Equal(999, t.Rows[0]["Checksum"]);
    }

    [Fact]
    public void BuildTable_KeepsDistinctKeys()
    {
        var t = BuildTable(Sink(), new[] { FullRow(Guid.NewGuid()), FullRow(Guid.NewGuid()) });
        Assert.Equal(2, t.Rows.Count);
    }

    [Fact]
    public void BuildTable_BusinessDate_BindsAtMidnight()
    {
        // A SQL DATE column takes a DateTime at midnight; a stray time component would be a silent
        // truncation on the server.
        var t = BuildTable(Sink(), new[] { FullRow() });
        var value = (DateTime)t.Rows[0]["BusinessDate"];
        Assert.Equal(TimeSpan.Zero, value.TimeOfDay);
        Assert.Equal(new DateTime(2026, 8, 24), value);
    }

    // ---- the rest of the sink contract ----------------------------------------------------------

    [Fact]
    public void Sink_TargetsTheExpectedProcAndTvpType()
    {
        var sink = Sink();
        Assert.Equal("arm.usp_BulkMergeMarketData", ProtectedString(sink, "StoredProcedureName"));
        Assert.Equal("arm.MarketDataTvp", ProtectedString(sink, "TableValuedParameterType"));
    }

    [Fact]
    public void Sink_ReadsTheProcsRowCount()
    {
        // arm.usp_BulkMergeMarketData SELECTs @@ROWCOUNT AS RecordsProcessed, so the base class must
        // use ExecuteScalar. (That count is CHANGED rows, not rows sent — see the proc header.)
        Assert.True(ProtectedBool(Sink(), "ProcedureReturnsRowCount"));
    }
}
