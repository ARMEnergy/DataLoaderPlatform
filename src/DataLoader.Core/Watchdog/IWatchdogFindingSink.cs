namespace DataLoader.Core.Watchdog;

/// <summary>
/// Records one watchdog pass and its findings (<c>core.WatchdogPass</c> /
/// <c>core.WatchdogFinding</c>). An ADDITIONAL sink fed from the same
/// <see cref="WatchdogReport"/> the email is built from — it never changes what is
/// detected or what is mailed.
///
/// <para>May throw. <see cref="WatchdogRunner"/> owns the isolation: a persistence
/// failure is logged at Error and noted in the email, and never stops the email.</para>
/// </summary>
public interface IWatchdogFindingSink
{
    Task<WatchdogPersistResult> RecordPassAsync(WatchdogPassRecord pass, CancellationToken cancellationToken);
}

/// <summary>
/// The database half of the sink — two calls, so the orchestration in
/// <see cref="WatchdogFindingSink"/> is testable without SQL Server.
/// </summary>
public interface IWatchdogFindingStore
{
    /// <summary>Every open finding, from <c>core.vw_WatchdogFinding_Open</c>.</summary>
    Task<IReadOnlyList<OpenWatchdogFinding>> ReadOpenFindingsAsync(CancellationToken cancellationToken);

    /// <summary>One call to <c>core.usp_RecordWatchdogPass</c>.</summary>
    Task<WatchdogPersistResult> RecordPassAsync(
        WatchdogPassRecord pass, IReadOnlyList<WatchdogResolutionHint> hints, CancellationToken cancellationToken);
}

/// <summary>
/// The proc's outputs. <see cref="RetentionError"/> is set when the post-commit
/// retention sweep failed — the pass itself WAS recorded.
/// </summary>
public sealed record WatchdogPersistResult(long PassId, string? RetentionError);
