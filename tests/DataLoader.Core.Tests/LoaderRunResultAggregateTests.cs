using DataLoader.Core.Abstractions;
using Xunit;

namespace DataLoader.Core.Tests;

/// <summary>
/// <see cref="LoaderRunResult.Aggregate"/> exists because every loader that runs
/// several sub-pipelines (one per endpoint/feed/table) has to combine their
/// results into the single <see cref="LoaderRunResult"/> <c>ILoaderModule.RunAsync</c>
/// returns, and a hand-rolled version of that merge is exactly how
/// <see cref="LoaderRunResult.FailedUnits"/> went missing from a real AGSI
/// failure alert in production while <c>WorkUnitsFailed</c> correctly said 4 —
/// the merge summed the counters but was written before <c>FailedUnits</c>
/// existed, so it never carried the list. These tests pin the contract so the
/// next field added to <see cref="LoaderRunResult"/> cannot be dropped the same
/// way, with <see cref="Aggregate_CarriesFailedUnitsThroughFromEveryPart"/>
/// pinning the actual bug directly.
/// </summary>
public class LoaderRunResultAggregateTests
{
    private static LoaderRunResult Ok(int total = 10, int succeeded = 10, int skipped = 0, int records = 100, TimeSpan? duration = null) => new()
    {
        LoaderId = "Test", Success = true,
        WorkUnitsTotal = total, WorkUnitsSucceeded = succeeded, WorkUnitsSkipped = skipped,
        RecordsProcessed = records, Duration = duration ?? TimeSpan.Zero
    };

    private static LoaderRunResult Bad(
        int total, int failed, string? errorMessage = null, IReadOnlyList<FailedWorkUnit>? failedUnits = null,
        TimeSpan? duration = null, int records = 0) => new()
    {
        LoaderId = "Test", Success = false,
        WorkUnitsTotal = total, WorkUnitsSucceeded = total - failed, WorkUnitsFailed = failed,
        ErrorMessage = errorMessage, FailedUnits = failedUnits ?? Array.Empty<FailedWorkUnit>(),
        Duration = duration ?? TimeSpan.Zero, RecordsProcessed = records
    };

    private static FailedWorkUnit Unit(int i) => new(
        Key: $"unit={i}", DisplayName: $"Unit {i}", ErrorText: $"error {i}",
        StackTrace: $"System.InvalidOperationException: error {i}\n   at DataLoader.Vendor.Reader.Read{i}()");

    // ------------------------------------------------------------- counters

    [Fact]
    public void SumsTheFourUnitCountersAndRecordsProcessed()
    {
        var parts = new[]
        {
            Ok(total: 10, succeeded: 9, skipped: 1, records: 200),
            Bad(total: 5, failed: 2, records: 0) // succeeded = 3
        };

        var result = LoaderRunResult.Aggregate("AGSI", parts, TimeSpan.FromSeconds(30));

        Assert.Equal(15, result.WorkUnitsTotal);
        Assert.Equal(12, result.WorkUnitsSucceeded); // 9 + 3
        Assert.Equal(1, result.WorkUnitsSkipped);
        Assert.Equal(2, result.WorkUnitsFailed);
        Assert.Equal(200, result.RecordsProcessed);
    }

    [Fact]
    public void RecordsProcessedSums_EvenWhenABadPartContributedRecords()
    {
        var parts = new[] { Ok(records: 100), Bad(total: 1, failed: 1, records: 50) };

        Assert.Equal(150, LoaderRunResult.Aggregate("Test", parts, TimeSpan.Zero).RecordsProcessed);
    }

    // -------------------------------------------------------------- success

    [Fact]
    public void SuccessfulOnlyWhenEveryPartSucceeded()
    {
        var allGood = new[] { Ok(), Ok() };
        var oneBad = new[] { Ok(), Bad(total: 1, failed: 1) };

        Assert.True(LoaderRunResult.Aggregate("Test", allGood, TimeSpan.Zero).Success);
        Assert.False(LoaderRunResult.Aggregate("Test", oneBad, TimeSpan.Zero).Success);
    }

    // ---------------------------------------------------------- error messages

