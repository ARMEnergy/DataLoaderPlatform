using System.Net.Http.Json;
using System.Text.Json;
using DataLoader.Core.Sources;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.Vulcan;

/// <summary>
/// Reads one Vulcan table (one work unit) by POSTing its SQL query to
/// query_datalinks. Generic over the row model; the same code serves all five
/// tables. The Access-Key header and base address are configured on the injected
/// HttpClient (named client "Vulcan") in <see cref="VulcanModule"/>.
/// </summary>
public sealed class VulcanQuerySourceReader<TRow> : HttpJsonSourceReaderBase<VulcanWorkUnit, TRow>
{
    private readonly VulcanSettings _settings;

    public VulcanQuerySourceReader(HttpClient httpClient, IOptions<VulcanSettings> settings, ILogger logger)
        : base(httpClient, logger)
    {
        _settings = settings.Value;
    }

    // Base class is GET-oriented; Vulcan needs a POST body, so BuildRequestUri is unused.
    protected override Uri BuildRequestUri(VulcanWorkUnit unit) =>
        new(_settings.QueryEndpoint, UriKind.Relative);

    public override async Task<IReadOnlyList<TRow>> ReadAsync(VulcanWorkUnit unit, CancellationToken cancellationToken)
    {
        Logger.LogInformation("POST {Endpoint} for {Table}", _settings.QueryEndpoint, unit.TableId);
        using var request = new HttpRequestMessage(HttpMethod.Post, _settings.QueryEndpoint)
        {
            Content = JsonContent.Create(new { query = unit.Query })
        };
        using var response = await HttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await DeserializeAsync(response, cancellationToken).ConfigureAwait(false);
    }

    protected override async Task<IReadOnlyList<TRow>> DeserializeAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return ParseRows(json);
    }

    /// <summary>
    /// Parses the response body. Assumes { "data": [ {row}, ... ] }; also tolerates a
    /// bare top-level array. See spec §11.1 — confirm against a live sample.
    /// </summary>
    internal static IReadOnlyList<TRow> ParseRows(string json)
    {
        using var doc = JsonDocument.Parse(json);
        JsonElement arrayElement;
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
            arrayElement = doc.RootElement;
        else if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            arrayElement = data;
        else
            return Array.Empty<TRow>();

        var rows = JsonSerializer.Deserialize<List<TRow>>(arrayElement.GetRawText(), DefaultJsonOptions);
        return rows ?? new List<TRow>();
    }
}
