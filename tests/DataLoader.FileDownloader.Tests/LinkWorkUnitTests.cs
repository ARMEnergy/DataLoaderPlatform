using DataLoader.Core.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DataLoader.FileDownloader.Tests;

/// <summary>
/// URL templating and Link-mode enumeration. Hand-written fake rather than a mocking
/// library, matching the rest of this repo's suites.
/// </summary>
public sealed class LinkWorkUnitTests
{
    // ------------------------------------------------------------------ UrlTemplate

    [Fact]
    public void Build_substitutes_semicolon_delimited_arguments()
    {
        Assert.Equal(
            "https://v.example/2026/11/report.csv",
            UrlTemplate.Build("https://v.example/{0}/{1}/report.csv", "2026;11", "S"));
    }

    [Fact]
    public void Build_accepts_a_constant_url_with_no_arguments()
    {
        // Several configured sources have a fixed URL and store Value = ''.
        Assert.Equal("https://v.example/latest.xlsx",
            UrlTemplate.Build("https://v.example/latest.xlsx", "", "S"));
        Assert.Equal("https://v.example/latest.xlsx",
            UrlTemplate.Build("https://v.example/latest.xlsx", null, "S"));
    }

    [Fact]
    public void Build_keeps_an_empty_middle_argument()
    {
        // ⚠ Position is what the template binds to. Dropping the blank would shift
        // every later argument up one and silently produce a wrong-but-valid URL.
        Assert.Equal("https://v.example/a//c",
            UrlTemplate.Build("https://v.example/{0}/{1}/{2}", "a;;c", "S"));
    }

    [Fact]
    public void Build_names_the_source_when_template_and_arguments_disagree()
    {
        // string.Format's own message never says which of 17 sources is at fault.
        var ex = Assert.Throws<InvalidOperationException>(
            () => UrlTemplate.Build("https://v.example/{0}/{1}", "only-one", "Quantum CI"));

        Assert.Contains("Quantum CI", ex.Message);
    }

    [Fact]
    public void Build_rejects_a_blank_base_url()
    {
        Assert.Throws<InvalidOperationException>(() => UrlTemplate.Build("  ", "x", "S"));
    }

    // ------------------------------------------------------------------ provider

    private sealed class FakeCatalog : IFileDownloaderCatalog
    {
        public List<FileDownloaderInfo> Sources { get; } = new();
        public Dictionary<int, List<FileDownloaderQueueRow>> Queues { get; } = new();
        public List<(int InfoId, int RowId, bool NotFound)> Stamped { get; } = new();

        public Task<IReadOnlyList<FileDownloaderInfo>> GetActiveSourcesAsync(CancellationToken _) =>
            Task.FromResult<IReadOnlyList<FileDownloaderInfo>>(Sources);

        public Task<IReadOnlyList<FileDownloaderQueueRow>> GetQueueAsync(int infoId, CancellationToken _) =>
            Task.FromResult<IReadOnlyList<FileDownloaderQueueRow>>(
                Queues.TryGetValue(infoId, out var q) ? q : new List<FileDownloaderQueueRow>());

        public Task StampAsync(int infoId, int rowId, bool isNotFound, CancellationToken _)
        {
            Stamped.Add((infoId, rowId, isNotFound));
            return Task.CompletedTask;
        }
    }

    private static LinkWorkUnitProvider Provider(FakeCatalog catalog, params string[] modes) =>
        new(catalog,
            Options.Create(new FileDownloaderSettings
            {
                ConnectionString = "unused",
                EnabledModes = modes.Length == 0 ? new List<string> { "Link" } : modes.ToList()
            }),
            NullLogger<LinkWorkUnitProvider>.Instance);

    private static LoaderRunContext Context() => new()
    {
        RunId = Guid.NewGuid(),
        StartedAtUtc = DateTime.UtcNow,
        CancellationToken = CancellationToken.None
    };

    [Fact]
    public async Task Returns_no_units_when_Link_is_not_an_enabled_mode()
    {
        var catalog = new FakeCatalog();
        catalog.Sources.Add(new FileDownloaderInfo(1, "S", "https://v/{0}", @"\\x\y", "Doc_"));
        catalog.Queues[1] = new() { new FileDownloaderQueueRow(10, "a", "f.xlsx") };

        var units = await Provider(catalog, "Sftp", "Ftp").GetWorkUnitsAsync(Context());

        Assert.Empty(units);
    }

