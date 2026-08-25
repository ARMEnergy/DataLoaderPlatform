using DataLoader.Core.Configuration;

namespace DataLoader.ModernCommodities;

/// <summary>
/// How the work-unit resume key varies between runs (design §3.3).
///
/// <para><b>⚠ There is NO settled variant, and none must ever be added.</b> Every other
/// date-windowed loader here (StormVista, AGSI, NGI) splits its window into a settled zone
/// (stable key → loaded once, forever) and a hot zone. ModernCommodities must not, because
/// <b>nothing in this feed is ever final</b>: the trades window filters on
/// <c>Last Updated Timestamp</c>, a trade's <c>State</c> can flip <c>Finalized → Cancelled</c>
/// months later, and the rolling re-pull is the <b>only</b> mechanism that surfaces revisions and
/// cancellations. A stable key would freeze a trade at its pre-revision state forever while the
/// loader reported clean runs (design "Rationale A").</para>
///
/// <para>All three tokens are stamped in <b>UTC</b>. <see cref="RunHour"/>'s <c>yyyyMMddHH</c> is
/// strictly monotonic only in UTC — <c>01:00</c> Central/Mountain occurs twice on a fall-back
/// night, so a local-zone hour token would repeat, the key would go backwards, and an
/// already-recorded success would suppress a legitimate re-pull for an hour (design §7).</para>
/// </summary>
public enum ModComHotKeyStrategy
{
    /// <summary>
    /// Suffix the key with the run's UTC <c>yyyyMMddHH</c> → one re-pull per clock hour; a second
    /// run inside the same hour idempotently skips. <b>The default</b>, matching the user's
    /// hourly / several-times-a-day schedule (decision U5).
    /// </summary>
    RunHour,

    /// <summary>Suffix the key with the UTC run date <c>yyyyMMdd</c> → one re-pull per UTC day.</summary>
    RunDate,

    /// <summary>Suffix the key with the run id → <b>every</b> invocation re-pulls (diagnostics / a forced re-pull).</summary>
    RunId
}

/// <summary>
/// Per-endpoint override of the two window-scaling knobs (decision D10). Bound from
/// <c>Loaders:ModernCommodities:Endpoints:&lt;EndpointId&gt;</c>. Both are nullable so an absent
/// entry falls through to the global value.
///
/// <para>This is the documented, <b>code-free</b> path to a deep backfill — e.g.
/// <c>MyTrades: { "DaysBack": 3650 }</c> to exploit its all-time history, or
/// <c>AllTrades: { "DaysBack": 183, "ChunkDays": 30 }</c> /
/// <c>Settlements: { "DaysBack": 183, "ChunkDays": 60 }</c> for a full-history pull. See design
/// §3.4 for the row-cap arithmetic behind those numbers — in particular that a single full
/// 6-month settlements request lands at ~94 % of the 100,000-row cap and <b>must not</b> be
/// attempted, and that a trades window wider than ≈171 days is over its 10,000-row cap.</para>
/// </summary>
public sealed class ModComEndpointOverride
{
    /// <summary>Trailing-window length for this endpoint. <c>null</c> = use the global <see cref="ModComSettings.DaysBack"/>.</summary>
    public int? DaysBack { get; set; }

    /// <summary>Inclusive calendar days per request for this endpoint. <c>null</c> = use the global <see cref="ModComSettings.ChunkDays"/>.</summary>
    public int? ChunkDays { get; set; }
}

/// <summary>
/// Modern Commodities ("ModCom") loader settings, bound from <c>Loaders:ModernCommodities</c>
/// (design §10). Inherits the common bits (connection string, retry, concurrency, per-unit
/// timeout) and adds the API, auth, windowing and resume-key fields.
///
/// <para><b>SECURITY.</b> <see cref="Username"/> and <see cref="Password"/> both default to the
/// <c>SEE_DB</c> sentinel and are resolved at run time from the platform DB
/// (<c>core.Param</c>, <c>LoaderName='ModernCommodities'</c>) by
/// <c>AddLoaderSettings&lt;ModComSettings&gt;</c>, or overridden via
/// <c>DATALOADER_Loaders__ModernCommodities__Username</c> /
/// <c>…__Password</c>. They are presented as HTTP Basic in a request <b>header</b> and are
/// <b>never logged, never written to appsettings.json and never put in a test fixture</b>. The
/// vendor also publishes the pair pre-encoded as a ready-made <c>Authorization: Basic …</c>
/// value — <b>that encoded form is a credential too</b> (design §4.1).</para>
/// </summary>
public sealed class ModComSettings : LoaderSettingsBase
{
    /// <summary>The documented API root, and the fallback when the binding yields null/blank.</summary>
    public const string DefaultBaseUrl = "https://app.modcom.inc/api/integration/";

