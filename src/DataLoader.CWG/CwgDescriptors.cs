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
/// <param name="RegionUnits">OPTIONAL, index-aligned 1:1 with <see cref="Regions"/> — the per-region
/// <c>{units}</c> filename token (CityForecast: <c>{"F"}</c> for <c>{"northamerica"}</c>); null on the
/// other 18 endpoints. When set it must have the same length as <see cref="Regions"/> (enforced by
/// the registry invariant in the static ctor).</param>
/// <param name="WideRegionColumns">Shape B in-file region column set (ordered) — the VALIDATION set:
/// every region column the source file is allowed to contain. An unknown column is still fatal.
/// Null on non-Shape-B endpoints.</param>
/// <param name="TargetRegionColumns">Shape B — the subset of <see cref="WideRegionColumns"/> that is
/// actually WRITTEN, in target-table column order. The target tables are wide (one column per region)
/// and carry fewer regions than the files publish, so this is deliberately narrower than
/// <see cref="WideRegionColumns"/>: DailyNormal/WindHourly drop NW+SW, SolarHourly keeps ERCOT+CAISO
/// of 14. Narrowing the WRITE set here never relaxes the parser's unknown-column guard.</param>
/// <param name="KeyColumns">Shape B leading key-column count (1: DATE / UTC_HOUR_ENDING).</param>
/// <param name="ExpectedBlocks">Shape E: 1 (Climo) or 3 (MW/Pct); null otherwise.</param>
/// <param name="TargetTable">arm.&lt;Table&gt; (informational; the sink is per-endpoint).</param>
/// <param name="TargetTvp">arm.&lt;Table&gt;Tvp (informational).</param>
/// <param name="TargetProc">arm.usp_BulkMerge&lt;Table&gt; (informational).</param>
/// <param name="ExpectedColumns">Shape A documented column count — the width guard, so a stray
/// trailing comma on the header row does not drop every data row; null → guard on header width.</param>
/// <param name="AllowShortRows">Shape A: when true, data rows with fewer than the expected column
/// count are NOT dropped — missing (trailing) fields are read as NULL (Station tolerates truncated rows).</param>
/// <param name="RegionlessTarget">The target table has NO region column, so a row from region A and a
/// row from region B would land on the SAME primary key and silently overwrite each other. True for
/// CityForecast / CityObservation / Station. The work-unit provider REFUSES to enumerate such an
/// endpoint when more than one region survives the Geographies filter (see CwgWorkUnitProvider) —
/// a loud failure instead of a silent last-writer-wins.</param>
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
    bool AllowShortRows = false,
    string[]? RegionUnits = null,
    string[]? TargetRegionColumns = null,
    bool RegionlessTarget = false)
{
    /// <summary>Undated endpoints use the RunDate/RunId hot key (design §3.1).</summary>
    public bool IsHot => !Dated;
}

/// <summary>The 19 concrete CWG endpoint descriptors (design §2.1) — the static discovery result.</summary>
public static class CwgDescriptors
{
    private static readonly (string, string[])[] NoExtras = System.Array.Empty<(string, string[])>();

    // The geography axis each geography descriptor COULD serve. Which one it actually runs is
    // chosen by the CwgSettings.Geographies setting (default: northamerica alone) — the single
    // narrowing point. All three of these targets are RegionlessTarget, so the work-unit provider
    // refuses to enumerate when the setting leaves more than one standing. Keeping the full axis
    // here means the loader can be REPOINTED at another geography by config, one at a time,
    // instead of being frozen at compile time.
    private static readonly string[] Geographies = { "northamerica", "asia", "europe" };

    // CityForecast fetches each region in its native unit via the {units} filename token:
    // northamerica in _F, europe in _C. RegionUnits is index-aligned 1:1 with CityForecastRegions.
    // 'asia' is dropped STRUCTURALLY here (it has no CityForecast file), independent of the setting.
    private static readonly string[] CityForecastRegions = { "northamerica", "europe" };
    private static readonly string[] CityForecastUnits = { "F", "C" };

