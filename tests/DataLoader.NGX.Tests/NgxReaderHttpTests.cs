using System.Globalization;
using System.Net;
using System.Text;
using Xunit;

namespace DataLoader.NGX.Tests;

/// <summary>
/// A stub transport. Records every URL the reader asks for and answers from a
/// caller-supplied function, so the request-building, the status matrix, the paging loop
/// and the 403-narrowing recovery can all be exercised offline.
///
/// <para><b><see cref="MaxRequests"/> is the point of this class as much as the
/// responses are.</b> The index reader pages in a <c>while (true)</c> loop whose only
/// exits are the vendor's own <c>truncated</c> flag, its <c>fullListSize</c> and an
/// empty page. A regression that removed one of those would hang the test run forever
/// rather than fail it; throwing past a request ceiling turns a hang into a
/// diagnosable failure.</para>
/// </summary>
internal sealed class FakeNgxHandler : HttpMessageHandler
{
    private readonly Func<string, int, HttpResponseMessage> _respond;

    internal FakeNgxHandler(Func<string, int, HttpResponseMessage> respond) => _respond = respond;

    /// <summary>Every URL requested, in order.</summary>
    internal List<string> Urls { get; } = new();

    /// <summary>Request ceiling before the handler declares a runaway loop.</summary>
    internal int MaxRequests { get; init; } = 200;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();
        Urls.Add(url);

        if (Urls.Count > MaxRequests)
            throw new InvalidOperationException(
                $"NGX reader issued more than {MaxRequests} requests — the paging loop is not " +
                $"terminating. Last URL: {url}");

        return Task.FromResult(_respond(url, Urls.Count));
    }

    internal static HttpResponseMessage Xml(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new TrackingContent(body, "application/xml")
        };

    internal static HttpResponseMessage Status(HttpStatusCode code, string body = "") =>
        new(code) { Content = new TrackingContent(body, "text/html") };

    /// <summary>
    /// Content that records its own disposal.
    ///
    /// <para><see cref="HttpCompletionOption.ResponseHeadersRead"/> keeps the connection
    /// open until the <see cref="HttpResponseMessage"/> is disposed, so a reader that
    /// hands out a bare stream and forgets the message leaks a connection per request —
    /// worst on the failure paths, which is exactly when a run is issuing the most of
    /// them. Disposing the content is what disposing the message does, so this is the
    /// observable signal.</para>
    /// </summary>
    internal sealed class TrackingContent : StringContent
    {
        internal TrackingContent(string body, string mediaType) : base(body, Encoding.UTF8, mediaType) { }

        internal bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    /// <summary>What an unauthenticated request actually gets: a 302 at the SSO form.</summary>
    internal static HttpResponseMessage Redirect(HttpStatusCode code = HttpStatusCode.Found)
    {
        var response = new HttpResponseMessage(code) { Content = new StringContent(string.Empty) };
        response.Headers.Location = new Uri("https://sso.ice.com/appUserLogin?loginApp=CSEXT");
        return response;
    }
}

/// <summary>
/// The reader half that the parser tests cannot reach: request construction, the HTTP
/// status matrix of docs/apis/NGX.md section 8, the index feed's paging loop, and the
/// 403 batch-narrowing recovery.
///
/// <para>Nothing here opens a socket — every response is served by
/// <see cref="FakeNgxHandler"/>.</para>
/// </summary>
public class NgxReaderHttpTests
{
    private const string Base = "https://ngxclearing.ice.com/ngxcs";

    private static NgxSettings Settings(Action<NgxSettings>? configure = null) =>
        TestHelpers.Settings(s =>
        {
            s.BaseUrl = Base;
            configure?.Invoke(s);
        });

    private static NgxIndexPriceReader IndexReader(FakeNgxHandler handler, NgxSettings? settings = null) =>
        new(new HttpClient(handler), settings ?? Settings(), TestHelpers.Log);

    private static NgxStripReader StripReader(FakeNgxHandler handler, NgxSettings? settings = null) =>
        new(new HttpClient(handler), settings ?? Settings(), TestHelpers.Log);

    // ---------------------------------------------------------------- fixtures

    private static string IndexRecord(int indexId, string date) =>
        $"<indexPriceSummary>" +
        $"<id>{indexId}-{date}</id>" +
        $"<index><id>{indexId}</id><name>Index {indexId}</name></index>" +
        $"<priceEffectiveStart>{date}</priceEffectiveStart>" +
        $"<priceEffectiveEnd>{date}</priceEffectiveEnd>" +
        $"<price><amount>1.0000</amount><currency>CAD</currency></price>" +
        $"<settlementState>Settled</settlementState>" +
        $"</indexPriceSummary>";

