using DataLoader.Core.Configuration;

namespace DataLoader.EvolutionMarkets;

/// <summary>
/// How the hot-zone work-unit resume key varies between runs — design §3.3.
///
/// <para><b><c>RunHour</c> is deliberately NOT offered.</b> <c>EVOID/USNaturalGasIndex</c> is an
/// <b>end-of-day</b> dataset: every observed <c>priceTs</c> across all 8,118 rows of available
/// history is exactly midnight <c>Z</c>, and <c>/v1/market-data</c> documents itself as returning
/// "the data from the last business date". Nothing arrives intra-day worth re-probing more than once
/// (contrast IHSPointLogic, whose hourly endpoints need it). If an hourly cadence is ever added the
/// hour token <b>must be UTC</b>: <c>01:00</c> Central occurs twice on a fall-back night, so a
/// Central <c>yyyyMMddHH</c> token would repeat and the key would go backwards. At <b>date</b>
/// granularity the Central calendar date is monotonic non-decreasing across both DST transitions,
/// which is why the <see cref="RunDate"/> token is safe on the Central clock (design §3.3 / §7).</para>
/// </summary>
public enum EvoHotKeyStrategy
{
    /// <summary>Suffix the key with the US-Central run date → one re-pull per calendar day (the default).</summary>
    RunDate,

    /// <summary>Suffix the key with the run id → every invocation re-pulls.</summary>
    RunId
}

/// <summary>
/// Evolution Markets ("EVO DataPipeline API") loader settings, bound from
/// <c>Loaders:EvolutionMarkets</c> (design §10). Inherits the common bits (connection string, retry,
/// concurrency, per-unit timeout) and adds the API, auth, windowing, paging and resume-heuristic
/// fields.
///
/// <para>
/// <see cref="ApiKey"/> defaults to the <c>SEE_DB</c> sentinel and is resolved at run time from the
/// platform DB (<c>core.Param</c>, <c>LoaderName='EvolutionMarkets'</c>) by
/// <c>AddLoaderSettings&lt;EvoSettings&gt;</c>, or overridden via the environment variable
/// <c>DATALOADER_Loaders__EvolutionMarkets__ApiKey</c>. It is sent as the <b>raw value of the
/// <c>Authorization</c> request header</b> and is <b>never logged, never written to
/// appsettings.json and never put in a test fixture</b> (design §4.1, §10).
/// </para>
/// </summary>
public sealed class EvoSettings : LoaderSettingsBase
{
    /// <summary>The documented default host, and the fallback when the binding yields null/blank.</summary>
    public const string DefaultBaseUrl = "https://evolve-api.evomarkets.com";

    /// <summary>
    /// The vendor's own retention horizon, published per dataset as
    /// <c>GET /v1/datasets</c> → <c>lookbackWindow</c>, and observed as <b>60</b> for
    /// <c>EVOID/USNaturalGasIndex</c> (docs/apis §3.2).
    ///
    /// <para>A <c>dateFrom</c> older than this is <b>not an error</b> — it returns <c>200</c> with an
    /// empty array, exactly like a weekend — so a too-large <see cref="DaysBack"/> costs wasted
    /// requests rather than a failure. <c>EvolutionMarketsModule.RunAsync</c> warns when
    /// <see cref="DaysBack"/> exceeds this, because a settled-zone date beyond the horizon would be
    /// recorded permanently done having never returned a single row.</para>
    /// </summary>
    public const int VendorLookbackDays = 60;

    /// <summary>The vendor's hard per-request row cap (<c>limit</c> must be 1..10000).</summary>
    public const int MaxVendorPageSize = 10000;

    /// <summary>API host. The version segment (<c>/v1</c>) lives in the endpoint path, not here.</summary>
    public string BaseUrl { get; set; } = DefaultBaseUrl;

    /// <summary>
    /// <see cref="BaseUrl"/> with any trailing <c>/</c> removed, ready to concatenate with a
    /// leading-slash relative path.
    ///
    /// <para>Coalesces to <see cref="DefaultBaseUrl"/> when the configuration binds
    /// <c>Loaders:EvolutionMarkets:BaseUrl</c> to JSON <c>null</c> (or blank): the property is
    /// non-nullable, so the binder happily assigns <c>null</c> over the initialiser and every call
    /// site would then <c>NullReferenceException</c> on its first request instead of failing with a
    /// diagnosable message. Use this — never <c>BaseUrl.TrimEnd('/')</c> — at every request-building
    /// site.</para>
    /// </summary>
    internal string BaseUrlRoot() =>
        (string.IsNullOrWhiteSpace(BaseUrl) ? DefaultBaseUrl : BaseUrl).TrimEnd('/');

    /// <summary>
    /// The API key. Resolved from
    /// <c>core.Param(LoaderName='EvolutionMarkets', ParamName='ApiKey')</c>.
    ///
    /// <para><b>⚠ Despite being described as "Basic authentication" in the loader request, this is
    /// NOT HTTP Basic.</b> The vendor's own OpenAPI document declares the scheme as
    /// <c>type: apiKey</c>, <c>name: Authorization</c>, <c>in: header</c>, and the live API accepts the
    /// key as the <b>raw, un-encoded</b> header value — <c>Authorization: &lt;key&gt;</c>, NOT
    /// <c>Authorization: Basic base64(user:pass)</c>. A Basic-encoded value yields <c>403</c>. See
    /// <see cref="EvoApiKeyAuthHandler"/> and docs/apis §2.</para>
    ///
    /// <para><b>Never logged.</b> Because the credential is a header and never a query parameter, no
    /// Evolution Markets URL carries a secret — request URIs are safe to log in full, which is why
    /// this loader logs them unsanitised (contrast CWG/StormVista, whose <c>?apikey=</c> URLs must be
    /// stripped before logging).</para>
    /// </summary>
    public string ApiKey { get; set; } = "SEE_DB";

