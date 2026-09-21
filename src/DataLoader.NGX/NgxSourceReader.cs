using System.Net;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using DataLoader.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace DataLoader.NGX;

/// <summary>
/// The vendor answered 403 "Access Denied" — this account is not entitled to at least
/// one of the index ids in the request. NOT transient, so it is never retried; the
/// reader narrows the batch instead.
/// </summary>
internal sealed class NgxForbiddenException : Exception
{
    internal NgxForbiddenException(string message) : base(message) { }
}

/// <summary>
/// A 200 response whose body is not the document we asked for. Thrown rather than
/// swallowed: an empty result and a wrong-shaped result must not look alike, or an
/// authentication regression would read as "no data today" forever.
/// </summary>
internal sealed class NgxMalformedResponseException : Exception
{
    internal NgxMalformedResponseException(string message) : base(message) { }
}

/// <summary>
/// Shared HTTP for both feeds: issue the request, classify the status, hand back a
/// stream.
///
/// <para>
/// <b>Authentication is HTTP BASIC, not the bearer token.</b> ICE NGX publishes a
/// documented <c>POST /api/v2/authentication/token</c> endpoint, and that token works —
/// for <c>/api/v2</c> only. The <c>.xml</c> documents this loader reads sit outside
/// that prefix behind the legacy web-session filter, which accepts Basic credentials
/// and ignores the bearer token entirely: fifteen different token placements (cookie,
/// query, header) all returned <c>302</c> to the ICE SSO login form. See
/// docs/apis/NGX.md section 2 — this is the single most surprising thing about the
/// endpoint.
/// </para>
/// <para>
/// A 302 is therefore the signature of MISSING OR REJECTED credentials, and is
/// translated into a loud error rather than followed — the redirect target is an HTML
/// login page that would otherwise parse as zero records.
/// </para>
/// </summary>
internal abstract class NgxReaderBase
{
    private readonly HttpClient _http;
    private readonly ILogger _logger;

    private protected NgxReaderBase(HttpClient http, ILogger logger)
    {
        _http = http;
        _logger = logger;
    }

    private protected ILogger Logger => _logger;

