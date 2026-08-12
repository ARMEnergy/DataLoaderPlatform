namespace DataLoader.CWG;

/// <summary>
/// Static description of one CWG endpoint (design §2). The descriptor registry is
/// the (compile-time) discovery result — enumeration is DB-free. Everything a
/// work-unit provider, source reader and FileLog writer needs to drive one
/// endpoint comes from here; the only per-endpoint C# is the row type, its row
/// factory and its sink.
/// </summary>
/// <param name="EndpointId">Stable id, e.g. "CityForecast" … "Station".</param>
/// <param name="DisplayName">Human-readable name.</param>
/// <param name="ParseShape">A|B|C|D|E — selects the shared shape parser.</param>
/// <param name="FilenameTemplate">Filename with <c>{date}</c>/<c>{datemmddyyyy}</c> + named placeholders.</param>
/// <param name="Dated">true → date token (stable key); false → undated "latest" (hot key).</param>
/// <param name="DateToken">None | Ymd (YYYYMMDD) | Mdyyyy (MMDDYYYY).</param>
/// <param name="DateOffsetDays">Newest represented date = runDate + offset (0, or −1 for CityObservation).</param>
/// <param name="RegionKind">None | Geography (na/asia/europe) | Iso.</param>
/// <param name="RegionPlaceholder">Token name in the template (e.g. "region"), or null if none / literal.</param>
/// <param name="Regions">Enum values for the region placeholder (empty if none).</param>
/// <param name="ExtraPlaceholders">Extra named placeholders, e.g. subregion=["national"].</param>
/// <param name="WideRegionColumns">Shape B in-file region column set (ordered); null otherwise.</param>
/// <param name="KeyColumns">Shape B leading key-column count (1: DATE / UTC_HOUR_ENDING).</param>
/// <param name="ExpectedBlocks">Shape E: 1 (Climo) or 3 (MW/Pct); null otherwise.</param>
/// <param name="TargetTable">arm.&lt;Table&gt; (informational; the sink is per-endpoint).</param>
/// <param name="TargetTvp">arm.&lt;Table&gt;Tvp (informational).</param>
/// <param name="TargetProc">arm.usp_BulkMerge&lt;Table&gt; (informational).</param>
/// <param name="ExpectedColumns">Shape A documented column count — the width guard, so a stray
/// trailing comma on the header row does not drop every data row; null → guard on header width.</param>
/// <param name="AllowShortRows">Shape A: when true, data rows with fewer than the expected column
/// count are NOT dropped — missing (trailing) fields are read as NULL (Station tolerates truncated rows).</param>
public sealed record CwgEndpointDescriptor(
    string EndpointId,
    string DisplayName,
    CwgParseShape ParseShape,
    string FilenameTemplate,
    bool Dated,
    CwgDateToken DateToken,
    int DateOffsetDays,
    CwgRegionKind RegionKind,
    string? RegionPlaceholder,
    string[] Regions,
    (string Name, string[] Values)[] ExtraPlaceholders,
    string[]? WideRegionColumns,
    int KeyColumns,
    int? ExpectedBlocks,
    string TargetTable,
    string TargetTvp,
    string TargetProc,
    int? ExpectedColumns = null,
    bool AllowShortRows = false)
{
    /// <summary>Undated endpoints use the RunDate/RunId hot key (design §3.1).</summary>
    public bool IsHot => !Dated;
}

/// <summary>The 15 concrete CWG endpoint descriptors (design §2.1) — the static discovery result.</summary>
public static class CwgDescriptors
{
    private static readonly (string, string[])[] NoExtras = System.Array.Empty<(string, string[])>();
    private static readonly string[] Geographies = { "northamerica", "asia", "europe" };

    // Shape B in-file region column sets (docs/apis §4/§7/§11).
    private static readonly string[] DailyNormalRegions =
        { "CAISO", "SPP", "ERCOT", "MISO", "PJM", "NEPOOL", "NYISO", "BPA", "IESO", "AESO", "NW", "SW" };
    private static readonly string[] SolarHourlyRegions =
        { "ERCOT", "CAISO", "PJM", "AESO", "MISO", "IESO", "NEPOOL", "NW", "SW", "SPP", "FRCC", "CAR", "SE", "TVA" };
    private static readonly string[] WindHourlyRegions =
        { "CAISO", "SPP", "ERCOT", "MISO", "PJM", "NEPOOL", "NYISO", "BPA", "IESO", "AESO", "NW", "SW" };

