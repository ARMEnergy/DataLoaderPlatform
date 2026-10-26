namespace DataLoader.Core.Abstractions;

/// <summary>
/// Summary of what a single loader did during one run.
/// Returned by <see cref="ILoaderModule.RunAsync"/>.
/// </summary>
public sealed class LoaderRunResult
{
    public required string LoaderId { get; init; }
    public required bool Success { get; init; }
    public int WorkUnitsTotal { get; init; }
    public int WorkUnitsSucceeded { get; init; }
    public int WorkUnitsSkipped { get; init; }
    public int WorkUnitsFailed { get; init; }
    public int RecordsProcessed { get; init; }
    public string? ErrorMessage { get; init; }
    public TimeSpan Duration { get; init; }

    /// <summary>
    /// The individual work units that failed, captured at the moment of
    /// failure — see <see cref="FailedWorkUnit"/> for why that has to happen
    /// in-process rather than by reading <c>core.LoadLog</c> back afterwards.
    ///
    /// <para>Capped (currently at 25 — see <c>LoaderPipelineBase.Counters</c>)
    /// so a loader with thousands of failures cannot balloon this into a
    /// multi-megabyte result. <see cref="WorkUnitsFailed"/> always carries the
    /// true count, even when this list stopped short of it.</para>
    /// </summary>
    public IReadOnlyList<FailedWorkUnit> FailedUnits { get; init; } = Array.Empty<FailedWorkUnit>();

    public static LoaderRunResult Failed(string loaderId, string message, TimeSpan duration) => new()
    {
        LoaderId = loaderId,
        Success = false,
        ErrorMessage = message,
        Duration = duration
    };
}

/// <summary>
/// One work unit that failed, captured at the moment of failure.
///
/// <para>⚠ Captured in-process rather than read back from <c>core.LoadLog</c>,
/// for two reasons: that table stores only the exception MESSAGE, so the stack
/// is gone once the run ends; and the notifier has to work when the platform
/// database is unreachable, which is precisely the failure a SQL-side notifier
/// can never report.</para>
/// </summary>
public sealed record FailedWorkUnit(string Key, string DisplayName, string ErrorText, string? StackTrace);
