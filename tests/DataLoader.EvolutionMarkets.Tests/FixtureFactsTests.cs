using System.Text.Json;
using Xunit;

namespace DataLoader.EvolutionMarkets.Tests;

/// <summary>
/// Guards the committed fixtures against being "tidied" into uselessness.
///
/// <para><c>Samples/history_live_slice.json</c> is a VERBATIM 5-record slice of a real live 200 for
/// 2026-08-24. Its records were chosen because each carries a specific trap the parser has to handle:
/// a row WITH <c>tenor</c> and rows WITHOUT it, negative ask/bid/mid, <c>change</c> equal to zero,
/// positive and negative <c>change</c>, and the nine never-populated fields being ABSENT rather than
/// <c>null</c>. If someone regenerates or normalises the fixture and those traps disappear, the
/// parse tests would still pass while testing nothing — so this file asserts the traps are still
/// there.</para>
///
/// <para>It also asserts the fixtures contain no credential, which is a standing rule for every
/// fixture in this repo.</para>
/// </summary>
public class FixtureFactsTests
{
    private static JsonElement[] Records()
    {
        using var doc = Samples.LiveSliceDoc;
        return doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToArray();
    }

    private static bool Has(JsonElement e, string name) => e.TryGetProperty(name, out _);

    private static decimal? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : null;

    // ---- shape -------------------------------------------------------------------------------

    [Fact]
    public void LiveSlice_IsABareJsonArray_NotAnEnvelope()
    {
        // The documented and observed shape. If this ever becomes an object, the reader's shape-drift
        // guard is the thing under test, not the parser.
        using var doc = Samples.LiveSliceDoc;
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
    }

    [Fact]
    public void LiveSlice_HasFiveRecords()
    {
        Assert.Equal(5, Records().Length);
    }

    [Fact]
    public void LiveSlice_RecordsHaveDistinctPriceIds()
    {
        var ids = Records().Select(r => r.GetProperty("priceId").GetString()).ToList();
        Assert.Equal(5, ids.Distinct().Count());
    }

    [Fact]
    public void Empty_IsExactlyTheTwoCharacterArray()
    {
        // The real body a non-publishing day returns. It is the single most important fixture here:
        // an empty 200 must be a routine success, not an error.
        Assert.Equal("[]", Samples.Empty.Trim());
    }

    // ---- the traps ---------------------------------------------------------------------------

    [Fact]
    public void LiveSlice_StillContainsARowWithTenorAndRowsWithout()
    {
        // ~53% of live rows omit `tenor` ENTIRELY (the key is absent, not null). Both cases must stay
        // represented or the "absent key" path goes untested.
        var records = Records();
        Assert.Contains(records, r => Has(r, "tenor"));
        Assert.Contains(records, r => !Has(r, "tenor"));
    }

    [Fact]
    public void LiveSlice_StillContainsNegativePrices()
    {
        // These are basis differentials; negative values are routine and are why no price column has a
        // non-negative CHECK constraint.
        var records = Records();
        Assert.Contains(records, r => Num(r, "ask") < 0);
        Assert.Contains(records, r => Num(r, "bid") < 0);
        Assert.Contains(records, r => Num(r, "mid") < 0);
    }

    [Fact]
    public void LiveSlice_StillContainsAZeroChange()
    {
        // 0 is a real value (a point that did not move) and must never be normalised to NULL.
        Assert.Contains(Records(), r => Num(r, "change") == 0m);
    }

    [Fact]
    public void LiveSlice_StillContainsPositiveAndNegativeChange()
    {
        var records = Records();
        Assert.Contains(records, r => Num(r, "change") > 0);
        Assert.Contains(records, r => Num(r, "change") < 0);
    }

    [Fact]
    public void LiveSlice_StillOmitsTheNineNeverPopulatedFields()
    {
        // *** THE KEY FIXTURE FACT. *** The vendor OMITS these keys rather than sending null. If a
        // regenerated fixture started including them as `null`, the "absent key" code path would stop
        // being exercised — and that is the path 9 of 23 columns actually take in production.
        foreach (var field in new[]
                 {
                     "term2", "instrumentSourceName", "size", "depth", "price",
                     "askSize", "bidSize", "midSize", "pctRetDaily", "pct_ret_daily"
                 })
            Assert.All(Records(), r => Assert.False(Has(r, field),
                $"the live fixture unexpectedly carries '{field}'; the nine-always-NULL premise in " +
                "docs/apis/EvolutionMarkets.md §4.2 and sql/EvolutionMarkets/001 needs re-checking"));
    }

    [Fact]
    public void LiveSlice_UsesTheResponseVocabulary_NotTheRequestVocabulary()
    {
        // The whole two-vocabulary trap in one assertion: the RESPONSE carries priceId/instrument/
        // priceTs/date, never marketDataId/instrumentName/priceTS/businessDate.
        Assert.All(Records(), r =>
        {
            Assert.True(Has(r, "priceId"));
            Assert.True(Has(r, "instrument"));
            Assert.True(Has(r, "priceTs"));
            Assert.True(Has(r, "date"));

            Assert.False(Has(r, "marketDataId"));
            Assert.False(Has(r, "instrumentName"));
            Assert.False(Has(r, "businessDate"));
        });
    }

    [Fact]
    public void LiveSlice_PriceTsCarriesAnExplicitUtcOffset()
    {
        // The Z is what makes UTC parsing unambiguous. Without it the timezone trap in ParseTests
        // becomes real rather than defensive.
        Assert.All(Records(), r => Assert.EndsWith("Z", r.GetProperty("priceTs").GetString()));
    }

    [Fact]
    public void LiveSlice_IsAllForOneBusinessDate_MatchingTheHarnessFixtureDate()
    {
        // Each request pins dateFrom = dateTo, so a real single-date response is what the fixture must
        // represent.
        Assert.All(Records(), r =>
            Assert.Equal(EvoTime.Iso(ReaderHarness.FixtureDate), r.GetProperty("date").GetString()));
    }

    [Fact]
    public void LiveSlice_CarriesTheSingleObservedPriceTypeAndCurrency()
    {
        // Recorded as a SNAPSHOT, not a contract: neither is an enum in any vendor document, which is
        // why there is no lookup table and no CHECK constraint on either column.
        Assert.All(Records(), r => Assert.Equal("Indicative", r.GetProperty("priceType").GetString()));
        Assert.All(Records(), r => Assert.Equal("USD", r.GetProperty("currency").GetString()));
    }

    [Fact]
    public void LiveSlice_TermValuesShowMoreThanOneShape()
    {
        // "Sep'26-Oct'26", "2026-Winter", "2026-Oct" are all legitimate `term` values. This is why the
        // column is free text and is never parsed or normalised.
        var terms = Records().Select(r => r.GetProperty("term").GetString()!).Distinct().ToList();
        Assert.True(terms.Count > 1, "the fixture no longer shows more than one `term` shape");
        Assert.Contains(terms, t => t.Contains('\''));      // the quoted month-range shape
    }

    // ---- no secrets --------------------------------------------------------------------------

    [Fact]
    public void Fixtures_ContainNoCredential()
    {
        // Standing repo rule. The Evolution Markets key is a raw token in the Authorization HEADER, so
        // it could only get here by someone pasting it in.
        foreach (var content in new[] { Samples.LiveSlice, Samples.Empty })
        {
            Assert.DoesNotContain("Authorization", content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("apikey", content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Bearer", content, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Fixtures_ContainNoUrl_SoNoQueryStringCouldCarryASecret()
    {
        Assert.DoesNotContain("http", Samples.LiveSlice, StringComparison.OrdinalIgnoreCase);
    }
}
