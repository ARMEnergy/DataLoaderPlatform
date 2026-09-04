using System.Globalization;

namespace DataLoader.Criterion;

/// <summary>
/// The loader's only clock arithmetic.
///
/// <para>
/// <b>Everything here is UTC</b>, unlike the exchange loaders (ICE, EvolutionMarkets)
/// which enumerate on a US Central business calendar. That is a deliberate
/// difference, not an oversight:
/// </para>
/// <list type="bullet">
///   <item>
///     The window columns are <c>post_date</c> and <c>eff_gas_day</c> — values the
///     SOURCE assigns, not dates this loader derives from a business calendar. The
///     window's only job is to bracket them generously; the source decides what
///     falls inside.
///   </item>
///   <item>
///     Enumerating a day early or late costs one work unit that finds no rows, which
///     is a first-class non-error outcome here (a gas day the source has not
///     published yet). Guessing at a business calendar would risk the opposite —
///     skipping a day the source did publish, which is the failure that loses data.
///   </item>
///   <item>
///     <c>yyyyMMddHH</c> is strictly monotonic only in UTC. <c>01:00</c> local occurs
///     TWICE on a fall-back night, so a local-zone hour token would repeat, the
///     resume key would go backwards, and an already-recorded success would suppress
///     a legitimate re-pull for an hour.
///   </item>
/// </list>
/// </summary>
internal static class CriterionTime
{
    internal static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>The current date in UTC.</summary>
    public static DateOnly Today(DateTime utcNow) => DateOnly.FromDateTime(utcNow);

    /// <summary>
    /// A date rendered <c>yyyy-MM-dd</c> under the INVARIANT culture — for resume
    /// keys, display names persisted into <c>core.LoadLog</c>, and log messages.
    ///
    /// <para>
    /// An interpolated <c>{d:yyyy-MM-dd}</c> uses the AMBIENT culture, so on a host
    /// whose default culture carries a non-Gregorian calendar the same date prints a
    /// different year — and the resume key silently changes identity.
    /// </para>
    /// </summary>
    public static string Iso(DateOnly value) => value.ToString("yyyy-MM-dd", Inv);

    /// <summary>
    /// The date formats seen in the <c>data</c> JSON, tried in this order.
    ///
    /// <para>
    /// All three are live as of 2026-09-03 and appear in the SAME table — the shape
    /// depends on which upstream loader wrote the series, not on anything the
    /// consumer can select on:
    /// </para>
    /// <list type="bullet">
    ///   <item><c>MM/dd/yyyy</c> — <c>"09/02/2026"</c>. The most common.</item>
    ///   <item><c>yyyy-MM-dd</c> — <c>"2023-08-24"</c>.</item>
    ///   <item>ISO-8601 with a <c>Z</c> — <c>"2016-08-21T00:00:00.000Z"</c>.</item>
    /// </list>
    /// <para>
    /// ⚠ <c>MM/dd/yyyy</c> is listed FIRST and parsed with the invariant culture on
    /// purpose. The requester's own sample used the unpadded <c>"10/5/2021"</c>, which
    /// is ambiguous with <c>dd/MM/yyyy</c>: under a day-first reading that is 10 May
    /// rather than 5 October. Criterion is a US vendor and every sampled series is
    /// month-first (values beyond 12 appear in the second component, never the
    /// first), so month-first is correct — but it must be PINNED, because an ambient
    /// culture of en-GB on the host would otherwise silently reinterpret every date
    /// in the table.
    /// </para>
    /// </summary>
    public static readonly string[] JsonDateFormats =
    {
        "M/d/yyyy",
        "MM/dd/yyyy",
        "yyyy-MM-dd",
        "yyyy-MM-ddTHH:mm:ss.fffZ",
        "yyyy-MM-ddTHH:mm:ssZ",
        "yyyy-MM-ddTHH:mm:ss.fff",
        "yyyy-MM-ddTHH:mm:ss"
    };

    /// <summary>
    /// Parses one <c>date</c> value from the JSON to its calendar DATE, discarding
    /// any time component. Returns false for a null, blank or unrecognised value —
    /// the caller drops the observation and counts it, rather than merging it under
    /// a wrong date.
    /// </summary>
    public static bool TryParseJsonDate(string? text, out DateTime date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var trimmed = text.Trim();

        if (DateTime.TryParseExact(
                trimmed, JsonDateFormats, Inv,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var parsed))
        {
            date = parsed.Date;
            return true;
        }

        // Last resort for a format not yet seen. Still invariant — never the ambient
        // culture — so a host in en-GB cannot flip month and day underneath us.
        if (DateTime.TryParse(
                trimmed, Inv,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out parsed))
        {
            date = parsed.Date;
            return true;
        }

        return false;
    }
}
