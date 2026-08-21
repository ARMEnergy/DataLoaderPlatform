using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.NGI;

/// <summary>
/// Mints, caches and (thread-safely) re-mints the NGI JWT (design §4). No OAuth2 grant, no scope and
/// <b>no usable refresh token</b> — <c>POST /auth</c> mints a JWT, then
/// <c>Authorization: Bearer &lt;access_token&gt;</c> is stamped on every data call and, on a
/// <c>401</c>, a fresh one is minted. Registered as a <b>singleton</b> so both pipelines share one
/// cached token and one re-mint path.
/// </summary>
public interface INgiTokenProvider
{
    /// <summary>Returns the cached JWT, minting once on first use (thread-safe).</summary>
    Task<string> GetTokenAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Re-mints the JWT only if the cached token still equals <paramref name="staleToken"/> (the
    /// thundering-herd guard: when many parallel requests <c>401</c> at once, exactly ONE mint
    /// happens); otherwise returns the already-refreshed cached token.
    /// </summary>
    Task<string> RefreshTokenAsync(string staleToken, CancellationToken cancellationToken);
}

/// <summary>
/// Thread-safe JWT provider over the dedicated un-authed <c>"NGI.Token"</c> HttpClient (design §4.1).
///
/// <para><b>⚠ THE SECRET IS IN THE REQUEST BODY, NOT THE URL.</b> This is the one substantive
/// difference from the IIR provider this class is modelled on: IIR puts its credentials in the mint
/// URL query string (so its rule is "never log the URI"), whereas NGI POSTs a JSON body
/// <c>{"email":…,"password":…}</c>. Therefore: <b>the request body is NEVER logged</b> — not at Trace,
/// not in an exception message, not on a serialisation failure — and <c>.RemoveAllLoggers()</c> is
/// still applied to the token client (belt-and-braces plus log-surface parity across loaders). The
/// mint logs exactly one fixed sanitised string. The token, the <c>Authorization</c> header, the
/// credentials and <c>refresh</c> are never logged.</para>
///
/// <para><b>⚠ The body field is <c>email</c>, NOT <c>username</c></b> (the <c>NGITokenObtainPair</c>
/// schema is <c>required: [email, password]</c>; a body posted with <c>username</c> will not
/// authenticate). The platform setting is nonetheless called <see cref="NgiSettings.Username"/> to
/// match the <c>Loaders:&lt;Id&gt;:Username</c> convention — this class is where the
/// <c>Username → "email"</c> mapping happens (design §4.1).</para>
///
/// <para><b>Caching:</b> the token is held for the process lifetime. The JWT's
/// <c>exp − iat = 86400</c> (24 h) vastly exceeds any run, so proactive <c>exp</c>-based pre-expiry is
/// deliberately NOT implemented (we never decode the token); the re-mint-once-on-401 in
/// <see cref="NgiTokenAuthHandler"/> is the correctness mechanism (design §4.3).</para>
/// </summary>
public sealed class NgiTokenProvider : INgiTokenProvider
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly NgiSettings _settings;
    private readonly ILogger<NgiTokenProvider> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _token;

