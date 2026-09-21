using System.Globalization;

namespace DataLoader.NGX;

/// <summary>
/// Every time- and number-shaped conversion this loader performs, in one place, because
/// each of them is silently wrong in a way that type-checks.
///
/// <list type="number">
///   <item>
///     <b>Timestamps are converted to US CENTRAL.</b> The vendor stamps its own
///     Mountain offset (<c>-07:00</c> in winter, <c>-06:00</c> in summer). The
///     incumbent loader stores Central, and this loader matches it byte-for-byte:
///     <c>2026-09-01T06:37:06-06:00</c> lands as <c>2026-09-01 07:37:06</c> and
///     <c>2026-01-15T14:14:54-07:00</c> as <c>2026-01-15 15:14:54</c> (both verified
///     against <c>dbo.StripTradingSummary</c>). Getting this wrong is a ONE HOUR
///     error, and <c>TradeDateTime</c> is a primary key component — a wrong hour
///     forks the key instead of merely mis-stamping the row.
///   </item>
///   <item>
///     <b>The conversion goes through the offset, never through a fixed delta.</b>
///     Mountain and Central happen to shift on the same dates today, so "add one
///     hour" is right all year — right up until it is not. <see cref="ToCentral"/>
///     converts the parsed <see cref="DateTimeOffset"/> to the Central zone, so the
///     answer stays correct if the vendor ever stamps UTC, or if either zone's rules
///     change.
///   </item>
///   <item>
///     <b>Amounts carry THOUSANDS SEPARATORS.</b> <c>&lt;amount&gt;313,100&lt;/amount&gt;</c>
///     is three hundred thirteen thousand one hundred. A plain
///     <c>decimal.Parse(s, InvariantCulture)</c> REJECTS it, and a parse under a
///     comma-decimal culture would read it as 313.1. <see cref="ParseAmount"/> pins
///     both the style and the culture.
///   </item>
///   <item>
///     <b>Request dates use the vendor's own <c>d-MMMM-yyyy</c> spelling</b>
///     (<c>1-September-2026</c>), which is month-NAME based and therefore
///     culture-sensitive; it is formatted invariantly so a server with a non-English
///     locale does not send <c>1-septembre-2026</c> and get an empty window back.
///   </item>
/// </list>
/// </summary>
internal static class NgxTime
{
    internal static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>
    /// US Central. Resolved once; the Windows id is tried first because that is where
    /// this platform runs, with the IANA id as the fallback so the loader and its
    /// tests behave identically on a Linux build agent.
    /// </summary>
    internal static readonly TimeZoneInfo Central = ResolveCentral();

