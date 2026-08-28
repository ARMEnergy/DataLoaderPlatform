using System.Net;
using System.Text.Json;
using Xunit;

namespace DataLoader.EvolutionMarkets.Tests;

/// <summary>
/// Pins the <c>&amp;field=</c> projection (docs/apis §4.3) — the single most surprising part of this
/// API, and the one a well-meaning "let's only request what we need" cleanup would silently break.
///
/// <para>Three live-verified vendor behaviours make the list load-bearing rather than cosmetic:</para>
/// <list type="number">
///   <item><b>Two vocabularies.</b> Fields are REQUESTED by their output/CSV names
///     (<c>marketDataId</c>, <c>instrumentName</c>, <c>priceTS</c>, <c>businessDate</c>,
///     <c>pct_ret_daily</c>) and RETURNED under their <c>responseFields</c> names
///     (<c>priceId</c>, <c>instrument</c>, <c>priceTs</c>, <c>date</c>, <c>pctRetDaily</c>).
///     Requesting the response spelling returns <b>nothing at all</b> for that field —
///     <c>field=priceId</c> yields no <c>priceId</c>.</item>
///   <item><b><c>change</c> only resolves in the FULL list.</b> In a short list the server returns
///     <c>term</c> in its place.</item>
///   <item><b>An unknown name is a 500, not a 400</b> — so a typo burns the whole retry budget on
///     every work unit.</item>
/// </list>
/// </summary>
public class FieldProjectionTests
{
    /// <summary>
    /// The 23 REQUEST names, transcribed literally from the live-verified contract. If this array and
    /// <see cref="EvoRequestFields.All"/> ever disagree, one of them was edited without the other.
    /// </summary>
    private static readonly string[] ExpectedRequestFields =
    {
        "marketDataId", "market", "term", "term2", "tenor", "instrumentSourceName", "instrumentId",
        "instrumentName", "priceTS", "businessDate", "priceType", "size", "depth", "price", "ask",
        "askSize", "bid", "bidSize", "mid", "midSize", "change", "pct_ret_daily", "currency"
    };

    [Fact]
    public void RequestFieldList_IsPinnedExactlyAndInOrder()
    {
        Assert.Equal(ExpectedRequestFields, EvoRequestFields.All);
    }

    [Fact]
    public void RequestFieldList_HasExactlyTwentyThreeNames()
    {
        // The count matters on its own: `change` resolves correctly only in the FULL list, so a
        // shorter list is a functional change, not a tidy-up.
        Assert.Equal(23, EvoRequestFields.All.Length);
    }

    [Fact]
    public void Csv_IsTheCommaJoinedList_WithNoSpaces()
    {
        Assert.Equal(string.Join(",", ExpectedRequestFields), EvoRequestFields.Csv);
        Assert.DoesNotContain(" ", EvoRequestFields.Csv);
    }

    [Fact]
    public void RequestFieldList_HasNoDuplicates()
    {
        Assert.Equal(EvoRequestFields.All.Length, EvoRequestFields.All.Distinct().Count());
    }

    /// <summary>
    /// The four names where the request spelling differs from the response spelling. Requesting the
    /// RESPONSE spelling silently returns nothing, so these must be the output names.
    /// </summary>
    [Theory]
    [InlineData("marketDataId", "priceId")]
    [InlineData("instrumentName", "instrument")]
    [InlineData("priceTS", "priceTs")]
    [InlineData("businessDate", "date")]
    [InlineData("pct_ret_daily", "pctRetDaily")]
    public void TheRequestSpellingIsUsed_NotTheResponseSpelling(string requestName, string responseName)
    {
        Assert.Contains(requestName, EvoRequestFields.All);
        Assert.DoesNotContain(responseName, EvoRequestFields.All);
    }

    [Fact]
    public void ChangeIsRequested_BecauseTheDefaultProjectionOmitsIt()
    {
        // Omitting `field` entirely returns only 10 default fields and silently drops market,
        // priceType, currency AND change.
        Assert.Contains("change", EvoRequestFields.All);
        Assert.Contains("market", EvoRequestFields.All);
        Assert.Contains("priceType", EvoRequestFields.All);
        Assert.Contains("currency", EvoRequestFields.All);
    }

    [Fact]
    public void TheNineNeverPopulatedFields_AreStillRequested()
    {
        // Requesting them is free and keeps the loader ready for a new dataset entitlement. Dropping
        // them would mean a silent gap the day the key gains a permission.
        foreach (var f in new[]
                 {
                     "term2", "instrumentSourceName", "size", "depth", "price",
                     "askSize", "bidSize", "midSize", "pct_ret_daily"
                 })
            Assert.Contains(f, EvoRequestFields.All);
    }

    // ---- the response-shape guard's expectations ------------------------------------------------

    [Fact]
    public void ExpectedResponseKeys_UseTheRESPONSEVocabulary()
    {
        // The guard checks what came BACK, so it must use the response spellings.
        Assert.Contains("priceId", EvoRequestFields.ExpectedResponseKeys);
        Assert.Contains("instrument", EvoRequestFields.ExpectedResponseKeys);
        Assert.Contains("priceTs", EvoRequestFields.ExpectedResponseKeys);
        Assert.Contains("date", EvoRequestFields.ExpectedResponseKeys);

        Assert.DoesNotContain("marketDataId", EvoRequestFields.ExpectedResponseKeys);
        Assert.DoesNotContain("businessDate", EvoRequestFields.ExpectedResponseKeys);
    }