    /// <summary>
    /// GETs <paramref name="url"/> and returns the response for the caller to
    /// <c>using</c>.
    ///
    /// <para>
    /// The RESPONSE is returned rather than just its stream because
    /// <see cref="HttpCompletionOption.ResponseHeadersRead"/> leaves the connection open
    /// until the message is disposed. Handing out a bare stream orphaned the response on
    /// both the success path and the <see cref="HttpResponseMessage.EnsureSuccessStatusCode"/>
    /// throw, leaking a connection per failure until finalization — which matters most
    /// exactly when it hurts, during a run of 5xx that has exhausted its retry budget.
    /// Every exit here either returns an owned response or disposes it.
    /// </para>
    /// <para>The status matrix this enforces:</para>
    /// <list type="bullet">
    ///   <item><c>200</c> + expected root -> data (possibly zero records, which is legitimate);</item>
    ///   <item><c>200</c> + anything else -> <see cref="NgxMalformedResponseException"/>;</item>
    ///   <item><c>302</c> -> credentials missing or rejected (redirect to ICE SSO);</item>
    ///   <item><c>403</c> -> <see cref="NgxForbiddenException"/>, an entitlement problem, never retried;</item>
    ///   <item>anything else -> <see cref="HttpRequestException"/>, retried by the policy.</item>
    /// </list>
    /// </summary>
    private protected async Task<HttpResponseMessage> SendAsync(string url, CancellationToken cancellationToken)
    {
        _logger.LogDebug("NGX GET {Url}", url);

        var response = await _http
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            if (response.StatusCode is HttpStatusCode.Found or HttpStatusCode.Redirect
                or HttpStatusCode.MovedPermanently or HttpStatusCode.TemporaryRedirect)
                throw new NgxMalformedResponseException(
                    $"NGX answered {(int)response.StatusCode} with a redirect to the ICE SSO login page for {url}. " +
                    "That means the HTTP Basic credentials were missing or rejected — this endpoint does NOT " +
                    "accept the /api/v2 bearer token. Check core.Param(LoaderName='NGX') Username/Password.");

            if (response.StatusCode == HttpStatusCode.Forbidden)
                throw new NgxForbiddenException(
                    $"NGX answered 403 Access Denied for {url}. On this endpoint that means the account is not " +
                    "entitled to one of the requested indices (the vendor fails the WHOLE request, not the " +
                    "offending id), or more than 10 indexId parameters were sent.");

            response.EnsureSuccessStatusCode();

            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Percent-encodes a query value. The request dates contain no reserved characters
    /// today (<c>1-September-2026</c>), but the grouping and commodity settings are
    /// operator-supplied.
    /// </summary>
    private protected static string Q(string value) => Uri.EscapeDataString(value);
}

/// <summary>
/// Reads <c>GET /ngxcs/indexPrice.xml</c> for one batch of index ids over one window,
/// and projects straight into <c>arm.IndexPrice</c> row order.
///
/// <para>Two vendor behaviours dominate this class:</para>
/// <list type="number">
///   <item>
///     <b>Silent truncation.</b> The default page is FIFTY rows, and the only sign is a
///     <c>&lt;truncated&gt;true&lt;/truncated&gt;</c> element alongside a
///     <c>&lt;fullListSize&gt;</c> the caller has to compare for itself. The loader
///     sends an explicit <c>pageSize</c> AND checks the flag, then pages with
///     <c>page=N</c> until it has <c>fullListSize</c> rows. Note the paging parameter
///     really is <c>page</c>: <c>pageNumber</c> is accepted and SILENTLY IGNORED
///     (<c>pageNumber=2</c> returns page 1 again), as is <c>size</c>.
///   </item>
///   <item>
///     <b>All-or-nothing entitlement.</b> A batch naming one index the account cannot
///     see returns 403 for the entire batch. Rather than fail up to ten indices because
///     of one, the reader narrows to single-id requests and skips only the ids that are
///     genuinely refused, warning once per id.
///   </item>
/// </list>
/// </summary>
internal sealed class NgxIndexPriceReader : NgxReaderBase, ISourceReader<NgxIndexPriceWorkUnit, NgxRow>
{
    private const string RecordElement = "indexPriceSummary";
    private const string RootElement = "indexPriceList";

    private readonly NgxSettings _settings;

    internal NgxIndexPriceReader(HttpClient http, NgxSettings settings, ILogger logger)
        : base(http, logger)
    {
        _settings = settings;
    }

    public async Task<IReadOnlyList<NgxRow>> ReadAsync(
        NgxIndexPriceWorkUnit unit, CancellationToken cancellationToken)
    {
        try
        {
            return await ReadBatchAsync(unit, unit.IndexIds, cancellationToken).ConfigureAwait(false);
        }
        catch (NgxForbiddenException) when (unit.IndexIds.Count > 1)
        {
            Logger.LogWarning(
                "NGX IndexPrice: batch [{Ids}] was refused wholesale (403). Retrying the {Count} ids " +
                "individually so one unentitled index does not cost the rest",
                string.Join(",", unit.IndexIds), unit.IndexIds.Count);

            var rows = new List<NgxRow>();
            var refused = new List<int>();

            foreach (var id in unit.IndexIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    rows.AddRange(await ReadBatchAsync(unit, new[] { id }, cancellationToken).ConfigureAwait(false));
                }
                catch (NgxForbiddenException)
                {
                    refused.Add(id);
                }
            }

            // ⚠ If EVERY id was refused, this must FAIL rather than return an empty list.
            // An empty return would be recorded as a successful, legitimately-empty read
            // under the hot key — so a revoked entitlement or a locked account would look
            // exactly like "no data in this window", which is the one confusion the
            // status matrix exists to prevent. A partial refusal is a warning; a total
            // one is a failure.
            if (refused.Count == unit.IndexIds.Count)
                throw new NgxForbiddenException(
                    $"NGX IndexPrice: ALL {refused.Count} index id(s) in this unit were refused 403 " +
                    $"([{string.Join(",", refused)}]). That is an entitlement or account problem, not an " +
                    "empty window — failing the work unit rather than recording an empty read.");

            if (refused.Count > 0)
                Logger.LogWarning(
                    "NGX IndexPrice: index id(s) [{Refused}] are NOT entitled on indexPrice.xml and were " +
                    "skipped. They are most likely CrudeIndexPrice rows that have leaked into the " +
                    "IndexType = '{Filter}' filter, and they belong to a different endpoint",
                    string.Join(",", refused), _settings.IndexTypeFilter);

            return rows;
        }

