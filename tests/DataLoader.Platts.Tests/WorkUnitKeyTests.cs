using DataLoader.Core.Abstractions;
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

    // ---- ReloadAllFiles: the run token that defeats the LoadLog skip ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NoRunToken_LeavesTheStableKeyUntouched(string? token)
    {
        var file = new RemoteFile("/20260731/market.ftp", "market.ftp", 4096, T1);
        var plain = new SymbolDataWorkUnit { File = file, Folder = "20260731" };
        var tokened = new SymbolDataWorkUnit { File = file, Folder = "20260731", RunToken = token };

        // An absent token must not alter the settled key, or switching the flag back
        // off would orphan every key written while it was on.
        Assert.Equal(plain.Key, tokened.Key);
        Assert.DoesNotContain(":run=", tokened.Key);
    }

    [Fact]
    public void SymbolDataKey_WithRunToken_DiffersPerRun_SoNothingIsEverSkipped()
    {
        var file = new RemoteFile("/20260731/market.ftp", "market.ftp", 4096, T1);
        var run1 = new SymbolDataWorkUnit { File = file, Folder = "20260731", RunToken = "aaaaaaaa" };
        var run2 = new SymbolDataWorkUnit { File = file, Folder = "20260731", RunToken = "bbbbbbbb" };

        Assert.NotEqual(run1.Key, run2.Key);
        Assert.StartsWith($"platts:sd:20260731/market.ftp:4096:{T1:O}", run1.Key);
        Assert.EndsWith(":run=aaaaaaaa", run1.Key);
        // DisplayName stays clean so the log stays readable.
        Assert.Equal("20260731/market.ftp", run1.DisplayName);
    }

    [Fact]
    public void SymbolKey_WithRunToken_DiffersPerRun_SoNothingIsEverSkipped()
    {
        var file = new RemoteFile("symbols/csv-version/AA_sym.csv", "AA_sym.csv", 1024, T1);
        var run1 = new SymbolWorkUnit { File = file, RunToken = "aaaaaaaa" };
        var run2 = new SymbolWorkUnit { File = file, RunToken = "bbbbbbbb" };

        Assert.NotEqual(run1.Key, run2.Key);
        Assert.Equal($"platts:sym:AA_sym.csv:1024:{T1:O}:run=aaaaaaaa", run1.Key);
        Assert.Equal("AA_sym.csv", run1.DisplayName);
    }

    [Fact]
    public void RunToken_IsNullUnlessReloadAllFilesIsSet()
    {
        var context = new LoaderRunContext
        {
            RunId = Guid.NewGuid(),
            StartedAtUtc = DateTime.UtcNow,
            CancellationToken = CancellationToken.None
        };

        Assert.Null(PlattsKey.RunToken(new PlattsSettings(), context));
        Assert.Null(PlattsKey.RunToken(new PlattsSettings { ReloadAllFiles = false }, context));
        Assert.Equal(context.RunId.ToString("N"),
                     PlattsKey.RunToken(new PlattsSettings { ReloadAllFiles = true }, context));
    }
}
