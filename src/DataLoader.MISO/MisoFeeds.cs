namespace DataLoader.MISO;

/// <summary>Unit shape of a feed (design §6).</summary>
public enum MisoFeedKind
{
    /// <summary>One unit per market date <c>D</c>.</summary>
    MarketDay,

    /// <summary>One unit per <c>(init I, forecast date D)</c>, <c>D = I+1 … I+MtlfHorizonDays</c>.</summary>
    Mtlf,
}

/// <summary>
/// One feed's vendor facts (design §1, §4). The request is <c>{BaseUrl}/{PathPrefix}/{D}/{PathSuffix}</c>
/// plus <see cref="FixedQuery"/>, <c>init={I}</c> for MTLF, and <c>pageNumber=N</c> always.
/// </summary>
public sealed record MisoFeed(
    string Id,
    MisoFeedKind Kind,
    string PathPrefix,
    string PathSuffix,
    string? FixedQuery,
    string MemberField,
    IReadOnlyList<string> Members,
    string Table)
{
    /// <summary>Canonical (upper-case) member set for the case-insensitive check (design §4 rule 4).</summary>
    public IReadOnlySet<string> MemberSet { get; } = new HashSet<string>(Members.Select(m => m.ToUpperInvariant()), StringComparer.Ordinal);
}

/// <summary>The four feeds (design §1), in execution order.</summary>
public static class MisoFeeds
{
    public const string ZonalLoadHourlyId = "zonal_load_hourly";
    public const string LoadHourlyId = "load_hourly";
    public const string FuelMixHourlyId = "fuel_mix_hourly";
    public const string LoadForecastMtlfId = "load_forecast_mtlf";

    public static readonly IReadOnlyList<string> Lrz = new[] { "LRZ1", "LRZ2_7", "LRZ3_5", "LRZ4", "LRZ6", "LRZ8_9_10" };
    public static readonly IReadOnlyList<string> Regions = new[] { "NORTH", "CENTRAL", "SOUTH" };
    public static readonly IReadOnlyList<string> MtlfZones = Enumerable.Range(1, 10).Select(i => $"Z{i}").ToArray();
    /// <summary>
    /// MTLF zone → region, as OBSERVED in every row of both live captures (2026-11-01 and 2026-11-18,
    /// 480 rows): NORTH = Z1, Z3; CENTRAL = Z2, Z4–Z7; SOUTH = Z8–Z10. ⚠ The API doc §5.3 text
    /// "NORTH (Z1,Z2) | CENTRAL (Z3..Z7)" contradicts the vendor's own data; the data wins.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> MtlfZoneRegion = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Z1"] = "NORTH", ["Z3"] = "NORTH",
        ["Z2"] = "CENTRAL", ["Z4"] = "CENTRAL", ["Z5"] = "CENTRAL", ["Z6"] = "CENTRAL", ["Z7"] = "CENTRAL",
        ["Z8"] = "SOUTH", ["Z9"] = "SOUTH", ["Z10"] = "SOUTH",
    };

    public static readonly IReadOnlyList<string> FuelKeys = new[] { "coal", "gas", "nuclear", "water", "wind", "solar", "other", "storage" };

    public static readonly MisoFeed ZonalLoadHourly = new(
        ZonalLoadHourlyId, MisoFeedKind.MarketDay, "real-time", "demand/actual", "geoResolution=localResourceZone",
        "localResourceZone", Lrz, "miso.miso_zonal_load_hourly");

    public static readonly MisoFeed LoadHourly = new(
        LoadHourlyId, MisoFeedKind.MarketDay, "real-time", "demand/actual", "geoResolution=region",
        "region", Regions, "miso.miso_load_hourly");

    public static readonly MisoFeed FuelMixHourly = new(
        FuelMixHourlyId, MisoFeedKind.MarketDay, "real-time", "generation/fuel-type", null,
        "region", Regions, "miso.miso_fuel_mix_hourly");

    public static readonly MisoFeed LoadForecastMtlf = new(
        LoadForecastMtlfId, MisoFeedKind.Mtlf, "forecast", "load", null,
        "localResourceZone", MtlfZones, "miso.miso_load_forecast_mtlf");

    /// <summary>All feeds in design §1 order (also the execution order and the <c>EnabledFeeds</c> default).</summary>
    public static readonly IReadOnlyList<MisoFeed> All = new[] { ZonalLoadHourly, LoadHourly, FuelMixHourly, LoadForecastMtlf };

    public static readonly IReadOnlyList<string> ExecutionOrder = All.Select(f => f.Id).ToArray();

    public static MisoFeed? ById(string id) => All.FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.OrdinalIgnoreCase));
}
