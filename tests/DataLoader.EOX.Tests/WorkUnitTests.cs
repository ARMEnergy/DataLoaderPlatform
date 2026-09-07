using DataLoader.Core.Sources;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DataLoader.EOX.Tests;

/// <summary>
/// The resume key and the date window — the two places where a subtle bug silently
/// stops loading data rather than failing loudly.
/// </summary>
public sealed class WorkUnitTests
{
    private static EoxWorkUnitProvider Provider(
        EoxFeedDescriptor feed, FakeEoxFtp ftp, EoxSettings settings, FakeEoxFileLog? fileLog = null) =>
        new(feed, new EoxListingCache(ftp), fileLog ?? new FakeEoxFileLog(), settings, NullLogger.Instance);

    // ---------------------------------------------------------------- file naming

    [Theory]
    [InlineData("CrudeOil", "EOD_CSV_C_20221223_1430.csv")]
    [InlineData("NaturalGas", "EOD_CSV_NG_20221223_1430.csv")]
    [InlineData("NGL", "EOD_CSV_NGL_20221223_1430.csv")]
    public void File_names_are_built_exactly_as_the_drop_publishes_them(string feedId, string expected)
    {
        var feed = EoxDescriptors.Find(feedId)!;

        Assert.Equal(expected, feed.FileNameFor(new DateOnly(2022, 12, 23), "1430"));
    }

    /// <summary>
    /// The drop holds ~20 files named like
    /// <c>EOD_CSV_C_20210622_1430 (EOXHOUMD11's conflicted copy 2021-06-22).csv</c>.
    /// Addressing files by exact constructed name — not by a glob — is what keeps
    /// them out, and would also have kept the .xlsx twins and the 20YR series out.
    /// </summary>
    [Fact]
    public void A_conflicted_copy_sharing_the_prefix_and_date_is_never_selected()
    {
        var feed = EoxDescriptors.CrudeOil;
        var real = feed.FileNameFor(new DateOnly(2021, 6, 22), "1430");
        var decoy = "EOD_CSV_C_20210622_1430 (EOXHOUMD11's conflicted copy 2021-06-22).csv";

        Assert.NotEqual(real, decoy);
        Assert.StartsWith(feed.FilePrefix + "20210622", decoy);   // shares prefix AND date
    }

    // ---------------------------------------------------------------- window

    [Fact]
    public async Task The_window_is_today_minus_DaysBack_through_today_inclusive()
    {
        var settings = TestHelpers.Settings(s => { s.DaysBack = 3; s.SettledAfterDays = 3; });
        var context = TestHelpers.Context(new DateTime(2026, 9, 4, 18, 0, 0, DateTimeKind.Utc));
        var today = EoxTime.Today(context.StartedAtUtc);

        var dates = Enumerable.Range(0, 10).Select(i => today.AddDays(-i)).ToArray();
        var ftp = new FakeEoxFtp().WithDates("/", "1430", dates);

        var units = await Provider(EoxDescriptors.CrudeOil, ftp, settings).GetWorkUnitsAsync(context);

        Assert.Equal(4, units.Count);                                    // today + 3 back
        Assert.Equal(today, units.Max(u => u.CurveDate));
        Assert.Equal(today.AddDays(-3), units.Min(u => u.CurveDate));
    }

    [Fact]
    public async Task Units_come_back_newest_first()
    {
        var settings = TestHelpers.Settings(s => { s.DaysBack = 5; s.SettledAfterDays = 5; });
        var context = TestHelpers.Context();
        var today = EoxTime.Today(context.StartedAtUtc);

        var ftp = new FakeEoxFtp().WithDates("/", "1430", Enumerable.Range(0, 6).Select(i => today.AddDays(-i)).ToArray());
        var units = await Provider(EoxDescriptors.NaturalGas, ftp, settings).GetWorkUnitsAsync(context);

        Assert.Equal(units.OrderByDescending(u => u.CurveDate).Select(u => u.CurveDate), units.Select(u => u.CurveDate));
    }

