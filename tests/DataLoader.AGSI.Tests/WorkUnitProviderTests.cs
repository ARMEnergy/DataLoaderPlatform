using DataLoader.Core.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DataLoader.AGSI.Tests;

/// <summary>
/// <see cref="AgsiStorageWorkUnitProvider"/> — DB-free work-unit enumeration
/// (design §3.2). It crosses the distinct country codes (from the country provider)
/// with the trailing <c>DaysBack</c> window ending at the newest requestable gas day
/// (<c>runDate + DateOffsetDays</c>, via the SHARED <see cref="AgsiTime.ResolveWindow"/>
/// helper the validator also uses — so the two windows can't drift). Each unit's
/// <c>RequestPath</c> is the sanitized <c>/api?country=&amp;date=</c> descriptor.
/// </summary>
public class WorkUnitProviderTests
{
    private static readonly DateTime StartedAt = new(2026, 8, 17, 12, 0, 0, DateTimeKind.Utc); // runDate 2026-08-17

    private static LoaderRunContext Context() => new()
    {
        RunId = Guid.NewGuid(),
        StartedAtUtc = StartedAt,
        CancellationToken = CancellationToken.None
    };

    private static async Task<IReadOnlyList<AgsiStorageWorkUnit>> Enumerate(
        AgsiSettings settings, params string[] codes)
    {
        var provider = new AgsiStorageWorkUnitProvider(
            settings, new FakeAgsiCountryProvider(codes), NullLogger.Instance);
        return await provider.GetWorkUnitsAsync(Context());
    }

    [Fact]
    public async Task Enumerate_ProducesCountriesTimesWindow_OverExpectedDateRange()
    {
        var settings = new AgsiSettings { DaysBack = 3, SettledAfterDays = 21 };

        var units = await Enumerate(settings, "de", "at");

        // 2 countries × 3-day window (newest 2026-08-16 back to 2026-08-14).
        Assert.Equal(6, units.Count);
        Assert.Equal(
            new[] { new DateOnly(2026, 8, 14), new DateOnly(2026, 8, 15), new DateOnly(2026, 8, 16) },
            units.Select(u => u.Date).Distinct().OrderBy(d => d).ToArray());
        Assert.Equal(new[] { "at", "de" }, units.Select(u => u.CountryCode).Distinct().OrderBy(c => c).ToArray());
    }

    [Fact]
    public async Task Enumerate_RequestPath_IsSanitizedCountryDateDescriptor()
    {
        var settings = new AgsiSettings { DaysBack = 1, SettledAfterDays = 21 };

        var units = await Enumerate(settings, "de");

        var u = Assert.Single(units);
        Assert.Equal(new DateOnly(2026, 8, 16), u.Date); // newest = runDate − 1
        Assert.Equal("/api?country=de&date=2026-08-16", u.RequestPath);
        Assert.Equal("AGSI storage de 2026-08-16", u.DisplayName);
    }

    [Fact]
    public async Task Enumerate_NewestDateEqualsResolveWindowNewest_NoDrift()
    {
        var settings = new AgsiSettings { DaysBack = 5, SettledAfterDays = 21 };
        var window = AgsiTime.ResolveWindow(StartedAt, settings.DaysBack);

        var units = await Enumerate(settings, "de");

        Assert.Equal(window.Newest, units.Max(u => u.Date));  // newest unit == window.To
        Assert.Equal(window.From, units.Min(u => u.Date));    // oldest unit == window.From
        Assert.Equal(window.DaysBack, units.Select(u => u.Date).Distinct().Count());
    }

    [Fact]
    public async Task Enumerate_CountryCodeIsLowercased()
    {
        var settings = new AgsiSettings { DaysBack = 1, SettledAfterDays = 21 };

        var units = await Enumerate(settings, "DE");

        var u = Assert.Single(units);
        Assert.Equal("de", u.CountryCode); // lowercased for the case-insensitive API + resume key
        Assert.Equal("/api?country=de&date=2026-08-16", u.RequestPath);
    }

    [Fact]
    public async Task Enumerate_BlankCountryCode_IsSkipped()
    {
        var settings = new AgsiSettings { DaysBack = 1, SettledAfterDays = 21 };

        var units = await Enumerate(settings, "de", "   ");

        var u = Assert.Single(units); // the blank code produced no unit
        Assert.Equal("de", u.CountryCode);
    }

    [Fact]
    public async Task Enumerate_DaysBackBelow1_ClampedTo1()
    {
        var settings = new AgsiSettings { DaysBack = 0, SettledAfterDays = 21 };

        var units = await Enumerate(settings, "de");

        Assert.Single(units); // Math.Max(1, DaysBack)
    }

    // ---------------------------------------------------------------- EntityId comes from the provider's (Id, Code) pair

    [Fact]
    public async Task Enumerate_StampsEntityIdFromCountryId_ByPairing_NotArrayPosition()
    {
        // Non-sequential Ids that deliberately do NOT equal (arrayIndex + 1): if the provider
        // stamped EntityId positionally the pairing below would fail. Multi-country so a single
        // accidental match can't pass the whole assertion.
        var pairs = new[]
        {
            new AgsiCountry(500, "de"),
            new AgsiCountry(200, "at"),
            new AgsiCountry(900, "nl")
        };
        var settings = new AgsiSettings { DaysBack = 3, SettledAfterDays = 21 };
        var provider = new AgsiStorageWorkUnitProvider(
            settings, new FakeAgsiCountryProvider(pairs), NullLogger.Instance);

        var units = await provider.GetWorkUnitsAsync(Context());

        // Every unit for a given (lowercased) code must carry that code's paired Id — for ALL days.
        var expected = new Dictionary<string, int> { ["de"] = 500, ["at"] = 200, ["nl"] = 900 };
        Assert.Equal(3 * expected.Count, units.Count);
        Assert.All(units, u => Assert.Equal(expected[u.CountryCode], u.EntityId));

        // And each code contributes the full window under exactly one distinct EntityId (no cross-wiring).
        foreach (var g in units.GroupBy(u => u.CountryCode))
        {
            Assert.Equal(3, g.Count());
            Assert.Equal(expected[g.Key], Assert.Single(g.Select(u => u.EntityId).Distinct()));
        }
    }
}
