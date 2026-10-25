using DataLoader.Core.Abstractions;
using DataLoader.Core.Notifications;
using Xunit;

namespace DataLoader.Core.Tests;

public class RunFailureReportTests
{
    private static LoaderRunResult Ok(string id) => new()
    {
        LoaderId = id, Success = true, WorkUnitsTotal = 10, WorkUnitsSucceeded = 10, RecordsProcessed = 500
    };

    private static LoaderRunResult Bad(string id, string error, int failed = 2, int total = 14) => new()
    {
        LoaderId = id, Success = false, ErrorMessage = error,
        WorkUnitsTotal = total, WorkUnitsFailed = failed, WorkUnitsSucceeded = total - failed
    };

    private static RunFailureReport Report(
        IEnumerable<LoaderRunResult>? results = null,
        int staleRowsClosed = 0,
        Exception? exception = null) => new()
    {
        HostName = "ARMH-OPSDB01",
        RunId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
        StartedAtUtc = new DateTime(2026, 10, 24, 11, 0, 0, DateTimeKind.Utc),
        CompletedAtUtc = new DateTime(2026, 10, 24, 11, 5, 0, DateTimeKind.Utc),
        Results = (results ?? Array.Empty<LoaderRunResult>()).ToList(),
        StaleRowsClosed = staleRowsClosed,
        Exception = exception
    };

    // ------------------------------------------------------------ ShouldNotify

    [Fact]
    public void ShouldNotify_False_WhenEverythingSucceeded()
    {
        Assert.False(Report(new[] { Ok("Platts"), Ok("Vulcan") }).ShouldNotify);
    }

    [Fact]
    public void ShouldNotify_True_WhenAnyLoaderFailed()
    {
        Assert.True(Report(new[] { Ok("Platts"), Bad("Vulcan", "boom") }).ShouldNotify);
    }

    [Fact]
    public void ShouldNotify_True_WhenStaleRowsWereClosed_EvenThoughTheRunSucceeded()
    {
        // Stale rows mean an EARLIER run was killed. Nothing else reports that
        // from inside the process, so a clean run still has news to deliver.
        Assert.True(Report(new[] { Ok("Platts") }, staleRowsClosed: 3).ShouldNotify);
    }

    [Fact]
    public void ShouldNotify_True_OnUnhandledException()
    {
        Assert.True(Report(exception: new InvalidOperationException("bang")).ShouldNotify);
    }

    [Fact]
    public void ShouldNotify_False_ForAnOverlapSkip()
    {
        // The overlap guard records Success = true with an explanatory message.
        // Another process is already running the loader; that is not a failure.
        var skipped = new LoaderRunResult
        {
            LoaderId = "Platts", Success = true,
            ErrorMessage = "Skipped — another instance already running"
        };

        Assert.False(Report(new[] { skipped }).ShouldNotify);
    }

    // ------------------------------------------------------------- the subject

    [Fact]
    public void Subject_SingleFailingLoader_NamesItAndItsUnitCounts()
    {
        var subject = Report(new[] { Bad("Platts", "boom") }).BuildMessage().Subject;

        // No prefix here — FailureNotifier prepends it on the single send path.
        Assert.Equal("FAILED: Platts on ARMH-OPSDB01 (2/14 units failed)", subject);
    }

    [Fact]
    public void Subject_SeveralFailingLoaders_CountsThem()
    {
        var subject = Report(new[] { Bad("Platts", "a"), Bad("Vulcan", "b"), Ok("CWG") })
            .BuildMessage().Subject;

        Assert.Equal("FAILED: 2 loaders on ARMH-OPSDB01", subject);
    }

    [Fact]
    public void Subject_UnhandledException_NamesTheExceptionType()
    {
        var subject = Report(exception: new InvalidOperationException("bang"))
            .BuildMessage().Subject;

        Assert.Contains("InvalidOperationException", subject);
        Assert.Contains("ARMH-OPSDB01", subject);
    }

    [Fact]
    public void Subject_OnlyStaleRows_IsAWarningNotAFailure()
    {
        var subject = Report(new[] { Ok("Platts") }, staleRowsClosed: 3)
            .BuildMessage().Subject;

        Assert.Contains("WARNING", subject);
        Assert.Contains("3", subject);
    }

    // ---------------------------------------------------------------- the body

    [Fact]
    public void Body_ContainsRunIdHostPerLoaderDetailAndTheLogPath()
    {
        var body = Report(new[] { Ok("Platts"), Bad("Vulcan", "boom") }).BuildMessage().Body;

        Assert.Contains("11111111-2222-3333-4444-555555555555", body);
        Assert.Contains("ARMH-OPSDB01", body);
        Assert.Contains("Platts", body);
        Assert.Contains("Vulcan", body);
        Assert.Contains("boom", body);
        Assert.Contains(@"C:\ETL_Tools\DataLoaderPlatform\Log", body);
    }

