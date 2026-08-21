using Xunit;

namespace DataLoader.IIR.Tests;

/// <summary>
/// <see cref="IirDescriptors"/> — the static discovery result whose invariants are asserted once at
/// first access (design §2.2, CWG precedent): exactly 3 descriptors, unique ids, <c>StampsRunDate</c>
/// true iff OfflineEvent, <c>StatusScoped == StampsRunDate</c>, non-blank product/id fields, and the
/// summary/detail path shape. Merely touching <see cref="IirDescriptors.All"/> runs the guarded ctor.
/// </summary>
public class DescriptorsTests
{
    [Fact]
    public void Registry_HasExactlyThreeDescriptors_WithUniqueIds()
    {
        Assert.Equal(3, IirDescriptors.All.Length);
        Assert.Equal(new[] { "Plant", "Unit", "OfflineEvent" }, IirDescriptors.All.Select(d => d.EndpointId).ToArray());
        Assert.Equal(3, IirDescriptors.All.Select(d => d.EndpointId).Distinct().Count());
        Assert.Equal(new[] { "Plant", "Unit", "OfflineEvent" }, IirDescriptors.AllIds);
    }

    [Fact]
    public void OnlyOfflineEvent_StampsRunDate_And_IsStatusScoped()
    {
        foreach (var d in IirDescriptors.All)
        {
            var isOffline = d.EndpointId == "OfflineEvent";
            Assert.Equal(isOffline, d.StampsRunDate);
            Assert.Equal(d.StampsRunDate, d.StatusScoped); // StatusScoped == StampsRunDate for all three
        }
    }

    [Fact]
    public void EveryDescriptor_HasWellFormedPathsAndIdFields()
    {
        foreach (var d in IirDescriptors.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(d.Product));
            Assert.False(string.IsNullOrWhiteSpace(d.IdField));
            Assert.False(string.IsNullOrWhiteSpace(d.IdParam));
            Assert.Contains("{ver}", d.SummaryPath);
            Assert.EndsWith("/summary", d.SummaryPath);
            Assert.EndsWith("/detail", d.DetailPath);
        }
    }

    [Fact]
    public void OfflineEvent_UsesEventIdAndOfflineEventsArrayKey()
    {
        var oe = IirDescriptors.OfflineEvent;
        Assert.Equal("eventId", oe.IdField);
        Assert.Equal("offlineEvents", oe.DataArrayKey);
        Assert.Equal("idb/{ver}/offlineevents/summary", oe.SummaryPath);
    }
}
