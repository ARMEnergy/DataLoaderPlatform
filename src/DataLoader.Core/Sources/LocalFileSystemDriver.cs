namespace DataLoader.Core.Sources;

/// <summary>
/// Local-disk implementation of <see cref="IFileSystemDriver"/>.
/// Useful for CSV-drop loaders and for testing other loaders without
/// standing up a real FTP server.
/// </summary>
public sealed class LocalFileSystemDriver : IFileSystemDriver
{
    public Task<IReadOnlyList<RemoteFile>> ListAsync(string path, string pattern, CancellationToken ct)
    {
        if (!Directory.Exists(path))
            return Task.FromResult<IReadOnlyList<RemoteFile>>(Array.Empty<RemoteFile>());

        var files = Directory.EnumerateFiles(path, pattern, SearchOption.TopDirectoryOnly)
            .Select(p =>
            {
                var fi = new FileInfo(p);
                return new RemoteFile(fi.FullName, fi.Name, fi.Length, fi.LastWriteTimeUtc);
            })
            .ToList();

        return Task.FromResult<IReadOnlyList<RemoteFile>>(files);
    }

    public Task<Stream> OpenReadAsync(string fullPath, CancellationToken ct) =>
        Task.FromResult<Stream>(File.OpenRead(fullPath));

    public Task MoveAsync(string fromPath, string toPath, CancellationToken ct)
    {
        var toDir = Path.GetDirectoryName(toPath);
        if (!string.IsNullOrEmpty(toDir)) Directory.CreateDirectory(toDir);
        File.Move(fromPath, toPath, overwrite: true);
        return Task.CompletedTask;
    }
}
