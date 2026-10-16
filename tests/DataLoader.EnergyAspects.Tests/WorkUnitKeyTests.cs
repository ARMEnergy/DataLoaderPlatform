using DataLoader.EnergyAspects.Models;
using Xunit;

namespace DataLoader.EnergyAspects.Tests;

/// <summary>
/// Resume-key behaviour. <c>core.LoadLog</c> skips a work unit whose key has
/// already completed, so these strings decide what is re-fetched and what is
/// not — a careless change here either reloads 90 days for 207 mappings or
/// silently skips data that was never loaded.
/// </summary>
public class WorkUnitKeyTests
{
    private static DatasetMapping Mapping(int id = 375, params int[] datasetIds) => new()
    {
        MappingId = id,
        Name = "OilX supplydemand",
        Category = "oil",
        Licensed = "yes",
        DatasetIds = datasetIds.Length > 0 ? datasetIds.ToList() : new List<int> { 1, 2, 3 }
    };

    private static EnergyAspectsWorkUnit Unit(int part = 1, int partCount = 1) => new()
    {
        Mapping = Mapping(),
        DatasetIds = new[] { 1, 2, 3 },
        WindowStart = new DateTime(2026, 9, 11),
        WindowEnd = new DateTime(2026, 10, 10),
        PartNumber = part,
        Settled = true,
        PartCount = partCount
    };

    [Fact]
    public void SinglePartKeyIsUnchangedFromBeforeSplittingExisted()
    {
        // The 204 mappings that fit in one request must keep the exact key
        // their completed LoadLog rows were written under, or introducing
        // splitting would silently reload all of their history.
        Assert.Equal(
            "ea:mapping=375;from=2026-09-11;to=2026-10-10",
            Unit().Key);
    }

    [Fact]
    public void MultiPartKeyNamesThePartAndTheTotal()
    {
        Assert.Equal(
            "ea:mapping=375;part=3of7;from=2026-09-11;to=2026-10-10",
            Unit(part: 3, partCount: 7).Key);
    }

    [Fact]
    public void PartsOfOneWindowGetDistinctKeys()
    {
        var keys = Enumerable.Range(1, 7).Select(p => Unit(p, 7).Key).ToList();
        Assert.Equal(7, keys.Distinct().Count());
    }

    [Fact]
    public void ChangingThePartCountChangesTheKey()
    {
        // Deliberate: if a mapping grows enough to need another request, the
        // old parts no longer cover the same ids, so their completed rows must
        // stop matching and the window must be re-fetched.
        Assert.NotEqual(Unit(part: 1, partCount: 7).Key, Unit(part: 1, partCount: 8).Key);
    }

    [Fact]
    public void KeyIsStableAcrossRunsForTheSameWindowAndPart() =>
        Assert.Equal(Unit(part: 2, partCount: 7).Key, Unit(part: 2, partCount: 7).Key);

    [Fact]
    public void KeyFitsTheLoadLogColumn()
    {
        // core.LoadLog.WorkUnitKey is nvarchar(450).
        Assert.True(Unit(part: 7, partCount: 7).Key.Length <= 450);
    }

    [Fact]
    public void DifferentWindowsOfTheSamePartGetDistinctKeys()
    {
        var first = new EnergyAspectsWorkUnit
        {
            Mapping = Mapping(), DatasetIds = new[] { 1 },
            WindowStart = new DateTime(2026, 8, 12), WindowEnd = new DateTime(2026, 9, 10),
            PartNumber = 2, PartCount = 7, Settled = true
        };
        var second = Unit(part: 2, partCount: 7);

        Assert.NotEqual(first.Key, second.Key);
    }

    [Fact]
    public void DisplayNameFlagsASplitUnit()
    {
        Assert.DoesNotContain("part", Unit().DisplayName);
        Assert.Contains("part 3/7", Unit(part: 3, partCount: 7).DisplayName);
    }
}
