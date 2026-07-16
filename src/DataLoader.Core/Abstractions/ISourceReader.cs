namespace DataLoader.Core.Abstractions;

/// <summary>
/// Reads raw items for one work unit from the source system.
///
/// Implementations include:
///   - REST API readers          (see <see cref="Sources.HttpJsonSourceReaderBase{TUnit,TItem}"/>)
///   - FTP / SFTP file readers   (see <see cref="Sources.FileSourceReaderBase{TUnit,TItem}"/>)
///   - Local CSV readers
///   - S3 / Azure Blob readers
///
/// The reader's only job is to produce <typeparamref name="TItem"/> instances.
/// Don't transform here — that's the next stage of the pipeline.
/// </summary>
public interface ISourceReader<TUnit, TItem> where TUnit : WorkUnit
{
    Task<IReadOnlyList<TItem>> ReadAsync(TUnit unit, CancellationToken cancellationToken);
}
