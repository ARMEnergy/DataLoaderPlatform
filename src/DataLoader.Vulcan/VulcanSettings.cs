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
    public int PageSize { get; set; } = 50000;
    public int MaxPages { get; set; } = 10000;

    /// <summary>
    /// Extra attempts for a page whose HTTP 200 body does not parse. query_datalinks streams
    /// NDJSON, so a fault after the status line is sent can only show up as a body that ends
    /// early; the HTTP retry policy (status codes + transport errors) never sees it. Observed
    /// 2026-11-06: datacenters page 1 ended mid-string at byte 1,255 of a row after ~40s, the
    /// first such failure in 65 runs, and with one run a day it cost a full day's load.
    /// Re-POSTing is safe: the query is read-only and nothing is written until every page is in.
    /// Not shipped in appsettings.json; defaults apply. 0 disables the retry.
    /// </summary>
    public int MalformedBodyRetryCount { get; set; } = 2;

    /// <summary>Wait before each <see cref="MalformedBodyRetryCount"/> retry.</summary>
    public int MalformedBodyRetryDelaySeconds { get; set; } = 10;

    // NOTE: which tables to load is NOT a setting. It lives in dbo.LoaderTable in the Vulcan
    // database (Enabled / LastRun), read through IVulcanTableRegistry, so the set can be changed
    // without a redeploy. The old Loaders:Vulcan:EnabledTables list was removed on 2026-10-13 —
    // do not reintroduce it, or the enabled set would live in two places that can disagree.
}
