using System.Globalization;
using System.Text;

namespace DataLoader.OilX;

/// <summary>
/// CSV parsing for the OilX files — comma-delimited, CRLF, one header row,
/// RFC 4180 quoting.
///
/// <para>
/// <b>This parser STREAMS, and that is the one place OilX deliberately departs from
/// the EOX template.</b> <c>EoxCsv.ParseRecords(string)</c> takes a whole document as
/// a string, which is fine for a 2 MB drop and impossible here: a CargoTracking
/// snapshot is <b>208 MB / 396,866 rows</b>, so the string alone would be ~416 MB of
/// UTF-16 and the resulting <c>List&lt;string[]&gt;</c> another 1–2 GB — four times a
/// day, across a 31-day window. <see cref="ReadRecords"/> yields one record at a time
/// and holds nothing but the current row.
/// </para>
/// <para>
/// <b>Quoting is not optional.</b> 3,447 of CargoTracking's rows contain a quoted
/// field, because country names like <c>"Bonaire, Sint Eustatius and Saba"</c> carry a
/// comma. A naive split on <c>,</c> shifts every later column one place left and writes
/// plausible garbage — the TVP binds BY POSITION, so nothing would raise.
/// </para>
/// <para>
/// Values are returned unquoted and trimmed. Trimming matters: several trimmed columns
/// feed <see cref="OilXRowId"/>, so a stray space must not become part of a key.
/// </para>
/// </summary>
internal static class OilXCsv
{
    /// <summary>
    /// The scale every <c>DECIMAL(p,8)</c> column in this loader uses.
    ///
    /// <para>
    /// <b>Rounding to this on conversion is required, not cosmetic.</b> A value with
    /// more than 8 decimal places cannot be stored in a <c>DECIMAL(p,8)</c> parameter —
    /// SqlClient raises "Parameter value out of range" and the whole 20,000-row batch
    /// dies, not just the offending cell. Rounding here keeps the batch alive and makes
    /// the stored value exactly what <see cref="OilXChecksum"/> hashed, so an unchanged
    /// row never looks changed.
    /// </para>
    /// </summary>
    public const int DecimalScale = 8;

