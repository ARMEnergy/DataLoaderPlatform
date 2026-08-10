using Xunit;

namespace DataLoader.StormVista.Tests;

/// <summary>
/// The <see cref="StormVistaReference"/> projections that drive enumeration (§5) and
/// regional header validation (§8.2): the daily/regional model + cycle splits and the
/// ordered <c>RegionsFor</c> lookup. These are the pure, testable half of the reference
/// layer.
///
/// NOTE: the SQL provider's positional 6-result-set mapping and its fail-fast-on-empty
/// guard live inside <c>SqlStormVistaReferenceProvider.LoadAsync</c>, which constructs
/// its own <c>SqlConnection</c>; it cannot be unit-tested without a live DB or a
/// production refactor. See the QA report for the recommended (test-only) extraction.
/// </summary>
public class ReferenceTests
{
    private static StormVistaReference Reference() => ReferenceBuilder.Full();

    [Fact]
    public void DailyModels_And_RegionalModels_SplitBySupportFlags()
    {
        var r = Reference();

        Assert.Equal(new[] { "gfs", "ecmwf", "mlr15" }.ToHashSet(),
            r.DailyModels.Select(m => m.ModelSlug).ToHashSet());
        Assert.Equal(new[] { "ecmwf-weekly", "ai-fourcastnetv2-gfs-ens-weekly" }.ToHashSet(),
            r.RegionalModels.Select(m => m.ModelSlug).ToHashSet());
    }

    [Fact]
    public void DailyCycles_AreAllFour_RegionalCycles_AreOnly00And12()
    {
        var r = Reference();

        Assert.Equal(new[] { "00", "06", "12", "18" }.ToHashSet(), r.DailyCycles.ToHashSet());
        Assert.Equal(new[] { "00", "12" }.ToHashSet(), r.RegionalCycles.ToHashSet());
    }

    [Fact]
    public void RegionsFor_ReturnsSeededOrder_ForKnownSet()
    {
        var r = Reference();

        // Order is significant — it is the wide-CSV column order.
        Assert.Equal(new[] { "West", "East", "Producing" }, r.RegionsFor("3"));
        Assert.Equal(new[] { "bpa", "miso", "nyiso" }, r.RegionsFor("iso"));
    }

    [Fact]
    public void RegionsFor_UnknownSet_ReturnsEmpty()
    {
        Assert.Empty(Reference().RegionsFor("999"));
    }
}
