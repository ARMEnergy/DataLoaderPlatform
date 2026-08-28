using System.Text;
using DataLoader.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace DataLoader.EvolutionMarkets;

// =============================================================================
// Work unit + provider (design §2, §3). ONE unit per BUSINESS DATE in the trailing
// window, carrying the two-zone settled/hot resume key.
//
// The provider touches NO database table, holds NO reference cache and has NO
// fail-fast-if-empty guard: /v1/market-data/history is parameterised by DATE ALONE,
// so there is no discovery tier and nothing to barrier against. (Contrast AGSI,
// whose storage units are a country list read back out of a dimension the first
// pipeline populated.)
//
// The generic provider is NEVER registered in DI — it is new'ed inside the module's
// factory closure (design §1.1), so the shared generics cannot collide with another
// loader's.
// =============================================================================

/// <summary>
/// One <b>business date</b> = one <c>arm.FileLog</c> row = one or more HTTP requests (the pager may
/// need several pages, but they are one logical read).
///
/// <para><b>Why per-date units and not one big window request.</b> The endpoint accepts a
/// <c>dateFrom</c>/<c>dateTo</c> range and would happily return the whole 30-day window in one call,
/// which would be fewer requests. Per-date was chosen deliberately for four reasons:</para>
/// <list type="number">
///   <item><b>The 10,000-row cap is silent.</b> The response is a bare JSON array with no envelope,
///     no total count and no next-page link, so a window that exceeded the cap would look exactly
///     like a complete read. One date is ~205 rows — three orders of magnitude of headroom — so the
///     cap becomes unreachable rather than merely handled.</item>
///   <item><b>Per-date audit.</b> Each date gets its own hub row, so "did 2026-08-14 publish?" is a
///     single <c>SELECT</c> against <c>arm.FileLog</c> rather than an inference.</item>
///   <item><b>Per-date resumability.</b> A failure re-runs one date, not the window.</item>
///   <item><b>It sidesteps a vendor argument-validation quirk.</b> Every request sends
///     <c>dateTo == dateFrom</c>, so the reversed-range rule (<c>400</c> when <c>dateTo</c> precedes
///     <c>dateFrom</c>) is structurally unreachable.</item>
/// </list>
/// </summary>
public sealed class EvoMarketDataWorkUnit : WorkUnit
{
    /// <summary>The single business date this unit requests, as both <c>dateFrom</c> and <c>dateTo</c>.</summary>
    public required DateOnly BusinessDate { get; init; }

    /// <summary>
    /// The fully-built RELATIVE request path including <c>dateFrom</c>, <c>dateTo</c>, the pinned
    /// <c>field</c> projection and any <c>datasetName</c> filter — but <b>NOT</b> <c>limit</c>/
    /// <c>offset</c>, which the pager appends per page.
    ///
    /// <para>Carries no credential: the API key is a request HEADER, so no Evolution Markets URL is
    /// ever sensitive and this value is safe to log and to persist in the hub.</para>
    /// </summary>
    public required string RequestPath { get; init; }

    public required string KeyValue { get; init; }
    public override string Key => KeyValue;
    public override string DisplayName => $"EvolutionMarkets market-data {EvoTime.Iso(BusinessDate)}";
}

/// <summary>
/// Enumerates the units from the trailing date window <b>and nothing else</b> (design §2):
/// <c>EvoTime.ResolveWindow(context.StartedAtUtc, DaysBack)</c> → one unit per calendar date in
/// <c>[runDate−(DaysBack−1) … runDate]</c>, US Central.
///
/// <para>Each unit's resume key uses the two-zone rule (design §3.3): a date older than
/// <see cref="EvoSettings.SettledAfterDays"/> gets a <b>STABLE</b> key (loaded once, then a cheap
/// <c>core.LoadLog</c> skip forever); a recent one gets a <b>run-varying HOT</b> key so it is
/// re-pulled and upserted idempotently through the PK MERGE. At the shipped 30/30 default the max
/// age is 29, so the settled zone is empty and the whole window is hot.</para>
/// </summary>
public sealed class EvoMarketDataWorkUnitProvider : IWorkUnitProvider<EvoMarketDataWorkUnit>
{
    private readonly EvoSettings _settings;
    private readonly ILogger _logger;

    public EvoMarketDataWorkUnitProvider(EvoSettings settings, ILogger logger)
    {
        _settings = settings;
        _logger = logger;
    }

