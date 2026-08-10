using System.Globalization;
using System.Net;
using System.Text;
using DataLoader.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace DataLoader.StormVista;

/// <summary>
/// Shared HTTP + FileLog plumbing for the two StormVista CSV feeds. Implements
/// <see cref="ISourceReader{TUnit,TRow}"/> directly (NOT via
/// <c>HttpJsonSourceReaderBase</c>, which would <c>EnsureSuccessStatusCode</c> +
/// JSON-deserialize).
///
/// <para>Per-request load flow (design):</para>
/// <list type="number">
///   <item>Build the sanitized path and the absolute URI (<c>?apikey=</c>).</item>
///   <item>GET. Determine the outcome and parse the leaf rows:
///     404 → <c>NotAvailable</c>, 0 rows; 2xx → parse CSV (n leaf rows), <c>Success</c>;
///     any other non-success → throw.</item>
///   <item>Upsert the mandatory <c>dbo.FileLog</c> hub row (natural keys + status +
///     HttpStatus + <c>RowCount = n</c>) and read back its <c>FileLogId</c>.</item>
///   <item>Stamp that <c>FileLogId</c> onto every leaf row and return them
///     (404 / header-only → return empty AFTER the FileLog upsert).</item>
/// </list>
/// On a non-cancellation failure the catch upserts a <c>Failed</c> FileLog row
/// (HttpStatus if known, RowCount 0) then rethrows so the pipeline records a
/// LoadLog failure. <see cref="OperationCanceledException"/> is rethrown WITHOUT a
/// FileLog write. The api key is never logged: only the sanitized path (no query
/// string) appears in logs and in the FileLog <c>RequestPath</c>.
/// </summary>
public abstract class StormVistaSourceReaderBase<TUnit, TRow> : ISourceReader<TUnit, TRow>
    where TUnit : WorkUnit, IStormVistaAuditContext
    where TRow : IStormVistaFactRow
{
    private readonly HttpClient _http;
    private readonly StormVistaSettings _settings;
    private readonly IStormVistaFileLog _fileLog;

    protected StormVistaSourceReaderBase(HttpClient http, StormVistaSettings settings, IStormVistaFileLog fileLog, ILogger logger)
    {
        _http = http;
        _settings = settings;
        _fileLog = fileLog;
        Logger = logger;
    }

    protected ILogger Logger { get; }

    /// <summary>Endpoint name written to <c>dbo.Endpoint</c> and used in logs — "Daily" / "Regional".</summary>
    protected abstract string Feed { get; }

    /// <summary>The resource path (no host, no query) that identifies this unit's CSV.</summary>
    protected abstract string BuildRelativePath(TUnit unit);

    /// <summary>Parses a 200-response CSV body into leaf rows (feed-specific, no FileLogId yet).</summary>
    protected abstract Task<IReadOnlyList<TRow>> ParseAsync(TUnit unit, string csv, CancellationToken cancellationToken);

    public async Task<IReadOnlyList<TRow>> ReadAsync(TUnit unit, CancellationToken cancellationToken)
    {
        var relativePath = BuildRelativePath(unit);
        var requestUri = new Uri(
            $"{_settings.BaseUrl.TrimEnd('/')}{relativePath}?apikey={Uri.EscapeDataString(_settings.ApiKey)}",
            UriKind.Absolute);

        IStormVistaAuditContext a = unit;
        var file = new StormVistaFileContext(Feed, a.Model, a.Cycle, a.WddType, a.RegionSetCode, a.InitDate);

        int? httpStatus = null;
        try
        {
            Logger.LogDebug("[{Feed}] GET {Path}", Feed, relativePath); // sanitized — no query/apikey
            using var response = await _http.GetAsync(requestUri, cancellationToken).ConfigureAwait(false);
            httpStatus = (int)response.StatusCode;

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                Logger.LogDebug("[{Feed}] 404 not available: {Path}", Feed, relativePath);
                // FileLog is mandatory even for 404 — record NotAvailable, then no facts.
                await _fileLog.UpsertAsync(file, "NotAvailable", httpStatus, relativePath, 0, cancellationToken).ConfigureAwait(false);
                return Array.Empty<TRow>();
            }

            if (!response.IsSuccessStatusCode)
            {
                // 401/403 (auth), or 429/5xx after retries are exhausted — loud.
                throw new HttpRequestException($"[{Feed}] {relativePath} returned HTTP {httpStatus}");
            }

            var csv = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var rows = await ParseAsync(unit, csv, cancellationToken).ConfigureAwait(false);

            // Upsert the hub row (RowCount = number of leaf/fact rows produced), get its id,
            // then stamp that id onto every row so the sink can bulk-merge by FileLogId.
            var fileLogId = await _fileLog.UpsertAsync(file, "Success", httpStatus, relativePath, rows.Count, cancellationToken).ConfigureAwait(false);
            foreach (var r in rows) r.FileLogId = fileLogId;

            Logger.LogDebug("[{Feed}] {Path} → {Rows} rows (FileLog #{Id})", Feed, relativePath, rows.Count, fileLogId);
            return rows;
        }
        catch (OperationCanceledException)
        {
            // Per-unit timeout / run cancellation — the LoadLog already records this;
            // don't attempt a FileLog write on a spent token (keep the prior behaviour).
            throw;
        }
        catch (Exception)
        {
            await TryUpsertFailedAsync(file, relativePath, httpStatus).ConfigureAwait(false);
            throw; // LoaderPipelineBase logs + records the LoadLog failure and moves on.
        }
    }

    private async Task TryUpsertFailedAsync(StormVistaFileContext file, string relativePath, int? httpStatus)
    {
        try
        {
            // Best-effort; use None so the Failed row is still written even when the
            // failure was a timeout, and never mask the original exception.
            await _fileLog.UpsertAsync(file, "Failed", httpStatus, relativePath, 0, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[{Feed}] failed to write 'Failed' FileLog for {Path}", Feed, relativePath);
        }
    }
}

