using System.Globalization;

namespace DataLoader.OilX;

/// <summary>
/// Date and time handling for the OilX loader. Everything here is <b>UTC</b>.
///
/// <para>
/// The vendor stamps <c>uploaded_at</c> with an explicit <c>+00:00</c> offset and
/// the in-file <c>RunDateTime</c> is UTC too, so there is no zone conversion
/// anywhere in this loader — unlike NGX, which has to move Mountain to Central.
/// The one thing that matters is that the loader's own "today" is also UTC: a
/// local-zone window boundary would shift twice a year and silently re-key the
/// work units on those two days.
/// </para>
/// </summary>
internal static class OilXTime
{
    public static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>
    /// The datetime shapes the feeds publish, tried in order. All five were seen in
    /// the live files of 2026-09-30:
    ///
    /// <list type="bullet">
    ///   <item><c>2026-09-30</c> — every <c>RunDate</c>, and date-only columns like
    ///         CargoTracking's <c>LastUpdateDate</c>;</item>
    ///   <item><c>2015-01-28 03:56:20</c> — FloatingStorage's <c>StartDate</c>;</item>
    ///   <item><c>2015-12-13 11:58:43.000</c> — CargoTracking's <c>LoadDate</c>
    ///         (3 fractional digits, usually all zero);</item>
    ///   <item><c>2026-09-30 20:33:45.887052</c> — <c>RunDateTime</c>
    ///         (6 fractional digits).</item>
    /// </list>
    ///
    /// <para>
    /// <c>FFFFFFF</c> accepts one to seven fractional digits, so the 3-digit and
    /// 6-digit forms both match the one pattern; the no-fraction form needs its own
    /// entry because the literal <c>.</c> is not optional in a custom format string.
    /// The <c>T</c>-separated variants are not in any observed file — they are here
    /// because the manifest's own timestamps use that form and a feed that switched
    /// should keep loading rather than drop every row.
    /// </para>
    /// <para>
    /// Every pattern carries a FOUR-digit year, so nothing here depends on
    /// <c>TwoDigitYearMax</c> — the trap that would have turned EOX's 2050 into 1950.
    /// </para>
    /// </summary>
    public static readonly string[] DateTimeFormats =
    {
        "yyyy-MM-dd HH:mm:ss.FFFFFFF",
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-dd",
        "yyyy-MM-ddTHH:mm:ss.FFFFFFF",
        "yyyy-MM-ddTHH:mm:ss"
    };

    /// <summary>Parse one of <see cref="DateTimeFormats"/>; <c>false</c> if none matches.</summary>
    public static bool TryParseDateTime(string raw, out DateTime value) =>
        DateTime.TryParseExact(raw, DateTimeFormats, Inv, DateTimeStyles.None, out value);

    /// <summary>
    /// Parse a <c>yyyy-MM-dd</c> day token — used for the <c>day=</c> query parameter
    /// and for work-unit keys, never for cell values.
    /// </summary>
    public static bool TryParseDay(string raw, out DateOnly value) =>
        DateOnly.TryParseExact(raw, "yyyy-MM-dd", Inv, DateTimeStyles.None, out value);

    /// <summary>The day token the API's <c>day=</c> parameter expects.</summary>
    public static string DayToken(DateOnly day) => day.ToString("yyyy-MM-dd", Inv);

    /// <summary>Today in UTC. See the class remarks for why this is not local time.</summary>
    public static DateOnly Today(DateTime startedAtUtc) => DateOnly.FromDateTime(startedAtUtc);

    /// <summary>
    /// The inclusive window <c>[today - daysBack, today]</c>, oldest first.
    ///
    /// <para>
    /// Inclusive at BOTH ends, so the shipped <c>DaysBack = 30</c> yields 31 days —
    /// which is what the loader request asked for and what Genscape/ICE do.
    /// A negative <paramref name="daysBack"/> is clamped to 0 rather than throwing;
    /// the module warns about it at startup.
    /// </para>
    /// </summary>
    public static IReadOnlyList<DateOnly> Window(DateOnly today, int daysBack)
    {
        if (daysBack < 0) daysBack = 0;

        var days = new List<DateOnly>(daysBack + 1);
        for (var i = daysBack; i >= 0; i--)
            days.Add(today.AddDays(-i));

        return days;
    }

    /// <summary>
    /// How many whole days old <paramref name="day"/> is relative to
    /// <paramref name="today"/>. Negative for a future day, which
    /// <see cref="IsSettled"/> treats as hot.
    /// </summary>
    public static int AgeInDays(DateOnly day, DateOnly today) => today.DayNumber - day.DayNumber;

    /// <summary>
    /// A day is SETTLED once it is older than <paramref name="settledAfterDays"/>.
    ///
    /// <para>
    /// Settled means the vendor's published file for that day can no longer change
    /// (<c>docs/apis/OilX.md</c> §2 Behaviour 8), so the work unit gets a stable
    /// resume key, is loaded once and is then skipped without any download. Only a
    /// HOT day is re-pulled, because today's file set is still growing.
    /// </para>
    /// </summary>
    public static bool IsSettled(DateOnly day, DateOnly today, int settledAfterDays) =>
        AgeInDays(day, today) > Math.Max(0, settledAfterDays);
}
