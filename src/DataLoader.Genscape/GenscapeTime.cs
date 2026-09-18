using System.Globalization;

namespace DataLoader.Genscape;

/// <summary>
/// The loader's only clock arithmetic. <b>Everything here is UTC and invariant-culture.</b>
///
/// <para>
/// The window's job is to bracket the vendor's own <c>reportDate</c> values generously,
/// not to derive them: enumerating a day early or late costs nothing (the API answers
/// <c>200 {"data":[]}</c> for a window with no report in it), whereas guessing at a
/// business calendar risks skipping a week the vendor did publish.
/// </para>
/// </summary>
internal static class GenscapeTime
{
    internal static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>The current date in UTC.</summary>
    public static DateOnly Today(DateTime utcNow) => DateOnly.FromDateTime(utcNow);

    /// <summary>
    /// A date rendered <c>yyyy-MM-dd</c> under the INVARIANT culture — for the query
    /// string, resume keys, load-log display names and log messages.
    ///
    /// <para>
    /// An interpolated <c>{d:yyyy-MM-dd}</c> uses the AMBIENT culture, so on a host
    /// whose default culture carries a non-Gregorian calendar the same date prints a
    /// different year. That would change the resume key's identity AND produce a
    /// request the API rejects with <c>400</c>, since it validates RFC 3339.
    /// </para>
    /// </summary>
    public static string Iso(DateOnly value) => value.ToString("yyyy-MM-dd", Inv);

    /// <summary>
    /// The calendar WEEK OF YEAR of <paramref name="date"/> — the value SQL Server's
    /// <c>DATEPART(week, date)</c> returns under the <c>us_english</c> default
    /// <c>DATEFIRST</c> of 7 (weeks start on Sunday). Range 1..54.
    ///
    /// <para>
    /// <b>Why this exists.</b> The <c>crude-storage</c> endpoint returns a
    /// week-of-MONTH in its <c>week</c> field — 2026-08-28 comes back as <c>4</c>,
    /// 2025-12-05 as <c>1</c> — which is not what
    /// <c>arm.OilFundamentals_CrudeStorage_Weekly.Week</c> means. The requester asked
    /// for the value to be overwritten, so both feeds derive it here instead of
    /// trusting the payload.
    /// </para>
    /// <para>
    /// <b>Why in C# rather than in the merge proc.</b> <c>DATEPART(week, …)</c> is
    /// DATEFIRST-dependent, and DATEFIRST follows the login's default language — a
    /// connection under a language whose first day is Monday returns a different
    /// number for the same date. <c>Week</c> is a <b>primary-key component</b> of both
    /// tables, so a session-dependent value would fork the key and load each report
    /// twice. Computing it here makes the stored value a pure function of
    /// <c>ReportDate</c> and nothing else.
    /// </para>
    /// <para>
    /// <b>Why this formula is known to be right.</b> The <c>crude-transportation</c>
    /// endpoint already returns exactly this number, and its values were compared
    /// against this implementation across a year boundary on live data (2025-12-05 to
    /// 49, 2025-12-26 to 52, 2026-01-02 to 1, 2026-02-13 to 7). The vendor and SQL
    /// Server agree; only the storage endpoint disagrees, which is the bug being
    /// corrected. <c>arm.usp_ValidateLoad</c> re-asserts the equality server-side under
    /// an explicit <c>SET DATEFIRST 7</c>.
    /// </para>
    /// <para>
    /// Deliberately NOT <see cref="Calendar.GetWeekOfYear"/>: that follows the ambient
    /// culture's <c>CalendarWeekRule</c>, which is the same class of host-dependence
    /// the whole method exists to avoid.
    /// </para>
    /// </summary>
    public static byte WeekOfYear(DateOnly date)
    {
        // Sunday-based index (Sun=0 … Sat=6) of 1 January in this date's year: how
        // many days of week 1 fall before 1 January. Week 1 is therefore short by
        // exactly that many days, which is what shifts every later boundary.
        var jan1Offset = (int)new DateOnly(date.Year, 1, 1).DayOfWeek;

        // DayOfYear is 1-based; -1 makes it a 0-based day index within the year.
        return (byte)(1 + (date.DayOfYear - 1 + jan1Offset) / 7);
    }

    /// <summary>
    /// The calendar YEAR of <paramref name="date"/>, as the target's <c>SMALLINT</c>.
    ///
    /// <para>
    /// Derived for the same reason as <see cref="WeekOfYear"/>: the two columns are a
    /// PAIR. <see cref="WeekOfYear"/> resets on 1 January of the CALENDAR year, so a
    /// <c>Year</c> taken from the payload while <c>Week</c> is derived could produce an
    /// incoherent key such as <c>(2025, 1)</c> for a January 2026 report. Both
    /// endpoints' <c>year</c> field already equals <c>YEAR(reportDate)</c> in every row
    /// sampled live, so this changes no observed value — it removes the chance of a
    /// mismatch rather than fixing one.
    /// </para>
    /// </summary>
    public static short YearOf(DateOnly date) => (short)date.Year;

    /// <summary>
    /// The formats <c>reportDate</c> is accepted in.
    ///
    /// <para>
    /// Every live row uses the RFC 3339 full-date <c>yyyy-MM-dd</c>; the two date-time
    /// forms are here because the API's own validation message names them as legal date
    /// representations, so the vendor could start emitting one.
    /// </para>
    /// </summary>
    private static readonly string[] ReportDateFormats =
    {
        "yyyy-MM-dd",
        "yyyy-MM-ddTHH:mm:ssZ",
        "yyyy-MM-ddTHH:mm:ss.fffZ"
    };

    /// <summary>
    /// Reads one <c>reportDate</c>. Returns false for a null, blank or unrecognised
    /// value; the caller drops that record and counts it rather than merging it under a
    /// wrong or blank date.
    ///
    /// <para>
    /// ⚠ A slash form such as <c>01/02/2026</c> is REJECTED rather than guessed at.
    /// The API documents and emits RFC 3339 only, and accepting a slash form would mean
    /// choosing between 2 January and 1 February — under an ambient <c>en-GB</c>
    /// culture, silently.
    /// </para>
    /// </summary>
    public static bool TryParseReportDate(string? text, out DateOnly date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        if (DateTime.TryParseExact(
                text.Trim(), ReportDateFormats, Inv,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var parsed))
        {
            date = DateOnly.FromDateTime(parsed.Date);
            return true;
        }

        return false;
    }
}
