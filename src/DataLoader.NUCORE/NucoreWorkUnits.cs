using DataLoader.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace DataLoader.NUCORE;

/// <summary>
/// One unit of NUCORE work: either a business DATE, or a TRADE MONTH for the endpoint that
/// has no date parameter.
/// </summary>
internal sealed class NucoreWorkUnit : WorkUnit
{
    public required NucoreEndpoint Endpoint { get; init; }

    /// <summary>The business date, for a date-partitioned endpoint.</summary>
    public DateTime? Date { get; init; }

    /// <summary>The trade month as <c>yyyyMM</c>, for the trade-month-partitioned endpoint.</summary>
    public string? TradeMonth { get; init; }

    /// <summary>
    /// False once the unit is old enough to be considered final, which is what lets its
    /// resume key be stable and the unit be skipped forever after one success.
    /// </summary>
    public required bool IsHot { get; init; }

    /// <summary>
    /// The resume key. A SETTLED unit's key is stable, so the load log's "already done"
    /// check skips it at zero cost. A HOT unit's key carries a run-varying token, so it is
    /// re-read on every run (or every hour, depending on the strategy).
    /// </summary>
    /// <remarks>
    /// Held separately because <see cref="WorkUnit.Key"/> is an abstract get-only property
    /// and cannot be overridden by an <c>init</c> accessor.
    /// </remarks>
    public required string ResumeKey { get; init; }

    public override string Key => ResumeKey;

    public override string DisplayName =>
        Date is not null
            ? $"{Endpoint.Name} {Date:yyyy-MM-dd}"
            : $"{Endpoint.Name} tradeMonth={TradeMonth}";
}

/// <summary>
/// Produces the work units for one endpoint.
///
/// <para>
/// Nothing here calls the API. Dates are enumerated from configuration and trade months
/// from a fixed range, because every "is there data?" probe against this vendor costs a
/// real query - and an empty day or month answers HTTP 200 with zero rows anyway, which the
/// pipeline records as a legitimate empty read.
/// </para>
/// </summary>
internal sealed class NucoreWorkUnitProvider : IWorkUnitProvider<NucoreWorkUnit>
{
    private readonly NucoreEndpoint _endpoint;
    private readonly NucoreSettings _settings;
    private readonly ILogger _logger;

    public NucoreWorkUnitProvider(NucoreEndpoint endpoint, NucoreSettings settings, ILogger logger)
    {
        _endpoint = endpoint;
        _settings = settings;
        _logger = logger;
    }

    public Task<IReadOnlyList<NucoreWorkUnit>> GetWorkUnitsAsync(LoaderRunContext context)
    {
        var units = _endpoint.Partition == NucorePartition.TradeMonth
            ? BuildTradeMonthUnits(context)
            : BuildDateUnits(context);

        _logger.LogInformation(
            "NUCORE {Endpoint}: {Total} work unit(s) - {Hot} hot, {Settled} settled",
            _endpoint.Name, units.Count, units.Count(u => u.IsHot), units.Count(u => !u.IsHot));

        return Task.FromResult<IReadOnlyList<NucoreWorkUnit>>(units);
    }

    private List<NucoreWorkUnit> BuildDateUnits(LoaderRunContext context)
    {
        var today = NucoreTime.Today();
        var (from, to) = ResolveWindow(context, today);

        var units = new List<NucoreWorkUnit>();
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            // NUCORE publishes on business days only: every weekend sampled returned 200
            // with totalRecords 0. Skipping them removes ~29% of a full-history backfill's
            // units without losing a row. Holidays are NOT skipped - they still come back
            // as legitimate empty reads, because there is no calendar to consult.
            if (_settings.SkipWeekends &&
                day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                continue;

            var isHot = day > today.AddDays(-Math.Max(0, _settings.SettledAfterDays));

            units.Add(new NucoreWorkUnit
            {
                Endpoint = _endpoint,
                Date = day,
                IsHot = isHot,
                ResumeKey = BuildKey($"{_endpoint.Name};date={day:yyyy-MM-dd}", isHot, context)
            });
        }

        return units;
    }

    private List<NucoreWorkUnit> BuildTradeMonthUnits(LoaderRunContext context)
    {
        var today = NucoreTime.Today();

        if (!NucoreTime.TryParseTradeMonth(_settings.TradeMonthFrom, out var first))
        {
            _logger.LogWarning(
                "NUCORE {Endpoint}: TradeMonthFrom '{Value}' is not yyyyMM; falling back to {Fallback}",
                _endpoint.Name, _settings.TradeMonthFrom, "202501");
            first = new DateTime(2025, 1, 1);
        }

        var last = new DateTime(today.Year, today.Month, 1)
            .AddMonths(Math.Max(0, _settings.TradeMonthsAhead));

        // Everything from this month back is still live enough to re-check; everything
        // older has stopped accumulating system dates. Note that every FUTURE trade month
        // is necessarily inside the hot window, which is correct - those are the open
        // positions that gain a row every business day.
        var settledBefore = new DateTime(today.Year, today.Month, 1)
            .AddMonths(-Math.Max(0, _settings.SettledTradeMonthsBack));

        var units = new List<NucoreWorkUnit>();
        for (var month = first; month <= last; month = month.AddMonths(1))
        {
            var isHot = month >= settledBefore;
            var token = month.ToString("yyyyMM");

            units.Add(new NucoreWorkUnit
            {
                Endpoint = _endpoint,
                TradeMonth = token,
                IsHot = isHot,
                ResumeKey = BuildKey($"{_endpoint.Name};tradeMonth={token}", isHot, context)
            });
        }

        return units;
    }

    private (DateTime From, DateTime To) ResolveWindow(LoaderRunContext context, DateTime today)
    {
        // An explicit run window always wins - that is how a targeted backfill is driven.
        if (context.DateFrom is not null && context.DateTo is not null)
            return (context.DateFrom.Value.Date, context.DateTo.Value.Date);

        var to = today;

        if (!string.IsNullOrWhiteSpace(_settings.StartDate) &&
            DateTime.TryParse(_settings.StartDate, out var configured))
            return (configured.Date, to);

        return (to.AddDays(-Math.Max(0, _settings.DaysBack)), to);
    }

    /// <summary>
    /// Appends the run-varying token to a hot unit's key. A settled unit's key is returned
    /// unchanged, which is what makes it skippable forever.
    /// </summary>
    private string BuildKey(string baseKey, bool isHot, LoaderRunContext context)
    {
        if (!isHot) return baseKey;

        // UTC in every case - see NucoreHotKeyStrategy.
        var token = _settings.HotKeyStrategy switch
        {
            NucoreHotKeyStrategy.RunHour => DateTime.UtcNow.ToString("yyyyMMddHH"),
            NucoreHotKeyStrategy.RunId   => context.RunId.ToString("N"),
            _                            => DateTime.UtcNow.ToString("yyyyMMdd")
        };

        return $"{baseKey};run={token}";
    }
}
