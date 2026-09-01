using DataLoader.Core.Abstractions;
using DataLoader.Core.Sources;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DataLoader.Argus.Tests;

/// <summary>
/// Work-unit discovery: the file-name regex that is the loader's whole exclusion
/// mechanism, the suffix-to-Module map, and the resume key.
/// </summary>
public sealed class WorkUnitTests
{
    private static LoaderRunContext Context() => new()
    {
        RunId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
        StartedAtUtc = new DateTime(2026, 8, 27, 6, 0, 0, DateTimeKind.Utc),
        CancellationToken = CancellationToken.None
    };

    private static RemoteFile File(string name, long size = 1024, DateTime? modified = null) =>
        new($"/DCRDEUS/{name}", name, size, modified ?? new DateTime(2026, 8, 27, 23, 30, 0, DateTimeKind.Utc));

    private static ArgusTimeSeriesWorkUnitProvider Provider(ArgusSettings settings, FakeArgusFtp ftp) =>
        new(new ArgusListingCache(ftp), settings, NullLogger.Instance);

    // ------------------------------------------------------------------ name matching

    [Theory]
    [InlineData("20260827dhc.csv", true)]
    [InlineData("20260827dhca.csv", true)]
    [InlineData("20260814DHC.CSV", true)]      // case-insensitive
    [InlineData("latestdhc.csv", false)]       // alias of the newest dated file
    [InlineData("previousdhca.csv", false)]    // alias of the second-newest
    [InlineData("7667.csv", false)]            // ad-hoc dump, no date = no ordering guard
    [InlineData("20260827.csv", false)]        // no suffix = no Module
    [InlineData("dhc.csv", false)]
    [InlineData("2026082dhc.csv", false)]      // 7 digits, not 8
    public void The_pattern_accepts_only_dated_module_files(string fileName, bool expected)
    {
        var provider = Provider(TestHelpers.Settings(), new FakeArgusFtp());

        Assert.Equal(expected, provider.TryParseName(fileName) is not null);
    }

    [Fact]
    public void The_pattern_extracts_the_date_and_the_suffix()
    {
        var parsed = Provider(TestHelpers.Settings(), new FakeArgusFtp()).TryParseName("20260827dhca.csv");

        Assert.NotNull(parsed);
        Assert.Equal(new DateOnly(2026, 8, 27), parsed!.Value.Date);
        Assert.Equal("dhca", parsed.Value.Suffix);
    }

    [Fact]
    public void An_impossible_date_in_a_matching_name_is_rejected()
    {
        Assert.Null(Provider(TestHelpers.Settings(), new FakeArgusFtp()).TryParseName("20261332dhc.csv"));
    }

    /// <summary>
    /// The four names that are present on EVERY run and must never become work
    /// units: loading the aliases would merge the same rows twice, and 7667.csv has
    /// no date and therefore no merge ordering guard.
    /// </summary>
    [Fact]
    public async Task The_real_directory_listing_yields_only_the_dated_files()
    {
        var ftp = new FakeArgusFtp().WithDirectory(
            "/DCRDEUS", Samples.TimeSeriesDirectoryNames.Select(n => File(n)).ToArray());

        var units = await Provider(TestHelpers.Settings(), ftp).GetWorkUnitsAsync(Context());

        Assert.Equal(4, units.Count);
        Assert.All(units, u => Assert.Matches(@"^\d{8}dhca?\.csv$", u.File.Name));
    }

    // ------------------------------------------------------------------ module mapping

    /// <summary>
    /// Module is ALWAYS the uppercased suffix. Exact for the two suffixes DCRDEUS
    /// publishes; for a new suffix it may not match latestModules (DAMCOAL's file is
    /// 'dcm'), which usp_ValidateLoad's FactModulesNotInModuleLookup check reports.
    /// </summary>
    [Theory]
    [InlineData("dhc", "DHC")]
    [InlineData("DHC", "DHC")]
    [InlineData("dhca", "DHCA")]
    [InlineData("DhCa", "DHCA")]
    [InlineData("dcm2", "DCM2")]
    public void Module_is_always_the_uppercased_suffix(string suffix, string expected)
    {
        Assert.Equal(expected, ArgusTimeSeriesWorkUnitProvider.ResolveModule(suffix));
    }

    /// <summary>The value that reaches the database is uppercase, whatever the file name's case.</summary>
    [Theory]
    [InlineData("20260827dhc.csv", "DHC")]
    [InlineData("20260827DHCA.csv", "DHCA")]
    [InlineData("20260827DhC.csv", "DHC")]
    public async Task The_stored_module_is_uppercase_for_any_file_name_casing(string fileName, string expected)
    {
        var ftp = new FakeArgusFtp().WithDirectory("/DCRDEUS", File(fileName));

        var unit = Assert.Single(await Provider(TestHelpers.Settings(), ftp).GetWorkUnitsAsync(Context()));

        Assert.Equal(expected, unit.Module);
        Assert.Equal(unit.Module, unit.Module!.ToUpperInvariant());
    }

