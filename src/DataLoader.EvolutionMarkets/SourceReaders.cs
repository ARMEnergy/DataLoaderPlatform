using System.Net;
using System.Text.Json;
using DataLoader.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace DataLoader.EvolutionMarkets;

// =============================================================================
// The JSON source reader (design §5).
//
// *** IT IMPLEMENTS ISourceReader DIRECTLY — NOT HttpJsonSourceReaderBase. ***
// Three reasons, each independently sufficient:
//   1. That base calls EnsureSuccessStatusCode(), which cannot express this feed's
//      status matrix (an empty 200 is a routine non-publishing day, and a 400 must be
//      distinguished from it as a hard loader bug).
//   2. It reads ONE response body. This endpoint needs a pager: the response is a
//      bare array with a silent 10,000-row cap.
//   3. It deserialises straight to List<TItem>. This feed's rows are heterogeneous
//      (keys are OMITTED rather than null) and the response spelling differs from the
//      request spelling, so every field is navigated by candidate name instead.
//
// The reader uses the registered "EvolutionMarkets" client (api-key + retry +
// throttle applied by handlers), parses with System.Text.Json under the invariant
// culture, writes its own arm.FileLog outcome row (it holds the HTTP status, the row
// count and the page count) and stamps the returned FileLogId onto every produced row.
//
// The API key is a request HEADER and never appears in a URL, so — unusually for this
// repo — request URIs are logged IN FULL rather than sanitised. There is no secret to
// strip. (Contrast CWG/StormVista's ?apikey= URLs, which must be truncated at the path.)
// =============================================================================

/// <summary>How one HTTP status is dispatched — design §5.4.</summary>
internal enum EvoStatusAction
{
    /// <summary>2xx — parse the body (which may legitimately be an empty array).</summary>
    Parse,

    /// <summary>404 — a successful, EMPTY read: <c>NotAvailable</c> hub row, 0 rows, the unit SUCCEEDS.</summary>
    NotAvailable,

    /// <summary>Anything else — throw (the catch writes a <c>Failed</c> hub row and rethrows).</summary>
    Throw
}

/// <summary>
/// The status matrix (design §5.4), in one place so it cannot drift and can be unit-tested directly.
/// </summary>
internal static class EvoStatus
{
    /// <summary>
    /// <list type="bullet">
    ///   <item><b>200</b> → parse. <b>An empty array <c>[]</c> is a NORMAL 200</b>, not an error: it
    ///     is what every weekend, US holiday, pre-retention date and not-yet-published current date
    ///     returns. It is recorded as <c>NotAvailable</c> with <c>RowCount = 0</c> and the work unit
    ///     SUCCEEDS. See the settled-zone hazard in <see cref="EvoSettings.SettledAfterDays"/> for why
    ///     the hub row matters.</item>
    ///   <item><b>404</b> → <c>NotAvailable</c>, <c>RowCount = 0</c>, unit SUCCEEDS. Never observed on
    ///     this endpoint (it answers 200 <c>[]</c> instead), so it is also WARNED about — an
    ///     unexpected 404 most likely means the path or the API version moved.</item>
    ///   <item><b>400</b> → <b>THROW</b>. The vendor emits it for a malformed <c>dateFrom</c>
    ///     ("Should be yyyy-MM-dd"), a future <c>dateFrom</c>, a reversed date range, or a
    ///     <c>limit</c>/<c>offset</c> outside 1..10000 / 0..int.MaxValue. Every one is OUR bug,
    ///     unfixable by retry, and must never be swallowed like an empty read.</item>
    ///   <item><b>401</b> → <b>THROW</b>. Missing/blank API key. Static key: nothing to refresh.</item>
    ///   <item><b>403</b> → <b>THROW</b>. Key valid but not entitled to the resource.</item>
    ///   <item><b>429 / 5xx</b> → Polly has already exhausted its retries → throw (the unit fails,
    ///     the run continues).</item>
    /// </list>
    /// </summary>
    public static EvoStatusAction Classify(HttpStatusCode status)
    {
        if ((int)status is >= 200 and < 300) return EvoStatusAction.Parse;
        if (status == HttpStatusCode.NotFound) return EvoStatusAction.NotAvailable;
        return EvoStatusAction.Throw;
    }

