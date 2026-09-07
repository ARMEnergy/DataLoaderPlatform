using System.Globalization;

namespace DataLoader.EOX;

/// <summary>
/// The loader's only clock arithmetic, and its only culture handling.
///
/// <para>
/// <b>Two different zones, on purpose</b> — the ICE loader's posture, for the same
/// reasons:
/// </para>
/// <list type="bullet">
///   <item>
///     <b>The curve-date window is US Central.</b> These are EOX's end-of-day
///     broker curves, snapped at 14:30 Central — which is literally the
///     <c>_1430</c> token in every file name. Enumerating in UTC would ask for
///     "today" before Central has one for several hours each evening, producing a
///     run of pointless <c>NotAvailable</c> rows every night.
///   </item>
///   <item>
///     <b>The hot-key token is UTC.</b> <c>yyyyMMddHH</c> is strictly monotonic
///     only in UTC — <c>01:00</c> Central happens TWICE on a fall-back night, so a
///     local-zone hour token would repeat, the resume key would go backwards, and
///     an already-recorded success would suppress a legitimate re-pull for an hour.
///   </item>
/// </list>
/// </summary>
internal static class EoxTime
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

    /// <summary>The current curve date in US Central (UTC if the zone is unavailable).</summary>
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
    /// whose default culture carries a non-Gregorian calendar the same date prints
    /// a different year — and the resume key silently changes identity.
    /// </para>
    /// </summary>
    public static string Iso(DateOnly value) => value.ToString("yyyy-MM-dd", Inv);

    /// <summary>The <c>yyyyMMdd</c> token as it appears in a file name.</summary>
    public static string FileToken(DateOnly value) => value.ToString("yyyyMMdd", Inv);
}
