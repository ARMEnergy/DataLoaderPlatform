using Xunit;
using DataLoader.Core.Abstractions;

namespace DataLoader.CME.Tests;

/// <summary>
/// Resume-key semantics and the discovery-path interpretation.
///
/// <para>
/// These are the two places a file-drop loader silently loses or duplicates data:
/// a key that does not change when the file does (so a revision is never
/// reloaded), a key that changes when it should not (so every run reloads
/// everything), or a path parsed into the wrong trade date.
/// </para>
/// </summary>
public class WorkUnitTests
{
    private static CmeWorkUnit Unit(
        DateOnly tradeDate, string suffix = "", bool hot = false,
        long size = 4741334, DateTime? modified = null) =>
        new()
        {
            Feed = TestHelpers.Feed("STLNYMEX"),
            TradeDate = tradeDate,
            File = TestHelpers.File(
                "BAS_STLNYMEX_STLCPC/EOD_STLNYMEX/2026/09/04/STLNYMEX_20260904.txt",
                "STLNYMEX_20260904.txt",
                size,
                modified ?? new DateTime(2026, 9, 5, 3, 46, 0, DateTimeKind.Utc)),
            IsHot = hot,
            KeySuffix = suffix
        };

    [Fact]
    public void SettledKeyIsTheFeedDateAndContentStamp()
    {
        var unit = Unit(new DateOnly(2026, 9, 4));

        Assert.Equal("cme:EOD_STLNYMEX:2026-09-04:20260905034600:4741334", unit.Key);
    }

    /// <summary>
    /// The content stamp is what makes a republished bulletin reload at ANY age.
    /// CME revises settlement files in place (PRE-CLEARING then POST-CLEARING), so
    /// this is the mechanism that catches the revision.
    /// </summary>
    [Fact]
    public void ADifferentStampIsADifferentKey()
    {
        var first = Unit(new DateOnly(2026, 9, 4));
        var resized = Unit(new DateOnly(2026, 9, 4), size: 4741335);
        var retimed = Unit(new DateOnly(2026, 9, 4), modified: new DateTime(2026, 9, 5, 4, 0, 0, DateTimeKind.Utc));

        Assert.NotEqual(first.Key, resized.Key);
        Assert.NotEqual(first.Key, retimed.Key);
    }

    [Fact]
    public void AnUnchangedFileKeepsTheSameSettledKey()
    {
        Assert.Equal(Unit(new DateOnly(2026, 9, 4)).Key, Unit(new DateOnly(2026, 9, 4)).Key);
    }

    [Fact]
    public void HotSuffixMakesTheKeyVaryPerRun()
    {
        var hot = Unit(new DateOnly(2026, 9, 4), ":run=20260907", hot: true);

        Assert.EndsWith(":run=20260907", hot.Key);
        Assert.NotEqual(Unit(new DateOnly(2026, 9, 4)).Key, hot.Key);
    }

    [Fact]
    public void KeysAreDistinctPerFeedAndPerDate()
    {
        var a = Unit(new DateOnly(2026, 9, 4));
        var b = Unit(new DateOnly(2026, 9, 3));

        Assert.NotEqual(a.Key, b.Key);

        var otherFeed = new CmeWorkUnit
        {
            Feed = TestHelpers.Feed("STLCPC"),
            TradeDate = new DateOnly(2026, 9, 4),
            File = a.File
        };

        Assert.NotEqual(a.Key, otherFeed.Key);
    }

