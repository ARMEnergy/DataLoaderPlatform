using DataLoader.Core.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DataLoader.ModernCommodities.Tests;

/// <summary>
/// *** THE SECOND-MOST VALUABLE TEST IN THIS PROJECT: the resumability rule. ***
///
/// <para>A work unit is skipped <b>only</b> when <c>core.LoadLog</c> shows that exact key
/// SUCCESSFULLY COMPLETED. A unit that is missing from the log, or whose previous attempt FAILED or
/// never completed, <b>is processed</b>. Everything below drives the real
/// <see cref="ModComPipeline{TRow}"/> over the real <see cref="ModComWorkUnitProvider"/> with a
/// scripted load log, so the keys under test are the ones the loader actually emits.</para>
///
/// <para>Also pinned here: a failure in one block is logged and does <b>not</b> abort the run
/// (fail-a-block-not-the-run), and the success path reports the right
/// <c>RecordsProcessed</c>.</para>
/// </summary>
public class PipelineTests
{
    private static readonly Guid RunA = Guid.Parse("aaaaaaaa-1111-2222-3333-444444444444");
    private static readonly Guid RunB = Guid.Parse("bbbbbbbb-1111-2222-3333-444444444444");

    private static LoaderRunContext Context(DateTime startedAtUtc, Guid runId) => new()
    {
        RunId = runId,
        StartedAtUtc = startedAtUtc,
        CancellationToken = CancellationToken.None
    };

    private static DateTime Utc(int y, int mo, int d, int h) => new(y, mo, d, h, 0, 0, DateTimeKind.Utc);

    private static TradeRow Trade(int tradeNumber) => new() { TradeNumber = tradeNumber, State = "Finalized" };

    private sealed record Harness(
        ModComPipeline<TradeRow> Pipeline,
        ScriptedLoadLog LoadLog,
        ScriptedSourceReader<TradeRow> Source,
        RecordingSink<TradeRow> Sink,
        IReadOnlyList<ModComWorkUnit> Units);

    /// <summary>
    /// Builds a pipeline over the REAL provider (so the keys are the real ones) with a scripted log,
    /// reader and sink. <paramref name="rowsPerUnit"/> defaults to two rows per unit.
    /// </summary>
    private static Harness Build(
        LoaderRunContext context,
        int chunkDays = 10,
        Func<ModComWorkUnit, IReadOnlyList<TradeRow>>? rowsPerUnit = null,
        ScriptedLoadLog? loadLog = null)
    {
        var settings = ReaderHarness.Settings(chunkDays: chunkDays);
        settings.MaxConcurrentWorkUnits = 2;

        var provider = new ModComWorkUnitProvider(ModComDescriptors.AllTrades, settings, NullLogger.Instance);
        var units = provider.GetWorkUnitsAsync(context).GetAwaiter().GetResult();

        var source = new ScriptedSourceReader<TradeRow>(
            rowsPerUnit ?? (_ => new[] { Trade(1), Trade(2) }));
        var sink = new RecordingSink<TradeRow>();
        var log = loadLog ?? new ScriptedLoadLog();

        var pipeline = new ModComPipeline<TradeRow>(
            ModComEndpoints.AllTrades, provider, source, sink, log, settings, NullLogger.Instance);

        return new Harness(pipeline, log, source, sink, units);
    }

    // ============================================================ the resumability rule

    [Fact]
    public async Task AUnitTheLogRecordsAsSUCCESSFUL_IsSkipped_AndIsNeverRead()
    {
        var context = Context(Utc(2026, 8, 24, 13), RunA);
        var harness = Build(context);
        var alreadyDone = harness.Units[0].Key;
        harness.LoadLog.AlreadySucceeded.Add(alreadyDone);

        var result = await harness.Pipeline.ExecuteAsync(context);

        Assert.Equal(harness.Units.Count, result.WorkUnitsTotal);
        Assert.Equal(1, result.WorkUnitsSkipped);
        Assert.Equal(harness.Units.Count - 1, result.WorkUnitsSucceeded);
        Assert.True(result.Success);

        Assert.DoesNotContain(alreadyDone, harness.Source.ReadKeys);     // never read
        Assert.DoesNotContain(alreadyDone, harness.LoadLog.Begun);
        Assert.Contains(alreadyDone, harness.LoadLog.Skipped);
    }

