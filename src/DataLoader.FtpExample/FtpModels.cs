using DataLoader.Core.Abstractions;
using DataLoader.Core.Sources;

namespace DataLoader.FtpExample;

/// <summary>One remote file on the FTP server = one work unit.</summary>
public sealed class FtpFileWorkUnit : WorkUnit
{
    public required RemoteFile File { get; init; }

    public override string Key => $"ftp:{File.FullPath}:{File.Size}:{File.LastModifiedUtc:O}";
    public override string DisplayName => File.Name;
}

/// <summary>
/// One parsed row from a downloaded file. Same shape as the CSV-drop loader
/// because both happen to deliver CSV — but they're separate types so each
/// loader can evolve its schema independently.
/// </summary>
public sealed class FtpFeedRow
{
    public string SourceFile { get; init; } = string.Empty;
    public DateTime IngestedAtUtc { get; init; }
    public int RowNumber { get; init; }
    public string[] Cells { get; init; } = Array.Empty<string>();
}