    /// <summary>
    /// API root. <b>The <c>/v1</c> segment is per endpoint</b> (<c>allTrades/v1</c>), not part of
    /// this path — see <see cref="ModComEndpointDescriptor.Path"/>.
    /// </summary>
    public string BaseUrl { get; set; } = DefaultBaseUrl;

    /// <summary>
    /// <see cref="BaseUrl"/> with any trailing <c>/</c> removed, ready to concatenate with a
    /// leading-slash relative path.
    ///
    /// <para>Coalesces to <see cref="DefaultBaseUrl"/> when the configuration binds
    /// <c>Loaders:ModernCommodities:BaseUrl</c> to JSON <c>null</c> (or blank): the property is
    /// non-nullable, so the binder happily assigns <c>null</c> over the initialiser and every call
    /// site would then <see cref="NullReferenceException"/> on its first request instead of failing
    /// with a diagnosable message. <b>Use this — never <c>BaseUrl.TrimEnd('/')</c> — at every
    /// request-building site</b> (the NGI pattern).</para>
    /// </summary>
    internal string BaseUrlRoot() =>
        (string.IsNullOrWhiteSpace(BaseUrl) ? DefaultBaseUrl : BaseUrl).TrimEnd('/');

    /// <summary>
    /// HTTP Basic user. Resolved from <c>core.Param(LoaderName='ModernCommodities', ParamName='Username')</c>.
    /// <b>Never logged.</b>
    /// </summary>
    public string Username { get; set; } = "SEE_DB";

    /// <summary>
    /// HTTP Basic password. Same resolution, same prohibitions. <b>Never logged.</b>
    /// </summary>
    public string Password { get; set; } = "SEE_DB";

    /// <summary>
    /// Per-request timeout. Higher than NGI's 60 because a chunked settlements pull can return tens
    /// of thousands of rows with <b>no compression negotiated</b> (a 30-day pull ≈ 1.5 MB; a 60-day
    /// chunk ≈ 3 MB) — design §10.
    /// </summary>
    public int HttpTimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Endpoints to run this pass; matched case-insensitively against
    /// <see cref="IModComPipeline.EndpointId"/>. <b>Any subset is a valid run</b> — the three
    /// pipelines are mutually independent, with no ordering requirement, no prerequisite table and
    /// no first-run bootstrap (design §1.2).
    ///
    /// <para>The property is non-nullable, but a value bound to JSON <c>null</c> would overwrite the
    /// initialiser; <c>ModernCommoditiesModule.RunAsync</c> therefore coalesces it to an empty list
    /// — "run nothing", reported as a warning + success — instead of throwing.</para>
    /// </summary>
    public string[] EnabledEndpoints { get; set; } = { "AllTrades", "MyTrades", "Settlements" };

    /// <summary>
    /// Global trailing-window length in days. <c>startDate = endDate − DaysBack</c>, and <b>both
    /// ends are inclusive</b>, so the shipped <c>30</c> spans <b>31</b> calendar days
    /// (decision U4 + D13).
    ///
    /// <para><b>⚠ Do not "fix" the arithmetic to <c>DaysBack − 1</c>.</b> The extra day is wanted:
    /// our <c>endDate</c> is UTC today while the vendor's clock runs behind UTC, and merge-by-PK
    /// makes an overlapping day free. Under-covering is the only failure mode that loses data
    /// (design §3.2 #2 / D13).</para>
    /// </summary>
    public int DaysBack { get; set; } = 30;