    [Fact]
    public async Task AUnitMISSINGFromTheLog_IsProcessed()
    {
        var context = Context(Utc(2026, 8, 24, 13), RunA);
        var harness = Build(context);      // the log is empty: nothing has ever succeeded

        var result = await harness.Pipeline.ExecuteAsync(context);

        Assert.Equal(harness.Units.Count, result.WorkUnitsSucceeded);
        Assert.Equal(0, result.WorkUnitsSkipped);
        Assert.Equal(harness.Units.Count, harness.Source.ReadKeys.Count);
        Assert.Equal(harness.Units.Select(u => u.Key).OrderBy(k => k),
            harness.LoadLog.Begun.OrderBy(k => k));
    }

    [Fact]
    public async Task AUnitWhosePreviousAttemptFAILED_IsProcessedAgain_NotSkipped()
    {
        // *** The half of the rule that a naive "have I seen this key?" implementation gets wrong. ***
        var context = Context(Utc(2026, 8, 24, 13), RunA);
        var harness = Build(context);
        var previouslyFailed = harness.Units[1].Key;
        harness.LoadLog.PreviouslyFailed.Add(previouslyFailed);   // recorded, but NOT as a success

        var result = await harness.Pipeline.ExecuteAsync(context);

        Assert.Equal(0, result.WorkUnitsSkipped);
        Assert.Contains(previouslyFailed, harness.Source.ReadKeys);
        Assert.Contains(previouslyFailed, harness.LoadLog.Begun);
        Assert.Contains(harness.LoadLog.Succeeded, s => s.Key == previouslyFailed);
    }

    [Fact]
    public async Task OnlyTheSucceededKeysAreSkipped_TheRestOfTheWindowStillLoads()
    {
        var context = Context(Utc(2026, 8, 24, 13), RunA);
        var harness = Build(context);
        Assert.Equal(4, harness.Units.Count);                     // 31 days / ChunkDays 10

        harness.LoadLog.AlreadySucceeded.Add(harness.Units[0].Key);
        harness.LoadLog.AlreadySucceeded.Add(harness.Units[2].Key);
        harness.LoadLog.PreviouslyFailed.Add(harness.Units[1].Key);

        var result = await harness.Pipeline.ExecuteAsync(context);

        Assert.Equal(2, result.WorkUnitsSkipped);
        Assert.Equal(2, result.WorkUnitsSucceeded);
        Assert.Equal(
            new[] { harness.Units[1].Key, harness.Units[3].Key }.OrderBy(k => k),
            harness.Source.ReadKeys.OrderBy(k => k));
    }

    [Fact]
    public async Task ASecondRunInTheSAMEUtcHour_SkipsEverythingItAlreadyLoaded()
    {
        var context = Context(Utc(2026, 8, 24, 13), RunA);
        var loadLog = new ScriptedLoadLog();

        var first = Build(context, loadLog: loadLog);
        var firstResult = await first.Pipeline.ExecuteAsync(context);
        Assert.Equal(first.Units.Count, firstResult.WorkUnitsSucceeded);

        // Everything the first run completed is now recorded as successful...
        foreach (var (key, _) in loadLog.Succeeded) loadLog.AlreadySucceeded.Add(key);

        // ...so a re-run inside the SAME clock hour (a different RunId) does nothing at all.
        var sameHour = Context(Utc(2026, 8, 24, 13), RunB);
        var second = Build(sameHour, loadLog: loadLog);
        var secondResult = await second.Pipeline.ExecuteAsync(sameHour);

        Assert.Equal(second.Units.Count, secondResult.WorkUnitsSkipped);
        Assert.Equal(0, secondResult.WorkUnitsSucceeded);
        Assert.Empty(second.Source.ReadKeys);
        Assert.Empty(second.Sink.Batches);
        Assert.True(secondResult.Success);
    }

