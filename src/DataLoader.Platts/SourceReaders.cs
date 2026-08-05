using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DataLoader.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.Platts;

/// <summary>
/// Parses one <c>.ftp</c> market file into <see cref="SymbolDataRow"/> rows.
/// Emits target rows directly (the pipeline uses an identity transformer).
/// </summary>
public sealed class SymbolDataSourceReader : ISourceReader<SymbolDataWorkUnit, SymbolDataRow>
{
    private const string TimestampFormat = "yyyyMMddHHmm";
    private const string DateFormat = "yyyyMMdd";

    private readonly IPlattsSftp _sftp;
    private readonly ILogger<SymbolDataSourceReader> _logger;

    public SymbolDataSourceReader(IPlattsSftp sftp, ILogger<SymbolDataSourceReader> logger)
    {
        _sftp = sftp;
        _logger = logger;
    }

    public async Task<IReadOnlyList<SymbolDataRow>> ReadAsync(SymbolDataWorkUnit unit, CancellationToken cancellationToken)
    {
        var lines = await ReadLinesAsync(unit, cancellationToken).ConfigureAwait(false);
        if (lines.Count < 2)
        {
            _logger.LogWarning("Platts .ftp {File} has fewer than 2 lines; nothing to parse", unit.DisplayName);
            return Array.Empty<SymbolDataRow>();
        }

        // Line[0] = copyright banner (skipped). Line[1] = header. Lines[2+] = data.
        var (mdc, actionDate) = ParseHeader(lines[1], unit);

        var rows = new List<SymbolDataRow>();
        for (var i = 2; i < lines.Count; i++)
        {
            var lineNumber = i + 1; // 1-based for diagnostics
            var row = ParseDataLine(lines[i], mdc, actionDate, unit, lineNumber);
            if (row is not null)
                rows.Add(row);
        }

        _logger.LogDebug("Platts .ftp {File} → {Rows} rows", unit.DisplayName, rows.Count);
        return rows;
    }

    private async Task<List<string>> ReadLinesAsync(SymbolDataWorkUnit unit, CancellationToken ct)
    {
        await using var stream = await _sftp.OpenReadAsync(unit.File.FullPath, ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var lines = new List<string>();
        string? line;
        while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
            lines.Add(line);
        return lines;
    }

    /// <summary>
    /// Parses the header: <c>ActionDate = tokens[1]</c>, <c>MDC = tokens[^2]</c>,
    /// file date = <c>tokens[^1]</c> (validated against the folder, not stored).
    /// </summary>
    private (string Mdc, DateTime ActionDate) ParseHeader(string header, SymbolDataWorkUnit unit)
    {
        var tokens = Regex.Split(header.Trim(), @"\s+");
        if (tokens.Length < 4)
            throw new FormatException(
                $"Platts .ftp header for {unit.DisplayName} has {tokens.Length} tokens (need >= 4): '{header}'");

        var actionDate = DateTime.ParseExact(tokens[1], TimestampFormat, CultureInfo.InvariantCulture);
        var mdc = tokens[^2];
        var fileDate = DateTime.ParseExact(tokens[^1], DateFormat, CultureInfo.InvariantCulture);

        // Validate the header's file date against the folder name (warn only, never throw).
        if (DateTime.TryParseExact(unit.Folder, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var folderDate))
        {
            if (folderDate.Date != fileDate.Date)
                _logger.LogWarning("Platts .ftp {File}: header file date {FileDate:yyyyMMdd} != folder {Folder}",
                    unit.DisplayName, fileDate, unit.Folder);
        }
        else
        {
            _logger.LogWarning("Platts .ftp {File}: folder name '{Folder}' is not {Format}; skipping date validation",
                unit.DisplayName, unit.Folder, DateFormat);
        }

        return (mdc, actionDate);
    }

    /// <summary>
    /// Parses one data line into a row, or returns null (with a warning) for a
    /// malformed line that should be skipped without failing the whole file.
    /// </summary>
    private SymbolDataRow? ParseDataLine(string line, string mdc, DateTime actionDate, SymbolDataWorkUnit unit, int lineNumber)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        // Split on any run of whitespace → [Action, SymbolBate, Date, Value?].
        var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
        {
            _logger.LogWarning("Platts .ftp {File} line {Line}: {Count} tokens (need >= 3); skipping",
                unit.DisplayName, lineNumber, parts.Length);
            return null;
        }

        var action = parts[0];
        var symbolBate = parts[1];
        if (symbolBate.Length < 2)
        {
            _logger.LogWarning("Platts .ftp {File} line {Line}: symbol/bate token '{Token}' too short; skipping",
                unit.DisplayName, lineNumber, symbolBate);
            return null;
        }

        var bate = symbolBate[^1].ToString();
        var symbol = symbolBate[..^1];

        if (!DateTime.TryParseExact(parts[2], TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            _logger.LogWarning("Platts .ftp {File} line {Line}: unparseable date '{Date}'; skipping",
                unit.DisplayName, lineNumber, parts[2]);
            return null;
        }

        decimal? value = null;
        if (parts.Length >= 4 && !string.IsNullOrWhiteSpace(parts[3]))
        {
            if (decimal.TryParse(parts[3], NumberStyles.Number, CultureInfo.InvariantCulture, out var parsedValue))
            {
                value = parsedValue;
            }
            else
            {
                _logger.LogWarning("Platts .ftp {File} line {Line}: unparseable value '{Value}'; skipping",
                    unit.DisplayName, lineNumber, parts[3]);
                return null;
            }
        }

        return new SymbolDataRow
        {
            MDC = mdc,
            Symbol = symbol,
            Bate = bate,
            Date = date,
            Action = action,
            Value = value,
            ActionDate = actionDate,
            SourcePath = $"{unit.Folder}\\{unit.File.Name}",
            FileName = unit.File.Name,
            LastModifiedUtc = unit.File.LastModifiedUtc,
            SizeBytes = unit.File.Size
        };
    }
}

/// <summary>
/// Parses one reference-metadata CSV into <see cref="SymbolRow"/> rows. Uses a
/// small quote-aware RFC-4180 splitter (Description may contain commas and
/// doubled <c>""</c> escapes), maps the 14 columns by position, and emits target
/// rows directly.
/// </summary>
public sealed class SymbolSourceReader : ISourceReader<SymbolWorkUnit, SymbolRow>
{
    private static readonly string[] UsDateFormats = { "M/d/yyyy", "MM/dd/yyyy" };
    private static readonly CultureInfo UsDateCulture = CultureInfo.GetCultureInfo("en-US");

