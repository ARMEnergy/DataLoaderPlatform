namespace DataLoader.Core.Abstractions;

/// <summary>
/// The smallest unit of work the pipeline processes independently.
///
/// What a "work unit" means is up to the loader:
///   - Energy Aspects: one (mapping_id, date-window) pair
///   - FTP loader:     one file path on the remote server
///   - CSV drop:       one local file
///
/// The framework treats work units opaquely. It uses <see cref="Key"/> to
/// identify each unit in the load log so it can skip already-processed units
/// on a re-run (idempotency).
/// </summary>
public abstract class WorkUnit
{
    /// <summary>
    /// Stable string identifier — must uniquely identify this unit *for this
    /// loader*. Two work units with the same key are considered the same
    /// piece of work; the load log uses this to detect "already done".
    ///
    /// Examples:
    ///   "mapping=12;from=2024-01-01;to=2024-01-31"
    ///   "ftp://feeds/2024-05-27/prices.csv"
    ///   "drop/orders/2024-05-27.csv"
    /// </summary>
    public abstract string Key { get; }

    /// <summary>
    /// Optional friendly description for log output.
    /// </summary>
    public virtual string DisplayName => Key;
}