    /// <summary>
    /// End to end: the Module that lands in the row array — the value the TVP
    /// carries to SQL — is uppercase.
    /// </summary>
    [Fact]
    public void The_module_written_to_the_tvp_is_uppercase()
    {
        var feed = ArgusDescriptors.TimeSeries;
        var unit = TestHelpers.TimeSeriesUnit("20260827dhca.csv", ArgusTimeSeriesWorkUnitProvider.ResolveModule("dhca"));

        var rows = TestHelpers.Reader().Parse(Samples.TimeSeries, unit);

        Assert.All(rows, r => Assert.Equal("DHCA", r.Values[feed.Ordinal("Module")]));
    }

    [Fact]
    public async Task Units_carry_the_resolved_module_and_file_date()
    {
        var ftp = new FakeArgusFtp().WithDirectory("/DCRDEUS", File("20260827dhca.csv"));

        var unit = Assert.Single(await Provider(TestHelpers.Settings(), ftp).GetWorkUnitsAsync(Context()));

        Assert.Equal("DHCA", unit.Module);
        Assert.Equal(new DateOnly(2026, 8, 27), unit.SourceFileDate);
        Assert.Same(ArgusDescriptors.TimeSeries, unit.Feed);
    }

    // ------------------------------------------------------------------ ordering, filtering

    [Fact]
    public async Task Units_come_back_in_ascending_file_date_order()
    {
        var ftp = new FakeArgusFtp().WithDirectory(
            "/DCRDEUS", File("20260827dhc.csv"), File("20260814dhc.csv"), File("20260820dhc.csv"));

        var units = await Provider(TestHelpers.Settings(), ftp).GetWorkUnitsAsync(Context());

        Assert.Equal(
            new[] { new DateOnly(2026, 8, 14), new DateOnly(2026, 8, 20), new DateOnly(2026, 8, 27) },
            units.Select(u => u.SourceFileDate!.Value));
    }

    [Fact]
    public async Task The_newest_enumerated_date_is_exposed_for_validation_scoping()
    {
        var ftp = new FakeArgusFtp().WithDirectory(
            "/DCRDEUS", File("20260814dhc.csv"), File("20260827dhc.csv"));

        var provider = Provider(TestHelpers.Settings(), ftp);
        await provider.GetWorkUnitsAsync(Context());

        Assert.Equal(new DateOnly(2026, 8, 27), provider.LastEnumeratedMaxDate);
    }

    [Fact]
    public async Task The_newest_date_is_null_when_nothing_matched()
    {
        var ftp = new FakeArgusFtp().WithDirectory("/DCRDEUS", File("latestdhc.csv"));

        var provider = Provider(TestHelpers.Settings(), ftp);
        Assert.Empty(await provider.GetWorkUnitsAsync(Context()));
        Assert.Null(provider.LastEnumeratedMaxDate);
    }

    [Fact]
    public async Task DaysBack_zero_takes_every_dated_file()
    {
        var ftp = new FakeArgusFtp().WithDirectory(
            "/DCRDEUS", File("20200101dhc.csv"), File("20260827dhc.csv"));

        var units = await Provider(TestHelpers.Settings(s => s.DaysBack = 0), ftp).GetWorkUnitsAsync(Context());

        Assert.Equal(2, units.Count);
    }

    [Fact]
    public async Task A_positive_DaysBack_filters_out_old_files()
    {
        var recent = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var ftp = new FakeArgusFtp().WithDirectory(
            "/DCRDEUS",
            File("20200101dhc.csv"),
            File($"{recent:yyyyMMdd}dhc.csv"));

        var units = await Provider(TestHelpers.Settings(s => s.DaysBack = 7), ftp).GetWorkUnitsAsync(Context());

        Assert.Single(units);
    }

    // ------------------------------------------------------------------ resume key

    [Fact]
    public void The_key_embeds_folder_name_stamp_and_size()
    {
        var unit = TestHelpers.TimeSeriesUnit();

        Assert.Equal("argus:DCRDEUS:20260827dhc.csv:20260827150000:1024", unit.Key);
    }

    [Fact]
    public void A_republished_file_gets_a_new_key_so_it_is_reprocessed()
    {
        var first = TestHelpers.Unit(ArgusDescriptors.Codes,
            modifiedUtc: new DateTime(2026, 8, 27, 15, 0, 0, DateTimeKind.Utc));
        var republished = TestHelpers.Unit(ArgusDescriptors.Codes,
            modifiedUtc: new DateTime(2026, 8, 28, 15, 0, 0, DateTimeKind.Utc));

        Assert.NotEqual(first.Key, republished.Key);
    }