/// <summary>
/// Daily national feed. GETs <c>/model-data/{model}/{date}/{cycle}z/wdd/{type}-daily.csv</c>
/// and parses the 3-column CSV to <see cref="DailyWddRow"/> (design §8.1). Bad
/// date / value / flag rows are skipped with a warning (tolerant per-row parse).
/// </summary>
public sealed class DailySourceReader : StormVistaSourceReaderBase<DailyWorkUnit, DailyWddRow>
{
    public DailySourceReader(HttpClient http, StormVistaSettings settings, IStormVistaFileLog fileLog, ILogger<DailySourceReader> logger)
        : base(http, settings, fileLog, logger)
    {
    }

    protected override string Feed => "Daily";

    protected override string BuildRelativePath(DailyWorkUnit unit) =>
        $"/model-data/{unit.Model}/{Ymd(unit.InitDate)}/{unit.Cycle}z/wdd/{unit.WddType}-daily.csv";

    protected override Task<IReadOnlyList<DailyWddRow>> ParseAsync(DailyWorkUnit unit, string csv, CancellationToken cancellationToken) =>
        Task.FromResult(Parse(unit, csv));

    private IReadOnlyList<DailyWddRow> Parse(DailyWorkUnit unit, string csv)
    {
        var records = StormVistaCsv.Parse(csv);
        if (records.Count <= 1)
        {
            Logger.LogWarning("[Daily] {Unit}: CSV has no data rows", unit.DisplayName);
            return Array.Empty<DailyWddRow>();
        }

        var rows = new List<DailyWddRow>(records.Count - 1);
        for (var i = 1; i < records.Count; i++) // records[0] is the header row
        {
            var lineNumber = i + 1;
            var cells = records[i];
            if (cells.Length < 3)
            {
                Logger.LogWarning("[Daily] {Unit} line {Line}: {Count} columns (need ≥ 3); skipping", unit.DisplayName, lineNumber, cells.Length);
                continue;
            }

            if (!StormVistaCsv.TryParseDate(cells[0], out var validDate))
            {
                Logger.LogWarning("[Daily] {Unit} line {Line}: unparseable date '{Date}'; skipping", unit.DisplayName, lineNumber, cells[0]);
                continue;
            }

            if (!StormVistaCsv.TryParseDecimal(cells[1], out var value))
            {
                Logger.LogWarning("[Daily] {Unit} line {Line}: blank/unparseable value '{Value}'; skipping", unit.DisplayName, lineNumber, cells[1]);
                continue;
            }

            if (!StormVistaCsv.TryParseFlag(cells[2], out var flag))
            {
                Logger.LogWarning("[Daily] {Unit} line {Line}: flag '{Flag}' not in {{0,1,2}}; skipping", unit.DisplayName, lineNumber, cells[2]);
                continue;
            }

            rows.Add(new DailyWddRow
            {
                ValidDate = validDate,
                FlagCode = flag,
                Value = value
            });
        }

        return rows;
    }

