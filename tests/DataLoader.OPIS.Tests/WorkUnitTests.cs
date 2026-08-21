using DataLoader.Core.Abstractions;
using DataLoader.Core.Sources;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DataLoader.OPIS.Tests;

/// <summary>
/// Work-unit enumeration and the resume key — the idempotency contract.
/// <c>core.LoadLog</c> skips a key already recorded successful, so the key alone
/// decides what gets re-pulled and what never runs twice.
/// </summary>
public class WorkUnitTests
{
    private static readonly DateTime Modified = new(2026, 8, 20, 21, 26, 2, DateTimeKind.Utc);

    private static OpisWorkUnit Unit(
        string name = "20260820LP.csv", DateTime? modified = null, long size = 14401) => new()
        {
            File = new RemoteFile("/" + name, name, size, modified ?? Modified),
            SourceFileDate = new DateOnly(2026, 8, 20)
        };

    private static OpisWorkUnitProvider Provider(FakeFtp ftp, OpisSettings? settings = null) =>
        new(ftp, Options.Create(settings ?? new OpisSettings()), NullLogger<OpisWorkUnitProvider>.Instance);

    private static LoaderRunContext Context() => new()
    {
        RunId = Guid.NewGuid(),
        StartedAtUtc = new DateTime(2026, 8, 21, 6, 0, 0, DateTimeKind.Utc),
        CancellationToken = CancellationToken.None
    };

    // ================================================================== resume key

    [Fact]
    public void Key_IsStableForAnUnchangedFile()
    {
        // Same name + timestamp + size => same key => the second run skips the file.
        Assert.Equal(Unit().Key, Unit().Key);
    }

    [Fact]
    public void Key_ChangesWhenOpisRepublishesTheFile()
    {
        // A republished file gets a new last-modified stamp, hence a new key, hence
        // it is reprocessed — which is how a revision reaches the history table.
        var before = Unit();
        var after = Unit(modified: Modified.AddMinutes(3));

        Assert.NotEqual(before.Key, after.Key);
    }

    [Fact]
    public void Key_ChangesWhenOnlyTheSizeChanges()
    {
        // Belt and braces: a same-second rewrite of different length is still new work.
        Assert.NotEqual(Unit().Key, Unit(size: 14999).Key);
    }

    [Fact]
    public void Key_IsDistinctPerFile()
    {
        Assert.NotEqual(Unit("20260819LP.csv").Key, Unit("20260820LP.csv").Key);
    }

    [Fact]
    public void Key_IsNamespacedToThisLoader()
    {
        Assert.StartsWith("opis:", Unit().Key);
    }

    // ================================================================== enumeration

    [Fact]
    public async Task Enumerates_EveryDatedFile_InAscendingDateOrder()
    {
        // Default DaysBack=0 => take everything the drop offers ("all the available
        // files need to be processed"). Ascending order feeds the merge the oldest first.
        var ftp = new FakeFtp("20260820LP.csv", "20260720LP.csv", "20260803LP.csv");
        var units = await Provider(ftp).GetWorkUnitsAsync(Context());

        Assert.Equal(3, units.Count);
        Assert.Equal(
            new[] { new DateOnly(2026, 7, 20), new DateOnly(2026, 8, 3), new DateOnly(2026, 8, 20) },
            units.Select(u => u.SourceFileDate).ToArray());
    }

    [Fact]
    public async Task ParsesThePublicationDateOutOfTheFileName()
    {
        var units = await Provider(new FakeFtp("20260803LP.csv")).GetWorkUnitsAsync(Context());
        Assert.Equal(new DateOnly(2026, 8, 3), Assert.Single(units).SourceFileDate);
    }

    [Fact]
    public async Task SkipsAFileWhoseNameCarriesNoDate()
    {
        // "monthlyLP.csv" passes the *LP.csv glob but carries no yyyyMMdd token. No
        // date means no merge ordering guard, so it is skipped rather than merged
        // where a stale value could win. "README.txt" never passes the glob at all.
        var units = await Provider(new FakeFtp("README.txt", "monthlyLP.csv", "20260820LP.csv"))
            .GetWorkUnitsAsync(Context());

        Assert.Equal("20260820LP.csv", Assert.Single(units).File.Name);
    }

    [Fact]
    public async Task DaysBackZero_TakesEverything()
    {
        var ftp = new FakeFtp("20260720LP.csv", "20260820LP.csv");
        var units = await Provider(ftp, new OpisSettings { DaysBack = 0 }).GetWorkUnitsAsync(Context());
        Assert.Equal(2, units.Count);
    }

    [Fact]
    public async Task DaysBack_FiltersOlderFilesWhenSet()
    {
        // Context starts 2026-08-21; DaysBack=7 keeps 08-20 and drops 07-20.
        // NOTE: the cutoff is measured from UtcNow, so this asserts relative ordering
        // by using dates around a fixed "recent" anchor rather than a hard count.
        var recent = DateTime.UtcNow.Date.AddDays(-2);
        var old = DateTime.UtcNow.Date.AddDays(-40);
        var ftp = new FakeFtp($"{recent:yyyyMMdd}LP.csv", $"{old:yyyyMMdd}LP.csv");

        var units = await Provider(ftp, new OpisSettings { DaysBack = 7 }).GetWorkUnitsAsync(Context());

        Assert.Equal(DateOnly.FromDateTime(recent), Assert.Single(units).SourceFileDate);
    }

    [Fact]
    public async Task RecordsTheNewestDateEnumerated_ForPostLoadValidationScope()
    {
        var provider = Provider(new FakeFtp("20260720LP.csv", "20260820LP.csv"));
        Assert.Null(provider.LastEnumeratedMaxDate);

        await provider.GetWorkUnitsAsync(Context());

        Assert.Equal(new DateOnly(2026, 8, 20), provider.LastEnumeratedMaxDate);
    }

    [Fact]
    public async Task EmptyDrop_YieldsNoUnitsAndNoDate()
    {
        var provider = Provider(new FakeFtp());
        Assert.Empty(await provider.GetWorkUnitsAsync(Context()));
        Assert.Null(provider.LastEnumeratedMaxDate);
    }

    // ================================================================== glob

    [Theory]
    [InlineData("20260820LP.csv", "*LP.csv", true)]
    [InlineData("20260820LP.csv", "*.csv", true)]
    [InlineData("20260820LP.CSV", "*LP.csv", true)]   // case-insensitive
    [InlineData("20260820XX.csv", "*LP.csv", false)]
    [InlineData("notes.txt", "*LP.csv", false)]
    [InlineData("anything", "*", true)]
    public void GlobMatch(string name, string pattern, bool expected) =>
        Assert.Equal(expected, OpisFtpFileSystem.GlobMatch(name, pattern));

    // ================================================================== fake

    /// <summary>Stands in for the FTP drop: returns a listing, never touches a network.</summary>
    private sealed class FakeFtp : IOpisFtp
    {
        private readonly string[] _names;
        public FakeFtp(params string[] names) => _names = names;

        public Task<IReadOnlyList<RemoteFile>> ListAsync(string path, string pattern, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<RemoteFile>>(
                _names.Where(n => OpisFtpFileSystem.GlobMatch(n, pattern))
                      .Select(n => new RemoteFile("/" + n, n, 14401, Modified))
                      .ToList());

        public Task<Stream> OpenReadAsync(string fullPath, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task MoveAsync(string fromPath, string toPath, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