    // ---- Shape B: the FULL in-file region column sets (docs/apis §4/§7/§11). These stay complete
    // so the parser's "unknown region column is fatal" guard keeps working on the real files.
    private static readonly string[] DailyNormalRegions =
        { "CAISO", "SPP", "ERCOT", "MISO", "PJM", "NEPOOL", "NYISO", "BPA", "IESO", "AESO", "NW", "SW" };
    private static readonly string[] SolarHourlyRegions =
        { "ERCOT", "CAISO", "PJM", "AESO", "MISO", "IESO", "NEPOOL", "NW", "SW", "SPP", "FRCC", "CAR", "SE", "TVA" };
    private static readonly string[] WindHourlyRegions =
        { "CAISO", "SPP", "ERCOT", "MISO", "PJM", "NEPOOL", "NYISO", "BPA", "IESO", "AESO", "NW", "SW" };

    // ---- Shape B: the region columns actually WRITTEN, in target-table column order.
    //      DailyNormal / WindHourly drop NW+SW; SolarHourly keeps 2 of 14.
    private static readonly string[] DailyNormalTargetRegions =
        { "CAISO", "SPP", "ERCOT", "MISO", "PJM", "NEPOOL", "NYISO", "BPA", "IESO", "AESO" };
    private static readonly string[] SolarHourlyTargetRegions = { "ERCOT", "CAISO" };
    private static readonly string[] WindHourlyTargetRegions =
        { "CAISO", "SPP", "ERCOT", "MISO", "PJM", "NEPOOL", "NYISO", "BPA", "IESO", "AESO" };

    // ISO region filename axes (docs/apis §5/§9/§10). Unaffected by the reshape — the solar/wind
    // forecast tables all keep a Region key column.
    private static readonly string[] SolarRegions =
        { "ERCOT", "CAISO", "MISO", "PJM", "SPP", "NEPOOL", "IESO", "AESO", "NW", "SW" };
    private static readonly string[] WindRegions =
        { "ERCOT", "CAISO", "MISO", "PJM", "SPP", "NYISO", "NEPOOL", "BPA", "IESO", "AESO", "UK", "GERMANY", "FRANCE", "SPAIN", "NW", "SW" };
    private static readonly string[] WindSubRegionParents =
        { "ERCOT", "CAISO", "MISO", "PJM", "SPP", "UK", "GERMANY", "FRANCE" };