    [Fact]
    public void Body_IncludesTheStackTraceWhenThereWasAnException()
    {
        Exception captured;
        try { throw new InvalidOperationException("bang"); }
        catch (Exception ex) { captured = ex; }

        var body = Report(exception: captured).BuildMessage().Body;

        Assert.Contains("InvalidOperationException", body);
        Assert.Contains("bang", body);
    }

    [Fact]
    public void Body_RedactsSecretsOutOfLoaderErrorMessages()
    {
        // The whole point of ErrorTextRedactor — proven at the boundary that
        // actually sends, not only in the redactor's own unit tests.
        var body = Report(new[] { Bad("AGSI", "access denied (API key: abc123XYZ)") })
            .BuildMessage().Body;

        Assert.DoesNotContain("abc123XYZ", body);
        Assert.Contains("REDACTED", body);
    }

    [Fact]
    public void Body_TruncatesAPathologicallyLongErrorMessage()
    {
        var body = Report(new[] { Bad("Platts", new string('x', 50_000)) })
            .BuildMessage().Body;

        Assert.True(body.Length < 5_000, $"body was {body.Length} chars");
        Assert.Contains("truncated", body);
    }

    [Fact]
    public void Body_DurationOverADay_ShowsTotalHoursNotAClockComponent()
    {
        var report = new RunFailureReport
        {
            HostName = "ARMH-OPSDB01",
            RunId = Guid.NewGuid(),
            StartedAtUtc = new DateTime(2026, 10, 23, 1, 0, 0, DateTimeKind.Utc),
            CompletedAtUtc = new DateTime(2026, 10, 24, 11, 5, 0, DateTimeKind.Utc),
            Results = new[] { LoaderRunResult.Failed("EIA", "boom", TimeSpan.Zero) }
        };

        // 34h05m. The `hh` format token would render this as "10:05:00".
        Assert.Contains("34:05:00", report.BuildMessage().Body);
    }

    [Fact]
    public void Body_NegativeDuration_IsSignedNotSilentlyPositive()
    {
        var report = new RunFailureReport
        {
            HostName = "ARMH-OPSDB01",
            RunId = Guid.NewGuid(),
            StartedAtUtc = new DateTime(2026, 10, 24, 11, 5, 0, DateTimeKind.Utc),
            CompletedAtUtc = new DateTime(2026, 10, 24, 11, 0, 0, DateTimeKind.Utc),
            Results = new[] { LoaderRunResult.Failed("Platts", "boom", TimeSpan.Zero) }
        };

        Assert.Contains("-00:05:00", report.BuildMessage().Body);
    }

    [Fact]
    public void Body_SingleFailure_NamesTheActualLogFile()
    {
        var body = Report(new[] { Ok("Platts"), Bad("Vulcan", "boom") }).BuildMessage().Body;

        Assert.Contains(@"Log\Vulcan.log", body);
    }

    [Fact]
    public void Body_SeveralFailures_KeepsThePlaceholder()
    {
        var body = Report(new[] { Bad("Platts", "a"), Bad("Vulcan", "b") }).BuildMessage().Body;

        Assert.Contains(@"Log\<Loader>.log", body);
    }

    [Fact]
    public void BuildMessage_Throws_WhenNothingToReport()
    {
        var report = Report(new[] { Ok("Platts") });

        Assert.Throws<InvalidOperationException>(() => report.BuildMessage());
    }

    // ------------------------------------------------- non-run alerts (watchdog)

    [Fact]
    public void Body_OmitsRunId_WhenThereIsNoRealRunToName()
    {
        // The watchdog's own "could not read history" alert passes Guid.Empty
        // because it is reporting a failed CHECK, not a run — the all-zero
        // guid would only confuse whoever reads it.
        var now = new DateTime(2026, 10, 24, 11, 0, 0, DateTimeKind.Utc);
        var report = new RunFailureReport
        {
            HostName = "ARMH-OPSDB01",
            RunId = Guid.Empty,
            StartedAtUtc = now,
            CompletedAtUtc = now,
            Exception = new InvalidOperationException("db down")
        };

        var body = report.BuildMessage().Body;

        Assert.DoesNotContain("Run id:", body);
        Assert.DoesNotContain("00000000-0000-0000-0000-000000000000", body);
    }

    [Fact]
    public void Body_OmitsDuration_WhenStartedAndCompletedAreTheSameInstant()
    {
        var now = new DateTime(2026, 10, 24, 11, 0, 0, DateTimeKind.Utc);
        var report = new RunFailureReport
        {
            HostName = "ARMH-OPSDB01",
            RunId = Guid.Empty,
            StartedAtUtc = now,
            CompletedAtUtc = now,
            Exception = new InvalidOperationException("db down")
        };

        var body = report.BuildMessage().Body;

        Assert.DoesNotContain("Duration:", body);
    }

    [Fact]
    public void Body_StillShowsRunIdAndDuration_ForARealRun()
    {
        // Guards the guard: a normal in-process run report (non-empty RunId,
        // a real elapsed span) must not lose these lines too.
        var body = Report(new[] { Bad("Platts", "boom") }).BuildMessage().Body;

        Assert.Contains("Run id:", body);
        Assert.Contains("Duration:", body);
    }
}