    /// <summary>
    /// A negative DaysBack would invert the window and produce zero units — a loader
    /// that silently does nothing while reporting success.
    /// </summary>
    [Fact]
    public async Task A_negative_DaysBack_is_clamped_to_today_only()
    {
        var settings = TestHelpers.Settings(s => { s.DaysBack = -10; s.SettledAfterDays = 30; });
        var context = TestHelpers.Context();
        var today = EoxTime.Today(context.StartedAtUtc);

        var ftp = new FakeEoxFtp().WithDates("/", "1430", today, today.AddDays(-1));
        var units = await Provider(EoxDescriptors.CrudeOil, ftp, settings).GetWorkUnitsAsync(context);

        Assert.Single(units);
        Assert.Equal(today, units[0].CurveDate);
    }

    /// <summary>
    /// A date the drop has no file for is a first-class NON-error outcome — EOX
    /// publishes on trading days only. It produces no unit and one auditable
    /// <c>NotAvailable</c> hub row.
    /// </summary>
    [Fact]
    public async Task A_date_with_no_file_yields_no_unit_and_one_NotAvailable_row()
    {
        var settings = TestHelpers.Settings(s => { s.DaysBack = 2; s.SettledAfterDays = 2; });
        var context = TestHelpers.Context();
        var today = EoxTime.Today(context.StartedAtUtc);

        // Only today and today-2 exist; today-1 is the "weekend".
        var ftp = new FakeEoxFtp().WithDates("/", "1430", today, today.AddDays(-2));
        var fileLog = new FakeEoxFileLog();

        var units = await Provider(EoxDescriptors.Ngl, ftp, settings, fileLog).GetWorkUnitsAsync(context);

        Assert.Equal(2, units.Count);
        var recorded = Assert.Single(fileLog.Calls);
        Assert.Equal("NotAvailable", recorded.Status);
        Assert.Equal(0, recorded.RowCount);
        Assert.Equal(today.AddDays(-1), recorded.File.CurveDate);
        Assert.Equal(EoxDescriptors.Ngl.FileNameFor(today.AddDays(-1), "1430"), recorded.File.FileName);
    }

    /// <summary>The hub path must never carry the password, because humans read it.</summary>
    [Fact]
    public async Task The_recorded_request_path_carries_no_credentials()
    {
        var settings = TestHelpers.Settings(s =>
        {
            s.DaysBack = 1;
            s.SettledAfterDays = 1;
            s.Username = "arm@eoxlive.com";
            s.Password = "hunter2";
        });

        var fileLog = new FakeEoxFileLog();
        var units = await Provider(EoxDescriptors.CrudeOil, new FakeEoxFtp().WithDirectory("/"), settings, fileLog)
            .GetWorkUnitsAsync(TestHelpers.Context());

        Assert.Empty(units);
        Assert.NotEmpty(fileLog.Calls);
        Assert.All(fileLog.Calls, c =>
        {
            Assert.DoesNotContain("hunter2", c.File.RequestPath);
            Assert.DoesNotContain("@eoxlive.com:", c.File.RequestPath);
            Assert.StartsWith("ftp://ftp.eoxlive.com:21/", c.File.RequestPath);
        });
    }

    /// <summary>
    /// The root holds 18k entries and listing it costs ~1.7 s, so all three feeds
    /// must share ONE listing per run.
    /// </summary>
    [Fact]
    public async Task All_three_feeds_share_a_single_directory_listing()
    {
        var settings = TestHelpers.Settings(s => { s.DaysBack = 1; s.SettledAfterDays = 1; });
        var context = TestHelpers.Context();
        var today = EoxTime.Today(context.StartedAtUtc);

        var ftp = new FakeEoxFtp().WithDates("/", "1430", today);
        var cache = new EoxListingCache(ftp);
        var fileLog = new FakeEoxFileLog();

        foreach (var feed in EoxDescriptors.All)
            await new EoxWorkUnitProvider(feed, cache, fileLog, settings, NullLogger.Instance)
                .GetWorkUnitsAsync(context);

        Assert.Equal(1, ftp.ListCallCount);
    }

    // ---------------------------------------------------------------- resume key

