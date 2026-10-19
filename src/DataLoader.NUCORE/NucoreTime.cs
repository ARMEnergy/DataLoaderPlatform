using System.Globalization;

namespace DataLoader.NUCORE;

/// <summary>
/// Date handling for NUCORE.
///
/// <para>
/// The vendor's dates are plain business dates with no zone - <c>2026-10-14T00:00:00</c> -
/// and the API's date parameters take <c>yyyy-MM-dd</c>. So everything here works in
/// <see cref="DateTime"/> with <see cref="DateTimeKind.Unspecified"/> and
/// <see cref="CultureInfo.InvariantCulture"/>; nothing is ever converted between zones. The
/// one place UTC appears is the hot resume-key token, which needs a monotonic clock rather
/// than a business date.
/// </para>
/// </summary>
internal static class NucoreTime
{
    /// <summary>
    /// Today's business date.
    ///
    /// <para>
    /// LOCAL, not UTC, and deliberately. The loader runs on US Central time next to a risk
    /// system whose "system date" is a local business date; taking UTC would roll the window
    /// forward to tomorrow from 19:00 Central and make the loader ask for a day the vendor
    /// has not published, which returns an empty 200 and looks like missing data.
    /// </para>
    /// </summary>
    public static DateTime Today() => DateTime.Now.Date;

    /// <summary>Formats a date for the API's date parameters.</summary>
    public static string FormatDate(DateTime date) =>
        date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Parses a <c>yyyyMM</c> trade month into the first of that month.</summary>
    public static bool TryParseTradeMonth(string? value, out DateTime month)
    {
        month = default;
        if (string.IsNullOrWhiteSpace(value)) return false;

        return DateTime.TryParseExact(
            value.Trim(), "yyyyMM", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out month);
    }
}
