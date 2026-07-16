using DataLoader.Core.Abstractions;
using DataLoader.EnergyAspects.Models;
using Microsoft.Extensions.Logging;

namespace DataLoader.EnergyAspects;

/// <summary>
/// Flattens API items into rows ready for the sink. Pulled out of the
/// original orchestrator's <c>MapApiResponseToRecords</c> /
/// <c>MapApiResponseToMetadataRecords</c> methods.
///
/// The transformer emits a single <see cref="TimeseriesBatch"/> per work
/// unit, containing both the value rows and the metadata rows for that unit.
/// </summary>
public sealed class EnergyAspectsTransformer : ITransformer<TimeseriesApiItem, TimeseriesBatch>
{
    private readonly ILogger<EnergyAspectsTransformer> _logger;

    public EnergyAspectsTransformer(ILogger<EnergyAspectsTransformer> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<TimeseriesBatch> Transform(IReadOnlyList<TimeseriesApiItem> items, WorkUnit unit)
    {
        var workUnit = (EnergyAspectsWorkUnit)unit;
        var mappingId = workUnit.Mapping.MappingId;

        var data = new List<TimeseriesRecord>();
        var meta = new List<TimeseriesMetadataRecord>(items.Count);

        foreach (var item in items)
        {
            // Value rows — one per (datasetId, date)
            foreach (var (dateStr, value) in item.Data)
            {
                if (!DateTime.TryParse(dateStr, out var dataDate))
                {
                    _logger.LogWarning("Unparseable date '{Date}' for dataset {Id} — skipping",
                        dateStr, item.DatasetId);
                    continue;
                }
                data.Add(new TimeseriesRecord
                {
                    DatasetId = item.DatasetId,
                    DataDate = dataDate.Date,
                    Value = value
                });
            }

            // Metadata row — one per dataset
            var m = item.Metadata;
            meta.Add(new TimeseriesMetadataRecord
            {
                MappingId = mappingId,
                DatasetId = item.DatasetId,
                Country = m.Country, CountryIso = m.CountryIso, Region = m.Region, SubRegion = m.SubRegion,
                Description = m.Description, Aspect = m.Aspect, AspectSubtype = m.AspectSubtype,
                Category = m.Category, CategorySubtype = m.CategorySubtype,
                ForecastStartDate = TryParseDate(m.ForecastStartDate),
                Frequency = m.Frequency, LifecycleStage = m.LifecycleStage,
                Source = m.Source, Unit = m.Unit,
                ReleaseDate = TryParseDateTime(m.ReleaseDate),
                Basin = item.AdditionalFields.GetValueOrDefault("basin"),
                PartnerSubRegion = item.AdditionalFields.GetValueOrDefault("partner_sub_region"),
                Pipeline = item.AdditionalFields.GetValueOrDefault("pipeline"),
                EaIsoRto = item.AdditionalFields.GetValueOrDefault("ea_iso_rto")
            });
        }

        return new[] { new TimeseriesBatch { Data = data, Metadata = meta } };
    }

    private static DateTime? TryParseDate(string? v) =>
        string.IsNullOrWhiteSpace(v) ? null : DateTime.TryParse(v, out var d) ? d.Date : null;

    private static DateTime? TryParseDateTime(string? v) =>
        string.IsNullOrWhiteSpace(v) ? null : DateTime.TryParse(v, out var d) ? d : null;
}
