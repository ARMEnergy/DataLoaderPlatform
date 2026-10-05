using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DataLoader.OilX.Tests;

/// <summary>
/// The status matrix — the repo's <c>resume-key-and-status-matrix-check</c> gate.
///
/// <para>
/// ⚠ The case that makes this loader unusual: <b>an empty day and a bad feed name are
/// BOTH HTTP 422</b>, told apart only by the error message prefix. Treating 422 as a
/// blanket error fails every publication gap in the vendor's history; treating it as a
/// blanket "no data" turns a typo in <c>EnabledFeeds</c> into clean, empty runs forever.
/// Both failure modes are silent, which is why they are pinned here.
/// </para>
/// </summary>
public sealed class OilXManifestTests
{
    private static readonly OilXFeedDescriptor Cargo = TestHelpers.Feed(OilXDescriptors.CargoTracking);
    private static readonly DateOnly Day = new(2026, 9, 30);

    private static (OilXManifestClient Client, StubHandler Handler) Client(
        HttpStatusCode status, string body, Action<OilXSettings>? configure = null)
    {
        var handler = new StubHandler().Enqueue(status, body);
        var http = new HttpClient(handler);
        return (new OilXManifestClient(http, TestHelpers.Settings(configure), NullLogger.Instance), handler);
    }

    // ------------------------------------------------- the two 422 meanings

    /// <summary>422 "No data for files" — a publication gap. A LEGITIMATE EMPTY READ.</summary>
    [Fact]
    public async Task Http_422_no_data_is_an_empty_day_not_a_failure()
    {
        var (client, _) = Client(HttpStatusCode.UnprocessableEntity, Samples.ErrorNoData);

        var result = await client.ListAsync(Cargo, Day, CancellationToken.None);

        Assert.True(result.EmptyDay);
        Assert.Empty(result.Files);
    }

    /// <summary>422 "Unavailable files" — the feed name is wrong. MUST FAIL LOUDLY.</summary>
    [Fact]
    public async Task Http_422_unavailable_files_fails_loudly()
    {
        var (client, _) = Client(HttpStatusCode.UnprocessableEntity, Samples.ErrorUnavailable);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ListAsync(Cargo, Day, CancellationToken.None));

