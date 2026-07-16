using System.Text.Json;
using DataLoader.Core.Sources;
using DataLoader.EnergyAspects.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.EnergyAspects;

/// <summary>
/// HTTP source reader for the Energy Aspects timeseries API. Returns
/// <see cref="TimeseriesApiItem"/>s for one (mapping × date-window) unit.
///
/// All HTTP plumbing — base URL, timeout, retry, JSON options — comes from
/// the platform's base class and the registered <see cref="HttpClient"/>.
/// This class only has to say what URL to call and how to read the response.
/// </summary>
public sealed class EnergyAspectsApiSource : HttpJsonSourceReaderBase<EnergyAspectsWorkUnit, TimeseriesApiItem>
{
    private readonly EnergyAspectsSettings _settings;
    private readonly IEnergyAspectsApiLogger _apiLogger;

    public EnergyAspectsApiSource(
        HttpClient httpClient,
        IOptions<EnergyAspectsSettings> settings,
        IEnergyAspectsApiLogger apiLogger,
        ILogger<EnergyAspectsApiSource> logger)
        : base(httpClient, logger)
    {
        _settings = settings.Value;
        _apiLogger = apiLogger;
    }

    protected override Uri BuildRequestUri(EnergyAspectsWorkUnit unit)
    {
        var ids = string.Join(",", unit.Mapping.DatasetIds);
        var from = unit.WindowStart.ToString("yyyy-MM-dd");
        var to = unit.WindowEnd.ToString("yyyy-MM-dd");
        // BaseAddress is set on the HttpClient — pass relative
        return new Uri(
            $"{_settings.TimeseriesEndpoint}?api_key={_settings.ApiKey}&dataset_id={ids}&date_from={from}&date_to={to}",
            UriKind.Relative);
    }

    protected override async Task OnResponseReceivedAsync(
        EnergyAspectsWorkUnit unit, HttpResponseMessage response, CancellationToken ct)
    {
        // Preserve the original loader's behaviour: record every API
        // response code for this mapping
        await _apiLogger.LogApiResponseAsync(unit.Mapping.MappingId, (int)response.StatusCode, ct);

        // The original treated 417 as "not authorized for this licensed
        // dataset" — translate so the pipeline can short-circuit
        if ((int)response.StatusCode == 417)
            throw new UnauthorizedAccessException($"Mapping {unit.Mapping.MappingId} returned 417 — likely unlicensed");
    }

    protected override async Task<IReadOnlyList<TimeseriesApiItem>> DeserializeAsync(
        HttpResponseMessage response, CancellationToken ct)
    {
        // The EA API returns extra fields outside the known metadata block;
        // capture them in AdditionalFields. Do a low-level parse to keep
        // that behaviour intact.
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var list = JsonSerializer.Deserialize<List<TimeseriesApiItem>>(json, DefaultJsonOptions) ?? new();
        return list;
    }

    // ---- Mappings endpoint is a different shape — handle in a separate method ----
    // The work-unit provider uses this directly, outside the standard pipeline.

    public async Task<IReadOnlyList<DatasetMapping>> GetDatasetMappingsAsync(CancellationToken ct)
    {
        var uri = new Uri($"{_settings.MappingsEndpoint}?api_key={_settings.ApiKey}", UriKind.Relative);
        Logger.LogInformation("Fetching dataset mappings");
        var response = await HttpClient.GetAsync(uri, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return ParseMappingsResponse(json);
    }

    private static IReadOnlyList<DatasetMapping> ParseMappingsResponse(string json)
    {
        var mappings = new List<DatasetMapping>();
        using var doc = JsonDocument.Parse(json);
        foreach (var categoryProp in doc.RootElement.EnumerateObject())
        {
            var category = categoryProp.Name;
            foreach (var item in categoryProp.Value.EnumerateArray())
            {
                var mapping = new DatasetMapping
                {
                    MappingId = item.GetProperty("mapping_id").GetInt32(),
                    Name = item.GetProperty("name").GetString() ?? string.Empty,
                    Category = category,
                    RequestString = item.TryGetProperty("request_string", out var rs) ? rs.GetString() ?? string.Empty : string.Empty,
                    Licensed = item.TryGetProperty("licensed", out var lic) ? lic.GetString() ?? string.Empty : string.Empty,
                    DatasetIds = new List<int>()
                };
                if (item.TryGetProperty("dataset_ids", out var dsIds))
                    foreach (var id in dsIds.EnumerateArray())
                        mapping.DatasetIds.Add(id.GetInt32());
                mappings.Add(mapping);
            }
        }
        return mappings;
    }
}

/// <summary>
/// Tiny side-channel for recording API response codes per mapping. Lives
/// here rather than in the sink because it's an audit event, not a row
/// write, and it has to fire for every HTTP response — even failures.
/// </summary>
public interface IEnergyAspectsApiLogger
{
    Task LogApiResponseAsync(int mappingId, int responseCode, CancellationToken ct);
}
