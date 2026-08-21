using DataLoader.Core.Configuration;

namespace DataLoader.IIR;

/// <summary>
/// How the "current-state" work-unit resume key varies between runs (design §5.3).
/// Mirrors CWG's / AGSI's <c>HotKeyStrategy</c> semantics.
/// </summary>
public enum IirHotKeyStrategy
{
    /// <summary>Key is the US-Central run date → each endpoint loads once per Central calendar day (recommended).</summary>
    RunDate,

    /// <summary>Key gains a <c>:run={RunId}</c> suffix → every invocation re-pulls (the idempotent MERGE keeps it safe).</summary>
    RunId
}

/// <summary>
/// IIR (Industrial Info Resources — IDB API v2.7) loader settings, bound from
/// <c>Loaders:IIR</c> (design §10). Inherits the common bits (connection string,
/// retry, concurrency, per-unit timeout) and adds the API/auth/paging fields.
///
/// <para>
/// <see cref="Username"/> / <see cref="Password"/> default to the <c>SEE_DB</c>
/// sentinel and are resolved at run time from the platform DB (<c>core.Param</c>) by
/// <c>AddLoaderSettings&lt;IirSettings&gt;</c>, or overridden via the environment
/// variables <c>DATALOADER_Loaders__IIR__Username</c> / <c>…__Password</c>. They mint
/// the JWT presented as <c>Authorization: Bearer &lt;jwt&gt;</c> on every data call and
/// are <b>never written to logs</b> (they ride in the <c>/token</c> query string).
/// </para>
/// </summary>
public sealed class IirSettings : LoaderSettingsBase
{
    /// <summary>API host (production <c>https://api.industrialinfo.com</c>; test <c>https://apitest.industrialinfo.com</c>).</summary>
    public string BaseUrl { get; set; } = "https://api.industrialinfo.com";

    /// <summary>Pins the <c>/idb/{version}/…</c> minor version for reproducibility.</summary>
    public string Version { get; set; } = "v2.7";

    /// <summary>Explicit <c>/token</c> URL; empty ⇒ derive <c>{BaseUrl}/idb/{Version}/token</c> (design §3.1).</summary>
    public string TokenEndpoint { get; set; } = "";

    /// <summary>Login username. Resolved from <c>core.Param</c>; never logged.</summary>
    public string Username { get; set; } = "SEE_DB";

    /// <summary>Login password. Resolved from <c>core.Param</c>; never logged.</summary>
    public string Password { get; set; } = "SEE_DB";

    /// <summary><c>tokenLifeTime</c> in days (default 1, max 30); the 401 re-mint handles expiry regardless.</summary>
    public int TokenLifetimeDays { get; set; } = 1;

    /// <summary>Per-request timeout on both HttpClients.</summary>
    public int HttpTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Optional global client-side throttle (requests/second). <c>null</c> or ≤ 0 = unlimited. No IIR
    /// rate limit is published — pace conservatively and back off on 429 (design §10/§11). Default 5
    /// (the two-step pull is request-heavy — ~2,345 requests for plants; live-verify the real limit).
    /// </summary>
    public double? RequestsPerSecond { get; set; } = 5;

    /// <summary>Endpoints to run this pass; matched case-insensitively against pipeline endpoint ids. Defaults to all three (single source: <see cref="IirDescriptors.AllIds"/>).</summary>
    public string[] EnabledEndpoints { get; set; } = IirDescriptors.AllIds;

    /// <summary>Summary page size (<c>limit</c>), clamped to <c>[1, 1000]</c> (summary max = 1000).</summary>
    public int SummaryPageSize { get; set; } = 1000;

    /// <summary>
    /// STEP-1 <c>physicalAddressCountryName</c> filter values, applied as repeated query keys on ALL
    /// three summary calls (design §4/§10). Default U.S.A. + Canada (the North-American scope).
    /// </summary>
    public string[] PhysicalAddressCountryNames { get; set; } = { "U.S.A.", "Canada" };

    /// <summary>
    /// OPTIONAL OfflineEvent <c>eventKind</c> narrowing (repeated query keys); appended only when
    /// non-empty. Default EMPTY — the country-only pull (design §5.2).
    /// </summary>
    public string[] OfflineEventKinds { get; set; } = Array.Empty<string>();

    /// <summary>
    /// OPTIONAL OfflineEvent <c>eventStatusDesc</c> narrowing (repeated query keys); appended only when
    /// non-empty. Default EMPTY — the country-only pull (design §5.2).
    /// </summary>
    public string[] OfflineEventStatuses { get; set; } = Array.Empty<string>();

    /// <summary>STEP-2 detail id batch size, clamped to <c>[1, 50]</c> (detail <c>limit</c> max = 50). Detail is now unconditional for all three endpoints (design §4).</summary>
    public int DetailBatchSize { get; set; } = 50;

    /// <summary>
    /// TVP MERGE chunk size (design §15.8): the plants catalogue is ~112k rows, so the sink splits a
    /// large batch into this-many-row MERGE calls rather than one huge TVP. Clamped to a sane minimum.
    /// Default 10000. (Loader addition — not in the design's §10 table.)
    /// </summary>
    public int MergeChunkSize { get; set; } = 10000;

    /// <summary>Resume-key cadence (design §5.3): <see cref="IirHotKeyStrategy.RunDate"/> = one pull per Central day; <see cref="IirHotKeyStrategy.RunId"/> = re-pull each run.</summary>
    public IirHotKeyStrategy HotKeyStrategy { get; set; } = IirHotKeyStrategy.RunDate;
}
