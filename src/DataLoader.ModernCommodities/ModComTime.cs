using System.Globalization;
using DataLoader.Core.Abstractions;

namespace DataLoader.ModernCommodities;

/// <summary>
/// A resolved request window (design §3.2). <c>[Start .. End]</c> is <b>inclusive at both ends</b>
/// — that is the literal <c>startDate</c>/<c>endDate</c> pair sent to the API and is verified
/// behaviour, not an assumption.
/// </summary>
/// <param name="Today">The run's UTC calendar date (decision D5).</param>
/// <param name="Start">The <c>startDate</c> actually requested, after any history clamp.</param>
/// <param name="End">The <c>endDate</c> actually requested (= <paramref name="Today"/>).</param>
/// <param name="DaysBack">The effective <c>DaysBack</c> used.</param>
/// <param name="Clamped"><c>true</c> when the 6-calendar-month history floor moved <paramref name="Start"/> forward.</param>
internal readonly record struct ModComWindow(DateOnly Today, DateOnly Start, DateOnly End, int DaysBack, bool Clamped)
{
    /// <summary>Inclusive length of the window in calendar days (<c>DaysBack + 1</c> when unclamped).</summary>
    public int LengthDays => End.DayNumber - Start.DayNumber + 1;
}

/// <summary>
/// The loader's <b>only</b> clock arithmetic (design §7). Everything here is <b>UTC</b>
/// (decision D5) and there is deliberately no time-zone lookup, hence no
/// <c>UsingUtcFallback</c> warning of the kind <c>NgiTime</c>/<c>AgsiTime</c> carry: nothing to
/// resolve, nothing to misconfigure, one fewer failure mode.
///
/// <para><b>Why UTC and not US-Central (NGI's basis):</b></para>
/// <list type="bullet">
///   <item><b>It cannot clip.</b> UTC is ahead of both US-Central and the vendor's Mountain zone, so
///     a UTC <c>today</c> as <c>endDate</c> is never <i>behind</i> the venue's day — and a future
///     <c>endDate</c> is verified harmless (<c>200</c>).</item>
///   <item><b><c>yyyyMMddHH</c> is strictly monotonic in UTC and NOT in any DST zone.</b> The resume
///     key is hour-granular, and <c>01:00</c> Central/Mountain occurs <b>twice</b> on a fall-back
///     night — a local-zone hour token would repeat, the key would go backwards, and a legitimate
///     re-pull would be suppressed for an hour.</item>
/// </list>
///
/// <para><b>⚠ What UTC does NOT fix.</b> The payload timestamps (<c>Executed</c>,
/// <c>LastUpdated</c>) carry <b>no offset and no stated zone anywhere</b>. They are stored exactly
/// as given and never shifted; never join them to a UTC-based column assuming they align
/// (design §7 / §12 item 3).</para>
/// </summary>
internal static class ModComTime
{
    internal static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>
    /// A date rendered <c>yyyy-MM-dd</c> under the <b>invariant</b> culture — for the query string,
    /// the sanitised request path, the work-unit <c>DisplayName</c> (persisted verbatim into
    /// <c>core.LoadLog</c>) and every log message.
    ///
    /// <para>An interpolated <c>{d:yyyy-MM-dd}</c> uses the AMBIENT culture, so on a host whose
    /// default culture carries a non-Gregorian calendar the same date prints a different calendar's
    /// year — and the API answers <c>400 Invalid startDate</c>.</para>
    /// </summary>
    public static string Iso(DateOnly value) => value.ToString("yyyy-MM-dd", Inv);

    /// <summary>
    /// The oldest <c>startDate</c> a history-limited endpoint accepts: <c>today.AddMonths(-6).AddDays(1)</c>.
    ///
    /// <para><b>⚠ CALENDAR months, never a day count</b> (decision D6). Probed to the day on
    /// 2026-08-24: <c>startDate=2026-02-24</c> → <c>200</c>; <c>2026-02-23</c> →
    /// <c>400 startDate must be within the last six months</c>. 183 days back is 2026-02-22
    /// (<b>already rejected</b>); 180 days is 2026-02-25 (<b>needlessly tight</b>). The <c>+1</c>
    /// day absorbs the vendor computing "today" in its own (Mountain) zone, removing an entire class
    /// of scheduled failure for the hours when UTC and Mountain disagree on the date.</para>
    /// </summary>
    public static DateOnly HistoryFloor(DateOnly today) => today.AddMonths(-6).AddDays(1);

    /// <summary>
    /// Resolves the request window for a run — the <b>single source of truth</b> shared by every
    /// work-unit provider (the load window) and <see cref="ModernCommoditiesLoadValidator"/> (the
    /// validation window), so the two can never drift apart. <b>Do not recompute this arithmetic
    /// anywhere else.</b>
    ///
    /// <para><c>start = end − DaysBack</c> spans <c>DaysBack + 1</c> calendar days because both ends
    /// are inclusive: at the shipped <c>DaysBack = 30</c> the window is <b>31</b> days. That is
    /// decision D13 <i>as written</i> and is deliberately not NGI's <c>end.AddDays(-(days-1))</c> —
    /// the extra day is free overlap that guarantees no gap between consecutive daily windows.
    /// <b>Do not "fix" it to 30 days.</b></para>
    ///
    /// <para><b>⚠ An inverted range fails SILENTLY as an empty <c>200</c></b> (verified —
    /// <c>startDate</c> after <c>endDate</c> returns a header-only body, <b>not</b> an error). A
    /// window-computation bug would therefore load zero rows forever without a single error, so this
    /// method <b>throws</b> on <c>start &gt; end</c> and the readers assert it again per request
    /// (design §3.2 #5).</para>
    /// </summary>
    public static ModComWindow ResolveWindow(DateTime startedAtUtc, int daysBack, bool historyLimited)
    {
        var today = DateOnly.FromDateTime(startedAtUtc);   // UTC calendar date (decision D5)
        var end = today;                                   // endDate = UTC today; a future endDate is verified harmless
        var days = Math.Max(0, daysBack);
        var start = end.AddDays(-days);                    // D13, LITERALLY: endDate - DaysBack (inclusive both ends)

        var clamped = false;
        if (historyLimited)
        {
            var floor = HistoryFloor(today);
            if (start < floor)
            {
                start = floor;
                clamped = true;
            }
        }

        if (start > end)
            throw new InvalidOperationException(
                $"ModernCommodities window is inverted (start={Iso(start)} > end={Iso(end)}); " +
                "an inverted range returns a SILENT empty 200 from the API, so this is failed loudly instead " +
                "(design §3.2 #5). Check DaysBack / the history clamp.");

        return new ModComWindow(today, start, end, days, clamped);
    }

    /// <summary>
    /// The <c>{hot}</c> resume-key token for a run (design §3.3), always on the <b>UTC</b> clock.
    /// <see cref="ModComHotKeyStrategy.RunHour"/> (the default) is <c>yyyyMMddHH</c> — one re-pull
    /// per clock hour, a same-hour re-run idempotently skips.
    /// </summary>
    public static string HotToken(ModComHotKeyStrategy strategy, LoaderRunContext context) => strategy switch
    {
        ModComHotKeyStrategy.RunDate => DateOnly.FromDateTime(context.StartedAtUtc).ToString("yyyyMMdd", Inv),
        ModComHotKeyStrategy.RunId => context.RunId.ToString("N"),
        _ => context.StartedAtUtc.ToString("yyyyMMddHH", Inv),   // RunHour (default)
    };
}
