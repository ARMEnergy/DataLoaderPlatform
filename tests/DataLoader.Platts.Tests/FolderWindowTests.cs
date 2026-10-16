using Xunit;

namespace DataLoader.Platts.Tests;

/// <summary>
/// The DaysBack / SettledAfterDays two-zone model over the SymbolData feed's
/// <c>yyyymmdd</c> date folders. These are the rules that decide whether a folder
/// is listed at all, and whether its files are re-read on every run or only when
/// they actually change — so both directions are pinned here, not just the happy
/// one.
/// </summary>
public class FolderWindowTests
{
    private static readonly DateOnly Today = new(2026, 10, 7);

    private static PlattsSettings S(int daysBack = 0, int settledAfterDays = 0, bool reloadAll = false) =>
        new() { DaysBack = daysBack, SettledAfterDays = settledAfterDays, ReloadAllFiles = reloadAll };

    // ---- folder name -> age ----

    [Theory]
    [InlineData("20261007", 0)]
    [InlineData("20261006", 1)]
    [InlineData("20260930", 7)]
    [InlineData("20261008", -1)]   // vendor day ahead of UTC
    public void AgeDays_CountsBackFromTheRunDate(string folder, int expected)
    {
        Assert.Equal(expected, PlattsFolderWindow.AgeDays(folder, Today));
    }

    [Theory]
    [InlineData("20261340")]   // eight digits, not a real date
    [InlineData("20260231")]   // 31 Feb
    [InlineData("notadate")]
    [InlineData("")]
    public void AgeDays_IsNullWhenTheFolderIsNotARealDate(string folder)
    {
        // DateFolderPattern only promises eight digits, so this must not throw.
        Assert.Null(PlattsFolderWindow.AgeDays(folder, Today));
    }

    // ---- DaysBack: the discovery window ----

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void InWindow_NonPositiveDaysBack_MeansNoLimit(int daysBack)
    {
        // The behaviour before this setting existed: every folder on the server.
        Assert.True(PlattsFolderWindow.InWindow(0, daysBack));
        Assert.True(PlattsFolderWindow.InWindow(9999, daysBack));
    }

    [Theory]
    [InlineData(0, true)]    // today
    [InlineData(29, true)]   // oldest day still inside a 30-day window
    [InlineData(30, false)]  // first day outside it
    [InlineData(400, false)]
    public void InWindow_DaysBack30_KeepsTheLast30Days(int ageDays, bool expected)
    {
        Assert.Equal(expected, PlattsFolderWindow.InWindow(ageDays, 30));
    }

    [Fact]
    public void InWindow_FutureDatedFolderIsAlwaysKept()
    {
        // A run just after midnight UTC legitimately sees a folder named for the
        // vendor's "tomorrow"; dropping it would lose a whole day.
        Assert.True(PlattsFolderWindow.InWindow(-1, 1));
        Assert.True(PlattsFolderWindow.InWindow(-1, 30));
    }

    [Fact]
    public void InWindow_UndatableFolderIsAlwaysKept()
    {
        // Can't window what we can't date — keep it rather than silently drop data.
        Assert.True(PlattsFolderWindow.InWindow(null, 1));
        Assert.True(PlattsFolderWindow.InWindow(null, 30));
    }

    // ---- SettledAfterDays: the hot zone ----

    [Theory]
    [InlineData(0, true)]    // today is hot
    [InlineData(1, false)]   // yesterday has settled
    [InlineData(9, false)]
    public void IsHot_SettledAfterDays0_OnlyTodayIsHot(int ageDays, bool expected)
    {
        Assert.Equal(expected, PlattsFolderWindow.IsHot(ageDays, S(settledAfterDays: 0)));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]    // the 00:00 UTC run works on yesterday's folder
    [InlineData(2, false)]
    public void IsHot_SettledAfterDays1_CoversTodayAndYesterday(int ageDays, bool expected)
    {
        Assert.Equal(expected, PlattsFolderWindow.IsHot(ageDays, S(settledAfterDays: 1)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(100)]
    public void IsHot_NegativeSettledAfterDays_LeavesNothingHot(int ageDays)
    {
        // Restores pure "load each file version once" behaviour.
        Assert.False(PlattsFolderWindow.IsHot(ageDays, S(settledAfterDays: -1)));
    }

    [Fact]
    public void IsHot_FutureDatedFolderIsHot()
    {
        // Age -1 is newer than today, so it must fall on the hot side of every
        // non-negative threshold rather than wrapping around to settled.
        Assert.True(PlattsFolderWindow.IsHot(-1, S(settledAfterDays: 0)));
        Assert.True(PlattsFolderWindow.IsHot(-1, S(settledAfterDays: 1)));
    }

    [Fact]
    public void IsHot_UndatableFolderSettles()
    {
        // It still re-reads whenever size/last-modified change; it just isn't
        // force-reloaded every run.
        Assert.False(PlattsFolderWindow.IsHot(null, S(settledAfterDays: 0)));
        Assert.False(PlattsFolderWindow.IsHot(null, S(settledAfterDays: 365)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(500)]
    public void IsHot_ReloadAllFiles_OverridesSettledAfterDays(int ageDays)
    {
        Assert.True(PlattsFolderWindow.IsHot(ageDays, S(settledAfterDays: 0, reloadAll: true)));
        Assert.True(PlattsFolderWindow.IsHot(ageDays, S(settledAfterDays: -1, reloadAll: true)));
    }

    [Fact]
    public void IsHot_ReloadAllFiles_AlsoCoversAnUndatableFolder()
    {
        Assert.True(PlattsFolderWindow.IsHot(null, S(reloadAll: true)));
    }

    // ---- the token the zone produces ----

    [Fact]
    public void RunTokenFor_HotFolderGetsTheRunId_SettledGetsNull()
    {
        var context = new Core.Abstractions.LoaderRunContext
        {
            RunId = Guid.NewGuid(),
            StartedAtUtc = DateTime.UtcNow,
            CancellationToken = CancellationToken.None
        };

        Assert.Equal(context.RunId.ToString("N"),
                     PlattsFolderWindow.RunTokenFor(0, S(settledAfterDays: 0), context));
        Assert.Null(PlattsFolderWindow.RunTokenFor(1, S(settledAfterDays: 0), context));
    }
}
