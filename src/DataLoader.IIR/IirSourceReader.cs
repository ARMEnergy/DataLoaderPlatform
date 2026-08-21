using System.Net;
using System.Text.Json;
using DataLoader.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace DataLoader.IIR;

/// <summary>
/// The single shared HTTP plumbing for the MANDATORY two-step summary→detail pull for every IIR
/// endpoint (design §4). Implements <see cref="ISourceReader{TUnit,TRow}"/> directly (NOT
/// <c>HttpJsonSourceReaderBase</c>, which <c>EnsureSuccessStatusCode()</c> + single-shot deserializes
/// and can neither tolerate 404/no-data nor page/batch). Constructed per endpoint with the descriptor,
/// the per-endpoint fact row factory and a per-endpoint id-catalog census sink.
///
/// <para>One work unit runs the whole two-step pull:</para>
/// <list type="number">
///   <item><b>STEP 1 — discover.</b> Page <c>POST {SummaryPath}?{country filters}&amp;limit&amp;offset</c>
///     (empty body); for each summary record extract the entity id + lat/long TOLERANTLY into an
///     ordered id list + an id→(lat,long) map, and accumulate an <see cref="IirSummaryRow"/> census.
///     Write the census via the injected summary sink (its own <c>SqlWriteGate</c> proc key — design
///     §4.4) BEFORE step 2.</item>
///   <item><b>STEP 2 — enrich.</b> Batch the ids into ≤ <c>DetailBatchSize</c> (50) groups and
///     <c>POST {DetailPath}?{idParam}=id&amp;…</c> (empty body); the DETAIL records populate the fact
///     rows. Lat/long CARRY-FORWARD (§6.3): if a detail row omits the coordinates, fill them from the
///     STEP-1 map by id. <c>PlantPoint</c> is still built in-proc from whichever is non-null.</item>
///   <item>FileLog the DETAIL outcome (Success when facts &gt; 0, else NotAvailable), stamp the returned
///     <c>FileLogId</c> onto every fact row.</item>
/// </list>
/// 404 / empty → NotAvailable (no failure; a per-batch 404 skips that batch); 401 → the auth handler
/// re-mints, a persistent 401 throws (loud); 403 → throw (entitlement); 429/5xx after retries → throw
/// (unit fails, run continues). A STEP-1 or census-write failure fails the whole unit (both are
/// prerequisites for STEP 2). <see cref="OperationCanceledException"/> is rethrown WITHOUT a FileLog
/// write; any other failure writes a best-effort <c>Failed</c> row (carrying the last HTTP status)
/// then rethrows so <c>LoaderPipelineBase</c> records the LoadLog failure and continues.
/// </summary>
public sealed class IirSourceReader<TRow> : ISourceReader<IirWorkUnit, TRow>
    where TRow : class, IIirFactRow
{
    // Catalogue-realistic safety cap on paging (design §4.2): the largest pull (plants ≈112k rows @
    // 1000/page ≈ 112 pages) is well under this. Bounds paging if the API ever omits totalCount AND
    // never returns a short page; a hit is logged loudly. Primary termination stays offset >= totalCount.
    private const int MaxPagesSafety = 5000;

    private static readonly HashSet<string> ReservedEnvelopeKeys =
        new(StringComparer.OrdinalIgnoreCase) { "limit", "offset", "resultCount", "totalCount" };

    private readonly HttpClient _http;
    private readonly IirSettings _settings;
    private readonly IIirFileLog _fileLog;
    private readonly IirEndpointDescriptor _descriptor;
    private readonly Func<JsonElement, IirWorkUnit, TRow?> _rowFactory;
    private readonly ISink<IirSummaryRow> _summarySink;
    private readonly ILogger _logger;

    public IirSourceReader(
        HttpClient http,
        IirSettings settings,
        IIirFileLog fileLog,
        IirEndpointDescriptor descriptor,
        Func<JsonElement, IirWorkUnit, TRow?> rowFactory,
        ISink<IirSummaryRow> summarySink,
        ILogger logger)
    {
        _http = http;
        _settings = settings;
        _fileLog = fileLog;
        _descriptor = descriptor;
        _rowFactory = rowFactory;
        _summarySink = summarySink;
        _logger = logger;
    }

    public async Task<IReadOnlyList<TRow>> ReadAsync(IirWorkUnit unit, CancellationToken cancellationToken)
    {
        var summaryPath = _descriptor.SummaryPath.Replace("{ver}", _settings.Version);
        var detailPath = _descriptor.DetailPath.Replace("{ver}", _settings.Version);
        var requestPath = string.IsNullOrEmpty(unit.QueryString) ? summaryPath : $"{summaryPath}?{unit.QueryString}";
        var file = new IirFileContext(_descriptor.EndpointId, unit.RunDate, requestPath);

        // PageAsync reports each response's status through this sink BEFORE it may throw, so a Failed
        // FileLog row written in the catch still carries the actual HTTP status. Never logs a secret.
        int? lastHttpStatus = null;
        void ReportStatus(int? status) => lastHttpStatus = status;

        try
        {
            // ---- STEP 1 — DISCOVER (summary, paged, country-filtered) --------------------------------
            var summaryLimit = Math.Clamp(_settings.SummaryPageSize, 1, 1000);
            var ids = new List<int>();
            var seen = new HashSet<int>();
            var coords = new Dictionary<int, (double? Lat, double? Long)>();
            var census = new List<IirSummaryRow>();

            void OnSummary(JsonElement el)
            {
                var s = IirSummaryRow.From(el, _descriptor.IdField, unit.RunDate);
                if (s is null) return; // keyless summary record — cannot seed the census or id list
                if (seen.Add(s.EntityId)) ids.Add(s.EntityId);
                coords[s.EntityId] = (s.Latitude, s.Longitude); // last wins
                census.Add(s);
            }

            await PageAsync(summaryPath, unit.QueryString, summaryLimit, OnSummary, ReportStatus, cancellationToken).ConfigureAwait(false);

            // Write the id-catalog census side-write BEFORE step 2 — a distinct SqlWriteGate proc key
            // from the fact merge and arm.usp_UpsertFileLog (design §4.4). A failure here fails the unit.
            if (census.Count > 0)
                await _summarySink.WriteAsync(census, cancellationToken).ConfigureAwait(false);

            // Release the census rows now they are persisted so the ~112k plant summary objects can be
            // GC'd during the STEP-2 detail pull; coords (carry-forward), ids and seen remain.
            census.Clear();
            census.TrimExcess();

            // ---- STEP 2 — ENRICH (detail, batched ≤ DetailBatchSize) --------------------------------
            var facts = new List<TRow>(ids.Count);
            var detailDrops = 0;

            void OnDetail(JsonElement el)
            {
                var row = _rowFactory(el, unit);
                if (row is null) { detailDrops++; return; } // keyless/unparseable detail record

                // LAT/LONG CARRY-FORWARD (§6.3): fill from the STEP-1 summary when detail omits it.
                if (coords.TryGetValue(row.EntityId, out var c))
                {
                    row.Latitude ??= c.Lat;
                    row.Longitude ??= c.Long;
                }
                facts.Add(row);
            }

            var batchSize = Math.Clamp(_settings.DetailBatchSize, 1, 50);
            for (var i = 0; i < ids.Count; i += batchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batch = ids.GetRange(i, Math.Min(batchSize, ids.Count - i));
                var query = string.Join("&", batch.Select(id => $"{_descriptor.IdParam}={id}"));
                await PageAsync(detailPath, query, batch.Count, OnDetail, ReportStatus, cancellationToken).ConfigureAwait(false);
            }

            if (detailDrops > 0)
                _logger.LogWarning("[IIR {Endpoint}] {Path}: dropped {Count} keyless/unparseable detail record(s)",
                    _descriptor.EndpointId, detailPath, detailDrops);

            // ---- FileLog the DETAIL outcome; stamp FileLogId onto every fact row (§4.5/§8) ----------
            var status = facts.Count == 0 ? "NotAvailable" : "Success";
            var fileLogId = await _fileLog.UpsertAsync(file, status, lastHttpStatus, requestPath, facts.Count, cancellationToken).ConfigureAwait(false);

            if (facts.Count == 0)
                return Array.Empty<TRow>();

            foreach (var r in facts) r.FileLogId = fileLogId;

            _logger.LogDebug("[IIR {Endpoint}] {Path} → {Ids} id(s), {Rows} fact row(s) (FileLog #{Id})",
                _descriptor.EndpointId, requestPath, ids.Count, facts.Count, fileLogId);
            return facts;
        }
        catch (OperationCanceledException)
        {
            throw; // LoadLog already records cancellation; do not write a hub row on a spent token.
        }
        catch (Exception)
        {
            await TryUpsertFailedAsync(file, requestPath, lastHttpStatus).ConfigureAwait(false);
            throw; // LoaderPipelineBase logs + records the LoadLog failure and moves on.
        }
    }

    /// <summary>
    /// Pages <c>POST {relativePath}?{query}&amp;limit=&amp;offset=</c> (empty body), invoking
    /// <paramref name="onElement"/> for each data element of each page (the element is read inline while
    /// its <see cref="JsonDocument"/> is alive — never retained, so no clone is needed). Reports each
    /// response's HTTP status via <paramref name="reportStatus"/> BEFORE any throw, so a Failed FileLog
    /// row can carry it. Stops on <c>offset ≥ totalCount</c>, a short/empty page, a 404, or the cap.
    /// </summary>
    private async Task PageAsync(string relativePath, string query, int limit, Action<JsonElement> onElement, Action<int?> reportStatus, CancellationToken cancellationToken)
    {
        var offset = 0;
        var pagesFetched = 0;
        int? totalCount = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var uri = BuildUri(relativePath, query, limit, offset);
            _logger.LogDebug("[IIR {Endpoint}] POST {Path} (offset {Offset}, limit {Limit})",
                _descriptor.EndpointId, relativePath, offset, limit); // sanitized — the Bearer header is never logged

            using var request = new HttpRequestMessage(HttpMethod.Post, uri);
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            reportStatus(status); // record before the throw paths below so the Failed FileLog row keeps it

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                _logger.LogDebug("[IIR {Endpoint}] 404 not available: {Path}", _descriptor.EndpointId, relativePath);
                break; // nothing here; STEP 1 → NotAvailable, STEP 2 → skip this batch
            }
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                // 401 (a persistent auth failure the handler could not re-mint) or 403 (entitlement) — loud.
                throw new HttpRequestException($"[{_descriptor.EndpointId}] {relativePath} returned HTTP {status} (auth/entitlement)");
            if (!response.IsSuccessStatusCode)
                // 429/5xx after Polly retries exhausted — unit fails, run continues.
                throw new HttpRequestException($"[{_descriptor.EndpointId}] {relativePath} returned HTTP {status}");

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var pageRows = ExtractPage(body, onElement, ref totalCount, relativePath);
            pagesFetched++;

            if (pageRows == 0) break;                    // empty page → done
            if (pagesFetched >= MaxPagesSafety)
            {
                _logger.LogWarning("[IIR {Endpoint}] {Path}: stopped paging at the {Max}-page safety cap",
                    _descriptor.EndpointId, relativePath, MaxPagesSafety);
                break;
            }

            offset += limit;
            if (totalCount is int tc && offset >= tc) break; // primary bound
            if (pageRows < limit) break;                     // short final page (safety net)
        }
    }

    private Uri BuildUri(string relativePath, string query, int limit, int offset)
    {
        var paging = $"limit={limit}&offset={offset}";
        var full = string.IsNullOrEmpty(query)
            ? $"{_settings.BaseUrl.TrimEnd('/')}/{relativePath.TrimStart('/')}?{paging}"
            : $"{_settings.BaseUrl.TrimEnd('/')}/{relativePath.TrimStart('/')}?{query}&{paging}";
        return new Uri(full, UriKind.Absolute);
    }

    /// <summary>
    /// Parses one page and invokes <paramref name="onElement"/> for each data element; returns the
    /// page's element count. Reads <c>totalCount</c> (case-insensitively, first non-null wins) and finds
    /// the data array TOLERANTLY: the descriptor key, else the first array-valued non-reserved property.
    /// A bare-array root is also accepted; invalid JSON is tolerated (warn + 0).
    /// </summary>
    private int ExtractPage(string body, Action<JsonElement> onElement, ref int? totalCount, string relativePath)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "[IIR {Endpoint}] {Path}: response was not valid JSON", _descriptor.EndpointId, relativePath);
            return 0;
        }

        using (doc)
        {
            var root = doc.RootElement;

            JsonElement dataArray;
            if (root.ValueKind == JsonValueKind.Array)
            {
                dataArray = root; // defensive: a bare array (no envelope) — totalCount stays unknown
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                var tc = IirParse.Int(root, "totalCount");
                if (tc.HasValue && totalCount is null) totalCount = tc;

                var found = FindDataArray(root);
                if (found is null) return 0;
                dataArray = found.Value;
            }
            else
            {
                return 0;
            }

            var count = 0;
            foreach (var el in dataArray.EnumerateArray())
            {
                onElement(el); // read inline; the element is not retained past this call
                count++;
            }
            return count;
        }
    }

    /// <summary>Tolerant data-array location (design §4.5): the descriptor key (case-insensitive), else the first array-valued non-reserved property.</summary>
    private JsonElement? FindDataArray(JsonElement root)
    {
        var byKey = IirParse.Prop(root, _descriptor.DataArrayKey);
        if (byKey is { ValueKind: JsonValueKind.Array }) return byKey;

        foreach (var p in root.EnumerateObject())
            if (p.Value.ValueKind == JsonValueKind.Array && !ReservedEnvelopeKeys.Contains(p.Name))
                return p.Value;

        return null;
    }

    private async Task TryUpsertFailedAsync(IirFileContext file, string requestPath, int? httpStatus)
    {
        try
        {
            // Best-effort; use None so the Failed row is still written even when the failure was a
            // timeout, and never mask the original exception.
            await _fileLog.UpsertAsync(file, "Failed", httpStatus, requestPath, 0, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[IIR {Endpoint}] failed to write 'Failed' FileLog for {Path}", _descriptor.EndpointId, requestPath);
        }
    }
}
