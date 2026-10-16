using DataLoader.Core.Abstractions;
using DataLoader.Core.Sources;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DataLoader.Platts.Tests;

/// <summary>
/// Group D (spec §5.1 / §6.1) — the work-unit providers, fed by an in-memory
/// <see cref="FakePlattsSftp"/>. No live SFTP.
/// </summary>
public class WorkUnitProviderTests
{
    private static LoaderRunContext Context() => new()
    {
        RunId = Guid.NewGuid(),
        StartedAtUtc = DateTime.UtcNow,
        CancellationToken = CancellationToken.None
    };

    /// <summary>A run anchored to a fixed UTC date, so window maths is deterministic.</summary>
    private static LoaderRunContext ContextOn(int year, int month, int day) => new()
    {
        RunId = Guid.NewGuid(),
        StartedAtUtc = new DateTime(year, month, day, 12, 0, 0, DateTimeKind.Utc),
        CancellationToken = CancellationToken.None
    };

    private static RemoteFile Ftp(string folderPath, string name) =>
        new($"{folderPath}/{name}", name, 1000, new DateTime(2026, 7, 31, 0, 0, 0, DateTimeKind.Utc));

    /// <summary>One .ftp file in each of the named date folders.</summary>
    private static FakePlattsSftp DatedSftp(params string[] folders)
    {
        var sftp = new FakePlattsSftp { Directories = folders.Select(f => "/" + f).ToArray() };
        foreach (var folder in folders)
            sftp.Listings["/" + folder] = new[] { Ftp("/" + folder, "a.ftp") };
        return sftp;
    }

    private static FakePlattsSftp TwoFolderSftp()
    {
        var sftp = new FakePlattsSftp { Directories = new[] { "/20260731", "/20260730" } };
        sftp.Listings["/20260731"] = new[] { Ftp("/20260731", "a.ftp"), Ftp("/20260731", "b.ftp") };
        sftp.Listings["/20260730"] = new[] { Ftp("/20260730", "c.ftp") };
        return sftp;
    }

    private static FakePlattsSftp SymbolSftp()
    {
        var sftp = new FakePlattsSftp();
        sftp.Listings["symbols/csv-version"] = new[]
        {
            new RemoteFile("symbols/csv-version/AA_sym.csv", "AA_sym.csv", 1,
                           new DateTime(2026, 7, 31, 0, 0, 0, DateTimeKind.Utc))
        };
        return sftp;
    }

    [Fact]
    public async Task SymbolDataProvider_YieldsOneUnitPerFile_WithCorrectFolder()
    {
        var sftp = new FakePlattsSftp
        {
            Directories = new[] { "/20260731", "/20260730" }
        };
        sftp.Listings["/20260731"] = new[] { Ftp("/20260731", "a.ftp"), Ftp("/20260731", "b.ftp") };
        sftp.Listings["/20260730"] = new[] { Ftp("/20260730", "c.ftp") };

        var settings = Options.Create(new PlattsSettings
        {
            RootDirectory = "/",
            MarketDataFilePattern = "*.ftp"
        });
        var provider = new SymbolDataWorkUnitProvider(sftp, settings, NullLogger<SymbolDataWorkUnitProvider>.Instance);

        var units = await provider.GetWorkUnitsAsync(Context());

        Assert.Equal(3, units.Count);
        Assert.Equal(2, units.Count(u => u.Folder == "20260731"));
        Assert.Equal(1, units.Count(u => u.Folder == "20260730"));
        // Folder is the last path segment of the date folder, files carried through.
        Assert.Contains(units, u => u.Folder == "20260731" && u.File.Name == "a.ftp");
        Assert.Contains(units, u => u.Folder == "20260731" && u.File.Name == "b.ftp");
        Assert.Contains(units, u => u.Folder == "20260730" && u.File.Name == "c.ftp");
    }

