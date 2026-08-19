using System.Globalization;
using DataLoader.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace DataLoader.AGSI;

// =============================================================================
// Work units + their providers (design §3). Two closed pipelines, so two unit
// types: a single undated "entities" unit (always hot) and one "storage" unit per
// (countryCode, date) with the StormVista two-zone resume key. The generic
// providers are NEVER registered in DI — each is new'ed inside the module factory
// closure (design §1.1), so the shared generics can't collide.
// =============================================================================

/// <summary>
/// Endpoint 1 (<c>/api/about</c>) work unit — a single undated unit per run, always
/// hot (design §3.1). Refreshes <c>arm.GasStorageEntity</c> in place each run.
/// </summary>
public sealed class AgsiEntitiesWorkUnit : WorkUnit
{
    public required string KeyValue { get; init; }
    public override string Key => KeyValue;
    public override string DisplayName => "AGSI entities /api/about";
}

/// <summary>
/// Endpoint 2 (<c>/api?country=&amp;date=</c>) work unit — one <c>(country, date)</c>
/// request. The persisted fact links to the country via <see cref="EntityId"/> (not any
/// code); <see cref="CountryCode"/> (lowercased) is used only for the request URL and the
/// resume key (design §2). <see cref="KeyValue"/> is the precomputed two-zone resume key
/// (design §3.3).
/// </summary>
public sealed class AgsiStorageWorkUnit : WorkUnit
{
    public required int EntityId { get; init; }         // FK → arm.GasStorageEntity(Id); stamped onto the fact row
    public required string CountryCode { get; init; }   // lowercased for the URL / resume key
    public required DateOnly Date { get; init; }        // requested gas day
    public required string RequestPath { get; init; }   // sanitized descriptor for logging (no secret)
    public required string KeyValue { get; init; }
    public override string Key => KeyValue;
    public override string DisplayName => $"AGSI storage {CountryCode} {Date:yyyy-MM-dd}";
}

/// <summary>
/// Emits the single undated entities unit per run (design §3.1). Always hot: the
/// key varies by <see cref="AgsiHotKeyStrategy"/> (RunDate → once per CET calendar
/// day; RunId → every invocation), so <c>/api/about</c> is refreshed each run.
/// </summary>
public sealed class AgsiEntitiesWorkUnitProvider : IWorkUnitProvider<AgsiEntitiesWorkUnit>
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly AgsiSettings _settings;
    private readonly ILogger _logger;

    public AgsiEntitiesWorkUnitProvider(AgsiSettings settings, ILogger logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public Task<IReadOnlyList<AgsiEntitiesWorkUnit>> GetWorkUnitsAsync(LoaderRunContext context)
    {
        var runDate = AgsiTime.CetToday(context.StartedAtUtc);
        var hot = _settings.HotZoneKeyStrategy == AgsiHotKeyStrategy.RunDate
            ? runDate.ToString("yyyyMMdd", Inv)
            : context.RunId.ToString("N");

        var unit = new AgsiEntitiesWorkUnit { KeyValue = $"agsi:entities:run={hot}" };
        _logger.LogDebug("[AGSI About] enumerated 1 entities work unit (key={Key})", unit.KeyValue);
        return Task.FromResult<IReadOnlyList<AgsiEntitiesWorkUnit>>(new[] { unit });
    }
}

/// <summary>
/// Enumerates the storage work units: the distinct country codes from the
/// just-refreshed <c>arm.GasStorageEntity</c> (via <see cref="IAgsiCountryProvider"/>,
/// fail-fast if empty) crossed with the trailing <see cref="AgsiSettings.DaysBack"/>
/// window ending at the newest requestable gas day (<c>runDate + DateOffsetDays</c>,
/// CET). Each unit's resume key uses the StormVista two-zone rule (design §3.3):
/// a requested date older than <see cref="AgsiSettings.SettledAfterDays"/> gets a
/// STABLE key (loaded once, then a cheap <c>core.LoadLog</c> skip forever); a recent
/// one gets a run-varying HOT key so the hot window is re-pulled and upserted
/// idempotently.
/// </summary>
public sealed class AgsiStorageWorkUnitProvider : IWorkUnitProvider<AgsiStorageWorkUnit>
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly AgsiSettings _settings;
    private readonly IAgsiCountryProvider _countryProvider;
    private readonly ILogger _logger;

    public AgsiStorageWorkUnitProvider(AgsiSettings settings, IAgsiCountryProvider countryProvider, ILogger logger)
    {
        _settings = settings;
        _countryProvider = countryProvider;
        _logger = logger;
    }

    public async Task<IReadOnlyList<AgsiStorageWorkUnit>> GetWorkUnitsAsync(LoaderRunContext context)
    {
        // Distinct (Id, Code) entity pairs from the just-refreshed dimension (fail-fast if empty).
        var countries = await _countryProvider.GetCountriesAsync(context.CancellationToken).ConfigureAwait(false);

        // Single source of truth for the load window (shared with the validator — design §3.2).
        var window = AgsiTime.ResolveWindow(context.StartedAtUtc, _settings.DaysBack);

        var hot = _settings.HotZoneKeyStrategy == AgsiHotKeyStrategy.RunDate
            ? window.RunDate.ToString("yyyyMMdd", Inv)
            : context.RunId.ToString("N");

        var units = new List<AgsiStorageWorkUnit>(window.DaysBack * countries.Count);
        for (var k = 0; k < window.DaysBack; k++)
        {
            var d = window.Newest.AddDays(-k);
            var ageDays = window.RunDate.DayNumber - d.DayNumber;
            foreach (var country in countries)
            {
                // Lowercase for the case-insensitive API + the resume key; the fact links back via
                // EntityId, so no code echo/case matching is needed (design §2).
                var code = country.Code.Trim().ToLowerInvariant();
                if (code.Length == 0) continue;

                var dateStr = d.ToString("yyyy-MM-dd", Inv);
                var baseKey = $"agsi:storage:{code}:{d.ToString("yyyyMMdd", Inv)}";
                units.Add(new AgsiStorageWorkUnit
                {
                    EntityId = country.Id,
                    CountryCode = code,
                    Date = d,
                    RequestPath = $"/api?country={code}&date={dateStr}",
                    // Two-zone key: settled (age > SettledAfterDays) → stable; hot → run-varying.
                    KeyValue = ageDays > _settings.SettledAfterDays ? baseKey : $"{baseKey}:run={hot}"
                });
            }
        }

        _logger.LogDebug("[AGSI Storage] enumerated {Count} work unit(s) over {Codes} entity/code(s) × {Days} day(s)",
            units.Count, countries.Count, window.DaysBack);
        return units;
    }
}
