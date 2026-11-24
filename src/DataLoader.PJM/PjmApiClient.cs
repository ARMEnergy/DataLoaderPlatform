using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Polly.Timeout;

namespace DataLoader.PJM;

/// <summary>One parsed page: the items (cloned, so they outlive the document) and the query's <c>totalRows</c>.</summary>
public sealed record PjmPage(IReadOnlyList<JsonElement> Items, int TotalRows);

/// <summary>Seam over one page request, so the day reader can be tested without HTTP.</summary>
public interface IPjmPageSource
{
    Task<PjmPage> GetPageAsync(PjmDayWorkUnit unit, int startRow, CancellationToken cancellationToken);
}

/// <summary>
/// Builds and sends one data-API page request, attaches the key HEADER per request, and classifies
/// the response per the status matrix (design §4.1, §5.3, §6.1). Polly (outer) has already retried
/// the transient statuses by the time a response reaches here; the rate limiter (inner) paced every
/// attempt.
///
/// <para>🔒 The key travels ONLY in <c>Ocp-Apim-Subscription-Key</c>. It is read per request from
/// <see cref="IPjmKeyProvider.Current"/> (it can change mid-run), never set as a default header,
/// never put in the URL, and every piece of vendor text quoted into an exception goes through
/// <see cref="PjmRedaction.Clean"/> with every key this run has held.</para>
/// </summary>
public sealed class PjmApiClient : IPjmPageSource
{
    public const string KeyHeader = "Ocp-Apim-Subscription-Key";
    private const int MaxErrorMessages = 3;
    private const int MaxErrorMessageLength = 300;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly HttpClient _http;
    private readonly PjmSettings _settings;
    private readonly IPjmKeyProvider _keys;
    private readonly PjmRunGuard _guard;
    private readonly ILogger _logger;

    public PjmApiClient(HttpClient http, PjmSettings settings, IPjmKeyProvider keys, PjmRunGuard guard, ILogger logger)
    {
        _http = http;
        _settings = settings;
        _keys = keys;
        _guard = guard;
        _logger = logger;
    }

    /// <summary>Sanitized path + query for logging and tests (contains no secret: the key is a header).</summary>
    public string BuildPathAndQuery(PjmDayWorkUnit unit, int startRow)
    {
        // sort = the feed's SortField (every request carries one: the NUCORE lesson); the filter is
        // {FilterField}={FilterValue}, omitted for a snapshot (design §14.2, §14.8).
        var query =
            $"rowCount={_settings.PageSize.ToString(Inv)}" +
            $"&startRow={startRow.ToString(Inv)}" +
            $"&sort={unit.Feed.SortField}&order=Asc";
        if (unit.FilterValue is { } filter && !string.IsNullOrEmpty(unit.Feed.FilterField))
            query += $"&{unit.Feed.FilterField}={Uri.EscapeDataString(filter)}";
        if (!string.IsNullOrEmpty(unit.Feed.ExtraQuery))
            query += "&" + unit.Feed.ExtraQuery;
        return $"/{unit.Feed.Name}?{query}";
    }

    public Uri BuildUri(PjmDayWorkUnit unit, int startRow) =>
        new($"{_settings.BaseUrl.TrimEnd('/')}{BuildPathAndQuery(unit, startRow)}", UriKind.Absolute);