    private static string Ymd(DateOnly d) => d.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
}

/// <summary>
/// Regional/weekly feed. GETs <c>/model-data/{wkmodel}/{date}/{cycle}z/wdd/{type}_reg{code}.csv</c>,
/// validates the wide header against the seeded region set (fail the unit loudly on an
/// unknown/extra or duplicate column; TOLERATE missing seeded regions — region sets grow
/// over time, e.g. ISO 18→21, so historical files carry a subset and we load the columns
/// that are present), then unpivots wide → long into
/// <see cref="RegionalWddRow"/> (design §8.2). Bad cells are skipped with a
/// warning (tolerant per-cell parse); a bad row date skips the whole row.
/// </summary>
public sealed class RegionalSourceReader : StormVistaSourceReaderBase<RegionalWorkUnit, RegionalWddRow>
{
    private readonly IStormVistaReferenceProvider _reference;

    public RegionalSourceReader(
        HttpClient http, StormVistaSettings settings, IStormVistaFileLog fileLog,
        IStormVistaReferenceProvider reference, ILogger<RegionalSourceReader> logger)
        : base(http, settings, fileLog, logger)
    {
        _reference = reference;
    }

    protected override string Feed => "Regional";

    protected override string BuildRelativePath(RegionalWorkUnit unit) =>
        $"/model-data/{unit.WkModel}/{Ymd(unit.InitDate)}/{unit.Cycle}z/wdd/{unit.WddType}_reg{unit.RegionSetCode}.csv";

    protected override async Task<IReadOnlyList<RegionalWddRow>> ParseAsync(RegionalWorkUnit unit, string csv, CancellationToken cancellationToken)
    {
        var reference = await _reference.GetAsync(cancellationToken).ConfigureAwait(false);
        var expected = reference.RegionsFor(unit.RegionSetCode);
        return Parse(unit, csv, expected);
    }

