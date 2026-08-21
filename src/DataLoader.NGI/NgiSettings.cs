using DataLoader.Core.Configuration;

namespace DataLoader.NGI;

/// <summary>
/// How the hot-zone (and the always-hot Locations) work-unit resume key varies
/// between runs — design §3.3.
///
/// <para><b><c>RunHour</c> is deliberately NOT offered.</b> Bidweek is a MONTHLY
/// feed, so nothing arrives intra-day worth re-probing more than once (contrast
/// IHSPointLogic, whose hourly endpoints need it). If an hourly cadence is ever
/// added, the hour token <b>must be UTC</b>: <c>01:00</c> Central occurs twice on a
/// fall-back night, so a Central <c>yyyyMMddHH</c> token would repeat and the key
/// would go backwards. At <b>date</b> granularity the Central calendar date is
/// monotonic non-decreasing across both DST transitions, which is why the
/// <see cref="RunDate"/> token is safe on the Central clock (design §3.3 / §7).</para>
/// </summary>
public enum NgiHotKeyStrategy
{
    /// <summary>Suffix the key with the US-Central run date → one re-pull per calendar day (the default).</summary>
    RunDate,

    /// <summary>Suffix the key with the run id → every invocation re-pulls.</summary>
    RunId
}

/// <summary>
/// NGI Data Services (Bidweek price survey) loader settings, bound from
/// <c>Loaders:NGI</c> (design §10). Inherits the common bits (connection string,
/// retry, concurrency, per-unit timeout) and adds the API, auth, windowing and
/// resume-heuristic fields.
///
/// <para>
/// <see cref="Username"/> and <see cref="Password"/> both default to the
/// <c>SEE_DB</c> sentinel and are resolved at run time from the platform DB
/// (<c>core.Param</c>, <c>LoaderName='NGI'</c>) by
/// <c>AddLoaderSettings&lt;NgiSettings&gt;</c>, or overridden via the environment
/// variables <c>DATALOADER_Loaders__NGI__Username</c> /
/// <c>DATALOADER_Loaders__NGI__Password</c>. They are POSTed in the
/// <c>/auth</c> request <b>body</b> and are <b>never logged, never written to
/// appsettings.json and never put in a test fixture</b> (design §4.1, §10).
/// </para>
/// </summary>
public sealed class NgiSettings : LoaderSettingsBase
{
    /// <summary>The documented default host, and the fallback when the binding yields null/blank.</summary>
    public const string DefaultBaseUrl = "https://api.ngidata.com";

    /// <summary>API host. No version segment and no <c>basePath</c> — the vendor spec has no <c>servers</c> block.</summary>
    public string BaseUrl { get; set; } = DefaultBaseUrl;

    /// <summary>
    /// <see cref="BaseUrl"/> with any trailing <c>/</c> removed, ready to concatenate with a
    /// leading-slash relative path.
    ///
    /// <para>Coalesces to <see cref="DefaultBaseUrl"/> when the configuration binds
    /// <c>Loaders:NGI:BaseUrl</c> to JSON <c>null</c> (or blank): the property is non-nullable, so the
    /// binder happily assigns <c>null</c> over the initialiser and every call site would then
    /// <c>NullReferenceException</c> on its first request instead of failing with a diagnosable
    /// message. Use this — never <c>BaseUrl.TrimEnd('/')</c> — at every request-building site.</para>
    /// </summary>
    internal string BaseUrlRoot() =>
        (string.IsNullOrWhiteSpace(BaseUrl) ? DefaultBaseUrl : BaseUrl).TrimEnd('/');

    /// <summary>Token-mint path appended to <see cref="BaseUrl"/> (design §4.1).</summary>
    public string AuthPath { get; set; } = "/auth";

    /// <summary>
    /// The account <b>e-mail</b>. Resolved from <c>core.Param(LoaderName='NGI', ParamName='Username')</c>.
    ///
    /// <para><b>⚠ It is sent as the JSON body field <c>email</c>, NOT <c>username</c></b> — the
    /// <c>NGITokenObtainPair</c> schema is <c>required: [email, password]</c> and a body posted with
    /// <c>username</c> will not authenticate. The <i>setting</i> keeps the platform's
    /// <c>Loaders:&lt;Id&gt;:Username</c> name for cross-loader consistency; the
    /// <c>Username → "email"</c> mapping happens in <see cref="NgiTokenProvider"/> (design §4.1).</para>
    /// </summary>
    public string Username { get; set; } = "SEE_DB";

