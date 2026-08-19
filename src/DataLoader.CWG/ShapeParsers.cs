using Microsoft.Extensions.Logging;

namespace DataLoader.CWG;

// =============================================================================
// Shape intermediates (design §4). A shape parser turns raw CSV into a list of
// one of these typed records; the per-endpoint row factory then maps each to a
// TRow? (null drops the record). Region/InitDate come from the work unit, not
// the CSV, for shapes A/C/D.
// =============================================================================

/// <summary>Shape A — one raw data row (positional fields + the header for optional checks).</summary>
public sealed record CwgTabularRecord(string[] Fields, string[] Header);

/// <summary>Shape B — one unpivoted (key, region, value) cell.</summary>
public sealed record CwgUnpivotCell(string KeyCell, string Region, string RawValue);

/// <summary>Shape C — one unpivoted (forecast-date × hour) matrix cell.</summary>
public sealed record CwgMatrixCell(DateOnly ForecastDate, string HourLabel, int HourOfDay, string RawValue);

/// <summary>Shape D — one unpivoted matrix cell tagged with its block's sub-region.</summary>
public sealed record CwgSubRegionCell(string SubRegion, DateOnly ForecastDate, string HourLabel, int HourOfDay, string RawValue);

/// <summary>Shape E — one region row within a block (raw numeric cells kept as strings).</summary>
public sealed record CwgCapacityRow(string Block, string Region, string TotalCapacityRaw, string Avg1_5Raw, string Avg6_10Raw, string Avg11_15Raw);

/// <summary>
/// A pure shape parser: <c>Parse(descriptor, unit, csvText, log) → records</c>.
/// Emits no title/footer rows — those are dropped here so the row factory only
/// ever sees data records. Stateless; one shared instance per shape.
/// </summary>
public interface ICwgShapeParser<TRecord>
{
    IReadOnlyList<TRecord> Parse(CwgEndpointDescriptor descriptor, CwgWorkUnit unit, string csvText, ILogger logger);
}

/// <summary>Shared singleton shape parsers (design §1.3).</summary>
internal static class CwgShapeParsers
{
    public static readonly ShapeAParser A = new();
    public static readonly ShapeBParser B = new();
    public static readonly ShapeCParser C = new();
    public static readonly ShapeDParser D = new();
    public static readonly ShapeEParser E = new();
}

/// <summary>
/// Shared helper for shapes C/D: parse a <c>Date (EST),&lt;dates&gt;</c> header row
/// into positional forecast dates. Reads the width dynamically from the run of
/// leading non-empty date cells (so 16 vs 14 vs 15 fall out naturally); a
/// trailing empty cell (from a trailing comma) ends the run and is excluded.
/// </summary>
internal static class CwgMatrix
{
    public static List<DateOnly?> ParseDateHeader(string[] header, CwgEndpointDescriptor d, CwgWorkUnit unit, ILogger log)
    {
        var dates = new List<DateOnly?>();
        for (var c = 1; c < header.Length; c++)
        {
            var cell = header[c].Trim();
            if (cell.Length == 0) break; // trailing empties end the run
            if (CwgParse.DateMdyyyy(cell, out var dt))
            {
                dates.Add(dt);
            }
            else
            {
                log.LogWarning("[CWG {Endpoint}] {File}: unparseable forecast date '{Cell}' in 'Date (EST)' header",
                    d.EndpointId, unit.Filename, cell);
                dates.Add(null);
            }
        }
        return dates;
    }
}

