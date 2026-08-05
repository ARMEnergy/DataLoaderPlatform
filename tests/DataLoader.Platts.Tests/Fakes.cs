using System.Text;
using DataLoader.Core.Abstractions;
using DataLoader.Core.Sources;

namespace DataLoader.Platts.Tests;

/// <summary>
/// In-memory <see cref="IPlattsSftp"/> test double. No network, no real
/// <c>SftpClient</c>. Directory listings and file contents are supplied up-front.
/// </summary>
internal sealed class FakePlattsSftp : IPlattsSftp
{
    /// <summary>Full path -> file bytes, returned by <see cref="OpenReadAsync"/>.</summary>
    public Dictionary<string, byte[]> Files { get; } = new();

    /// <summary>Directory path -> files, returned by <see cref="ListAsync"/>.</summary>
    public Dictionary<string, IReadOnlyList<RemoteFile>> Listings { get; } = new();

    /// <summary>Result of <see cref="ListDirectoriesAsync"/>.</summary>
    public IReadOnlyList<string> Directories { get; set; } = Array.Empty<string>();

    public List<string> OpenedPaths { get; } = new();

    public void AddTextFile(string fullPath, string content) =>
        Files[fullPath] = Encoding.UTF8.GetBytes(content);

    public Task<IReadOnlyList<string>> ListDirectoriesAsync(string path, CancellationToken ct) =>
        Task.FromResult(Directories);

    public Task<IReadOnlyList<RemoteFile>> ListAsync(string path, string pattern, CancellationToken ct) =>
        Task.FromResult(Listings.TryGetValue(path, out var files)
            ? files
            : (IReadOnlyList<RemoteFile>)Array.Empty<RemoteFile>());

    public Task<Stream> OpenReadAsync(string fullPath, CancellationToken ct)
    {
        OpenedPaths.Add(fullPath);
        if (!Files.TryGetValue(fullPath, out var bytes))
            throw new FileNotFoundException($"FakePlattsSftp has no file at '{fullPath}'");
        return Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
    }

    public Task MoveAsync(string fromPath, string toPath, CancellationToken ct) =>
        throw new NotSupportedException();
}

/// <summary>Records the calls to <see cref="IPlattsFileLog.UpsertAsync"/> for assertion.</summary>
internal sealed class FakeFileLog : IPlattsFileLog
{
    public sealed record Call(
        string Feed, string SourcePath, string FileName, DateTime LastModifiedUtc,
        long? SizeBytes, int RowCount, string Status, CancellationToken Token);

    public List<Call> Calls { get; } = new();

    public Task UpsertAsync(
        string feed, string sourcePath, string fileName, DateTime lastModifiedUtc,
        long? sizeBytes, int rowCount, string status, CancellationToken ct)
    {
        Calls.Add(new Call(feed, sourcePath, fileName, lastModifiedUtc, sizeBytes, rowCount, status, ct));
        return Task.CompletedTask;
    }
}

/// <summary>
/// Inner <see cref="ISink{TRow}"/> stub: returns a preset count, records the rows
/// it was handed, and can be told to throw to exercise the failure path.
/// </summary>
internal sealed class FakeSink<TRow> : ISink<TRow>
{
    public int ReturnCount { get; set; }
    public Exception? ThrowOnWrite { get; set; }
    public int WriteCalls { get; private set; }
    public IReadOnlyList<TRow>? LastRows { get; private set; }

    public Task<int> WriteAsync(IReadOnlyList<TRow> rows, CancellationToken cancellationToken)
    {
        WriteCalls++;
        LastRows = rows;
        if (ThrowOnWrite is not null)
            throw ThrowOnWrite;
        return Task.FromResult(ReturnCount);
    }
}
