using Xunit;

namespace DataLoader.EvolutionMarkets.Tests;

/// <summary>
/// Pins <see cref="EvoChecksum"/>, which drives the MERGE's change short-circuit and therefore
/// decides what <c>arm.MarketData.ModifiedAtUtc</c> means.
///
/// <para>Two failure modes matter, and both are SILENT in production:</para>
/// <list type="number">
///   <item><b>An unstable hash</b> (the <c>string.GetHashCode</c> trap: .NET randomises string
///     hashing per process) makes every row look changed on every run. ModifiedAtUtc becomes "time
///     of last run", every re-pull dirties ~4,400 rows, and nobody notices because the data is
///     still correct.</item>
///   <item><b>An over-sensitive hash</b> (an un-normalised decimal render) does the same thing for a
///     subtler reason: the API sends JSON numbers whose scale varies row to row, so <c>0.5</c> and
///     <c>0.50</c> would hash differently despite being the same price.</item>
/// </list>
/// <para>Neither would fail a build, a load, or a spot check of the data. Only these tests catch
/// them.</para>
/// </summary>
public class ChecksumTests
{
    private static MarketDataRow Row(Action<Builder>? tweak = null)
    {
        var b = new Builder();
        tweak?.Invoke(b);
        return b.Build();
    }

    /// <summary>A mutable builder, because <see cref="MarketDataRow"/> uses <c>init</c> accessors.</summary>
    private sealed class Builder
    {
        public int FileLogId = 4242;
        public Guid MarketDataId = Guid.Parse("6d11bc3c-a601-480a-8032-b6d42068b9ea");
        public string? Market = "US Natural Gas Index";
        public string? Term = "Sep'26-Oct'26";
        public string? Term2;
        public string? Tenor = "3m";
        public string? InstrumentSourceName;
        public Guid? InstrumentId = Guid.Parse("997feae2-4c97-4e14-b090-203d625ff5a5");
        public string? InstrumentName = "Socal-Border Index Futures";
        public DateTime? PriceTs = new(2026, 8, 24, 0, 0, 0, DateTimeKind.Utc);
        public DateOnly? BusinessDate = new(2026, 8, 24);
        public string? PriceType = "Indicative";
        public int? Size;
        public int? Depth;
        public decimal? Price;
        public decimal? Ask = -0.02m;
        public int? AskSize;
        public decimal? Bid = -0.0325m;
        public int? BidSize;
        public decimal? Mid = -0.0263m;
        public int? MidSize;
        public decimal? Change = -0.0263m;
        public decimal? PctRetDaily;
        public string? Currency = "USD";

        public MarketDataRow Build() => new()
        {
            FileLogId = FileLogId,
            MarketDataId = MarketDataId,
            Market = Market,
            Term = Term,
            Term2 = Term2,
            Tenor = Tenor,
            InstrumentSourceName = InstrumentSourceName,
            InstrumentId = InstrumentId,
            InstrumentName = InstrumentName,
            PriceTs = PriceTs,
            BusinessDate = BusinessDate,
            PriceType = PriceType,
            Size = Size,
            Depth = Depth,
            Price = Price,
            Ask = Ask,
            AskSize = AskSize,
            Bid = Bid,
            BidSize = BidSize,
            Mid = Mid,
            MidSize = MidSize,
            Change = Change,
            PctRetDaily = PctRetDaily,
            Currency = Currency
        };
    }

    // ---- stability -----------------------------------------------------------------------------

    [Fact]
    public void SameRow_HashesIdentically()
    {
        Assert.Equal(EvoChecksum.Compute(Row()), EvoChecksum.Compute(Row()));
    }

    /// <summary>
    /// *** THE PROCESS-STABILITY PIN. ***
    /// A HARD-CODED expected value. If someone swaps FNV-1a for <c>GetHashCode</c>,
    /// <c>HashCode.Combine</c> or any other seeded/randomised algorithm, this test fails immediately
    /// rather than the change guard quietly ceasing to work. The number below was produced by the
    /// committed implementation; if the canonical form is ever intentionally changed, update it
    /// deliberately and note why in the design doc.
    /// </summary>
    [Fact]
    public void Checksum_IsAFixedValue_NotAProcessSeededHash()
    {
        var actual = EvoChecksum.Compute(Row());

        // Regenerate deliberately (never blindly) if the canonical form changes on purpose.
        Assert.Equal(EvoChecksumExpected.FullRow, actual);
    }