    /// <summary>
    /// A message that names the LIKELY CAUSE, so an operator is not left guessing which of several
    /// very different faults produced the same status code.
    /// </summary>
    public static string Describe(string path, int httpStatus, string? body) => httpStatus switch
    {
        400 => $"[EvolutionMarkets] {path} returned HTTP 400 — malformed request. Check, in order: " +
               "dateFrom/dateTo must be yyyy-MM-dd invariant; dateFrom must not be in the vendor's " +
               "future; dateTo must not precede dateFrom; limit must be 1..10000 and offset >= 0. " +
               $"This is a LOADER BUG, not a data condition. Vendor said: {Snippet(body)}",

        401 => $"[EvolutionMarkets] {path} returned HTTP 401 Unauthorized — the Authorization header " +
               "was missing or empty. Verify core.Param(LoaderName='EvolutionMarkets', ParamName='ApiKey'). " +
               "NOTE the key is sent as the RAW header value, NOT as HTTP Basic.",

        403 => $"[EvolutionMarkets] {path} returned HTTP 403 — the key was recognised but is not " +
               "authorized for this resource (an entitlement/permission gap, or a wrong key). " +
               $"Vendor said: {Snippet(body)}",

        // 500 is retryable and Polly has already exhausted its attempts by the time we get here.
        // The single most likely permanent cause is a bad `field` name, which this API reports as a
        // 500 rather than a 400 — so say so, or the next reader of this log will chase a phantom
        // outage. See EvoRequestFields.
        500 => $"[EvolutionMarkets] {path} returned HTTP 500 after exhausting retries. If this is " +
               "reproducible rather than intermittent, the most likely cause is an UNKNOWN NAME in the " +
               "'field' projection — this API reports that as a 500, not a 400. Verify " +
               $"EvoRequestFields.All against docs/apis/EvolutionMarkets.md. Vendor said: {Snippet(body)}",

        _ => $"[EvolutionMarkets] {path} returned HTTP {httpStatus}. Vendor said: {Snippet(body)}"
    };

    /// <summary>
    /// A short, single-line excerpt of an error body for a log message. The vendor's errors are tiny
    /// JSON objects (<c>{"message":"…"}</c>), but a proxy or gateway can return an HTML page, so this
    /// is bounded and newline-free.
    /// </summary>
    private static string Snippet(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "(empty body)";
        var flat = body.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return flat.Length <= 300 ? flat : flat[..300] + "…";
    }
}

/// <summary>
/// <c>GET /v1/market-data/history</c> — reads one business date and produces
/// <see cref="MarketDataRow"/>s for <c>arm.MarketData</c> (design §5.2).
/// </summary>
public sealed class EvoMarketDataSourceReader : ISourceReader<EvoMarketDataWorkUnit, MarketDataRow>
{
    /// <summary>
    /// Guard against a runaway pager. At the shipped 5,000-row page size and ~205 rows per business
    /// date this is unreachable by four orders of magnitude; it exists only so a vendor-side change
    /// that made <c>offset</c> a no-op could never spin forever.
    /// </summary>
    private const int MaxPagesSafety = 200;

    private readonly HttpClient _http;
    private readonly EvoSettings _settings;
    private readonly IEvoFileLog _fileLog;
    private readonly ILogger _logger;

    public EvoMarketDataSourceReader(
        HttpClient http, EvoSettings settings, IEvoFileLog fileLog, ILogger logger)
    {
        _http = http;
        _settings = settings;
        _fileLog = fileLog;
        _logger = logger;
    }

    /// <summary>
    /// The effective page size: the configured value clamped into the vendor's accepted
    /// <c>1..10000</c> range. Clamping rather than validating is deliberate — a mis-set
    /// <c>PageSize</c> must not fail every work unit with a 400 the operator has to decode.
    /// </summary>
    internal int EffectivePageSize =>
        Math.Clamp(_settings.PageSize, 1, EvoSettings.MaxVendorPageSize);

