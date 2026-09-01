using System.Globalization;

namespace DataLoader.Argus;

/// <summary>
/// CSV parsing for the Argus drop.
///
/// <para>
/// The feed is RFC 4180: comma-delimited, CRLF, one header row, and quoted fields
/// wherever a value carries leading or trailing whitespace (<c>"Argus NPKs "</c>).
/// No embedded comma or newline was observed inside a quoted field across all 16
/// files, but this parser handles both anyway — the TVP binds BY POSITION, so a
/// future embedded comma handled naively would shift every column one place left
/// and corrupt rows rather than fail them.
/// </para>
/// <para>
/// Every value is TRIMMED after unquoting. That is load-bearing, not cosmetic: the
/// quoting exists precisely because values are space-padded, and several of those
/// columns are primary-key components.
/// </para>
/// </summary>
internal static class ArgusCsv
{
    /// <summary>
    /// The one date format the whole feed uses: <c>dd-MMM-yyyy</c> with English
    /// month abbreviations (<c>26-Aug-2026</c>). Verified over every date cell in
    /// every loaded file — zero deviations. The looser fallbacks are defensive.
    /// </summary>
    public static readonly string[] DateFormats =
        { "dd-MMM-yyyy", "d-MMM-yyyy", "yyyy-MM-dd", "MM/dd/yyyy" };

    /// <summary><c>latestModules.LocalTime</c> is the only time column.</summary>
    public static readonly string[] TimeFormats = { "HH:mm:ss", "H:mm:ss", "HH:mm" };

    /// <summary>
    /// Split a whole document into records, honouring RFC 4180 quoting — including
    /// a newline INSIDE a quoted field, which a line-by-line split would break.
    ///
    /// <para>
    /// Values are returned unquoted and trimmed. A trailing blank line is skipped;
    /// a blank line in the middle is skipped too (it cannot be a valid record for
    /// any feed here, all of which have at least two columns).
    /// </para>
    /// </summary>
    public static List<string[]> ParseRecords(string text)
    {
        var records = new List<string[]>();
        var fields = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        var sawAnyChar = false;

        void EndField()
        {
            fields.Add(current.ToString().Trim());
            current.Clear();
        }

        void EndRecord()
        {
            EndField();
            // A "record" of one empty field is a blank line — not data.
            if (!(fields.Count == 1 && fields[0].Length == 0))
                records.Add(fields.ToArray());
            fields.Clear();
            sawAnyChar = false;
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    // "" inside a quoted field is one literal quote.
                    if (i + 1 < text.Length && text[i + 1] == '"') { current.Append('"'); i++; }
                    else inQuotes = false;
                }
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
                    EndField();
                    sawAnyChar = true;
                    break;

                case '\r':
                    // CRLF or a lone CR both terminate the record; swallow the LF.
                    if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                    EndRecord();
                    break;

                case '\n':
                    EndRecord();
                    break;

                default:
                    current.Append(c);
                    sawAnyChar = true;
                    break;
            }
        }

        // Final record when the file does not end with a newline.
        if (sawAnyChar || current.Length > 0 || fields.Count > 0)
            EndRecord();

        return records;
    }

    /// <summary>
    /// Build a case-insensitive header name to column-index map. A duplicated header
    /// keeps the FIRST occurrence — none of the live files has one, and silently
    /// preferring the last would be the more surprising choice.
    /// </summary>
    public static Dictionary<string, int> BuildHeaderMap(string[] header)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < header.Length; i++)
            if (!map.ContainsKey(header[i]))
                map[header[i]] = i;
        return map;
    }

    /// <summary>
    /// Convert one raw cell to the CLR value the sink's DataTable expects.
    ///
    /// <para>
    /// Returns <c>true</c> with <paramref name="value"/> set to
    /// <see cref="DBNull.Value"/> for a blank cell — blank is a legitimate NULL
    /// throughout this feed, not a parse failure. Returns <c>false</c> only when a
    /// NON-blank cell cannot be converted, which the caller reports.
    /// </para>
    /// </summary>
    public static bool TryConvert(ArgusColumnType type, string raw, out object value)
    {
        if (raw.Length == 0)
        {
            value = DBNull.Value;
            return true;
        }

        switch (type)
        {
            case ArgusColumnType.String:
                value = raw;
                return true;

            case ArgusColumnType.Char:
                // CHAR(1): keep the first character only. Every observed value is
                // already one character ('N', 'C', 'Y').
                value = raw.Length == 1 ? raw : raw[..1];
                return true;

            case ArgusColumnType.Int16:
                if (short.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s))
                { value = s; return true; }
                break;

            case ArgusColumnType.Int32:
                if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
                { value = i; return true; }
                break;

            case ArgusColumnType.Int64:
                if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
                { value = l; return true; }
                break;

            case ArgusColumnType.Byte:
                if (byte.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var b))
                { value = b; return true; }
                break;

            case ArgusColumnType.Decimal:
                if (decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                { value = d; return true; }
                break;

            case ArgusColumnType.Date:
                if (DateOnly.TryParseExact(raw, DateFormats, CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var date))
                { value = date.ToDateTime(TimeOnly.MinValue); return true; }
                break;

            case ArgusColumnType.Time:
                if (TimeOnly.TryParseExact(raw, TimeFormats, CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var time))
                { value = time.ToTimeSpan(); return true; }
                break;

            default:
                throw new NotSupportedException($"Unmapped ArgusColumnType '{type}'.");
        }

        value = DBNull.Value;
        return false;
    }
}
