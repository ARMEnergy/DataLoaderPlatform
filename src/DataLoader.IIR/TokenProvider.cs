using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.IIR;

/// <summary>
/// Mints, caches and (thread-safely) re-mints the IIR JWT (design §3). There is no OAuth2 grant, no
/// scope and no refresh token — a login endpoint mints a JWT, then <c>Authorization: Bearer &lt;jwt&gt;</c>
/// is stamped on every data call and, on expiry/401, a fresh one is minted. Registered as a
/// <b>singleton</b> so all three pipelines' internal paging share one cached token and one re-mint path.
/// </summary>
public interface IIirTokenProvider
{
    /// <summary>Returns the cached JWT, minting once on first use (thread-safe).</summary>
    Task<string> GetTokenAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Re-mints the JWT only if the cached token still equals <paramref name="staleToken"/> (avoids a
    /// thundering herd when many parallel requests 401 at once); otherwise returns the
    /// already-refreshed cached token.
    /// </summary>
    Task<string> RefreshTokenAsync(string staleToken, CancellationToken cancellationToken);
}

/// <summary>
/// Thread-safe JWT provider over the dedicated un-authed <c>"IIR.Token"</c> HttpClient. Reads the
/// token TOLERANTLY from the response body (a JSON field named <c>token</c>/<c>access_token</c>/
/// <c>accessToken</c>/<c>jwt</c>, case-insensitive) OR a response header (design §3.2). The mint URL
/// carries the credentials in its query string, so the provider NEVER logs the URI (only a fixed
/// sanitized string) and the <c>"IIR.Token"</c> client has <c>.RemoveAllLoggers()</c>; the token and
/// credentials are never logged.
/// </summary>
public sealed class IirTokenProvider : IIirTokenProvider
{
    private static readonly string[] BodyTokenNames = { "token", "access_token", "accessToken", "jwt" };

    private readonly IHttpClientFactory _httpFactory;
    private readonly IirSettings _settings;
    private readonly ILogger<IirTokenProvider> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _token;

    public IirTokenProvider(IHttpClientFactory httpFactory, IOptions<IirSettings> settings, ILogger<IirTokenProvider> logger)
    {
        _httpFactory = httpFactory;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        var cached = _token;
        if (cached is not null) return cached;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_token is not null) return _token;
            _token = await MintAsync(cancellationToken).ConfigureAwait(false);
            return _token;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> RefreshTokenAsync(string staleToken, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Only the first of many concurrent 401 callers re-mints; the rest reuse the fresh token.
            if (_token is not null && !string.Equals(_token, staleToken, StringComparison.Ordinal))
                return _token;

            _token = await MintAsync(cancellationToken).ConfigureAwait(false);
            return _token;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// <c>POST {TokenEndpoint}?username=&amp;password=&amp;tokenLifeTime=&lt;days&gt;</c> (empty body) over the
    /// un-authed client. Throws on a non-2xx (a credential failure — surface loudly) or when neither
    /// channel yields a token. Never logs the URI/body/token/credentials.
    /// </summary>
    private async Task<string> MintAsync(CancellationToken cancellationToken)
    {
        var client = _httpFactory.CreateClient(IirModule.TokenClientName);
        var uri = BuildTokenUri();

        using var request = new HttpRequestMessage(HttpMethod.Post, uri);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"IIR /token mint failed with HTTP {(int)response.StatusCode} — verify the IIR credentials (core.Param LoaderName='IIR').");

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var token = ExtractToken(body, response);
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException(
                "Could not extract a JWT from the /token response — verify the token delivery channel per docs/apis/IIR.md §2.2.");

        _logger.LogDebug("IIR token minted (POST …/token)"); // sanitized — never the URI/token/creds
        return token!;
    }

    private Uri BuildTokenUri()
    {
        var endpoint = string.IsNullOrWhiteSpace(_settings.TokenEndpoint)
            ? $"{_settings.BaseUrl.TrimEnd('/')}/idb/{_settings.Version}/token"
            : _settings.TokenEndpoint;

        var lifetime = Math.Clamp(_settings.TokenLifetimeDays, 1, 30);
        var query =
            $"username={Uri.EscapeDataString(_settings.Username)}" +
            $"&password={Uri.EscapeDataString(_settings.Password)}" +
            $"&tokenLifeTime={lifetime}";

        var sep = endpoint.Contains('?') ? '&' : '?';
        return new Uri($"{endpoint}{sep}{query}", UriKind.Absolute);
    }

    /// <summary>
    /// Reads the JWT tolerantly (design §3.2): first the body (a JSON string field named
    /// token/access_token/accessToken/jwt, case-insensitive), else the response <c>Authorization</c>
    /// header, else any response header whose value looks like a bearer JWT. A leading <c>Bearer </c>
    /// is stripped.
    /// </summary>
    private static string? ExtractToken(string body, HttpResponseMessage response)
    {
        // 1) Body JSON field.
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var name in BodyTokenNames)
                    {
                        var v = IirParse.Str(doc.RootElement, name);
                        if (!string.IsNullOrWhiteSpace(v)) return StripBearer(v!);
                    }
                }
                else if (doc.RootElement.ValueKind == JsonValueKind.String)
                {
                    var v = doc.RootElement.GetString();
                    if (!string.IsNullOrWhiteSpace(v)) return StripBearer(v!);
                }
            }
            catch (JsonException) { /* not JSON — fall through to headers */ }
        }

        // 2) Authorization response header.
        if (response.Headers.TryGetValues("Authorization", out var authValues))
        {
            var v = authValues.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
            if (v is not null) return StripBearer(v);
        }

        // 3) Any response header value that looks like a bearer JWT.
        foreach (var header in response.Headers.Concat(response.Content.Headers))
        {
            foreach (var value in header.Value)
            {
                if (string.IsNullOrWhiteSpace(value)) continue;
                var trimmed = value.Trim();
                if (trimmed.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("ey", StringComparison.Ordinal))
                    return StripBearer(trimmed);
            }
        }

        return null;
    }

    private static string StripBearer(string value)
    {
        var v = value.Trim();
        return v.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? v.Substring(7).Trim() : v;
    }
}

/// <summary>
/// Delegating handler that stamps <c>Authorization: Bearer &lt;jwt&gt;</c> on every outgoing data request
/// and, on a <c>401</c>, re-mints once and retries the request a single time (design §3.3). Registered
/// INNER of the retry policy and OUTER of the throttle so it re-stamps on every retry attempt. A second
/// consecutive 401 propagates (a genuine credential failure, not an expiry). 401 is excluded from the
/// Polly policy precisely so the handler owns it. The token is never logged.
/// </summary>
public sealed class IirTokenAuthHandler : DelegatingHandler
{
    private readonly IIirTokenProvider _provider;

    public IirTokenAuthHandler(IIirTokenProvider provider) => _provider = provider;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await _provider.GetTokenAsync(cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != System.Net.HttpStatusCode.Unauthorized)
            return response;

        // Expiry / stale token → re-mint once and replay (the empty request body makes replay safe).
        response.Dispose();
        var fresh = await _provider.RefreshTokenAsync(token, cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fresh);
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
