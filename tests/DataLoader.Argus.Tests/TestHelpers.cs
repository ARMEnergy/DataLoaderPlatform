using DataLoader.Core.Sources;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DataLoader.Argus.Tests;

/// <summary>
/// Shared fixtures. The reader's FTP and FileLog collaborators are unused by
/// <c>Parse</c>, so the tests exercise parsing without any I/O at all.
/// </summary>
internal static class TestHelpers
{
    public static ArgusSettings Settings(Action<ArgusSettings>? configure = null)
    {
        var settings = new ArgusSettings
        {
            ConnectionString = "Server=(local);Database=Argus;Integrated Security=SSPI;"
        };

        configure?.Invoke(settings);
        return settings;
    }

    public static ArgusSourceReader Reader(ArgusSettings? settings = null) =>
        new(new UnusedFtp(), new UnusedFileLog(), Options.Create(settings ?? Settings()), NullLogger.Instance);

    public static ArgusWorkUnit Unit(
        ArgusFeedDescriptor feed,
        string? fileName = null,
        DateOnly? sourceFileDate = null,
        string? module = null,
        string? fullPath = null,
        long size = 1024,
        DateTime? modifiedUtc = null)
    {
        var name = fileName ?? feed.FileName ?? "20260827dhc.csv";
        var folder = feed.RemoteFolder == ArgusDescriptors.TimeSeriesFolder ? "/DCRDEUS" : "/DOCUMENTATION";

        return new ArgusWorkUnit
        {
            Feed = feed,
            File = new RemoteFile(
                fullPath ?? $"{folder}/{name}",
                name,
                size,
                modifiedUtc ?? new DateTime(2026, 8, 27, 15, 0, 0, DateTimeKind.Utc)),
            SourceFileDate = sourceFileDate,
            Module = module
        };
    }

    /// <summary>A DCRDEUS unit with the derived values a real one would carry.</summary>
    public static ArgusWorkUnit TimeSeriesUnit(string fileName = "20260827dhc.csv", string module = "DHC") =>
        Unit(ArgusDescriptors.TimeSeries,
             fileName,
             sourceFileDate: new DateOnly(2026, 8, 27),
             module: module,
             fullPath: $"/DCRDEUS/{fileName}");

    /// <summary>Index of a descriptor column by name — tests read values positionally.</summary>
    public static int Ordinal(this ArgusFeedDescriptor feed, string columnName)
    {
        for (var i = 0; i < feed.Columns.Count; i++)
            if (feed.Columns[i].Name.Equals(columnName, StringComparison.OrdinalIgnoreCase))
                return i;

        throw new ArgumentOutOfRangeException(nameof(columnName), columnName, $"Not a column of {feed.FeedId}.");
    }

    private sealed class UnusedFtp : IArgusFtp
    {
        public Task<IReadOnlyList<RemoteFile>> ListAsync(string path, string pattern, CancellationToken ct) =>
            throw new NotSupportedException("Parse tests must not touch the network.");

        public Task<Stream> OpenReadAsync(string fullPath, CancellationToken ct) =>
            throw new NotSupportedException("Parse tests must not touch the network.");

        public Task MoveAsync(string fromPath, string toPath, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class UnusedFileLog : IArgusFileLog
    {
        public Task<int> UpsertAsync(
            ArgusFileContext file, string status, int rowCount, string? errorMessage, CancellationToken ct) =>
            throw new NotSupportedException("Parse tests must not touch the database.");
    }
}

/// <summary>Records every listing/download so provider tests can assert on them.</summary>
internal sealed class FakeArgusFtp : IArgusFtp
{
    private readonly Dictionary<string, List<RemoteFile>> _byDirectory = new(StringComparer.OrdinalIgnoreCase);

    public int ListCallCount { get; private set; }

    public FakeArgusFtp WithDirectory(string directory, params RemoteFile[] files)
    {
        _byDirectory[ArgusFtpFileSystem.NormalizeDirectory(directory)] = files.ToList();
        return this;
    }

    public Task<IReadOnlyList<RemoteFile>> ListAsync(string path, string pattern, CancellationToken ct)
    {
        ListCallCount++;
        var key = ArgusFtpFileSystem.NormalizeDirectory(path);

        return Task.FromResult<IReadOnlyList<RemoteFile>>(
            _byDirectory.TryGetValue(key, out var files) ? files : new List<RemoteFile>());
    }

    public Task<Stream> OpenReadAsync(string fullPath, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task MoveAsync(string fromPath, string toPath, CancellationToken ct) =>
        throw new NotSupportedException();
}

/// <summary>Captures FileLog writes instead of hitting SQL.</summary>
internal sealed class FakeArgusFileLog : IArgusFileLog
{
    public List<(ArgusFileContext File, string Status, int RowCount, string? Error)> Calls { get; } = new();

    public Task<int> UpsertAsync(
        ArgusFileContext file, string status, int rowCount, string? errorMessage, CancellationToken ct)
    {
        Calls.Add((file, status, rowCount, errorMessage));
        return Task.FromResult(Calls.Count);
    }
}
