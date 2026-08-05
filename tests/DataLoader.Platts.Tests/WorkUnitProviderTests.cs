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

    private static RemoteFile Ftp(string folderPath, string name) =>
        new($"{folderPath}/{name}", name, 1000, new DateTime(2026, 7, 31, 0, 0, 0, DateTimeKind.Utc));

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
}