    [Fact]
    public async Task TheNEXTUtcHour_RePullsTheWholeWindow_BecauseThereIsNoSettledZone()
    {
        // *** This is the behaviour the whole loader is built around: nothing in this feed is ever
        // final (a trade can be cancelled or restated months later), so every window is re-pulled on
        // the next clock hour regardless of the previous outcome. ***
        var hour13 = Context(Utc(2026, 8, 24, 13), RunA);
        var loadLog = new ScriptedLoadLog();

        var first = Build(hour13, loadLog: loadLog);
        await first.Pipeline.ExecuteAsync(hour13);
        foreach (var (key, _) in loadLog.Succeeded) loadLog.AlreadySucceeded.Add(key);

        var hour14 = Context(Utc(2026, 8, 24, 14), RunB);
        var second = Build(hour14, loadLog: loadLog);
        var result = await second.Pipeline.ExecuteAsync(hour14);

        Assert.Equal(second.Units.Count, result.WorkUnitsSucceeded);
        Assert.Equal(0, result.WorkUnitsSkipped);                       // NOT ONE unit skipped
        Assert.Equal(second.Units.Count, second.Source.ReadKeys.Count);

        // The windows are literally the same dates; only the run token differs.
        Assert.Equal(first.Units.Select(u => (u.WindowStart, u.WindowEnd)),
            second.Units.Select(u => (u.WindowStart, u.WindowEnd)));
        Assert.Empty(first.Units.Select(u => u.Key).Intersect(second.Units.Select(u => u.Key)));
    }

    // ============================================================ counters and the sink

    [Fact]
    public async Task TheSuccessPathReportsTheRealRecordCount_AndWritesEveryRowToTheSink()
    {
        var context = Context(Utc(2026, 8, 24, 13), RunA);
        var harness = Build(context, rowsPerUnit: unit => Enumerable
            .Range(0, unit.WindowEnd.Day % 3 + 1)
            .Select(i => Trade(unit.WindowStart.DayNumber * 10 + i))
            .ToArray());

        var result = await harness.Pipeline.ExecuteAsync(context);

        Assert.True(result.Success);
        Assert.Equal(harness.Sink.TotalRows, result.RecordsProcessed);
        Assert.Equal(harness.Units.Count, harness.Sink.Batches.Count);

        // core.LoadLog got the same per-unit counts the sink saw.
        Assert.Equal(result.RecordsProcessed, harness.LoadLog.Succeeded.Sum(s => s.Records));
    }

    [Fact]
    public async Task AUnitThatReadsZeroRows_StillSucceeds()
    {
        // A header-only 200 is a legitimate empty read: zero rows, unit SUCCEEDS.
        var context = Context(Utc(2026, 8, 24, 13), RunA);
        var harness = Build(context, rowsPerUnit: _ => Array.Empty<TradeRow>());

        var result = await harness.Pipeline.ExecuteAsync(context);

        Assert.True(result.Success);
        Assert.Equal(harness.Units.Count, result.WorkUnitsSucceeded);
        Assert.Equal(0, result.RecordsProcessed);
        Assert.All(harness.LoadLog.Succeeded, s => Assert.Equal(0, s.Records));
    }

    // ============================================================ fail a block, not the run