    public NgiTokenProvider(IHttpClientFactory httpFactory, IOptions<NgiSettings> settings, ILogger<NgiTokenProvider> logger)
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
    /// <c>POST {BaseUrl}{AuthPath}</c> with the JSON body <c>{"email":…,"password":…}</c> over the
    /// un-authed client (design §4.1). Throws on a non-2xx — a credential failure must be immediate
    /// and loud — with a message naming the <c>core.Param</c> rows to fix and <b>never</b> the values.
    /// Never logs the URI, the body, the credentials or the token.
    /// </summary>
    private async Task<string> MintAsync(CancellationToken cancellationToken)
    {
        var client = _httpFactory.CreateClient(NgiModule.TokenClientName);
        var uri = BuildAuthUri();

        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            // Built here (not from a shared serializer) so the credential-bearing string is created
            // as late as possible and never flows anywhere but the socket. NEVER log this content.
            Content = new StringContent(BuildAuthBody(), Encoding.UTF8, "application/json")
        };

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"NGI {_settings.AuthPath} token mint failed with HTTP {(int)response.StatusCode} — verify " +
                "core.Param(LoaderName='NGI', ParamName='Username') and core.Param(LoaderName='NGI', ParamName='Password') " +
                "(or the DATALOADER_Loaders__NGI__Username / __Password environment variables).");

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var token = ExtractToken(body, response);
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException(
                "Could not extract an access_token from the NGI /auth response — verify the token delivery " +
                "channel per docs/apis/NGI.md §2.2.");

        _logger.LogDebug("NGI token minted (POST …/auth)"); // fixed sanitised string — never the URI/body/creds/token
        return token!;
    }

    private Uri BuildAuthUri()
    {
        var path = string.IsNullOrWhiteSpace(_settings.AuthPath) ? "/auth" : _settings.AuthPath;
        if (!path.StartsWith('/')) path = "/" + path;
        return new Uri($"{_settings.BaseUrlRoot()}{path}", UriKind.Absolute);
    }

    /// <summary>
    /// The mint body. <b>The field is <c>email</c>, not <c>username</c></b> — see the class remarks.
    /// Serialised through <see cref="JsonSerializer"/> so a credential containing a quote or a
    /// backslash is escaped correctly rather than producing malformed JSON.
    /// </summary>
    internal string BuildAuthBody() =>
        JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["email"] = _settings.Username,     // <-- Loaders:NGI:Username maps to the API field "email"
            ["password"] = _settings.Password
        });

    /// <summary>
    /// Reads the bearer credential tolerantly (design §4.2): first a body JSON property matching
    /// <c>access_token</c>/<c>accessToken</c>/<c>token</c>/<c>jwt</c> case-insensitively, else the
    /// response <c>Authorization</c> header, else any response header value that looks like a bearer
    /// JWT. A leading <c>Bearer </c> is stripped. The spec's <c>Token</c> schema declares ONLY
    /// <c>access_token</c> while the live body returns TWO fields, so unknown members are ignored
    /// (STJ default) and <c>JsonUnmappedMemberHandling.Disallow</c> is never used.
    /// </summary>
    private static string? ExtractToken(string body, HttpResponseMessage response)
    {
        // 1) Body JSON field.
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    // Capture-and-ignore `refresh` (design §4.2): read it so a shape-strict binder can
                    // never trip on it, then DISCARD it immediately. There is NO refresh endpoint in the
                    // 59-path spec — renewal is a re-POST of /auth. It is a CREDENTIAL: never stored,
                    // cached, persisted, logged, or written to a fixture.
                    _ = NgiParse.Prop(root, NgiFields.RefreshToken);

                    var v = NgiParse.Str(root, NgiFields.AccessToken);
                    if (!string.IsNullOrWhiteSpace(v)) return StripBearer(v!);
                }
                else if (root.ValueKind == JsonValueKind.String)
                {
                    var v = root.GetString();
                    if (!string.IsNullOrWhiteSpace(v)) return StripBearer(v!);
                }
            }
            catch (JsonException)
            {
                // Not JSON — fall through to the header channels. The body is NEVER logged, so this
                // catch stays silent by design (a total failure still throws a clear error above).
            }
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
/// Delegating handler that stamps <c>Authorization: Bearer &lt;jwt&gt;</c> on every outgoing data
/// request and, on a <c>401</c>, re-mints once and replays the request a single time (design §4.3).
///
/// <para>Registered <b>INNER of the retry policy</b> (so it re-stamps on every retry attempt) and
/// <b>OUTER of the throttle</b> — the repo standard <b>retry (OUTER) → auth → throttle (INNER)</b>.
/// <c>401</c> is excluded from the Polly policy precisely so this handler owns it. A <b>second</b>
/// consecutive <c>401</c> propagates (a genuine credential failure, not an expiry) and the reader
/// turns it into a thrown unit failure.</para>
///
/// <para><b>Replay is trivially safe:</b> both NGI data calls are <c>GET</c>s with no request body.
/// The token is never logged.</para>
/// </summary>
public sealed class NgiTokenAuthHandler : DelegatingHandler
{
    private readonly INgiTokenProvider _provider;

    public NgiTokenAuthHandler(INgiTokenProvider provider) => _provider = provider;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await _provider.GetTokenAsync(cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
            return response;

        // Expiry / stale token → re-mint once and replay (a body-less GET makes replay safe).
        response.Dispose();
        var fresh = await _provider.RefreshTokenAsync(token, cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fresh);
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