    private IReadOnlyList<RegionalWddRow> Parse(RegionalWorkUnit unit, string csv, IReadOnlyList<string> expectedRegions)
    {
        var records = StormVistaCsv.Parse(csv);
        if (records.Count == 0)
        {
            Logger.LogWarning("[Regional] {Unit}: empty CSV", unit.DisplayName);
            return Array.Empty<RegionalWddRow>();
        }

        if (records.Count == 1)
        {
            // Header present but no data rows — mirror the Daily path's warning (L6).
            Logger.LogWarning("[Regional] {Unit}: CSV has no data rows", unit.DisplayName);
            return Array.Empty<RegionalWddRow>();
        }

        // --- header validation (§8.2 steps 1–3): fail loudly on unknown/extra or duplicate
        // columns; tolerate missing seeded regions (region sets grow over time). ---
        var header = records[0];
        if (header.Length < 2 || !string.Equals(header[0].Trim(), "Date", StringComparison.OrdinalIgnoreCase))
            throw new FormatException(
                $"Regional {unit.DisplayName}: header column 0 must be 'Date' (got '{(header.Length > 0 ? header[0] : string.Empty)}')");

        if (expectedRegions.Count == 0)
            throw new InvalidOperationException(
                $"Regional {unit.DisplayName}: no seeded regions for set 'reg{unit.RegionSetCode}' — reseed dbo.Region.");

        var actual = header.Skip(1).Select(h => h.Trim()).ToList();

        var duplicates = actual.GroupBy(a => a, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicates.Count > 0)
            throw new FormatException(
                $"Regional {unit.DisplayName}: duplicate region column(s) [{string.Join(", ", duplicates)}] in reg{unit.RegionSetCode}");

        var expectedSet = new HashSet<string>(expectedRegions, StringComparer.Ordinal);
        var actualSet = new HashSet<string>(actual, StringComparer.Ordinal);

        // Unknown/extra columns are fatal: StormVista added a region we don't know about.
        // It can't be mapped to a RegionId, and dbo.Region needs a reseed before it can load.
        var extra = actual.Where(a => !expectedSet.Contains(a)).ToList();
        if (extra.Count > 0)
            throw new FormatException(
                $"Regional {unit.DisplayName}: unknown region column(s) [{string.Join(", ", extra)}] " +
                $"not in the seeded set for reg{unit.RegionSetCode} — reseed dbo.Region.");

        // Missing seeded regions are NOT fatal: region sets grow over time (e.g. ISO 18→21),
        // so a historical file legitimately carries a subset. Load the columns that ARE present.
        var missing = expectedRegions.Where(e => !actualSet.Contains(e)).ToList();
        if (missing.Count > 0)
            Logger.LogInformation(
                "[Regional] {Unit}: {Present}/{Expected} regions present for reg{Code} (file predates {Count} region(s): [{Missing}]); loading present columns",
                unit.DisplayName, actual.Count, expectedRegions.Count, unit.RegionSetCode, missing.Count, string.Join(", ", missing));

        // Reorder warning: compare against the seeded order RESTRICTED to present regions,
        // otherwise a legitimately-missing region would spuriously trip this every time.
        var expectedPresentOrder = expectedRegions.Where(e => actualSet.Contains(e)).ToList();
        if (!actual.SequenceEqual(expectedPresentOrder, StringComparer.Ordinal))
            Logger.LogWarning("[Regional] {Unit}: region columns are reordered vs the seed (set matches, order differs)", unit.DisplayName);

        // Column index (1-based over the whole header) → region name.
        var regionByColumn = actual; // regionByColumn[j] is the region for data column (j+1)

        // --- unpivot data rows (§8.2 step 4) ---
        var rows = new List<RegionalWddRow>((records.Count - 1) * regionByColumn.Count);
        for (var i = 1; i < records.Count; i++)
        {
            var lineNumber = i + 1;
            var cells = records[i];
            if (cells.Length < 1 || !StormVistaCsv.TryParseDate(cells[0], out var validDate))
            {
                Logger.LogWarning("[Regional] {Unit} line {Line}: unparseable date '{Date}'; skipping row",
                    unit.DisplayName, lineNumber, cells.Length > 0 ? cells[0] : string.Empty);
                continue;
            }

            for (var col = 0; col < regionByColumn.Count; col++)
            {
                var cellIndex = col + 1; // column 0 is Date
                var raw = cellIndex < cells.Length ? cells[cellIndex] : string.Empty;
                if (!StormVistaCsv.TryParseDecimal(raw, out var value))
                {
                    Logger.LogWarning("[Regional] {Unit} line {Line} region '{Region}': blank/unparseable value '{Value}'; skipping cell",
                        unit.DisplayName, lineNumber, regionByColumn[col], raw);
                    continue;
                }

                rows.Add(new RegionalWddRow
                {
                    RegionName = regionByColumn[col],
                    ValidDate = validDate,
                    Value = value
                });
            }
        }

        return rows;
    }

    private static string Ymd(DateOnly d) => d.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
}

/// <summary>
/// Minimal quote-aware RFC-4180 CSV parser (same approach as the Platts
/// <c>SymbolSourceReader.ParseCsv</c>) plus the small invariant-culture value
/// parsers used by both feeds. The daily flag header is quoted
/// (<c>"Flag (0=obs 1=fcst 2=norm)"</c>), so a quote-aware split is required.
/// </summary>
internal static class StormVistaCsv
{
    public static bool TryParseDate(string? value, out DateOnly date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        return DateOnly.TryParseExact(value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    public static bool TryParseDecimal(string? value, out decimal result)
    {
        result = 0m;
        if (string.IsNullOrWhiteSpace(value)) return false;
        return decimal.TryParse(value.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out result);
    }

    public static bool TryParseFlag(string? value, out int flag)
    {
        flag = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (!int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out flag)) return false;
        return flag is 0 or 1 or 2;
    }

    /// <summary>
    /// Splits CSV content into records of fields, honouring quoted fields with
    /// embedded commas, doubled <c>""</c> escapes and embedded CR/LF. Fully-empty
    /// lines are dropped. No external dependency.
    /// </summary>
    public static IReadOnlyList<string[]> Parse(string content)
    {
        // Strip a single leading UTF-8 BOM if present — Trim() does not remove
        // U+FEFF, and a BOM-prefixed body would otherwise defeat the regional
        // header[0] == "Date" check.
        if (content.Length > 0 && content[0] == '﻿')
            content = content.Substring(1);

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
            if (!(fields.Count == 1 && fields[0].Length == 0)) // drop blank lines
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
                        i++; // consume LF of a CRLF pair
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

        if (field.Length > 0 || fields.Count > 0)
            EndRecord(); // flush a trailing record not newline-terminated

        return records;
    }
}