    /// <summary>
    /// An index page carrying <paramref name="records"/> records and the envelope.
    ///
    /// <para><paramref name="firstId"/> overrides the LEADING record's <c>&lt;id&gt;</c>.
    /// The pager treats a repeated leading id as proof the server is ignoring the
    /// <c>page</c> parameter, so a test that wants successive pages to look genuinely
    /// distinct has to vary it.</para>
    /// </summary>
    private static string IndexPage(
        bool truncated, int fullListSize, int records, int indexId = 350, string? firstId = null)
    {
        var body = new StringBuilder();
        for (var i = 0; i < records; i++)
        {
            var record = IndexRecord(indexId, new DateOnly(2026, 9, 1).AddDays(i % 28).ToString("yyyy-MM-dd"));

            // ⚠ Replace ONLY the first <id> — the record's own id. The second is
            // <index><id>, a primary key component; clobbering it would make the reader
            // drop the record and quietly change what the test is measuring.
            if (i == 0 && firstId is not null)
                record = new System.Text.RegularExpressions.Regex("<id>[^<]*</id>")
                    .Replace(record, $"<id>{firstId}</id>", 1);

            body.Append(record);
        }

        return
            "<indexPriceList xmlns=\"http://www.ngx.com/Clearing\">" +
            $"<listSize>{records}</listSize>" +
            $"<truncated>{(truncated ? "true" : "false")}</truncated>" +
            "<pageSize>50</pageSize><pageNumber>1</pageNumber>" +
            $"<fullListSize>{fullListSize}</fullListSize>" +
            $"<indexPrices>{body}</indexPrices>" +
            "</indexPriceList>";
    }

    private static string StripDocument(int records)
    {
        var body = new StringBuilder();
        for (var i = 0; i < records; i++)
            body.Append(
                "<stripTradingSummary>" +
                "<hub><id>28</id><name>AB-NIT</name></hub>" +
                "<market><id>1</id><name>NGX Phys, FP (CA/GJ), AB-NIT</name></market>" +
                "<stripType>Yesterday</stripType>" +
                $"<tradeDateTime>2026-09-01T06:37:{i:D2}-06:00</tradeDateTime>" +
                $"<exchangeReference>4800000000{i:D4}</exchangeReference>" +
                "<beginDate>2026-08-31</beginDate><endDate>2026-08-31</endDate>" +
                "<price><amount>1.2000</amount><currency>CAD</currency></price>" +
                "</stripTradingSummary>");

        return $"<stripTradingSummaries xmlns=\"http://www.ngx.com/Clearing\">{body}</stripTradingSummaries>";
    }

    // ========================================================= status matrix ===

    /// <summary>
    /// A 302 is the signature of missing or rejected HTTP Basic credentials. It must
    /// never be followed — the SSO target answers 200 with an HTML login page, which
    /// would parse as "no data today" on every work unit, forever — and the error must
    /// name credentials so the operator is not sent hunting an XML problem.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    public async Task Redirect_IsACredentialError_NotFollowed(HttpStatusCode code)
    {
        var handler = new FakeNgxHandler((_, _) => FakeNgxHandler.Redirect(code));

        var ex = await Assert.ThrowsAsync<NgxMalformedResponseException>(() =>
            StripReader(handler).ReadAsync(TestHelpers.StripUnit(), CancellationToken.None));

        Assert.Contains("credentials", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Basic", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Exactly one request: the redirect was classified, not chased.
        Assert.Single(handler.Urls);
    }

    /// <summary>
    /// A 403 on the strip feed has no entitlement story to recover from — there are no
    /// index ids to narrow — so it must surface rather than be swallowed as empty.
    /// </summary>
    [Fact]
    public async Task Strip_Forbidden_Surfaces()
    {
        var handler = new FakeNgxHandler((_, _) =>
            FakeNgxHandler.Status(HttpStatusCode.Forbidden, "<html>Access Denied</html>"));

        await Assert.ThrowsAsync<NgxForbiddenException>(() =>
            StripReader(handler).ReadAsync(TestHelpers.StripUnit(), CancellationToken.None));

        Assert.Single(handler.Urls);
    }

    /// <summary>
    /// A malformed <c>tradeStartDate</c> answers 500. That is transient-shaped, so it
    /// must arrive as an <see cref="HttpRequestException"/> — the type the retry policy
    /// handles — and not as one of the loader's own terminal exceptions.
    /// </summary>
    [Fact]
    public async Task ServerError_IsAnHttpFailure_SoThePolicyCanRetryIt()
    {
        var handler = new FakeNgxHandler((_, _) =>
            FakeNgxHandler.Status(HttpStatusCode.InternalServerError, "{\"type\":\"about:blank\"}"));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            StripReader(handler).ReadAsync(TestHelpers.StripUnit(), CancellationToken.None));
    }