    [Fact]
    public async Task SymbolDataProvider_NoFolders_YieldsNoUnits()
    {
        var sftp = new FakePlattsSftp { Directories = Array.Empty<string>() };
        var settings = Options.Create(new PlattsSettings { RootDirectory = "/" });
        var provider = new SymbolDataWorkUnitProvider(sftp, settings, NullLogger<SymbolDataWorkUnitProvider>.Instance);

        var units = await provider.GetWorkUnitsAsync(Context());

        Assert.Empty(units);
    }

    [Fact]
    public async Task SymbolProvider_YieldsOneUnitPerCsv()
    {
        var sftp = new FakePlattsSftp();
        sftp.Listings["symbols/csv-version"] = new[]
        {
            new RemoteFile("symbols/csv-version/AA_sym.csv", "AA_sym.csv", 1, DateTime.UtcNow),
            new RemoteFile("symbols/csv-version/AC_sym.csv", "AC_sym.csv", 1, DateTime.UtcNow)
        };

        var settings = Options.Create(new PlattsSettings
        {
            SymbolsDirectory = "symbols/csv-version",
            SymbolFilePattern = "*.csv"
        });
        var provider = new SymbolWorkUnitProvider(sftp, settings, NullLogger<SymbolWorkUnitProvider>.Instance);

        var units = await provider.GetWorkUnitsAsync(Context());

        Assert.Equal(2, units.Count);
        Assert.Equal(new[] { "AA_sym.csv", "AC_sym.csv" }, units.Select(u => u.File.Name).ToArray());
    }

    // ---- ReloadAllFiles: when on, no file may ever be skipped ----

    [Fact]
    public async Task SymbolDataProvider_ReloadAllFilesOff_KeysAreStableAcrossRuns()
    {
        var settings = Options.Create(new PlattsSettings { RootDirectory = "/", ReloadAllFiles = false });
        var provider = new SymbolDataWorkUnitProvider(
            TwoFolderSftp(), settings, NullLogger<SymbolDataWorkUnitProvider>.Instance);

        var run1 = await provider.GetWorkUnitsAsync(Context());
        var run2 = await provider.GetWorkUnitsAsync(Context());

        // Same files, different run ids -> identical keys, so core.LoadLog skips them.
        Assert.Equal(run1.Select(u => u.Key).ToArray(), run2.Select(u => u.Key).ToArray());
        Assert.All(run1, u => Assert.Null(u.RunToken));
        Assert.All(run1, u => Assert.DoesNotContain(":run=", u.Key));
    }

    [Fact]
    public async Task SymbolDataProvider_ReloadAllFilesOn_EveryKeyIsNewEveryRun()
    {
        var settings = Options.Create(new PlattsSettings { RootDirectory = "/", ReloadAllFiles = true });
        var provider = new SymbolDataWorkUnitProvider(
            TwoFolderSftp(), settings, NullLogger<SymbolDataWorkUnitProvider>.Instance);

        var context1 = Context();
        var run1 = await provider.GetWorkUnitsAsync(context1);
        var run2 = await provider.GetWorkUnitsAsync(Context());

        Assert.Equal(3, run1.Count);
        Assert.Equal(3, run2.Count);
        // Not one key survives into the next run -> nothing can be short-circuited.
        Assert.Empty(run1.Select(u => u.Key).Intersect(run2.Select(u => u.Key)));
        Assert.All(run1, u => Assert.EndsWith(":run=" + context1.RunId.ToString("N"), u.Key));
        // The shared token must not blur the files together.
        Assert.Equal(3, run1.Select(u => u.Key).Distinct().Count());
    }