    /// <summary>Per-request timeout on the data client.</summary>
    public int HttpTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Endpoints to run this pass; matched case-insensitively against
    /// <see cref="IEvoPipeline.EndpointId"/>. Defaults to the single in-scope endpoint.
    ///
    /// <para>The property is non-nullable, but a <c>Loaders:EvolutionMarkets:EnabledEndpoints</c>
    /// bound to JSON <c>null</c> would overwrite the initialiser with <c>null</c>;
    /// <c>EvolutionMarketsModule.RunAsync</c> therefore coalesces it to an empty list — "run
    /// nothing", reported as a warning + success — instead of throwing
    /// <see cref="ArgumentNullException"/>.</para>
    /// </summary>
    public string[] EnabledEndpoints { get; set; } = { EvoEndpoints.MarketDataHistory };

    /// <summary>
    /// Trailing-window length in days, ending at <b>today</b> inclusive → business dates
    /// <c>[runDate−(DaysBack−1) … runDate]</c> on the US-Central calendar (design §3.2).
    /// Clamped to ≥ 1 by <see cref="EvoTime.ResolveWindow"/>. Default <b>30</b>, per the loader spec.
    ///
    /// <para>Values above <see cref="VendorLookbackDays"/> are permitted but warned about: beyond the
    /// vendor's 60-day retention horizon a request simply returns an empty array.</para>
    /// </summary>
    public int DaysBack { get; set; } = 30;

    /// <summary>
    /// Candidate-date age boundary (days) separating the <b>settled</b> zone (stable key → loaded
    /// once, then a cheap <c>core.LoadLog</c> skip forever) from the <b>hot</b> zone (run-varying
    /// key → re-pulled every run) — design §3.3.
    ///
    /// <para>At the shipped default <c>30</c> with <see cref="DaysBack"/> <c>= 30</c> the maximum age
    /// in the window is <b>29</b>, so <c>age &gt; SettledAfterDays</c> is never true: the settled zone
    /// is <b>empty</b> and the whole window is hot and re-probed every run. That is exactly what
    /// "go back 30 days by default" asks for, and it is what makes revisions land — see below.</para>
    ///
    /// <para><b>⚠ Configuration hazard — two distinct losses.</b> Lowering this below
    /// <see cref="DaysBack"/> opens a settled zone, and a settled date is pulled once and then never
    /// re-probed. That loses BOTH:</para>
    /// <list type="number">
    ///   <item><b>Revisions.</b> This feed carries no revision, version or status field, and
    ///     <c>marketDataId</c> is stable per (instrument, term, tenor, business date) — so a
    ///     corrected price is delivered by <b>re-serving the same row id</b>. Only a re-pull can see
    ///     it. A settled date freezes its first print forever.</item>
    ///   <item><b>Late publication.</b> An empty <c>200</c> completes its unit as SUCCESS (it is
    ///     indistinguishable from a weekend), so a date that was merely <i>not published yet</i> when
    ///     first probed is recorded as permanently done and its prices are lost silently.</item>
    /// </list>
    /// <para><b>Recommended: leave this &gt;= <see cref="DaysBack"/> (the shipped 30/30 all-hot
    /// default).</b> Clamped to <c>&gt;= 0</c> at the point of use (a negative value would settle the
    /// ENTIRE window), and <c>EvolutionMarketsModule.RunAsync</c> logs a warning at run start whenever
    /// it is below <see cref="DaysBack"/>, naming the age band that turns stable. The hazard therefore
    /// never opens silently. See design §3.5.</para>
    /// </summary>
    public int SettledAfterDays { get; set; } = 30;

    /// <summary>Hot-zone key suffix strategy — drives the re-pull cadence of the whole window (design §3.3).</summary>
    public EvoHotKeyStrategy HotZoneKeyStrategy { get; set; } = EvoHotKeyStrategy.RunDate;

    /// <summary>
    /// Rows requested per page via <c>&amp;limit=</c> (design §5.5). Clamped to
    /// <c>1</c>..<see cref="MaxVendorPageSize"/> at the point of use — the vendor rejects anything
    /// outside that with <c>400 "The field Limit must be between 1 and 10000."</c>
    ///
    /// <para>Default <b>5000</b>: one business date of the only permissioned dataset is ~205 rows, so
    /// a single page covers a whole date ~24x over. The pager exists as a <b>correctness guard, not an
    /// optimisation</b> — the response is a bare JSON array with no envelope, no total count and no
    /// next-page link, so silently hitting the vendor cap would be indistinguishable from a complete
    /// read. See <see cref="EvoMarketDataSourceReader"/>.</para>
    /// </summary>
    public int PageSize { get; set; } = 5000;

    /// <summary>
    /// Optional <c>&amp;datasetName=</c> filter. <c>null</c>/blank (the default) requests <b>every
    /// dataset the key is permissioned for</b>, which is the intended posture: the entitlement set can
    /// grow without a config change.
    ///
    /// <para>On 2026-08-25 the key was permissioned for exactly one dataset,
    /// <c>EVOID/USNaturalGasIndex</c>. Set this only to deliberately narrow the pull.</para>
    /// </summary>
    public string? DatasetName { get; set; }

    /// <summary>
    /// Optional global client-side throttle (requests/second). <c>null</c> or ≤ 0 = unlimited.
    /// The vendor publishes no rate limit, no <c>Retry-After</c> and no <c>X-RateLimit-*</c> header,
    /// and no <c>429</c> was observed across ~60 probe calls — so pace gently and back off on
    /// <c>429</c> (design §4.4). Default 5.
    /// </summary>
    public double? RequestsPerSecond { get; set; } = 5;
}
