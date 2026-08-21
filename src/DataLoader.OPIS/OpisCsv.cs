using System.Globalization;

namespace DataLoader.OPIS;

/// <summary>
/// Tolerant parsing helpers for the OPIS LP CSV.
///
/// The feed is machine-generated and very regular — comma-delimited, CRLF, one
/// header row, exactly ten fields, no quoting, every value right- or
/// left-space-padded to a fixed width. These helpers therefore trim aggressively
/// and treat a blank as NULL rather than as a parse failure, so one odd row can
/// never fail a whole file. A row that is malformed in a way that matters (wrong
/// field count, unparseable date, missing key) is dropped and counted by the
/// caller.
/// </summary>
internal static class OpisCsv
{
    /// <summary>Field count of a well-formed data row.</summary>
    public const int FieldCount = 10;

    /// <summary>
    /// Split one CSV line. The feed never quotes, but a minimal quote-aware split
    /// costs little and stops a future embedded comma from silently shifting every
    /// column one place to the left — which, given the TVP binds by position,
    /// would corrupt rows rather than fail.
    /// </summary>
    public static string[] SplitLine(string line)
    {
        var fields = new List<string>(FieldCount);
        var current = new System.Text.StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    // "" inside a quoted field is a literal quote.
                    if (i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                    else inQuotes = false;
                }
                else current.Append(c);
            }
            else if (c == '"') inQuotes = true;
            else if (c == ',') { fields.Add(current.ToString()); current.Clear(); }
            else current.Append(c);
        }

        fields.Add(current.ToString());
        return fields.ToArray();
    }

    /// <summary>Trimmed field at <paramref name="index"/>, or empty when out of range.</summary>
    public static string Field(string[] fields, int index) =>
        index >= 0 && index < fields.Length ? fields[index].Trim() : string.Empty;

    /// <summary>Trimmed field, or null when blank — for NULL-able string columns.</summary>
    public static string? NullableField(string[] fields, int index)
    {
        var value = Field(fields, index);
        return value.Length == 0 ? null : value;
    }

    /// <summary>
    /// Parse a price cell. Blank is a legitimate NULL: basket rows (e.g.
    /// <c>MT BEL NT BSKT</c>) publish only <c>Avg</c> and leave Low/High empty.
    /// An unparseable non-blank value also yields null and is reported by the caller.
    /// </summary>
    public static decimal? Price(string[] fields, int index, out bool malformed)
    {
        malformed = false;
        var value = Field(fields, index);
        if (value.Length == 0) return null;

        if (decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
            return parsed;

        malformed = true;
        return null;
    }

    /// <summary>
    /// Parse the <c>Date</c> column against the configured candidate formats. The
    /// live feed publishes <c>MM/dd/yy</c> — a TWO-DIGIT year, which .NET's
    /// <c>Calendar.TwoDigitYearMax</c> (2049 by default) maps into the 2000s, so
    /// <c>08/20/26</c> becomes 2026-08-20. Pinned to InvariantCulture so a machine's
    /// regional settings cannot flip day and month.
    /// </summary>
    public static DateOnly? Date(string value, string[] formats)
    {
        value = value.Trim();
        if (value.Length == 0) return null;

        return DateOnly.TryParseExact(value, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact)
            ? exact
            : DateOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var loose)
                ? loose
                : null;
    }

    /// <summary>True when the line is the header row (or blank), i.e. not data.</summary>
    public static bool IsHeaderOrBlank(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length == 0
            || trimmed.StartsWith("Price,", StringComparison.OrdinalIgnoreCase);
    }
}
