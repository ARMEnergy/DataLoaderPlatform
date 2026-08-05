using DataLoader.Core.Abstractions;
using DataLoader.Core.Sources;

namespace DataLoader.Platts;

/// <summary>
/// One parsed row of the SymbolData feed, targeting <c>arm.SymbolData</c>.
/// The trailing carry fields are not written to <c>arm.SymbolData</c>; they feed
/// the <c>arm.FileLog</c> audit row via the file-logging sink decorator.
/// </summary>
public sealed class SymbolDataRow
{
    // --- arm.SymbolData columns ---
    public string MDC { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public string Bate { get; set; } = string.Empty;
    public DateTime Date { get; set; }
    public string Action { get; set; } = string.Empty;
    public decimal? Value { get; set; }
    public DateTime ActionDate { get; set; }
    public string SourcePath { get; set; } = string.Empty;

    // --- carry fields for arm.FileLog ---
    public string FileName { get; set; } = string.Empty;
    public DateTime LastModifiedUtc { get; set; }
    public long? SizeBytes { get; set; }
}

/// <summary>
/// One parsed row of the Symbol reference feed, targeting <c>arm.Symbol</c>.
/// The trailing carry fields feed the <c>arm.FileLog</c> audit row.
/// </summary>
public sealed class SymbolRow
{
    // --- arm.Symbol columns ---
    public string? MDC { get; set; }
    public string? Trans { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public string? Bates { get; set; }
    public string? Freq { get; set; }
    public string? Curr { get; set; }
    public string? UOM { get; set; }
    public int? Dec { get; set; }
    public decimal? Conv { get; set; }
    public string? Flag { get; set; }
    public string? ToUom { get; set; }
    public DateTime? Earliest { get; set; }
    public DateTime? Latest { get; set; }
    public string? Description { get; set; }

    // --- carry fields for arm.FileLog ---
    public string SourcePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public DateTime LastModifiedUtc { get; set; }
    public long? SizeBytes { get; set; }
}

/// <summary>One <c>.ftp</c> market file in a date folder = one SymbolData work unit.</summary>
public sealed class SymbolDataWorkUnit : WorkUnit
{
    public required RemoteFile File { get; init; }

    /// <summary>The date folder's name (last path segment), e.g. <c>20260731</c>.</summary>
    public required string Folder { get; init; }

    // LastModifiedUtc in the key means an unchanged file is skipped and a changed
    // one (new timestamp) gets a new key → reprocessed.
    public override string Key => $"platts:sd:{Folder}/{File.Name}:{File.Size}:{File.LastModifiedUtc:O}";
    public override string DisplayName => $"{Folder}/{File.Name}";
}

/// <summary>One reference-metadata CSV = one Symbol work unit.</summary>
public sealed class SymbolWorkUnit : WorkUnit
{
    public required RemoteFile File { get; init; }

    public override string Key => $"platts:sym:{File.Name}:{File.Size}:{File.LastModifiedUtc:O}";
    public override string DisplayName => File.Name;
}