    private readonly IPlattsSftp _sftp;
    private readonly PlattsSettings _settings;
    private readonly ILogger<SymbolSourceReader> _logger;

    public SymbolSourceReader(IPlattsSftp sftp, IOptions<PlattsSettings> settings, ILogger<SymbolSourceReader> logger)
    {
        _sftp = sftp;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<SymbolRow>> ReadAsync(SymbolWorkUnit unit, CancellationToken cancellationToken)
    {
        string content;
        await using (var stream = await _sftp.OpenReadAsync(unit.File.FullPath, cancellationToken).ConfigureAwait(false))
        using (var reader = new StreamReader(stream, Encoding.UTF8))
        {
            content = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }

        var records = ParseCsv(content);
        if (records.Count <= 1)
        {
            _logger.LogWarning("Platts symbol CSV {File} has no data rows", unit.File.Name);
            return Array.Empty<SymbolRow>();
        }

        var sourcePath = $"{_settings.SymbolsDirectory}\\{unit.File.Name}";
        var rows = new List<SymbolRow>();

        // records[0] is the header row → skipped.
        for (var r = 1; r < records.Count; r++)
        {
            var cells = records[r];
            var symbol = Cell(cells, 2);
            if (string.IsNullOrWhiteSpace(symbol))
            {
                _logger.LogWarning("Platts symbol CSV {File} row {Row}: missing Symbol; skipping", unit.File.Name, r + 1);
                continue;
            }

            rows.Add(new SymbolRow
            {
                MDC = Cell(cells, 0),
                Trans = Cell(cells, 1),
                Symbol = symbol!,
                Bates = Cell(cells, 3),
                Freq = Cell(cells, 4),
                Curr = Cell(cells, 5),
                UOM = Cell(cells, 6),
                Dec = ParseInt(Cell(cells, 7)),
                Conv = ParseDecimal(Cell(cells, 8)),
                Flag = Cell(cells, 9),
                ToUom = Cell(cells, 10),
                Earliest = ParseUsDate(Cell(cells, 11)),
                Latest = ParseUsDate(Cell(cells, 12)),
                Description = Cell(cells, 13),
                SourcePath = sourcePath,
                FileName = unit.File.Name,
                LastModifiedUtc = unit.File.LastModifiedUtc,
                SizeBytes = unit.File.Size
            });
        }

        _logger.LogDebug("Platts symbol CSV {File} → {Rows} rows", unit.File.Name, rows.Count);
        return rows;
    }

    /// <summary>Trimmed cell at <paramref name="index"/>, or null when absent/blank.</summary>
    private static string? Cell(string[] cells, int index) =>
        index < cells.Length ? Blank(cells[index].Trim()) : null;

    private static string? Blank(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static int? ParseInt(string? value) =>
        value is not null && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static decimal? ParseDecimal(string? value) =>
        value is not null && decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static DateTime? ParseUsDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        return DateTime.TryParseExact(value, UsDateFormats, UsDateCulture, DateTimeStyles.None, out var d) ? d : null;
    }

    /// <summary>
    /// Minimal RFC-4180 parser: quote-aware fields with embedded commas, doubled
    /// <c>""</c> escapes, and embedded CR/LF inside quotes. Fully-empty lines are
    /// dropped. No external dependency.
    /// </summary>
    internal static IReadOnlyList<string[]> ParseCsv(string content)
    {
        var records = new List<string[]>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        void EndField()
        {
            fields.Add(field.ToString());
            field.Clear();
        }

        void EndRecord()
        {
            EndField();
            // Drop blank lines (a single empty field).
            if (!(fields.Count == 1 && fields[0].Length == 0))
                records.Add(fields.ToArray());
            fields.Clear();
        }

        for (var i = 0; i < content.Length; i++)
        {
            var c = content[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < content.Length && content[i + 1] == '"')
                    {
                        field.Append('"'); // doubled quote → literal quote
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(c);
                }
                continue;
            }

            switch (c)
            {
                case '"':
                    inQuotes = true;
                    break;
                case ',':
                    EndField();
                    break;
                case '\r':
                    if (i + 1 < content.Length && content[i + 1] == '\n')
                        i++; // consume the LF of a CRLF pair
                    EndRecord();
                    break;
                case '\n':
                    EndRecord();
                    break;
                default:
                    field.Append(c);
                    break;
            }
        }

        // Flush a trailing record that isn't newline-terminated.
        if (field.Length > 0 || fields.Count > 0)
            EndRecord();

        return records;
    }
}