    [Fact]
    public void Canonical_IncludesEveryPayloadColumn_InTvpOrder()
    {
        // 22 payload fields => 22 separator-terminated segments.
        var canonical = EvoChecksum.Canonical(Row());
        var segments = canonical.Split('\u001F');

        // Split on a trailing separator yields one extra empty tail element.
        Assert.Equal(23, segments.Length);
        Assert.Equal(string.Empty, segments[^1]);

        // Spot-check the order matches the TVP: Market first, Currency last.
        Assert.Equal("US Natural Gas Index", segments[0]);
        Assert.Equal("USD", segments[21]);
    }

    // ---- what must NOT change the hash ---------------------------------------------------------

    [Fact]
    public void MarketDataId_IsExcluded_BecauseItIsTheMergeKey()
    {
        // The key is identical on both sides of every MATCHED comparison, so including it would add
        // nothing — and would make the hash useless for detecting a changed payload on a fixed key.
        var a = EvoChecksum.Compute(Row());
        var b = EvoChecksum.Compute(Row(r => r.MarketDataId = Guid.NewGuid()));
        Assert.Equal(a, b);
    }

    [Fact]
    public void FileLogId_IsExcluded_BecauseItIsProvenanceNotPayload()
    {
        // A re-pull always mints a new hub row. If FileLogId were hashed, EVERY row would look
        // changed on EVERY run and the guard would be silently defeated — the exact bug this excludes.
        var a = EvoChecksum.Compute(Row());
        var b = EvoChecksum.Compute(Row(r => r.FileLogId = 999999));
        Assert.Equal(a, b);
    }

    [Theory]
    [InlineData("0.5", "0.50")]
    [InlineData("1", "1.00000000")]
    [InlineData("-0.0263", "-0.02630000")]
    public void DecimalScale_DoesNotAffectTheHash(string a, string b)
    {
        // decimal preserves trailing zeros in its scale, so ToString() differs for equal numbers.
        // The API sends numbers whose scale varies row to row, so an un-normalised render would
        // report phantom changes on rows that never moved.
        var da = decimal.Parse(a, System.Globalization.CultureInfo.InvariantCulture);
        var db = decimal.Parse(b, System.Globalization.CultureInfo.InvariantCulture);
        Assert.NotEqual(da.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        db.ToString(System.Globalization.CultureInfo.InvariantCulture));   // the trap is real

        Assert.Equal(
            EvoChecksum.Compute(Row(r => r.Ask = da)),
            EvoChecksum.Compute(Row(r => r.Ask = db)));
    }

    [Fact]
    public void PriceTs_SubSecondPrecision_DoesNotAffectTheHash()
    {
        // The column is DATETIME2(0). Hashing milliseconds the database cannot store would flag a
        // change that the write cannot actually represent.
        var a = EvoChecksum.Compute(Row(r => r.PriceTs = new DateTime(2026, 8, 24, 0, 0, 0, 0, DateTimeKind.Utc)));
        var b = EvoChecksum.Compute(Row(r => r.PriceTs = new DateTime(2026, 8, 24, 0, 0, 0, 999, DateTimeKind.Utc)));
        Assert.Equal(a, b);
    }

    // ---- what MUST change the hash -------------------------------------------------------------

    [Fact]
    public void ChangedPrice_ChangesTheHash()
    {
        Assert.NotEqual(
            EvoChecksum.Compute(Row()),
            EvoChecksum.Compute(Row(r => r.Ask = -0.03m)));
    }

    [Fact]
    public void SwappedBidAndAsk_ChangesTheHash()
    {
        // Belt-and-braces against a positional mix-up upstream of the sink.
        Assert.NotEqual(
            EvoChecksum.Compute(Row()),
            EvoChecksum.Compute(Row(r => { (r.Ask, r.Bid) = (r.Bid, r.Ask); })));
    }

