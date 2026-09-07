using System.Globalization;

namespace DataLoader.EOX;

/// <summary>
/// CSV parsing for the EOX drop.
///
/// <para>
/// The feed is comma-delimited, CRLF, one header row, and — verified over 25 files
/// spanning 2011..2026 — contains <b>no quoting at all</b>, no embedded commas and
/// no empty cells. This parser still honours RFC 4180 quoting, including a newline
/// inside a quoted field, because the TVP binds BY POSITION: an embedded comma
/// handled naively would shift every following column one place left and write
/// plausible garbage rather than fail.
/// </para>
/// <para>
/// Values are trimmed after unquoting. Nothing in the observed feed is padded, but
/// several trimmed columns are primary-key components, so a future stray space
/// must not become part of a key.
/// </para>
/// </summary>
internal static class EoxCsv
{
    /// <summary>
    /// Date formats, tried in order under <see cref="EoxTime.Inv"/>.
    ///
    /// <para>
    /// The feed uses exactly two shapes, split by series and stable across the
    /// whole history: CrudeOil and NGL publish <c>yyyy-MM-dd</c>, NaturalGas
    /// publishes <c>MM/dd/yy</c>. Both are listed for every feed rather than
    /// per-descriptor — the shapes cannot be confused with one another, and a feed
    /// that switches format keeps loading instead of failing every row.
    /// </para>
    /// <para>
    /// Every entry here carries a FOUR-digit year. Two-digit years are widened
    /// first by <see cref="ExpandTwoDigitYear"/> — see there for why this is not
    /// left to the framework.
    /// </para>
    /// </summary>
    public static readonly string[] DateFormats =
        { "yyyy-MM-dd", "MM/dd/yyyy", "M/d/yyyy", "yyyyMMdd" };

    /// <summary>
    /// Rewrite <c>M/d/yy</c> to <c>M/d/20yy</c>, leaving anything else untouched.
    ///
    /// <para>
    /// <b>Load-bearing.</b> The NaturalGas feed publishes <c>MM/dd/yy</c> — a
    /// TWO-DIGIT year — for <c>Curve_Date</c>, <c>Contract_Begin</c> AND
    /// <c>Contract_End</c>. Handing that to <c>DateTime.ParseExact</c> with a
    /// <c>yy</c> format applies the calendar's <c>TwoDigitYearMax</c>, which
    /// defaults to <b>2049</b>: a contract ending in 2050 would silently load as
    /// <b>1950</b>. The 2026 files already carry contract ends out to 2036 and the
    /// tenor grows every year, so that is a dated bug waiting rather than a
    /// hypothetical one.
    /// </para>
    /// <para>
    /// Widening here rather than mutating a culture's <c>TwoDigitYearMax</c> keeps
    /// the rule visible, unit-testable, and independent of whether a cloned
    /// culture's calendar happens to be writable. EOX's history begins in 2011 and
    /// contains no 19xx date anywhere, so mapping the whole <c>00..99</c> range
    /// into 2000..2099 is unambiguously right for this feed.
    /// </para>
    /// </summary>
    internal static string ExpandTwoDigitYear(string raw)
    {
        // M/d/yy is 6..8 characters; anything longer already has a 4-digit year.
        if (raw.Length is < 6 or > 8) return raw;

        var firstSlash = raw.IndexOf('/');
        if (firstSlash <= 0) return raw;

        var lastSlash = raw.LastIndexOf('/');
        if (lastSlash == firstSlash) return raw;

        var year = raw.AsSpan(lastSlash + 1);
        if (year.Length != 2 || !char.IsAsciiDigit(year[0]) || !char.IsAsciiDigit(year[1])) return raw;

        return string.Concat(raw.AsSpan(0, lastSlash + 1), "20", year);
    }

    /// <summary>
    /// Split a whole document into records, honouring RFC 4180 quoting — including
    /// a newline INSIDE a quoted field, which a line-by-line split would break.
    ///
    /// <para>
    /// Values are returned unquoted and trimmed. Blank lines are skipped wherever
    /// they occur, so the trailing newline every file ends with does not become a
    /// phantom record.
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
    /// Build a case-insensitive header-name to column-index map. A duplicated
    /// header keeps the FIRST occurrence — none of the live files has one, and
    /// silently preferring the last would be the more surprising choice.
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
    /// Resolve a column to its index in this file's header, trying its
    /// <see cref="EoxColumn.SourceHeaders"/> in preference order. Returns -1 when
    /// none of them is present.
    /// </summary>
    public static int ResolveIndex(IReadOnlyDictionary<string, int> headerMap, EoxColumn column)
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
    /// Returns <c>true</c> with <paramref name="value"/> set to
    /// <see cref="DBNull.Value"/> for a blank cell — blank is a legitimate NULL,
    /// not a parse failure. Returns <c>false</c> only when a NON-blank cell cannot
    /// be converted, which the caller reports and counts.
    /// </para>
    /// </summary>
    public static bool TryConvert(EoxColumnType type, string raw, out object value)
    {
        if (raw.Length == 0)
        {
            value = DBNull.Value;
            return true;
        }

        switch (type)
        {
            case EoxColumnType.String:
                value = raw;
                return true;

            case EoxColumnType.Double:
                // NumberStyles.Float covers the leading '-' that spread curves carry
                // and the exponent form, without accepting thousands separators the
                // feed never uses.
                if (double.TryParse(raw, NumberStyles.Float, EoxTime.Inv, out var d)
                    && !double.IsNaN(d) && !double.IsInfinity(d))
                { value = d; return true; }
                break;

            case EoxColumnType.Date:
                if (DateOnly.TryParseExact(ExpandTwoDigitYear(raw), DateFormats, EoxTime.Inv,
                        DateTimeStyles.None, out var date))
                { value = date.ToDateTime(TimeOnly.MinValue); return true; }
                break;

            default:
                throw new NotSupportedException($"Unmapped EoxColumnType '{type}'.");
        }

        value = DBNull.Value;
        return false;
    }
}