    public async Task<IReadOnlyList<MarketDataRow>> ReadAsync(
        EvoMarketDataWorkUnit unit, CancellationToken cancellationToken)
    {
        var file = new EvoFileContext(EvoEndpoints.MarketDataHistory, unit.BusinessDate, unit.RequestPath);

        int? lastHttpStatus = null;
        var pageCount = 0;
        var counters = new EvoParseCounters();

        try
        {
            // -------------------------------------------------------------------------------------
            // PAGING — verified against the live API on 2026-08-25.
            //
            //   `offset`/`limit` are honoured and STABLE: the 2026-08-01..2026-08-10 window
            //   (1,230 rows) read in 500-row pages returned 500 + 500 + 230 with 1,230 DISTINCT
            //   priceIds, exactly matching the un-paged read. The same held for the 3,075-row August
            //   window in 1,000-row pages.
            //
            //   *** THE PAGER IS A CORRECTNESS GUARD, NOT AN OPTIMISATION. ***
            //   The response is a BARE JSON ARRAY: no envelope, no total count, no page count, no
            //   next-page link, no Link header. The vendor caps a single request at 10,000 rows. So a
            //   read that hit the cap would be INDISTINGUISHABLE from a complete read — silent
            //   truncation, no error, no warning. Paging until a short page is the only way to know
            //   the read finished. Do not "simplify" this to a single request.
            //
            //   Stop conditions: a short page (< limit) or an empty page. MaxPagesSafety is a
            //   belt-and-braces net, never reached in practice.
            // -------------------------------------------------------------------------------------
            var pageSize = EffectivePageSize;
            var byId = new Dictionary<Guid, MarketDataRow>();
            var ordered = new List<MarketDataRow>();
            var offset = 0;
            var shapeGuardRun = false;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var pageUri = BuildPageUri(unit.RequestPath, pageSize, offset);

                // Logged IN FULL and unsanitised: the credential is a header, so no URL is sensitive.
                _logger.LogDebug("[EvolutionMarkets] GET {Uri}", pageUri);

                using var response = await _http.GetAsync(pageUri, cancellationToken).ConfigureAwait(false);
                lastHttpStatus = (int)response.StatusCode;

                var action = EvoStatus.Classify(response.StatusCode);

                if (action == EvoStatusAction.NotAvailable)
                {
                    // Never observed on this endpoint — it answers 200 [] instead — so warn, but still
                    // treat it as a successful empty read per the shared matrix. Whatever earlier pages
                    // accumulated is kept: a mid-pagination 404 is reported by the row/page counts.
                    _logger.LogWarning(
                        "[EvolutionMarkets] unexpected HTTP 404 for {Date} — this endpoint returns " +
                        "200 [] for a non-publishing day, so a 404 suggests the path or API version moved",
                        EvoTime.Iso(unit.BusinessDate));
                    break;
                }

                if (action == EvoStatusAction.Throw)
                {
                    var errorBody = await SafeReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
                    throw new HttpRequestException(
                        EvoStatus.Describe(unit.RequestPath, lastHttpStatus.Value, errorBody));
                }

                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var pageRows = ParsePage(body, unit, counters, out var elementCount);
                pageCount++;

                // Run the projection-integrity guard ONCE, on the first page that carried any row.
                if (!shapeGuardRun && pageRows.Count > 0)
                {
                    shapeGuardRun = true;
                    WarnOnProjectionDrift(body, unit);
                }

                foreach (var row in pageRows)
                {
                    // De-dup across pages on the PK. Defensive: paging was verified collision-free,
                    // but a concurrent vendor-side insert could shift the offset window and re-serve a
                    // row. FIRST occurrence wins and the duplicate is counted, never silently dropped.
                    if (byId.TryAdd(row.MarketDataId, row))
                        ordered.Add(row);
                    else
                        counters.DroppedDuplicateKey++;
                }

                if (elementCount == 0) break;                    // empty page → done
                if (elementCount < pageSize) break;              // short page → last page

                if (pageCount >= MaxPagesSafety)
                {
                    _logger.LogWarning(
                        "[EvolutionMarkets] {Date}: stopped paging at the {Max}-page safety cap after " +
                        "{Rows} row(s) — the result may be INCOMPLETE; investigate offset/limit handling",
                        EvoTime.Iso(unit.BusinessDate), MaxPagesSafety, ordered.Count);
                    break;
                }

                offset += pageSize;
            }

            var rows = ordered;

            // An empty result is a legitimate non-publishing day (weekend, holiday, pre-retention, or
            // a current date that has not published yet). It is NOT a failure and NOT a warning.
            var status = rows.Count == 0 ? EvoFileStatus.NotAvailable : EvoFileStatus.Success;

            var fileLogId = await _fileLog.UpsertAsync(
                file, status, lastHttpStatus, rows.Count, DroppedTotal(counters), pageCount,
                errorMessage: null, cancellationToken).ConfigureAwait(false);

            if (!counters.IsClean)
                _logger.LogWarning(
                    "[EvolutionMarkets] {Date} tolerance counters: {Counters}",
                    EvoTime.Iso(unit.BusinessDate), counters);

            if (rows.Count == 0)
            {
                _logger.LogDebug(
                    "[EvolutionMarkets] {Date} → no rows published (HTTP {Status}); recorded NotAvailable (FileLog #{Id})",
                    EvoTime.Iso(unit.BusinessDate), lastHttpStatus, fileLogId);
                return Array.Empty<MarketDataRow>();
            }

            // Stamp provenance, THEN compute the checksum. Order does not matter for correctness here
            // (FileLogId is excluded from the hash on purpose — see EvoChecksum.Canonical) but doing
            // it in one pass keeps the row complete before it leaves the reader.
            foreach (var r in rows)
            {
                r.FileLogId = fileLogId;
                r.Checksum = EvoChecksum.Compute(r);
            }

            _logger.LogDebug(
                "[EvolutionMarkets] {Date} → {Rows} row(s) over {Pages} page(s) (FileLog #{Id})",
                EvoTime.Iso(unit.BusinessDate), rows.Count, pageCount, fileLogId);

            return rows;
        }
        catch (OperationCanceledException)
        {
            throw; // no FileLog write on a spent token
        }
        catch (Exception ex)
        {
            await TryUpsertFailedAsync(file, lastHttpStatus, pageCount, counters, ex).ConfigureAwait(false);
            throw; // LoaderPipelineBase records the LoadLog failure and the run continues
        }
    }

    /// <summary>
    /// Appends <c>limit</c>/<c>offset</c> to the unit's path and resolves it against the host.
    /// <c>internal</c> so a test can pin the exact URL shape.
    /// </summary>
    internal Uri BuildPageUri(string relativePath, int pageSize, int offset)
    {
        var sep = relativePath.Contains('?') ? '&' : '?';
        return new Uri(
            $"{_settings.BaseUrlRoot()}{relativePath}{sep}limit={pageSize.ToString(EvoTime.Inv)}&offset={offset.ToString(EvoTime.Inv)}",
            UriKind.Absolute);
    }

    private static int DroppedTotal(EvoParseCounters c) =>
        c.DroppedNoKey + c.DroppedDuplicateKey + c.DroppedNotAnObject;

    /// <summary>
    /// Parses one page. <paramref name="elementCount"/> reports the number of ARRAY ELEMENTS the
    /// vendor sent, which is what the pager's short-page test must use — <b>not</b> the number of rows
    /// produced, which is smaller whenever a record was dropped and would end paging early.
    /// </summary>
    private List<MarketDataRow> ParsePage(
        string body, EvoMarketDataWorkUnit unit, EvoParseCounters counters, out int elementCount)
    {
        elementCount = 0;

        // An empty/whitespace 2xx body is shape drift too. Without this guard JsonDocument.Parse
        // surfaces it as the opaque "The input does not contain any JSON tokens", which reads like a
        // loader bug rather than a vendor one.
        if (string.IsNullOrWhiteSpace(body))
            throw new InvalidOperationException(
                $"[EvolutionMarkets] {unit.RequestPath}: response shape drift — expected a top-level " +
                "JSON ARRAY of market-data records; the response body was empty.");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        // The documented and observed shape is a BARE ARRAY. An object here means the vendor wrapped
        // the payload in an envelope (as its /v1/datasets endpoints do) — a breaking change that must
        // fail LOUDLY rather than yield a silent zero-row success.
        if (root.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException(
                $"[EvolutionMarkets] {unit.RequestPath}: response shape drift — expected a top-level " +
                $"JSON ARRAY of market-data records but got {root.ValueKind}. If the vendor has " +
                "introduced a paging envelope, the reader must be updated to unwrap it (and to use its " +
                "page/total metadata instead of the short-page heuristic).");

        var rows = new List<MarketDataRow>(Math.Min(EffectivePageSize, 512));

        foreach (var element in root.EnumerateArray())
        {
            elementCount++;

            if (element.ValueKind != JsonValueKind.Object)
            {
                counters.DroppedNotAnObject++;
                continue;
            }

            var row = MapRow(element, counters);
            if (row is null) continue;
            rows.Add(row);
        }

        return rows;
    }

    /// <summary>
    /// Maps one JSON object to a row. Returns <c>null</c> (counted) only when the KEY is unusable;
    /// every other field degrades to <c>NULL</c>.
    ///
    /// <para>Column widths passed to <see cref="EvoParse.StrCapped"/> mirror
    /// <c>sql/EvolutionMarkets/002</c> exactly. If a width changes there, change it here.</para>
    /// </summary>
    private static MarketDataRow? MapRow(JsonElement e, EvoParseCounters counters)
    {
        var id = EvoParse.Guid(EvoParse.Prop(e, EvoFields.MarketDataId));
        if (id is null || id.Value == Guid.Empty)
        {
            // Unkeyable: no PK, so it cannot be merged. Drop-and-count — never insert a row that
            // every subsequent run would duplicate. Guid.Empty is rejected as well: it is a
            // placeholder, not an id, and would collide across unrelated records.
            counters.DroppedNoKey++;
            return null;
        }

        var truncated = counters.Truncated;

        var row = new MarketDataRow
        {
            MarketDataId = id.Value,
            Market = EvoParse.StrCapped(EvoParse.Prop(e, EvoFields.Market), 250, ref truncated),
            Term = EvoParse.StrCapped(EvoParse.Prop(e, EvoFields.Term), 50, ref truncated),
            Term2 = EvoParse.StrCapped(EvoParse.Prop(e, EvoFields.Term2), 50, ref truncated),
            Tenor = EvoParse.StrCapped(EvoParse.Prop(e, EvoFields.Tenor), 50, ref truncated),
            InstrumentSourceName = EvoParse.StrCapped(EvoParse.Prop(e, EvoFields.InstrumentSourceName), 250, ref truncated),
            InstrumentId = EvoParse.Guid(EvoParse.Prop(e, EvoFields.InstrumentId)),
            InstrumentName = EvoParse.StrCapped(EvoParse.Prop(e, EvoFields.InstrumentName), 250, ref truncated),
            PriceTs = EvoParse.Instant(EvoParse.Prop(e, EvoFields.PriceTs)),
            BusinessDate = EvoParse.Date(EvoParse.Prop(e, EvoFields.BusinessDate)),
            PriceType = EvoParse.StrCapped(EvoParse.Prop(e, EvoFields.PriceType), 250, ref truncated),
            Size = EvoParse.Int(EvoParse.Prop(e, EvoFields.Size)),
            Depth = EvoParse.Int(EvoParse.Prop(e, EvoFields.Depth)),
            Price = EvoParse.Dec(EvoParse.Prop(e, EvoFields.Price)),
            Ask = EvoParse.Dec(EvoParse.Prop(e, EvoFields.Ask)),
            AskSize = EvoParse.Int(EvoParse.Prop(e, EvoFields.AskSize)),
            Bid = EvoParse.Dec(EvoParse.Prop(e, EvoFields.Bid)),
            BidSize = EvoParse.Int(EvoParse.Prop(e, EvoFields.BidSize)),
            Mid = EvoParse.Dec(EvoParse.Prop(e, EvoFields.Mid)),
            MidSize = EvoParse.Int(EvoParse.Prop(e, EvoFields.MidSize)),
            Change = EvoParse.Dec(EvoParse.Prop(e, EvoFields.Change)),
            PctRetDaily = EvoParse.Dec(EvoParse.Prop(e, EvoFields.PctRetDaily)),
            Currency = EvoParse.StrCapped(EvoParse.Prop(e, EvoFields.Currency), 50, ref truncated)
        };

        counters.Truncated = truncated;
        return row;
    }

    /// <summary>
    /// Warns when the response is missing a key the pinned projection asked for (design §5.6).
    ///
    /// <para><b>This is the tripwire for the <c>change</c> vendor bug and for a vendor-side rename.</b>
    /// <c>change</c> is returned correctly only when the FULL 23-name projection is sent; in a short
    /// projection the server silently substitutes <c>term</c>, so <c>change</c> would simply be absent
    /// and the column would fill with NULLs with nothing in the log to explain it. Checked against the
    /// FIRST populated page only — once per work unit, not once per row.</para>
    ///
    /// <para>Deliberately excludes the nine fields this dataset never populates and <c>tenor</c>
    /// (absent on ~53% of rows), because including them would make the guard cry wolf on every
    /// healthy load and be muted within a week.</para>
    /// </summary>
    private void WarnOnProjectionDrift(string body, EvoMarketDataWorkUnit unit)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return;

            // Union the keys across the whole page: rows are heterogeneous, so a key missing from one
            // row is not evidence of drift — a key missing from EVERY row is.
            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object) continue;
                foreach (var p in element.EnumerateObject()) present.Add(p.Name);
            }

            if (present.Count == 0) return;

            var missing = EvoRequestFields.ExpectedResponseKeys
                .Where(k => !present.Contains(k))
                .ToArray();

            if (missing.Length == 0) return;

            _logger.LogWarning(
                "[EvolutionMarkets] {Date}: PROJECTION DRIFT — the response is missing expected key(s) " +
                "[{Missing}] on every row of the page, so those columns will load as NULL. If 'change' is " +
                "listed, the 'field' projection has almost certainly been trimmed: this vendor returns " +
                "'change' correctly ONLY when the full pinned EvoRequestFields.All list is sent, and " +
                "substitutes 'term' otherwise. Keys actually present: [{Present}]",
                EvoTime.Iso(unit.BusinessDate), string.Join(",", missing), string.Join(",", present.OrderBy(k => k)));
        }
        catch (JsonException)
        {
            // The guard is diagnostics only. The real parse already succeeded to get here, so a
            // failure re-parsing must never affect the load.
        }
    }

    /// <summary>Reads an error body without ever letting that read replace the error being reported.</summary>
    private static async Task<string?> SafeReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Best-effort <c>Failed</c> hub write. Swallows its own failure on purpose: the ORIGINAL
    /// exception is the one worth propagating, and a hub-write error must never mask it.
    /// </summary>
    private async Task TryUpsertFailedAsync(
        EvoFileContext file, int? httpStatus, int pageCount, EvoParseCounters counters, Exception cause)
    {
        try
        {
            await _fileLog.UpsertAsync(
                file, EvoFileStatus.Failed, httpStatus, rowCount: 0, DroppedTotal(counters), pageCount,
                cause.Message, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[EvolutionMarkets] could not write the Failed arm.FileLog row for {Date} " +
                "(the original failure is reported separately and is the one to act on)",
                EvoTime.Iso(file.RepresentativeDate));
        }
    }
}
