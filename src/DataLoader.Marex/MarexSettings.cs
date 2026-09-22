using DataLoader.Core.Configuration;

namespace DataLoader.Marex;

/// <summary>
/// How a feed's resume key varies between runs.
///
/// <para>All tokens are stamped in <b>UTC</b>: <c>yyyyMMddHH</c> is strictly monotonic
/// only in UTC, because 01:00 local occurs twice on a fall-back night, which would make
/// the key go backwards and let an already-recorded success suppress a legitimate
/// re-pull for an hour.</para>
/// </summary>
public enum MarexResumeKeyStrategy
{
    /// <summary>Suffix with the UTC run date <c>yyyyMMdd</c> — one load per UTC day.</summary>
    RunDate,

    /// <summary>
    /// Suffix with UTC <c>yyyyMMddHH</c> — one load per clock hour. <b>The default.</b>
    /// The gateway serves a LIVE snapshot: prices, settlements and market statistics all
    /// move during the trading day, so a key that only varies by date would let the
    /// first run of the day suppress every later one.
    /// </summary>
    RunHour,

    /// <summary>Suffix with the run id — <b>every</b> invocation reloads (diagnostics / forced reload).</summary>
    RunId
}

/// <summary>
/// Marex Neon loader settings, bound from <c>Loaders:Marex</c> via
/// <c>AddLoaderSettings&lt;MarexSettings&gt;</c> (which also wires the <c>SEE_DB</c>
/// indirection for the credentials). Inherits the common bits — destination connection
/// string, retry, concurrency, per-unit timeout — and adds the Neon connection.
/// </summary>
public sealed class MarexSettings : LoaderSettingsBase
{
    // ------------------------------------------------------------- the gateway

    /// <summary>
    /// The Neon SignalR gateway URL.
    ///
    /// <para><c>/api/crude</c> is the crude environment, which is what this account is
    /// entitled to — the issued token comes back with <c>scope: "crude"</c>.</para>
    /// </summary>
    public string ApiEndpoint { get; set; } = "https://app-ca.neon.markets/api/crude";

    /// <summary>
    /// The SignalR <b>hub name</b> on the gateway.
    ///
    /// <para><b>This is not a client label.</b> The SDK passes it straight to
    /// <c>HubConnection.CreateHubProxy(...)</c>, so a wrong value is a hub that does not
    /// exist and the gateway answers the negotiate with <b>HTTP 500</b> — after which
    /// the SDK retries every 5 seconds forever while <c>ConnectionStatus</c> sits on
    /// <c>Connecting</c> and no snapshot ever arrives. The vendor's own integration note
    /// suggests putting an application name here ("ARM-DataLoader"); that is exactly the
    /// failure just described, confirmed live.</para>
    ///
    /// <para>Leave it empty to use the SDK's own default, which is the correct
    /// <c>Gateway.Crude</c>. It is settable only so a future environment can be pointed
    /// at a differently-named hub without a code change.</para>
    /// </summary>
    public string HubName { get; set; } = string.Empty;

    /// <summary>
    /// Auth0 tenant that issues the gateway token (the OIDC issuer host).
    /// </summary>
    public string AuthDomain { get; set; } = "login.neon.markets";

    /// <summary>
    /// Auth0 application client id, issued by Marex. Public client — there is no client
    /// secret. Defaults to the <c>SEE_DB</c> sentinel, resolved from
    /// <c>core.Param(LoaderName='Marex', ParamName='ClientId')</c> at run time.
    /// </summary>
    public string ClientId { get; set; } = "SEE_DB";

    /// <summary>
    /// Neon account user name, resolved the same way.
    /// </summary>
    public string Username { get; set; } = "SEE_DB";

    /// <summary>
    /// Neon account password, resolved the same way. NEVER logged.
    ///
    /// <para>Auth is an Auth0 resource-owner password grant. If the account ever gets MFA
    /// the grant fails with <c>mfa_required</c> and the loader stops at the token step
    /// with that message — the fix is a non-interactive service account from Marex, not
    /// a code change.</para>
    /// </summary>
    public string Password { get; set; } = "SEE_DB";

    // -------------------------------------------------------------- the snapshot

    /// <summary>
    /// How long to wait for the gateway to deliver the opening snapshot set before
    /// giving up on the run.
    ///
    /// <para>Measured at roughly 5 seconds from <c>Connect</c> to the last of the six
    /// snapshots on a ~4,200-row closing-price set. The SDK's own internal snapshot
    /// request timeout is 600 s; there is no point waiting longer than that, and the
    /// default here is deliberately far shorter so a gateway that accepts the socket but
    /// never sends data fails the run promptly instead of holding the overlap lock.</para>
    /// </summary>
    public int SnapshotTimeoutSeconds { get; set; } = 120;

    /// <summary>Which feeds run. Matched case-insensitively against the descriptor ids.</summary>
    public string[] EnabledFeeds { get; set; } =
    {
        "PeriodGroup", "Period", "Product", "ClosingPrice", "MarketStatistic"
    };

    /// <summary>How a feed's resume key varies between runs.</summary>
    public MarexResumeKeyStrategy ResumeKeyStrategy { get; set; } = MarexResumeKeyStrategy.RunHour;

    /// <summary>
    /// Route the Neon SDK's own log4net output into the platform's logger.
    ///
    /// <para>Off by default: at its own INFO level the SDK logs a heartbeat line every
    /// two seconds and echoes the full connection config — <b>including the bearer
    /// token</b> — on every start. <see cref="MarexSdkLogBridge"/> drops the config line
    /// and demotes the heartbeats, but the safest default is still silence. Turn this on
    /// to diagnose a connection problem.</para>
    /// </summary>
    public bool EnableSdkLogging { get; set; }

    /// <summary>
    /// Sets <c>NeonApiConfig.EnableTracing</c>, which makes the SDK emit raw SignalR
    /// transport traces. Only useful with <see cref="EnableSdkLogging"/> on.
    /// </summary>
    public bool EnableSignalRTracing { get; set; }
}