    [Fact]
    public async Task Builds_one_unit_per_queue_row_across_every_active_source()
    {
        var catalog = new FakeCatalog();
        catalog.Sources.Add(new FileDownloaderInfo(3, "Transco", "https://v/{0}.xlsx", @"\\x\t", "Transco_"));
        catalog.Sources.Add(new FileDownloaderInfo(8, "Rover", "https://v/r/{0}.xlsx", @"\\x\r", "Rover_"));
        catalog.Queues[3] = new()
        {
            new FileDownloaderQueueRow(100, "20261104", "Transco_20261104.xlsx"),
            new FileDownloaderQueueRow(101, "20261105", "Transco_20261105.xlsx")
        };
        catalog.Queues[8] = new() { new FileDownloaderQueueRow(200, "20261104", "Rover_20261104.xlsx") };

        var units = await Provider(catalog).GetWorkUnitsAsync(Context());

        Assert.Equal(3, units.Count);
        Assert.All(units, u => Assert.Equal(FileAcquisitionMode.Link, u.Mode));
        Assert.Equal(3, units.Select(u => u.Key).Distinct().Count());

        var first = units.Single(u => u.FileName == "Transco_20261104.xlsx");
        Assert.Equal("https://v/20261104.xlsx", first.RemoteLocator);
        Assert.Equal(@"\\x\t", first.DestinationPath);
    }

    [Fact]
    public async Task Carries_InfoId_and_ListRowId_so_the_row_can_be_stamped()
    {
        // Without ListRowId there is no way to drain the queue, and the view would
        // re-offer the same row on every run forever.
        var catalog = new FakeCatalog();
        catalog.Sources.Add(new FileDownloaderInfo(9, "NW Natural", "https://v/{0}", @"\\x\n", "NW_"));
        catalog.Queues[9] = new() { new FileDownloaderQueueRow(555, "20261104", "NW_20261104.xlsx") };

        var unit = Assert.Single(await Provider(catalog).GetWorkUnitsAsync(Context()));

        Assert.Equal(9, unit.InfoId);
        Assert.Equal(555, unit.ListRowId);
    }

    [Fact]
    public async Task A_bad_template_skips_only_that_row()
    {
        // ⚠ THE behaviour that separates this from the standalone app, whose single
        // process-wide try/catch lets one bad source end the whole batch.
        var catalog = new FakeCatalog();
        catalog.Sources.Add(new FileDownloaderInfo(1, "Broken", "https://v/{0}/{1}", @"\\x\b", "B_"));
        catalog.Sources.Add(new FileDownloaderInfo(2, "Healthy", "https://v/{0}.csv", @"\\x\h", "H_"));
        catalog.Queues[1] = new()
        {
            new FileDownloaderQueueRow(10, "only-one", "B_1.xlsx"),   // too few args -> skipped
            new FileDownloaderQueueRow(11, "a;b", "B_2.xlsx")         // fine -> kept
        };
        catalog.Queues[2] = new() { new FileDownloaderQueueRow(20, "x", "H_1.csv") };

        var units = await Provider(catalog).GetWorkUnitsAsync(Context());

        Assert.Equal(2, units.Count);
        Assert.DoesNotContain(units, u => u.FileName == "B_1.xlsx");
        Assert.Contains(units, u => u.FileName == "B_2.xlsx");   // same source still runs
        Assert.Contains(units, u => u.FileName == "H_1.csv");    // other source untouched
    }

    [Fact]
    public async Task Link_units_never_carry_a_FileDate()
    {
        // The queue views cannot supply one; see FileDownloadUnit.Key.
        var catalog = new FakeCatalog();
        catalog.Sources.Add(new FileDownloaderInfo(3, "Transco", "https://v/{0}", @"\\x\t", "T_"));
        catalog.Queues[3] = new() { new FileDownloaderQueueRow(1, "20261104", "T_20261104.xlsx") };

        var unit = Assert.Single(await Provider(catalog).GetWorkUnitsAsync(Context()));

        Assert.Null(unit.FileDate);
        Assert.DoesNotContain("date=", unit.Key);
    }

    [Fact]
    public async Task No_active_sources_yields_no_units_rather_than_throwing()
    {
        Assert.Empty(await Provider(new FakeCatalog()).GetWorkUnitsAsync(Context()));
    }
}