    /// <summary>
    /// Global chunk size in <b>inclusive</b> calendar days per request. <c>0</c> (or negative) =
    /// the whole window in a single request — the shipped default, giving <b>3 requests per
    /// run</b> (decision U6).
    ///
    /// <para><b>There is no paging.</b> An over-cap request is <b>rejected with 400</b>, not
    /// truncated, so narrowing the date range is the <i>only</i> way to stay under a cap and this is
    /// the sole scaling lever (design §3.4). Recommended for a deep backfill: <b>30</b> for the
    /// trades endpoints, <b>60</b> for settlements. <b>Never set <c>ChunkDays &gt; 90</c> on a
    /// trades endpoint</b> — the measured per-day rate is unreconciled by ~3×.</para>
    /// </summary>
    public int ChunkDays { get; set; }

    /// <summary>Resume-key cadence (design §3.3). <b>There is no settled-zone setting</b> — see <see cref="ModComHotKeyStrategy"/>.</summary>
    public ModComHotKeyStrategy HotKeyStrategy { get; set; } = ModComHotKeyStrategy.RunHour;

    /// <summary>
    /// <c>myTrades</c> only. <b>Omitted by default</b> — it is verified that omitting it returns
    /// trades for <b>both</b> ARM legal entities, so the default is the widest scope.
    ///
    /// <para>When set it is URL-encoded (the valid values contain spaces <b>and a comma</b>) and its
    /// slug enters the resume key, so switching the setting cannot make the run idempotently skip
    /// and quietly keep the old, wider data. An invalid value returns a <c>400</c> whose body
    /// enumerates the valid options (design §5.6).</para>
    /// </summary>
    public string? LegalEntityName { get; set; }

    /// <summary>
    /// Per-endpoint overrides keyed by <c>EndpointId</c> (decision D10). Looked up
    /// case-insensitively by <see cref="EffectiveDaysBack"/> / <see cref="EffectiveChunkDays"/> —
    /// the configuration binder builds an ordinal-comparer dictionary, so never index it directly.
    /// </summary>
    public Dictionary<string, ModComEndpointOverride> Endpoints { get; set; } = new();

    /// <summary>
    /// Global client-side throttle (requests/second); <c>null</c> or ≤ 0 = unlimited. The vendor
    /// publishes no numeric limit but warns the API <i>"is not intended to be rapidly polled"</i>
    /// (design §4.3). With 3 requests per run, pacing at 1 rps costs nothing.
    /// </summary>
    public double? RequestsPerSecond { get; set; } = 1;

    /// <summary>
    /// Minutes subtracted from <c>context.StartedAtUtc</c> to form the validator's
    /// <c>@ModifiedSinceUtc</c> (design §6.3). The tolerance exists because fact rows are stamped by
    /// the <b>SQL Server's</b> <c>SYSUTCDATETIME()</c> while the run start comes from the
    /// <b>host's</b> clock; without it a few seconds of skew would make every freshness check read
    /// zero.
    /// </summary>
    public int ValidationClockSkewMinutes { get; set; } = 5;

    /// <summary>Case-insensitive per-endpoint override lookup; <c>null</c> when none is configured.</summary>
    internal ModComEndpointOverride? OverrideFor(string endpointId)
    {
        if (Endpoints is null || Endpoints.Count == 0) return null;
        foreach (var kv in Endpoints)
            if (string.Equals(kv.Key, endpointId, StringComparison.OrdinalIgnoreCase))
                return kv.Value;
        return null;
    }

    /// <summary>Effective trailing-window length for an endpoint: its override, else the global value, clamped to ≥ 0.</summary>
    internal int EffectiveDaysBack(string endpointId) =>
        Math.Max(0, OverrideFor(endpointId)?.DaysBack ?? DaysBack);

    /// <summary>
    /// Effective chunk size for an endpoint: its override, else the global value. <c>≤ 0</c> means
    /// "one chunk = the whole window" and is returned as-is so callers can report the configured value.
    /// </summary>
    internal int EffectiveChunkDays(string endpointId) =>
        OverrideFor(endpointId)?.ChunkDays ?? ChunkDays;
}
