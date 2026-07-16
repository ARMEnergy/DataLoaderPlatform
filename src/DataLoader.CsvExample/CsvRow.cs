namespace DataLoader.CsvExample;

/// <summary>
/// One row read from a CSV file. The schema is intentionally generic —
/// real loaders would have a strongly-typed row for their specific feed.
///
/// This is just enough to demonstrate the platform pattern; a real CSV
/// loader for, say, daily commodity prices would have specific columns
/// (CommodityCode, TradeDate, OpenPrice, ClosePrice, …).
/// </summary>
public sealed class CsvRow
{
    /// <summary>Source filename — preserved so the destination can audit it.</summary>
    public string SourceFile { get; init; } = string.Empty;

    /// <summary>1-based row number in the source file.</summary>
    public int RowNumber { get; init; }

    /// <summary>Raw cells.</summary>
    public string[] Cells { get; init; } = Array.Empty<string>();
}