        // NOTE the `when (unit.IndexIds.Count > 1)` guard above: a SINGLE-id unit lets the
        // 403 propagate untouched, which reaches the same outcome as the all-refused throw
        // below without spending a redundant request re-asking about the one id we already
        // know was refused. The two paths agree, so behaviour does not depend on whether
        // the id count happens to leave a remainder of one when cut into batches.
    }

    /// <summary>One batch, paged to completion.</summary>
    private async Task<IReadOnlyList<NgxRow>> ReadBatchAsync(
        NgxIndexPriceWorkUnit unit, IReadOnlyList<int> ids, CancellationToken cancellationToken)
    {
        var rows = new List<NgxRow>();

        // ⚠ Progress is counted in RECORDS SEEN, not in rows kept. fullListSize counts
        // what the vendor emitted; a record dropped by the key check would make a
        // rows.Count target permanently unreachable, so the loop would page past the end
        // of the data every time — and against a server that clamps an out-of-range
        // `page` back to page 1 (a common behaviour) it would re-collect page 1 over and
        // over, reporting a "complete" result full of duplicates with the real tail
        // missing.
        var recordsSeen = 0;
        var page = 1;
        var largestPage = 0;

        // Absolute bound, independent of what the envelope claims. Every other
        // termination condition below trusts the vendor to be self-consistent; this one
        // does not.
        var maxPages = 2;

        // First record id per page. A server that ignores `page` and re-serves page 1
        // would otherwise look like steady progress — recordsSeen climbs, pages are
        // never empty — right up until the loop "completes" with a pile of duplicates
        // and the real tail missing.
        var firstIdsSeen = new HashSet<string>(StringComparer.Ordinal);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var url = BuildUrl(unit, ids, page);

            PageResult result;
            using (var response = await SendAsync(url, cancellationToken).ConfigureAwait(false))
            await using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            {
                result = ParsePage(stream, unit, url);
            }

            if (page > 1 && result.FirstRecordId is { } firstId && !firstIdsSeen.Add(firstId))
                throw new NgxMalformedResponseException(
                    $"NGX IndexPrice: page {page} of {unit.DisplayName} starts with record id '{firstId}', " +
                    "which a previous page already returned — the server is ignoring the 'page' parameter. " +
                    "Continuing would collect duplicates and silently lose the rest of the window.");

            if (page == 1 && result.FirstRecordId is { } id1) firstIdsSeen.Add(id1);

            rows.AddRange(result.Rows);
            recordsSeen += result.RecordsSeen;
            largestPage = Math.Max(largestPage, result.RecordsSeen);

            // Size the bound from the records the server ACTUALLY serves per page, not
            // from the pageSize we asked for — a server honouring a smaller page is
            // legitimate and must not be cut off early. +2 pages of slack for a vendor
            // that counts slightly differently than it paginates.
            if (result.FullListSize > 0 && largestPage > 0)
                maxPages = (int)Math.Ceiling(result.FullListSize / (double)largestPage) + 2;

            // The flag is the contract; fullListSize is the target.
            if (!result.Truncated || recordsSeen >= result.FullListSize)
                break;

            // An empty page means the server has nothing further to give. Its
            // fullListSize was then simply wrong, which is a vendor bookkeeping bug
            // rather than a failure on our side: we have everything obtainable, so warn
            // and accept rather than failing the unit on every single run.
            if (result.RecordsSeen == 0)
            {
                Logger.LogWarning(
                    "NGX IndexPrice: stopped paging {Unit} at {Have} of a claimed {Want} record(s) — page " +
                    "{Page} came back empty while the response still reported itself truncated. The vendor's " +
                    "fullListSize appears to overstate what it will serve",
                    unit.DisplayName, recordsSeen, result.FullListSize, page);
                break;
            }

            // ⚠ Reaching the bound is DIFFERENT: pages are still non-empty, so the
            // server has more to give and is simply not paginating the way its envelope
            // describes. Returning here would record a SUCCESS for a window we provably
            // failed to read — and because ExecutionDate leads the target primary key,
            // that day's snapshot would stay permanently short with nothing to come back
            // for it. Fail the unit instead: the load log shows it, and the hot resume
            // key lets a re-run fix the same day.
            if (page >= maxPages)
                throw new NgxMalformedResponseException(
                    $"NGX IndexPrice: gave up paging {unit.DisplayName} after {page} page(s) holding " +
                    $"{recordsSeen} of a claimed {result.FullListSize} record(s). Pages are still non-empty " +
                    "and the response still reports itself truncated, so the endpoint is not paginating as " +
                    "its envelope describes. Failing rather than recording a partial window as complete.");

            page++;

            Logger.LogDebug(
                "NGX IndexPrice: {Unit} truncated at {Have}/{Want} record(s), fetching page {Page}",
                unit.DisplayName, recordsSeen, result.FullListSize, page);
        }

