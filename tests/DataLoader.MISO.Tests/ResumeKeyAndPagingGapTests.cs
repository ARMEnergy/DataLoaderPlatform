using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DataLoader.MISO.Tests;

/// <summary>
/// Gaps found by the test-gate audit against design §4-§6 and the resume-key / status-matrix skill:
/// settled-key stability ACROSS run days, the shipped unit/request budget, and the reader's
/// page-&gt;1 non-data outcomes and paging backstop.
/// </summary>
public sealed class ResumeKeyAndPagingGapTests
{
    /// <summary>2026-11-24 14:00 UTC = 09:00 EST.</summary>
    private static readonly DateTime Day1 = new(2026, 11, 24, 14, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Day2 = Day1.AddDays(1);
    private static readonly DateOnly D = Samples.D(2026, 11, 18);

    // ---- §6 / skill check 1: settled keys are stable; hot keys roll over ----------------

    [Fact]
    public void Settled_keys_are_identical_on_consecutive_run_days_for_every_feed()
    {
        foreach (var feed in MisoFeeds.All)
        {
            var s = Samples.Settings(x => x.StartDate = Samples.D(2026, 9, 1));
            var run1 = MisoWorkUnitProvider.Build(feed, s, Day1);
            var run2 = MisoWorkUnitProvider.Build(feed, s, Day2);

            var settled1 = run1.Where(u => u.Zone == MisoZone.Settled).ToDictionary(u => (u.InitDate, u.MarketDate), u => u.Key);
            var settled2 = run2.Where(u => u.Zone == MisoZone.Settled).ToDictionary(u => (u.InitDate, u.MarketDate), u => u.Key);

            Assert.NotEmpty(settled1);
            // Every unit settled yesterday is still enumerated today under the SAME key (so it is skipped, never re-read).
            foreach (var (id, key) in settled1)
                Assert.Equal(key, settled2[id]);
        }
    }

    [Fact]
    public void The_day_leaving_the_hot_window_gets_exactly_one_settled_key_and_its_hot_keys_never_recur()
    {
        foreach (var feed in MisoFeeds.All)
        {
            var s = Samples.Settings(x => x.StartDate = Samples.D(2026, 9, 1));
            var run1 = MisoWorkUnitProvider.Build(feed, s, Day1);
            var run2 = MisoWorkUnitProvider.Build(feed, s, Day2);

            // Age 10 on day 1 (hot) is age 11 on day 2 (settled): one final settled read.
            var leaving = run1.Where(u => u.Age == s.SettledAfterDays).ToList();
            Assert.NotEmpty(leaving);
            Assert.All(leaving, u => Assert.Equal(MisoZone.Hot, u.Zone));
            foreach (var u in leaving)
            {
                var next = run2.Single(n => n.InitDate == u.InitDate && n.MarketDate == u.MarketDate);
                Assert.Equal(MisoZone.Settled, next.Zone);
                Assert.Equal(s.SettledAfterDays + 1, next.Age);
                Assert.NotEqual(u.Key, next.Key);
                Assert.StartsWith(next.Key + "|run=", u.Key);   // hot key = settled key + run suffix
            }

            // No hot key from day 1 is reused on day 2: every hot day is re-read once per EST day.
            var keys2 = run2.Select(u => u.Key).ToHashSet(StringComparer.Ordinal);
            Assert.DoesNotContain(run1.Where(u => u.Zone == MisoZone.Hot), u => keys2.Contains(u.Key));
        }
    }

    // ---- §6 budget: ~11k units backfill, 100 hot units per steady-state run -----------

    [Fact]
    public void Shipped_settings_enumerate_the_designed_unit_and_hot_request_budget()
    {
        var s = Samples.Settings();
        var all = MisoFeeds.All.SelectMany(f => MisoWorkUnitProvider.Build(f, s, Day1)).ToList();

        Assert.Equal(3 * 1095 + 7 * 1095, all.Count);                        // design §6: ~11k
        Assert.Equal(3 * 10 + 7 * 10, all.Count(u => u.Zone == MisoZone.Hot)); // design §6: 100 requests/run
        Assert.Equal(all.Count, all.Select(u => u.Key).Distinct(StringComparer.Ordinal).Count());
    }

    // ---- §4/§5: non-data outcomes after page 1, and the paging backstop ------------------

    [Theory]
    [InlineData(MisoPageOutcome.NotYet, "not-yet")]
    [InlineData(MisoPageOutcome.OutOfRetention, "out-of-retention")]
    public async Task A_not_yet_or_out_of_retention_answer_on_page_2_fails_the_unit(MisoPageOutcome outcome, string fragment)
    {
        var rows = Samples.ZonalDay(D);
        var pages = new FakePageSource(p => p == 1
            ? MisoApiClient.ParseEnvelope(Samples.Envelope(rows.Take(100), totalElements: 144, pageNumber: 1, lastPage: false, totalPages: 2), "t")
            : MisoPageResult.NonData(outcome, "r"));
        var reader = new MisoDayReader<MisoZonalLoadHourlyRow>(pages, new MisoZonalLoadMapper(), Samples.Settings(), NullLogger.Instance);

        var ex = await Assert.ThrowsAsync<MisoDataException>(() => reader.ReadAsync(Samples.Unit(MisoFeeds.ZonalLoadHourly, D), default));
        Assert.Contains("page 2", ex.Message);
        Assert.Contains(fragment, ex.Message);
        Assert.Equal(new[] { 1, 2 }, pages.Pages);
    }

    [Fact]
    public async Task A_vendor_that_never_reports_lastPage_fails_at_the_MaxPages_backstop()
    {
        var pages = new FakePageSource(p => MisoApiClient.ParseEnvelope(
            Samples.Envelope(Array.Empty<JsonNode>(), totalElements: 144, pageNumber: p, lastPage: false, totalPages: 999), "t"));
        var reader = new MisoDayReader<MisoZonalLoadHourlyRow>(pages, new MisoZonalLoadMapper(), Samples.Settings(), NullLogger.Instance);

        var ex = await Assert.ThrowsAsync<MisoDataException>(() => reader.ReadAsync(Samples.Unit(MisoFeeds.ZonalLoadHourly, D), default));
        Assert.Contains("refusing to page further", ex.Message);
        Assert.Equal(MisoDayReader<MisoZonalLoadHourlyRow>.MaxPages, pages.Pages.Count);
    }
}
