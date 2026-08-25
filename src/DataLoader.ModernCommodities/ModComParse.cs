using System.Globalization;
using Microsoft.Extensions.Logging;

namespace DataLoader.ModernCommodities;

/// <summary>Outcome of a tolerant scalar parse (design §5.3).</summary>
internal enum ModComParseOutcome
{
    /// <summary>The field was blank — the ONLY absent-value representation this API has.</summary>
    Absent,

    /// <summary>Parsed successfully.</summary>
    Ok,

    /// <summary>Present but unparseable — degrade the field to NULL and count it.</summary>
    Unparseable,

    /// <summary>
    /// Parsed but outside the target column's range. Only <see cref="ModComParse.Dec92"/> can
    /// return this — an over-range value is a hard <b>arithmetic-overflow error</b> that would fail
    /// the ENTIRE batch, not a truncation, so the field degrades to NULL and the row survives.
    /// </summary>
    OutOfRange
}

/// <summary>
/// Invariant-culture value readers (design §5.3).
///
/// <para><b>⚠⚠ There is NO null sentinel other than the empty string.</b> In particular
/// <b><c>-</c> is NOT a sentinel</b>: <c>arm.Settlements.Location</c> and
/// <c>arm.Settlements.PieplineTerminal</c> are <c>NOT NULL</c> columns <b>inside the 6-column
/// PRIMARY KEY</b> and the vendor writes the literal one-character string <c>-</c> in both (82 of
/// 1,443 rows, always as a pair). <b>Do NOT port <c>NgiParse.IsNullSentinel</c></b>, which lists
/// <c>"-"</c> among its defensive nulls: reusing it here would map a KEY VALUE to NULL, violate the
/// <c>NOT NULL</c> PK, and — applied inconsistently — create duplicate logical rows the MERGE cannot
/// reconcile. There is likewise no <c>"None"</c>, <c>"N/A"</c> or <c>"NULL"</c> sentinel of any
/// kind here.</para>
///
/// <para>Contrast: a <c>-</c> <i>prefixing digits</i> in a numeric column is a <b>sign</b>. 74 % of
/// settlement prices and 21 % of <c>allTrades</c> prices are negative (they are differentials), and
/// <c>0.00</c> is a real value. Never a parse failure, and never a non-negative <c>CHECK</c>.</para>
///
/// <para><b>Overload discipline:</b> every helper has a distinct NAME and a fixed arity — there are
/// deliberately no overloads and no <c>params</c> anywhere in this class. A one-argument call
/// silently binding to a <c>params</c> overload (and returning <c>null</c> for every row) is a
/// failure mode that has already shipped in this repo; here the same mistake is a compile error.</para>
/// </summary>
internal static class ModComParse
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>
    /// The <c>DECIMAL(9,2)</c> ceiling shared by <c>Price</c>, <c>Volume</c> and both commission
    /// columns. Observed <c>Volume</c> maximum is <b>300,000</b>, so real headroom is <b>~33×, not
    /// ~1,000×</b> (design §12 item 5 recommends widening to <c>DECIMAL(13,2)</c>).
    /// </summary>
    public const decimal Dec92Ceiling = 9_999_999.99m;

    /// <summary>
    /// The exact timestamp format of <c>Executed Timestamp</c> and <c>Last Updated Timestamp</c>.
    ///
    /// <para><b>⚠⚠ Twelve-hour with <c>AM</c>/<c>PM</c>.</b> <c>2026-08-24 01:44:41 PM</c> is
    /// <b>13:44:41</b>. An <c>HH</c> (24-hour) pattern mis-parses or fails <b>every afternoon
    /// timestamp</b> (roughly a third of rows), and a bare <c>DateTime.Parse</c> on a non-US locale
    /// may not recognise <c>PM</c> at all — losing 12 hours. The <c>12:xx PM</c> noon-hour rows are
    /// the subtlest case (<c>2026-08-06 12:42:37 PM</c> = 12:42:37). Seconds precision only; no
    /// fractional seconds were ever observed, hence <c>DATETIME2(0)</c>.</para>
    ///
    /// <para><b>No timezone offset is supplied and no timezone is stated anywhere</b> — the value is
    /// stored <b>exactly as given</b> and is never shifted or normalised (design §7).</para>
    /// </summary>
    public const string TimestampFormat = "yyyy-MM-dd hh:mm:ss tt";

    /// <summary>
    /// Trimmed text, or <c>null</c> when the field is blank/absent. <b>No sentinel remapping of any
    /// kind</b> — the value is kept literally, including a lone <c>-</c> and <c>USD $</c> (a key
    /// containing a space and a <c>$</c>, which must not be trimmed, stripped or normalised).
    /// </summary>
    public static string? Text(string? raw) =>
        string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();

    /// <summary><c>int</c> under invariant culture. Used only for <c>Trade Number</c>, whose failure makes the row unkeyable.</summary>
    public static ModComParseOutcome Int32(string? raw, out int value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(raw)) return ModComParseOutcome.Absent;
        return int.TryParse(raw.Trim(), NumberStyles.Integer, Inv, out value)
            ? ModComParseOutcome.Ok
            : ModComParseOutcome.Unparseable;
    }

    /// <summary>
    /// A <c>DECIMAL(9,2)</c> measure. <b>A leading minus MUST parse</b> — negative prices are
    /// differentials, not errors. Values beyond <see cref="Dec92Ceiling"/> return
    /// <see cref="ModComParseOutcome.OutOfRange"/> so the caller can degrade that one field to NULL:
    /// letting it through would raise a server-side <b>arithmetic overflow</b> that fails the whole
    /// batch, which is the opposite of tolerant parsing.
    /// </summary>
    public static ModComParseOutcome Dec92(string? raw, out decimal value)
    {
        value = 0m;
        if (string.IsNullOrWhiteSpace(raw)) return ModComParseOutcome.Absent;
        if (!decimal.TryParse(raw.Trim(), NumberStyles.Number | NumberStyles.AllowLeadingSign, Inv, out value))
        {
            value = 0m;
            return ModComParseOutcome.Unparseable;
        }
        // NOTE: on OutOfRange the parsed value is deliberately RETAINED in `value` so the caller can
        // name it in the error-level warning. The caller must still persist NULL for that field.
        if (Math.Abs(value) > Dec92Ceiling)
            return ModComParseOutcome.OutOfRange;

        return ModComParseOutcome.Ok;
    }

    /// <summary>
    /// Case-insensitive <c>True</c>/<c>False</c> → <c>BIT</c>.
    ///
    /// <para><b>⚠ Blank → <see cref="ModComParseOutcome.Absent"/> (NULL), NOT <c>false</c>.</b>
    /// Conflating them destroys the anonymisation signal: all 98 <c>allTrades</c> rows would read
    /// "not a click trade" instead of "unknown".</para>
    /// </summary>
    public static ModComParseOutcome Bit(string? raw, out bool value)
    {
        value = false;
        if (string.IsNullOrWhiteSpace(raw)) return ModComParseOutcome.Absent;
        var s = raw.Trim();
        if (s.Equals("True", StringComparison.OrdinalIgnoreCase)) { value = true; return ModComParseOutcome.Ok; }
        if (s.Equals("False", StringComparison.OrdinalIgnoreCase)) { value = false; return ModComParseOutcome.Ok; }
        return ModComParseOutcome.Unparseable;
    }

    /// <summary><c>yyyy-MM-dd</c> → <c>DATE</c>. Used for <c>Term Start</c>, <c>Term End</c>, <c>Settlement Date</c>.</summary>
    public static ModComParseOutcome IsoDate(string? raw, out DateOnly value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(raw)) return ModComParseOutcome.Absent;
        return DateOnly.TryParseExact(raw.Trim(), "yyyy-MM-dd", Inv, DateTimeStyles.None, out value)
            ? ModComParseOutcome.Ok
            : ModComParseOutcome.Unparseable;
    }

    /// <summary>
    /// The 12-hour <see cref="TimestampFormat"/> → <c>DATETIME2(0)</c>, parsed with
    /// <see cref="CultureInfo.InvariantCulture"/> and stored <b>exactly as given</b> (never shifted).
    /// </summary>
    public static ModComParseOutcome Timestamp(string? raw, out DateTime value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(raw)) return ModComParseOutcome.Absent;
        return DateTime.TryParseExact(raw.Trim(), TimestampFormat, Inv, DateTimeStyles.None, out value)
            ? ModComParseOutcome.Ok
            : ModComParseOutcome.Unparseable;
    }
}