        return rows;
    }

    private string BuildUrl(NgxIndexPriceWorkUnit unit, IReadOnlyList<int> ids, int page)
    {
        var sb = new StringBuilder();
        sb.Append(_settings.BaseUrl.TrimEnd('/')).Append("/indexPrice.xml?");
        sb.Append("effectiveStart=").Append(Q(NgxTime.RequestDate(unit.Start)));
        sb.Append("&effectiveEnd=").Append(Q(NgxTime.RequestDate(unit.End)));
        sb.Append("&includeProjected=").Append(_settings.IncludeProjected ? "true" : "false");

        // Clamped to the server's own ceiling: it accepts a larger value and silently
        // reports 20000 back, so sending more just makes the log lie.
        sb.Append("&pageSize=").Append(Math.Clamp(_settings.IndexPageSize, 1, 20000));

        if (page > 1) sb.Append("&page=").Append(page);

        foreach (var id in ids) sb.Append("&indexId=").Append(id);

        return sb.ToString();
    }

    /// <summary>
    /// Internal so the parser can be exercised against real captured XML without a socket.
    ///
    /// <para><see cref="RecordsSeen"/> counts every record element encountered, including
    /// ones the key check dropped; <see cref="Rows"/> holds only the survivors. Paging
    /// compares the FORMER against <see cref="FullListSize"/> — see
    /// <c>ReadBatchAsync</c>.</para>
    /// </summary>
    internal readonly record struct PageResult(
        List<NgxRow> Rows, bool Truncated, int FullListSize, int RecordsSeen, string? FirstRecordId);

    /// <summary>
    /// Streams one response, collecting both the pagination envelope and the records.
    /// The envelope scalars precede <c>&lt;indexPrices&gt;</c> in document order, so a
    /// single forward pass sees them first.
    /// </summary>
    internal PageResult ParsePage(Stream stream, NgxIndexPriceWorkUnit unit, string url)
    {
        try
        {
            return ParsePageCore(stream, unit, url);
        }
        catch (XmlException ex)
        {
            // The SSO login page carries a <!DOCTYPE html>, which DtdProcessing.Prohibit
            // rejects. Re-thrown as a malformed-response so the operator is told what
            // actually happened rather than being handed an XML complaint about a DTD.
            throw new NgxMalformedResponseException(
                $"NGX answered 200 for {url} but the body is not valid NGX XML ({ex.Message}). " +
                "An HTML login page is the usual cause — check the Basic credentials.");
        }
    }

    private PageResult ParsePageCore(Stream stream, NgxIndexPriceWorkUnit unit, string url)
    {
        var rows = new List<NgxRow>();
        var truncated = false;
        var fullListSize = 0;
        var recordsSeen = 0;
        string? firstRecordId = null;
        var sawRoot = false;

        using var reader = XmlReader.Create(stream, NgxXml.ReaderSettings());

        // ⚠ ReadElementContentAsString and XNode.ReadFrom BOTH consume their element and
        // leave the reader on the NEXT node. Calling Read() after one of them skips that
        // next node — which, in a run of sibling <indexPriceSummary> elements, silently
        // drops every second record. Hence the explicit "did I already advance?" flag
        // instead of a while (reader.Read()) loop.
        while (!reader.EOF)
        {
            if (reader.NodeType != XmlNodeType.Element)
            {
                reader.Read();
                continue;
            }

            switch (reader.LocalName)
            {
                case RootElement:
                    sawRoot = true;
                    reader.Read();
                    break;

                case "truncated":
                    truncated = bool.TryParse(reader.ReadElementContentAsString(), out var t) && t;
                    break;

                case "fullListSize":
                    fullListSize = NgxTime.ParseInt(reader.ReadElementContentAsString()) ?? 0;
                    break;

                case RecordElement:
                    if (XNode.ReadFrom(reader) is XElement element)
                    {
                        recordsSeen++;
                        firstRecordId ??= element.Text("id");
                        var row = ParseRecord(element, unit);
                        if (row is not null) rows.Add(row);
                    }
                    else
                    {
                        reader.Read();
                    }
                    break;

                default:
                    reader.Read();
                    break;
            }
        }

        if (!sawRoot)
            throw new NgxMalformedResponseException(
                $"NGX answered 200 for {url} but the body had no <{RootElement}> root element. An " +
                "authentication or routing change can return an HTML page with a 200 status, which must " +
                "not be mistaken for an empty result.");

        return new PageResult(rows, truncated, fullListSize, recordsSeen, firstRecordId);
    }

    /// <summary>
    /// One <c>&lt;indexPriceSummary&gt;</c> projected into <c>arm.IndexPrice</c> column
    /// order. Returns null — dropping the record with a warning — when a primary key
    /// component is missing, rather than merging under a blank key.
    /// </summary>
    private NgxRow? ParseRecord(XElement e, NgxIndexPriceWorkUnit unit)
    {
        var indexId = NgxTime.ParseInt(e.Text("index", "id"));
        var pes = NgxTime.ParseDate(e.Text("priceEffectiveStart"));
        var pee = NgxTime.ParseDate(e.Text("priceEffectiveEnd"));

        if (indexId is null || pes is null || pee is null)
        {
            Logger.LogWarning(
                "NGX IndexPrice: dropping a record from {Unit} with an incomplete key " +
                "(indexId={IndexId}, priceEffectiveStart={Start}, priceEffectiveEnd={End})",
                unit.DisplayName, e.Text("index", "id"),
                e.Text("priceEffectiveStart"), e.Text("priceEffectiveEnd"));
            return null;
        }

        // quantityTraded is OPTIONAL and arrives whole: amount, unit, contractUnit and
        // totalAmount are present together or not at all (absent on ~45% of rows, which
        // the incumbent's NULL split corroborates).
        var qty = e.Child("quantityTraded");

        // alternateQuantityTraded has NEVER been observed on this endpoint — the four
        // AlternateTraded* columns belong to the crude index endpoint, which shares this
        // table shape. It is read anyway rather than hard-coded to null, so that a
        // vendor who starts emitting it is captured instead of silently discarded;
        // arm.usp_ValidateLoad flags any non-NULL value so the change gets noticed.
        var alt = e.Child("alternateQuantityTraded");

        return new NgxRow
        {
            Values = new object[]
            {
                unit.ExecutionDate.ToDateTime(TimeOnly.MinValue),
                indexId.Value,
                pes.Value.ToDateTime(TimeOnly.MinValue),
                pee.Value.ToDateTime(TimeOnly.MinValue),
                Db(NgxTime.ParseDate(e.Text("sourceDataDeliveryStart"))),
                Db(NgxTime.ParseDate(e.Text("sourceDataDeliveryEnd"))),
                DbStr(_settings.IndexCommodityType),
                DbStr(e.Text("id")),
                DbStr(e.Text("index", "name")),
                Db(NgxTime.ParseAmount(e.Text("price", "amount"))),
                DbStr(e.Text("price", "currency")),
                Db(NgxTime.ParseInt(e.Text("duration"))),
                Db(NgxTime.ParseAmount(qty?.Text("amount"))),
                DbStr(qty?.Text("unit")),
                DbStr(qty?.Text("contractUnit")),
                Db(NgxTime.ParseAmount(qty?.Text("totalAmount"))),
                Db(NgxTime.ParseAmount(alt?.Text("amount"))),
                DbStr(alt?.Text("unit")),
                DbStr(alt?.Text("contractUnit")),
                Db(NgxTime.ParseAmount(alt?.Text("totalAmount"))),
                Db(NgxTime.ParseInt(e.Text("numberOfTrades"))),
                DbStr(e.Text("settlementState")),
                Db(NgxTime.ParseCentralTimestamp(e.Text("lastUpdateDate")))
            }
        };
    }

    private static object Db<T>(T? value) where T : struct =>
        value.HasValue ? value.Value : DBNull.Value;

    private static object Db(DateOnly? value) =>
        value.HasValue ? value.Value.ToDateTime(TimeOnly.MinValue) : DBNull.Value;

    private static object DbStr(string? value) =>
        string.IsNullOrWhiteSpace(value) ? DBNull.Value : value;
}