    /// <summary>
    /// The hot token must be UTC. <c>01:00</c> Central happens twice on a
    /// fall-back night, so a local-zone hour token would repeat, the key would go
    /// backwards, and an already-recorded success would suppress a legitimate
    /// re-pull for an hour.
    /// </summary>
    [Theory]
    [InlineData(CmeHotKeyStrategy.RunDate, "20260907")]
    [InlineData(CmeHotKeyStrategy.RunHour, "2026090713")]
    public void HotTokenUsesUtcNotLocalTime(CmeHotKeyStrategy strategy, string expected)
    {
        var provider = new CmeWorkUnitProvider(
            discovery: null!,
            new CmeSettings { HotKeyStrategy = strategy },
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        var context = new LoaderRunContext
        {
            RunId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
            StartedAtUtc = new DateTime(2026, 9, 7, 13, 42, 17, DateTimeKind.Utc),
            CancellationToken = CancellationToken.None
        };

        Assert.Equal(expected, provider.HotToken(context));
    }

    [Fact]
    public void RunIdStrategyVariesEveryRun()
    {
        var provider = new CmeWorkUnitProvider(
            discovery: null!,
            new CmeSettings { HotKeyStrategy = CmeHotKeyStrategy.RunId },
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        static LoaderRunContext Ctx(Guid id) => new()
        {
            RunId = id,
            StartedAtUtc = new DateTime(2026, 9, 7, 13, 0, 0, DateTimeKind.Utc),
            CancellationToken = CancellationToken.None
        };

        Assert.NotEqual(provider.HotToken(Ctx(Guid.NewGuid())), provider.HotToken(Ctx(Guid.NewGuid())));
    }

    [Fact]
    public void DisplayNameIdentifiesFeedDateAndFile()
    {
        Assert.Equal(
            "CME EOD_STLNYMEX 2026-09-04 (STLNYMEX_20260904.txt)",
            Unit(new DateOnly(2026, 9, 4)).DisplayName);
    }

    // ---------------------------------------------------------------------
    // File-name date parsing
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("STLAGS_20260904.txt", 2026, 9, 4)]
    [InlineData("STLNYMEX_20260824.txt", 2026, 8, 24)]
    [InlineData("STLCOMEX_20261231.txt", 2026, 12, 31)]
    public void FileNameDateIsParsed(string fileName, int year, int month, int day)
    {
        Assert.True(CmeDiscoveryCache.TryParseFileDate(fileName, out var date));
        Assert.Equal(new DateOnly(year, month, day), date);
    }

    [Theory]
    [InlineData("STLAGS.txt")]          // no date token
    [InlineData("STLAGS_2026090.txt")]  // 7 digits
    [InlineData("STLAGS_20261332.txt")] // not a real date
    [InlineData("STLAGS_abcdefgh.txt")] // not digits
    public void BadFileNamesAreRejected(string fileName)
    {
        Assert.False(CmeDiscoveryCache.TryParseFileDate(fileName, out _));
    }

    [Fact]
    public void TreeDepthMatchesTheDropLayout()
    {
        // product / feed / yyyy / MM / dd — five directory levels, files below.
        Assert.Equal(5, CmeDiscoveryCache.TreeDepth);
    }
}

/// <summary>
/// The SFTP path rules.
///
/// <para>
/// ⚠ This server accepts ONLY paths relative to the login directory: listing
/// <c>./BAS_STLAGS</c> works and listing <c>/BAS_STLAGS</c> returns "no such
/// file" — even though SSH.NET's own directory entries report exactly that
/// absolute form. Feeding an entry's FullName back to the server is therefore the
/// bug that breaks this loader, and it fails on the SECOND directory level rather
/// than the first, so it would survive a shallow smoke test. These tests pin the
/// normalisation that prevents it.
/// </para>
/// </summary>
public class SftpPathTests
{
    [Theory]
    [InlineData("/BAS_STLAGS", "BAS_STLAGS")]
    [InlineData("/BAS_STLAGS/", "BAS_STLAGS")]
    [InlineData("BAS_STLAGS/", "BAS_STLAGS")]
    [InlineData("//BAS_STLAGS//", "BAS_STLAGS")]
    [InlineData("/", ".")]
    [InlineData("", ".")]
    [InlineData("   ", ".")]
    [InlineData(".", ".")]
    [InlineData("/a/b/c", "a/b/c")]
    [InlineData("a\\b", "a/b")]
    public void NormalizeStripsLeadingSlashesAndTrailingSeparators(string input, string expected)
    {
        Assert.Equal(expected, CmeSftpFileSystem.Normalize(input));
    }

    [Theory]
    [InlineData(".", "BAS_STLAGS", "BAS_STLAGS")]
    [InlineData("BAS_STLAGS", "EOD_STLAGS", "BAS_STLAGS/EOD_STLAGS")]
    [InlineData("/BAS_STLAGS", "EOD_STLAGS", "BAS_STLAGS/EOD_STLAGS")]
    [InlineData("a/b/c/d", "e.txt", "a/b/c/d/e.txt")]
    public void CombineBuildsRelativeChildPaths(string parent, string child, string expected)
    {
        Assert.Equal(expected, CmeSftpFileSystem.Combine(parent, child));
    }

    /// <summary>A combined path must never come back absolute — that is the form the server rejects.</summary>
    [Fact]
    public void CombineNeverProducesAnAbsolutePath()
    {
        var combined = CmeSftpFileSystem.Combine("/BAS_STLAGS", "/EOD_STLAGS");

        Assert.Equal("BAS_STLAGS/EOD_STLAGS", combined);
        Assert.False(combined.StartsWith('/'));
    }
}