/// <summary>Shape A — simple tabular. One <see cref="CwgTabularRecord"/> per data row.</summary>
public sealed class ShapeAParser : ICwgShapeParser<CwgTabularRecord>
{
    public IReadOnlyList<CwgTabularRecord> Parse(CwgEndpointDescriptor d, CwgWorkUnit unit, string csvText, ILogger log)
    {
        var records = CwgCsv.Parse(csvText);
        if (records.Count <= 1)
        {
            log.LogWarning("[CWG {Endpoint}] {File}: CSV has no data rows", d.EndpointId, unit.Filename);
            return Array.Empty<CwgTabularRecord>();
        }

        var header = records[0];
        // Guard on the DOCUMENTED column count when the descriptor carries one, so a stray
        // trailing comma on the header row (which would inflate header.Length) does not drop
        // every data row (Fix 3). Falls back to the header width when unspecified.
        var n = d.ExpectedColumns ?? header.Length;
        var rows = new List<CwgTabularRecord>(records.Count - 1);
        for (var i = 1; i < records.Count; i++)
        {
            var cells = records[i];
            // Skip the trailing sentinel row emitted by the `wdd` family (national + 5region +
            // 9region + iso): a literal `END.` in the first cell (design §4). Placed AHEAD of the
            // short-row guard so the bare `END.` row does not log a spurious width Warning. Purely
            // additive — the non-`wdd` Shape-A endpoints never emit an `END.` row.
            if (cells.Length > 0 && cells[0].Trim() == "END.")
            {
                log.LogDebug("[CWG {Endpoint}] {File} line {Line}: skipping 'END.' footer row",
                    d.EndpointId, unit.Filename, i + 1);
                continue;
            }
            if (cells.Length < n)
            {
                if (!d.AllowShortRows)
                {
                    log.LogWarning("[CWG {Endpoint}] {File} line {Line}: {Count} columns (need ≥ {N}); skipping",
                        d.EndpointId, unit.Filename, i + 1, cells.Length, n);
                    continue;
                }
                // Station tolerates truncated rows: keep the row; the factory reads missing fields as NULL.
                log.LogDebug("[CWG {Endpoint}] {File} line {Line}: {Count} columns (< {N}); missing fields → NULL",
                    d.EndpointId, unit.Filename, i + 1, cells.Length, n);
            }
            rows.Add(new CwgTabularRecord(cells, header));
        }
        return rows;
    }
}

/// <summary>
/// Shape B — wide-by-region → unpivot. Validates the region columns against the
/// descriptor's <c>WideRegionColumns</c> StormVista-style: fatal on an
/// unknown/extra column, tolerate missing (early history has only some regions),
/// warn on reordering; resolves region → column index by header name.
/// </summary>
public sealed class ShapeBParser : ICwgShapeParser<CwgUnpivotCell>
{
    public IReadOnlyList<CwgUnpivotCell> Parse(CwgEndpointDescriptor d, CwgWorkUnit unit, string csvText, ILogger log)
    {
        var records = CwgCsv.Parse(csvText);
        if (records.Count <= 1)
        {
            log.LogWarning("[CWG {Endpoint}] {File}: CSV has no data rows", d.EndpointId, unit.Filename);
            return Array.Empty<CwgUnpivotCell>();
        }

        var header = records[0];
        var keyCols = d.KeyColumns;
        var expected = d.WideRegionColumns ?? Array.Empty<string>();
        var expectedSet = new HashSet<string>(expected, StringComparer.Ordinal);

        // Present region columns as (name, header-column-index).
        var present = new List<(string Name, int Index)>();
        for (var c = keyCols; c < header.Length; c++)
        {
            var name = header[c].Trim();
            if (name.Length == 0) continue;
            if (!expectedSet.Contains(name))
                throw new FormatException(
                    $"CWG {d.EndpointId} {unit.Filename}: unknown region column '{name}' not in the expected set — update the descriptor.");
            present.Add((name, c));
        }

        var presentNames = present.Select(p => p.Name).ToList();
        var expectedPresentOrder = expected.Where(e => presentNames.Contains(e)).ToList();
        if (!presentNames.SequenceEqual(expectedPresentOrder, StringComparer.Ordinal))
            log.LogWarning("[CWG {Endpoint}] {File}: region columns are reordered vs the descriptor (set matches, order differs)",
                d.EndpointId, unit.Filename);

        var missing = expected.Where(e => !presentNames.Contains(e)).ToList();
        if (missing.Count > 0)
            log.LogInformation("[CWG {Endpoint}] {File}: {Present}/{Expected} regions present (missing [{Missing}]); loading present columns",
                d.EndpointId, unit.Filename, present.Count, expected.Length, string.Join(", ", missing));

        var cells = new List<CwgUnpivotCell>((records.Count - 1) * Math.Max(1, present.Count));
        for (var i = 1; i < records.Count; i++)
        {
            var row = records[i];
            if (row.Length == 0) continue;
            var keyCell = row[0]; // KeyColumns is always 1: DATE / UTC_HOUR_ENDING at index 0
            foreach (var (name, idx) in present)
            {
                var raw = idx < row.Length ? row[idx] : string.Empty;
                cells.Add(new CwgUnpivotCell(keyCell, name, raw));
            }
        }
        return cells;
    }
}

