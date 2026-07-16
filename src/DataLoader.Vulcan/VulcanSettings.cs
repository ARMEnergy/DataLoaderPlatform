using DataLoader.Core.Configuration;

namespace DataLoader.Vulcan;

/// <summary>
/// Vulcan loader settings, bound from <c>Loaders:Vulcan</c>. Inherits the common
/// bits (connection string, retry, concurrency) and adds the SynMax API fields.
/// </summary>
public sealed class VulcanSettings : LoaderSettingsBase
{
    public string ApiKey { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = "https://hyperion.api.synmax.com";
    public string QueryEndpoint { get; set; } = "/v4/beta/query_datalinks";
    public int HttpTimeoutSeconds { get; set; } = 60;
    public List<string> EnabledTables { get; set; } = new()
    {
        "under_construction", "datacenters", "lng_projects", "project_rankings", "metadata_history"
    };
}