    [Fact]
    public async Task SymbolProvider_ReloadAllFilesOn_EveryKeyIsNewEveryRun()
    {
        var settings = Options.Create(new PlattsSettings
        {
            SymbolsDirectory = "symbols/csv-version",
            ReloadAllFiles = true
        });
        var provider = new SymbolWorkUnitProvider(
            SymbolSftp(), settings, NullLogger<SymbolWorkUnitProvider>.Instance);

        var context1 = Context();
        var run1 = await provider.GetWorkUnitsAsync(context1);
        var run2 = await provider.GetWorkUnitsAsync(Context());

        Assert.Equal(context1.RunId.ToString("N"), run1[0].RunToken);
        Assert.EndsWith(":run=" + context1.RunId.ToString("N"), run1[0].Key);
        Assert.NotEqual(run1[0].Key, run2[0].Key);
    }

    [Fact]
    public async Task SymbolProvider_ReloadAllFilesOff_KeysAreStableAcrossRuns()
    {
        var settings = Options.Create(new PlattsSettings { SymbolsDirectory = "symbols/csv-version" });
        var provider = new SymbolWorkUnitProvider(
            SymbolSftp(), settings, NullLogger<SymbolWorkUnitProvider>.Instance);

        var run1 = await provider.GetWorkUnitsAsync(Context());
        var run2 = await provider.GetWorkUnitsAsync(Context());

        Assert.Equal(run1[0].Key, run2[0].Key);
        Assert.Null(run1[0].RunToken);
    }

    // ---- DaysBack / SettledAfterDays over the date folders ----

    private static SymbolDataWorkUnitProvider Provider(FakePlattsSftp sftp, PlattsSettings settings) =>
        new(sftp, Options.Create(settings), NullLogger<SymbolDataWorkUnitProvider>.Instance);

    [Fact]
    public async Task DaysBack_ExcludesOldFolders_WithoutEvenListingThem()
    {
        var sftp = DatedSftp("20261007", "20261006", "20261001", "20260901");
        var provider = Provider(sftp, new PlattsSettings { RootDirectory = "/", DaysBack = 7 });

        var units = await provider.GetWorkUnitsAsync(ContextOn(2026, 10, 7));

        // Ages 0 and 1 are in; 6 days back (20261001) is in; 36 days back is out.
        Assert.Equal(new[] { "20261007", "20261006", "20261001" }, units.Select(u => u.Folder).ToArray());
        // The excluded folder must cost no SFTP round-trip at all.
        Assert.DoesNotContain("/20260901", sftp.ListedPaths);
        Assert.Equal(3, sftp.ListedPaths.Count);
    }

    [Fact]
    public async Task DaysBack_Zero_KeepsEveryFolder()
    {
        var sftp = DatedSftp("20261007", "20200101");
        var provider = Provider(sftp, new PlattsSettings { RootDirectory = "/", DaysBack = 0 });

        var units = await provider.GetWorkUnitsAsync(ContextOn(2026, 10, 7));

        Assert.Equal(2, units.Count);
        Assert.Equal(2, sftp.ListedPaths.Count);
    }

    [Fact]
    public async Task DaysBack_KeepsAFutureDatedFolder()
    {
        // A run just after midnight UTC sees the vendor's "tomorrow" folder.
        var sftp = DatedSftp("20261008", "20261007");
        var provider = Provider(sftp, new PlattsSettings { RootDirectory = "/", DaysBack = 1 });

        var units = await provider.GetWorkUnitsAsync(ContextOn(2026, 10, 7));

        Assert.Equal(new[] { "20261008", "20261007" }, units.Select(u => u.Folder).ToArray());
    }

    [Fact]
    public async Task SettledAfterDays_SplitsHotFromSettledWithinOneRun()
    {
        var sftp = DatedSftp("20261007", "20261006", "20261005", "20261004");
        var provider = Provider(sftp, new PlattsSettings { RootDirectory = "/", SettledAfterDays = 1 });

        var context = ContextOn(2026, 10, 7);
        var units = await provider.GetWorkUnitsAsync(context);
        var token = ":run=" + context.RunId.ToString("N");

        // Ages 0 and 1 are hot, 2 and 3 have settled.
        Assert.EndsWith(token, units.Single(u => u.Folder == "20261007").Key);
        Assert.EndsWith(token, units.Single(u => u.Folder == "20261006").Key);
        Assert.DoesNotContain(":run=", units.Single(u => u.Folder == "20261005").Key);
        Assert.DoesNotContain(":run=", units.Single(u => u.Folder == "20261004").Key);
    }