/// <summary>
/// The tolerant-parse tallies for one pull, reported at the end of the work unit and persisted as
/// <c>arm.FileLog.DroppedRowCount</c> (design §5.4 step 4, §6.2).
///
/// <para>With no per-row provenance column (decision D1), <see cref="Dropped"/> is the <b>only</b>
/// durable record that rows were discarded — which is why it is a first-class hub column and a
/// validated check (<c>UnkeyableDrops</c>, expected 0).</para>
/// </summary>
internal sealed class ModComParseCounters
{
    /// <summary>Rows successfully mapped and returned.</summary>
    public int Parsed { get; set; }

    /// <summary>Rows dropped because their merge key was unusable (blank/unparseable, or over-width).</summary>
    public int Dropped { get; set; }

    /// <summary>Numeric/date/bit fields present but unparseable — degraded to NULL, row kept.</summary>
    public int UnparseableFields { get; set; }

    /// <summary>Numeric fields beyond the <c>DECIMAL(9,2)</c> ceiling — degraded to NULL, row kept.</summary>
    public int OutOfRangeFields { get; set; }

    /// <summary>Non-key text fields clamped to their target width — row kept.</summary>
    public int TruncatedFields { get; set; }

    /// <summary>Rows collapsed by the in-batch merge-key de-dup.</summary>
    public int DuplicateKeys { get; set; }

