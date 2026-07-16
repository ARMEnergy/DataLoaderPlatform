using DataLoader.Core.Sources;

namespace DataLoader.CsvExample;

/// <summary>
/// Loader-local wrapper around <see cref="IFileSystemDriver"/>.
///
/// Why an extra interface? Because <see cref="IFileSystemDriver"/> is a
/// shared platform abstraction; if both CSV and FTP loaders registered it
/// globally with different implementations, the second registration would
/// silently win. Each loader instead registers its own loader-local
/// interface (this one for CSV, <see cref="FtpExample.IFtpFileSystem"/> for
/// FTP), wrapping its choice of driver.
///
/// At swap-time — if the CSV loader needs to read from S3 instead of local
/// disk — change just the implementation registered for
/// <see cref="ICsvFileSystem"/>.
/// </summary>
public interface ICsvFileSystem : IFileSystemDriver
{
}

/// <summary>
/// Default implementation: local disk.
/// </summary>
internal sealed class LocalCsvFileSystem : ICsvFileSystem
{
    private readonly LocalFileSystemDriver _inner = new();

    public Task<IReadOnlyList<RemoteFile>> ListAsync(string path, string pattern, CancellationToken ct)
        => _inner.ListAsync(path, pattern, ct);

    public Task<Stream> OpenReadAsync(string fullPath, CancellationToken ct)
        => _inner.OpenReadAsync(fullPath, ct);

    public Task MoveAsync(string fromPath, string toPath, CancellationToken ct)
        => _inner.MoveAsync(fromPath, toPath, ct);
}
