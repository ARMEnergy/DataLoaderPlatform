using DataLoader.Core.Configuration;

namespace DataLoader.EnergyAspects;

/// <summary>
/// Energy Aspects loader settings, bound from
/// <c>Loaders:EnergyAspects</c>.
///
/// Inherits the common bits (connection string, retry, concurrency) and adds
/// the loader-specific fields: API endpoints, date window sizing, etc.
/// </summary>
public sealed class EnergyAspectsSettings : LoaderSettingsBase
{
    public string ApiKey { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public string MappingsEndpoint { get; set; } = "/data/dataset_mappings";
    public string TimeseriesEndpoint { get; set; } = "/data/timeseries/";
    public int HttpTimeoutSeconds { get; set; } = 30;
    public int TimeseriesWindowDays { get; set; } = 30;
    public int DaysBackStart { get; set; } = 90;
    public int DaysBackEnd { get; set; } = 0;
}
