using DataLoader.Core.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DataLoader.EvolutionMarkets.Tests;

/// <summary>
/// Pins the pipeline's use of <c>core.LoadLog</c> — the idempotency contract that makes the daily
/// full-window re-pull safe to over-schedule (design §11).
///
/// <para>The rule under test: <b>a unit is skipped if and only if its key already has a SUCCESSFULLY
/// COMPLETED load-log row.</b> A key that never ran, or whose previous attempt FAILED, must be
/// processed. Getting this wrong in either direction is silently costly — skip too much and prices
/// are lost, skip too little and nothing is idempotent.</para>
/// </summary>
public class PipelineTests
{
    private static LoaderRunContext Context() => new()
    {
        RunId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
        StartedAtUtc = new DateTime(2026, 8, 25, 18, 0, 0, DateTimeKind.Utc),
        CancellationToken = CancellationToken.None
    };

    private static MarketDataRow Row(Guid? id = null) => new()
    {
        MarketDataId = id ?? Guid.NewGuid(),
        BusinessDate = new DateOnly(2026, 8, 24),
        Ask = 1m,
        Checksum = 7
    };

    private static EvoPipeline<EvoMarketDataWorkUnit, MarketDataRow> Build(
        IWorkUnitProvider<EvoMarketDataWorkUnit> provider,
        ISourceReader<EvoMarketDataWorkUnit, MarketDataRow> source,
        ISink<MarketDataRow> sink,
        ILoadLogRepository loadLog,
        EvoSettings? settings = null) =>
        new(EvoEndpoints.MarketDataHistory, provider, source, sink, loadLog,
            settings ?? ReaderHarness.Settings(), NullLogger.Instance);

    [Fact]
    public async Task AllUnitsFresh_AllAreProcessed()
    {
        var units = new[]
        {
            ReaderHarness.Unit(new DateOnly(2026, 8, 24)),
            ReaderHarness.Unit(new DateOnly(2026, 8, 23))
        };

        var loadLog = new ScriptedLoadLog();
        var sink = new RecordingSink();
        var pipeline = Build(new FixedWorkUnitProvider(units),
            new ScriptedSourceReader(_ => new[] { Row() }), sink, loadLog);

        var result = await pipeline.ExecuteAsync(Context());

        Assert.True(result.Success);
        Assert.Equal(2, result.WorkUnitsTotal);
        Assert.Equal(2, result.WorkUnitsSucceeded);
        Assert.Equal(0, result.WorkUnitsSkipped);
        Assert.Equal(2, sink.TotalRows);
    }

    [Fact]
    public async Task AUnitWithACompletedLoadLogRow_IsSkipped()
    {
        var unit = ReaderHarness.Unit(new DateOnly(2026, 8, 24));
        var loadLog = new ScriptedLoadLog();
        loadLog.AlreadySucceeded.Add(unit.Key);

        var source = new ScriptedSourceReader(_ => new[] { Row() });
        var sink = new RecordingSink();
        var pipeline = Build(new FixedWorkUnitProvider(unit), source, sink, loadLog);

        var result = await pipeline.ExecuteAsync(Context());

        Assert.True(result.Success);
        Assert.Equal(1, result.WorkUnitsSkipped);
        Assert.Equal(0, result.WorkUnitsSucceeded);
        Assert.Empty(source.ReadKeys);          // the HTTP read never happened
        Assert.Equal(0, sink.TotalRows);
    }

    [Fact]
    public async Task AHotKeyDiffersBetweenRuns_SoTheSameDateIsRePulled()
    {
        // The whole point of the all-hot default: yesterday's key is not today's key, so a revision
        // published overnight is picked up.
        var date = new DateOnly(2026, 8, 24);
        var yesterday = ReaderHarness.Unit(date, hot: "20260824");
        var today = ReaderHarness.Unit(date, hot: "20260825");

        Assert.NotEqual(yesterday.Key, today.Key);

        var loadLog = new ScriptedLoadLog();
        loadLog.AlreadySucceeded.Add(yesterday.Key);   // yesterday's run completed

        var source = new ScriptedSourceReader(_ => new[] { Row() });
        var pipeline = Build(new FixedWorkUnitProvider(today), source, new RecordingSink(), loadLog);

        var result = await pipeline.ExecuteAsync(Context());

        Assert.Equal(1, result.WorkUnitsSucceeded);
        Assert.Equal(0, result.WorkUnitsSkipped);
        Assert.Contains(today.Key, source.ReadKeys);
    }