    [Fact]
    public async Task AFailureInOneBlock_IsLoggedAsAFailure_AndTheOtherBlocksStillLoad()
    {
        var context = Context(Utc(2026, 8, 24, 13), RunA);
        var doomedWindowStart = new DateOnly(2026, 8, 4);   // the second chunk at ChunkDays = 10

        var harness = Build(context, rowsPerUnit: unit =>
            unit.WindowStart == doomedWindowStart
                ? throw new ModComRequestException(
                    ModComFailureKind.RowCap, ModComEndpoints.AllTrades, 400, "row cap hit on this chunk")
                : new[] { Trade(unit.WindowStart.DayNumber) });

        var result = await harness.Pipeline.ExecuteAsync(context);

        Assert.False(result.Success);                          // the run reports the failure...
        Assert.Equal(1, result.WorkUnitsFailed);
        Assert.Equal(harness.Units.Count - 1, result.WorkUnitsSucceeded);   // ...but did not abort
        Assert.Equal(harness.Units.Count - 1, result.RecordsProcessed);

        var doomedKey = harness.Units.Single(u => u.WindowStart == doomedWindowStart).Key;
        var failure = Assert.Single(harness.LoadLog.Failed);
        Assert.Equal(doomedKey, failure.Key);
        Assert.Contains("row cap", failure.Error);

        // The failed unit is NOT recorded as a success, so the next run retries exactly it.
        Assert.DoesNotContain(doomedKey, harness.LoadLog.Succeeded.Select(s => s.Key));
    }

    [Fact]
    public async Task EveryBlockFailing_FailsTheRunWithoutThrowing()
    {
        var context = Context(Utc(2026, 8, 24, 13), RunA);
        var harness = Build(context, rowsPerUnit: _ => throw new InvalidOperationException("boom"));

        var result = await harness.Pipeline.ExecuteAsync(context);

        Assert.False(result.Success);
        Assert.Equal(harness.Units.Count, result.WorkUnitsFailed);
        Assert.Equal(0, result.WorkUnitsSucceeded);
        Assert.Equal(harness.Units.Count, harness.LoadLog.Failed.Count);
        Assert.Empty(harness.Sink.Batches);
    }

    [Fact]
    public async Task AFailedBlockIsRetriedByTheNextRunInTheSameHour_BecauseOnlySuccessesSkip()
    {
        var context = Context(Utc(2026, 8, 24, 13), RunA);
        var loadLog = new ScriptedLoadLog();
        var doomed = new DateOnly(2026, 8, 4);

        var first = Build(context, rowsPerUnit: unit => unit.WindowStart == doomed
            ? throw new InvalidOperationException("transient")
            : new[] { Trade(1) }, loadLog: loadLog);
        var firstResult = await first.Pipeline.ExecuteAsync(context);
        Assert.Equal(1, firstResult.WorkUnitsFailed);

        foreach (var (key, _) in loadLog.Succeeded) loadLog.AlreadySucceeded.Add(key);

        // Same hour, same keys: the three successes skip and the ONE failure is retried.
        var second = Build(context, loadLog: loadLog);
        var secondResult = await second.Pipeline.ExecuteAsync(context);

        Assert.Equal(3, secondResult.WorkUnitsSkipped);
        Assert.Equal(1, secondResult.WorkUnitsSucceeded);
        var retried = Assert.Single(second.Source.ReadKeys);
        Assert.Equal(second.Units.Single(u => u.WindowStart == doomed).Key, retried);
    }

    // ============================================================ degenerate cases

    [Fact]
    public async Task NoWorkUnits_IsAReportedSuccessWithNoReads()
    {
        var context = Context(Utc(2026, 8, 24, 13), RunA);
        var settings = ReaderHarness.Settings();
        var source = new ScriptedSourceReader<TradeRow>(_ => new[] { Trade(1) });
        var sink = new RecordingSink<TradeRow>();
        var loadLog = new ScriptedLoadLog();

        var pipeline = new ModComPipeline<TradeRow>(
            ModComEndpoints.AllTrades, new FixedWorkUnitProvider(), source, sink, loadLog, settings,
            NullLogger.Instance);

        var result = await pipeline.ExecuteAsync(context);

        Assert.True(result.Success);
        Assert.Equal(0, result.WorkUnitsTotal);
        Assert.Empty(source.ReadKeys);
        Assert.Empty(sink.Batches);
    }

    [Fact]
    public void ThePipelineExposesItsEndpointIdForTheModuleToggle()
    {
        var context = Context(Utc(2026, 8, 24, 13), RunA);
        var harness = Build(context);

        Assert.Equal(ModComEndpoints.AllTrades, ((IModComPipeline)harness.Pipeline).EndpointId);
    }
}