    // ISO region filename axes (docs/apis §5/§9/§10).
    private static readonly string[] SolarRegions =
        { "ERCOT", "CAISO", "MISO", "PJM", "SPP", "NEPOOL", "IESO", "AESO", "NW", "SW" };
    private static readonly string[] WindRegions =
        { "ERCOT", "CAISO", "MISO", "PJM", "SPP", "NYISO", "NEPOOL", "BPA", "IESO", "AESO", "UK", "GERMANY", "FRANCE", "SPAIN", "NW", "SW" };
    private static readonly string[] WindSubRegionParents =
        { "ERCOT", "CAISO", "MISO", "PJM", "SPP", "UK", "GERMANY", "FRANCE" };

    public static readonly CwgEndpointDescriptor CityForecast = new(
        "CityForecast", "City 15-day Forecast", CwgParseShape.A,
        "city15dfcst_{region}_{date}_F.csv", true, CwgDateToken.Ymd, 0,
        CwgRegionKind.Geography, "region", Geographies, NoExtras,
        null, 0, null,
        "arm.CityForecast", "arm.CityForecastTvp", "arm.usp_BulkMergeCityForecast", ExpectedColumns: 10);

    public static readonly CwgEndpointDescriptor CityGasForecast = new(
        "CityGasForecast", "City Gas-day Forecast", CwgParseShape.A,
        "city_gasday_fcst.csv", false, CwgDateToken.None, 0,
        CwgRegionKind.None, null, System.Array.Empty<string>(), NoExtras,
        null, 0, null,
        "arm.CityGasForecast", "arm.CityGasForecastTvp", "arm.usp_BulkMergeCityGasForecast", ExpectedColumns: 10);

    public static readonly CwgEndpointDescriptor CityObservation = new(
        "CityObservation", "City Observations (final)", CwgParseShape.A,
        "{region}_observations_final_{date}.csv", true, CwgDateToken.Ymd, -1,
        CwgRegionKind.Geography, "region", Geographies, NoExtras,
        null, 0, null,
        "arm.CityObservation", "arm.CityObservationTvp", "arm.usp_BulkMergeCityObservation", ExpectedColumns: 6);

    public static readonly CwgEndpointDescriptor DailyNormal = new(
        "DailyNormal", "Daily Normal Generation", CwgParseShape.B,
        "daily_normals.csv", false, CwgDateToken.None, 0,
        CwgRegionKind.None, null, System.Array.Empty<string>(), NoExtras,
        DailyNormalRegions, 1, null,
        "arm.DailyNormal", "arm.DailyNormalTvp", "arm.usp_BulkMergeDailyNormal");

    public static readonly CwgEndpointDescriptor SolarForecast = new(
        "SolarForecast", "Solar Forecast", CwgParseShape.C,
        "{region}solar_{datemmddyyyy}.csv", true, CwgDateToken.Mdyyyy, 0,
        CwgRegionKind.Iso, "region", SolarRegions, NoExtras,
        null, 0, null,
        "arm.SolarForecast", "arm.SolarForecastTvp", "arm.usp_BulkMergeSolarForecast");

    public static readonly CwgEndpointDescriptor SolarForecastChange = new(
        "SolarForecastChange", "Solar Forecast Change", CwgParseShape.C,
        "{region}solarchanges_{datemmddyyyy}.csv", true, CwgDateToken.Mdyyyy, 0,
        CwgRegionKind.Iso, "region", SolarRegions, NoExtras,
        null, 0, null,
        "arm.SolarForecastChange", "arm.SolarForecastChangeTvp", "arm.usp_BulkMergeSolarForecastChange");

    public static readonly CwgEndpointDescriptor SolarHourly = new(
        "SolarHourly", "Solar Hourly Actuals", CwgParseShape.B,
        "Gen_hrly_solar.csv", false, CwgDateToken.None, 0,
        CwgRegionKind.None, null, System.Array.Empty<string>(), NoExtras,
        SolarHourlyRegions, 1, null,
        "arm.SolarHourly", "arm.SolarHourlyTvp", "arm.usp_BulkMergeSolarHourly");

    public static readonly CwgEndpointDescriptor NationalDegreeDays = new(
        // RegionKind.None (NOT Geography): the fixed 'northamerica' is baked into the filename
        // and stamped on the unit/FileLog, but must NOT be subjected to the Geographies filter —
        // narrowing Geographies to exclude northamerica must not zero this endpoint out (Fix 2).
        "NationalDegreeDays", "National Weighted Degree Days", CwgParseShape.A,
        "northamerica_{subregion}_wdd_{date}.csv", true, CwgDateToken.Ymd, 0,
        CwgRegionKind.None, null, new[] { "northamerica" },
        new[] { ("subregion", new[] { "national" }) },
        null, 0, null,
        "arm.NationalDegreeDays", "arm.NationalDegreeDaysTvp", "arm.usp_BulkMergeNationalDegreeDays", ExpectedColumns: 14);

