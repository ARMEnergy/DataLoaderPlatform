using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Polly.Timeout;

namespace DataLoader.MISO;

/// <summary>The envelope's <c>page</c> object (API doc §2).</summary>
public sealed record MisoPageInfo(int PageNumber, int PageSize, int TotalElements, int TotalPages, bool LastPage);

/// <summary>How one page request ended, after the status matrix (design §5).</summary>
public enum MisoPageOutcome
{
    /// <summary>HTTP 200 with an envelope.</summary>
    Data,

    /// <summary>400 "data not available yet" / "Init date must be before today": not published yet.</summary>
    NotYet,

    /// <summary>400 "beyond the allowed lookback period": out of retention.</summary>
    OutOfRetention,

    /// <summary>404 "Empty data returned for date": an empty day on page 1, past-the-end on page &gt; 1.</summary>
    Empty,
}

/// <summary>One page request's result. <see cref="Reason"/> is the vendor's redacted, bounded <c>reason</c> for a non-data outcome.</summary>
public sealed record MisoPageResult(MisoPageOutcome Outcome, IReadOnlyList<JsonElement> Rows, MisoPageInfo? Page, string? Reason)
{
    public static MisoPageResult NonData(MisoPageOutcome outcome, string reason) => new(outcome, Array.Empty<JsonElement>(), null, reason);
}

/// <summary>Seam over one page request so the reader can be tested without HTTP.</summary>
public interface IMisoPageSource
{
    Task<MisoPageResult> GetPageAsync(MisoWorkUnit unit, int pageNumber, CancellationToken cancellationToken);
}

/// <summary>
/// Builds and sends one page request, attaches the key HEADER per request, and classifies the
/// response per the status matrix (design §5, API doc §4). Polly (outer) has already retried the
/// transient statuses by the time a response reaches here.
///
/// <para>🔒 The key travels ONLY in <c>Ocp-Apim-Subscription-Key</c>, set on each request (never a
/// default header), never in the URL. The only vendor text ever quoted is the <c>reason</c> /
/// <c>message</c> string, redacted and bounded to 300 chars.</para>
/// </summary>
public sealed class MisoApiClient : IMisoPageSource
{
    public const string KeyHeader = "Ocp-Apim-Subscription-Key";

    // 400 reason prefixes (API doc §4), matched ordinal-ignore-case on the trimmed reason.
    internal static readonly string[] NotYetPrefixes =
    {
        "Invalid date: data not available yet",
        "Init date must be before today",
    };

    internal const string OutOfRetentionPrefix = "Invalid date: market date beyond the allowed lookback period";
    internal const string EmptyPrefix = "Empty data returned for date";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly HttpClient _http;
    private readonly MisoSettings _settings;
    private readonly MisoRunGuard _guard;
    private readonly ILogger _logger;

    public MisoApiClient(HttpClient http, MisoSettings settings, MisoRunGuard guard, ILogger logger)
    {
        _http = http;
        _settings = settings;
        _guard = guard;
        _logger = logger;
    }

    /// <summary>Relative path + query (contains no secret: the key is a header).</summary>
    public static string BuildPathAndQuery(MisoWorkUnit unit, int pageNumber)
    {
        var feed = unit.Feed;
        var query = new List<string>();
        if (!string.IsNullOrEmpty(feed.FixedQuery)) query.Add(feed.FixedQuery);
        if (unit.InitDate is { } init) query.Add("init=" + MisoTime.Format(init));
        query.Add("pageNumber=" + pageNumber.ToString(Inv));
        return $"/{feed.PathPrefix}/{MisoTime.Format(unit.MarketDate)}/{feed.PathSuffix}?{string.Join("&", query)}";
    }

    public Uri BuildUri(MisoWorkUnit unit, int pageNumber) =>
        new($"{_settings.BaseUrl.TrimEnd('/')}{BuildPathAndQuery(unit, pageNumber)}", UriKind.Absolute);