    /// <summary>
    /// <b>The row that matters.</b> A 200 carrying the ICE SSO login page must not read
    /// as an empty window. Asserted end to end through <c>ReadAsync</c> rather than only
    /// against the parser, because it is the whole read path that has to fail loudly.
    /// </summary>
    [Fact]
    public async Task Html200_IsMalformed_NotAnEmptyRead()
    {
        var handler = new FakeNgxHandler((_, _) => FakeNgxHandler.Xml(Samples.SsoLoginHtml));

        var index = await Assert.ThrowsAsync<NgxMalformedResponseException>(() =>
            IndexReader(handler).ReadAsync(TestHelpers.IndexUnit(), CancellationToken.None));
        Assert.Contains("credentials", index.Message, StringComparison.OrdinalIgnoreCase);

        var strip = await Assert.ThrowsAsync<NgxMalformedResponseException>(() =>
            StripReader(new FakeNgxHandler((_, _) => FakeNgxHandler.Xml(Samples.SsoLoginHtml)))
                .ReadAsync(TestHelpers.StripUnit(), CancellationToken.None));
        Assert.Contains("credentials", strip.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The other half of the same guard: a 200 whose body is well-formed XML but the
    /// WRONG document. <see cref="Samples.SsoLoginHtml"/> trips the XML reader on its
    /// DOCTYPE; this one parses cleanly and must still be rejected on the missing root
    /// element, which is the branch a doctype-free error page would take.
    /// </summary>
    [Fact]
    public async Task WellFormedButWrongDocument_IsMalformed_NotAnEmptyRead()
    {
        const string notOurs = "<html xmlns=\"http://www.w3.org/1999/xhtml\"><body><p>Access Denied</p></body></html>";

        var index = await Assert.ThrowsAsync<NgxMalformedResponseException>(() =>
            IndexReader(new FakeNgxHandler((_, _) => FakeNgxHandler.Xml(notOurs)))
                .ReadAsync(TestHelpers.IndexUnit(), CancellationToken.None));
        Assert.Contains("indexPriceList", index.Message);

        var strip = await Assert.ThrowsAsync<NgxMalformedResponseException>(() =>
            StripReader(new FakeNgxHandler((_, _) => FakeNgxHandler.Xml(notOurs)))
                .ReadAsync(TestHelpers.StripUnit(), CancellationToken.None));
        Assert.Contains("stripTradingSummaries", strip.Message);
    }

    /// <summary>
    /// The contrast case, and the reason the two above have to be exceptions: a genuine
    /// empty window is a clean zero, not a failure. Same status, same content type —
    /// only the root element tells them apart.
    /// </summary>
    [Fact]
    public async Task EmptyWindow_IsACleanZero()
    {
        var index = await IndexReader(new FakeNgxHandler((_, _) => FakeNgxHandler.Xml(Samples.IndexPriceEmptyXml)))
            .ReadAsync(TestHelpers.IndexUnit(), CancellationToken.None);
        Assert.Empty(index);

        var strip = await StripReader(new FakeNgxHandler((_, _) => FakeNgxHandler.Xml(Samples.StripEmptyXml)))
            .ReadAsync(TestHelpers.StripUnit(), CancellationToken.None);
        Assert.Empty(strip);
    }

    // =============================================================== paging ===

    /// <summary>
    /// The vendor's default page is FIFTY rows with a 200 status and a well-formed body;
    /// <c>truncated</c> is the only signal. The reader must follow it to completion.
    ///
    /// <para>Also pins the paging parameter: <c>page</c>. <c>pageNumber</c> is accepted
    /// and SILENTLY IGNORED (it returns page 1 again), so sending it instead would loop
    /// on the first page until <c>fullListSize</c> was reached in duplicates.</para>
    /// </summary>
    [Fact]
    public async Task Pager_FollowsTruncationUntilFullListSizeIsReached()
    {
        // firstId varies per page, as a real paginated result does: successive pages
        // start with different records. (An identical leading id across pages is the
        // signature of a server ignoring `page`, which the reader rejects — see
        // Pager_FailsWhenTheServerIgnoresThePageParameter.)
        var handler = new FakeNgxHandler((_, n) => FakeNgxHandler.Xml(
            n == 1 ? IndexPage(truncated: true, fullListSize: 3, records: 2, firstId: "p1")
                   : IndexPage(truncated: false, fullListSize: 3, records: 1, firstId: "p2")));

        var rows = await IndexReader(handler).ReadAsync(TestHelpers.IndexUnit(), CancellationToken.None);

        Assert.Equal(3, rows.Count);
        Assert.Equal(2, handler.Urls.Count);

        // Page 1 carries no page parameter; page 2 carries page=2, not pageNumber=2.
        Assert.DoesNotContain("&page=", handler.Urls[0]);
        Assert.Contains("&page=2", handler.Urls[1]);
        Assert.All(handler.Urls, u => Assert.DoesNotContain("pageNumber=", u));
    }

    /// <summary>A response that reports itself complete must cost exactly one request.</summary>
    [Fact]
    public async Task Pager_UntruncatedResponse_IsASingleRequest()
    {
        var handler = new FakeNgxHandler((_, _) =>
            FakeNgxHandler.Xml(IndexPage(truncated: false, fullListSize: 2, records: 2)));

        var rows = await IndexReader(handler).ReadAsync(TestHelpers.IndexUnit(), CancellationToken.None);

        Assert.Equal(2, rows.Count);
        Assert.Single(handler.Urls);
    }

    /// <summary>
    /// The belt-and-braces exit. A server that keeps reporting itself truncated while
    /// handing back an empty page would otherwise loop forever: neither
    /// <c>truncated</c> nor <c>fullListSize</c> would ever release the loop. The reader
    /// stops on the empty page and warns.
    /// </summary>
    [Fact]
    public async Task Pager_StopsWhenAPageComesBackEmpty()
    {
        var handler = new FakeNgxHandler((_, n) => FakeNgxHandler.Xml(
            n == 1 ? IndexPage(truncated: true, fullListSize: 10_000, records: 2)
                   : IndexPage(truncated: true, fullListSize: 10_000, records: 0)));

        var rows = await IndexReader(handler).ReadAsync(TestHelpers.IndexUnit(), CancellationToken.None);

        Assert.Equal(2, rows.Count);
        Assert.Equal(2, handler.Urls.Count);
    }

    /// <summary>
    /// The realistic "unreachable <c>fullListSize</c>" case: the vendor ignores the page
    /// parameter and serves page 1 forever (which is exactly what it does for
    /// <c>pageNumber</c>). The target is never truly reached, only accumulated in
    /// duplicates — so the loop MUST still terminate, and in a bounded number of
    /// requests.
    ///
    /// <para>1,237 is the measured <c>fullListSize</c> for ids 1-10 over
    /// 2026-06-01..2027-03-01, here against the vendor's default 50-row page: 25
    /// requests of 50 duplicate rows. The merge is idempotent so the duplicates are
    /// harmless; the assertion is that it ENDS.</para>
    /// </summary>
    [Fact]
    public async Task Pager_FailsWhenTheServerIgnoresThePageParameter()
    {
        // Every page is page 1: same records, same leading record id.
        var handler = new FakeNgxHandler((_, _) =>
            FakeNgxHandler.Xml(IndexPage(truncated: true, fullListSize: 1237, records: 50)))
        {
            MaxRequests = 40
        };

        // Detected on the SECOND page, by its leading record id having already been
        // served. Continuing would pile up duplicates until recordsSeen crossed 1,237
        // and then report a "complete" window whose real tail was never fetched.
        var ex = await Assert.ThrowsAsync<NgxMalformedResponseException>(() =>
            IndexReader(handler, Settings(s => s.IndexPageSize = 50))
                .ReadAsync(TestHelpers.IndexUnit(), CancellationToken.None));

        Assert.Contains("ignoring the 'page' parameter", ex.Message);
        Assert.Equal(2, handler.Urls.Count);
    }

    /// <summary>
    /// The absolute bound, which does not depend on the envelope being self-consistent.
    /// A vendor that reports a large <c>fullListSize</c>, flags itself truncated forever
    /// and keeps serving non-empty pages would otherwise page indefinitely: neither the
    /// flag nor the target ever releases the loop.
    ///
    /// <para>
    /// Reaching the bound is a FAILURE, not a quiet stop. Pages are still non-empty, so
    /// the server has more to give and is simply not paginating the way its envelope
    /// describes — returning what we have would record a SUCCESS for a window we
    /// provably failed to read, and because <c>ExecutionDate</c> leads the target
    /// primary key that day's snapshot would stay permanently short with nothing ever
    /// coming back for it. Failing puts it in the load log, and the hot resume key lets
    /// a re-run fix the same day.
    /// </para>
    /// <para>
    /// The bound is sized from the records the server ACTUALLY serves per page, not from
    /// the <c>pageSize</c> requested — a server honouring a smaller page than asked for
    /// is legitimate and must not be cut off early while it is still making progress.
    /// So the pathology to provoke is a server whose pages SHRINK: 100 records on page
    /// one, then one record per page thereafter, against a claimed 10,000. The bound
    /// sizes to <c>ceil(10000 / 100) + 2 = 102</c> pages, by which point only 201
    /// records have arrived — progress is real but hopeless, pages are never empty, and
    /// neither the flag nor the target will ever release the loop.
    /// </para>
    /// <para>Each page carries a distinct leading id, so this is the bound firing rather
    /// than the repeated-page detector.</para>
    /// </summary>
    [Fact]
    public async Task Pager_FailsWhenTheBoundIsReachedWithPagesStillNonEmpty()
    {
        var page = 0;
        var handler = new FakeNgxHandler((_, _) =>
        {
            page++;
            return FakeNgxHandler.Xml(IndexPage(
                truncated: true, fullListSize: 10_000,
                records: page == 1 ? 100 : 1, firstId: $"page{page}"));
        })
        {
            MaxRequests = 500
        };

        var ex = await Assert.ThrowsAsync<NgxMalformedResponseException>(() =>
            IndexReader(handler).ReadAsync(TestHelpers.IndexUnit(), CancellationToken.None));

        Assert.Contains("not paginating as its envelope describes", ex.Message);
        Assert.Equal(102, handler.Urls.Count);   // ceil(10000 / 100) + 2
    }

    /// <summary>
    /// The reassuring counterpart: a server that honours a SMALLER page than requested
    /// but keeps serving it steadily is legitimate, and must be paged to completion
    /// rather than cut off by a bound sized to the page we asked for.
    ///
    /// <para>2,000 pages of 5 records against a claimed 10,000 — under the original
    /// bound (sized from the requested <c>pageSize</c> of 20,000) this stopped after
    /// three requests with 15 of 10,000 records and reported success.</para>
    /// </summary>
    [Fact]
    public async Task Pager_SmallerPagesThanRequested_ArePagedToCompletion()
    {
        var page = 0;
        var handler = new FakeNgxHandler((_, _) =>
        {
            page++;
            return FakeNgxHandler.Xml(IndexPage(
                truncated: true, fullListSize: 10_000, records: 5, firstId: $"page{page}"));
        })
        {
            MaxRequests = 2500
        };

        var rows = await IndexReader(handler).ReadAsync(TestHelpers.IndexUnit(), CancellationToken.None);

        Assert.Equal(2000, handler.Urls.Count);   // 10,000 / 5
        Assert.Equal(10_000, rows.Count);
    }

    /// <summary>
    /// The contrasting case: an EMPTY page means the server has nothing further to give,
    /// so its <c>fullListSize</c> merely overstated what it would serve. We have
    /// everything obtainable, so this warns and accepts rather than failing the unit on
    /// every run.
    /// </summary>
    [Fact]
    public async Task Pager_EmptyPageWhileStillTruncated_StopsWithoutFailing()
    {
        var page = 0;
        var handler = new FakeNgxHandler((_, _) =>
        {
            page++;
            return FakeNgxHandler.Xml(IndexPage(
                truncated: true, fullListSize: 10_000,
                records: page == 1 ? 5 : 0, firstId: $"page{page}"));
        });

        var rows = await IndexReader(handler).ReadAsync(TestHelpers.IndexUnit(), CancellationToken.None);

        Assert.Equal(5, rows.Count);
        Assert.Equal(2, handler.Urls.Count);
    }

    /// <summary>
    /// <b>Paging progress is counted in RECORDS SEEN, not rows kept.</b> A page whose
    /// records are all dropped by the key check still advanced the vendor's cursor, so
    /// it must not look like the empty page that ends the loop — otherwise a single
    /// malformed record on page 1 would silently abandon the rest of the window.
    /// </summary>
    [Fact]
    public async Task Pager_PageOfDroppedRecords_DoesNotEndTheLoop()
    {
        // Page 1: two records with no index/id at all — parsed, counted, then dropped.
        const string keyless =
            "<indexPriceList xmlns=\"http://www.ngx.com/Clearing\">" +
            "<truncated>true</truncated><fullListSize>4</fullListSize><indexPrices>" +
            "<indexPriceSummary><id>a</id><index><name>no id</name></index>" +
            "<priceEffectiveStart>2026-09-18</priceEffectiveStart>" +
            "<priceEffectiveEnd>2026-09-18</priceEffectiveEnd></indexPriceSummary>" +
            "<indexPriceSummary><id>b</id><index><name>no id</name></index>" +
            "<priceEffectiveStart>2026-09-19</priceEffectiveStart>" +
            "<priceEffectiveEnd>2026-09-19</priceEffectiveEnd></indexPriceSummary>" +
            "</indexPrices></indexPriceList>";

        var handler = new FakeNgxHandler((_, n) => FakeNgxHandler.Xml(
            n == 1 ? keyless : IndexPage(truncated: false, fullListSize: 4, records: 2)));

        var rows = await IndexReader(handler).ReadAsync(TestHelpers.IndexUnit(), CancellationToken.None);

        Assert.Equal(2, handler.Urls.Count);   // page 2 was still fetched
        Assert.Equal(2, rows.Count);           // only the good page contributed rows
    }

    /// <summary>
    /// The response — not just its stream — is disposed on every path.
    /// <c>ResponseHeadersRead</c> holds the connection until the message is disposed, so
    /// a missed dispose leaks one connection per request. Asserted on the success path
    /// and on the two terminal failure paths, since the failure paths are where a run
    /// issues the most requests.
    /// </summary>
    [Fact]
    public async Task Response_IsDisposedOnEveryPath()
    {
        var contents = new List<FakeNgxHandler.TrackingContent>();

        HttpResponseMessage Track(HttpResponseMessage response)
        {
            contents.Add((FakeNgxHandler.TrackingContent)response.Content);
            return response;
        }

        // Success.
        await StripReader(new FakeNgxHandler((_, _) => Track(FakeNgxHandler.Xml(StripDocument(2)))))
            .ReadAsync(TestHelpers.StripUnit(), CancellationToken.None);

        // 403 — terminal, never retried.
        await Assert.ThrowsAsync<NgxForbiddenException>(() =>
            StripReader(new FakeNgxHandler((_, _) =>
                    Track(FakeNgxHandler.Status(HttpStatusCode.Forbidden, "<html>Access Denied</html>"))))
                .ReadAsync(TestHelpers.StripUnit(), CancellationToken.None));

        // 500 — the path that runs hottest when the vendor is unwell.
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            StripReader(new FakeNgxHandler((_, _) =>
                    Track(FakeNgxHandler.Status(HttpStatusCode.InternalServerError))))
                .ReadAsync(TestHelpers.StripUnit(), CancellationToken.None));

        Assert.Equal(3, contents.Count);
        Assert.All(contents, c => Assert.True(c.Disposed, "an HttpResponseMessage was not disposed"));
    }

    /// <summary>
    /// Every record on every page must survive. <c>XNode.ReadFrom</c> leaves the reader
    /// on the NEXT node, so an unconditional <c>Read()</c> in the record loop drops every
    /// second sibling — a bug that halves the load while reporting success.
    /// </summary>
    [Fact]
    public async Task Pager_KeepsEveryRecordOnEveryPage()
    {
        var handler = new FakeNgxHandler((_, n) => FakeNgxHandler.Xml(
            n < 3 ? IndexPage(truncated: true, fullListSize: 15, records: 5, firstId: $"p{n}")
                  : IndexPage(truncated: false, fullListSize: 15, records: 5, firstId: $"p{n}")));

        var rows = await IndexReader(handler).ReadAsync(TestHelpers.IndexUnit(), CancellationToken.None);

        Assert.Equal(15, rows.Count);
        Assert.Equal(3, handler.Urls.Count);
    }

    // ========================================================== entitlement ===

    /// <summary>
    /// <b>All-or-nothing entitlement.</b> A batch naming one index the account cannot
    /// see returns 403 for the WHOLE batch — the entitled ids in it are lost too. The
    /// reader narrows to single-id requests and skips only the id genuinely refused.
    ///
    /// <para>Verified live with exactly this shape: a <c>1,2,3,85</c> batch yields the
    /// rows for 1-3 and one warning naming 85.</para>
    /// </summary>
    [Fact]
    public async Task Forbidden_BatchIsNarrowed_AndOnlyTheRefusedIdIsLost()
    {
        var handler = new FakeNgxHandler((url, _) =>
            url.Contains("indexId=85", StringComparison.Ordinal)
                ? FakeNgxHandler.Status(HttpStatusCode.Forbidden, "<html>Access Denied</html>")
                : FakeNgxHandler.Xml(IndexPage(truncated: false, fullListSize: 1, records: 1)));

        var unit = TestHelpers.IndexUnit(ids: new[] { 1, 2, 3, 85 });
        var rows = await IndexReader(handler).ReadAsync(unit, CancellationToken.None);

        // One row each from 1, 2 and 3; nothing from 85, and no exception.
        Assert.Equal(3, rows.Count);

        // The refused batch, then one request per id.
        Assert.Equal(5, handler.Urls.Count);
        Assert.All(new[] { 1, 2, 3, 85 }, id =>
            Assert.Contains(handler.Urls.Skip(1), u => u.EndsWith($"&indexId={id}", StringComparison.Ordinal)));
    }

    /// <summary>
    /// A single-id request that is refused has nothing left to narrow, so the 403 must
    /// propagate and fail the unit rather than be reported as an empty read.
    /// </summary>
    [Fact]
    public async Task Forbidden_SingleIdUnit_Propagates()
    {
        var handler = new FakeNgxHandler((_, _) =>
            FakeNgxHandler.Status(HttpStatusCode.Forbidden, "<html>Access Denied</html>"));

        await Assert.ThrowsAsync<NgxForbiddenException>(() =>
            IndexReader(handler).ReadAsync(TestHelpers.IndexUnit(ids: new[] { 85 }), CancellationToken.None));

        Assert.Single(handler.Urls);
    }

    /// <summary>
    /// A batch where EVERY id is refused must FAIL the work unit, not return empty.
    ///
    /// <para>
    /// Returning empty would have the pipeline record a successful, legitimately-empty
    /// read under the hot key — so a revoked entitlement or a locked account would be
    /// indistinguishable from "no data in this window", which is exactly the confusion
    /// the status matrix exists to prevent. A PARTIAL refusal is a warning and the
    /// entitled ids still load; a TOTAL one is a failure.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Forbidden_EveryIdRefused_FailsTheUnit()
    {
        var handler = new FakeNgxHandler((_, _) =>
            FakeNgxHandler.Status(HttpStatusCode.Forbidden, "<html>Access Denied</html>"));

        var ex = await Assert.ThrowsAsync<NgxForbiddenException>(() =>
            IndexReader(handler).ReadAsync(
                TestHelpers.IndexUnit(ids: new[] { 85, 86, 87 }), CancellationToken.None));

        Assert.Contains("ALL 3", ex.Message);
        Assert.Equal(4, handler.Urls.Count);   // the batch, then one per id
    }

    // ================================================== request construction ===

    /// <summary>
    /// The index request, element by element: the vendor's inclusive <c>d-MMMM-yyyy</c>
    /// window, <c>includeProjected</c>, an explicit <c>pageSize</c> (without which the
    /// vendor silently serves 50 rows) and one <c>indexId</c> parameter per id.
    /// </summary>
    [Fact]
    public async Task IndexUrl_CarriesTheVendorWindowAndAnExplicitPageSize()
    {
        var handler = new FakeNgxHandler((_, _) =>
            FakeNgxHandler.Xml(IndexPage(truncated: false, fullListSize: 0, records: 0)));

        await IndexReader(handler).ReadAsync(
            TestHelpers.IndexUnit(new DateOnly(2026, 6, 1), new DateOnly(2027, 3, 1), 1, 2, 3),
            CancellationToken.None);

        var url = handler.Urls[0];

        Assert.StartsWith($"{Base}/indexPrice.xml?", url);
        Assert.Contains("effectiveStart=1-June-2026", url);
        Assert.Contains("effectiveEnd=1-March-2027", url);
        Assert.Contains("includeProjected=true", url);
        Assert.Contains("pageSize=20000", url);
        Assert.Contains("&indexId=1&indexId=2&indexId=3", url);
    }

    /// <summary>
    /// <c>pageSize</c> is clamped to the server's own 20,000 ceiling. Asking for more is
    /// accepted and quietly reduced, so sending it only makes the log lie about what was
    /// requested.
    /// </summary>
    [Theory]
    [InlineData(100_000, 20_000)]
    [InlineData(20_000, 20_000)]
    [InlineData(500, 500)]
    [InlineData(0, 1)]
    [InlineData(-10, 1)]
    public async Task IndexUrl_PageSize_IsClampedToTheServerCeiling(int configured, int expected)
    {
        var handler = new FakeNgxHandler((_, _) =>
            FakeNgxHandler.Xml(IndexPage(truncated: false, fullListSize: 0, records: 0)));

        await IndexReader(handler, Settings(s => s.IndexPageSize = configured))
            .ReadAsync(TestHelpers.IndexUnit(), CancellationToken.None);

        Assert.Contains($"pageSize={expected}", handler.Urls[0]);
    }

    [Fact]
    public async Task IndexUrl_IncludeProjectedFalse_IsSentAsFalse()
    {
        var handler = new FakeNgxHandler((_, _) =>
            FakeNgxHandler.Xml(IndexPage(truncated: false, fullListSize: 0, records: 0)));

        await IndexReader(handler, Settings(s => s.IncludeProjected = false))
            .ReadAsync(TestHelpers.IndexUnit(), CancellationToken.None);

        Assert.Contains("includeProjected=false", handler.Urls[0]);
    }

    /// <summary>The strip request: <c>grouping</c> and the INCLUSIVE trade-date window.</summary>
    [Fact]
    public async Task StripUrl_CarriesGroupingAndTheInclusiveWindow()
    {
        var handler = new FakeNgxHandler((_, _) => FakeNgxHandler.Xml(StripDocument(1)));

        await StripReader(handler).ReadAsync(
            TestHelpers.StripUnit(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 7)),
            CancellationToken.None);

        var url = handler.Urls[0];

        Assert.StartsWith($"{Base}/stripTradingSummaryXml.xml?", url);
        Assert.Contains("grouping=Hub", url);
        Assert.Contains("tradeStartDate=1-September-2026", url);
        Assert.Contains("tradeEndDate=7-September-2026", url);
    }

