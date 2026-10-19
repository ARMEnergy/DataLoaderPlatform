using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DataLoader.EIA.Tests;

/// <summary>
/// URL construction and secret redaction.
///
/// <para>
/// Two things here are load-bearing and silent when wrong: a series id is a ROUTE and
/// must keep its slashes, and a <c>local-hourly</c> bound must carry a two-digit zone
/// suffix or the API answers HTTP 500 — a status that looks transient and gets retried
/// to exhaustion.
/// </para>
/// </summary>
public sealed class EiaApiClientTests
{
    private const string FakeKey = "test-key-0123456789";

    private static EiaApiClient Client(bool sortByPeriod = true)
    {
        var settings = new EiaSettings
        {
            ApiKey = FakeKey,
            BaseUrl = "https://api.eia.gov/v2/",
            SortByPeriod = sortByPeriod
        };

        return new EiaApiClient(
            new HttpClient { BaseAddress = new Uri(settings.BaseUrl) },
            Options.Create(settings),
            new EiaRateLimiter(1000),
            NullLogger<EiaApiClient>.Instance);
    }

    // --------------------------------------------------- route escaping

    /// <summary>
    /// A series id such as <c>natural-gas/cons/heat</c> is a path, not a value.
    /// Escaping it whole would produce <c>%2F</c> separators and every request would
    /// 404.
    /// </summary>
    [Theory]
    [InlineData("coal/market-sales-price", "coal/market-sales-price")]
    [InlineData("natural-gas/cons/heat", "natural-gas/cons/heat")]
    [InlineData("electricity/rto/fuel-type-data", "electricity/rto/fuel-type-data")]
    [InlineData("steo", "steo")]
    public void Keeps_the_slashes_in_a_series_route(string seriesId, string expected) =>
        Assert.Equal(expected, EiaApiClient.EscapeRoute(seriesId));

    [Fact]
    public void Escapes_unusual_characters_inside_a_route_segment() =>
        Assert.Equal("a%20b/c%26d", EiaApiClient.EscapeRoute("a b/c&d"));

    // ------------------------------------------------------ metadata URL

