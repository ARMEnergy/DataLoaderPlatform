using DataLoader.Core.Abstractions;
using DataLoader.EnergyAspects.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.EnergyAspects;

/// <summary>
/// Enumerates work units for one Energy Aspects run:
///
///   1. Refreshes the dataset mappings from the API (writes them to DB).
///   2. Reads the active mappings back from the DB.
///   3. For each active mapping, expands the run's date range into
///      <c>TimeseriesWindowDays</c>-sized windows.
///   4. Yields one <see cref="EnergyAspectsWorkUnit"/> per (mapping × window).
///
/// This is the loader-specific "what to do" knowledge. Once it produces a
/// flat list of units, the generic pipeline takes over.
/// </summary>
public sealed class EnergyAspectsWorkUnitProvider : IWorkUnitProvider<EnergyAspectsWorkUnit>
{
    private readonly EnergyAspectsApiSource _api;
    private readonly EnergyAspectsSink _sink;        // re-used for mapping persistence
    private readonly EnergyAspectsSettings _settings;
    private readonly ILogger<EnergyAspectsWorkUnitProvider> _logger;

    public EnergyAspectsWorkUnitProvider(
        EnergyAspectsApiSource api,
        EnergyAspectsSink sink,
        IOptions<EnergyAspectsSettings> settings,
        ILogger<EnergyAspectsWorkUnitProvider> logger)
    {
        _api = api;
        _sink = sink;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<EnergyAspectsWorkUnit>> GetWorkUnitsAsync(LoaderRunContext context)
    {
        var ct = context.CancellationToken;

        // Step 1 — refresh mappings from the API
        _logger.LogInformation("Refreshing dataset mappings from API");
        var fromApi = await _api.GetDatasetMappingsAsync(ct).ConfigureAwait(false);
        await _sink.BulkUpsertMappingsAsync(fromApi, ct).ConfigureAwait(false);
        _logger.LogInformation("Refreshed {Count} mappings", fromApi.Count);

        // Step 2 — read active mappings back from DB (single source of truth)
        var mappings = await _sink.GetActiveMappingsAsync(ct).ConfigureAwait(false);
        if (mappings.Count == 0)
        {
            _logger.LogWarning("No active mappings in DB after refresh");
            return Array.Empty<EnergyAspectsWorkUnit>();
        }

        // Step 3 — resolve date range. Prefer loader-specific config; fall
        // back to the platform-supplied context dates.
        var dateFrom = context.StartedAtUtc.Date.AddDays(-_settings.DaysBackStart);
        var dateTo = context.StartedAtUtc.Date.AddDays(-_settings.DaysBackEnd);

        if (dateFrom > dateTo)
            throw new InvalidOperationException(
                $"DateFrom ({dateFrom:yyyy-MM-dd}) is after DateTo ({dateTo:yyyy-MM-dd})");

        // Step 4 — expand into (mapping × window) units
        var window = Math.Max(1, _settings.TimeseriesWindowDays);
        var units = new List<EnergyAspectsWorkUnit>();
        foreach (var m in mappings.Where(m => m.DatasetIds.Count > 0))
        {
            var winStart = dateFrom;
            while (winStart <= dateTo)
            {
                var winEnd = winStart.AddDays(window - 1);
                if (winEnd > dateTo) winEnd = dateTo;
                units.Add(new EnergyAspectsWorkUnit
                {
                    Mapping = m, WindowStart = winStart, WindowEnd = winEnd
                });
                winStart = winEnd.AddDays(1);
            }
        }
        _logger.LogInformation("Built {Units} work units from {Mappings} mappings, {From:yyyy-MM-dd}–{To:yyyy-MM-dd}",
            units.Count, mappings.Count, dateFrom, dateTo);
        return units;
    }
}
