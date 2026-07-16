using DataLoader.Core.Abstractions;
using DataLoader.Core.Sources;

namespace DataLoader.CsvExample;

/// <summary>
/// One file in the drop directory = one work unit. Re-running the loader
/// will skip files that were already loaded thanks to the load log
/// (file path → key).
/// </summary>
public sealed class CsvFileWorkUnit : WorkUnit
{
    public required RemoteFile File { get; init; }

    public override string Key => $"csv:{File.FullPath}:{File.LastModifiedUtc:O}";
    public override string DisplayName => File.Name;
}