/// <summary>
/// Shape C — pivoted hour×forecast-day matrix → unpivot. Finds the
/// <c>Date (EST)</c> header (skipping any leading spacer row), reads the forecast
/// dates dynamically, then emits one cell per (hour × forecast-date). Non-hour
/// leading cells (footers, blanks) are skipped.
/// </summary>
public sealed class ShapeCParser : ICwgShapeParser<CwgMatrixCell>
{
    public IReadOnlyList<CwgMatrixCell> Parse(CwgEndpointDescriptor d, CwgWorkUnit unit, string csvText, ILogger log)
    {
        var records = CwgCsv.Parse(csvText);

        var headerIdx = -1;
        for (var i = 0; i < records.Count; i++)
        {
            var r = records[i];
            if (r.Length > 0 && r[0].Trim() == "Date (EST)") { headerIdx = i; break; }
        }
        if (headerIdx < 0)
        {
            log.LogWarning("[CWG {Endpoint}] {File}: no 'Date (EST)' header row found", d.EndpointId, unit.Filename);
            return Array.Empty<CwgMatrixCell>();
        }

        var dates = CwgMatrix.ParseDateHeader(records[headerIdx], d, unit, log);
        var width = dates.Count;
        if (width == 0)
        {
            log.LogWarning("[CWG {Endpoint}] {File}: 'Date (EST)' header has no forecast dates", d.EndpointId, unit.Filename);
            return Array.Empty<CwgMatrixCell>();
        }

        var cells = new List<CwgMatrixCell>();
        for (var i = headerIdx + 1; i < records.Count; i++)
        {
            var row = records[i];
            if (row.Length == 0) continue;
            var label = row[0].Trim();
            if (label.Length == 0) continue;

            var hourIdx = CwgHours.IndexOf(label);
            if (hourIdx < 0)
            {
                // 'sum change for the day' / 'average change for the day' footers, or a
                // stray non-hour leading cell — drop it (§4). Debug, not warn, to avoid noise.
                log.LogDebug("[CWG {Endpoint}] {File}: skipping non-hour row '{Label}'", d.EndpointId, unit.Filename, label);
                continue;
            }

            for (var k = 0; k < width; k++)
            {
                if (dates[k] is not { } fdate) continue;
                var raw = (k + 1) < row.Length ? row[k + 1] : string.Empty;
                cells.Add(new CwgMatrixCell(fdate, CwgHours.Labels[hourIdx], hourIdx, raw));
            }
        }
        return cells;
    }
}

/// <summary>
/// Shape D — stacked sub-region matrix blocks → unpivot per block. A block starts
/// at a <c>&lt;SubRegion&gt; region,,,,,</c> label row, followed by a
/// <c>Date (EST)</c> header (dynamic width) and 24 hour rows. Blank separator
/// rows between blocks need no counting — a new label row starts the next block.
/// </summary>
public sealed class ShapeDParser : ICwgShapeParser<CwgSubRegionCell>
{
    private const string RegionSuffix = " region";

    public IReadOnlyList<CwgSubRegionCell> Parse(CwgEndpointDescriptor d, CwgWorkUnit unit, string csvText, ILogger log)
    {
        var records = CwgCsv.Parse(csvText);
        var cells = new List<CwgSubRegionCell>();

        string? subRegion = null;
        List<DateOnly?>? dates = null;
        var width = 0;

        foreach (var row in records)
        {
            if (row.Length == 0) continue;
            var first = row[0].Trim();
            if (first.Length == 0) continue; // blank separator

            if (IsLabelRow(row, first, out var sr))
            {
                subRegion = sr;
                dates = null;
                width = 0;
                continue;
            }

            if (first == "Date (EST)")
            {
                dates = CwgMatrix.ParseDateHeader(row, d, unit, log);
                width = dates.Count;
                continue;
            }

            var hourIdx = CwgHours.IndexOf(first);
            if (hourIdx < 0 || subRegion is null || dates is null) continue;

            for (var k = 0; k < width; k++)
            {
                if (dates[k] is not { } fdate) continue;
                var raw = (k + 1) < row.Length ? row[k + 1] : string.Empty;
                cells.Add(new CwgSubRegionCell(subRegion, fdate, CwgHours.Labels[hourIdx], hourIdx, raw));
            }
        }
        return cells;
    }

