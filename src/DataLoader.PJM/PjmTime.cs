namespace DataLoader.PJM;

/// <summary>
/// Time helpers (design §0, §4.4). Work units are UTC days; the Eastern zone is needed only
/// for the EPT echo check, which is what proves the key column really is UTC.
///
/// <para>⚠ Unlike AGSI's enumeration, there is NO UTC fallback here: if neither
/// <c>America/New_York</c> nor <c>Eastern Standard Time</c> resolves, a UTC fallback would fail
/// every row's EPT check, so the module fails the run instead (design §4.4).</para>
/// </summary>
public static class PjmTime
{
    private static readonly Lazy<TimeZoneInfo?> EasternZone = new(ResolveEastern);

    /// <summary>The resolved Eastern zone, or <c>null</c> when the host has neither id.</summary>
    public static TimeZoneInfo? EasternOrNull => EasternZone.Value;

    /// <summary>The Eastern zone; throws when it cannot be resolved.</summary>
    public static TimeZoneInfo Eastern => EasternZone.Value ?? throw new InvalidOperationException(
        "PJM could not resolve the US Eastern time zone (neither 'America/New_York' nor 'Eastern Standard Time'); " +
        "the EPT echo check cannot run, so the run is refused rather than falling back to UTC");

    /// <summary>UTC calendar day of an instant.</summary>
    public static DateOnly UtcDay(DateTime utc) => DateOnly.FromDateTime(utc);

    /// <summary>Converts a UTC wall-clock instant (any Kind) to Eastern prevailing time (Kind Unspecified).</summary>
    public static DateTime ToEastern(DateTime utc)
    {
        var asUtc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        return DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(asUtc, Eastern), DateTimeKind.Unspecified);
    }

    /// <summary>
    /// How many UTC hours of <paramref name="utcDay"/> start at an Eastern wall clock already used by
    /// the previous UTC hour — the repeated hour of a DST fall-back (1 on 2026-11-01: UTC 06:00 is
    /// EST 01:00, the same wall clock as UTC 05:00 EDT 01:00), 0 on every other day.
    /// </summary>
    public static int RepeatedEasternHours(DateOnly utcDay)
    {
        var start = utcDay.ToDateTime(TimeOnly.MinValue);
        var count = 0;
        for (var h = 0; h < 24; h++)
        {
            var t = start.AddHours(h);
            if (ToEastern(t) == ToEastern(t.AddHours(-1))) count++;
        }
        return count;
    }

    private static TimeZoneInfo? ResolveEastern()
    {
        foreach (var id in new[] { "America/New_York", "Eastern Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        return null;
    }
}
