namespace DataLoader.Core.Abstractions;

/// <summary>
/// Everything a loader needs to know about the current run that isn't its own
/// configuration: who's running it, when, and how to cancel.
/// </summary>
public sealed class LoaderRunContext
{
    /// <summary>
    /// Identifier of this whole host run. Every loader on the same run shares
    /// this value; it ties together rows in <c>core.LoaderRun</c> and
    /// <c>core.LoadLog</c>.
    /// </summary>
    public required Guid RunId { get; init; }

    /// <summary>
    /// UTC time the host run started.
    /// </summary>
    public required DateTime StartedAtUtc { get; init; }

    /// <summary>
    /// Optional bounds for time-windowed loaders. A loader is free to ignore
    /// these if its source isn't date-based (e.g. an FTP file drop).
    /// </summary>
    public DateTime? DateFrom { get; init; }
    public DateTime? DateTo { get; init; }

    /// <summary>
    /// Cooperative cancellation for the whole run (Ctrl+C, timeout, etc).
    /// Loaders must observe this.
    /// </summary>
    public required CancellationToken CancellationToken { get; init; }

    /// <summary>
    /// Cancelled only when this loader's run deadline
    /// (<c>Platform:RunDeadlineMinutes</c>) fired. <see cref="CancellationToken"/> is
    /// cancelled too in that case, so a loader never needs to observe this one; it exists
    /// so a pipeline can SAY why it was stopped ("run deadline reached" rather than
    /// "run cancelled") when it closes the in-flight unit's load-log row.
    /// <see cref="System.Threading.CancellationToken.None"/> when no deadline is configured.
    /// </summary>
    public CancellationToken RunDeadlineToken { get; init; }
}
