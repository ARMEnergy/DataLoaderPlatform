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
}
