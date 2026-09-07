using DataLoader.Core.Abstractions;
using DataLoader.Core.Sources;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DataLoader.EOX.Tests;

/// <summary>
/// Shared fixtures. The reader's FTP and FileLog collaborators are unused by
/// <c>Parse</c>, so the parse tests exercise it without any I/O at all.
/// </summary>
internal static class TestHelpers
{
    public static EoxSettings Settings(Action<EoxSettings>? configure = null)
    {
        var settings = new EoxSettings
        {
            ConnectionString = "Server=(local);Database=EOX;Integrated Security=SSPI;"
        };

        configure?.Invoke(settings);
        return settings;
    }

    public static EoxSourceReader Reader(EoxSettings? settings = null) =>
        new(new UnusedFtp(), new UnusedFileLog(), Options.Create(settings ?? Settings()), NullLogger.Instance);

    public static EoxWorkUnit Unit(
        EoxFeedDescriptor feed,
        DateOnly? curveDate = null,
        string? fileName = null,
        long size = 2_620_463,
        DateTime? modifiedUtc = null,
        bool isHot = true,
        string keySuffix = "")
    {
        var date = curveDate ?? new DateOnly(2026, 9, 4);
        var name = fileName ?? feed.FileNameFor(date, "1430");

        return new EoxWorkUnit
        {
            Feed = feed,
            CurveDate = date,
            File = new RemoteFile(
                "/" + name,
                name,
                size,
                modifiedUtc ?? new DateTime(2026, 9, 4, 18, 54, 0, DateTimeKind.Utc)),
            IsHot = isHot,
            KeySuffix = keySuffix
        };
    }

    public static LoaderRunContext Context(DateTime? startedAtUtc = null, Guid? runId = null) => new()
    {
        RunId = runId ?? Guid.Parse("11111111-2222-3333-4444-555555555555"),
        StartedAtUtc = startedAtUtc ?? new DateTime(2026, 9, 4, 18, 0, 0, DateTimeKind.Utc),
        CancellationToken = CancellationToken.None
    };

    /// <summary>Index of a descriptor column by name — tests read values positionally.</summary>
    public static int Ordinal(this EoxFeedDescriptor feed, string columnName)
    {
        for (var i = 0; i < feed.Columns.Count; i++)
            if (feed.Columns[i].Name.Equals(columnName, StringComparison.OrdinalIgnoreCase))
                return i;

        throw new ArgumentOutOfRangeException(nameof(columnName), columnName, $"Not a column of {feed.FeedId}.");
    }

    private sealed class UnusedFtp : IEoxFtp
    {
        public Task<IReadOnlyList<RemoteFile>> ListAsync(string path, string pattern, CancellationToken ct) =>
            throw new NotSupportedException("Parse tests must not touch the network.");

        public Task<Stream> OpenReadAsync(string fullPath, CancellationToken ct) =>
            throw new NotSupportedException("Parse tests must not touch the network.");

        public Task MoveAsync(string fromPath, string toPath, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class UnusedFileLog : IEoxFileLog
    {
        public Task<int> UpsertAsync(
            EoxFileContext file, string status, int rowCount, string? errorMessage, CancellationToken ct) =>
            throw new NotSupportedException("Parse tests must not touch the database.");
    }
}

/// <summary>Serves a canned directory listing and counts how often it is asked for one.</summary>
internal sealed class FakeEoxFtp : IEoxFtp
{
    private readonly Dictionary<string, List<RemoteFile>> _byDirectory = new(StringComparer.OrdinalIgnoreCase);

    public int ListCallCount { get; private set; }

    public FakeEoxFtp WithDirectory(string directory, params RemoteFile[] files)
    {
        _byDirectory[EoxFtpFileSystem.NormalizeDirectory(directory)] = files.ToList();
        return this;
    }

    /// <summary>Adds one file per feed for each supplied curve date, as the real drop would.</summary>
    public FakeEoxFtp WithDates(string directory, string timeToken, params DateOnly[] dates)
    {
        var files = new List<RemoteFile>();

        foreach (var date in dates)
        foreach (var feed in EoxDescriptors.All)
        {
            var name = feed.FileNameFor(date, timeToken);
            files.Add(new RemoteFile("/" + name, name, 1024,
                new DateTime(date.Year, date.Month, date.Day, 18, 54, 0, DateTimeKind.Utc)));
        }

        return WithDirectory(directory, files.ToArray());
    }

    public Task<IReadOnlyList<RemoteFile>> ListAsync(string path, string pattern, CancellationToken ct)
    {
        ListCallCount++;
        var key = EoxFtpFileSystem.NormalizeDirectory(path);

        return Task.FromResult<IReadOnlyList<RemoteFile>>(
            _byDirectory.TryGetValue(key, out var files)
                ? files.Where(f => EoxFtpFileSystem.GlobMatch(f.Name, pattern)).ToList()
                : new List<RemoteFile>());
    }

    public Task<Stream> OpenReadAsync(string fullPath, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task MoveAsync(string fromPath, string toPath, CancellationToken ct) =>
        throw new NotSupportedException();
}

/// <summary>Captures FileLog writes instead of hitting SQL.</summary>
internal sealed class FakeEoxFileLog : IEoxFileLog
{
    public List<(EoxFileContext File, string Status, int RowCount, string? Error)> Calls { get; } = new();

    public Task<int> UpsertAsync(
        EoxFileContext file, string status, int rowCount, string? errorMessage, CancellationToken ct)
    {
        Calls.Add((file, status, rowCount, errorMessage));
        return Task.FromResult(Calls.Count);
    }
}