/// <summary>
/// Reads <c>GET /ngxcs/stripTradingSummaryXml.xml</c> for one date chunk and projects
/// straight into <c>arm.StripTradingSummary</c> row order.
///
/// <para>
/// Simpler than the index feed in one way and riskier in another: there is no
/// pagination to handle, but equally there is NO envelope at all — no
/// <c>truncated</c> flag, no <c>fullListSize</c>, no page size. If the vendor ever caps
/// a response there is nothing in the document to detect it with. That is the reason
/// the window is chunked to a week by default rather than requested a month at a time:
/// a month is 36,430 records and 29 MB, and the only defence against a silent cap is to
/// stay well under whatever it might be. <c>arm.usp_ValidateLoad</c>'s StripEmptyWeekday
/// check is the backstop.
/// </para>
/// <para>
/// <c>tradeEndDate</c> is INCLUSIVE — verified: a request for 1-September to
/// 1-September returns 1,601 trades, all stamped that day.
/// </para>
/// </summary>
internal sealed class NgxStripReader : NgxReaderBase, ISourceReader<NgxStripWorkUnit, NgxRow>
{
    private const string RecordElement = "stripTradingSummary";
    private const string RootElement = "stripTradingSummaries";

    private readonly NgxSettings _settings;

    internal NgxStripReader(HttpClient http, NgxSettings settings, ILogger logger)
        : base(http, logger)
    {
        _settings = settings;
    }

