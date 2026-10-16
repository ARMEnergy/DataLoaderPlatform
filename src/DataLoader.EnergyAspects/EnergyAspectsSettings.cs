using DataLoader.Core.Configuration;

namespace DataLoader.EnergyAspects;

/// <summary>How a hot window's resume key varies between runs.</summary>
public enum EnergyAspectsHotKeyStrategy
{
    /// <summary>Suffix with the UTC run date <c>yyyyMMdd</c> → one re-pull per UTC day.</summary>
    RunDate,

    /// <summary>
    /// Suffix with UTC <c>yyyyMMddHH</c> → one re-pull per clock hour. <b>The default.</b>
    /// The loader is invoked several times a day and a daily token lets whichever run
    /// goes first claim the day, so later runs — including the scheduled one — skip it
    /// and never see anything the vendor published in between.
    /// </summary>
    RunHour,

    /// <summary>Suffix with the run id → <b>every</b> invocation re-pulls (diagnostics / forced re-pull).</summary>
    RunId
}

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

    /// <summary>
    /// Character budget for one timeseries request URI (path + query). A
    /// mapping whose dataset-id list would push past this is split across
    /// several requests.
    ///
    /// <para>
    /// Must stay below <see cref="EnergyAspectsRequest.ServerRequestUriLimit"/>
    /// (16,384 — measured against the live API), and well below is the point:
    /// at 8,000 a chunk holds roughly 1,100 ids, which returns in about 12
    /// seconds against a 30-day window and so also keeps clear of
    /// <see cref="HttpTimeoutSeconds"/>. Raising this toward the server limit
    /// trades a 414 for a timeout.
    /// </para>
    /// </summary>
    public int MaxRequestUriLength { get; set; } = 8000;

    public int TimeseriesWindowDays { get; set; } = 30;
    public int DaysBackStart { get; set; } = 90;
    public int DaysBackEnd { get; set; } = 0;

    /// <summary>
    /// A window whose last day is older than this many days is treated as SETTLED: it
    /// keeps a stable resume key and is loaded exactly once. A newer window is HOT and
    /// gets a run-varying key (see <see cref="HotKeyStrategy"/>) so revisions are picked
    /// up again.
    ///
    /// <para>
    /// <b>Shipped at 90 — equal to <see cref="DaysBackStart"/>, so nothing in the window
    /// settles.</b> That matches ICE, Genscape and EvolutionMarkets rather than OilX's
    /// <c>1</c>, because Energy Aspects RESTATES across the whole window. Measured on
    /// 2026-10-15, of the 33,939 rows one run changed, only <b>0.8%</b> had a DataDate in
    /// the last day — <b>61.6%</b> were 8-30 days old and 4.8% were 31-90 days old. A
    /// short horizon here would silently freeze roughly 99% of the revisions at whatever
    /// value happened to be seen first.
    /// </para>
    ///
    /// <para>
    /// Lowering it trades completeness for requests. <see cref="EnergyAspectsModule"/>
    /// warns when it is set below <see cref="DaysBackStart"/> and names the share of
    /// revisions that would be missed.
    /// </para>
    /// </summary>
    public int SettledAfterDays { get; set; } = 90;

    /// <summary>
    /// How a hot window's key varies between runs. See <see cref="EnergyAspectsHotKeyStrategy"/>.
    /// Defaults to <see cref="EnergyAspectsHotKeyStrategy.RunHour"/>.
    /// </summary>
    public EnergyAspectsHotKeyStrategy HotKeyStrategy { get; set; } = EnergyAspectsHotKeyStrategy.RunHour;
}
