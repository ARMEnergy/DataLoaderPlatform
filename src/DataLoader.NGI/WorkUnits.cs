using DataLoader.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace DataLoader.NGI;

// =============================================================================
// Work units + their providers (design §2, §3). Two closed, MUTUALLY INDEPENDENT
// pipelines, so two unit types: a single undated Locations unit (always hot) and one
// BidWeekData unit per CALENDAR DATE in the trailing window, carrying the two-zone
// settled/hot resume key.
//
// ⚠ THE CRITICAL DIVERGENCE FROM AGSI (design §1.2): the pipelines are NOT coupled.
// In AGSI the /api/about pipeline populates a dimension, a reference provider reads it
// back, and the storage units ARE that country list × the date window — a hard barrier
// with a fail-fast-if-empty guard. NGI has NO such dependency: /bidweekDatafeed.json is
// parameterised by DATE ALONE. So NgiBidWeekWorkUnitProvider touches NO database table,
// holds NO reference cache, and has NO fail-fast-if-empty guard. A BidWeekData-only run
// is completely valid; a BidWeekLocations-only run is valid too; either order (or
// concurrent execution) would be correct.
//
// The generic providers are NEVER registered in DI — each is new'ed inside a module
// factory closure (design §1.1), so the shared generics cannot collide with another
// loader's.
// =============================================================================

/// <summary>
/// Endpoint 2 (<c>GET /bidweekLocations?format=json</c>) work unit — a single <b>undated</b> unit per
/// run, <b>always hot</b> (design §3.1). Refreshes the name↔code crosswalk in
/// <c>arm.BidWeekLocation</c> in place each run (RunDate cadence by default).
/// Key: <c>ngi:locations:run={hot}</c>.
/// </summary>
public sealed class NgiLocationsWorkUnit : WorkUnit
{
    public required string KeyValue { get; init; }
    public override string Key => KeyValue;
    public override string DisplayName => "NGI bidweek locations";
}

/// <summary>
/// Endpoint 1 (<c>GET /bidweekDatafeed.json?issue_date=…</c>) work unit — one <b>candidate issue
/// date</b> = one HTTP request = one <c>arm.FileLog</c> row. There is no per-point unit: one request
/// returns all ~163 points for the date (no paging, no id batching).
/// <see cref="KeyValue"/> is the precomputed two-zone resume key (design §3.3).
/// </summary>
public sealed class NgiBidWeekWorkUnit : WorkUnit
{
    /// <summary>The <c>issue_date</c> query value. Formatted <c>yyyy-MM-dd</c> invariant — any other format is a 400.</summary>
    public required DateOnly IssueDate { get; init; }

    /// <summary>Sanitised request descriptor, used for both the URL and the log line (no credential).</summary>
    public required string RequestPath { get; init; }

    public required string KeyValue { get; init; }
    public override string Key => KeyValue;
    public override string DisplayName => $"NGI bidweek {NgiTime.Iso(IssueDate)}";
}

/// <summary>
/// Emits the single undated Locations unit per run (design §3.1). Always hot: the key varies by
/// <see cref="NgiHotKeyStrategy"/> (<c>RunDate</c> → once per US-Central calendar day; <c>RunId</c> →
/// every invocation), so the crosswalk is refreshed each run.
/// </summary>
public sealed class NgiLocationsWorkUnitProvider : IWorkUnitProvider<NgiLocationsWorkUnit>
{
    private readonly NgiSettings _settings;
    private readonly ILogger _logger;

    public NgiLocationsWorkUnitProvider(NgiSettings settings, ILogger logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public Task<IReadOnlyList<NgiLocationsWorkUnit>> GetWorkUnitsAsync(LoaderRunContext context)
    {
        var runDate = NgiTime.CentralToday(context.StartedAtUtc);
        var hot = NgiTime.HotToken(_settings.HotZoneKeyStrategy, runDate, context.RunId);

        var unit = new NgiLocationsWorkUnit { KeyValue = $"ngi:locations:run={hot}" };
        _logger.LogDebug("[NGI BidWeekLocations] enumerated 1 work unit (key={Key})", unit.KeyValue);
        return Task.FromResult<IReadOnlyList<NgiLocationsWorkUnit>>(new[] { unit });
    }
}

/// <summary>
/// Enumerates the <c>BidWeekData</c> units from the trailing date window <b>and nothing else</b>
/// (design §2): <c>NgiTime.ResolveWindow(context.StartedAtUtc, DaysBack)</c> → one unit per calendar
/// date in <c>[runDate−(DaysBack−1) … runDate]</c>, US Central. No DB read, no reference cache, no
/// barrier, no fail-fast guard.
///
/// <para>Each unit's resume key uses the StormVista two-zone rule (design §3.3): a candidate date
/// older than <see cref="NgiSettings.SettledAfterDays"/> gets a <b>STABLE</b> key (loaded once, then a
/// cheap <c>core.LoadLog</c> skip forever); a recent one gets a <b>run-varying HOT</b> key so it is
/// re-pulled and upserted idempotently through the natural-key MERGE. At the shipped 60/60 default
/// the max age is 59, so the settled zone is empty and the whole window is hot.</para>
/// </summary>
public sealed class NgiBidWeekWorkUnitProvider : IWorkUnitProvider<NgiBidWeekWorkUnit>
{
    private readonly NgiSettings _settings;
    private readonly ILogger _logger;