    public async Task<IReadOnlyList<NgxRow>> ReadAsync(
        NgxStripWorkUnit unit, CancellationToken cancellationToken)
    {
        var url =
            $"{_settings.BaseUrl.TrimEnd('/')}/stripTradingSummaryXml.xml" +
            $"?grouping={Q(_settings.StripGrouping)}" +
            $"&tradeStartDate={Q(NgxTime.RequestDate(unit.Start))}" +
            $"&tradeEndDate={Q(NgxTime.RequestDate(unit.End))}";

        List<NgxRow> rows;
        using (var response = await SendAsync(url, cancellationToken).ConfigureAwait(false))
        await using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        {
            rows = ParseDocument(stream, unit, url);
        }

        Logger.LogDebug("NGX StripTradingSummary: {Unit} -> {Rows} row(s)", unit.DisplayName, rows.Count);

        return rows;
    }

    /// <summary>
    /// Streams one response into rows. Internal so the parser can be exercised against
    /// real captured XML without a socket.
    /// </summary>
    internal List<NgxRow> ParseDocument(Stream stream, NgxStripWorkUnit unit, string url)
    {
        try
        {
            return ParseDocumentCore(stream, unit, url);
        }
        catch (XmlException ex)
        {
            throw new NgxMalformedResponseException(
                $"NGX answered 200 for {url} but the body is not valid NGX XML ({ex.Message}). " +
                "An HTML login page is the usual cause — check the Basic credentials.");
        }
    }