    public static readonly CwgEndpointDescriptor WindForecast = new(
        "WindForecast", "Wind Forecast", CwgParseShape.C,
        "{region}wind_{datemmddyyyy}.csv", true, CwgDateToken.Mdyyyy, 0,
        CwgRegionKind.Iso, "region", WindRegions, NoExtras,
        null, 0, null,
        "arm.WindForecast", "arm.WindForecastTvp", "arm.usp_BulkMergeWindForecast");

    public static readonly CwgEndpointDescriptor WindForecastSubRegion = new(
        "WindForecastSubRegion", "Wind Forecast Sub-region", CwgParseShape.D,
        "{region}wind_regions_{datemmddyyyy}.csv", true, CwgDateToken.Mdyyyy, 0,
        CwgRegionKind.Iso, "region", WindSubRegionParents, NoExtras,
        null, 0, null,
        "arm.WindForecastSubRegion", "arm.WindForecastSubRegionTvp", "arm.usp_BulkMergeWindForecastSubRegion");

    public static readonly CwgEndpointDescriptor WindHourly = new(
        "WindHourly", "Wind Hourly Actuals", CwgParseShape.B,
        "Gen_hrly_5day.csv", false, CwgDateToken.None, 0,
        CwgRegionKind.None, null, System.Array.Empty<string>(), NoExtras,
        WindHourlyRegions, 1, null,
        "arm.WindHourly", "arm.WindHourlyTvp", "arm.usp_BulkMergeWindHourly");

    public static readonly CwgEndpointDescriptor WindTotalCapacityClimatology = new(
        "WindTotalCapacityClimatology", "Wind Total Capacity Climatology", CwgParseShape.E,
        "Total_Capacity_climo_{datemmddyyyy}.csv", true, CwgDateToken.Mdyyyy, 0,
        CwgRegionKind.None, null, System.Array.Empty<string>(), NoExtras,
        null, 0, 1,
        "arm.WindTotalCapacityClimatology", "arm.WindTotalCapacityClimatologyTvp", "arm.usp_BulkMergeWindTotalCapacityClimatology");

    public static readonly CwgEndpointDescriptor WindTotalCapacityMW = new(
        "WindTotalCapacityMW", "Wind Total Capacity (MW)", CwgParseShape.E,
        "Total_Capacity_vals_{datemmddyyyy}.csv", true, CwgDateToken.Mdyyyy, 0,
        CwgRegionKind.None, null, System.Array.Empty<string>(), NoExtras,
        null, 0, 3,
        "arm.WindTotalCapacityMW", "arm.WindTotalCapacityMWTvp", "arm.usp_BulkMergeWindTotalCapacityMW");

    public static readonly CwgEndpointDescriptor WindTotalCapacityPct = new(
        "WindTotalCapacityPct", "Wind Total Capacity (%)", CwgParseShape.E,
        "Total_Capacity_{datemmddyyyy}.csv", true, CwgDateToken.Mdyyyy, 0,
        CwgRegionKind.None, null, System.Array.Empty<string>(), NoExtras,
        null, 0, 3,
        "arm.WindTotalCapacityPct", "arm.WindTotalCapacityPctTvp", "arm.usp_BulkMergeWindTotalCapacityPct");

    public static readonly CwgEndpointDescriptor Station = new(
        "Station", "Forecast Station Reference", CwgParseShape.A,
        "{region}_station_information.csv", false, CwgDateToken.None, 0,
        CwgRegionKind.Geography, "region", Geographies, NoExtras,
        null, 0, null,
        "arm.Station", "arm.StationTvp", "arm.usp_BulkMergeStation", ExpectedColumns: 9, AllowShortRows: true);

    /// <summary>All 15 descriptors in registration order.</summary>
    public static readonly CwgEndpointDescriptor[] All =
    {
        CityForecast, CityGasForecast, CityObservation, DailyNormal, SolarForecast,
        SolarForecastChange, SolarHourly, NationalDegreeDays, WindForecast, WindForecastSubRegion,
        WindHourly, WindTotalCapacityClimatology, WindTotalCapacityMW, WindTotalCapacityPct, Station
    };

    /// <summary>The 15 endpoint ids (default <see cref="CwgSettings.EnabledEndpoints"/>).</summary>
    public static readonly string[] AllIds =
    {
        "CityForecast", "CityGasForecast", "CityObservation", "DailyNormal", "SolarForecast",
        "SolarForecastChange", "SolarHourly", "NationalDegreeDays", "WindForecast", "WindForecastSubRegion",
        "WindHourly", "WindTotalCapacityClimatology", "WindTotalCapacityMW", "WindTotalCapacityPct", "Station"
    };
}
