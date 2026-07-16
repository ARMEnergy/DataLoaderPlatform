using DataLoader.Core.Configuration;

namespace DataLoader.CsvExample;

/// <summary>
/// CSV-drop loader settings — watch a directory, parse each new file, move
/// processed ones aside.
/// </summary>
public sealed class CsvExampleSettings : LoaderSettingsBase
{
    /// <summary>Directory containing new CSV files to process.</summary>
    public string DropDirectory { get; set; } = string.Empty;

    /// <summary>Directory to move processed files into. Optional — leave
    /// empty to delete instead.</summary>
    public string ProcessedDirectory { get; set; } = string.Empty;

    /// <summary>Glob pattern, e.g. <c>*.csv</c>.</summary>
    public string FilePattern { get; set; } = "*.csv";

    /// <summary>If true, the first row of each file is treated as headers
    /// and skipped.</summary>
    public bool HasHeaderRow { get; set; } = true;
}
