using System.Globalization;

namespace DataLoader.MISO;

/// <summary>
/// MISO time (design §3). <c>timeInterval.start</c>/<c>.end</c> are wall-clock Eastern
/// STANDARD time — a fixed UTC−05:00 all year, no DST — so <c>utc = local + 5h</c> always.
///
/// <para>⚠ Deliberately no <see cref="TimeZoneInfo"/>: Eastern PREVAILING time would be wrong
/// eight months a year, and every market day (DST days included) has exactly 24 intervals
/// (API doc §3, live-verified 2026-03-08 and 2026-11-01).</para>
/// </summary>
public static class MisoTime
{
    public static readonly TimeSpan EstOffset = TimeSpan.FromHours(5);
    public const string IntervalFormat = "yyyy-MM-dd'T'HH:mm:ss";
    public const string DateFormat = "yyyy-MM-dd";

    /// <summary>The loader's "today": the EST calendar day of <paramref name="utcNow"/>.</summary>
    public static DateOnly TodayEst(DateTime utcNow) => DateOnly.FromDateTime(utcNow - EstOffset);

    /// <summary>EST wall clock → UTC (+5h, always).</summary>
    public static DateTime EstToUtc(DateTime est) => DateTime.SpecifyKind(est + EstOffset, DateTimeKind.Unspecified);

    /// <summary>Strict parse of an interval bound; <c>false</c> on any other shape (offsets included).</summary>
    public static bool TryParseInterval(string? text, out DateTime value) =>
        DateTime.TryParseExact(text, IntervalFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out value);

    public static string Format(DateOnly d) => d.ToString(DateFormat, CultureInfo.InvariantCulture);
}