    [Fact]
    public void CombinesErrorMessagesFromFailingPartsOnly()
    {
        var parts = new[]
        {
            Bad(total: 1, failed: 1, errorMessage: "About endpoint: boom"),
            Bad(total: 1, failed: 1, errorMessage: "Storage endpoint: kaboom"),
            Ok() // succeeded; must not contribute
        };

        var message = LoaderRunResult.Aggregate("AGSI", parts, TimeSpan.Zero).ErrorMessage;

        Assert.Contains("About endpoint: boom", message);
        Assert.Contains("Storage endpoint: kaboom", message);
    }

    [Fact]
    public void ErrorMessageIsNull_WhenNoPartFailed()
    {
        var parts = new[] { Ok(), Ok() };

        Assert.Null(LoaderRunResult.Aggregate("Test", parts, TimeSpan.Zero).ErrorMessage);
    }

    [Fact]
    public void ErrorMessageIsNull_WhenAFailingPartCarriesNoMessageOfItsOwn()
    {
        // The common shape for a LoaderPipelineBase-built sub-pipeline: it fails
        // on work units (detail lives in FailedUnits), not on enumeration, so it
        // never sets ErrorMessage at all. Nothing to join is not an error.
        var parts = new[] { Bad(total: 5, failed: 2, errorMessage: null, failedUnits: new[] { Unit(1) }) };

        Assert.Null(LoaderRunResult.Aggregate("Test", parts, TimeSpan.Zero).ErrorMessage);
    }

    [Fact]
    public void IgnoresASuccessfulPartsErrorMessage()
    {
        // The overlap guard's "Skipped — another instance already running" shape:
        // Success = true with a non-null ErrorMessage. That must never leak into
        // the combined failure message.
        var skipped = new LoaderRunResult
        {
            LoaderId = "Test", Success = true, ErrorMessage = "Skipped — another instance already running"
        };

        var result = LoaderRunResult.Aggregate("Test", new[] { skipped, Bad(1, 1, "real failure") }, TimeSpan.Zero);

        Assert.DoesNotContain("Skipped", result.ErrorMessage);
        Assert.Contains("real failure", result.ErrorMessage);
    }

    [Fact]
    public void ExplicitErrorMessageOverride_TakesPrecedenceOverPartMessages()
    {
        // OilX/ICE/Marex/NGX/Genscape/Criterion/EOX/Argus/NUCORE track which named
        // feed/endpoint failed — information Aggregate cannot derive from a bare
        // LoaderRunResult (LoaderId on every part is the one shared loader id).
        var parts = new[] { Bad(1, 1, "low-level message that would normally be joined") };

        var result = LoaderRunResult.Aggregate(
            "OilX", parts, TimeSpan.Zero, errorMessage: "Failed feed(s): CargoTracking, Flow");

        Assert.Equal("Failed feed(s): CargoTracking, Flow", result.ErrorMessage);
    }

    // -------------------------------------------------- FailedUnits (the bug)

    [Fact]
    public void Aggregate_CarriesFailedUnitsThroughFromEveryPart()
    {
        // THIS is the production bug: a hand-rolled merge summed WorkUnitsFailed
        // but dropped FailedUnits entirely. Several sub-pipelines, failures
        // spread across them — every one of those units must survive the merge.
        var parts = new[]
        {
            Bad(total: 2, failed: 1, failedUnits: new[] { Unit(1) }),
            Ok(),
            Bad(total: 3, failed: 2, failedUnits: new[] { Unit(2), Unit(3) })
        };

        var result = LoaderRunResult.Aggregate("AGSI", parts, TimeSpan.Zero);

        Assert.Equal(3, result.WorkUnitsFailed);
        Assert.Equal(3, result.FailedUnits.Count);
        Assert.Equal(new[] { "unit=1", "unit=2", "unit=3" }, result.FailedUnits.Select(u => u.Key));
    }

    [Fact]
    public void FailedUnits_PreservesFullDetail_KeyDisplayErrorAndStack()
    {
        var unit = new FailedWorkUnit(
            Key: "country=hu&date=2026-10-26",
            DisplayName: "AGSI Storage 2026-10-26 (hu)",
            ErrorText: "[AGSI Storage] /api?country=hu&date=2026-10-26: HTTP 200 but the body reports error 'Try/Catch error'",
            StackTrace: "System.InvalidOperationException: ...\n   at DataLoader.AGSI.AgsiStorageSourceReader.ReadAsync()");

        var result = LoaderRunResult.Aggregate("AGSI", new[] { Bad(1, 1, failedUnits: new[] { unit } ) }, TimeSpan.Zero);

        var carried = Assert.Single(result.FailedUnits);
        Assert.Equal(unit.Key, carried.Key);
        Assert.Equal(unit.DisplayName, carried.DisplayName);
        Assert.Equal(unit.ErrorText, carried.ErrorText);
        Assert.Equal(unit.StackTrace, carried.StackTrace);
    }

