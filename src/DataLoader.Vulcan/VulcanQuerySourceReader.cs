using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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

    private static readonly JsonSerializerOptions RowJsonOptions = BuildRowJsonOptions();

    private static JsonSerializerOptions BuildRowJsonOptions()
    {
        var options = new JsonSerializerOptions(DefaultJsonOptions)
        {
            NumberHandling = JsonNumberHandling.AllowReadingFromString
        };
        // The API is inconsistent about scalar typing (e.g. plant_id arrives as a
        // quoted string on some tables, a bare number on others). Coerce any scalar
        // into string properties instead of failing.
        options.Converters.Add(new FlexibleStringConverter());
        // Date fields mix ISO, US M/d/yyyy, and non-date sentinels ("TBD", "none").
        // Parse leniently to null instead of throwing on an odd value.
        options.Converters.Add(new FlexibleDateConverter());
        return options;
    }

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
        if (_settings.PageSize <= 0)
            throw new InvalidOperationException($"Vulcan PageSize must be >= 1 but was {_settings.PageSize}.");
        if (_settings.MaxPages <= 0)
            throw new InvalidOperationException($"Vulcan MaxPages must be >= 1 but was {_settings.MaxPages}.");

        var offset = 0;
        var page = 0;
        var all = new List<TRow>();

        // cancellationToken is the per-work-unit budget covering ALL pages of this table
        // (sized via Loaders:Vulcan:WorkUnitTimeoutSeconds). Each page's own HTTP
        // timeout/retry applies per SendAsync (Loaders:Vulcan:HttpTimeoutSeconds).
        while (true)
        {
            if (page >= _settings.MaxPages)
                throw new InvalidOperationException(
                    $"Vulcan {unit.TableId}: exceeded MaxPages={_settings.MaxPages} at offset={offset}; aborting to avoid infinite paging.");

            var sql = VulcanQueryBuilder.BuildPage(unit.Spec, unit.Watermark, offset, _settings.PageSize);
            var rows = await PostQueryAsync(sql, cancellationToken).ConfigureAwait(false);

            Logger.LogInformation("[{Table}] page {Page} offset {Offset}: {Count} rows",
                unit.TableId, page, offset, rows.Count);
            all.AddRange(rows);

            if (rows.Count < _settings.PageSize)
                break; // short/empty final page

            offset += _settings.PageSize; // advance by counter, never by rows.Count
            page++;
        }

        Logger.LogInformation("[{Table}] read complete: {Total} rows across {Pages} page(s)",
            unit.TableId, all.Count, page + 1);
        return all;
    }

    /// <summary>
    /// POSTs one SQL query string to query_datalinks (one SendAsync = one timeout/retry scope)
    /// and returns the parsed rows.
    /// </summary>
    private async Task<IReadOnlyList<TRow>> PostQueryAsync(string sql, CancellationToken cancellationToken)
    {
        Logger.LogDebug("POST {Endpoint}", _settings.QueryEndpoint);
        using var request = new HttpRequestMessage(HttpMethod.Post, _settings.QueryEndpoint)
        {
            Content = JsonContent.Create(new { query = sql })
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
    /// Parses the response body. query_datalinks streams NDJSON (one JSON object per
    /// line, no wrapper); this also tolerates a { "data": [ {row}, ... ] } envelope and
    /// a bare top-level array. Reads every top-level JSON value and flattens to rows.
    /// Errors arrive as non-2xx responses (already gated upstream by EnsureSuccessStatusCode),
    /// so any 2xx body reaching here is data; a lone top-level object is a single NDJSON row.
    /// An empty/whitespace body yields zero rows.
    /// </summary>
    internal static IReadOnlyList<TRow> ParseRows(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<TRow>();

        var bytes = Encoding.UTF8.GetBytes(json);
        var documents = new List<JsonDocument>();
        try
        {
            // .NET 8's Utf8JsonReader.Read() refuses to advance past one top-level
            // value, so read one value per reader and step forward by BytesConsumed.
            var options = new JsonReaderOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            };
            var offset = 0;
            while (offset < bytes.Length)
            {
                while (offset < bytes.Length && IsJsonWhitespace(bytes[offset]))
                    offset++;
                if (offset >= bytes.Length)
                    break;

                var reader = new Utf8JsonReader(bytes.AsSpan(offset), options);
                documents.Add(JsonDocument.ParseValue(ref reader));
                offset += (int)reader.BytesConsumed;
            }

            var rows = new List<TRow>();
            foreach (var doc in documents)
            {
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Array)
                    AddRange(rows, root);
                else if (root.ValueKind == JsonValueKind.Object &&
                         root.TryGetProperty("data", out var data) &&
                         data.ValueKind == JsonValueKind.Array)
                    AddRange(rows, data);
                else if (root.ValueKind == JsonValueKind.Object)
                {
                    // NDJSON row.
                    var row = root.Deserialize<TRow>(RowJsonOptions);
                    if (row is not null)
                        rows.Add(row);
                }
            }
            return rows;
        }
        finally
        {
            foreach (var doc in documents)
                doc.Dispose();
        }
    }

    private static void AddRange(List<TRow> rows, JsonElement array)
    {
        var list = array.Deserialize<List<TRow>>(RowJsonOptions);
        if (list is not null)
            rows.AddRange(list);
    }

    // JSON insignificant whitespace: space, tab, LF, CR (RFC 8259 §2).
    private static bool IsJsonWhitespace(byte b) => b is 0x20 or 0x09 or 0x0A or 0x0D;
}
