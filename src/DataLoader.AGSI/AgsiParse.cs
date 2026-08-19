using System.Globalization;

namespace DataLoader.AGSI;

/// <summary>
/// Tolerant, invariant-culture parsers for AGSI JSON values (design §4). Every
/// numeric measure arrives as a JSON <b>string</b> (e.g. <c>"121.1238"</c>,
/// <c>"-537.3"</c>); a blank / missing / unparseable value becomes <c>null</c>
/// (never a row failure — the API marks every measure NULLable for
/// <c>status:"E"</c>/<c>"N"</c>). Signs are preserved (<c>netWithdrawal</c>,
/// <c>trend</c> are signed).
/// </summary>
internal static class AgsiParse
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Blank / missing / unparseable → <c>null</c>. Keeps sign and decimals.</summary>
    public static decimal? Decimal(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return decimal.TryParse(value.Trim(), NumberStyles.Float, Inv, out var d) ? d : (decimal?)null;
    }

    /// <summary><c>yyyy-MM-dd</c> → <see cref="DateOnly"/>; blank/unparseable → <c>null</c>.</summary>
    public static DateOnly? Date(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return DateOnly.TryParseExact(value.Trim(), "yyyy-MM-dd", Inv, DateTimeStyles.None, out var d) ? d : (DateOnly?)null;
    }

    /// <summary>
    /// <c>yyyy-MM-dd HH:mm:ss</c> → <see cref="DateTime"/> (stored as received, GIE CET/CEST
    /// wall-clock — design §6/§11); blank/unparseable → <c>null</c>.
    /// </summary>
    public static DateTime? DateTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return System.DateTime.TryParseExact(value.Trim(), "yyyy-MM-dd HH:mm:ss", Inv, DateTimeStyles.None, out var dt)
            ? dt
            : (DateTime?)null;
    }
}

/// <summary>
/// The storage enumeration/validation window (design §3.2), derived once so the
/// work-unit provider (load window) and the post-load validator (validation window)
/// can never drift apart. <see cref="Newest"/> is the newest requestable gas day;
/// the inclusive window is <c>[From .. To]</c> (<c>To == Newest</c>).
/// </summary>
internal readonly record struct AgsiWindow(DateOnly RunDate, DateOnly Newest, DateOnly From, DateOnly To, int DaysBack);

/// <summary>
/// CET/Europe run-date basis (design §6). AGSI is European gas-day data published
/// on the CET/CEST calendar, so "newest gas day" is derived from the Central
/// European calendar date, not UTC — a run straddling UTC midnight does not target
/// a not-yet-existent future gas day, and the daily hot re-pull key flips on the
/// same boundary the vendor publishes on.
/// </summary>
internal static class AgsiTime
{
    /// <summary>
    /// Newest requestable gas day = <c>runDate + DateOffsetDays</c>. A gas day D is published
    /// the following morning (sample: run 2026-08-17 returned gas_day 2026-08-16), so today's
    /// CET gas day is not yet requestable — <c>-1</c> targets yesterday as the newest date
    /// (design §3.2). Single source of truth for BOTH the load window and the validation window.
    /// </summary>
    public const int DateOffsetDays = -1;

    private static readonly TimeZoneInfo CetTz;

    /// <summary>
    /// True when neither CET time-zone id could be resolved and enumeration fell back to UTC.
    /// The module logs a warning at run start so a misconfigured host is diagnosable (§6).
    /// </summary>
    public static bool UsingUtcFallback { get; }

    static AgsiTime()
    {
        CetTz = ResolveCet(out var usedFallback);
        UsingUtcFallback = usedFallback;
    }

    private static TimeZoneInfo ResolveCet(out bool usedFallback)
    {
        foreach (var id in new[] { "Europe/Brussels", "Central European Standard Time" })
        {
            try
            {
                var tz = TimeZoneInfo.FindSystemTimeZoneById(id);
                usedFallback = false;
                return tz;
            }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        usedFallback = true; // last-resort fallback; keeps enumeration working on an exotic host
        return TimeZoneInfo.Utc;
    }

    /// <summary>The CET/CEST calendar date for a run's UTC start time.</summary>
    public static DateOnly CetToday(DateTime startedAtUtc) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(
            System.DateTime.SpecifyKind(startedAtUtc, DateTimeKind.Utc), CetTz));

    /// <summary>
    /// Resolves the CET run date and the trailing storage window for a run. <paramref name="daysBack"/>
    /// is clamped to a minimum of 1. Used by the storage work-unit provider and the load validator so
    /// the two windows are always identical.
    /// </summary>
    public static AgsiWindow ResolveWindow(DateTime startedAtUtc, int daysBack)
    {
        var runDate = CetToday(startedAtUtc);
        var newest = runDate.AddDays(DateOffsetDays);
        var days = Math.Max(1, daysBack);
        var from = newest.AddDays(-(days - 1));
        return new AgsiWindow(runDate, newest, from, newest, days);
    }
}