    [Fact]
    public void A_file_that_changed_size_but_not_stamp_still_gets_a_new_key()
    {
        var first = TestHelpers.Unit(ArgusDescriptors.Codes, size: 1000);
        var grown = TestHelpers.Unit(ArgusDescriptors.Codes, size: 2000);

        Assert.NotEqual(first.Key, grown.Key);
    }

    [Fact]
    public void An_unchanged_file_keeps_its_key_so_the_load_log_can_skip_it()
    {
        Assert.Equal(
            TestHelpers.Unit(ArgusDescriptors.Codes).Key,
            TestHelpers.Unit(ArgusDescriptors.Codes).Key);
    }

    [Fact]
    public void The_same_name_in_two_folders_yields_different_keys()
    {
        var doc = TestHelpers.Unit(ArgusDescriptors.Codes, "same.csv");
        var ts = TestHelpers.Unit(ArgusDescriptors.TimeSeries, "same.csv");

        Assert.NotEqual(doc.Key, ts.Key);
    }

    [Fact]
    public async Task ForceReprocess_salts_the_key_so_the_load_log_cannot_skip()
    {
        var ftp = new FakeArgusFtp().WithDirectory("/DCRDEUS", File("20260827dhc.csv"));

        var plain = Assert.Single(await Provider(TestHelpers.Settings(), ftp).GetWorkUnitsAsync(Context()));
        var forced = Assert.Single(
            await Provider(TestHelpers.Settings(s => s.ForceReprocess = true), ftp).GetWorkUnitsAsync(Context()));

        Assert.NotEqual(plain.Key, forced.Key);
        Assert.Contains("force=", forced.Key);
    }

    // ------------------------------------------------------------------ documentation feeds

    [Fact]
    public async Task A_documentation_feed_yields_exactly_its_own_file()
    {
        var ftp = new FakeArgusFtp().WithDirectory(
            "/DOCUMENTATION",
            new RemoteFile("/DOCUMENTATION/latestCodes.csv", "latestCodes.csv", 100, DateTime.UnixEpoch),
            new RemoteFile("/DOCUMENTATION/latestQuotes.csv", "latestQuotes.csv", 100, DateTime.UnixEpoch));

        var provider = new ArgusDocumentationWorkUnitProvider(
            ArgusDescriptors.Codes, new ArgusListingCache(ftp), new FakeArgusFileLog(),
            TestHelpers.Settings(), NullLogger.Instance);

        var unit = Assert.Single(await provider.GetWorkUnitsAsync(Context()));
        Assert.Equal("latestCodes.csv", unit.File.Name);
        Assert.Null(unit.SourceFileDate);   // reference snapshots carry no date
    }

    /// <summary>
    /// A missing reference file must not fail the run — it is recorded and skipped
    /// so the other 14 feeds still load.
    /// </summary>
    [Fact]
    public async Task A_missing_documentation_file_is_recorded_not_available_and_skipped()
    {
        var ftp = new FakeArgusFtp().WithDirectory("/DOCUMENTATION");
        var fileLog = new FakeArgusFileLog();

        var provider = new ArgusDocumentationWorkUnitProvider(
            ArgusDescriptors.Codes, new ArgusListingCache(ftp), fileLog,
            TestHelpers.Settings(), NullLogger.Instance);

        Assert.Empty(await provider.GetWorkUnitsAsync(Context()));

        var call = Assert.Single(fileLog.Calls);
        Assert.Equal("NotAvailable", call.Status);
        Assert.Equal("latestCodes.csv", call.File.FileName);
        Assert.DoesNotContain("SEE_DB", call.File.RequestPath);
    }

    /// <summary>
    /// 15 reference feeds share one DOCUMENTATION listing. Without the cache the
    /// run would issue 15 identical LIST calls.
    /// </summary>
    [Fact]
    public async Task The_listing_cache_lists_each_directory_once()
    {
        var ftp = new FakeArgusFtp().WithDirectory(
            "/DOCUMENTATION",
            new RemoteFile("/DOCUMENTATION/latestCodes.csv", "latestCodes.csv", 1, DateTime.UnixEpoch));

        var cache = new ArgusListingCache(ftp);

        for (var i = 0; i < 5; i++)
            await cache.ListAsync("/DOCUMENTATION", CancellationToken.None);

        Assert.Equal(1, ftp.ListCallCount);
    }

    [Fact]
    public async Task The_listing_cache_treats_path_spellings_as_one_directory()
    {
        var ftp = new FakeArgusFtp().WithDirectory("/DOCUMENTATION");
        var cache = new ArgusListingCache(ftp);

        await cache.ListAsync("/DOCUMENTATION", CancellationToken.None);
        await cache.ListAsync("DOCUMENTATION/", CancellationToken.None);
        await cache.ListAsync("/documentation", CancellationToken.None);

        Assert.Equal(1, ftp.ListCallCount);
    }
}
