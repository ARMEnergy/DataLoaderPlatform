using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.ICE;

/// <summary>
/// Supplies the SSO token every download must present as <c>Cookie: iceSsoCookie=…</c>.
/// </summary>
public interface IIceAuthenticator
{
    /// <summary>The current token, signing on first if there isn't one.</summary>
    Task<string> GetTokenAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Report that <paramref name="staleToken"/> was rejected, and get a fresh one.
    ///
    /// <para>
    /// Takes the stale value rather than being a bare "refresh" so this is a
    /// compare-and-swap: 18 pipelines that hit expiry at the same moment all pass
    /// the SAME stale token, the first one re-authenticates, and the other 17 get
    /// the new token back without a second sign-on.
    /// </para>
    /// </summary>
    Task<string> RefreshAsync(string staleToken, CancellationToken cancellationToken);
}

/// <summary>
/// Two-step ICE sign-on (<c>docs/apis/ICE.md</c> 1).
///
/// <para>
/// <c>POST https://sso.theice.com/api/authenticateTfa</c> returns a token that
/// embeds its own issue timestamp and is therefore short-lived. ICE publishes no
/// TTL, so the loader never tries to pre-empt expiry on a timer: it detects the
/// login page coming back instead of a file (<see cref="IceResponseClassifier"/>)
/// and refreshes then. That makes the actual lifetime operationally irrelevant.
/// </para>
/// <para>
/// The SSO <c>Set-Cookie</c> is scoped to <c>.sso.theice.com</c> and is NOT sent to
/// <c>downloads.ice.com</c> automatically — different registrable domains
/// (<c>ice.com</c> vs <c>theice.com</c>). The reader sets the header explicitly.
/// </para>
/// <para>
/// <b>The token and password are secrets.</b> Neither is ever logged, and the token
/// never reaches <c>arm.FileLog.RequestPath</c>.
/// </para>
/// </summary>
public sealed class IceSsoAuthenticator : IIceAuthenticator, IDisposable
{
    private readonly HttpClient _http;
    private readonly IOptions<IceSettings> _options;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string? _token;

    public IceSsoAuthenticator(HttpClient http, IOptions<IceSettings> options, ILogger<IceSsoAuthenticator> logger)
    {
        _http = http;
        _options = options;
        _logger = logger;
    }

    public async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        var current = Volatile.Read(ref _token);
        if (!string.IsNullOrEmpty(current)) return current!;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Re-check inside the gate: another caller may have signed on while we waited.
            if (!string.IsNullOrEmpty(_token)) return _token!;

            _token = await AuthenticateAsync(cancellationToken).ConfigureAwait(false);
            return _token;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> RefreshAsync(string staleToken, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Someone already replaced it — the caller's token is simply out of date.
            if (!string.IsNullOrEmpty(_token) && !string.Equals(_token, staleToken, StringComparison.Ordinal))
                return _token!;

            _logger.LogInformation("ICE: SSO token rejected — re-authenticating");
            _token = await AuthenticateAsync(cancellationToken).ConfigureAwait(false);
            return _token;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string> AuthenticateAsync(CancellationToken cancellationToken)
    {
        var settings = _options.Value;

        if (string.IsNullOrWhiteSpace(settings.UserId) ||
            string.Equals(settings.UserId, "SEE_DB", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "ICE UserId is unresolved (still 'SEE_DB'). Add core.Param rows for " +
                "LoaderName='ICE', ParamName='UserId'/'Password', or set " +
                "DATALOADER_Loaders__ICE__UserId / __Password.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, settings.SsoUrl)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    userId = settings.UserId,
                    password = settings.Password,
                    appKey = settings.AppKey
                }),
                Encoding.UTF8,
                "application/json")
        };

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

        // Deliberately does NOT include the body in the message: a failed sign-on
        // response can echo request context, and this string reaches the log.
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"ICE SSO sign-on failed with HTTP {(int)response.StatusCode} {response.ReasonPhrase}.");

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var token = ExtractToken(json);
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException(
                "ICE SSO sign-on returned HTTP 200 but no token was found at result.data.token.");

        // Length only — never the value.
        _logger.LogInformation("ICE: SSO sign-on succeeded for user {User} (token length {Length})",
            settings.UserId, token!.Length);

        return token;
    }

    /// <summary>
    /// Pull the token out of the sign-on response.
    ///
    /// <para>
    /// The documented location is <c>result.data.token</c> — a SIBLING of
    /// <c>attributes</c> (which is an empty object), not nested inside it. The
    /// recursive fallback exists so a future reshuffle of the envelope degrades to
    /// "still works" rather than "every download fails".
    /// </para>
    /// </summary>
    internal static string? ExtractToken(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("result", out var result) &&
            result.TryGetProperty("data", out var data) &&
            data.TryGetProperty("token", out var token) &&
            token.ValueKind == JsonValueKind.String)
        {
            return token.GetString();
        }

        return FindTokenRecursive(root, depth: 0);
    }

    private static string? FindTokenRecursive(JsonElement element, int depth)
    {
        if (depth > 8) return null;

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (string.Equals(property.Name, "token", StringComparison.OrdinalIgnoreCase) &&
                        property.Value.ValueKind == JsonValueKind.String)
                    {
                        var value = property.Value.GetString();
                        if (!string.IsNullOrWhiteSpace(value)) return value;
                    }
                }

                foreach (var property in element.EnumerateObject())
                {
                    var found = FindTokenRecursive(property.Value, depth + 1);
                    if (found is not null) return found;
                }
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    var found = FindTokenRecursive(item, depth + 1);
                    if (found is not null) return found;
                }
                break;
        }

        return null;
    }

    public void Dispose() => _gate.Dispose();
}