    /// <summary>A label row: cell[0] ends with " region" (case-insensitive) and the rest are empty.</summary>
    private static bool IsLabelRow(string[] row, string first, out string subRegion)
    {
        subRegion = string.Empty;
        if (!first.EndsWith(RegionSuffix, StringComparison.OrdinalIgnoreCase)) return false;
        for (var c = 1; c < row.Length; c++)
            if (row[c].Trim().Length != 0) return false;
        subRegion = first.Substring(0, first.Length - RegionSuffix.Length).Trim();
        return subRegion.Length > 0;
    }
}

/// <summary>
/// Shape E — region-row summary, single- or multi-block. Title rows set the
/// current block (Current / Yesterday / Change); header rows (empty cell[0] +
/// "Total Capacity (MW)") and footer rows (Total All (MW) / Total Percent /
/// Total Change) and blanks are skipped; everything else is a region row.
/// </summary>
public sealed class ShapeEParser : ICwgShapeParser<CwgCapacityRow>
{
    public IReadOnlyList<CwgCapacityRow> Parse(CwgEndpointDescriptor d, CwgWorkUnit unit, string csvText, ILogger log)
    {
        var records = CwgCsv.Parse(csvText);
        var rows = new List<CwgCapacityRow>();
        var blocks = new HashSet<string>(StringComparer.Ordinal);
        string? block = null;

        foreach (var row in records)
        {
            if (row.Length == 0) continue;
            var t0 = row[0].Trim();

            // Title rows drive the current block. The prior-forecast and Change titles carry
            // the actual weekday name (e.g. "Friday's Forecast" / "Change from Friday's
            // Forecast" on a Monday), so match by SHAPE, not a fixed "Yesterday". Order
            // matters: check Change BEFORE the generic "…Forecast" (a Change title also ends
            // with "Forecast"). The middle block always maps to the literal 'Yesterday' the
            // schema's Block CHECK expects, whatever weekday the title names.
            if (t0.EndsWith("Across All Regions", StringComparison.OrdinalIgnoreCase))
            {
                block = "Current"; blocks.Add(block); continue;
            }
            if (t0.StartsWith("Change from", StringComparison.OrdinalIgnoreCase))
            {
                block = "Change"; blocks.Add(block); continue;
            }
            if (t0.EndsWith("Forecast", StringComparison.OrdinalIgnoreCase))
            {
                block = "Yesterday"; blocks.Add(block); continue;
            }

            // Header row (empty region label) or a blank row → skip.
            if (t0.Length == 0) continue;

            // Footer rows → skip.
            if (t0.Equals("Total All (MW)", StringComparison.OrdinalIgnoreCase)
                || t0.Equals("Total Percent", StringComparison.OrdinalIgnoreCase)
                || t0.Equals("Total Change", StringComparison.OrdinalIgnoreCase))
                continue;

            // A region row before any title (shouldn't happen) — skip defensively.
            if (block is null)
            {
                log.LogDebug("[CWG {Endpoint}] {File}: region row '{Region}' before any block title; skipping",
                    d.EndpointId, unit.Filename, t0);
                continue;
            }

            if (row.Length < 2)
            {
                // A region label with no value cells at all — nothing to load.
                log.LogDebug("[CWG {Endpoint}] {File}: capacity row '{Region}' has no value columns; skipping",
                    d.EndpointId, unit.Filename, t0);
                continue;
            }

            // Read positionally; a short block (e.g. a Change block that omits the trailing
            // 11-15 horizon) leaves the absent cells empty → the row factory maps them to
            // NULL instead of dropping the row (count the cells between the commas).
            rows.Add(new CwgCapacityRow(block, t0,
                1 < row.Length ? row[1] : string.Empty,
                2 < row.Length ? row[2] : string.Empty,
                3 < row.Length ? row[3] : string.Empty,
                4 < row.Length ? row[4] : string.Empty));
        }

        if (d.ExpectedBlocks is { } expected && blocks.Count != expected)
            log.LogWarning("[CWG {Endpoint}] {File}: found {Found} block(s), expected {Expected}",
                d.EndpointId, unit.Filename, blocks.Count, expected);

        return rows;
    }
}
