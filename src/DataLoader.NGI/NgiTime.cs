using System.Globalization;

namespace DataLoader.NGI;

/// <summary>
/// The issue-date enumeration/validation window (design §3.2), derived <b>once</b> so
/// the work-unit provider (the load window) and <see cref="NgiLoadValidator"/> (the
/// validation window) can never drift apart. The inclusive window is
/// <c>[From .. To]</c> and <c>To == Newest == RunDate</c>.
/// </summary>
internal readonly record struct NgiWindow(DateOnly RunDate, DateOnly Newest, DateOnly From, DateOnly To, int DaysBack);

/// <summary>
/// US-Central run-date basis (design §7). NGI Bidweek is a <b>US</b> gas-market feed
/// whose day boundary the business already runs on, and the vendor publishes no
/// timezone anywhere in the API — every date in both payloads is a bare
/// <c>YYYY-MM-DD</c> with no time component at all, which is exactly why all three
/// date columns are <c>DATE</c>, not <c>DATETIME2</c>.
///
/// <para><b>DST safety:</b> at <b>date</b> granularity the Central calendar date is
/// monotonic non-decreasing across both transitions, so the <c>RunDate</c> hot-key
/// token can never go backwards. This holds only at date granularity — see
/// <see cref="NgiHotKeyStrategy"/> for why an <c>yyyyMMddHH</c> Central token would
/// be unsafe.</para>
///
/// <para><b>Contrast (documented on purpose):</b> AGSI uses CET/Europe, CWG US
/// Eastern, StormVista UTC; IIR stamps its <c>OfflineEvent</c> run date in US
/// Central. NGI matches IIR.</para>
/// </summary>
internal static class NgiTime
{
    internal static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>
    /// A date rendered <c>yyyy-MM-dd</c> under the INVARIANT culture, for every human-facing string:
    /// the work-unit <c>DisplayName</c> (persisted verbatim into <c>core.LoadLog</c>) and the date
    /// placeholders in log messages.
    ///
    /// <para>An interpolated <c>{d:yyyy-MM-dd}</c> uses the AMBIENT culture, so on a host whose default
    /// culture carries a non-Gregorian calendar (e.g. <c>ar-SA</c>) the same date prints a different
    /// calendar's year — and the persisted load-log description would disagree with the
    /// <c>issue_date</c> actually requested. The request path and the resume key were always
    /// invariant; this makes the display strings match them.</para>
    /// </summary>
    public static string Iso(DateOnly value) => value.ToString("yyyy-MM-dd", Inv);

    private static readonly TimeZoneInfo CentralTz;

    /// <summary>
    /// True when neither US-Central time-zone id could be resolved and the window fell back to UTC.
    /// <c>NgiModule.RunAsync</c> logs a warning at run start so a misconfigured host is diagnosable
    /// (design §1.5 step 2) — AGSI does the same for CET.
    /// </summary>
    public static bool UsingUtcFallback { get; }

    static NgiTime()
    {
        CentralTz = ResolveCentral(out var usedFallback);
        UsingUtcFallback = usedFallback;
    }

    private static TimeZoneInfo ResolveCentral(out bool usedFallback)
    {
        foreach (var id in new[] { "America/Chicago", "Central Standard Time" })
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

    /// <summary>The US-Central (CST/CDT) calendar date for a run's UTC start time.</summary>
    public static DateOnly CentralToday(DateTime startedAtUtc) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(startedAtUtc, DateTimeKind.Utc), CentralTz));

    /// <summary>
    /// Resolves the Central run date and the trailing issue-date window for a run (design §3.2).
    /// <paramref name="daysBack"/> is clamped to a minimum of 1.
    ///
    /// <para><b>The window ENDS AT TODAY, never in the future.</b> Unlike AGSI (which offsets by −1
    /// for a publication lag) NGI publishes the issue <i>on</i> its issue date, so today can
    /// legitimately carry a publication. What must be excluded is the future: a future date returns
    /// <c>404</c>, and because a 404 completes its unit successfully, a future date that fell in the
    /// settled zone would be recorded permanently done and never re-probed once it became a real
    /// issue date (design §3.2).</para>
    ///
    /// <para><b>Single source of truth</b> for BOTH the load window (the work-unit provider) and the
    /// validation window (<see cref="NgiLoadValidator"/>). Do not recompute this arithmetic anywhere
    /// else.</para>
    /// </summary>
    public static NgiWindow ResolveWindow(DateTime startedAtUtc, int daysBack)
    {
        var runDate = CentralToday(startedAtUtc);
        var newest = runDate;                       // ends at today — NEVER a future date
        var days = Math.Max(1, daysBack);
        var from = newest.AddDays(-(days - 1));
        return new NgiWindow(runDate, newest, from, newest, days);
    }

    /// <summary>
    /// The <c>{hot}</c> resume-key token for a run (design §3.3): the Central run date
    /// <c>yyyyMMdd</c> under <see cref="NgiHotKeyStrategy.RunDate"/> (re-pull once per calendar day;
    /// a second same-day run idempotently skips), else the run id (re-pull every invocation).
    /// </summary>
    public static string HotToken(NgiHotKeyStrategy strategy, DateOnly runDate, Guid runId) =>
        strategy == NgiHotKeyStrategy.RunDate
            ? runDate.ToString("yyyyMMdd", Inv)
            : runId.ToString("N");
}