    [Fact]
    public void A_settled_key_is_stable_across_runs_while_the_file_is_unchanged()
    {
        var unit = TestHelpers.Unit(EoxDescriptors.CrudeOil, isHot: false, keySuffix: "");
        var again = TestHelpers.Unit(EoxDescriptors.CrudeOil, isHot: false, keySuffix: "");

        Assert.Equal(unit.Key, again.Key);
        Assert.Equal("eox:CrudeOil:2026-09-04:20260904185400:2620463", unit.Key);
    }

    /// <summary>
    /// The content stamp is what makes a republished file reload AT ANY AGE, hot or
    /// settled. Losing it would freeze a corrected curve at its original value while
    /// the loader reported clean runs.
    /// </summary>
    [Theory]
    [InlineData(2_620_999, "2026-09-04T18:54:00Z")]   // size changed
    [InlineData(2_620_463, "2026-09-05T09:00:00Z")]   // mtime changed
    public void A_republished_file_gets_a_different_settled_key(long size, string modifiedUtc)
    {
        var original = TestHelpers.Unit(EoxDescriptors.CrudeOil, isHot: false);
        var republished = TestHelpers.Unit(
            EoxDescriptors.CrudeOil,
            size: size,
            modifiedUtc: DateTime.Parse(modifiedUtc).ToUniversalTime(),
            isHot: false);

        Assert.NotEqual(original.Key, republished.Key);
    }