    [Fact]
    public void Metadata_url_has_no_data_node()
    {
        var uri = Client().BuildMetadataUri("coal/market-sales-price").OriginalString;

        Assert.StartsWith("coal/market-sales-price?api_key=", uri, StringComparison.Ordinal);
        Assert.DoesNotContain("/data", uri, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------- data URL

    private static string DataUrl(
        string seriesId, string frequencyId, string format,
        string windowStart, string windowEnd,
        string[] dataTypes, bool sortByPeriod = true)
    {
        var types = dataTypes.Select(d => TestHelpers.DataType(d, seriesId)).ToArray();
        var plan = TestHelpers.Plan(
            TestHelpers.Series(seriesId, windowStart, windowEnd, format, frequencyId),
            new[] { TestHelpers.Frequency(frequencyId, format, seriesId) },
            types);

        var unit = TestHelpers.Unit(plan, plan.Frequencies[0], windowStart, windowEnd);

        return Client(sortByPeriod)
            .BuildDataUri(unit, unit.WindowStart, unit.WindowEnd, offset: 0, length: 5000)
            .OriginalString;
    }

    /// <summary>
    /// The shape of the example in the loader request:
    /// <c>.../coal/market-sales-price/data/?frequency=annual&amp;data[0]=price&amp;data[1]=sales&amp;start=…&amp;end=…</c>.
    /// </summary>
    [Fact]
    public void Builds_the_data_url_in_the_documented_shape()
    {
        var url = DataUrl("coal/market-sales-price", "annual", "YYYY", "2020", "2026",
                          new[] { "price", "sales" }, sortByPeriod: false);

        Assert.Equal(
            "coal/market-sales-price/data/?frequency=annual&data[0]=price&data[1]=sales" +
            "&start=2020&end=2026&offset=0&length=5000&api_key=" + FakeKey,
            url);
    }

    [Fact]
    public void Numbers_the_data_parameters_from_zero()
    {
        var url = DataUrl("nuclear-outages/us-nuclear-outages", "daily", "YYYY-MM-DD",
                          "2024-01-01", "2024-12-31",
                          new[] { "capacity", "outage", "percentOutage" });

        Assert.Contains("data[0]=capacity", url, StringComparison.Ordinal);
        Assert.Contains("data[1]=outage", url, StringComparison.Ordinal);
        Assert.Contains("data[2]=percentOutage", url, StringComparison.Ordinal);
    }

    [Fact]
    public void Adds_the_period_sort_when_configured()
    {
        var sorted = DataUrl("coal/market-sales-price", "annual", "YYYY", "2020", "2026",
                             new[] { "price" }, sortByPeriod: true);
        var unsorted = DataUrl("coal/market-sales-price", "annual", "YYYY", "2020", "2026",
                               new[] { "price" }, sortByPeriod: false);

        Assert.Contains("sort[0][column]=period&sort[0][direction]=asc", sorted, StringComparison.Ordinal);
        Assert.DoesNotContain("sort[0]", unsorted, StringComparison.Ordinal);
    }

    /// <summary>
    /// ⚠ The local-hourly trap. Without the <c>-00</c> suffix the API answers HTTP 500
    /// ("Something unexpected happened") — not a 400 — so it reads as transient and
    /// gets retried forever. <c>Z</c> and the four-digit <c>-0400</c> form also 500,
    /// and a raw <c>+</c> decodes as a space in a query string, which 500s too.
    /// </summary>
    [Fact]
    public void Local_hourly_bounds_carry_the_mandatory_zone_suffix()
    {
        var url = DataUrl("electricity/rto/fuel-type-data", "local-hourly", "YYYY-MM-DD\"T\"HH24TZH",
                          "2026-10-01T00", "2026-10-31T23", new[] { "value" });

        Assert.Contains("start=2026-10-01T00-00", url, StringComparison.Ordinal);
        Assert.Contains("end=2026-10-31T23-00", url, StringComparison.Ordinal);
        Assert.DoesNotContain("%2B", url, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Plain_hourly_bounds_carry_no_zone_suffix()
    {
        var url = DataUrl("electricity/rto/fuel-type-data", "hourly", "YYYY-MM-DD\"T\"HH24",
                          "2026-10-01T00", "2026-10-31T23", new[] { "value" });

        Assert.Contains("start=2026-10-01T00&", url, StringComparison.Ordinal);
        Assert.Contains("end=2026-10-31T23&", url, StringComparison.Ordinal);
    }

    [Fact]
    public void Quarterly_bounds_use_the_Q_form()
    {
        var url = DataUrl("international", "quarterly", "YYYY-\"Q\"Q", "2020-Q1", "2026-Q4", new[] { "value" });

        Assert.Contains("start=2020-Q1", url, StringComparison.Ordinal);
        Assert.Contains("end=2026-Q4", url, StringComparison.Ordinal);
    }

    [Fact]
    public void Paging_parameters_appear_on_every_request()
    {
        var types = new[] { TestHelpers.DataType("price") };
        var plan = TestHelpers.Plan(TestHelpers.Series(), new[] { TestHelpers.Frequency() }, types);
        var unit = TestHelpers.Unit(plan, plan.Frequencies[0], "2001", "2010");

        var secondPage = Client()
            .BuildDataUri(unit, unit.WindowStart, unit.WindowEnd, offset: 5000, length: 5000)
            .OriginalString;

        Assert.Contains("offset=5000", secondPage, StringComparison.Ordinal);
        Assert.Contains("length=5000", secondPage, StringComparison.Ordinal);
    }

    // ------------------------------------------------------- redaction

    /// <summary>
    /// 🔒 EIA has no header auth, so the key is in the query string of every request
    /// and a single logged URI leaks the organisation's credential. Anything derived
    /// from an exception goes through <see cref="EiaHttp.Redact"/> first, because
    /// <c>HttpRequestException.Message</c> can carry the URI.
    /// </summary>
    [Fact]
    public void Redacts_the_api_key_out_of_a_message()
    {
        var message = $"Error connecting to https://api.eia.gov/v2/coal/data/?frequency=annual&api_key={FakeKey}";

        var redacted = EiaHttp.Redact(message);

        Assert.DoesNotContain(FakeKey, redacted, StringComparison.Ordinal);
        Assert.Contains("api_key=***", redacted, StringComparison.Ordinal);
        // Surrounding text survives, so the message is still useful.
        Assert.Contains("https://api.eia.gov/v2/coal/data/", redacted, StringComparison.Ordinal);
        Assert.Contains("frequency=annual", redacted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("api_key")]
    [InlineData("API_KEY")]
    [InlineData("apikey")]
    public void Redaction_is_case_insensitive_on_the_parameter_name(string parameterName)
    {
        var redacted = EiaHttp.Redact($"https://api.eia.gov/v2/x?{parameterName}={FakeKey}&length=1");

        Assert.DoesNotContain(FakeKey, redacted, StringComparison.Ordinal);
        Assert.Contains("length=1", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Redaction_tolerates_null_and_empty()
    {
        Assert.Equal(string.Empty, EiaHttp.Redact(null));
        Assert.Equal(string.Empty, EiaHttp.Redact(string.Empty));
    }

    // ------------------------------------------------------ rate limiter

    /// <summary>
    /// 🔒 api.data.gov limits per API KEY and suspends the key when exceeded, which
    /// would stop every other consumer of it. The limiter must actually space requests
    /// out rather than being a no-op.
    /// </summary>
    [Fact]
    public async Task Rate_limiter_spaces_requests_out()
    {
        using var limiter = new EiaRateLimiter(requestsPerSecond: 20);   // 50ms apart
        var started = DateTime.UtcNow;

        for (var i = 0; i < 5; i++)
            await limiter.WaitAsync(CancellationToken.None);

        // Four gaps of ~50ms. Generous lower bound so a loaded CI box does not flake.
        Assert.True((DateTime.UtcNow - started).TotalMilliseconds >= 120,
            "The limiter returned too quickly to have spaced the requests at all.");
    }

    [Fact]
    public async Task Rate_limiter_observes_cancellation()
    {
        using var limiter = new EiaRateLimiter(requestsPerSecond: 0.5);  // 2s apart
        await limiter.WaitAsync(CancellationToken.None);

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => limiter.WaitAsync(cts.Token));
    }
}