    private List<NgxRow> ParseDocumentCore(Stream stream, NgxStripWorkUnit unit, string url)
    {
        var rows = new List<NgxRow>();
        var sawRoot = false;

        using var reader = XmlReader.Create(stream, NgxXml.ReaderSettings());

        // ⚠ See the note in NgxIndexPriceReader.ParsePage: XNode.ReadFrom leaves the
        // reader on the NEXT node, so an unconditional Read() would drop every second
        // record.
        while (!reader.EOF)
        {
            if (reader.NodeType != XmlNodeType.Element)
            {
                reader.Read();
                continue;
            }

            if (string.Equals(reader.LocalName, RootElement, StringComparison.Ordinal))
            {
                sawRoot = true;
                reader.Read();
                continue;
            }

            if (!string.Equals(reader.LocalName, RecordElement, StringComparison.Ordinal))
            {
                reader.Read();
                continue;
            }

            if (XNode.ReadFrom(reader) is XElement element)
            {
                var row = ParseRecord(element, unit);
                if (row is not null) rows.Add(row);
            }
            else
            {
                reader.Read();
            }
        }

        if (!sawRoot)
            throw new NgxMalformedResponseException(
                $"NGX answered 200 for {url} but the body had no <{RootElement}> root element.");

        return rows;
    }

    /// <summary>
    /// One <c>&lt;stripTradingSummary&gt;</c> projected into
    /// <c>arm.StripTradingSummary</c> column order.
    ///
    /// <para>All SEVEN key components are required. <c>ExchangeReference</c> alone is
    /// NOT a key — the vendor recycles it across dates and markets — so a record missing
    /// any of hub, market, strip type, trade time or either delivery date is dropped
    /// rather than merged under a partial key that would collide with a real trade.</para>
    /// </summary>
    private NgxRow? ParseRecord(XElement e, NgxStripWorkUnit unit)
    {
        var tradeDateTime = NgxTime.ParseCentralTimestamp(e.Text("tradeDateTime"));
        var hubId = NgxTime.ParseInt(e.Text("hub", "id"));
        var marketId = NgxTime.ParseInt(e.Text("market", "id"));
        var stripType = e.Text("stripType");
        var exchangeRef = e.Text("exchangeReference");
        var begin = NgxTime.ParseDate(e.Text("beginDate"));
        var end = NgxTime.ParseDate(e.Text("endDate"));

        if (tradeDateTime is null || hubId is null || marketId is null ||
            stripType is null || exchangeRef is null || begin is null || end is null)
        {
            Logger.LogWarning(
                "NGX StripTradingSummary: dropping a record from {Unit} with an incomplete key " +
                "(tradeDateTime={T}, hubId={H}, marketId={M}, stripType={S}, exchangeReference={X}, " +
                "beginDate={B}, endDate={E})",
                unit.DisplayName, e.Text("tradeDateTime"), e.Text("hub", "id"), e.Text("market", "id"),
                stripType, exchangeRef, e.Text("beginDate"), e.Text("endDate"));
            return null;
        }

        var tradedVolume = e.Child("tradedVolume");
        var totalVolume = e.Child("totalVolume");
        var price = e.Child("price");

        return new NgxRow
        {
            Values = new object[]
            {
                tradeDateTime.Value,
                hubId.Value,
                marketId.Value,
                stripType,
                exchangeRef,
                begin.Value.ToDateTime(TimeOnly.MinValue),
                end.Value.ToDateTime(TimeOnly.MinValue),
                DbStr(e.Text("hub", "name")),
                DbStr(e.Text("market", "name")),
                DbStr(e.Text("settlementTitle")),
                Db(NgxTime.ParseBool(e.Text("cleared"))),
                Db(NgxTime.ParseAmount(tradedVolume?.Text("amount"))),
                DbStr(tradedVolume?.Text("unit")),
                Db(NgxTime.ParseAmount(totalVolume?.Text("amount"))),
                DbStr(totalVolume?.Text("unit")),
                Db(NgxTime.ParseAmount(e.Text("totalVolumeInTJ"))),
                Db(NgxTime.ParseAmount(price?.Text("amount"))),
                DbStr(price?.Text("currency")),

                // Absent from every 2026 response sampled and from a 2023 replay, while
                // the incumbent holds values for bilateral broker trades up to 2023.
                // Read rather than assumed, so it starts working the day it reappears.
                DbStr(e.Text("brokerCompanyName")),

                Db(NgxTime.ParseBool(e.Text("requestForQuoteIndicator"))),
                Db(NgxTime.ParseBool(e.Text("includeInIndexIndicator")))
            }
        };
    }

    private static object Db<T>(T? value) where T : struct =>
        value.HasValue ? value.Value : DBNull.Value;

    private static object DbStr(string? value) =>
        string.IsNullOrWhiteSpace(value) ? DBNull.Value : value;
}