    /// <summary>
    /// Builds the relative request path for one business date. <c>internal static</c> so the tests
    /// pin the exact query string — including the pinned <c>field</c> projection — rather than
    /// re-deriving it.
    ///
    /// <para><b>Date formatting is LOAD-BEARING:</b> <c>dateFrom</c>/<c>dateTo</c> must be
    /// <c>yyyy-MM-dd</c> INVARIANT. Any other format returns
    /// <c>400 "dateFrom invalid format. Should be yyyy-MM-dd"</c>, which this loader treats as a hard
    /// failure (design §5.4).</para>
    ///
    /// <para><c>datasetName</c> is URL-escaped because the live value contains a <c>/</c>
    /// (<c>EVOID/USNaturalGasIndex</c>), which would otherwise change the request path.</para>
    /// </summary>
    internal static string BuildRequestPath(DateOnly businessDate, string? datasetName)
    {
        var iso = EvoTime.Iso(businessDate);
        var sb = new StringBuilder("/v1/market-data/history?dateFrom=")
            .Append(iso)
            .Append("&dateTo=").Append(iso)
            .Append("&field=").Append(EvoRequestFields.Csv);

        if (!string.IsNullOrWhiteSpace(datasetName))
            sb.Append("&datasetName=").Append(Uri.EscapeDataString(datasetName.Trim()));

        return sb.ToString();
    }

    /// <summary>The resume key for one date. <c>internal static</c> so the tests pin the exact format.</summary>
    internal static string BuildKey(DateOnly businessDate, bool settled, string hotToken)
    {
        var baseKey = $"evolutionmarkets:marketdata:{businessDate.ToString("yyyyMMdd", EvoTime.Inv)}";
        return settled ? baseKey : $"{baseKey}:run={hotToken}";
    }

    public Task<IReadOnlyList<EvoMarketDataWorkUnit>> GetWorkUnitsAsync(LoaderRunContext context)
    {
        // Single source of truth for the window — shared with EvolutionMarketsLoadValidator (design §3.2).
        var window = EvoTime.ResolveWindow(context.StartedAtUtc, _settings.DaysBack);
        var hot = EvoTime.HotToken(_settings.HotZoneKeyStrategy, window.RunDate, context.RunId);

        // ⚠ CLAMPED, like DaysBack is inside ResolveWindow. A negative SettledAfterDays would make
        // `age > SettledAfterDays` true for EVERY date — the whole window settles on a stable key, and
        // because an EMPTY 200 completes a unit SUCCESSFULLY, a date that simply had not published yet
        // would be recorded as permanently done and its prices lost with no signal (design §3.5).
        // 0 is the strictest value the rule can meaningfully take (only today stays hot).
        // EvolutionMarketsModule.RunAsync warns whenever this is below DaysBack.
        var settledAfterDays = Math.Max(0, _settings.SettledAfterDays);

        var units = new List<EvoMarketDataWorkUnit>(window.DaysBack);
        var settledCount = 0;

        for (var k = 0; k < window.DaysBack; k++)
        {
            var d = window.Newest.AddDays(-k);

            // ⚠ FUTURE-DATE CLAMP (defence in depth — design §3.2). `Newest = RunDate` already
            // guarantees this, but a future DaysBack/offset edit must not be able to break the
            // invariant silently: the vendor rejects a future dateFrom with a hard 400
            // ("dateFrom must be smaller than current date"), which would fail the unit outright.
            if (d > window.RunDate)
            {
                _logger.LogWarning(
                    "[EvolutionMarkets] skipping future candidate business date {Date} (run date {RunDate}) — " +
                    "the vendor rejects a future dateFrom with HTTP 400",
                    EvoTime.Iso(d), EvoTime.Iso(window.RunDate));
                continue;
            }

            // ⚠ EVERY CALENDAR DAY IS ENUMERATED — there is deliberately NO weekday filter and no
            // holiday calendar. A non-publishing day returns a plain 200 [] which costs one cheap
            // request; a hard-coded calendar would silently skip a date that DID publish.
            //
            // THE LIVE DATA RULES OUT EVERY DERIVABLE CALENDAR. Over the full 60-day retention
            // window (38 published dates, 2026-06-26..2026-08-24):
            //   * no weekend date is EVER present -> a weekday filter LOOKS safe, and is not;
            //   * 4 WEEKDAYS are missing anyway: 2026-07-02 (Thu), 2026-07-03 (Fri) and
            //     2026-07-06 (Mon) -- the US Independence Day cluster -- AND
            //     *** 2026-08-20 (Thu), a plain mid-week day with NO holiday explanation. ***
            // That last one is the whole argument: the gap is not derivable from a weekday rule,
            // from a US federal holiday table, or from anything else the loader could compute, and
            // the vendor publishes no trading calendar at all. Probe every day and let the empty
            // array be the answer (design §3.4). DO NOT "optimise" this loop.
            var ageDays = window.RunDate.DayNumber - d.DayNumber;         // 0 .. DaysBack-1
            var settled = ageDays > settledAfterDays;
            if (settled) settledCount++;

            units.Add(new EvoMarketDataWorkUnit
            {
                BusinessDate = d,
                RequestPath = BuildRequestPath(d, _settings.DatasetName),
                KeyValue = BuildKey(d, settled, hot)
            });
        }

        _logger.LogDebug(
            "[EvolutionMarkets] enumerated {Count} work unit(s) over {From}..{To} ({Hot} hot, {Settled} settled)",
            units.Count, EvoTime.Iso(window.From), EvoTime.Iso(window.To),
            units.Count - settledCount, settledCount);

        return Task.FromResult<IReadOnlyList<EvoMarketDataWorkUnit>>(units);
    }
}