    [Fact]
    public void Feeds_and_dates_never_share_a_key()
    {
        var date = new DateOnly(2026, 9, 4);

        var keys = EoxDescriptors.All
            .SelectMany(f => new[]
            {
                TestHelpers.Unit(f, date).Key,
                TestHelpers.Unit(f, date.AddDays(-1)).Key
            })
            .ToList();

        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// With the shipped 30/30 defaults the settled zone is EMPTY — the oldest date
    /// in the window is exactly 30 days old and <c>age &gt; 30</c> is false. That is
    /// deliberate (ICE 30/30, EvolutionMarkets 30/30, NGI 60/60) and this pins it,
    /// because flipping the comparison is a one-character edit.
    /// </summary>
    [Fact]
    public async Task With_the_shipped_defaults_every_date_in_the_window_is_hot()
    {
        var settings = TestHelpers.Settings();   // DaysBack = SettledAfterDays = 30
        Assert.Equal(30, settings.DaysBack);
        Assert.Equal(30, settings.SettledAfterDays);

        var context = TestHelpers.Context();
        var today = EoxTime.Today(context.StartedAtUtc);
        var ftp = new FakeEoxFtp().WithDates("/", "1430", Enumerable.Range(0, 31).Select(i => today.AddDays(-i)).ToArray());

        var units = await Provider(EoxDescriptors.CrudeOil, ftp, settings).GetWorkUnitsAsync(context);

        Assert.Equal(31, units.Count);
        Assert.All(units, u => Assert.True(u.IsHot));
        Assert.All(units, u => Assert.Contains(":run=", u.Key));
    }

    [Fact]
    public async Task Lowering_SettledAfterDays_creates_a_settled_zone_with_no_run_token()
    {
        var settings = TestHelpers.Settings(s => { s.DaysBack = 5; s.SettledAfterDays = 2; });
        var context = TestHelpers.Context();
        var today = EoxTime.Today(context.StartedAtUtc);
        var ftp = new FakeEoxFtp().WithDates("/", "1430", Enumerable.Range(0, 6).Select(i => today.AddDays(-i)).ToArray());

        var units = await Provider(EoxDescriptors.NaturalGas, ftp, settings).GetWorkUnitsAsync(context);

        Assert.Equal(6, units.Count);
        Assert.Equal(3, units.Count(u => u.IsHot));         // today, -1, -2
        Assert.Equal(3, units.Count(u => !u.IsHot));        // -3, -4, -5

        Assert.All(units.Where(u => u.IsHot), u => Assert.Contains(":run=", u.Key));
        Assert.All(units.Where(u => !u.IsHot), u => Assert.DoesNotContain(":run=", u.Key));
    }

    /// <summary>
    /// A negative SettledAfterDays would settle the whole window, so a republish
    /// that somehow kept both mtime and size would never be re-pulled. Clamped.
    /// </summary>
    [Fact]
    public async Task A_negative_SettledAfterDays_is_clamped_so_today_stays_hot()
    {
        var settings = TestHelpers.Settings(s => { s.DaysBack = 2; s.SettledAfterDays = -5; });
        var context = TestHelpers.Context();
        var today = EoxTime.Today(context.StartedAtUtc);
        var ftp = new FakeEoxFtp().WithDates("/", "1430", today, today.AddDays(-1), today.AddDays(-2));

        var units = await Provider(EoxDescriptors.CrudeOil, ftp, settings).GetWorkUnitsAsync(context);

        Assert.Single(units, u => u.IsHot);
        Assert.Equal(today, units.Single(u => u.IsHot).CurveDate);
    }

    [Theory]
    [InlineData(EoxHotKeyStrategy.RunDate, "20260904")]
    [InlineData(EoxHotKeyStrategy.RunHour, "2026090418")]
    public void The_hot_token_follows_the_configured_strategy_in_UTC(EoxHotKeyStrategy strategy, string expected)
    {
        var settings = TestHelpers.Settings(s => s.HotKeyStrategy = strategy);
        var provider = Provider(EoxDescriptors.CrudeOil, new FakeEoxFtp(), settings);

        Assert.Equal(expected, provider.HotToken(TestHelpers.Context(new DateTime(2026, 9, 4, 18, 30, 0, DateTimeKind.Utc))));
    }

    [Fact]
    public void RunId_strategy_makes_every_invocation_re_pull()
    {
        var settings = TestHelpers.Settings(s => s.HotKeyStrategy = EoxHotKeyStrategy.RunId);
        var provider = Provider(EoxDescriptors.CrudeOil, new FakeEoxFtp(), settings);

        var a = provider.HotToken(TestHelpers.Context(runId: Guid.NewGuid()));
        var b = provider.HotToken(TestHelpers.Context(runId: Guid.NewGuid()));

        Assert.NotEqual(a, b);
    }

    /// <summary>ForceReprocess must defeat the skip for SETTLED dates too, not just hot ones.</summary>
    [Fact]
    public async Task ForceReprocess_salts_every_key_including_settled_ones()
    {
        var settings = TestHelpers.Settings(s =>
        {
            s.DaysBack = 4;
            s.SettledAfterDays = 0;      // everything but today is settled
            s.ForceReprocess = true;
        });

        var context = TestHelpers.Context();
        var today = EoxTime.Today(context.StartedAtUtc);
        var ftp = new FakeEoxFtp().WithDates("/", "1430", Enumerable.Range(0, 5).Select(i => today.AddDays(-i)).ToArray());

        var units = await Provider(EoxDescriptors.CrudeOil, ftp, settings).GetWorkUnitsAsync(context);

        Assert.Equal(5, units.Count);
        Assert.All(units, u => Assert.Contains(":force=", u.Key));
    }

    // ---------------------------------------------------------------- listing cache

    [Fact]
    public async Task A_failed_listing_is_evicted_so_the_next_feed_retries()
    {
        var ftp = new ThrowOnceFtp();
        var cache = new EoxListingCache(ftp);

        await Assert.ThrowsAsync<IOException>(() => cache.ListAsync("/", CancellationToken.None));

        var second = await cache.ListAsync("/", CancellationToken.None);
        Assert.Single(second);
    }

    private sealed class ThrowOnceFtp : IEoxFtp
    {
        private bool _thrown;

        public Task<IReadOnlyList<RemoteFile>> ListAsync(string path, string pattern, CancellationToken ct)
        {
            if (!_thrown)
            {
                _thrown = true;
                throw new IOException("transient");
            }

            return Task.FromResult<IReadOnlyList<RemoteFile>>(new[]
            {
                new RemoteFile("/x.csv", "x.csv", 1, DateTime.UnixEpoch)
            });
        }

        public Task<Stream> OpenReadAsync(string fullPath, CancellationToken ct) => throw new NotSupportedException();
        public Task MoveAsync(string fromPath, string toPath, CancellationToken ct) => throw new NotSupportedException();
    }
}
