using System.Globalization;
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

        // Step 4 — expand into (mapping × window × id-chunk) units
        var window = Math.Max(1, _settings.TimeseriesWindowDays);
        var fixedUriLength = EnergyAspectsRequest.FixedUriLength(_settings);
        var today = context.StartedAtUtc.Date;
        var hotToken = BuildHotToken(context);
        var units = new List<EnergyAspectsWorkUnit>();
        var splitMappings = 0;
        var hotUnits = 0;

        foreach (var m in mappings.Where(m => m.DatasetIds.Count > 0))
        {
            // Chunk once per mapping rather than once per window: the id list
            // is fixed for the run, so every window splits the same way and a
            // part's resume key means the same ids in all of them.
            var chunks = EnergyAspectsRequest.ChunkDatasetIds(
                m.DatasetIds, fixedUriLength, _settings.MaxRequestUriLength);

            if (chunks.Count > 1)
            {
                splitMappings++;
                _logger.LogInformation(
                    "Mapping {MappingId} ({Name}) has {Ids} dataset ids — splitting each window into " +
                    "{Parts} requests to stay inside the {Budget}-character URI budget",
                    m.MappingId, m.Name, m.DatasetIds.Count, chunks.Count, _settings.MaxRequestUriLength);
            }

            var winStart = dateFrom;
            while (winStart <= dateTo)
            {
                var winEnd = winStart.AddDays(window - 1);
                if (winEnd > dateTo) winEnd = dateTo;

                // A window is settled once its LAST day is older than the horizon —
                // using the last day, not the first, so a window is only frozen when
                // every day inside it is beyond revision.
                var settled = (today - winEnd).Days > Math.Max(0, _settings.SettledAfterDays);

                for (var part = 0; part < chunks.Count; part++)
                {
                    units.Add(new EnergyAspectsWorkUnit
                    {
                        Mapping = m,
                        DatasetIds = chunks[part],
                        PartNumber = part + 1,
                        PartCount = chunks.Count,
                        WindowStart = winStart,
                        WindowEnd = winEnd,
                        Settled = settled,
                        HotToken = settled ? null : hotToken
                    });
                    if (!settled) hotUnits++;
                }
                winStart = winEnd.AddDays(1);
            }
        }
        _logger.LogInformation(
            "Built {Units} work units from {Mappings} mappings ({Split} split across several requests), " +
            "{From:yyyy-MM-dd}–{To:yyyy-MM-dd}; {Hot} hot (re-fetched, token {Token}), {Settled} settled",
            units.Count, mappings.Count, splitMappings, dateFrom, dateTo,
            hotUnits, hotToken, units.Count - hotUnits);
        return units;
    }

    /// <summary>
    /// The token appended to every hot window's resume key this run.
    ///
    /// <para>
    /// An unmapped strategy THROWS rather than quietly falling back to a stable key:
    /// the fallback would freeze the hot zone and silently stop picking up the
    /// revisions it exists to catch.
    /// </para>
    /// </summary>
    private string BuildHotToken(LoaderRunContext context) =>
        _settings.HotKeyStrategy switch
        {
            EnergyAspectsHotKeyStrategy.RunDate =>
                context.StartedAtUtc.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
            EnergyAspectsHotKeyStrategy.RunHour =>
                context.StartedAtUtc.ToString("yyyyMMddHH", CultureInfo.InvariantCulture),
            EnergyAspectsHotKeyStrategy.RunId =>
                context.RunId.ToString("N"),
            _ => throw new NotSupportedException(
                $"Unmapped EnergyAspectsHotKeyStrategy '{_settings.HotKeyStrategy}'.")
        };
}