    [Fact]
    public async Task SettledFolderKeepsTheSameKeyAcrossRuns_HotFolderDoesNot()
    {
        var provider = Provider(DatedSftp("20261007", "20261001"),
                                new PlattsSettings { RootDirectory = "/", SettledAfterDays = 0 });

        var run1 = await provider.GetWorkUnitsAsync(ContextOn(2026, 10, 7));
        var run2 = await provider.GetWorkUnitsAsync(ContextOn(2026, 10, 7));

        string Key(IReadOnlyList<SymbolDataWorkUnit> us, string folder) => us.Single(u => u.Folder == folder).Key;

        // Settled -> the LoadLog skips it. Hot -> it is re-read every run.
        Assert.Equal(Key(run1, "20261001"), Key(run2, "20261001"));
        Assert.NotEqual(Key(run1, "20261007"), Key(run2, "20261007"));
    }

    [Fact]
    public async Task UndatableFolderIsKeptAndSettled()
    {
        // DateFolderPattern only promises eight digits, so 20261340 can reach here.
        var sftp = DatedSftp("20261340", "20261007");
        var provider = Provider(sftp, new PlattsSettings { RootDirectory = "/", DaysBack = 2, SettledAfterDays = 1 });

        var run1 = await provider.GetWorkUnitsAsync(ContextOn(2026, 10, 7));
        var run2 = await provider.GetWorkUnitsAsync(ContextOn(2026, 10, 7));

        var odd1 = run1.Single(u => u.Folder == "20261340");
        var odd2 = run2.Single(u => u.Folder == "20261340");
        Assert.Null(odd1.RunToken);          // settled, not force-reloaded
        Assert.Equal(odd1.Key, odd2.Key);    // but still present, never dropped
    }

    [Fact]
    public async Task ReloadAllFiles_MakesEveryFolderInTheWindowHot_IncludingSettledOnes()
    {
        var sftp = DatedSftp("20261007", "20260101");
        var provider = Provider(sftp, new PlattsSettings
        {
            RootDirectory = "/",
            SettledAfterDays = 0,
            ReloadAllFiles = true
        });

        var context = ContextOn(2026, 10, 7);
        var units = await provider.GetWorkUnitsAsync(context);

        Assert.All(units, u => Assert.EndsWith(":run=" + context.RunId.ToString("N"), u.Key));
    }

    [Fact]
    public async Task ReloadAllFiles_DoesNotWidenTheDaysBackWindow()
    {
        // ReloadAllFiles governs re-reading, not discovery.
        var sftp = DatedSftp("20261007", "20260101");
        var provider = Provider(sftp, new PlattsSettings
        {
            RootDirectory = "/",
            DaysBack = 2,
            ReloadAllFiles = true
        });

        var units = await provider.GetWorkUnitsAsync(ContextOn(2026, 10, 7));

        Assert.Equal(new[] { "20261007" }, units.Select(u => u.Folder).ToArray());
        Assert.DoesNotContain("/20260101", sftp.ListedPaths);
    }

    [Fact]
    public async Task NegativeSettledAfterDays_LeavesNothingHot()
    {
        var provider = Provider(DatedSftp("20261007"),
                                new PlattsSettings { RootDirectory = "/", SettledAfterDays = -1 });

        var run1 = await provider.GetWorkUnitsAsync(ContextOn(2026, 10, 7));
        var run2 = await provider.GetWorkUnitsAsync(ContextOn(2026, 10, 7));

        Assert.Null(run1[0].RunToken);
        Assert.Equal(run1[0].Key, run2[0].Key);
    }
}