    [Fact]
    public void CapsCombinedFailedUnitsAt25_WhileWorkUnitsFailedReportsTheTrueTotal()
    {
        // Two parts that individually respected LoaderPipelineBase's own 25-unit
        // cap would otherwise SUM past it when merged — right back into the
        // "balloon the email" problem that cap exists to prevent.
        var partA = Bad(total: 25, failed: 25, failedUnits: Enumerable.Range(1, 25).Select(Unit).ToArray());
        var partB = Bad(total: 25, failed: 25, failedUnits: Enumerable.Range(26, 25).Select(Unit).ToArray());

        var result = LoaderRunResult.Aggregate("Test", new[] { partA, partB }, TimeSpan.Zero);

        Assert.Equal(50, result.WorkUnitsFailed);
        Assert.Equal(LoaderRunResult.MaxFailedUnitsRetained, result.FailedUnits.Count);
        Assert.Equal("unit=1", result.FailedUnits[0].Key);
        Assert.Equal("unit=25", result.FailedUnits[24].Key);
    }

    [Fact]
    public void CapAppliesAcrossPartBoundaries_NotJustWithinOnePart()
    {
        // A cap enforced per-part (take 25 of each) rather than across the
        // combined list would let three parts of 10 each sail through as 30 —
        // make sure the cap is on the TOTAL, not re-applied per part.
        var parts = Enumerable.Range(0, 3)
            .Select(p => Bad(total: 10, failed: 10,
                failedUnits: Enumerable.Range(p * 10 + 1, 10).Select(Unit).ToArray()))
            .ToArray();

        var result = LoaderRunResult.Aggregate("Test", parts, TimeSpan.Zero);

        Assert.Equal(30, result.WorkUnitsFailed);
        Assert.Equal(LoaderRunResult.MaxFailedUnitsRetained, result.FailedUnits.Count);
    }

    // ------------------------------------------------------------- duration

    [Fact]
    public void UsesTheSuppliedDuration_NotASumOfPartDurations()
    {
        var parts = new[] { Ok(duration: TimeSpan.FromSeconds(5)), Ok(duration: TimeSpan.FromSeconds(7)) };

        var result = LoaderRunResult.Aggregate("Test", parts, TimeSpan.FromMinutes(1));

        Assert.Equal(TimeSpan.FromMinutes(1), result.Duration);
    }

    // ------------------------------------------------------------- edge cases

    [Fact]
    public void EmptySequence_IsASuccessWithEverythingZeroed()
    {
        // Matches the "nothing enabled, nothing to do" result every module
        // already returns by hand when its own pipeline list is empty.
        var result = LoaderRunResult.Aggregate("Test", Array.Empty<LoaderRunResult>(), TimeSpan.Zero);

        Assert.True(result.Success);
        Assert.Equal(0, result.WorkUnitsTotal);
        Assert.Equal(0, result.WorkUnitsFailed);
        Assert.Empty(result.FailedUnits);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public void SinglePart_PassesThroughItsOwnShape()
    {
        var part = Bad(total: 7, failed: 2, errorMessage: "boom", failedUnits: new[] { Unit(1), Unit(2) },
            duration: TimeSpan.FromSeconds(3));

        var result = LoaderRunResult.Aggregate("Test", new[] { part }, TimeSpan.FromSeconds(3));

        Assert.False(result.Success);
        Assert.Equal(7, result.WorkUnitsTotal);
        Assert.Equal(5, result.WorkUnitsSucceeded);
        Assert.Equal(2, result.WorkUnitsFailed);
        Assert.Equal("boom", result.ErrorMessage);
        Assert.Equal(2, result.FailedUnits.Count);
        Assert.Equal(TimeSpan.FromSeconds(3), result.Duration);
    }

    [Fact]
    public void UsesTheSuppliedLoaderId_NotAnyPartsLoaderId()
    {
        var parts = new[] { new LoaderRunResult { LoaderId = "SomeSubPipelineId", Success = true } };

        Assert.Equal("AGSI", LoaderRunResult.Aggregate("AGSI", parts, TimeSpan.Zero).LoaderId);
    }
}
