using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DataLoader.Genscape.Tests;

/// <summary>
/// The HTTP reader: the status matrix, the window semantics, and the 5,000-row cap.
/// Nothing here opens a socket — <see cref="StubHandler"/> answers every request.
/// </summary>
public sealed class GenscapeReaderTests
{
    /// <summary>Records every request and answers from a canned response or a callback.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public StubHandler(HttpStatusCode status, string body)
            : this(_ => new HttpResponseMessage(status) { Content = new StringContent(body) }) { }

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        public List<Uri> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(_respond(request));
        }
    }

    /// <summary>One query-string value, without dragging in System.Web.</summary>
    private static string Query(Uri uri, string name) =>
        uri.Query.TrimStart('?').Split('&')
           .Select(pair => pair.Split('=', 2))
           .Where(parts => parts.Length == 2 && parts[0] == name)
           .Select(parts => Uri.UnescapeDataString(parts[1]))
           .Single();

    private static GenscapeSourceReader Build(
        StubHandler handler,
        GenscapeFeedDescriptor? feed = null,
        Action<GenscapeSettings>? configure = null) =>
        new(feed ?? GenscapeDescriptors.All[0], new HttpClient(handler),
            TestHelpers.Settings(configure), NullLogger.Instance);

    private static GenscapeWorkUnit Unit(
        GenscapeFeedDescriptor? feed = null,
        string region = "NorthAmerica",
        string start = "2026-08-09",
        string end = "2026-09-08") => new()
    {
        Feed = feed ?? GenscapeDescriptors.All[0],
        Region = region,
        Start = DateOnly.Parse(start),
        End = DateOnly.Parse(end)
    };

    private static IReadOnlyList<GenscapeRow> Read(GenscapeSourceReader reader, GenscapeWorkUnit unit) =>
        reader.ReadAsync(unit, CancellationToken.None).GetAwaiter().GetResult();

    // -------------------------------------------------------- window semantics

    /// <summary>
    /// ⚠ THE ONE THAT LOSES DATA SILENTLY. The API's window is
    /// <c>[startDate, endDate)</c> — verified live: asking for
    /// <c>2026-08-14..2026-08-28</c> returns the 14th and the 21st but NOT the 28th. The
    /// reader must therefore send <c>end + 1 day</c>, or every run drops the most recent
    /// report and looks perfectly healthy doing it.
    /// </summary>
    [Fact]
    public void The_request_sends_endDate_one_day_past_the_windows_last_day()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Samples.EmptyJson);

        Read(Build(handler), Unit(start: "2026-08-09", end: "2026-09-08"));

        var uri = Assert.Single(handler.Requests);

        Assert.Equal("2026-08-09", Query(uri, "startDate"));
        Assert.Equal("2026-09-09", Query(uri, "endDate"));      // NOT 2026-09-08
    }

    [Fact]
    public void The_request_carries_the_region_the_revision_and_the_json_format()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Samples.EmptyJson);

        Read(Build(handler, configure: s => s.Revision = "revised"), Unit(region: "GulfCoast"));

        var uri = Assert.Single(handler.Requests);

        Assert.Equal("GulfCoast", Query(uri, "region"));
        Assert.Equal("revised", Query(uri, "revision"));
        Assert.Equal("json", Query(uri, "format"));
        Assert.EndsWith("/crude-storage/weekly", uri.AbsolutePath);
    }

    [Fact]
    public void Each_feed_requests_its_own_endpoint_path()
    {
        foreach (var feed in GenscapeDescriptors.All)
        {
            var handler = new StubHandler(HttpStatusCode.OK, Samples.EmptyJson);

            Read(Build(handler, feed), Unit(feed));

            Assert.EndsWith("/" + feed.EndpointPath, Assert.Single(handler.Requests).AbsolutePath);
        }
    }

    /// <summary>A base URL with a trailing slash must not produce a double slash.</summary>
    [Fact]
    public void A_trailing_slash_on_the_base_url_is_tolerated()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Samples.EmptyJson);

        Read(Build(handler, configure: s => s.BaseUrl = "https://api.genscape.com/oil-fundamentals/v1/"), Unit());

        Assert.Equal("/oil-fundamentals/v1/crude-storage/weekly", Assert.Single(handler.Requests).AbsolutePath);
    }

    /// <summary>
    /// The key travels in a header, so it can never leak through a URL that ends up in a
    /// log line or an exception message.
    /// </summary>
    [Fact]
    public void The_api_key_never_appears_in_the_request_uri()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Samples.EmptyJson);

        Read(Build(handler, configure: s => s.ApiKey = "super-secret-key"), Unit());

        var uri = Assert.Single(handler.Requests);
        Assert.DoesNotContain("super-secret-key", uri.ToString());
        Assert.DoesNotContain("apikey", uri.Query, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------ status matrix

    [Fact]
    public void A_200_with_rows_is_read()
    {
        Assert.Equal(3, Read(Build(new StubHandler(HttpStatusCode.OK, Samples.StorageJson)), Unit()).Count);
    }

    /// <summary>
    /// A 200 with an EMPTY array is a legitimate empty read — a window before the feed's
    /// first report or after the latest published one. It must succeed, not throw:
    /// treating it as an error would fail every backfill unit that predates the history.
    /// </summary>
    [Fact]
    public void A_200_with_an_empty_array_succeeds_with_no_rows()
    {
        Assert.Empty(Read(Build(new StubHandler(HttpStatusCode.OK, Samples.EmptyJson)), Unit()));
    }

    /// <summary>
    /// A 400 is a MALFORMED REQUEST — it will be rejected identically forever, so it
    /// throws rather than retrying, and the <c>invalidParameters</c> reason is carried
    /// into the message so the cause is in the log.
    /// </summary>
    [Theory]
    [InlineData(true, "must be after")]
    [InlineData(false, "region")]
    public void A_400_throws_with_the_vendors_reason_in_the_message(bool reversedWindow, string expectedFragment)
    {
        var body = reversedWindow ? Samples.ReversedWindowProblemJson : Samples.BadRegionProblemJson;

        var ex = Assert.Throws<InvalidOperationException>(
            () => Read(Build(new StubHandler(HttpStatusCode.BadRequest, body)), Unit()));

        Assert.Contains("400", ex.Message);
        Assert.Contains(expectedFragment, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// ⚠ A 401 from this API has an EMPTY BODY, so the exception message is the only
    /// diagnosis anyone gets. It must name the setting and where it resolves from.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public void An_auth_failure_throws_and_names_where_the_key_comes_from(HttpStatusCode status)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => Read(Build(new StubHandler(status, string.Empty)), Unit()));

        Assert.Contains("ApiKey", ex.Message);
        Assert.Contains("core.Param", ex.Message);
    }

    /// <summary>The key must never appear in the exception message either.</summary>
    [Fact]
    public void An_auth_failure_never_echoes_the_key()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => Read(Build(new StubHandler(HttpStatusCode.Unauthorized, string.Empty),
                             configure: s => s.ApiKey = "super-secret-key"), Unit()));

        Assert.DoesNotContain("super-secret-key", ex.Message);
    }

    /// <summary>
    /// A 404 here means a required parameter was dropped as often as it means a wrong
    /// path — the API answers 404 when <c>region</c> is omitted. Either way it is this
    /// loader's bug, so it throws.
    /// </summary>
    [Fact]
    public void A_404_throws_and_explains_that_it_can_mean_a_missing_parameter()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => Read(Build(new StubHandler(HttpStatusCode.NotFound, Samples.MissingParameterJson)), Unit()));

        Assert.Contains("404", ex.Message);
        Assert.Contains("parameter", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A 5xx that survives the retry policy still has to fail the unit rather than be
    /// read as an empty load.
    /// </summary>
    [Fact]
    public void A_500_throws()
    {
        Assert.Throws<HttpRequestException>(
            () => Read(Build(new StubHandler(HttpStatusCode.InternalServerError, "upstream exploded")), Unit()));
    }

    /// <summary>
    /// An HTML error page from a CDN must not be mistaken for an empty payload. This is
    /// the Imperva-in-front-of-the-API failure mode.
    /// </summary>
    [Fact]
    public void A_200_carrying_html_throws_rather_than_loading_nothing()
    {
        Assert.Throws<InvalidOperationException>(
            () => Read(Build(new StubHandler(HttpStatusCode.OK, "<html><body>Request blocked</body></html>")), Unit()));
    }

    // ----------------------------------------------------------- the row cap

    /// <summary>
    /// ⚠ THE SILENT TRUNCATION GUARD. A response AT the cap is not trusted: the API
    /// drops the OLDEST rows without saying so, so the window is split and re-read. The
    /// two halves must tile the original window exactly and must not overlap.
    /// </summary>
    [Fact]
    public void A_response_at_the_cap_is_split_into_two_disjoint_halves()
    {
        var handler = new StubHandler(request =>
        {
            var start = DateOnly.Parse(Query(request.RequestUri!, "startDate"));
            var end = DateOnly.Parse(Query(request.RequestUri!, "endDate"));

            // Only the full window is "too big"; anything narrower answers normally.
            var body = (end.DayNumber - start.DayNumber) > 20
                ? Samples.StorageEnvelope(10, new DateOnly(2026, 9, 4))
                : Samples.StorageEnvelope(2, new DateOnly(2026, 9, 4));

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        });

        var rows = Read(Build(handler, configure: s => s.MaxRowsPerResponse = 10),
                        Unit(start: "2026-08-09", end: "2026-09-08"));

        Assert.Equal(3, handler.Requests.Count);   // the capped one, then its two halves

        var windows = handler.Requests
            .Select(u => (Start: DateOnly.Parse(Query(u, "startDate")), EndExclusive: DateOnly.Parse(Query(u, "endDate"))))
            .ToList();

        // Request 0 is the whole window; 1 and 2 are the halves and must tile it.
        Assert.Equal(windows[0].Start, windows[1].Start);
        Assert.Equal(windows[1].EndExclusive, windows[2].Start);          // disjoint: [a,mid] then [mid+1,b]
        Assert.Equal(windows[0].EndExclusive, windows[2].EndExclusive);

        // Only the two halves' rows are kept — the truncated response is discarded.
        Assert.Equal(4, rows.Count);
    }

    /// <summary>A response BELOW the cap is trusted and costs exactly one request.</summary>
    [Fact]
    public void A_response_below_the_cap_is_not_split()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Samples.StorageEnvelope(9, new DateOnly(2026, 9, 4)));

        Assert.Equal(9, Read(Build(handler, configure: s => s.MaxRowsPerResponse = 10), Unit()).Count);
        Assert.Single(handler.Requests);
    }

    /// <summary>
    /// ⚠ A SINGLE DAY that still hits the cap cannot be split further. It must THROW:
    /// the alternative is loading a knowingly truncated window as if it were complete.
    /// </summary>
    [Fact]
    public void A_single_day_still_at_the_cap_throws_rather_than_loading_partial_history()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Samples.StorageEnvelope(10, new DateOnly(2026, 9, 4)));

        var ex = Assert.Throws<InvalidOperationException>(
            () => Read(Build(handler, configure: s => s.MaxRowsPerResponse = 10),
                       Unit(start: "2026-09-08", end: "2026-09-08")));

        Assert.Contains("cannot be split", ex.Message);
        Assert.Contains("truncated", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Bisection must terminate. A source that always answers at the cap has to fail,
    /// not recurse until the stack does.
    /// </summary>
    [Fact]
    public void Bisection_terminates_when_every_window_is_at_the_cap()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Samples.StorageEnvelope(10, new DateOnly(2026, 9, 4)));

        Assert.Throws<InvalidOperationException>(
            () => Read(Build(handler, configure: s => s.MaxRowsPerResponse = 10),
                       Unit(start: "2020-01-01", end: "2026-09-08")));

        // Bounded, not runaway: a 2,400-day window bisects to single days in ~12 levels.
        Assert.InRange(handler.Requests.Count, 1, 20_000);
    }

    /// <summary>
    /// Records dropped for a blank key still occupied a slot in the capped response, so
    /// the cap must be compared against what the API RETURNED, not against what survived
    /// parsing. Otherwise a window full of malformed rows reads as "under the cap" and
    /// its truncation goes unnoticed.
    /// </summary>
    [Fact]
    public void The_cap_is_measured_against_returned_records_not_surviving_rows()
    {
        var junk = string.Join(",", Enumerable.Repeat(
            """{"reportDate":"2026-09-04","region":"","product":"Crude","storageFieldType":"T"}""", 10));

        var handler = new StubHandler(request =>
        {
            var span = DateOnly.Parse(Query(request.RequestUri!, "endDate")).DayNumber
                     - DateOnly.Parse(Query(request.RequestUri!, "startDate")).DayNumber;

            // The full window returns 10 unusable records; the halves return nothing.
            var body = span > 20 ? $$"""{"data":[{{junk}}]}""" : Samples.EmptyJson;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        });

        Assert.Empty(Read(Build(handler, configure: s => s.MaxRowsPerResponse = 10),
                          Unit(start: "2026-08-09", end: "2026-09-08")));

        Assert.Equal(3, handler.Requests.Count);   // it DID notice the cap and split
    }

    /// <summary>
    /// A bisected read must return exactly the rows the halves contain — no duplicates
    /// from the discarded capped response, nothing dropped.
    /// </summary>
    [Fact]
    public void A_bisected_read_keeps_only_the_halves_rows()
    {
        var handler = new StubHandler(request =>
        {
            var start = DateOnly.Parse(Query(request.RequestUri!, "startDate"));
            var end = DateOnly.Parse(Query(request.RequestUri!, "endDate"));

            // Distinct report dates per half, so duplicates would be visible.
            var body = (end.DayNumber - start.DayNumber) > 20
                ? Samples.StorageEnvelope(10, new DateOnly(2026, 9, 4))
                : Samples.StorageEnvelope(3, start.AddDays(1));

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        });

        var rows = Read(Build(handler, configure: s => s.MaxRowsPerResponse = 10),
                        Unit(start: "2026-08-09", end: "2026-09-08"));

        Assert.Equal(6, rows.Count);   // 3 + 3, and none of the discarded 10
    }
}