    public async Task<PjmPage> GetPageAsync(PjmDayWorkUnit unit, int startRow, CancellationToken cancellationToken)
    {
        var uri = BuildUri(unit, startRow);
        var resent = false;

        while (true)
        {
            _guard.ThrowIfTripped();

            var key = _keys.Current;
            _logger.LogDebug("[PJM {Feed}] {Unit} GET {PathAndQuery}", unit.Feed.Name, unit.Label, BuildPathAndQuery(unit, startRow));

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation(KeyHeader, key.Value);
            if (resent)
                request.Options.Set(PjmHttpPolicy.SingleAttempt, true); // ONE attempt, no retry cycle (design §5.3)

            HttpResponseMessage response;
            try
            {
                // The body is already buffered inside the per-attempt timeout by PjmBufferingHandler.
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (TimeoutRejectedException ex)
            {
                throw new PjmRequestException(PjmFailureClass.TransientExhausted, null,
                    $"PJM {unit.Feed.Name} {unit.Label}: no response within {_settings.HttpTimeoutSeconds}s on any of {_settings.RetryCount + 1} attempt(s)", ex);
            }
            catch (HttpRequestException ex)
            {
                throw new PjmRequestException(PjmFailureClass.TransientExhausted, null,
                    $"PJM {unit.Feed.Name} {unit.Label}: transport failure after {_settings.RetryCount + 1} attempt(s) ({ex.GetType().Name}: {Clean(ex.Message, 300)})");
            }

            using (response)
            {
                var status = (int)response.StatusCode;

                // 🔒 Redirects are never followed (the key header would go with them): contract failure, no retry.
                if (status is >= 300 and <= 399)
                {
                    var location = response.Headers.Location is { } loc
                        ? $"; Location: {Clean(loc.IsAbsoluteUri ? loc.GetLeftPart(UriPartial.Path) : loc.OriginalString.Split('?')[0], 200)}"
                        : string.Empty;
                    throw new PjmContractException(
                        $"PJM {unit.Feed.Name} {unit.Label} (startRow {startRow}): HTTP {status} redirect — redirects are never followed, " +
                        $"because the subscription key header would be forwarded{location}", status);
                }

                // A Retry-After beyond the cap is never retried early: the shared pool wants more patience than a unit has.
                if (PjmHttpPolicy.IsRetryableStatus(response.StatusCode) && PjmHttpPolicy.RetryAfterExceedsCap(response))
                {
                    var hint = PjmHttpPolicy.RetryAfterHint(response.Headers.RetryAfter) ?? TimeSpan.Zero;
                    Trip(
                        $"PJM answered HTTP {status} with Retry-After {(long)hint.TotalSeconds}s, beyond the {(long)PjmHttpPolicy.MaxRetryAfter.TotalSeconds}s cap; " +
                        "stopping the run so the shared Data Miner 2 pool can recover");
                }

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    if (_keys.IsOverride)
                    {
                        Trip(
                            "the ARM-registered PJM key (core.Param PJM/ApiKey or DATALOADER_Loaders__PJM__ApiKeyOverride) was refused (HTTP 401); " +
                            "update it, or delete the row to fall back to the public Data Miner 2 key");
                    }

                    if (resent)
                        Trip(TwiceRefusedReason());

                    PjmKey? refreshed;
                    try
                    {
                        refreshed = await _keys.RefreshAsync(key.Version, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // PjmKeyAcquisitionException messages are already key-free by construction.
                        Trip($"PJM refused the subscription key (HTTP 401) and re-fetching it failed: {Clean(ex.Message, 300)}");
                        throw; // not reached: Trip always throws (definite assignment needs this)
                    }

                    if (refreshed is null)
                        Trip(TwiceRefusedReason());

                    resent = true;
                    continue; // re-send once with the (possibly) new key: ONE attempt, paced by the limiter
                }

                if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    var body = await SafeReadAsync(response, cancellationToken).ConfigureAwait(false);
                    Trip(
                        "PJM refused the subscription key by entitlement or quota, not authentication (HTTP 403)" +
                        (string.IsNullOrWhiteSpace(body) ? string.Empty : $": {Clean(body, 300)}"));
                }

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    Trip(
                        $"PJM is still throttling the shared subscription key (HTTP 429) after {_settings.RetryCount} retr(ies); " +
                        "stopping the run so the shared Data Miner 2 pool can recover");
                }

                if (!response.IsSuccessStatusCode)
                {
                    var body = await SafeReadAsync(response, cancellationToken).ConfigureAwait(false);
                    var suffix = string.IsNullOrWhiteSpace(body) ? string.Empty : $": {Clean(body, 300)}";
                    var where = $"PJM {unit.Feed.Name} {unit.Label} (startRow {startRow})";

                    throw status switch
                    {
                        400 => new PjmRequestException(PjmFailureClass.Http400, status, $"{where}: malformed request (HTTP 400){suffix}"),
                        404 => new PjmRequestException(PjmFailureClass.Http404, status, $"{where}: feed path not found (HTTP 404) — the loader built a bad URL{suffix}"),
                        _ when PjmHttpPolicy.IsRetryableStatus(response.StatusCode) =>
                            new PjmRequestException(PjmFailureClass.TransientExhausted, status,
                                $"{where}: HTTP {status} after {_settings.RetryCount} retr(ies){suffix}"),
                        _ => new PjmRequestException(PjmFailureClass.ForStatus(status), status, $"{where}: unexpected HTTP {status}{suffix}"),
                    };
                }

                var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                return ParseEnvelope(json, unit);
            }
        }
    }

    private string TwiceRefusedReason() =>
        "PJM refused the subscription key twice (HTTP 401); the Data Miner 2 public key in " +
        $"{_settings.SettingsUrl} may have been rotated or withdrawn, or the core.Param(PJM, ApiKey) override is invalid";

    /// <summary>Trips the guard and throws the halt for the current unit.</summary>
    [DoesNotReturn]
    private void Trip(string reason)
    {
        _guard.Trip(reason);
        throw new PjmRunHaltedException(_guard.Reason ?? reason);
    }

    private string Clean(string? text, int maxLength) => PjmRedaction.Clean(text, _keys.KnownKeys, maxLength);

    private static async Task<string> SafeReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try { return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return string.Empty; }
    }

    /// <summary>
    /// Envelope parse (design §4.3, §6.1): case-insensitive property names; <c>errors</c> non-empty →
    /// throw (no retry); <c>items</c> absent/not an array → contract; <c>totalRows</c> absent → contract.
    /// The body is never quoted except the redacted <c>errors</c> messages.
    /// </summary>
    internal PjmPage ParseEnvelope(string json, PjmDayWorkUnit unit)
    {
        var where = $"PJM {unit.Feed.Name} {unit.Label}";

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException)
        {
            throw new PjmContractException($"{where}: response body was not JSON");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new PjmContractException($"{where}: response body was not a JSON object");

            JsonElement? items = null, totalRows = null, errors = null;
            foreach (var p in root.EnumerateObject())
            {
                if (string.Equals(p.Name, "items", StringComparison.OrdinalIgnoreCase)) items ??= p.Value;
                else if (string.Equals(p.Name, "totalRows", StringComparison.OrdinalIgnoreCase)) totalRows ??= p.Value;
                else if (string.Equals(p.Name, "errors", StringComparison.OrdinalIgnoreCase)) errors ??= p.Value;
            }

            if (errors is { } e && IsNonEmpty(e))
            {
                var messages = ExtractErrorMessages(e)
                    .Take(MaxErrorMessages)
                    .Select(m => Clean(m, MaxErrorMessageLength));
                throw new PjmRequestException(PjmFailureClass.Errors, 200,
                    $"{where}: PJM rejected the query in-band (HTTP 200 with errors): {string.Join(" | ", messages)}");
            }

            if (items is not { ValueKind: JsonValueKind.Array } itemArray)
                throw new PjmContractException($"{where}: envelope has no 'items' array");

            if (totalRows is not { } t)
                throw new PjmContractException($"{where}: envelope has no totalRows");

            int total;
            if (t.ValueKind == JsonValueKind.Number && t.TryGetInt32(out var n)) total = n;
            else if (t.ValueKind == JsonValueKind.String && int.TryParse(t.GetString(), NumberStyles.None, Inv, out var s)) total = s;
            else throw new PjmContractException($"{where}: envelope totalRows is not a non-negative integer");

            if (total < 0)
                throw new PjmContractException($"{where}: envelope totalRows is negative");

            var list = new List<JsonElement>(itemArray.GetArrayLength());
            foreach (var item in itemArray.EnumerateArray())
                list.Add(item.Clone());

            return new PjmPage(list, total);
        }
    }

    private static bool IsNonEmpty(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => false,
        JsonValueKind.Array => e.GetArrayLength() > 0,
        JsonValueKind.Object => e.EnumerateObject().Any(),
        JsonValueKind.String => !string.IsNullOrWhiteSpace(e.GetString()),
        JsonValueKind.False => false,
        _ => true,
    };

    private static IEnumerable<string> ExtractErrorMessages(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in e.EnumerateArray())
                    yield return OneMessage(item);
                break;
            default:
                yield return OneMessage(e);
                break;
        }
    }

    private static string OneMessage(JsonElement item)
    {
        if (item.ValueKind == JsonValueKind.String) return item.GetString() ?? string.Empty;
        if (item.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in item.EnumerateObject())
                if (string.Equals(p.Name, "message", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
                    return p.Value.GetString() ?? string.Empty;
        }
        return item.GetRawText();
    }
}
