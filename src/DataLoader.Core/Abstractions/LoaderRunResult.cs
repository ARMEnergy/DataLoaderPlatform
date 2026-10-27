namespace DataLoader.Core.Abstractions;

/// <summary>
/// Summary of what a single loader did during one run.
/// Returned by <see cref="ILoaderModule.RunAsync"/>.
/// </summary>
public sealed class LoaderRunResult
{
    /// <summary>
    /// Cap on how many <see cref="FailedWorkUnit"/> details (each carrying a
    /// full stack trace) are retained — shared by <c>LoaderPipelineBase.Counters</c>
    /// (which fills <see cref="FailedUnits"/> for a single pipeline run) and
    /// <see cref="Aggregate"/> (which re-caps it when combining several). One
    /// constant so the two enforcement points cannot quietly drift apart; a
    /// loader with thousands of failures still cannot balloon this into a
    /// multi-megabyte result, and <see cref="WorkUnitsFailed"/> always carries
    /// the true count regardless of how much of <see cref="FailedUnits"/> survived.
    /// </summary>
    public const int MaxFailedUnitsRetained = 25;

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
    /// <para>Capped at <see cref="MaxFailedUnitsRetained"/> so a loader with
    /// thousands of failures cannot balloon this into a multi-megabyte result.
    /// <see cref="WorkUnitsFailed"/> always carries the true count, even when
    /// this list stopped short of it.</para>
    /// </summary>
    public IReadOnlyList<FailedWorkUnit> FailedUnits { get; init; } = Array.Empty<FailedWorkUnit>();

    public static LoaderRunResult Failed(string loaderId, string message, TimeSpan duration) => new()
    {
        LoaderId = loaderId,
        Success = false,
        ErrorMessage = message,
        Duration = duration
    };

    /// <summary>
    /// Combines the results of several sub-pipelines into one.
    ///
    /// <para>⚠ Use this rather than hand-rolling a merged <see cref="LoaderRunResult"/>.
    /// Most loaders run several pipelines (one per endpoint/feed/table) and combine
    /// them into the single result <see cref="ILoaderModule.RunAsync"/> has to return.
    /// A hand-written merge silently drops any field it was not written to know
    /// about — which is exactly how <see cref="FailedUnits"/> came to be empty in a
    /// production AGSI failure alert while <see cref="WorkUnitsFailed"/> correctly
    /// said 4: the merge summed the counters but never carried the list, so the
    /// email printed "...and 4 more failed unit(s)" with zero of the four shown
    /// above it. The next field added to this class would be dropped the same way
    /// by every hand-rolled merge that does not go through here.</para>
    ///
    /// <para><paramref name="duration"/> is taken as given rather than summed from
    /// <paramref name="parts"/>, because callers differ on what "the run's duration"
    /// means — most want the sum of sequential sub-pipeline durations (which is what
    /// every existing call site passes), but a caller measuring its own wall clock
    /// around the whole batch should be free to pass that instead.</para>
    /// </summary>
    /// <param name="loaderId">
    /// The loader's own id. Not read off <paramref name="parts"/> because a part
    /// built directly off <c>LoaderPipelineBase</c> already stamps this same value,
    /// but a synthetic part for a sub-pipeline that threw (<see cref="Failed"/>,
    /// wrapping the exception) may have been constructed before the caller had
    /// anything better to put there — the caller's own id is always correct.
    /// </param>
    /// <param name="parts">One result per sub-pipeline. Safe to pass an empty
    /// sequence — see the "nothing enabled" shape below.</param>
    /// <param name="duration">The run's overall duration — see above.</param>
    /// <param name="errorMessage">
    /// Optional override for the combined <see cref="ErrorMessage"/>. Several
    /// loaders (OilX, ICE, Marex, NGX, Genscape, Criterion, EOX, Argus, NUCORE)
    /// track which named feed/endpoint failed — information this method cannot
    /// reconstruct from a bare <see cref="LoaderRunResult"/>, since
    /// <see cref="LoaderId"/> on every part is the one shared loader id, not the
    /// sub-pipeline's own name, and a part that failed on some work units
    /// (rather than on enumerating them) carries no <see cref="ErrorMessage"/>
    /// of its own at all — the detail lives in <see cref="FailedUnits"/> instead.
    /// Pass the caller's own "Failed feed(s): A, B" style summary here rather
    /// than hand-building the final result just to attach it. Left null, the
    /// default (join every failing part's own <see cref="ErrorMessage"/>) is
    /// used, which is already the right answer for a loader with nothing extra
    /// to add.
    /// </param>
    public static LoaderRunResult Aggregate(
        string loaderId, IEnumerable<LoaderRunResult> parts, TimeSpan duration, string? errorMessage = null)
    {
        var partList = parts as IReadOnlyCollection<LoaderRunResult> ?? parts.ToList();

        // Each part already obeys MaxFailedUnitsRetained on its own, but re-cap
        // here too: several parts each sitting at the cap would otherwise sum to
        // well past it, right back into the "balloon the email" problem the cap
        // exists to prevent.
        var failedUnits = new List<FailedWorkUnit>(MaxFailedUnitsRetained);
        foreach (var part in partList)
        {
            if (failedUnits.Count >= MaxFailedUnitsRetained)
                break;
            var room = MaxFailedUnitsRetained - failedUnits.Count;
            failedUnits.AddRange(part.FailedUnits.Take(room));
        }

        // Only FAILING parts contribute — a part that succeeded with a non-null
        // ErrorMessage is the overlap-guard's "Skipped — another instance already
        // running" shape, which is informational, not a failure to report here.
        var combinedErrorMessage = errorMessage ?? CombineErrorMessages(partList);

        return new LoaderRunResult
        {
            LoaderId = loaderId,
            // True for an empty sequence (vacuous truth) — matches the "nothing
            // enabled, nothing to do" result every module already returns by hand
            // when its own enabled-pipelines list is empty.
            Success = partList.All(r => r.Success),
            WorkUnitsTotal = partList.Sum(r => r.WorkUnitsTotal),
            WorkUnitsSucceeded = partList.Sum(r => r.WorkUnitsSucceeded),
            WorkUnitsSkipped = partList.Sum(r => r.WorkUnitsSkipped),
            WorkUnitsFailed = partList.Sum(r => r.WorkUnitsFailed),
            RecordsProcessed = partList.Sum(r => r.RecordsProcessed),
            FailedUnits = failedUnits,
            ErrorMessage = combinedErrorMessage,
            Duration = duration
        };
    }

    private static string? CombineErrorMessages(IReadOnlyCollection<LoaderRunResult> parts)
    {
        var messages = parts
            .Where(r => !r.Success && !string.IsNullOrEmpty(r.ErrorMessage))
            .Select(r => r.ErrorMessage!)
            .ToList();

        return messages.Count > 0 ? string.Join("; ", messages) : null;
    }
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