    public override string ToString() =>
        $"parsed={Parsed} dropped={Dropped} unparseable={UnparseableFields} " +
        $"outOfRange={OutOfRangeFields} truncated={TruncatedFields} duplicateKeys={DuplicateKeys}";
}

/// <summary>
/// Binds one CSV data record's fields by <b>literal header name</b> and applies the width/range
/// guards, accumulating the tallies in a <see cref="ModComParseCounters"/> (design §5.3).
///
/// <para><b>The width policy differs for KEY and NON-KEY columns, and that asymmetry is the
/// point:</b></para>
/// <list type="bullet">
///   <item><b>Non-key over width</b> → clamp to <c>maxLen</c> + count (one aggregate warning at the
///     end). An unclamped value raises <i>"String or binary data would be truncated"</i> server-side
///     and fails the whole work unit — and keeps failing every run, the opposite of tolerant
///     parsing.</item>
///   <item><b>Key over width</b> → <b>drop the row and count it</b>, at error level. A truncated key
///     silently MERGEs onto a <i>different</i> logical entity, which is worse than a missing row.</item>
/// </list>
///
/// <para>Values are never echoed in a log line for the counterparty / PII block (traders, legal
/// names, addresses, notes) — only the column name and the length.</para>
/// </summary>
internal sealed class ModComFieldReader
{
    private readonly ModComHeaderMap _map;
    private readonly ModComParseCounters _counters;
    private readonly ILogger _logger;
    private readonly string _endpointId;

    public ModComFieldReader(ModComHeaderMap map, ModComParseCounters counters, ILogger logger, string endpointId)
    {
        _map = map;
        _counters = counters;
        _logger = logger;
        _endpointId = endpointId;
        RowKey = "-";
    }

    /// <summary>
    /// A short, log-safe identifier for the record currently being mapped (the trade number, or the
    /// settlements key tuple). Set it at the top of each record so a degraded field can be traced
    /// back to its row. It must never contain a PII value.
    /// </summary>
    public string RowKey { get; set; }

    /// <summary>Raw, untrimmed field text (or <c>null</c> when the column is absent / the record is short).</summary>
    public string? Raw(string[] record, string column) => _map.Raw(record, column);

