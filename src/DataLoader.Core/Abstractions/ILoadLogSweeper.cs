namespace DataLoader.Core.Abstractions;

/// <summary>
/// Housekeeping for <c>core.LoadLog</c>: closes rows that were opened but never closed.
///
/// <para>Deliberately a SEPARATE interface from <see cref="ILoadLogRepository"/>. That one is the
/// per-work-unit contract every pipeline depends on and every test fakes; this is a once-per-run
/// maintenance concern the host owns. Keeping them apart means a loader test's fake load log does
/// not have to grow a method it will never call.</para>
///
/// <para>Background: <c>usp_CompleteLoadLog</c> stamps <c>CompletedAtUtc</c> on success AND on
/// failure, so <c>IsComplete = 0</c> is simply how a failure is stored. The state this sweeps is
/// <c>CompletedAtUtc IS NULL</c> — a row whose <c>usp_BeginLoadLog</c> INSERT committed while the
/// client never received the id back, which leaves nothing able to close it.</para>
/// </summary>
public interface ILoadLogSweeper
{
    /// <summary>
    /// Closes never-closed rows belonging to runs OTHER than <paramref name="currentRunId"/> whose
    /// run is definitively over, and returns how many were closed.
    ///
    /// <para>Separate host invocations legitimately overlap, so a row is only closed when its own
    /// run has recorded a <c>CompletedAtUtc</c>, or when that run started longer ago than the
    /// configured staleness window (the killed-process case, where the run row never closed
    /// either). A live run's rows are never touched.</para>
    /// </summary>
    Task<int> AbandonStaleAsync(Guid currentRunId, CancellationToken cancellationToken);
}
