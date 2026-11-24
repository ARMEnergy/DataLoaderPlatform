using System.Globalization;
using Xunit;

namespace DataLoader.PJM.Tests;

/// <summary>Design §12.2 tests 6-11: enumeration, zones and resume keys (§3).</summary>
public sealed class WorkUnitTests
{
    private static readonly PjmSettings S = new();
    private static readonly DateOnly Today = new(2026, 11, 23);

    private static List<PjmDayWorkUnit> Build(PjmFeed feed, DateTime startedUtc, PjmSettings? s = null) =>
        PjmWorkUnitProvider.Build(feed, s ?? S, startedUtc);

    private static DateOnly D(int month, int day) => new(2026, month, day);

    // ---- 6 ------------------------------------------------------------------------

    [Fact]
    public void T06_gen_by_fuel_at_1420_UTC_is_31_units_1_recent_9_hot_21_settled()
    {
        var units = Build(PjmFeeds.GenByFuel(S), Samples.Started);

        Assert.Equal(31, units.Count);
        Assert.Equal(Today, units[0].Day);                    // newest first
        Assert.Equal(D(10, 24), units[^1].Day);

        Assert.Equal(new[] { D(11, 23) }, units.Where(u => u.Zone == PjmZone.Recent).Select(u => u.Day));
        var hot = units.Where(u => u.Zone == PjmZone.Hot).Select(u => u.Day).OrderBy(d => d).ToList();
        Assert.Equal(9, hot.Count);
        Assert.Equal(D(11, 14), hot.First());
        Assert.Equal(D(11, 22), hot.Last());
        var settled = units.Where(u => u.Zone == PjmZone.Settled).Select(u => u.Day).OrderBy(d => d).ToList();
        Assert.Equal(21, settled.Count);
        Assert.Equal(D(10, 24), settled.First());
        Assert.Equal(D(11, 13), settled.Last());
    }

    [Fact]
    public void T06_inst_load_at_1420_UTC_is_29_units_with_the_28_day_ceiling()
    {
        var units = Build(PjmFeeds.InstLoad(S), Samples.Started);

        Assert.Equal(29, units.Count);
        Assert.Single(units, u => u.Zone == PjmZone.Recent);
        Assert.Equal(9, units.Count(u => u.Zone == PjmZone.Hot));
        var settled = units.Where(u => u.Zone == PjmZone.Settled).Select(u => u.Day).OrderBy(d => d).ToList();
        Assert.Equal(19, settled.Count);
        Assert.Equal(D(10, 26), settled.First());
        Assert.Equal(D(11, 13), settled.Last());
    }

    // ---- 7 ------------------------------------------------------------------------

    [Fact]
    public void T07_Settled_key_is_stable_across_runs_on_different_days()
    {
        var feed = PjmFeeds.GenByFuel(S);
        var a = Build(feed, Samples.Started).Single(u => u.Day == D(11, 1));
        var b = Build(feed, Samples.Started.AddDays(1)).Single(u => u.Day == D(11, 1));

        Assert.Equal(PjmZone.Settled, a.Zone);
        Assert.Equal(PjmZone.Settled, b.Zone);
        Assert.Equal("pjm:gen_by_fuel:20261101", a.Key);
        Assert.Equal(a.Key, b.Key);
    }

    [Fact]
    public void T07_Hot_key_carries_the_run_date_and_is_equal_within_one_day()
    {
        var feed = PjmFeeds.InstLoad(S);
        var morning = Build(feed, new DateTime(2026, 11, 23, 3, 0, 0, DateTimeKind.Utc)).Single(u => u.Day == D(11, 20));
        var evening = Build(feed, new DateTime(2026, 11, 23, 22, 0, 0, DateTimeKind.Utc)).Single(u => u.Day == D(11, 20));
        var nextDay = Build(feed, new DateTime(2026, 11, 24, 3, 0, 0, DateTimeKind.Utc)).Single(u => u.Day == D(11, 20));

        Assert.Equal(PjmZone.Hot, morning.Zone);
        Assert.Equal("pjm:inst_load:20261120;run=20261123", morning.Key);
        Assert.Equal(morning.Key, evening.Key);
        Assert.NotEqual(morning.Key, nextDay.Key);
    }