    /// <summary>
    /// <b>The request dates are month-NAME based, so they are culture-sensitive.</b>
    /// Under a French current culture a naive format sends <c>1-septembre-2026</c>,
    /// which the endpoint cannot parse — the index feed answers 200 with a wrong window
    /// and the strip feed answers 500. Asserted on the URL that actually goes out, not
    /// only on the formatter, because the formatter is only right if the reader uses it.
    /// </summary>
    [Theory]
    [InlineData("fr-FR")]
    [InlineData("de-DE")]
    [InlineData("tr-TR")]
    public async Task RequestDates_AreEnglishUnderAnyCurrentCulture(string culture)
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);

            var strip = new FakeNgxHandler((_, _) => FakeNgxHandler.Xml(StripDocument(0)));
            await StripReader(strip).ReadAsync(
                TestHelpers.StripUnit(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 1)),
                CancellationToken.None);

            Assert.Contains("tradeStartDate=1-September-2026", strip.Urls[0]);
            Assert.Contains("tradeEndDate=1-September-2026", strip.Urls[0]);

            var index = new FakeNgxHandler((_, _) =>
                FakeNgxHandler.Xml(IndexPage(truncated: false, fullListSize: 0, records: 0)));
            await IndexReader(index).ReadAsync(
                TestHelpers.IndexUnit(new DateOnly(2026, 9, 1), new DateOnly(2027, 3, 1)),
                CancellationToken.None);

            Assert.Contains("effectiveStart=1-September-2026", index.Urls[0]);
            Assert.Contains("effectiveEnd=1-March-2027", index.Urls[0]);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    /// <summary>
    /// A comma-decimal culture must not turn the vendor's <c>313,100</c> into 313.1 on
    /// the way into a DECIMAL(18,8) column. Asserted through the real parser rather than
    /// the number helper alone.
    /// </summary>
    [Fact]
    public async Task Amounts_AreInvariantUnderACommaDecimalCulture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            var handler = new FakeNgxHandler((_, _) => FakeNgxHandler.Xml(Samples.IndexPriceXml));
            var rows = await IndexReader(handler).ReadAsync(TestHelpers.IndexUnit(), CancellationToken.None);

            var ip = NgxDescriptors.IndexPrice;
            Assert.Equal(313100m, rows[0].Value(ip, "TradedAmount"));
            Assert.Equal(313100m, rows[0].Value(ip, "TradedTotalAmount"));
            Assert.Equal(-0.0046m, rows[0].Value(ip, "PriceAmount"));

            var stripHandler = new FakeNgxHandler((_, _) => FakeNgxHandler.Xml(Samples.StripWinterXml));
            var stripRows = await StripReader(stripHandler)
                .ReadAsync(TestHelpers.StripUnit(), CancellationToken.None);

            var st = NgxDescriptors.StripTradingSummary;
            Assert.Equal(5000m, stripRows[0].Value(st, "TradedVolumeAmount"));
            Assert.Equal(1070000m, stripRows[0].Value(st, "TotalVolumeAmount"));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    /// <summary>A trailing slash on BaseUrl must not produce a double slash in the path.</summary>
    [Fact]
    public async Task BaseUrl_TrailingSlash_IsNormalised()
    {
        var index = new FakeNgxHandler((_, _) =>
            FakeNgxHandler.Xml(IndexPage(truncated: false, fullListSize: 0, records: 0)));
        await IndexReader(index, Settings(s => s.BaseUrl = Base + "/"))
            .ReadAsync(TestHelpers.IndexUnit(), CancellationToken.None);
        Assert.StartsWith($"{Base}/indexPrice.xml?", index.Urls[0]);

        var strip = new FakeNgxHandler((_, _) => FakeNgxHandler.Xml(StripDocument(0)));
        await StripReader(strip, Settings(s => s.BaseUrl = Base + "/"))
            .ReadAsync(TestHelpers.StripUnit(), CancellationToken.None);
        Assert.StartsWith($"{Base}/stripTradingSummaryXml.xml?", strip.Urls[0]);
    }

    /// <summary>
    /// Cancellation is honoured between pages, so a shutdown does not have to wait out a
    /// long paging run.
    /// </summary>
    [Fact]
    public async Task Cancellation_StopsThePager()
    {
        using var cts = new CancellationTokenSource();

        var handler = new FakeNgxHandler((_, n) =>
        {
            if (n == 1) cts.Cancel();
            return FakeNgxHandler.Xml(IndexPage(truncated: true, fullListSize: 10_000, records: 5));
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            IndexReader(handler).ReadAsync(TestHelpers.IndexUnit(), cts.Token));
    }
}
