using System.Globalization;

namespace DataLoader.ICE;

/// <summary>
/// Text-to-CLR conversion for every ICE feed.
///
/// <para>
/// The formats below were all observed live (<c>docs/apis/ICE.md</c> 4). ICE is
/// not internally consistent: a single Crude Index row carries three different
/// date/time shapes, and the XLSX feed writes its trade date <c>MM/dd/yyyy</c>
/// while writing its strip <c>yyyy-MM-dd</c>. Rather than give every column its own
/// format string — 100+ places to get wrong — each conversion tries the small
/// closed set of shapes that actually occur, exact-match and invariant-culture
/// only. Exact matching is what keeps this safe: it will never silently read
/// <c>03/04/2026</c> as 3 April.
/// </para>
/// </summary>
internal static class IceConvert
{
    /// <summary>
    /// Date shapes across all 18 feeds:
    /// <c>8/28/2026</c> (settlement .dat, not zero-padded), <c>08/28/2026</c> (XLSX),
    /// <c>2026-08-28</c> (Crude Index, and XLSX strips).
    /// </summary>
    public static readonly string[] DateFormats =
    {
        "M/d/yyyy",
        "MM/dd/yyyy",
        "yyyy-MM-dd"
    };

    /// <summary>
    /// Timestamp shapes. The <b>12-hour with AM/PM</b> form is the one the Crude
    /// Index feeds actually use (<c>2026-08-28 03:00:00 PM</c>); a 24-hour-only
    /// parser would reject every row. The 24-hour forms are defensive.
    /// </summary>
    public static readonly string[] DateTimeFormats =
    {
        "yyyy-MM-dd hh:mm:ss tt",
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-dd hh:mm tt",
        "yyyy-MM-dd HH:mm",
        "M/d/yyyy h:mm:ss tt",
        "M/d/yyyy HH:mm:ss",
        "yyyy-MM-ddTHH:mm:ss"
    };

    /// <summary>
    /// Convert one raw cell to the CLR value the sink's DataTable expects.
    ///
    /// <para>
    /// Returns <c>true</c> with <paramref name="value"/> set to
    /// <see cref="DBNull.Value"/> for a blank cell — blank is a legitimate NULL
    /// throughout these feeds (an empty <c>STRIKE</c> on a futures row, an empty
    /// <c>NET CHANGE</c> on an untraded contract). Returns <c>false</c> only when a
    /// NON-blank cell cannot be converted, which the caller counts and reports.
    /// </para>
    /// <para>
    /// The caller — not this method — decides what a blank means for a REQUIRED
    /// column: those rows are dropped rather than merged under a blank key.
    /// </para>
    /// </summary>
    public static bool TryConvert(IceColumnType type, string raw, out object value)
    {
        raw = raw.Trim();

        if (raw.Length == 0)
        {
            value = DBNull.Value;
            return true;
        }

        switch (type)
        {
            case IceColumnType.String:
                value = raw;
                return true;

            case IceColumnType.Char:
                // CHAR(1): keep the first character only. Every observed value is
                // already one character ('F', 'C', 'P', 'D').
                value = raw.Length == 1 ? raw : raw[..1];
                return true;

            case IceColumnType.Int32:
                if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
                { value = i; return true; }
                break;

            case IceColumnType.Int64:
                if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
                { value = l; return true; }
                break;

            case IceColumnType.Decimal:
                if (decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                { value = d; return true; }
                break;

            case IceColumnType.Date:
                if (DateOnly.TryParseExact(raw, DateFormats, CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var date))
                { value = date.ToDateTime(TimeOnly.MinValue); return true; }
                break;

            case IceColumnType.DateTime:
                if (DateTime.TryParseExact(raw, DateTimeFormats, CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var dt))
                { value = dt; return true; }

                // A timestamp column carrying just a date is accepted at midnight
                // rather than dropped — it is unambiguous and losing the row would
                // be the worse outcome.
                if (DateOnly.TryParseExact(raw, DateFormats, CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var dateOnly))
                { value = dateOnly.ToDateTime(TimeOnly.MinValue); return true; }
                break;

            default:
                throw new NotSupportedException($"Unmapped IceColumnType '{type}'.");
        }

        value = DBNull.Value;
        return false;
    }
}
