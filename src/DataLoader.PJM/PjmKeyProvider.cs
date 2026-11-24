using System.Text.Json;
using DataLoader.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace DataLoader.PJM;

/// <summary>Which source supplied the key in force (design §5.1). The NAME is logged; the value never is.</summary>
public enum PjmKeySource
{
    EnvOverride,
    ParamOverride,
    PublicSettings
}

/// <summary>
/// A key and its version. 🔒 <see cref="ToString"/> is overridden so a stray interpolation or
/// structured-log capture of this value can never print the key.
/// </summary>
public readonly record struct PjmKey(string Value, int Version)
{
    public override string ToString() => $"PjmKey(v{Version}, ***)";
}

/// <summary>Key acquisition and the single guarded 401 re-fetch (design §5).</summary>
public interface IPjmKeyProvider
{
    /// <summary>Acquires the key once per run: env override → core.Param override → settings.json.</summary>
    /// <exception cref="PjmKeyAcquisitionException">No key could be acquired; the run must fail before any unit.</exception>
    Task InitializeAsync(CancellationToken cancellationToken);

    /// <summary>The key in force. Throws if <see cref="InitializeAsync"/> has not succeeded.</summary>
    PjmKey Current { get; }

    /// <summary>Which source won. Throws if not initialized.</summary>
    PjmKeySource Source { get; }

    /// <summary>True when an ARM-owned override is in force (a 401 then trips at once; no re-fetch).</summary>
    bool IsOverride { get; }

    /// <summary>
    /// Re-fetches settings.json after a 401 seen on key version <paramref name="seenVersion"/>.
    /// Serialized: only the first caller still on that version fetches; concurrent callers reuse
    /// its result. Returns <c>null</c> when no refresh is permitted (override in force, or the one
    /// refresh per run already spent) — the caller must then trip the run.
    /// </summary>
    Task<PjmKey?> RefreshAsync(int seenVersion, CancellationToken cancellationToken);

    /// <summary>Every key value this run has held (current and pre-refresh), for redaction only.</summary>
    IReadOnlyList<string> KnownKeys { get; }
}

/// <summary>
/// Default <see cref="IPjmKeyProvider"/> (design §5). Singleton.
///
/// <para>🔒 <b>Secret hygiene (design §5.4).</b> The key and the settings.json body are never
/// logged, quoted or put in an exception. A JSON parse failure is reported by exception TYPE
/// name only and is NOT wrapped as an inner exception, because a <see cref="JsonException"/>
/// message can carry a fragment of the body.</para>
///
/// <para><b>Why the store is called directly rather than through SEE_DB (design §5.2):</b> the
/// SEE_DB resolver throws on a missing row, so "absent means use the public key" cannot be
/// expressed through the sentinel. <see cref="ISeeDbParamStore.GetParam"/> returns <c>null</c> for
/// no row, and a store FAILURE fails the run — an unreadable override must not silently switch
/// the loader onto a different identity and quota tier.</para>
/// </summary>
public sealed class PjmKeyProvider : IPjmKeyProvider
{
    public const string ParamLoaderName = "PJM";
    public const string ParamName = "ApiKey";
    public const int MaxRefreshesPerRun = 1;

    private readonly PjmSettings _settings;
    private readonly HttpClient _settingsClient;
    private readonly ISeeDbParamStore _store;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _stateGate = new();
    private readonly List<string> _knownKeys = new();

    private PjmKey? _current;
    private PjmKeySource? _source;
    private int _refreshes;

    public PjmKeyProvider(PjmSettings settings, HttpClient settingsClient, ISeeDbParamStore store, ILogger logger)
    {
        _settings = settings;
        _settingsClient = settingsClient;
        _store = store;
        _logger = logger;
    }

    public PjmKey Current
    {
        get { lock (_stateGate) return _current ?? throw new InvalidOperationException("PJM key provider has not been initialized"); }
    }

    public PjmKeySource Source
    {
        get { lock (_stateGate) return _source ?? throw new InvalidOperationException("PJM key provider has not been initialized"); }
    }

    public bool IsOverride
    {
        get { lock (_stateGate) return _source is PjmKeySource.EnvOverride or PjmKeySource.ParamOverride; }
    }

