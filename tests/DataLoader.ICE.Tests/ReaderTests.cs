using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DataLoader.ICE.Tests;

/// <summary>
/// The reader end to end, with a scripted HTTP handler: how each classification
/// turns into rows, a FileLog status, an exception, or a cache write.
/// </summary>
public sealed class ReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ice-reader-" + Guid.NewGuid().ToString("N"));
    private static readonly IceFeedDescriptor Gas = IceDescriptors.Find("IceGas")!;
    private static readonly DateOnly TradeDate = new(2026, 8, 28);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best-effort test cleanup */ }
    }

    private sealed record Harness(
        IceSourceReader Reader,
        ScriptedHandler Handler,
        FakeFileLog FileLog,
        FakeAuthenticator Auth,
        IceFileCache Cache);

    private Harness Build(params string[] responses)
    {
        var settings = TestHelpers.Settings(s => s.DownloadDirectory = _root);
        var handler = new ScriptedHandler(responses);
        var fileLog = new FakeFileLog();
        var auth = new FakeAuthenticator();
        var cache = TestHelpers.Cache(settings);

        var reader = new IceSourceReader(
            Gas, new HttpClient(handler), auth, cache, fileLog,
            Options.Create(settings), NullLogger.Instance);

        return new Harness(reader, handler, fileLog, auth, cache);
    }

    private static IceWorkUnit Unit() => new() { Feed = Gas, TradeDate = TradeDate, KeySuffix = ":run=20260901" };

    [Fact]
    public async Task Data_response_yields_rows_and_a_Success_file_log()
    {
        var harness = Build(Samples.IceGas);

        var rows = await harness.Reader.ReadAsync(Unit(), CancellationToken.None);

        Assert.Equal(2, rows.Count);

        var call = Assert.Single(harness.FileLog.Calls);
        Assert.Equal("Success", call.Status);
        Assert.Equal(2, call.RowCount);
        Assert.Equal(0, call.RowsDropped);
        Assert.Null(call.Error);
        Assert.Equal("IceGas", call.File.FeedId);
        Assert.Equal(TradeDate, call.File.TradeDate);
        Assert.Equal("arm.Futures", call.File.TargetTable);
    }

    /// <summary>
    /// The central status-matrix behaviour: a missing file is zero rows and SUCCESS,
    /// so a weekend never fails a run.
    /// </summary>
    [Fact]
    public async Task Missing_file_is_zero_rows_and_does_not_throw()
    {
        var harness = Build(Samples.NoFilesHtml);

        var rows = await harness.Reader.ReadAsync(Unit(), CancellationToken.None);

        Assert.Empty(rows);
        Assert.Equal("NotAvailable", Assert.Single(harness.FileLog.Calls).Status);
    }

    /// <summary>
    /// ⚠ The regression that would be silent data loss: an auth failure must NOT be
    /// recorded as a clean zero-row day. After one refresh and retry it throws.
    /// </summary>
    [Fact]
    public async Task Persistent_auth_failure_throws_and_is_never_recorded_as_success()
    {
        var harness = Build(Samples.SsoLoginHtml, Samples.SsoLoginHtml);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Reader.ReadAsync(Unit(), CancellationToken.None));

        var call = Assert.Single(harness.FileLog.Calls);
        Assert.Equal("AuthExpired", call.Status);
        Assert.Equal(0, call.RowCount);

        Assert.Equal(1, harness.Auth.RefreshCount);   // exactly one refresh, not a loop
    }

    [Fact]
    public async Task Expired_token_is_refreshed_once_and_the_retry_succeeds()
    {
        var harness = Build(Samples.SsoLoginHtml, Samples.IceGas);

        var rows = await harness.Reader.ReadAsync(Unit(), CancellationToken.None);

        Assert.Equal(2, rows.Count);
        Assert.Equal(1, harness.Auth.RefreshCount);
        Assert.Equal(2, harness.Handler.RequestCount);
        Assert.Equal("Success", Assert.Single(harness.FileLog.Calls).Status);
    }

    [Fact]
    public async Task Malformed_response_throws_and_is_recorded_as_Malformed()
    {
        const string renamedHeader =
            "TRADE DATE|HUB|PRODUCT|STRIP|CONTRACT_CODE|CONTRACT TYPE|STRIKE|SETTLEMENT PRICE|NET CHANGE|EXPIRATION DATE|PRODUCT_ID\n";

        var harness = Build(renamedHeader);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Reader.ReadAsync(Unit(), CancellationToken.None));

        Assert.Equal("Malformed", Assert.Single(harness.FileLog.Calls).Status);
    }

    [Fact]
    public async Task Dropped_rows_are_reported_to_the_file_log()
    {
        var options = IceDescriptors.Find("GasOptions")!;
        var settings = TestHelpers.Settings(s => s.DownloadDirectory = _root);
        var fileLog = new FakeFileLog();

        var reader = new IceSourceReader(
            options, new HttpClient(new ScriptedHandler(Samples.GasOptions)), new FakeAuthenticator(),
            TestHelpers.Cache(settings), fileLog, Options.Create(settings), NullLogger.Instance);

        var rows = await reader.ReadAsync(
            new IceWorkUnit { Feed = options, TradeDate = TradeDate }, CancellationToken.None);

        Assert.Single(rows);

        var call = Assert.Single(fileLog.Calls);
        Assert.Equal("Success", call.Status);
        Assert.Equal(1, call.RowCount);
        Assert.Equal(1, call.RowsDropped);   // the blank-strike 'F' row
    }

    // ---------------------------------------------------------------- caching

    [Fact]
    public async Task Data_is_cached_and_the_second_read_makes_no_request()
    {
        var harness = Build(Samples.IceGas);

        await harness.Reader.ReadAsync(Unit(), CancellationToken.None);
        Assert.Equal(1, harness.Handler.RequestCount);
        Assert.True(File.Exists(harness.Cache.PathFor(Gas, TradeDate)));

        // The scripted handler has no second response — a second request would throw.
        var rows = await harness.Reader.ReadAsync(Unit(), CancellationToken.None);

        Assert.Equal(2, rows.Count);
        Assert.Equal(1, harness.Handler.RequestCount);
    }

    /// <summary>
    /// ⚠ Caching a sentinel page would poison that date forever — every later run
    /// would read HTML back and never retry the download.
    /// </summary>
    [Fact]
    public async Task Sentinel_pages_are_never_written_to_the_cache()
    {
        var missing = Build(Samples.NoFilesHtml);
        await missing.Reader.ReadAsync(Unit(), CancellationToken.None);
        Assert.False(File.Exists(missing.Cache.PathFor(Gas, TradeDate)));

        var auth = Build(Samples.SsoLoginHtml, Samples.SsoLoginHtml);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => auth.Reader.ReadAsync(Unit(), CancellationToken.None));
        Assert.False(File.Exists(auth.Cache.PathFor(Gas, TradeDate)));
    }

    /// <summary>
    /// A cached file that no longer matches the expected header (ICE changed a
    /// column) must trigger a re-download rather than be parsed against a stale map.
    /// </summary>
    [Fact]
    public async Task Unusable_cached_file_triggers_a_redownload()
    {
        var harness = Build(Samples.IceGas);

        var path = harness.Cache.PathFor(Gas, TradeDate);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "TRADE DATE|SOMETHING ELSE\n1|2\n");

        var rows = await harness.Reader.ReadAsync(Unit(), CancellationToken.None);

        Assert.Equal(2, rows.Count);
        Assert.Equal(1, harness.Handler.RequestCount);
    }

    /// <summary>The token travels in a Cookie header, never in the URL.</summary>
    [Fact]
    public async Task Token_is_sent_as_the_iceSsoCookie_header()
    {
        var harness = Build(Samples.IceGas);
        await harness.Reader.ReadAsync(Unit(), CancellationToken.None);

        var request = Assert.Single(harness.Handler.Requests);

        Assert.True(request.Headers.TryGetValues("Cookie", out var cookies));
        Assert.Contains("iceSsoCookie=token-0", cookies!.Single());
        Assert.DoesNotContain("token", request.RequestUri!.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Non_200_status_propagates_as_a_failure()
    {
        var settings = TestHelpers.Settings(s => s.DownloadDirectory = _root);
        var fileLog = new FakeFileLog();

        var reader = new IceSourceReader(
            Gas, new HttpClient(new StatusHandler(HttpStatusCode.InternalServerError)), new FakeAuthenticator(),
            TestHelpers.Cache(settings), fileLog, Options.Create(settings), NullLogger.Instance);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => reader.ReadAsync(Unit(), CancellationToken.None));
    }

    // ---------------------------------------------------------------- handlers

    /// <summary>Returns the scripted bodies in order; a request past the end throws.</summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<string> _responses;

        public ScriptedHandler(params string[] responses) => _responses = new Queue<string>(responses);

        public int RequestCount { get; private set; }
        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            RequestCount++;
            Requests.Add(request);

            if (_responses.Count == 0)
                throw new InvalidOperationException("Unexpected extra HTTP request — the cache should have served this.");

            // ICE answers 200 for data, missing files AND auth failures alike.
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(TestHelpers.Bytes(_responses.Dequeue()))
            });
        }
    }

    private sealed class StatusHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;

        public StatusHandler(HttpStatusCode status) => _status = status;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(_status) { Content = new ByteArrayContent(Array.Empty<byte>()) });
    }
}