    /// <summary>
    /// A NON-KEY text column: trimmed, blank → <c>null</c>, over-width → clamped + counted.
    /// </summary>
    public string? Text(string[] record, string column, int maxLen)
    {
        var value = ModComParse.Text(_map.Raw(record, column));
        if (value is null) return null;
        if (value.Length <= maxLen) return value;

        _counters.TruncatedFields++;
        return value.Substring(0, maxLen);
    }

    /// <summary>
    /// A KEY text column: trimmed; blank or over-width yields <c>null</c> so the caller
    /// <b>drops the row</b>. Never truncates — a truncated key merges onto the wrong row.
    /// </summary>
    public string? KeyText(string[] record, string column, int maxLen, out bool overWidth)
    {
        overWidth = false;
        var value = ModComParse.Text(_map.Raw(record, column));
        if (value is null) return null;
        if (value.Length <= maxLen) return value;

        overWidth = true;
        return null;
    }

    /// <summary>An <c>INT</c> column; <c>null</c> when blank or unparseable (counted).</summary>
    public int? Int32(string[] record, string column)
    {
        var outcome = ModComParse.Int32(_map.Raw(record, column), out var value);
        if (outcome == ModComParseOutcome.Ok) return value;
        if (outcome == ModComParseOutcome.Unparseable) _counters.UnparseableFields++;
        return null;
    }

    /// <summary>
    /// A <c>DECIMAL(9,2)</c> column; <c>null</c> when blank, unparseable (counted) or beyond the
    /// ceiling.
    ///
    /// <para>An over-range value logs at <b>error</b> level naming the column, <see cref="RowKey"/>
    /// and the value, then degrades <i>that field</i> to NULL — preserving the other columns of the
    /// row and, critically, the rest of the batch. Letting it through would raise a server-side
    /// arithmetic overflow that fails the entire merge (design §5.3, §12 item 5).</para>
    /// </summary>
    public decimal? Dec92(string[] record, string column)
    {
        var outcome = ModComParse.Dec92(_map.Raw(record, column), out var value);
        switch (outcome)
        {
            case ModComParseOutcome.Ok:
                return value;
            case ModComParseOutcome.Unparseable:
                _counters.UnparseableFields++;
                return null;
            case ModComParseOutcome.OutOfRange:
                _counters.OutOfRangeFields++;
                _logger.LogError(
                    "[ModCom {Endpoint}] {Column}={Value} on row {RowKey} exceeds the DECIMAL(9,2) ceiling {Ceiling}; " +
                    "the field is degraded to NULL so the row and the batch survive (an over-range value would be a hard " +
                    "arithmetic-overflow error). Consider widening the column to DECIMAL(13,2)",
                    _endpointId, column, value, RowKey, ModComParse.Dec92Ceiling);
                return null;
            default:
                return null;
        }
    }

    /// <summary>A <c>BIT</c> column; <b>blank → <c>null</c>, never <c>false</c></b>.</summary>
    public bool? Bit(string[] record, string column)
    {
        var outcome = ModComParse.Bit(_map.Raw(record, column), out var value);
        if (outcome == ModComParseOutcome.Ok) return value;
        if (outcome == ModComParseOutcome.Unparseable) _counters.UnparseableFields++;
        return null;
    }

    /// <summary>A <c>DATE</c> column (<c>yyyy-MM-dd</c>); <c>null</c> when blank or unparseable (counted).</summary>
    public DateOnly? IsoDate(string[] record, string column)
    {
        var outcome = ModComParse.IsoDate(_map.Raw(record, column), out var value);
        if (outcome == ModComParseOutcome.Ok) return value;
        if (outcome == ModComParseOutcome.Unparseable) _counters.UnparseableFields++;
        return null;
    }

    /// <summary>A <c>DATETIME2(0)</c> column in the 12-hour <c>tt</c> format; <c>null</c> when blank or unparseable (counted).</summary>
    public DateTime? Timestamp(string[] record, string column)
    {
        var outcome = ModComParse.Timestamp(_map.Raw(record, column), out var value);
        if (outcome == ModComParseOutcome.Ok) return value;
        if (outcome == ModComParseOutcome.Unparseable) _counters.UnparseableFields++;
        return null;
    }
}