    public async Task<MisoPageResult> GetPageAsync(MisoWorkUnit unit, int pageNumber, CancellationToken cancellationToken)
    {
        _guard.ThrowIfTripped();

        var where = $"MISO {unit.Feed.Id} {unit.Label} page {pageNumber.ToString(Inv)}";
        _logger.LogDebug("[MISO {Feed}] {Unit} GET {PathAndQuery}", unit.Feed.Id, unit.Label, BuildPathAndQuery(unit, pageNumber));

        using var request = new HttpRequestMessage(HttpMethod.Get, BuildUri(unit, pageNumber));
        request.Headers.TryAddWithoutValidation(KeyHeader, _settings.ApiKey);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutRejectedException ex)
        {
            throw new MisoRequestException(null,
                $"{where}: no response within {_settings.HttpTimeoutSeconds}s on any of {_settings.RetryCount + 1} attempt(s)", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new MisoRequestException(null,
                $"{where}: transport failure after {_settings.RetryCount + 1} attempt(s) ({ex.GetType().Name}: {Clean(ex.Message)})");
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            var body = await SafeReadAsync(response, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.OK)
                return ParseEnvelope(body, where);

            var reason = ExtractReason(body);

            // 🔒 Redirects are never followed (the key header would go with them).
            if (status is >= 300 and <= 399)
                throw new MisoContractException($"{where}: HTTP {status} redirect; redirects are never followed because the key header would be forwarded", status);

            if (status is 401 or 403)
                Trip($"MISO refused the subscription key (HTTP {status}){(reason.Length > 0 ? ": " + reason : string.Empty)}; " +
                     "check core.Param(MISO, ApiKey) and the key's LGI subscription");

            if (status == 400)
            {
                if (NotYetPrefixes.Any(p => reason.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                    return MisoPageResult.NonData(MisoPageOutcome.NotYet, reason);
                if (reason.StartsWith(OutOfRetentionPrefix, StringComparison.OrdinalIgnoreCase))
                    return MisoPageResult.NonData(MisoPageOutcome.OutOfRetention, reason);
                throw new MisoRequestException(400, $"{where}: MISO rejected the request (HTTP 400): {(reason.Length > 0 ? reason : "no reason given")}");
            }

            if (status == 404)
            {
                if (reason.StartsWith(EmptyPrefix, StringComparison.OrdinalIgnoreCase))
                    return MisoPageResult.NonData(MisoPageOutcome.Empty, reason);
                throw new MisoRequestException(404, $"{where}: HTTP 404 without the documented empty-data reason; the loader may have built a bad URL{(reason.Length > 0 ? ": " + reason : string.Empty)}");
            }

            if (MisoHttpPolicy.IsRetryableStatus(response.StatusCode))
                throw new MisoRequestException(status, $"{where}: HTTP {status} after {_settings.RetryCount} retr(ies){(reason.Length > 0 ? ": " + reason : string.Empty)}");

            throw new MisoRequestException(status, $"{where}: unexpected HTTP {status}{(reason.Length > 0 ? ": " + reason : string.Empty)}");
        }
    }

    [DoesNotReturn]
    private void Trip(string reason)
    {
        _guard.Trip(reason);
        throw new MisoRunHaltedException(_guard.Reason ?? reason);
    }

    private string Clean(string? text) => MisoRedaction.Clean(text, _settings.ApiKey);

    private static async Task<string> SafeReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try { return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return string.Empty; }
    }

    /// <summary>
    /// The refusal text only: <c>reason</c> (<c>{"status","reason"}</c>) or <c>message</c> (APIM's
    /// <c>{"statusCode","message"}</c>), redacted and ≤ 300 chars. Empty when the body is not that shape —
    /// the raw body is never quoted.
    /// </summary>
    internal string ExtractReason(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return string.Empty;
            foreach (var name in new[] { "reason", "message" })
                if (MisoJson.TryGet(doc.RootElement, name, out var v) && v.ValueKind == JsonValueKind.String)
                    return Clean(v.GetString()?.Trim());
        }
        catch (JsonException) { }
        return string.Empty;
    }

    /// <summary>Envelope parse: <c>data</c> must be an array, <c>page</c> an object with integer fields and a boolean <c>lastPage</c>.</summary>
    internal static MisoPageResult ParseEnvelope(string json, string where)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { throw new MisoContractException($"{where}: response body was not JSON"); }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new MisoContractException($"{where}: response body was not a JSON object");
            if (!MisoJson.TryGet(root, "data", out var data) || data.ValueKind != JsonValueKind.Array)
                throw new MisoContractException($"{where}: envelope has no 'data' array");
            if (!MisoJson.TryGet(root, "page", out var page) || page.ValueKind != JsonValueKind.Object)
                throw new MisoContractException($"{where}: envelope has no 'page' object");

            var info = new MisoPageInfo(
                Int(page, "pageNumber", where), Int(page, "pageSize", where), Int(page, "totalElements", where),
                Int(page, "totalPages", where), Bool(page, "lastPage", where));

            var rows = new List<JsonElement>(data.GetArrayLength());
            foreach (var item in data.EnumerateArray()) rows.Add(item.Clone());
            return new MisoPageResult(MisoPageOutcome.Data, rows, info, null);
        }
    }

    private static int Int(JsonElement page, string name, string where) =>
        MisoJson.TryGet(page, name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) && n >= 0
            ? n
            : throw new MisoContractException($"{where}: envelope page.{name} is missing or not a non-negative integer");

    private static bool Bool(JsonElement page, string name, string where) =>
        MisoJson.TryGet(page, name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean()
            : throw new MisoContractException($"{where}: envelope page.{name} is missing or not a boolean");
}

/// <summary>Case-insensitive JSON property lookup.</summary>
internal static class MisoJson
{
    public static bool TryGet(JsonElement obj, string name, out JsonElement value)
    {
        if (obj.ValueKind == JsonValueKind.Object)
        {
            if (obj.TryGetProperty(name, out value)) return true;
            foreach (var p in obj.EnumerateObject())
            {
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = p.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }
}