    public static readonly CwgEndpointDescriptor CityForecast = new(
        // arm.CityForecast has NO Region and NO Units column, so exactly ONE (region, units) pair may
        // survive the Geographies filter — northamerica in °F by default. RegionlessTarget makes
        // widening Geographies a loud enumeration failure rather than a silent overwrite of °F rows
        // by °C ones (the two geographies disagree about the unit and nothing records which is which).
        "CityForecast", "City 15-day Forecast", CwgParseShape.A,
        "city15dfcst_{region}_{date}_{units}.csv", true, CwgDateToken.Ymd, 0,
        CwgRegionKind.Geography, "region", CityForecastRegions, NoExtras,
        null, 0, null,
        "arm.CityForecast", "arm.CityForecastTvp", "arm.usp_BulkMergeCityForecast",
        ExpectedColumns: 10, RegionUnits: CityForecastUnits, RegionlessTarget: true);

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
        "arm.CityObservation", "arm.CityObservationTvp", "arm.usp_BulkMergeCityObservation",
        ExpectedColumns: 6, RegionlessTarget: true);

    public static readonly CwgEndpointDescriptor DailyNormal = new(
        "DailyNormal", "Daily Normal Generation", CwgParseShape.B,
        "daily_normals.csv", false, CwgDateToken.None, 0,
        CwgRegionKind.None, null, System.Array.Empty<string>(), NoExtras,
        DailyNormalRegions, 1, null,
        "arm.DailyNormal", "arm.DailyNormalTvp", "arm.usp_BulkMergeDailyNormal",
        TargetRegionColumns: DailyNormalTargetRegions);

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
        "arm.SolarHourly", "arm.SolarHourlyTvp", "arm.usp_BulkMergeSolarHourly",
        TargetRegionColumns: SolarHourlyTargetRegions);

    public static readonly CwgEndpointDescriptor NationalDegreeDays = new(
        // RegionKind.None (NOT Geography): the fixed 'northamerica' is baked into the filename
        // and stamped on the unit/FileLog, but must NOT be subjected to the Geographies filter.
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
        "arm.WindHourly", "arm.WindHourlyTvp", "arm.usp_BulkMergeWindHourly",
        TargetRegionColumns: WindHourlyTargetRegions);

    public static readonly CwgEndpointDescriptor WindTotalCapacityClimatology = new(
        "WindTotalCapacityClimatology", "Wind Total Capacity Climatology", CwgParseShape.E,
        "Total_Capacity_climo_{datemmddyyyy}.csv", true, CwgDateToken.Mdyyyy, 0,
        CwgRegionKind.None, null, System.Array.Empty<string>(), NoExtras,
        null, 0, 1,
        "arm.WindTotalCapacityClimatology", "arm.WindTotalCapacityClimatologyTvp", "arm.usp_BulkMergeWindTotalCapacityClimatology");

    public static readonly CwgEndpointDescriptor WindTotalCapacityMW = new(
        // Still ExpectedBlocks: 3 — the FILE has three blocks and the parser still warns if it
        // does not. Only the WRITE narrows: the row factory keeps 'Current' and drops the rest,
        // because arm.WindTotalCapacityMW's key (ForecastDate, Region) holds exactly one block.
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
        "arm.Station", "arm.StationTvp", "arm.usp_BulkMergeStation",
        ExpectedColumns: 9, AllowShortRows: true, RegionlessTarget: true);

    // Regions5/9/ISO degree-days: per-region siblings of NationalDegreeDays in the same
    // northamerica_{subregion}_wdd_{date}.csv family. Like National, RegionKind.None with the
    // fixed 'northamerica' baked in; {subregion} selects 5region / 9region / iso. Each adds an
    // in-file REGION_NAME to the merge key (read in the row factory, not the unit).
    public static readonly CwgEndpointDescriptor Regions5DegreeDays = new(
        "Regions5DegreeDays", "5-Region Weighted Degree Days", CwgParseShape.A,
        "northamerica_{subregion}_wdd_{date}.csv", true, CwgDateToken.Ymd, 0,
        CwgRegionKind.None, null, new[] { "northamerica" },
        new[] { ("subregion", new[] { "5region" }) },
        null, 0, null,
        "arm.Regions5DegreeDays", "arm.Regions5DegreeDaysTvp", "arm.usp_BulkMergeRegions5DegreeDays", ExpectedColumns: 18);

    public static readonly CwgEndpointDescriptor Regions9DegreeDays = new(
        "Regions9DegreeDays", "9-Region Weighted Degree Days", CwgParseShape.A,
        "northamerica_{subregion}_wdd_{date}.csv", true, CwgDateToken.Ymd, 0,
        CwgRegionKind.None, null, new[] { "northamerica" },
        new[] { ("subregion", new[] { "9region" }) },
        null, 0, null,
        "arm.Regions9DegreeDays", "arm.Regions9DegreeDaysTvp", "arm.usp_BulkMergeRegions9DegreeDays", ExpectedColumns: 18);

    public static readonly CwgEndpointDescriptor ISODegreeDays = new(
        "ISODegreeDays", "ISO Weighted Degree Days", CwgParseShape.A,
        "northamerica_{subregion}_wdd_{date}.csv", true, CwgDateToken.Ymd, 0,
        CwgRegionKind.None, null, new[] { "northamerica" },
        new[] { ("subregion", new[] { "iso" }) },
        null, 0, null,
        "arm.ISODegreeDays", "arm.ISODegreeDaysTvp", "arm.usp_BulkMergeISODegreeDays", ExpectedColumns: 11);

    public static readonly CwgEndpointDescriptor StateDegreeDayObservation = new(
        // State-level population-weighted degree days. NOT part of the
        // northamerica_{subregion}_wdd_{date}.csv family — its own filename, with no {subregion}
        // token. Verified live 2026-10-05 on 2026-08-11 / 2026-10-03 / 2026-10-04: HTTP 200,
        // 15 columns, 48 data rows, NO 'END.' footer, and DATES equal to the filename date
        // (hence DateOffsetDays 0 and a single ObservationDate per file).
        "StateDegreeDayObservation", "State Degree-Day Observations", CwgParseShape.A,
        "northamerica_state_wdd_observations_{date}.csv", true, CwgDateToken.Ymd, 0,
        CwgRegionKind.None, null, new[] { "northamerica" }, NoExtras,
        null, 0, null,
        "arm.StateDegreeDayObservation", "arm.StateDegreeDayObservationTvp",
        "arm.usp_BulkMergeStateDegreeDayObservation", ExpectedColumns: 15);

    /// <summary>All 19 descriptors in registration order.</summary>
    public static readonly CwgEndpointDescriptor[] All =
    {
        CityForecast, CityGasForecast, CityObservation, DailyNormal, SolarForecast,
        SolarForecastChange, SolarHourly, NationalDegreeDays, WindForecast, WindForecastSubRegion,
        WindHourly, WindTotalCapacityClimatology, WindTotalCapacityMW, WindTotalCapacityPct, Station,
        Regions5DegreeDays, Regions9DegreeDays, ISODegreeDays, StateDegreeDayObservation
    };

    /// <summary>
    /// Registry invariants (design §2/§3), checked at startup (first static access) so a
    /// mis-declared descriptor fails loudly rather than mis-substituting a token or silently
    /// writing the wrong column set later:
    /// <list type="number">
    ///   <item><see cref="CwgEndpointDescriptor.RegionUnits"/>, when present, is index-aligned 1:1
    ///     with <see cref="CwgEndpointDescriptor.Regions"/>.</item>
    ///   <item><see cref="CwgEndpointDescriptor.TargetRegionColumns"/>, when present, is a SUBSET of
    ///     <see cref="CwgEndpointDescriptor.WideRegionColumns"/> — a target column the parser does
    ///     not know about could never be filled, and would silently write NULL forever.</item>
    ///   <item>A Shape B descriptor declares both sets; a non-Shape-B descriptor declares neither.</item>
    /// </list>
    /// (Static field initializers run before this body, so <see cref="All"/> is fully populated.)
    /// </summary>
    static CwgDescriptors()
    {
        foreach (var d in All)
        {
            if (d.RegionUnits is not null && d.RegionUnits.Length != d.Regions.Length)
                throw new InvalidOperationException(
                    $"CWG descriptor '{d.EndpointId}': RegionUnits length {d.RegionUnits.Length} must " +
                    $"equal Regions length {d.Regions.Length} — they are index-aligned 1:1.");

            var isWide = d.ParseShape == CwgParseShape.B;
            if (isWide != (d.WideRegionColumns is not null) || isWide != (d.TargetRegionColumns is not null))
                throw new InvalidOperationException(
                    $"CWG descriptor '{d.EndpointId}': Shape B must declare BOTH WideRegionColumns and " +
                    $"TargetRegionColumns; every other shape must declare neither.");

            if (d.TargetRegionColumns is not null && d.WideRegionColumns is not null)
            {
                var known = new HashSet<string>(d.WideRegionColumns, StringComparer.Ordinal);
                var unknown = d.TargetRegionColumns.Where(t => !known.Contains(t)).ToList();
                if (unknown.Count > 0)
                    throw new InvalidOperationException(
                        $"CWG descriptor '{d.EndpointId}': TargetRegionColumns [{string.Join(", ", unknown)}] " +
                        $"are not in WideRegionColumns — the parser would never fill them.");
            }
        }
    }

    /// <summary>The 19 endpoint ids (default <see cref="CwgSettings.EnabledEndpoints"/>).</summary>
    public static readonly string[] AllIds =
    {
        "CityForecast", "CityGasForecast", "CityObservation", "DailyNormal", "SolarForecast",
        "SolarForecastChange", "SolarHourly", "NationalDegreeDays", "WindForecast", "WindForecastSubRegion",
        "WindHourly", "WindTotalCapacityClimatology", "WindTotalCapacityMW", "WindTotalCapacityPct", "Station",
        "Regions5DegreeDays", "Regions9DegreeDays", "ISODegreeDays", "StateDegreeDayObservation"
    };
}