    /// <summary>
    /// Stream records out of <paramref name="reader"/>, honouring RFC 4180 quoting —
    /// including a newline INSIDE a quoted field, which a <c>ReadLine</c> loop would
    /// split in half.
    ///
    /// <para>
    /// The first record yielded is the header. Blank lines are skipped wherever they
    /// occur, so the trailing newline every file ends with does not become a phantom
    /// record.
    /// </para>
    /// </summary>
    public static IEnumerable<string[]> ReadRecords(TextReader reader)
    {
        var buffer = new char[64 * 1024];
        var fields = new List<string>(64);
        var current = new StringBuilder(128);
        var inQuotes = false;
        var sawAnyChar = false;

        // Set when a '"' is consumed inside a quoted field: the NEXT character decides
        // whether it was an escaped quote ("") or the end of the field. Carrying it
        // across buffer refills is what makes the streaming version correct — a
        // whole-document parser can just peek at text[i + 1].
        var pendingQuote = false;

        // A lone '\r' at the very end of a buffer: we cannot yet tell whether the next
        // buffer starts with '\n' (CRLF, swallow it) or with data (a bare CR record
        // terminator). The record is ended here and the flag suppresses the empty
        // record a following '\n' would otherwise produce.
        var pendingCr = false;

        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                var c = buffer[i];

                if (pendingQuote)
                {
                    pendingQuote = false;
                    if (c == '"')
                    {
                        // "" inside a quoted field is one literal quote.
                        current.Append('"');
                        sawAnyChar = true;
                        continue;
                    }

                    inQuotes = false;
                    // fall through and handle c as an unquoted character
                }

                if (pendingCr)
                {
                    pendingCr = false;
                    // CRLF: the record already ended on the CR, so swallow the LF.
                    if (c == '\n') continue;
                }

                if (inQuotes)
                {
                    if (c == '"') pendingQuote = true;
                    else current.Append(c);

                    sawAnyChar = true;
                    continue;
                }

                switch (c)
                {
                    case '"':
                        inQuotes = true;
                        sawAnyChar = true;
                        break;

                    case ',':
                        fields.Add(current.ToString().Trim());
                        current.Clear();
                        sawAnyChar = true;
                        break;

                    case '\r':
                    case '\n':
                    {
                        if (c == '\r') pendingCr = true;

                        fields.Add(current.ToString().Trim());
                        current.Clear();

                        // A "record" of one empty field is a blank line, not data.
                        if (!(fields.Count == 1 && fields[0].Length == 0))
                        {
                            yield return fields.ToArray();
                        }

                        fields.Clear();
                        sawAnyChar = false;
                        break;
                    }

                    default:
                        current.Append(c);
                        sawAnyChar = true;
                        break;
                }
            }
        }

        // An unterminated quoted field at EOF: treat what we have as the value rather
        // than discarding the row. A truncated download is caught by the SnapshotShrink
        // validation check, not by silently losing one record here.
        if (pendingQuote) inQuotes = false;

        // Final record when the file does not end with a newline.
        if (sawAnyChar || current.Length > 0 || fields.Count > 0)
        {
            fields.Add(current.ToString().Trim());
            if (!(fields.Count == 1 && fields[0].Length == 0))
                yield return fields.ToArray();
        }
    }

    /// <summary>
    /// Build a case-insensitive header-name to column-index map. A duplicated header
    /// keeps the FIRST occurrence — no live file has one, and silently preferring the
    /// last would be the more surprising choice.
    /// </summary>
    public static Dictionary<string, int> BuildHeaderMap(string[] header)
    {
        var map = new Dictionary<string, int>(header.Length, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < header.Length; i++)
            if (!map.ContainsKey(header[i]))
                map[header[i]] = i;
        return map;
    }

    /// <summary>
    /// Resolve a column to its index in this file's header, trying its
    /// <see cref="OilXColumn.SourceHeaders"/> in preference order. Returns -1 when none
    /// is present.
    /// </summary>
    public static int ResolveIndex(IReadOnlyDictionary<string, int> headerMap, OilXColumn column)
    {
        foreach (var header in column.SourceHeaders)
            if (headerMap.TryGetValue(header, out var index))
                return index;

        return -1;
    }

    /// <summary>
    /// Convert one raw cell to the CLR value the sink's DataTable expects.
    ///
    /// <para>
    /// Returns <c>true</c> with <paramref name="value"/> set to <see cref="DBNull.Value"/>
    /// for a blank cell — blank is a legitimate NULL, not a parse failure. Returns
    /// <c>false</c> only when a NON-blank cell cannot be converted, which the caller
    /// reports and counts.
    /// </para>
    /// </summary>
    public static bool TryConvert(OilXColumnType type, string raw, out object value)
    {
        if (raw.Length == 0)
        {
            value = DBNull.Value;
            return true;
        }

        switch (type)
        {
            case OilXColumnType.String:
                value = raw;
                return true;

            case OilXColumnType.Date:
                // A DATE column may still be fed a datetime string — FloatingStorage's
                // ReferenceDate is date-only but Flow's is read by the same path — so
                // parse the full set and drop the time component.
                if (OilXTime.TryParseDateTime(raw, out var d))
                { value = d.Date; return true; }
                break;

            case OilXColumnType.DateTime:
                if (OilXTime.TryParseDateTime(raw, out var dt))
                { value = dt; return true; }
                break;

            case OilXColumnType.Decimal:
                // NumberStyles.Float covers a leading sign and the exponent form; it
                // does NOT accept thousands separators, which these feeds never use
                // (NGX's do — a different loader, a different parser).
                if (decimal.TryParse(raw, NumberStyles.Float, OilXTime.Inv, out var dec))
                {
                    value = decimal.Round(dec, DecimalScale, MidpointRounding.ToEven);
                    return true;
                }
                break;

            case OilXColumnType.Double:
                if (double.TryParse(raw, NumberStyles.Float, OilXTime.Inv, out var dbl)
                    && !double.IsNaN(dbl) && !double.IsInfinity(dbl))
                { value = dbl; return true; }
                break;

            case OilXColumnType.Guid:
            case OilXColumnType.Int:
                // Derived columns never come from a cell; reaching here is a bug.
                throw new InvalidOperationException(
                    $"OilXColumnType '{type}' is computed, not parsed from CSV.");

            default:
                throw new NotSupportedException($"Unmapped OilXColumnType '{type}'.");
        }

        value = DBNull.Value;
        return false;
    }
}