    public NgiBidWeekWorkUnitProvider(NgiSettings settings, ILogger logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public Task<IReadOnlyList<NgiBidWeekWorkUnit>> GetWorkUnitsAsync(LoaderRunContext context)
    {
        // Single source of truth for the window — shared with NgiLoadValidator (design §3.2).
        var window = NgiTime.ResolveWindow(context.StartedAtUtc, _settings.DaysBack);
        var hot = NgiTime.HotToken(_settings.HotZoneKeyStrategy, window.RunDate, context.RunId);

        // ⚠ CLAMPED, like DaysBack is inside ResolveWindow. A negative SettledAfterDays would make
        // `age > SettledAfterDays` true for EVERY candidate date — the whole window settles on a stable
        // key, and because a 404 completes a unit SUCCESSFULLY a transient 404 would then be recorded
        // as a permanent success and that issue date lost with no signal (design §3.5). 0 is the
        // strictest value the rule can meaningfully take (only today stays hot).
        // NgiModule.RunAsync warns when this is below DaysBack, and warns harder below the
        // recommended floor of 35.
        var settledAfterDays = Math.Max(0, _settings.SettledAfterDays);

        var units = new List<NgiBidWeekWorkUnit>(window.DaysBack);
        for (var k = 0; k < window.DaysBack; k++)
        {
            var d = window.Newest.AddDays(-k);

            // ⚠ FUTURE-DATE CLAMP (defence in depth — design §3.2). `Newest = RunDate` already
            // guarantees this, but a future DaysBack/offset edit must not be able to break the
            // invariant silently: a future date returns 404, a 404 COMPLETES THE UNIT SUCCESSFULLY,
            // so a future date that landed in the SETTLED zone would be recorded permanently done and
            // never re-probed once it became a real issue date.
            if (d > window.RunDate)
            {
                _logger.LogWarning(
                    "[NGI BidWeekData] skipping future candidate issue date {Date} (run date {RunDate})",
                    NgiTime.Iso(d), NgiTime.Iso(window.RunDate));
                continue;
            }

            // ⚠ EVERY CALENDAR DAY IS ENUMERATED — there is deliberately NO business-day or holiday
            // filter, and no day-of-month rule. NGI's own spec claims "issue date will always be a
            // business day"; the LIVE DATA CONTRADICTS IT: 2026-08-01 is a SATURDAY and returned 200
            // with 163 records, while Friday 2026-07-31 returned 404. A weekday filter would have
            // silently missed the entire August 2026 issue while reporting a clean run. There is no
            // vendor-published publication calendar. Probe every day and let the 404 be the answer
            // (design §3.4). DO NOT "optimise" this loop.
            var ageDays = window.RunDate.DayNumber - d.DayNumber;         // 0 .. DaysBack-1
            var stamp = d.ToString("yyyyMMdd", NgiTime.Inv);
            var baseKey = $"ngi:bidweek:{stamp}";

            units.Add(new NgiBidWeekWorkUnit
            {
                IssueDate = d,
                // Date formatting is LOAD-BEARING: issue_date MUST be yyyy-MM-dd invariant. Any other
                // format returns 400, which this loader treats as a hard failure (design §3.2, §5.4).
                RequestPath = $"/bidweekDatafeed.json?issue_date={d.ToString("yyyy-MM-dd", NgiTime.Inv)}",
                // Two-zone key: settled (age > SettledAfterDays) → stable; hot → run-varying.
                KeyValue = ageDays > settledAfterDays ? baseKey : $"{baseKey}:run={hot}"
            });
        }

        _logger.LogDebug(
            "[NGI BidWeekData] enumerated {Count} work unit(s) over {From}..{To} (every calendar day; ~58 of 60 legitimately 404)",
            units.Count, NgiTime.Iso(window.From), NgiTime.Iso(window.To));
        return Task.FromResult<IReadOnlyList<NgiBidWeekWorkUnit>>(units);
    }
}
