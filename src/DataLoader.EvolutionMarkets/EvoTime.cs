using System.Globalization;

namespace DataLoader.EvolutionMarkets;

/// <summary>
/// The business-date enumeration/validation window (design §3.2), derived <b>once</b> so the
/// work-unit provider (the load window) and <see cref="EvolutionMarketsLoadValidator"/> (the
/// validation window) can never drift apart. The inclusive window is <c>[From .. To]</c> and
/// <c>To == Newest == RunDate</c>.
/// </summary>
internal readonly record struct EvoWindow(DateOnly RunDate, DateOnly Newest, DateOnly From, DateOnly To, int DaysBack);

/// <summary>
/// US-Central run-date basis (design §7). <c>EVOID/USNaturalGasIndex</c> is a <b>US natural gas</b>
/// index whose day boundary the business already runs on, and the feed's business date is a bare
/// <c>YYYY-MM-DD</c> with no timezone — which is exactly why <c>BusinessDate</c> is <c>DATE</c>.
///
/// <para><b>DST safety:</b> at <b>date</b> granularity the Central calendar date is monotonic
/// non-decreasing across both transitions, so the <c>RunDate</c> hot-key token can never go
/// backwards. This holds only at date granularity — see <see cref="EvoHotKeyStrategy"/> for why an
/// <c>yyyyMMddHH</c> Central token would be unsafe.</para>
///
/// <para><b>Why Central is also the SAFE choice against the vendor's future-date rule.</b> The API
/// rejects a <c>dateFrom</c> in its own future with <c>400 "dateFrom must be smaller than current
/// date"</c>. US Central is always BEHIND UTC (by 5–6 h), so a Central calendar date can never be
/// ahead of the server's UTC date, and the window's newest member can therefore never trip that 400.
/// Choosing a timezone ahead of UTC would make the last day of the window intermittently fail
/// around midnight.</para>
///
/// <para><b>Contrast (documented on purpose):</b> AGSI uses CET/Europe, CWG US Eastern, StormVista
/// UTC; NGI and IIR use US Central. This loader matches NGI/IIR.</para>
/// </summary>
internal static class EvoTime
{
    internal static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>
    /// A date rendered <c>yyyy-MM-dd</c> under the INVARIANT culture, for every human-facing string:
    /// the work-unit <c>DisplayName</c> (persisted verbatim into <c>core.LoadLog</c>) and the date
    /// placeholders in log messages.
    ///
    /// <para>An interpolated <c>{d:yyyy-MM-dd}</c> uses the AMBIENT culture, so on a host whose
    /// default culture carries a non-Gregorian calendar (e.g. <c>ar-SA</c>) the same date prints a
    /// different calendar's year — and the persisted load-log description would disagree with the
    /// <c>dateFrom</c> actually requested. The request path and the resume key are always invariant;
    /// this keeps the display strings matching them.</para>
    /// </summary>
    public static string Iso(DateOnly value) => value.ToString("yyyy-MM-dd", Inv);

    private static readonly TimeZoneInfo CentralTz;

    /// <summary>
    /// True when neither US-Central time-zone id could be resolved and the window fell back to UTC.
    /// <c>EvolutionMarketsModule.RunAsync</c> logs a warning at run start so a misconfigured host is
    /// diagnosable (design §1.5 step 2) — AGSI/NGI do the same.
    /// </summary>
    public static bool UsingUtcFallback { get; }

    static EvoTime()
    {
        CentralTz = ResolveCentral(out var usedFallback);
        UsingUtcFallback = usedFallback;
    }

    /// <summary>
    /// Resolves US Central by IANA id first, then the Windows id. Falling back to UTC keeps
    /// enumeration working on an exotic host and is only ever a boundary shift, never a crash —
    /// and UTC is still never AHEAD of the server clock, so the future-date 400 stays unreachable.
    /// </summary>
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
        usedFallback = true;
        return TimeZoneInfo.Utc;
    }

    /// <summary>The US-Central (CST/CDT) calendar date for a run's UTC start time.</summary>
    public static DateOnly CentralToday(DateTime startedAtUtc) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(startedAtUtc, DateTimeKind.Utc), CentralTz));

    /// <summary>
    /// Resolves the Central run date and the trailing business-date window for a run (design §3.2).
    /// <paramref name="daysBack"/> is clamped to a minimum of 1.
    ///
    /// <para><b>The window ENDS AT TODAY, never in the future.</b> Today is deliberately INCLUDED
    /// even though this is an end-of-day feed that has never yet published for the current date:
    /// requesting it returns a plain <c>200 []</c> (verified live), which the status matrix treats as
    /// a legitimate empty read, so including it costs one cheap request and guarantees that a date
    /// published earlier than expected is never missed. A FUTURE date, by contrast, is a hard
    /// <c>400</c> from the vendor and must never be enumerated.</para>
    ///
    /// <para><b>Single source of truth</b> for BOTH the load window (the work-unit provider) and the
    /// validation window (<see cref="EvolutionMarketsLoadValidator"/>). Do not recompute this
    /// arithmetic anywhere else.</para>
    /// </summary>
    public static EvoWindow ResolveWindow(DateTime startedAtUtc, int daysBack)
    {
        var runDate = CentralToday(startedAtUtc);
        var newest = runDate;                       // ends at today — NEVER a future date
        var days = Math.Max(1, daysBack);
        var from = newest.AddDays(-(days - 1));
        return new EvoWindow(runDate, newest, from, newest, days);
    }

    /// <summary>
    /// The <c>{hot}</c> resume-key token for a run (design §3.3): the Central run date
    /// <c>yyyyMMdd</c> under <see cref="EvoHotKeyStrategy.RunDate"/> (re-pull once per calendar day;
    /// a second same-day run idempotently skips), else the run id (re-pull every invocation).
    /// </summary>
    public static string HotToken(EvoHotKeyStrategy strategy, DateOnly runDate, Guid runId) =>
        strategy == EvoHotKeyStrategy.RunDate
            ? runDate.ToString("yyyyMMdd", Inv)
            : runId.ToString("N");
}