        Assert.Contains("does not recognise this feed name", ex.Message);
        Assert.Contains("/csv/list", ex.Message);
    }

    /// <summary>
    /// An unrecognised 422 must NOT be guessed either way. Guessing "empty" would hide a
    /// broken request behind clean, empty runs.
    /// </summary>
    [Fact]
    public async Task An_unrecognised_422_fails_rather_than_being_guessed()
    {
        var (client, _) = Client(HttpStatusCode.UnprocessableEntity,
            """{"error":"Something new the vendor added","success":false}""");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ListAsync(Cargo, Day, CancellationToken.None));

        Assert.Contains("unrecognised HTTP 422", ex.Message);
    }

    // ------------------------------------------------------- the other statuses

    [Fact]
    public async Task Http_401_names_the_param_row_to_check()
    {
        var (client, _) = Client(HttpStatusCode.Unauthorized, Samples.ErrorBadKey);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ListAsync(Cargo, Day, CancellationToken.None));

        Assert.Contains("core.Param", ex.Message);
    }

    [Fact]
    public async Task Http_500_fails()
    {
        var (client, _) = Client(HttpStatusCode.InternalServerError, "upstream exploded");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ListAsync(Cargo, Day, CancellationToken.None));
    }

    [Fact]
    public async Task Success_false_at_http_200_fails()
    {
        var (client, _) = Client(HttpStatusCode.OK, """{"data":[],"success":false,"error":"nope"}""");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ListAsync(Cargo, Day, CancellationToken.None));

        Assert.Contains("success=false", ex.Message);
    }

    /// <summary>
    /// ⚠ The vendor signals an empty day with 422, so a 200 carrying an empty
    /// <c>data[]</c> means the contract moved. Reporting it as an empty day would hide
    /// that.
    /// </summary>
    [Fact]
    public async Task Http_200_with_an_empty_data_array_is_treated_as_a_contract_change()
    {
        var (client, _) = Client(HttpStatusCode.OK, """{"data":[],"success":true}""");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ListAsync(Cargo, Day, CancellationToken.None));

        Assert.Contains("empty data[]", ex.Message);
        Assert.Contains("422", ex.Message);
    }

    [Fact]
    public async Task Malformed_json_fails_with_a_clear_message()
    {
        var (client, _) = Client(HttpStatusCode.OK, "{not json");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ListAsync(Cargo, Day, CancellationToken.None));

        Assert.Contains("not valid JSON", ex.Message);
    }

    // ------------------------------------------------------------- the happy path

    /// <summary>
    /// ⚠ THE ORDERING. A day's snapshots all carry the same in-file RunDate, so they
    /// collide on (RunDate, RowId) and must merge oldest-published-first for the newest
    /// value to win. The sample deliberately arrives NEWEST FIRST.
    /// </summary>
    [Fact]
    public async Task Files_come_back_ordered_oldest_uploaded_first()
    {
        var (client, _) = Client(HttpStatusCode.OK, Samples.ManifestTwoFiles);

        var result = await client.ListAsync(Cargo, Day, CancellationToken.None);

        Assert.False(result.EmptyDay);
        Assert.Equal(
            new[] { "CargoTracking.2026-09-30T03-39.csv", "CargoTracking.2026-09-30T09-23.csv" },
            result.Files.Select(f => f.FileName).ToArray());

        Assert.True(result.Files[0].UploadedAt < result.Files[1].UploadedAt);
    }

    [Fact]
    public async Task File_name_is_parsed_from_the_url_without_its_query_string()
    {
        var (client, _) = Client(HttpStatusCode.OK, Samples.ManifestTwoFiles);

        var result = await client.ListAsync(Cargo, Day, CancellationToken.None);

        Assert.All(result.Files, f => Assert.DoesNotContain("?", f.FileName));
        Assert.All(result.Files, f => Assert.DoesNotContain("Signature", f.FileName));
    }

    [Theory]
    [InlineData("https://oilx-csvs.s3.amazonaws.com/CargoTracking.2026-09-30T03-39.csv?AWSAccessKeyId=A&Signature=B",
        "CargoTracking.2026-09-30T03-39.csv")]
    [InlineData("https://oilx-csvs.s3.amazonaws.com/Regional_Balance.2026-09-30T09-55.csv",
        "Regional_Balance.2026-09-30T09-55.csv")]
    [InlineData("not-a-url", "not-a-url")]
    public void FileNameFrom_handles_the_shapes_the_vendor_sends(string url, string expected)
    {
        Assert.Equal(expected, OilXManifestClient.FileNameFrom(url));
    }

    /// <summary>
    /// The manifest carries no feed field, so a file belonging to another feed is dropped
    /// rather than loaded into the wrong table.
    /// </summary>
    [Fact]
    public async Task A_file_belonging_to_another_feed_is_ignored()
    {
        const string body = """
            {"data":[
              {"uploaded_at":"2026-09-30T09:55:00+00:00",
               "url":"https://oilx-csvs.s3.amazonaws.com/TerminalsWeekly.2026-09-30T09-55.csv?Signature=x"},
              {"uploaded_at":"2026-09-30T09:55:01+00:00",
               "url":"https://oilx-csvs.s3.amazonaws.com/Terminals.2026-09-30T09-55.csv?Signature=x"}
            ],"success":true}
            """;

        var (client, _) = Client(HttpStatusCode.OK, body);

        var result = await client.ListAsync(
            TestHelpers.Feed(OilXDescriptors.Terminals), Day, CancellationToken.None);

        Assert.Single(result.Files);
        Assert.Equal("Terminals.2026-09-30T09-55.csv", result.Files[0].FileName);
    }

    [Fact]
    public async Task A_manifest_with_nothing_for_this_feed_fails_rather_than_loading_nothing()
    {
        const string body = """
            {"data":[{"uploaded_at":"2026-09-30T09:55:00+00:00",
                      "url":"https://oilx-csvs.s3.amazonaws.com/USWeekly.2026-09-30T09-55.csv"}],
             "success":true}
            """;

        var (client, _) = Client(HttpStatusCode.OK, body);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ListAsync(Cargo, Day, CancellationToken.None));

        Assert.Contains("none of them belonging to this feed", ex.Message);
    }

    // ------------------------------------------------------------- the request

    /// <summary>
    /// ⚠ ONE FEED PER REQUEST. With several feeds in one call the API answers 200 and
    /// silently omits any that published nothing, so a missing feed could not be
    /// attributed to its own work unit.
    /// </summary>
    [Fact]
    public async Task Requests_exactly_one_feed_and_one_day()
    {
        var (client, handler) = Client(HttpStatusCode.OK, Samples.ManifestTwoFiles);

        await client.ListAsync(Cargo, Day, CancellationToken.None);

        var uri = Assert.Single(handler.RequestedUris);

        Assert.Contains("files=CargoTracking", uri);
        Assert.DoesNotContain(",", uri[uri.IndexOf("files=", StringComparison.Ordinal)..]);
        Assert.Contains("day=2026-09-30", uri);
        Assert.DoesNotContain("range=", uri);
    }

    /// <summary>The underscore in Regional_Balance must survive escaping, or the API 422s.</summary>
    [Fact]
    public async Task Regional_Balance_is_requested_under_the_vendor_spelling()
    {
        var (client, handler) = Client(HttpStatusCode.UnprocessableEntity, Samples.ErrorNoData);

        await client.ListAsync(TestHelpers.Feed(OilXDescriptors.RegionalBalance), Day, CancellationToken.None);

        Assert.Contains("files=Regional_Balance", Assert.Single(handler.RequestedUris));
    }

    // ---------------------------------------------------------------- redaction

    /// <summary>
    /// 🔒 The api_key is a QUERY PARAMETER here, unlike every other HTTP loader in this
    /// repo, so anything derived from a URL must be scrubbed before it reaches a log.
    /// </summary>
    [Theory]
    [InlineData("https://api.energyaspects.com/oilx/v2/csv/?api_key=b24a305a-65c5&day=2026-09-30")]
    [InlineData("GET https://x/?apikey=SECRET failed")]
    [InlineData("https://oilx-csvs.s3.amazonaws.com/f.csv?AWSAccessKeyId=ASIAU4&Signature=abc%3D")]
    [InlineData("https://oilx-csvs.s3.amazonaws.com/f.csv?x-amz-security-token=IQoJb3JpZ2lu")]
    public void Redact_removes_every_secret_a_url_can_carry(string text)
    {
        var redacted = OilXHttp.Redact(text);

        foreach (var secret in new[] { "b24a305a-65c5", "SECRET", "ASIAU4", "abc%3D", "IQoJb3JpZ2lu" })
            Assert.DoesNotContain(secret, redacted);

        Assert.Contains("***", redacted);
    }

    [Fact]
    public void Redact_keeps_the_surrounding_text_readable()
    {
        var redacted = OilXHttp.Redact(
            "https://api.energyaspects.com/oilx/v2/csv/?api_key=SECRET&day=2026-09-30&files=Flows");

        Assert.Contains("day=2026-09-30", redacted);
        Assert.Contains("files=Flows", redacted);
        Assert.Contains("api_key=***", redacted);
    }

    [Fact]
    public void Redact_is_null_safe()
    {
        Assert.Equal(string.Empty, OilXHttp.Redact(null));
        Assert.Equal(string.Empty, OilXHttp.Redact(string.Empty));
    }

    /// <summary>A failure message must never quote the URL it came from.</summary>
    [Fact]
    public async Task Failure_messages_do_not_leak_the_api_key()
    {
        var (client, _) = Client(HttpStatusCode.InternalServerError,
            "boom at https://api.energyaspects.com/oilx/v2/csv/?api_key=LEAKED-KEY&day=2026-09-30");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ListAsync(Cargo, Day, CancellationToken.None));

        Assert.DoesNotContain("LEAKED-KEY", ex.Message);
    }
}
