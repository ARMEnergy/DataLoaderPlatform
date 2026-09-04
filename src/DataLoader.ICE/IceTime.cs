using System.Globalization;

namespace DataLoader.ICE;

/// <summary>
/// The loader's only clock arithmetic.
///
/// <para>
/// <b>Two different zones, on purpose:</b>
/// </para>
/// <list type="bullet">
///   <item>
///     <b>The trade-date window is US Central.</b> ICE settlement files are stamped
///     with the exchange's business date, and Central is the business day for these
///     products. Enumerating in UTC would ask for "today" hours before ICE has one,
///     producing a run of pointless <c>NotAvailable</c> rows every evening.
///   </item>
///   <item>
///     <b>The hot-key token is UTC.</b> <c>yyyyMMddHH</c> is strictly monotonic only
///     in UTC — <c>01:00</c> Central occurs TWICE on a fall-back night, so a
///     local-zone hour token would repeat, the resume key would go backwards, and an
///     already-recorded success would suppress a legitimate re-pull for an hour.
///   </item>
/// </list>
/// </summary>
internal static class IceTime
{
    internal static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>
    /// US Central, or null when the host has no time-zone database entry for it.
    /// Both the Windows and IANA ids are tried; callers fall back to UTC and warn
    /// rather than throwing, because a missing tz database must not stop a load.
    /// </summary>
    public static readonly TimeZoneInfo? CentralTz = ResolveCentral();

    private static TimeZoneInfo? ResolveCentral()
    {
        foreach (var id in new[] { "Central Standard Time", "America/Chicago" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        return null;
    }

    /// <summary>True when <see cref="Today"/> is falling back to UTC. Drives a startup warning.</summary>
    public static bool UsingUtcFallback => CentralTz is null;

    /// <summary>The current trade date in US Central (UTC if the zone is unavailable).</summary>
    public static DateOnly Today(DateTime utcNow) =>
        DateOnly.FromDateTime(CentralTz is null
            ? utcNow
            : TimeZoneInfo.ConvertTimeFromUtc(utcNow, CentralTz));

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
}
