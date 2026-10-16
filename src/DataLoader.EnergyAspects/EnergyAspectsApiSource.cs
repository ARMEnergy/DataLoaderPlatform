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
        // DatasetIds is the unit's own chunk, not the whole mapping — the work
        // unit provider already split anything too long for one request URI.
        // BaseAddress is set on the HttpClient — pass relative.
        return new Uri(
            EnergyAspectsRequest.BuildTimeseriesUri(
                _settings, unit.DatasetIds, unit.WindowStart, unit.WindowEnd),
            UriKind.Relative);
    }

    protected override async Task OnResponseReceivedAsync(
        EnergyAspectsWorkUnit unit, HttpResponseMessage response, CancellationToken ct)
    {
        // Preserve the original loader's behaviour: record every API
        // response code for this mapping
        await _apiLogger.LogApiResponseAsync(unit.Mapping.MappingId, (int)response.StatusCode, ct);

        // 414 means the chunking budget is wrong, not that the data is bad.
        // Say so plainly — the bare status text sent the previous investigation
        // looking at the vendor rather than at config. The URI itself carries
        // the api key, so report its length and never the string.
        if ((int)response.StatusCode == 414)
            throw new InvalidOperationException(
                $"Mapping {unit.Mapping.MappingId} part {unit.PartNumber}/{unit.PartCount} " +
                $"built a {BuildRequestUri(unit).OriginalString.Length}-character request URI for " +
                $"{unit.DatasetIds.Count} dataset ids and the API rejected it with 414. The server's " +
                $"limit is {EnergyAspectsRequest.ServerRequestUriLimit}; lower " +
                "Loaders:EnergyAspects:MaxRequestUriLength so the mapping splits into more parts.");
    }

    /// <summary>
    /// Standard read, except that <c>417</c> is classified rather than thrown.
    ///
    /// <para>
    /// Measured against the live API: <c>417 {"detail":"User is not licensed to
    /// data."}</c> is returned only when <b>not one</b> of the requested dataset
    /// ids is licensed. If even one is, the answer is <c>200</c> and the
    /// unlicensed ids are silently omitted from the body. So for a work unit
    /// holding part of a mapping, a 417 is a true statement about that slice —
    /// "nothing here is licensed" — and not a failure: mapping 515 has 1,470
    /// ids of which only 8 are licensed, so the part that happens to hold none
    /// of them answers 417 on every window while the other part loads fine.
    /// </para>
    ///
    /// <para>
    /// Since <c>usp_GetActiveMappings</c> no longer filters on <c>Licensed</c>,
    /// a 417 is the normal answer for any slice we are not entitled to, and
    /// tolerating it is what lets the API — rather than the vendor's unreliable
    /// per-mapping flag — decide what gets loaded.
    /// </para>
    ///
    /// <para>
    /// One combination is still an error. If a mapping the vendor flags
    /// <c>Licensed = 'yes'</c> answers 417 for its ENTIRE id list, the vendor's
    /// own metadata and its API disagree in the dangerous direction: we are told
    /// we hold the licence and served nothing. A 2026-10-14 census of all 30,061
    /// ids under licensed mappings found none with zero entitlement, so that
    /// means something really changed — an entitlement lapse or a downgraded key
    /// — and it must fail loudly rather than quietly become "0 rows". The flag
    /// is thus kept as a corroborating hint after being dropped as a filter.
    /// </para>
    /// </summary>
    public override async Task<IReadOnlyList<TimeseriesApiItem>> ReadAsync(
        EnergyAspectsWorkUnit unit, CancellationToken cancellationToken)
    {
        var uri = BuildRequestUri(unit);
        Logger.LogDebug("GET {Uri}", SanitiseForLog(uri));

        var response = await HttpClient.GetAsync(uri, cancellationToken).ConfigureAwait(false);
        await OnResponseReceivedAsync(unit, response, cancellationToken).ConfigureAwait(false);

        if ((int)response.StatusCode == 417)
        {
            if (unit.PartCount <= 1 && unit.Mapping.IsLicensed)
                throw new UnauthorizedAccessException(
                    $"Mapping {unit.Mapping.MappingId} ({unit.Mapping.Name}) returned 417 for its " +
                    $"entire {unit.DatasetIds.Count}-id list, even though the vendor flags it " +
                    "Licensed='yes'. Entitlement has changed, or the API key has.");

            Logger.LogInformation(
                "Mapping {MappingId} part {Part}/{Parts} (Licensed={Licensed}): none of its {Ids} " +
                "dataset ids is licensed (417) — empty read for {From:yyyy-MM-dd}–{To:yyyy-MM-dd}",
                unit.Mapping.MappingId, unit.PartNumber, unit.PartCount,
                string.IsNullOrWhiteSpace(unit.Mapping.Licensed) ? "?" : unit.Mapping.Licensed,
                unit.DatasetIds.Count, unit.WindowStart, unit.WindowEnd);

            return Array.Empty<TimeseriesApiItem>();
        }

        response.EnsureSuccessStatusCode();
        return await DeserializeAsync(response, cancellationToken).ConfigureAwait(false);
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
