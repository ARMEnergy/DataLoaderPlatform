namespace DataLoader.Core.Abstractions;

/// <summary>
/// Audit log for work units. Used by the pipeline for two things:
///
///   1. <b>Idempotency.</b> Before processing a unit, ask: was this unit
///      already loaded successfully? If yes, skip it. Cheap retries.
///
///   2. <b>Observability.</b> Every attempt — success or failure — is
///      recorded with row count, duration, and any error message.
///
/// The platform ships a SQL implementation
/// (<see cref="Persistence.SqlLoadLogRepository"/>) that writes to a single
/// shared <c>core.LoadLog</c> table, partitioned by <c>LoaderId</c>.
/// A loader can swap in its own implementation if it needs to.
/// </summary>
public interface ILoadLogRepository
{
    /// <summary>
    /// Opens a load-log row for a work unit. Returns <c>null</c> if this unit
    /// has already been processed successfully and should be skipped.
    /// Otherwise returns a handle to update on completion.
    /// </summary>
    Task<LoadLogHandle?> BeginAsync(
        string loaderId,
        Guid runId,
        string workUnitKey,
        string workUnitDisplay,
        CancellationToken cancellationToken);

    /// <summary>
    /// Closes a load-log row as success.
    /// </summary>
    Task CompleteSuccessAsync(LoadLogHandle handle, int recordsProcessed, CancellationToken cancellationToken);

    /// <summary>
    /// Closes a load-log row as failure.
    /// </summary>
    Task CompleteFailureAsync(LoadLogHandle handle, string errorMessage, CancellationToken cancellationToken);

    /// <summary>
    /// Opens a run row at host startup.
    /// </summary>
    Task BeginRunAsync(Guid runId, CancellationToken cancellationToken);

    /// <summary>
    /// Closes the run row when all loaders finish.
    /// </summary>
    Task CompleteRunAsync(Guid runId, bool success, CancellationToken cancellationToken);
}

/// <summary>
/// Opaque handle to an open load-log row. The repository uses its internal
/// id and timestamp to close the row out.
/// </summary>
public sealed class LoadLogHandle
{
    public required long LoadLogId { get; init; }
    public required string LoaderId { get; init; }
    public required string WorkUnitKey { get; init; }
    public required DateTime StartedAtUtc { get; init; }
}
