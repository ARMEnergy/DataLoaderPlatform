using Xunit;

namespace DataLoader.NGI.Tests;

/// <summary>
/// An INFORMATIONAL cross-check between the two captured fixtures.
///
/// <para>*** THIS IS DELIBERATELY NOT A COUPLING, NOT AN FK AND NOT A LOAD GATE. *** ***</para>
///
/// <para>In the 2026-08-21 capture the datafeed's 163 point codes happen to be exactly the 163 codes
/// in the locations crosswalk. That is an <b>observation about one snapshot</b>, not a contract:</para>
/// <list type="bullet">
///   <item>The two pipelines are <b>mutually independent</b> - <c>/bidweekDatafeed.json</c> is
///     parameterised by <b>date alone</b>, nothing is read back out of <c>arm.BidWeekLocation</c>, and
///     a <c>BidWeekData</c>-only run (or a <c>BidWeekLocations</c>-only run) is completely valid.</item>
///   <item>There is <b>no FK</b> between <c>arm.BidWeekData.PointCode</c> and
///     <c>arm.BidWeekLocation.PointCode</c>, and the locations merge is <b>upsert-only</b>, so retired
///     codes linger and a brand-new code can legitimately appear in a datafeed <i>before</i> the
///     crosswalk snapshot is refreshed.</item>
///   <item><c>arm.usp_ValidateLoad</c>'s two reconciliation checks
///     (<c>FactCodesMissingFromLocation</c> / <c>LocationCodesMissingFromFact</c>) are therefore
///     <b>informational only</b>, with <c>ExpectedCount = NULL</c>.</item>
/// </list>
///
/// <para><b>Do not promote any assertion in this file into a coupling.</b> It exists so a future
/// reader can see the observed relationship and its intended (non-)status in one place.</para>
/// </summary>
public class FixtureCrossCheckTests
{
    [Fact]
    public async Task Observation_TheTwoFixturesCoverTheSame163PointCodes()
    {
        var factCodes = (await ReaderHarness.ReadDatafeedAsync(Samples.Datafeed20260801))
            .Select(r => r.PointCode).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var crosswalkCodes = (await ReaderHarness.ReadLocationsAsync(Samples.Locations))
            .Select(r => r.PointCode).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Equal(163, factCodes.Count);
        Assert.Equal(163, crosswalkCodes.Count);

        // Observed, not enforced (see the class remarks): the sets coincided in this snapshot.
        Assert.Empty(factCodes.Except(crosswalkCodes));   // FactCodesMissingFromLocation  -> 0 here
        Assert.Empty(crosswalkCodes.Except(factCodes));   // LocationCodesMissingFromFact  -> 0 here
    }

    [Fact]
    public async Task Observation_ThePricingPointNameMatchesTheCrosswalkNameForEveryCode()
    {
        // Also observational: "Pricing Point" on the fact equalled the crosswalk LocationName for all
        // 163 codes. Still no coupling - the fact persists what the datafeed published, full stop.
        var facts = (await ReaderHarness.ReadDatafeedAsync(Samples.Datafeed20260801))
            .ToDictionary(r => r.PointCode, r => r.PricingPoint, StringComparer.OrdinalIgnoreCase);
        var crosswalk = (await ReaderHarness.ReadLocationsAsync(Samples.Locations))
            .ToDictionary(r => r.PointCode, r => r.LocationName, StringComparer.OrdinalIgnoreCase);

        foreach (var (code, name) in crosswalk)
            Assert.Equal(name, facts[code]);
    }

    [Fact]
    public void Observation_TheDesignForbidsAnFkOrReferenceProviderBetweenThePipelines()
    {
        // Structural proof that the independence is real rather than a comment: the BidWeekData
        // work-unit provider takes ONLY settings + a logger, so there is nothing it could read the
        // crosswalk through.
        var parameters = typeof(NgiBidWeekWorkUnitProvider).GetConstructors().Single()
            .GetParameters().Select(p => p.ParameterType.Name).ToArray();

        Assert.Equal(new[] { "NgiSettings", "ILogger" }, parameters);
    }

    [Fact]
    public async Task Observation_TheFeedMixesGranularPointsWithInBandAggregates()
    {
        // There is NO flag field marking aggregates, so any downstream GROUP BY double-counts unless it
        // excludes these codes. Recorded here (and by usp_ValidateLoad's AggregateRowsPresent check) so
        // the fact is not lost.
        var codes = (await ReaderHarness.ReadDatafeedAsync(Samples.Datafeed20260801))
            .Select(r => r.PointCode).ToHashSet(StringComparer.Ordinal);

        Assert.Contains("USAVG", codes);        // the national average
        Assert.Contains("STXRAVG", codes);      // a regional average
        Assert.Contains("APPREGAVG", codes);    // a sub-aggregate
        Assert.Contains("CALRAVG", codes);
        Assert.True(codes.Count(c => c.EndsWith("RAVG", StringComparison.Ordinal)) > 1);
    }
}
