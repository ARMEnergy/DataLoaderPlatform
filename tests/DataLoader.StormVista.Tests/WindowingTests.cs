using DataLoader.Core.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DataLoader.StormVista.Tests;

/// <summary>
/// The feed pipeline's date-range windowing (design §6). Driven end-to-end through
/// <see cref="StormVistaFeedPipeline{TUnit,TRow}.ExecuteAsync"/> with a recording
/// enumerator that captures each window and yields no units (so no source/sink/DB is
/// touched). Asserts the windows are contiguous, non-overlapping, gap-free and
/// ordered oldest→newest, and that incremental vs backfill choose the right range.
/// </summary>
public class WindowingTests
{
    private static readonly DateTime StartedAt = new(2026, 8, 6, 0, 0, 0, DateTimeKind.Utc);

    private static LoaderRunContext Context() => new()
    {
        RunId = Guid.NewGuid(),
        StartedAtUtc = StartedAt,
        CancellationToken = CancellationToken.None
    };

    private static async Task<List<(DateOnly Start, DateOnly End)>> RunAndCaptureWindows(StormVistaSettings settings)
    {
        var enumerator = new RecordingEnumerator<DailyWorkUnit>();
        var pipeline = new StormVistaFeedPipeline<DailyWorkUnit, DailyWddRow>(
            "Daily",
            enumerator,
            new NullSource<DailyWorkUnit, DailyWddRow>(),
            new NullSink<DailyWddRow>(),
            new FakeLoadLog(),
            settings,
            NullLoggerFactory.Instance);

        var result = await pipeline.ExecuteAsync(Context());
        Assert.True(result.Success);
        return enumerator.Windows;
    }

    /// <summary>Asserts the invariants every window list must satisfy.</summary>
    private static void AssertContiguousOldestToNewest(
        List<(DateOnly Start, DateOnly End)> windows, DateOnly expectedStart, DateOnly expectedEnd, int chunkDays)
    {
        Assert.NotEmpty(windows);
        Assert.Equal(expectedStart, windows[0].Start);
        Assert.Equal(expectedEnd, windows[^1].End);

        for (var i = 0; i < windows.Count; i++)
        {
            Assert.True(windows[i].Start <= windows[i].End, $"window {i} start after end");
            var lengthDays = windows[i].End.DayNumber - windows[i].Start.DayNumber + 1;
            Assert.True(lengthDays <= chunkDays, $"window {i} length {lengthDays} exceeds chunk {chunkDays}");

            if (i > 0)
            {
                // Contiguous, gap-free, non-overlapping and strictly increasing.
                Assert.Equal(windows[i - 1].End.AddDays(1), windows[i].Start);
                Assert.True(windows[i].Start > windows[i - 1].Start);
            }
        }
    }

    [Fact]
    public async Task Incremental_RangeIsLastDaysBackDays_SingleWindowWhenChunkCoversIt()
    {
        var settings = new StormVistaSettings { Mode = StormVistaMode.Incremental, DaysBack = 21, ChunkDays = 30 };

        var windows = await RunAndCaptureWindows(settings);

        var expectedStart = DateOnly.FromDateTime(StartedAt).AddDays(-21);
        var expectedEnd = DateOnly.FromDateTime(StartedAt);
        AssertContiguousOldestToNewest(windows, expectedStart, expectedEnd, chunkDays: 30);
        Assert.Single(windows); // 22 days fits one 30-day chunk
    }

    [Fact]
    public async Task Incremental_ChunksIntoMultipleContiguousWindows()
    {
        var settings = new StormVistaSettings { Mode = StormVistaMode.Incremental, DaysBack = 21, ChunkDays = 10 };

        var windows = await RunAndCaptureWindows(settings);

        var expectedStart = DateOnly.FromDateTime(StartedAt).AddDays(-21);
        var expectedEnd = DateOnly.FromDateTime(StartedAt);
        AssertContiguousOldestToNewest(windows, expectedStart, expectedEnd, chunkDays: 10);
        Assert.Equal(3, windows.Count); // 22 days / 10 -> 3 windows (10,10,2)
    }

    [Fact]
    public async Task Backfill_WalksBackfillRangeByChunkDays_OldestToNewest()
    {
        var settings = new StormVistaSettings
        {
            Mode = StormVistaMode.Backfill,
            BackfillStart = new DateTime(2024, 1, 1),
            BackfillEnd = new DateTime(2024, 3, 15),
            ChunkDays = 30
        };

        var windows = await RunAndCaptureWindows(settings);

        AssertContiguousOldestToNewest(windows, new DateOnly(2024, 1, 1), new DateOnly(2024, 3, 15), chunkDays: 30);
    }

    [Fact]
    public async Task Backfill_ClampsStartToArchiveFloor()
    {
        // Start before the 2018-07-08 archive floor must be clamped up to it.
        var settings = new StormVistaSettings
        {
            Mode = StormVistaMode.Backfill,
            BackfillStart = new DateTime(2010, 1, 1),
            BackfillEnd = new DateTime(2018, 8, 1),
            ChunkDays = 30
        };

        var windows = await RunAndCaptureWindows(settings);

        Assert.Equal(new DateOnly(2018, 7, 8), windows[0].Start); // clamped, not 2010-01-01
        Assert.Equal(new DateOnly(2018, 8, 1), windows[^1].End);
    }

    [Fact]
    public async Task Backfill_WithoutStart_FailsWithoutTouchingWindows()
    {
        var enumerator = new RecordingEnumerator<DailyWorkUnit>();
        var pipeline = new StormVistaFeedPipeline<DailyWorkUnit, DailyWddRow>(
            "Daily", enumerator,
            new NullSource<DailyWorkUnit, DailyWddRow>(), new NullSink<DailyWddRow>(),
            new FakeLoadLog(),
            new StormVistaSettings { Mode = StormVistaMode.Backfill, BackfillStart = null },
            NullLoggerFactory.Instance);

        var result = await pipeline.ExecuteAsync(Context());

        Assert.False(result.Success);
        Assert.Empty(enumerator.Windows); // never got as far as enumerating
    }
}
