namespace DataLoader.Core.Sources;

/// <summary>
/// Pluggable filesystem driver — local disk, FTP, SFTP, S3, Azure Blob, …
///
/// Loaders that consume files from anywhere depend on this interface, not on
/// a specific protocol. The platform ships
/// <see cref="LocalFileSystemDriver"/> and a stub <see cref="Ftp.FtpFileSystemDriver"/>;
/// new drivers can be added without touching loaders.
/// </summary>
public interface IFileSystemDriver
{
    /// <summary>
    /// List files in <paramref name="path"/> matching <paramref name="pattern"/>.
    /// Pattern is glob-style (<c>*.csv</c>). Recursion is driver-defined.
    /// </summary>
    Task<IReadOnlyList<RemoteFile>> ListAsync(string path, string pattern, CancellationToken ct);

    /// <summary>
    /// Open a stream over the file's contents. Caller disposes.
    /// </summary>
    Task<Stream> OpenReadAsync(string fullPath, CancellationToken ct);

    /// <summary>
    /// Optional — move a processed file aside so it isn't picked up again.
    /// Drivers that don't support move should throw <see cref="NotSupportedException"/>.
    /// </summary>
    Task MoveAsync(string fromPath, string toPath, CancellationToken ct);
}

public sealed record RemoteFile(string FullPath, string Name, long Size, DateTime LastModifiedUtc);