    [Fact]
    public async Task ASettledKeyIsStable_SoASecondRunSkipsIt()
    {
        var date = new DateOnly(2026, 7, 1);
        var settledKey = EvoMarketDataWorkUnitProvider.BuildKey(date, settled: true, "IGNORED");
        var unit = new EvoMarketDataWorkUnit
        {
            BusinessDate = date,
            RequestPath = EvoMarketDataWorkUnitProvider.BuildRequestPath(date, null),
            KeyValue = settledKey
        };

        var loadLog = new ScriptedLoadLog();
        loadLog.AlreadySucceeded.Add(settledKey);

        var source = new ScriptedSourceReader(_ => new[] { Row() });
        var pipeline = Build(new FixedWorkUnitProvider(unit), source, new RecordingSink(), loadLog);

        var result = await pipeline.ExecuteAsync(Context());

        Assert.Equal(1, result.WorkUnitsSkipped);
        Assert.Empty(source.ReadKeys);
    }

    [Fact]
    public async Task APreviouslyFailedUnit_IsRetried()
    {
        // core.usp_CompleteLoadLog closes a FAILED attempt with IsComplete = 0, so it does not skip.
        var unit = ReaderHarness.Unit(new DateOnly(2026, 8, 24));
        var loadLog = new ScriptedLoadLog();   // deliberately NOT in AlreadySucceeded

        var source = new ScriptedSourceReader(_ => new[] { Row() });
        var pipeline = Build(new FixedWorkUnitProvider(unit), source, new RecordingSink(), loadLog);

        var result = await pipeline.ExecuteAsync(Context());

        Assert.Equal(1, result.WorkUnitsSucceeded);
        Assert.Contains(unit.Key, source.ReadKeys);
    }

    [Fact]
    public async Task OneFailingUnit_FailsThatUnitOnly_AndTheRunContinues()
    {
        // "Fail a unit, not the run": a single bad date must not abandon the other 29.
        var good = ReaderHarness.Unit(new DateOnly(2026, 8, 24));
        var bad = ReaderHarness.Unit(new DateOnly(2026, 8, 23));

        var loadLog = new ScriptedLoadLog();
        var sink = new RecordingSink();
        var source = new ScriptedSourceReader(u =>
            u.Key == bad.Key ? throw new HttpRequestException("boom") : new[] { Row() });

        var pipeline = Build(new FixedWorkUnitProvider(good, bad), source, sink, loadLog);
        var result = await pipeline.ExecuteAsync(Context());

        Assert.False(result.Success);
        Assert.Equal(1, result.WorkUnitsSucceeded);
        Assert.Equal(1, result.WorkUnitsFailed);
        Assert.Equal(1, sink.TotalRows);                                     // the good unit still wrote
        Assert.Contains(loadLog.Failed, f => f.Key == bad.Key);
    }

    [Fact]
    public async Task AnEmptyRead_CompletesTheUnitSuccessfullyWithZeroRecords()
    {
        // A weekend. The unit SUCCEEDS with 0 records — and that recorded success is precisely what
        // makes the settled-zone hazard real (design §3.5), which is why the default keeps everything
        // hot.
        var unit = ReaderHarness.Unit(new DateOnly(2026, 8, 22));   // a Saturday
        var loadLog = new ScriptedLoadLog();
        var sink = new RecordingSink();

        var pipeline = Build(new FixedWorkUnitProvider(unit),
            new ScriptedSourceReader(_ => Array.Empty<MarketDataRow>()), sink, loadLog);

        var result = await pipeline.ExecuteAsync(Context());

        Assert.True(result.Success);
        Assert.Equal(1, result.WorkUnitsSucceeded);
        Assert.Equal(0, result.RecordsProcessed);
        Assert.Contains(loadLog.Succeeded, s => s.Key == unit.Key && s.Records == 0);
    }

    [Fact]
    public async Task NoWorkUnits_IsAWarningAndASuccess()
    {
        var pipeline = Build(new FixedWorkUnitProvider(),
            new ScriptedSourceReader(_ => Array.Empty<MarketDataRow>()), new RecordingSink(),
            new ScriptedLoadLog());

        var result = await pipeline.ExecuteAsync(Context());

        Assert.True(result.Success);
        Assert.Equal(0, result.WorkUnitsTotal);
    }

    [Fact]
    public void Pipeline_ReportsItsEndpointId()
    {
        var pipeline = Build(new FixedWorkUnitProvider(),
            new ScriptedSourceReader(_ => Array.Empty<MarketDataRow>()), new RecordingSink(),
            new ScriptedLoadLog());

        Assert.Equal(EvoEndpoints.MarketDataHistory, pipeline.EndpointId);
        Assert.Equal("MarketDataHistory", pipeline.EndpointId);
    }
}
