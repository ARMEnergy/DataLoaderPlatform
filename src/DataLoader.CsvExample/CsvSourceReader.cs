using DataLoader.Core.Abstractions;
using DataLoader.Core.Sources;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.CsvExample;

/// <summary>
/// Reads one CSV file via the filesystem driver and yields a <see cref="CsvRow"/>
/// per line. Intentionally minimal — uses a small built-in CSV parser rather
/// than pulling in a dependency. A production loader would use CsvHelper.
/// </summary>
public sealed class CsvSourceReader : ISourceReader<CsvFileWorkUnit, CsvRow>
{
    private readonly ICsvFileSystem _fs;
    private readonly CsvExampleSettings _settings;
    private readonly ILogger<CsvSourceReader> _logger;

    public CsvSourceReader(
        ICsvFileSystem fs,
        IOptions<CsvExampleSettings> settings,
        ILogger<CsvSourceReader> logger)
    {
        _fs = fs;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<CsvRow>> ReadAsync(CsvFileWorkUnit unit, CancellationToken cancellationToken)
    {
        var rows = new List<CsvRow>();
        await using var stream = await _fs.OpenReadAsync(unit.File.FullPath, cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        int lineNumber = 0;
        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
        {
            lineNumber++;
            if (lineNumber == 1 && _settings.HasHeaderRow) continue;
            if (string.IsNullOrWhiteSpace(line)) continue;

            rows.Add(new CsvRow
            {
                SourceFile = unit.File.Name,
                RowNumber = lineNumber,
                Cells = ParseCsvLine(line)
            });
        }
        _logger.LogDebug("Parsed {Rows} rows from {File}", rows.Count, unit.File.Name);
        return rows;
    }

    // Minimal RFC-4180-ish parser: handles quoted fields and escaped quotes.
    // Good enough for a demo; swap for CsvHelper in production.
    private static string[] ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                else if (c == '"') inQuotes = false;
                else current.Append(c);
            }
            else
            {
                if (c == ',') { fields.Add(current.ToString()); current.Clear(); }
                else if (c == '"') inQuotes = true;
                else current.Append(c);
            }
        }
        fields.Add(current.ToString());
        return fields.ToArray();
    }
}