    private static TimeZoneInfo ResolveCentral()
    {
        foreach (var id in new[] { "Central Standard Time", "America/Chicago" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }

        // Deliberately fatal rather than silently falling back to UTC: a UTC fallback
        // would shift every timestamp by five or six hours and fork every strip key,
        // and it would do so quietly on every row.
        throw new InvalidOperationException(
            "NGX: neither 'Central Standard Time' nor 'America/Chicago' is available on this " +
            "machine. Every NGX timestamp is stored in US Central, so there is no safe default.");
    }

    /// <summary>
    /// The offset-aware source timestamp, expressed in US Central, with the offset
    /// dropped so it fits <c>DATETIME2</c>. Returns <see cref="DateTimeKind.Unspecified"/>
    /// — the value is a wall-clock reading in a known zone, not a UTC instant, and
    /// tagging it Local would make it move again on a machine that is not in Central.
    /// </summary>
    internal static DateTime ToCentral(DateTimeOffset value) =>
        DateTime.SpecifyKind(
            TimeZoneInfo.ConvertTime(value, Central).DateTime,
            DateTimeKind.Unspecified);

    /// <summary>
    /// The run's "today" in US Central. Used for <c>ExecutionDate</c> and for sizing
    /// both request windows.
    ///
    /// <para>Central, not the server's local zone and not UTC: <c>ExecutionDate</c> is
    /// a primary key component, so a run that starts at 23:30 Central would otherwise
    /// stamp TOMORROW under UTC and write a whole extra snapshot generation.</para>
    /// </summary>
    internal static DateOnly Today(DateTime startedAtUtc) =>
        DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.SpecifyKind(startedAtUtc, DateTimeKind.Utc), Central));

    /// <summary>The first of the month <paramref name="months"/> away from <paramref name="from"/>.</summary>
    internal static DateOnly FirstOfMonthOffset(DateOnly from, int months)
    {
        var anchor = new DateTime(from.Year, from.Month, 1).AddMonths(months);
        return new DateOnly(anchor.Year, anchor.Month, 1);
    }

    /// <summary>
    /// The vendor's request-date spelling, e.g. <c>1-September-2026</c>. Invariant so
    /// the month name is always English.
    /// </summary>
    internal static string RequestDate(DateOnly d) => d.ToString("d-MMMM-yyyy", Inv);

    /// <summary>ISO, for keys and log lines.</summary>
    internal static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", Inv);

    /// <summary>
    /// A <c>&lt;amount&gt;</c> / price / volume value. Accepts thousands separators
    /// (see the type remarks) and a leading sign; returns null for absent or
    /// unparseable text rather than throwing, so one malformed number costs one
    /// column rather than the whole batch.
    /// </summary>
    internal static decimal? ParseAmount(string? text) =>
        decimal.TryParse(
            text, NumberStyles.Number | NumberStyles.AllowLeadingSign, Inv, out var value)
            ? value
            : null;

    /// <summary>An <c>&lt;duration&gt;</c> / <c>&lt;numberOfTrades&gt;</c> / id value.</summary>
    internal static int? ParseInt(string? text) =>
        int.TryParse(text, NumberStyles.Integer | NumberStyles.AllowThousands, Inv, out var value)
            ? value
            : null;

    /// <summary>An ISO <c>yyyy-MM-dd</c> element such as <c>priceEffectiveStart</c>.</summary>
    internal static DateOnly? ParseDate(string? text) =>
        DateOnly.TryParseExact(text, "yyyy-MM-dd", Inv, DateTimeStyles.None, out var value)
            ? value
            : null;

    /// <summary>
    /// Matches a TIME followed by a UTC designator or numeric offset — <c>…06:37:06Z</c>,
    /// <c>…06:37:06-06:00</c>, <c>…06:37-0600</c>. Used to REJECT a timestamp that
    /// carries no offset.
    ///
    /// <para>The leading <c>\d{2}:\d{2}</c> is load-bearing, not decoration: without it
    /// the trailing <c>-01</c> of a bare date like <c>2026-09-01</c> reads as a
    /// <c>-HH</c> offset and the date sails through as midnight local.</para>
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex HasOffset =
        new(@"\d{2}:\d{2}(:\d{2}(\.\d+)?)?(Z|[+\-]\d{2}:?\d{2})$",
            System.Text.RegularExpressions.RegexOptions.Compiled |
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// An offset-bearing timestamp such as <c>2026-09-01T06:37:06-06:00</c>, returned
    /// in US Central.
    ///
    /// <para>
    /// ⚠ <b>The offset is REQUIRED, and text without one is rejected rather than
    /// guessed at.</b> <see cref="DateTimeOffset.TryParse(string, IFormatProvider, DateTimeStyles, out DateTimeOffset)"/>
    /// assumes the MACHINE'S LOCAL ZONE for an offset-less timestamp, and
    /// <see cref="ToCentral"/> would then convert from whatever zone the server happens
    /// to sit in — shifting the value on any host that is not already Central. Since
    /// <c>TradeDateTime</c> is a primary key component that would fork the key, host by
    /// host. Returning null instead drops the record at the reader's key check, loudly.
    /// </para>
    /// <para>
    /// (<see cref="DateTimeStyles.RoundtripKind"/> is deliberately NOT passed: the
    /// <see cref="DateTimeOffset"/> overload silently strips it, so naming it would
    /// only suggest a protection that is not in force. The explicit offset check below
    /// is the protection.)
    /// </para>
    /// </summary>
    internal static DateTime? ParseCentralTimestamp(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var trimmed = text.Trim();
        if (!HasOffset.IsMatch(trimmed)) return null;

        return DateTimeOffset.TryParse(trimmed, Inv, DateTimeStyles.None, out var value)
            ? ToCentral(value)
            : null;
    }

    /// <summary>
    /// A <c>true</c> / <c>false</c> element. Anything else (including an empty
    /// element) is null rather than false — the three BIT columns this feeds are
    /// nullable, and guessing false would be indistinguishable from a real false.
    /// </summary>
    internal static bool? ParseBool(string? text) =>
        bool.TryParse(text, out var value) ? value : null;
}
