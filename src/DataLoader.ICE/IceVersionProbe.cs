using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.ICE;

/// <summary>What a metadata-only <c>HEAD</c> said about one (feed, trade date) file.</summary>
public enum IceFileVersionKind
{
    /// <summary>The file exists; <see cref="IceFileVersion.Token"/> is its version stamp.</summary>
    Published,

    /// <summary>
    /// ICE answered, but with no <c>Last-Modified</c> — the shape of a file that is not
    /// published (yet, or ever: weekend, holiday, future date). A one-time <c>GET</c>
    /// still confirms it through the content classifier.
    /// </summary>
    Absent,

    /// <summary>
    /// The <c>HEAD</c> itself failed (transport error, non-2xx after retries). The
    /// caller must fall back to an unconditional <c>GET</c> rather than guess.
    /// </summary>
    Unknown
}

/// <summary>The outcome of one <see cref="IIceVersionProbe.ProbeAsync"/>.</summary>
public readonly record struct IceFileVersion(IceFileVersionKind Kind, string? Token)
{
    public static IceFileVersion Absent { get; } = new(IceFileVersionKind.Absent, null);
    public static IceFileVersion Unknown { get; } = new(IceFileVersionKind.Unknown, null);

    public static IceFileVersion Published(string token) => new(IceFileVersionKind.Published, token);

    /// <summary>
    /// Render a <c>Last-Modified</c> value as the version token <c>yyyyMMddHHmmss</c>.
    ///
    /// <para>
    /// ⚠ <b>This is an opaque stamp, not an instant.</b> ICE labels the header
    /// <c>GMT</c> but the value is US Eastern wall-clock — <c>Mon, 09 Nov 2026
    /// 17:05:04 GMT</c> is the file the directory listing shows at <c>17:05 (ET)</c>
    /// (verified live 2026-11-10). It is only ever compared for equality with
    /// another value ICE produced the same way, never with a clock, so the mislabel
    /// is harmless here — and must stay that way.
    /// </para>
    /// </summary>
    public static string Format(DateTimeOffset lastModified) =>
        lastModified.UtcDateTime.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
}

/// <summary>
/// Asks ICE for a file's version without downloading it.
/// </summary>
public interface IIceVersionProbe
{
    Task<IceFileVersion> ProbeAsync(IceFeedDescriptor feed, DateOnly tradeDate, CancellationToken cancellationToken);
}

/// <summary>
/// <c>HEAD</c> against <c>downloads.ice.com</c>, read for <c>Last-Modified</c>.
///
/// <para>
/// Verified live 2026-11-10 on all four URL families the loader uses
/// (<c>Settlement_Reports_CSV/*.dat</c>, <c>Crude_Index/*.csv</c>,
/// <c>ICEF_options_greeks/*.dat</c>, and both XLSX sets):
/// </para>
/// <list type="bullet">
///   <item>a published file → <c>200</c>, <c>Last-Modified</c>, <c>Content-Disposition: attachment</c>, no body;</item>
///   <item>an unpublished one (today before ~17:00 ET, a weekend) → <c>200 text/html</c>, NO <c>Last-Modified</c>;</item>
///   <item>a bad token → <c>302</c> to <c>sso.ice.com</c>, which the shared client follows, so it arrives as a
///         response whose final URI is on another host;</item>
///   <item><c>If-Modified-Since</c> is IGNORED (a full <c>200</c> body comes back), so a conditional
///         <c>GET</c> is not an option — this probe is the only cheap change check.</item>
/// </list>
/// <para>
/// A <c>HEAD</c> costs one request against ICE's 30/minute account-wide limit, exactly
/// like a download — it goes through the same paced <see cref="HttpClient"/>.
/// </para>
/// </summary>
public sealed class IceHeadVersionProbe : IIceVersionProbe
{
    /// <summary>
    /// Bound on ONE HEAD, retries included. A HEAD answers in ~0.1 s; without this the
    /// client's download-sized <see cref="IceSettings.HttpTimeoutSeconds"/> (300 s)
    /// would apply, and a hung host would hold the overlap lock for hours across 84
    /// (or 651) sequential probes.
    /// </summary>
    internal static readonly TimeSpan DefaultHeadTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// After this many CONSECUTIVE Unknown results, stop sending HEADs for the rest of
    /// this process (one run) and answer Unknown at once — every unit then downloads,
    /// which is exactly what the clock strategies did, instead of paying a timeout per
    /// date first. Any successful HEAD resets the count.
    /// </summary>
    internal const int BreakerThreshold = 5;

    private readonly HttpClient _http;
    private readonly IIceAuthenticator _auth;
    private readonly IceSettings _settings;
    private readonly ILogger _logger;
    private readonly TimeSpan _headTimeout;
    private int _consecutiveUnknown;

    public IceHeadVersionProbe(HttpClient http, IIceAuthenticator auth, IOptions<IceSettings> settings, ILogger logger)
        : this(http, auth, settings, logger, DefaultHeadTimeout)
    {
    }

    internal IceHeadVersionProbe(
        HttpClient http, IIceAuthenticator auth, IOptions<IceSettings> settings, ILogger logger, TimeSpan headTimeout)
    {
        _http = http;
        _auth = auth;
        _settings = settings.Value;
        _logger = logger;
        _headTimeout = headTimeout;
    }

    /// <summary>True once the breaker has opened for this process.</summary>
    internal bool BreakerOpen => Volatile.Read(ref _consecutiveUnknown) >= BreakerThreshold;

