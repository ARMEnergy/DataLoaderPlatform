using System.Text.Json;
using DataLoader.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace DataLoader.Core.Sources;

/// <summary>
/// Base class for source readers that fetch JSON from an HTTP endpoint.
///
/// Subclasses supply:
///   - <see cref="BuildRequestUri"/> — how to form the URL for one work unit
///   - <see cref="DeserializeAsync"/> — how to turn the response body into items
///
/// HttpClient is injected (so it can be configured via
/// <c>IHttpClientFactory</c> with the platform's Polly retry policy).
/// </summary>
public abstract class HttpJsonSourceReaderBase<TUnit, TItem> : ISourceReader<TUnit, TItem>
    where TUnit : WorkUnit
{
    protected readonly HttpClient HttpClient;
    protected readonly ILogger Logger;

    protected static readonly JsonSerializerOptions DefaultJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    protected HttpJsonSourceReaderBase(HttpClient httpClient, ILogger logger)
    {
        HttpClient = httpClient;
        Logger = logger;
    }

    protected abstract Uri BuildRequestUri(TUnit unit);

    /// <summary>
    /// Convert the raw response body into items. Override if the response
    /// shape doesn't deserialise directly to <c>List&lt;TItem&gt;</c>.
    /// </summary>
    protected virtual async Task<IReadOnlyList<TItem>> DeserializeAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var items = await JsonSerializer.DeserializeAsync<List<TItem>>(stream, DefaultJsonOptions, ct).ConfigureAwait(false);
        return items ?? new List<TItem>();
    }

    /// <summary>
    /// Hook for subclasses to react to the HTTP status before
    /// <c>EnsureSuccessStatusCode</c>. The Energy Aspects loader uses this
    /// to log API response codes per mapping.
    /// </summary>
    protected virtual Task OnResponseReceivedAsync(TUnit unit, HttpResponseMessage response, CancellationToken ct)
        => Task.CompletedTask;

    public virtual async Task<IReadOnlyList<TItem>> ReadAsync(TUnit unit, CancellationToken cancellationToken)
    {
        var uri = BuildRequestUri(unit);
        Logger.LogDebug("GET {Uri}", SanitiseForLog(uri));

        var response = await HttpClient.GetAsync(uri, cancellationToken).ConfigureAwait(false);
        await OnResponseReceivedAsync(unit, response, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        return await DeserializeAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Strip query string from a URI before logging so we don't leak api keys.
    /// </summary>
    protected static string SanitiseForLog(Uri uri) =>
        uri.IsAbsoluteUri
            ? uri.GetLeftPart(UriPartial.Path)
            : uri.OriginalString.Split('?')[0];
}
