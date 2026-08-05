using DataLoader.Core.Sources;
using Xunit;

namespace DataLoader.Platts.Tests;

/// <summary>
/// Group C (spec §5.1 / §6.1) — the work-unit keys that drive core.LoadLog
/// idempotency. The key embeds Size and LastModifiedUtc so an unchanged file is
/// skipped and a changed one is reprocessed.
/// </summary>
public class WorkUnitKeyTests
{
    private static readonly DateTime T1 = new(2026, 7, 31, 23, 45, 0, DateTimeKind.Utc);
    private static readonly DateTime T2 = new(2026, 8, 1, 1, 15, 0, DateTimeKind.Utc);

    private static SymbolDataWorkUnit SdUnit(RemoteFile file) => new() { File = file, Folder = "20260731" };
    private static SymbolWorkUnit SymUnit(RemoteFile file) => new() { File = file };

    [Fact]
    public void SymbolDataKey_HasExpectedShape()
    {
        var unit = SdUnit(new RemoteFile("/20260731/market.ftp", "market.ftp", 4096, T1));
        Assert.Equal($"platts:sd:20260731/market.ftp:4096:{T1:O}", unit.Key);
        Assert.Equal("20260731/market.ftp", unit.DisplayName);
    }

    [Fact]
    public void SymbolDataKey_IdenticalMetadata_ProducesIdenticalKeys()
    {
        var a = SdUnit(new RemoteFile("/20260731/market.ftp", "market.ftp", 4096, T1));
        var b = SdUnit(new RemoteFile("/20260731/market.ftp", "market.ftp", 4096, T1));
        Assert.Equal(a.Key, b.Key);
    }

    [Fact]
    public void SymbolDataKey_ChangesWhenLastModifiedChanges()
    {
        var a = SdUnit(new RemoteFile("/20260731/market.ftp", "market.ftp", 4096, T1));
        var b = SdUnit(new RemoteFile("/20260731/market.ftp", "market.ftp", 4096, T2));
        Assert.NotEqual(a.Key, b.Key);
    }

    [Fact]
    public void SymbolDataKey_ChangesWhenSizeChanges()
    {
        var a = SdUnit(new RemoteFile("/20260731/market.ftp", "market.ftp", 4096, T1));
        var b = SdUnit(new RemoteFile("/20260731/market.ftp", "market.ftp", 8192, T1));
        Assert.NotEqual(a.Key, b.Key);
    }

    [Fact]
    public void SymbolKey_HasExpectedShape()
    {
        var unit = SymUnit(new RemoteFile("symbols/csv-version/AA_sym.csv", "AA_sym.csv", 1024, T1));
        Assert.Equal($"platts:sym:AA_sym.csv:1024:{T1:O}", unit.Key);
        Assert.Equal("AA_sym.csv", unit.DisplayName);
    }

    [Fact]
    public void SymbolKey_IdenticalMetadata_ProducesIdenticalKeys()
    {
        var a = SymUnit(new RemoteFile("symbols/csv-version/AA_sym.csv", "AA_sym.csv", 1024, T1));
        var b = SymUnit(new RemoteFile("symbols/csv-version/AA_sym.csv", "AA_sym.csv", 1024, T1));
        Assert.Equal(a.Key, b.Key);
    }

    [Fact]
    public void SymbolKey_ChangesWhenLastModifiedChanges()
    {
        var a = SymUnit(new RemoteFile("symbols/csv-version/AA_sym.csv", "AA_sym.csv", 1024, T1));
        var b = SymUnit(new RemoteFile("symbols/csv-version/AA_sym.csv", "AA_sym.csv", 1024, T2));
        Assert.NotEqual(a.Key, b.Key);
    }
}