    /// <summary>The account password. Resolved from <c>core.Param(LoaderName='NGI', ParamName='Password')</c>. Never logged.</summary>
    public string Password { get; set; } = "SEE_DB";

    /// <summary>Per-request timeout on both the data client and the un-authed token client.</summary>
    public int HttpTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Endpoints to run this pass; matched case-insensitively against
    /// <see cref="INgiPipeline.EndpointId"/>. Defaults to both.
    ///
    /// <para>The property is non-nullable, but a <c>Loaders:NGI:EnabledEndpoints</c> bound to JSON
    /// <c>null</c> would overwrite the initialiser with <c>null</c>; <c>NgiModule.RunAsync</c> therefore
    /// coalesces it to an empty list — "run nothing", reported as a warning + success — instead of
    /// throwing <see cref="ArgumentNullException"/>.</para>
    /// </summary>
    public string[] EnabledEndpoints { get; set; } = { "BidWeekLocations", "BidWeekData" };

    /// <summary>
    /// <c>BidWeekData</c> trailing-window length in days, ending at <b>today</b> inclusive → issue
    /// dates <c>[runDate−(DaysBack−1) … runDate]</c> on the US-Central calendar (design §3.2).
    /// <c>BidWeekLocations</c> ignores it. Clamped to ≥ 1 by <see cref="NgiTime.ResolveWindow"/>.
    /// </summary>
    public int DaysBack { get; set; } = 60;

    /// <summary>
    /// Candidate-date age boundary (days) separating the <b>settled</b> zone (stable key → loaded
    /// once, then a cheap <c>core.LoadLog</c> skip forever) from the <b>hot</b> zone (run-varying
    /// key → re-pulled every run) — design §3.3.
    ///
    /// <para>At the shipped default <c>60</c> with <see cref="DaysBack"/> <c>= 60</c> the maximum age
    /// in the window is <b>59</b>, so <c>age &gt; SettledAfterDays</c> is never true: the settled zone
    /// is <b>empty</b> and the whole window is hot and re-probed every run (design §3.5).</para>
    ///
    /// <para><b>⚠ Configuration hazard.</b> Lowering this makes the settled-zone-404 invariant
    /// load-bearing: a <c>404</c> completes its unit as SUCCESS, so a <b>transient</b> 404 on a
    /// settled (stable-key) date is recorded as permanently done and that issue is lost silently.
    /// <b>Recommended safe floor: 35</b> (longer than one monthly publication cycle); safest is
    /// <c>SettledAfterDays &gt;= DaysBack</c> (all-hot), which is the default. See design §3.5.</para>
    ///
    /// <para>Clamped to <c>&gt;= 0</c> at the point of use (a negative value would settle the ENTIRE
    /// window), and <c>NgiModule.RunAsync</c> logs a warning at run start whenever it is below
    /// <see cref="DaysBack"/> — naming the age band that turns stable — plus a stronger one below the
    /// recommended floor of 35. The hazard therefore never opens silently.</para>
    /// </summary>
    public int SettledAfterDays { get; set; } = 60;

    /// <summary>
    /// Hot-zone key suffix strategy — drives both the hot <c>BidWeekData</c> window and the
    /// always-hot Locations re-pull cadence (design §3.3).
    /// </summary>
    public NgiHotKeyStrategy HotZoneKeyStrategy { get; set; } = NgiHotKeyStrategy.RunDate;

    /// <summary>
    /// Optional global client-side throttle (requests/second) applied to <b>both</b> clients.
    /// <c>null</c> or ≤ 0 = unlimited. NGI publishes no rate limit, no <c>Retry-After</c> and no
    /// <c>X-RateLimit-*</c> header, and no <c>429</c> was ever observed — so pace gently and back off
    /// on <c>429</c> (design §4.4 / §12 item 9). Default 2.
    /// </summary>
    public double? RequestsPerSecond { get; set; } = 2;
}