    public IReadOnlyList<string> KnownKeys
    {
        get { lock (_stateGate) return _knownKeys.ToArray(); }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        // 1. Environment override — non-blank and not the literal SEE_DB.
        var env = _settings.ApiKeyOverride;
        if (!string.IsNullOrWhiteSpace(env) && !string.Equals(env.Trim(), "SEE_DB", StringComparison.Ordinal))
        {
            SetKey(env.Trim(), PjmKeySource.EnvOverride);
            return;
        }

        // 2. core.Param override — null/blank means "no override"; a FAILURE fails the run.
        string? param;
        try
        {
            param = _store.GetParam(ParamLoaderName, ParamName);
        }
        catch (Exception ex)
        {
            throw new PjmKeyAcquisitionException(
                $"PJM could not read the optional key override core.Param({ParamLoaderName}, {ParamName}) " +
                $"({ex.GetType().Name}: {PjmRedaction.Clean(ex.Message, Array.Empty<string>())}); " +
                "refusing to fall back to the public Data Miner 2 key, because an unreadable override must not silently switch identity");
        }

        if (!string.IsNullOrWhiteSpace(param))
        {
            SetKey(param.Trim(), PjmKeySource.ParamOverride);
            return;
        }

        // 3. The public Data Miner 2 key from settings.json.
        var fetched = await FetchPublicKeyAsync(cancellationToken).ConfigureAwait(false);
        SetKey(fetched, PjmKeySource.PublicSettings);
    }

    public async Task<PjmKey?> RefreshAsync(int seenVersion, CancellationToken cancellationToken)
    {
        if (IsOverride) return null;

        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_stateGate)
            {
                if (_current is { } cur && cur.Version != seenVersion)
                    return cur; // another unit already refreshed: reuse its result
                if (_refreshes >= MaxRefreshesPerRun)
                    return null;
                _refreshes++;
            }

            _logger.LogWarning("PJM refused the subscription key (HTTP 401); re-fetching the public key from {SettingsUrl} once",
                _settings.SettingsUrl);

            var fetched = await FetchPublicKeyAsync(cancellationToken).ConfigureAwait(false);
            lock (_stateGate)
            {
                var next = new PjmKey(fetched, (_current?.Version ?? 0) + 1);
                _current = next;
                if (!_knownKeys.Contains(fetched)) _knownKeys.Add(fetched);
                return next;
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private void SetKey(string key, PjmKeySource source)
    {
        lock (_stateGate)
        {
            _current = new PjmKey(key, 1);
            _source = source;
            if (!_knownKeys.Contains(key)) _knownKeys.Add(key);
        }
        _logger.LogInformation("PJM subscription key acquired from {KeySource}", source);
    }

    /// <summary>GET settings.json and read <c>subscriptionKey</c> (case-sensitive). 🔒 The body is never logged or quoted.</summary>
    private async Task<string> FetchPublicKeyAsync(CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(_settings.SettingsUrl, UriKind.Absolute, out var uri))
            throw new PjmKeyAcquisitionException("Loaders:PJM:SettingsUrl is not an absolute URL");

        int status;
        string body;
        try
        {
            using var response = await _settingsClient.GetAsync(uri, cancellationToken).ConfigureAwait(false);
            status = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
                throw new PjmKeyAcquisitionException(
                    $"PJM could not fetch the public Data Miner 2 key: {_settings.SettingsUrl} returned HTTP {status}");
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PjmKeyAcquisitionException) { throw; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // Transport failures and the per-attempt timeout; the message names the type only.
            throw new PjmKeyAcquisitionException(
                $"PJM could not fetch the public Data Miner 2 key from {_settings.SettingsUrl} ({ex.GetType().Name})");
        }

        string? key = null;
        string? advertisedBaseUrl = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (doc.RootElement.TryGetProperty("subscriptionKey", out var k) && k.ValueKind == JsonValueKind.String)
                    key = k.GetString();
                if (doc.RootElement.TryGetProperty("baseUrl", out var b) && b.ValueKind == JsonValueKind.String)
                    advertisedBaseUrl = b.GetString();
            }
        }
        catch (JsonException ex)
        {
            // ⚠ NOT wrapped: a JsonException message can quote part of the body (which holds the key).
            throw new PjmKeyAcquisitionException(
                $"settings.json had no non-blank string 'subscriptionKey' (HTTP {status}; body was not valid JSON: {ex.GetType().Name})");
        }

        if (string.IsNullOrWhiteSpace(key))
            throw new PjmKeyAcquisitionException($"settings.json had no non-blank string 'subscriptionKey' (HTTP {status})");

        key = key.Trim();

        if (!string.IsNullOrWhiteSpace(advertisedBaseUrl) &&
            !string.Equals(advertisedBaseUrl.Trim().TrimEnd('/'), _settings.BaseUrl.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
        {
            // Both URLs are public; the advertised one is still scrubbed of the key, defensively.
            _logger.LogWarning(
                "PJM settings.json advertises baseUrl {AdvertisedBaseUrl}, which differs from the configured {ConfiguredBaseUrl}; the configured URL is used",
                PjmRedaction.Clean(advertisedBaseUrl, new[] { key }), _settings.BaseUrl);
        }

        return key;
    }
}