    [Fact]
    public void ExpectedResponseKeys_ExcludeTheNeverPopulatedFields()
    {
        // Including them would make the drift guard fire on every single healthy load, and it would be
        // muted within a week.
        foreach (var f in new[]
                 {
                     "term2", "instrumentSourceName", "size", "depth", "price",
                     "askSize", "bidSize", "midSize", "pctRetDaily", "pct_ret_daily"
                 })
            Assert.DoesNotContain(f, EvoRequestFields.ExpectedResponseKeys);
    }

    [Fact]
    public void ExpectedResponseKeys_ExcludeTenor()
    {
        // `tenor` is legitimately absent on ~53% of rows, so its absence is not drift.
        Assert.DoesNotContain("tenor", EvoRequestFields.ExpectedResponseKeys);
    }

    [Fact]
    public void ExpectedResponseKeys_IncludeChange_TheBugSignature()
    {
        // A missing `change` is the observable signature of a trimmed projection, which is the whole
        // reason the guard exists.
        Assert.Contains("change", EvoRequestFields.ExpectedResponseKeys);
    }

    [Fact]
    public void EveryExpectedResponseKey_IsActuallyPresentInTheLiveFixture()
    {
        // Ties the guard's expectations to real vendor bytes: if a key is expected but the live
        // response never carries it, the guard would warn on every healthy load.
        using var doc = Samples.LiveSliceDoc;
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in doc.RootElement.EnumerateArray())
            foreach (var p in element.EnumerateObject())
                present.Add(p.Name);

        foreach (var key in EvoRequestFields.ExpectedResponseKeys)
            Assert.Contains(key, present);
    }

    // ---- the drift guard's behaviour -----------------------------------------------------------

    [Fact]
    public async Task AMissingChangeKey_TriggersTheProjectionDriftWarning()
    {
        // Simulates exactly what a trimmed `field` list produces: rows arrive, but `change` is absent
        // from every one of them.
        const string noChange = """
            [{"priceId":"00000000-0000-0000-0000-000000000001","market":"US Natural Gas Index",
              "term":"2026-Oct","instrumentId":"99c5fe58-9c6a-41d5-bd12-6340a7a6b6d6",
              "instrument":"X","priceTs":"2026-08-24T00:00:00.000Z","date":"2026-08-24",
              "priceType":"Indicative","ask":0.1,"bid":0.05,"mid":0.075,"currency":"USD"}]
            """;

        var result = await ReaderHarness.RunAsync(HttpStatusCode.OK, noChange);

        Assert.Single(result.Rows);                                   // the row still loads
        Assert.Null(result.Rows[0].Change);                           // ...with a NULL Change
        var warning = Assert.Single(result.Log.Warnings, m => m.Contains("PROJECTION DRIFT"));
        Assert.Contains("change", warning);
        Assert.Contains("field", warning);
    }

    [Fact]
    public async Task ACleanLiveResponse_DoesNotTriggerTheDriftWarning()
    {
        var result = await ReaderHarness.RunAsync(HttpStatusCode.OK, Samples.LiveSlice);
        Assert.DoesNotContain(result.Log.Warnings, m => m.Contains("PROJECTION DRIFT"));
    }

    [Fact]
    public async Task TheDriftGuard_RunsOncePerRead_NotOncePerRow()
    {
        // Union-of-keys across the page, one warning. A per-row check would emit ~205 identical
        // warnings per date.
        const string twoRowsNoChange = """
            [{"priceId":"00000000-0000-0000-0000-000000000001","ask":1},
             {"priceId":"00000000-0000-0000-0000-000000000002","ask":2}]
            """;

        var result = await ReaderHarness.RunAsync(HttpStatusCode.OK, twoRowsNoChange);
        Assert.Single(result.Log.Warnings, m => m.Contains("PROJECTION DRIFT"));
    }

    [Fact]
    public async Task AnEmptyResponse_DoesNotTriggerTheDriftWarning()
    {
        // A weekend has no rows, so there are no keys to be missing. Warning here would fire on every
        // non-publishing day.
        var result = await ReaderHarness.RunAsync(HttpStatusCode.OK, Samples.Empty);
        Assert.DoesNotContain(result.Log.Warnings, m => m.Contains("PROJECTION DRIFT"));
    }

    [Fact]
    public async Task AKeyMissingFromOnlySomeRows_IsNotDrift()
    {
        // Rows are heterogeneous: a key missing from ONE row is normal (that is how `tenor` behaves).
        // Only a key missing from EVERY row is evidence of drift.
        const string mixed = """
            [{"priceId":"00000000-0000-0000-0000-000000000001","market":"M","term":"T",
              "instrumentId":"99c5fe58-9c6a-41d5-bd12-6340a7a6b6d6","instrument":"I",
              "priceTs":"2026-08-24T00:00:00.000Z","date":"2026-08-24","priceType":"P",
              "ask":1,"bid":1,"mid":1,"change":1,"currency":"USD"},
             {"priceId":"00000000-0000-0000-0000-000000000002"}]
            """;

        var result = await ReaderHarness.RunAsync(HttpStatusCode.OK, mixed);
        Assert.DoesNotContain(result.Log.Warnings, m => m.Contains("PROJECTION DRIFT"));
    }
}