    [Fact]
    public void NullVersusEmptyString_AreDistinct()
    {
        // A value being CLEARED by the vendor must register as a change. If null and "" hashed alike,
        // a cleared Tenor would be invisible.
        Assert.NotEqual(
            EvoChecksum.Compute(Row(r => r.Tenor = null)),
            EvoChecksum.Compute(Row(r => r.Tenor = string.Empty)));
    }

    [Fact]
    public void NullVersusZero_AreDistinct()
    {
        // NULL means "not published"; 0 is a real, tradeable price. They must never collide.
        Assert.NotEqual(
            EvoChecksum.Compute(Row(r => r.Change = null)),
            EvoChecksum.Compute(Row(r => r.Change = 0m)));
    }

    [Fact]
    public void FieldBoundaries_AreRespected_NoConcatenationCollision()
    {
        // Without a separator, ("ab","c") and ("a","bc") would hash alike. Move a character across
        // the Term/Term2 boundary and the hash must change.
        var a = EvoChecksum.Compute(Row(r => { r.Term = "AB"; r.Term2 = "C"; }));
        var b = EvoChecksum.Compute(Row(r => { r.Term = "A"; r.Term2 = "BC"; }));
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void EveryPayloadColumn_ParticipatesInTheHash()
    {
        // If a column were forgotten in Canonical(), a change to it would be invisible to the MERGE
        // guard and the value would never be updated in the database. This enumerates all 22.
        var baseline = EvoChecksum.Compute(Row());

        var mutations = new (string Name, Action<Builder> Mutate)[]
        {
            ("Market",               r => r.Market = "OTHER"),
            ("Term",                 r => r.Term = "OTHER"),
            ("Term2",                r => r.Term2 = "OTHER"),
            ("Tenor",                r => r.Tenor = "9m"),
            ("InstrumentSourceName", r => r.InstrumentSourceName = "OTHER"),
            ("InstrumentId",         r => r.InstrumentId = Guid.NewGuid()),
            ("InstrumentName",       r => r.InstrumentName = "OTHER"),
            ("PriceTs",              r => r.PriceTs = new DateTime(2026, 8, 25, 1, 2, 3, DateTimeKind.Utc)),
            ("BusinessDate",         r => r.BusinessDate = new DateOnly(2026, 8, 25)),
            ("PriceType",            r => r.PriceType = "Settle"),
            ("Size",                 r => r.Size = 5),
            ("Depth",                r => r.Depth = 6),
            ("Price",                r => r.Price = 7.5m),
            ("Ask",                  r => r.Ask = 8.5m),
            ("AskSize",              r => r.AskSize = 9),
            ("Bid",                  r => r.Bid = 10.5m),
            ("BidSize",              r => r.BidSize = 11),
            ("Mid",                  r => r.Mid = 12.5m),
            ("MidSize",              r => r.MidSize = 13),
            ("Change",               r => r.Change = 14.5m),
            ("PctRetDaily",          r => r.PctRetDaily = 15.5m),
            ("Currency",             r => r.Currency = "CAD")
        };

        Assert.Equal(22, mutations.Length);

        foreach (var (name, mutate) in mutations)
            Assert.True(
                EvoChecksum.Compute(Row(mutate)) != baseline,
                $"changing {name} did not change the checksum — it is missing from EvoChecksum.Canonical, " +
                "so the MERGE would never update that column");
    }
}

/// <summary>
/// The hard-coded expected checksum, kept in its own type so the intent is unmistakable: it is a
/// REGRESSION PIN on a persisted value, not an assertion about what the number "should" be.
/// </summary>
internal static class EvoChecksumExpected
{
    /// <summary>
    /// FNV-1a/32 of the canonical form of <c>ChecksumTests.Row()</c>'s default row — the live
    /// 2026-08-24 <c>Socal-Border Index Futures</c> / <c>Sep'26-Oct'26</c> record.
    ///
    /// <para>Change this ONLY alongside a deliberate change to the canonical form, and say so in
    /// docs/design/EvolutionMarkets.md §8.4. A change here silently invalidates every
    /// <c>Checksum</c> already in the database: the first run after it will see every row as changed
    /// and re-stamp every <c>ModifiedAtUtc</c> once. That is recoverable but it is a real, visible
    /// event — do not do it casually.</para>
    /// </summary>
    public const int FullRow = -1779413438;
}
