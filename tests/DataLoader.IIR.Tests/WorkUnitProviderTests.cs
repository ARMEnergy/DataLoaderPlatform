using DataLoader.Core.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DataLoader.IIR.Tests;

/// <summary>
/// <see cref="IirWorkUnitProvider"/> — DB-free enumeration (design §5). Each descriptor emits exactly
/// ONE work unit per run whose reader then runs the two-step pull. Covers the STEP-1 summary query
/// (<c>BuildSummaryQuery</c>): a repeated <c>physicalAddressCountryName=</c> for each configured country
/// on ALL three endpoints, the OPTIONAL OfflineEvent <c>eventKind</c>/<c>eventStatusDesc</c> narrowing
/// (present only when configured, absent by the new default), and escaping. Also the Central run-date
/// stamp + resume key + HotKeyStrategy. A midday-UTC start keeps the Central date stable regardless of
/// the host's Central-vs-UTC-fallback tz resolution.
/// </summary>
public class WorkUnitProviderTests
{
    // 12:00 UTC → 07:00 CDT the same calendar day → Central run date 2026-08-20 (stable under fallback).
    private static readonly DateTime StableStart = new(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc);

    private static LoaderRunContext Context(DateTime? startedAt = null, Guid? runId = null) => new()
    {
        RunId = runId ?? Guid.NewGuid(),
        StartedAtUtc = startedAt ?? StableStart,
        CancellationToken = CancellationToken.None
    };

    private static IirSettings Settings(
        IirHotKeyStrategy strategy = IirHotKeyStrategy.RunDate,
        string[]? countries = null,
        string[]? kinds = null,
        string[]? statuses = null) => new()
    {
        HotKeyStrategy = strategy,
        PhysicalAddressCountryNames = countries ?? new[] { "U.S.A.", "Canada" },
        OfflineEventKinds = kinds ?? Array.Empty<string>(),
        OfflineEventStatuses = statuses ?? Array.Empty<string>()
    };

    private static async Task<IirWorkUnit> Single(IirEndpointDescriptor d, IirSettings settings, LoaderRunContext ctx)
    {
        var provider = new IirWorkUnitProvider(d, settings, NullLogger.Instance);
        return Assert.Single(await provider.GetWorkUnitsAsync(ctx));
    }

    // ---------------------------------------------------------------- STEP-1 country query on ALL three

    [Theory]
    [InlineData("Plant")]
    [InlineData("Unit")]
    [InlineData("OfflineEvent")]
    public async Task AllEndpoints_CountryFilter_RepeatedPhysicalAddressCountryNameKeys(string endpointId)
    {
        var d = IirDescriptors.All.Single(x => x.EndpointId == endpointId);
        var u = await Single(d, Settings(), Context());
        // Default North-American scope on every endpoint; "U.S.A." dots are unreserved (not escaped).
        Assert.Equal("physicalAddressCountryName=U.S.A.&physicalAddressCountryName=Canada", u.QueryString);
    }

    [Fact]
    public async Task CountryFilter_EscapesSpaces_SkipsBlanks()
    {
        var settings = Settings(countries: new[] { "South Korea", "   ", "U.S.A." });
        var u = await Single(IirDescriptors.Plant, settings, Context());
        Assert.Equal("physicalAddressCountryName=South%20Korea&physicalAddressCountryName=U.S.A.", u.QueryString);
    }

    [Fact]
    public async Task EmptyCountryList_Plant_YieldsEmptyQuery()
    {
        var u = await Single(IirDescriptors.Plant, Settings(countries: Array.Empty<string>()), Context());
        Assert.Equal("", u.QueryString);
    }

    // ---------------------------------------------------------------- OfflineEvent optional narrowing

    [Fact]
    public async Task OfflineEvent_ByDefault_HasNoEventKindOrStatus_CountryOnly()
    {
        // New default: OfflineEventKinds/Statuses are EMPTY → the country-only pull, no event narrowing.
        var u = await Single(IirDescriptors.OfflineEvent, Settings(), Context());
        Assert.Equal("physicalAddressCountryName=U.S.A.&physicalAddressCountryName=Canada", u.QueryString);
        Assert.DoesNotContain("eventKind=", u.QueryString);
        Assert.DoesNotContain("eventStatusDesc=", u.QueryString);
    }

