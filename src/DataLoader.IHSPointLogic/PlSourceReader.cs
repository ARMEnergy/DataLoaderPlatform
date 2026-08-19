using System.Net;
using System.Text.Json;
using DataLoader.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace DataLoader.IHSPointLogic;

/// <summary>
/// The single shared HTTP + FileLog + parse + map plumbing for every IHSPointLogic endpoint
/// (design §5.2). Implements <see cref="ISourceReader{TUnit,TRow}"/> directly (NOT
/// <c>HttpJsonSourceReaderBase</c>, which calls <c>EnsureSuccessStatusCode</c> + a single-shot
/// deserialize and can neither tolerate 404/no-data nor page). Constructed per endpoint with the
/// descriptor + the per-endpoint row factory.
///
/// <para>Per work unit:</para>
/// <list type="number">
///   <item>Page over <c>?pageIndex=N</c>/<c>&amp;pageIndex=N</c> using the convention the response shape
///     dictates (see the paging note in <c>ReadAsync</c>): FLAT bare-array responses are <b>0-based</b>
///     (pages <c>0,1,2,…</c>; stop on a short/empty page); WRAPPER (<c>PagingInfo</c>/<c>Data</c>)
///     responses are <b>1-based</b> and driven off <c>PagingInfo.page_count</c> — the index-0 fetch
///     returns page 1, the remaining real pages are at indices <c>2..page_count</c>, and index 1 (a
///     duplicate of index 0) is skipped. Elements are <c>.Clone()</c>d across each page's
///     <see cref="JsonDocument"/> disposal.</item>
///   <item>Map each accumulated element via the row factory, dropping nulls (sentinel vs invalid
///     classification, CWG style).</item>
///   <item>Upsert the <c>arm.FileLog</c> hub row (Success when rows &gt; 0, else NotAvailable) and
///     stamp the returned <c>FileLogId</c> onto every row.</item>
/// </list>
/// 404 / empty → NotAvailable (no failure); 401/403 → throw (auth/entitlement, no token to refresh);
/// 429/5xx after retries → throw (unit fails, run continues). <see cref="OperationCanceledException"/>
/// is rethrown WITHOUT a FileLog write; any other failure writes a best-effort <c>Failed</c> row then
/// rethrows so <c>LoaderPipelineBase</c> records the LoadLog failure and continues (fail-a-block-not-the-run).
/// </summary>
public sealed class PlSourceReader<TRow> : ISourceReader<PlWorkUnit, TRow>
    where TRow : class, IPlFactRow
{
    private const int PageSize = 10000;      // API-fixed page size (design constant, §8)
    private const int MaxPagesSafety = 1000; // guard against a runaway pager (never hit in practice)

    private readonly HttpClient _http;
    private readonly IHSPointLogicSettings _settings;
    private readonly IPlFileLog _fileLog;
    private readonly PlEndpointDescriptor _descriptor;
    private readonly Func<JsonElement, PlWorkUnit, TRow?> _rowFactory;
    private readonly Func<JsonElement, bool>? _sentinelPredicate;
    private readonly ILogger _logger;

    /// <param name="sentinelPredicate">
    /// Optional: identifies an element the row factory intentionally drops as an EXPECTED sentinel
    /// (logged at Debug); any other dropped element is treated as a genuine parse failure (logged at
    /// Warning so data-quality regressions stay visible). Null = every drop is a genuine failure.
    /// </param>
    public PlSourceReader(
        HttpClient http,
        IHSPointLogicSettings settings,
        IPlFileLog fileLog,
        PlEndpointDescriptor descriptor,
        Func<JsonElement, PlWorkUnit, TRow?> rowFactory,
        ILogger logger,
        Func<JsonElement, bool>? sentinelPredicate = null)
    {
        _http = http;
        _settings = settings;
        _fileLog = fileLog;
        _descriptor = descriptor;
        _rowFactory = rowFactory;
        _logger = logger;
        _sentinelPredicate = sentinelPredicate;
    }

    public async Task<IReadOnlyList<TRow>> ReadAsync(PlWorkUnit unit, CancellationToken cancellationToken)
    {
        var relativePath = unit.RequestPath;
        var file = new PlFileContext(_descriptor.EndpointId, unit.ParamKey, unit.Variant, unit.RepresentativeDate, relativePath);

        int? lastHttpStatus = null;
        try
        {
            // -----------------------------------------------------------------------------------------
            // PAGING SEMANTICS — verified against the live API on 2026-08-19 (do not "simplify" back to a
            // single 0-based rule; that silently drops the last wrapper page).
            //
            //   FLAT responses   (a bare JSON array — the cs/v1/plview/retrieve/* family):
            //       pageIndex is 0-BASED. Fetch pages 0,1,2,… and stop on a short (< PageSize) or empty page.
            //
            //   WRAPPER responses ({ "PagingInfo": { page_size, page_count, total_record_count }, "Data":[…] }
            //                      — the cs/v1/pointlogic/* family):
            //       pageIndex is 1-BASED, AND pageIndex=0 returns the SAME rows as pageIndex=1 (page 1).
            //       The real pages live at indices 1..page_count. We keep the already-fetched index-0
            //       result as page 1, then fetch indices 2..page_count (skipping index 1, which duplicates
            //       index 0). Paging is driven off page_count — NOT the PageSize short-page heuristic
            //       (a wrapper's page_size need not equal PageSize). A short/empty page and MaxPagesSafety
            //       remain as belt-and-braces safety nets.
            //
            // The shape is unknown until the first fetch: ExtractPage populates WrapperPaging from
            // PagingInfo when present; PageCount == null ⇒ flat.
            // -----------------------------------------------------------------------------------------
            var elements = new List<JsonElement>();
            var pageIndex = 0;
            var pagesFetched = 0;
            var paging = default(WrapperPaging); // all null ⇒ flat (bare array); PageCount set ⇒ wrapper

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var pageUri = BuildPageUri(relativePath, pageIndex);
                _logger.LogDebug("[{Endpoint}] GET {Path} (page {Page})", _descriptor.EndpointId, relativePath, pageIndex); // sanitized — the Basic header is never logged

                using var request = new HttpRequestMessage(HttpMethod.Get, pageUri);
                using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                lastHttpStatus = (int)response.StatusCode;

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    _logger.LogDebug("[{Endpoint}] 404 not available: {Path}", _descriptor.EndpointId, relativePath);
                    break; // nothing here; if nothing accumulated → NotAvailable
                }

                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    // 401 (missing/wrong Basic header — no token to refresh) or 403 (entitlement gap) — loud.
                    throw new HttpRequestException($"[{_descriptor.EndpointId}] {relativePath} returned HTTP {lastHttpStatus} (auth/entitlement)");

                if (!response.IsSuccessStatusCode)
                    // 429/5xx after Polly retries exhausted — unit fails, run continues.
                    throw new HttpRequestException($"[{_descriptor.EndpointId}] {relativePath} returned HTTP {lastHttpStatus}");

                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var pageRows = ExtractPage(body, elements, ref paging, relativePath);
                pagesFetched++;

                if (pageRows == 0) break;                                        // empty page → done (both shapes)
                if (pagesFetched >= MaxPagesSafety)
                {
                    _logger.LogWarning("[{Endpoint}] {Path}: stopped paging at the {Max}-page safety cap", _descriptor.EndpointId, relativePath, MaxPagesSafety);
                    break;
                }

                if (paging.PageCount is int pageCount)
                {
                    // WRAPPER (1-based): index 0 was page 1; fetch the rest at indices 2..page_count.
                    if (paging.PageSize is int wrapperPageSize && pageRows < wrapperPageSize) break; // short final page (net)
                    var nextIndex = pageIndex == 0 ? 2 : pageIndex + 1;          // skip index 1 (== index 0 / page 1)
                    if (nextIndex > pageCount) break;                            // fetched all pages 1..page_count
                    pageIndex = nextIndex;
                }
                else
                {
                    // FLAT (0-based): contiguous pages; a short page is the last page.
                    if (pageRows < PageSize) break;
                    pageIndex++;
                }
            }

            if (paging.TotalRecordCount is int totalRecords && totalRecords != elements.Count)
                // Informational only — element count can legitimately differ from the API's total after
                // sentinel/invalid drops; this just surfaces a gross mismatch for diagnosis.
                _logger.LogDebug("[{Endpoint}] {Path}: accumulated {Rows} element(s); PagingInfo.total_record_count={Total}",
                    _descriptor.EndpointId, relativePath, elements.Count, totalRecords);

            var rows = MapRows(elements, unit, relativePath);

            // A 404 OR a zero-row 200 both log NotAvailable, no failure (design §5.2/§6).
            var status = rows.Count == 0 ? "NotAvailable" : "Success";
            var fileLogId = await _fileLog.UpsertAsync(file, status, lastHttpStatus, relativePath, rows.Count, cancellationToken).ConfigureAwait(false);

            if (rows.Count == 0)
                return Array.Empty<TRow>();

            foreach (var r in rows) r.FileLogId = fileLogId;

            _logger.LogDebug("[{Endpoint}] {Path} → {Rows} rows (FileLog #{Id})",
                _descriptor.EndpointId, relativePath, rows.Count, fileLogId);
            return rows;
        }
        catch (OperationCanceledException)
        {
            // Per-unit timeout / run cancellation is already recorded by LoadLog; don't write a
            // spurious hub row on a spent token.
            throw;
        }
        catch (Exception)
        {
            await TryUpsertFailedAsync(file, relativePath, lastHttpStatus).ConfigureAwait(false);
            throw; // LoaderPipelineBase logs + records the LoadLog failure and moves on.
        }
    }

    /// <summary>Builds the absolute page URI, appending <c>pageIndex</c> with the correct separator.</summary>
    private Uri BuildPageUri(string relativePath, int pageIndex)
    {
        var sep = relativePath.Contains('?') ? '&' : '?';
        var full = $"{_settings.BaseUrl.TrimEnd('/')}/{relativePath.TrimStart('/')}{sep}pageIndex={pageIndex}";
        return new Uri(full, UriKind.Absolute);
    }

    /// <summary>The wrapper envelope's <c>PagingInfo</c> (all null for a flat/bare-array response).</summary>
    private readonly record struct WrapperPaging(int? PageCount, int? PageSize, int? TotalRecordCount);

    /// <summary>
    /// Parses one page and appends its data elements (cloned) to <paramref name="accumulator"/>;
    /// returns the page's element count. Detects BOTH envelope shapes and, for the wrapper shape, reads
    /// <c>PagingInfo</c> (<c>page_count</c>/<c>page_size</c>/<c>total_record_count</c>) into
    /// <paramref name="paging"/> — a non-null <c>PageCount</c> marks a wrapper response. Invalid JSON is
    /// tolerated (warn + 0 rows).
    /// </summary>
    private int ExtractPage(string body, List<JsonElement> accumulator, ref WrapperPaging paging, string relativePath)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "[{Endpoint}] {Path}: response was not valid JSON", _descriptor.EndpointId, relativePath);
            return 0;
        }

        using (doc)
        {
            var root = doc.RootElement;

            JsonElement dataArray;
            if (root.ValueKind == JsonValueKind.Array)
            {
                dataArray = root;
            }
            else if (root.ValueKind == JsonValueKind.Object
                     && root.TryGetProperty("Data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                dataArray = data;
                if (root.TryGetProperty("PagingInfo", out var pi) && pi.ValueKind == JsonValueKind.Object)
                    paging = new WrapperPaging(
                        ReadInt(pi, "page_count"),
                        ReadInt(pi, "page_size"),
                        ReadInt(pi, "total_record_count"));
            }
            else
            {
                // Unexpected shape (no root array, no Data wrapper) → treat as no data for this page.
                return 0;
            }

            var count = 0;
            foreach (var el in dataArray.EnumerateArray())
            {
                accumulator.Add(el.Clone()); // clone: the per-page JsonDocument is disposed before the next page
                count++;
            }
            return count;
        }
    }

    /// <summary>Reads an integer property from a JSON object, or null if absent/non-numeric.</summary>
    private static int? ReadInt(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)
            ? n
            : null;

    /// <summary>Maps the accumulated elements via the row factory, classifying sentinel vs invalid drops.</summary>
    private List<TRow> MapRows(List<JsonElement> elements, PlWorkUnit unit, string relativePath)
    {
        var rows = new List<TRow>(elements.Count);
        var sentinelDrops = 0;
        var invalidDrops = 0;

        foreach (var elem in elements)
        {
            var row = _rowFactory(elem, unit);
            if (row is not null) { rows.Add(row); continue; }

            if (_sentinelPredicate is not null && _sentinelPredicate(elem)) sentinelDrops++;
            else invalidDrops++;
        }

        if (sentinelDrops > 0)
            _logger.LogDebug("[{Endpoint}] {Path}: skipped {Count} sentinel element(s)",
                _descriptor.EndpointId, relativePath, sentinelDrops);
        if (invalidDrops > 0)
            _logger.LogWarning("[{Endpoint}] {Path}: dropped {Count} unparseable/invalid record(s)",
                _descriptor.EndpointId, relativePath, invalidDrops);

        return rows;
    }

    private async Task TryUpsertFailedAsync(PlFileContext file, string relativePath, int? httpStatus)
    {
        try
        {
            // Best-effort; use None so the Failed row is still written even when the failure was a
            // timeout, and never mask the original exception.
            await _fileLog.UpsertAsync(file, "Failed", httpStatus, relativePath, 0, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{Endpoint}] failed to write 'Failed' FileLog for {Path}", _descriptor.EndpointId, relativePath);
        }
    }
}