    [Fact]
    public void T07_Recent_key_carries_the_run_hour_and_differs_between_hours()
    {
        var feed = PjmFeeds.GenByFuel(S);
        var at14 = Build(feed, Samples.Started).Single(u => u.Day == Today);
        var at15 = Build(feed, Samples.Started.AddHours(1)).Single(u => u.Day == Today);
        var at14b = Build(feed, Samples.Started.AddMinutes(30)).Single(u => u.Day == Today);

        Assert.Equal(PjmZone.Recent, at14.Zone);
        Assert.Equal("pjm:gen_by_fuel:20261123;run=2026112314", at14.Key);
        Assert.NotEqual(at14.Key, at15.Key);
        Assert.Equal(at14.Key, at14b.Key);
    }

    // ---- 8 ------------------------------------------------------------------------

    [Fact]
    public void T08_Yesterday_is_Recent_at_0159_UTC_and_Hot_at_0200_UTC()
    {
        var feed = PjmFeeds.GenByFuel(S);
        var yesterday = D(11, 22);

        var before = Build(feed, new DateTime(2026, 11, 23, 1, 59, 0, DateTimeKind.Utc)).Single(u => u.Day == yesterday);
        var at = Build(feed, new DateTime(2026, 11, 23, 2, 0, 0, DateTimeKind.Utc)).Single(u => u.Day == yesterday);

        Assert.Equal(PjmZone.Recent, before.Zone);
        Assert.EndsWith(";run=2026112301", before.Key);
        Assert.Equal(PjmZone.Hot, at.Zone);
        Assert.EndsWith(";run=20261123", at.Key);
    }

    // ---- 9 ------------------------------------------------------------------------

    [Fact]
    public void T09_inst_load_ceiling_holds_even_with_DaysBack_40()
    {
        var s = new PjmSettings { DaysBack = 40 };
        var units = Build(PjmFeeds.InstLoad(s), Samples.Started, s);
        Assert.Equal(Today.AddDays(-28), units.Min(u => u.Day));
        Assert.Equal(29, units.Count);
    }

    [Fact]
    public void T09_gen_by_fuel_floor_holds_even_with_DaysBack_5()
    {
        var s = new PjmSettings { DaysBack = 5, SettledAfterDays = 5 };
        var units = Build(PjmFeeds.GenByFuel(s), Samples.Started, s);
        Assert.Equal(Today.AddDays(-30), units.Min(u => u.Day));
        Assert.Equal(31, units.Count);
    }

    // ---- 10 -----------------------------------------------------------------------

    [Fact]
    public void T10_FilterValue_is_invariant_under_a_non_invariant_culture()
    {
        var saved = CultureInfo.CurrentCulture;
        var savedUi = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            CultureInfo.CurrentUICulture = new CultureInfo("de-DE");
            var unit = Build(PjmFeeds.GenByFuel(S), Samples.Started).Single(u => u.Day == D(11, 2));
            Assert.Equal("11/02/2026 00:00to11/02/2026 23:59", unit.FilterValue);
            Assert.Equal("pjm:gen_by_fuel:20261102", unit.Key);
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
            CultureInfo.CurrentUICulture = savedUi;
        }
    }

    // ---- 11 -----------------------------------------------------------------------

    [Fact]
    public void T11_Keys_never_contain_the_api_key_or_a_URL()
    {
        const string sentinel = "CANARY-KEY-11-0f9e8d7c";
        var s = new PjmSettings { ApiKeyOverride = sentinel };
        var all = Build(PjmFeeds.GenByFuel(s), Samples.Started, s)
            .Concat(Build(PjmFeeds.InstLoad(s), Samples.Started, s))
            .Concat(Build(PjmFeeds.InstLoad(s), new DateTime(2026, 11, 23, 0, 30, 0, DateTimeKind.Utc), s));

        foreach (var u in all)
        {
            Assert.DoesNotContain(sentinel, u.Key);
            Assert.DoesNotContain(sentinel, u.DisplayName);
            Assert.DoesNotContain("?", u.Key);
            Assert.DoesNotContain("http", u.Key, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("/", u.Key);
            Assert.Matches(@"^pjm:(gen_by_fuel|inst_load):\d{8}(;run=\d{8}(\d{2})?)?$", u.Key);
        }
    }
}