    [Fact]
    public async Task OfflineEvent_WhenConfigured_AppendsEventKindAndStatus_AfterCountry()
    {
        var settings = Settings(
            countries: new[] { "U.S.A." },
            kinds: new[] { "O" },
            statuses: new[] { "Ongoing", "Future" });
        var u = await Single(IirDescriptors.OfflineEvent, settings, Context());
        Assert.Equal(
            "physicalAddressCountryName=U.S.A.&eventKind=O&eventStatusDesc=Ongoing&eventStatusDesc=Future",
            u.QueryString);
    }

    [Fact]
    public async Task OfflineEvent_EventNarrowing_EscapesAndSkipsBlanks()
    {
        var settings = Settings(
            countries: Array.Empty<string>(),
            kinds: new[] { "O", "  " },
            statuses: new[] { "In Progress" });
        var u = await Single(IirDescriptors.OfflineEvent, settings, Context());
        Assert.Equal("eventKind=O&eventStatusDesc=In%20Progress", u.QueryString);
    }

    [Fact]
    public async Task Plant_IgnoresEventNarrowing_EvenWhenConfigured()
    {
        // Plant is not StatusScoped → eventKind/eventStatusDesc must never appear, even if set.
        var settings = Settings(countries: new[] { "U.S.A." }, kinds: new[] { "O" }, statuses: new[] { "Ongoing" });
        var u = await Single(IirDescriptors.Plant, settings, Context());
        Assert.Equal("physicalAddressCountryName=U.S.A.", u.QueryString);
        Assert.DoesNotContain("eventKind=", u.QueryString);
    }

    // ---------------------------------------------------------------- single unit + resume key

    [Fact]
    public async Task EmitsOneUnit_CentralRunDate_Key_DisplayName()
    {
        var u = await Single(IirDescriptors.Plant, Settings(), Context());
        Assert.Equal("Plant", u.EndpointId);
        Assert.Equal(new DateOnly(2026, 8, 20), u.RunDate);
        Assert.Equal("iir:Plant:20260820", u.KeyValue);
        Assert.Equal("iir:Plant:20260820", u.Key);
        Assert.Equal("IIR Plant 2026-08-20", u.DisplayName);
    }

    [Fact]
    public async Task HotKeyStrategy_RunId_AppendsRunSuffix_VariesAcrossRuns()
    {
        var settings = Settings(IirHotKeyStrategy.RunId);
        var k1 = (await Single(IirDescriptors.Plant, settings, Context(runId: Guid.NewGuid()))).Key;
        var k2 = (await Single(IirDescriptors.Plant, settings, Context(runId: Guid.NewGuid()))).Key;

        Assert.StartsWith("iir:Plant:20260820:run=", k1);
        Assert.NotEqual(k1, k2); // RunId in the key → every invocation re-pulls
    }

    [Fact]
    public async Task HotKeyStrategy_RunDate_KeyStableAcrossRunsSameDay()
    {
        var settings = Settings(IirHotKeyStrategy.RunDate);
        var k1 = (await Single(IirDescriptors.Plant, settings, Context(runId: Guid.NewGuid()))).Key;
        var k2 = (await Single(IirDescriptors.Plant, settings, Context(runId: Guid.NewGuid()))).Key;
        Assert.Equal(k1, k2); // same Central day → same key → idempotent skip
    }

    // ---------------------------------------------------------------- US-Central date stamping (offset-sensitive)

    [Fact]
    public async Task RunDate_UsesUsCentralCalendarDate_NotUtc_ForAnInstantThatDiffersByDate()
    {
        if (IirTime.UsingUtcFallback) return; // host lacks the Central tz db → conversion is identity; skip

        // 03:00 UTC on 2026-08-20 = 22:00 CDT on 2026-08-19 → the Central date is the PREVIOUS day.
        var justAfterUtcMidnight = new DateTime(2026, 8, 20, 3, 0, 0, DateTimeKind.Utc);
        var u = await Single(IirDescriptors.OfflineEvent, Settings(), Context(startedAt: justAfterUtcMidnight));

        Assert.Equal(new DateOnly(2026, 8, 19), u.RunDate);          // Central, not the 2026-08-20 UTC date
        Assert.Equal("iir:OfflineEvent:20260819", u.KeyValue);
    }
}