    public async Task<IceFileVersion> ProbeAsync(IceFeedDescriptor feed, DateOnly tradeDate, CancellationToken cancellationToken)
    {
        if (BreakerOpen) return IceFileVersion.Unknown;

        var version = await ProbeOnceAsync(feed, tradeDate, cancellationToken).ConfigureAwait(false);

        if (version.Kind == IceFileVersionKind.Unknown)
        {
            if (Interlocked.Increment(ref _consecutiveUnknown) == BreakerThreshold)
                _logger.LogWarning(
                    "ICE: {Count} consecutive HEADs failed — skipping version checks for the rest of this run; " +
                    "every remaining hot date will download unconditionally", BreakerThreshold);
        }
        else
        {
            Interlocked.Exchange(ref _consecutiveUnknown, 0);
        }

        return version;
    }

    private async Task<IceFileVersion> ProbeOnceAsync(IceFeedDescriptor feed, DateOnly tradeDate, CancellationToken cancellationToken)
    {
        var url = IceUrls.For(_settings, feed, tradeDate);

        // ⚠ OUTSIDE the catch-all: a sign-on that FAILS must fail this feed's
        // enumeration, not become Unknown. Swallowed, it would retry the SSO login once
        // per date — 651 login attempts in a full run, an account-lockout risk.
        var token = await _auth.GetTokenAsync(cancellationToken).ConfigureAwait(false);

        var (version, authRedirect) = await SafeHeadAsync(feed, tradeDate, url, token, cancellationToken).ConfigureAwait(false);
        if (!authRedirect) return version;

        _logger.LogInformation("ICE {Feed} {Date}: HEAD redirected to sign-on — refreshing token and retrying once",
            feed.FeedId, IceTime.Iso(tradeDate));

        // Same rule: a refresh that throws is a sign-on failure and propagates.
        var fresh = await _auth.RefreshAsync(token, cancellationToken).ConfigureAwait(false);
        (version, authRedirect) = await SafeHeadAsync(feed, tradeDate, url, fresh, cancellationToken).ConfigureAwait(false);

        // Still bounced to sign-on: we cannot see this file's version. Unknown, NOT
        // Absent — Absent may be recorded as "confirmed missing" and skipped.
        return authRedirect ? IceFileVersion.Unknown : version;
    }

    /// <summary>One HEAD; any failure but the caller's own cancellation becomes Unknown.</summary>
    private async Task<(IceFileVersion Version, bool AuthRedirect)> SafeHeadAsync(
        IceFeedDescriptor feed, DateOnly tradeDate, string url, string token, CancellationToken cancellationToken)
    {
        try
        {
            return await HeadAsync(url, token, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Never fatal: Unknown routes the unit to an unconditional, cache-bypassing
            // GET, which has the full content classifier behind it. A HEAD problem must
            // not hide a file. (A timeout lands here as an OperationCanceledException
            // with the caller's token NOT cancelled.)
            _logger.LogWarning("ICE {Feed} {Date}: HEAD failed ({Error}) — falling back to a download",
                feed.FeedId, IceTime.Iso(tradeDate), ex.Message);
            return (IceFileVersion.Unknown, false);
        }
    }

    private async Task<(IceFileVersion Version, bool AuthRedirect)> HeadAsync(string url, string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, url);
        request.Headers.TryAddWithoutValidation("Cookie", $"iceSsoCookie={token}");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_headTimeout);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
            .ConfigureAwait(false);

        if (IsAuthRedirect(request, response)) return (IceFileVersion.Unknown, true);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("ICE HEAD {Url} answered HTTP {Status} — falling back to a download",
                url, (int)response.StatusCode);
            return (IceFileVersion.Unknown, false);
        }

        return (Interpret(response), false);
    }

    /// <summary>
    /// Read a successful HEAD. <b>Absent is inferred narrowly</b>, because a stable
    /// <c>:lm=none</c> key is skipped once confirmed: only a response with NO
    /// <c>Last-Modified</c> header at all AND no <c>Content-Disposition</c> — the
    /// verified shape of an unpublished file — is Absent. A file-shaped response whose
    /// <c>Last-Modified</c> is missing or unparseable is Unknown (always download),
    /// never Absent.
    /// </summary>
    internal static IceFileVersion Interpret(HttpResponseMessage response)
    {
        var headers = response.Content.Headers;

        if (headers.LastModified is { } value)
            return IceFileVersion.Published(IceFileVersion.Format(value));

        var hasRawLastModified = headers.Contains("Last-Modified");
        var looksLikeAFile = headers.ContentDisposition is not null;

        return hasRawLastModified || looksLikeAFile ? IceFileVersion.Unknown : IceFileVersion.Absent;
    }

    /// <summary>
    /// True when ICE bounced the request to its sign-on host — either as a raw
    /// <c>3xx</c> (a client that does not follow redirects) or as a followed redirect
    /// whose final URI is no longer the downloads host.
    /// </summary>
    internal static bool IsAuthRedirect(HttpRequestMessage original, HttpResponseMessage response)
    {
        var code = (int)response.StatusCode;
        if (code is >= 300 and < 400) return true;

        var requested = original.RequestUri;
        var final = response.RequestMessage?.RequestUri;
        return requested is not null && final is not null &&
               !string.Equals(requested.Host, final.Host, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>The one place a feed/date becomes an absolute download URL.</summary>
internal static class IceUrls
{
    public static string For(IceSettings settings, IceFeedDescriptor feed, DateOnly tradeDate) =>
        $"{settings.DownloadBaseUrl.TrimEnd('/')}/{feed.PathFor(tradeDate)}";
}
