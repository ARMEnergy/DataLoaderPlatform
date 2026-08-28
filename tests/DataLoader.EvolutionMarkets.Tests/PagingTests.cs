using System.Net;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DataLoader.EvolutionMarkets.Tests;

/// <summary>
/// Pins the pager (design §5.5).
///
/// <para><b>Why the pager is a correctness guard and not an optimisation.</b> The response is a bare
/// JSON array — no envelope, no total count, no page count, no next-page link, no <c>Link</c> header
/// — and the vendor caps a single request at 10,000 rows. A read that hit the cap would be
/// <b>indistinguishable from a complete read</b>: no error, no warning, just silently missing prices.
/// Paging until a short page is the only way to know the read actually finished.</para>
///
/// <para>The stop rule is subtle in one specific way, which
/// <see cref="ExactMultipleOfPageSize_FetchesTheFollowingEmptyPage"/> pins: when the row count is an
/// exact multiple of the page size, the last full page looks like "there may be more", so one extra
/// request is required to confirm the end. Optimising that request away would truncate every dataset
/// whose size happens to divide evenly.</para>
/// </summary>
public class PagingTests
{
    private static EvoSettings Settings(int pageSize) => ReaderHarness.Settings(pageSize: pageSize);

    // ---- the URL shape -------------------------------------------------------------------------

    [Fact]
    public async Task FirstPage_RequestsLimitAndOffsetZero()
    {
        var result = await ReaderHarness.RunAsync(
            FakeHttpMessageHandler.Respond(HttpStatusCode.OK, Samples.Empty),
            settings: Settings(500));

        Assert.Single(result.Handler.Requests);
        Assert.Equal(500, result.Handler.Limits[0]);
        Assert.Equal(0, result.Handler.Offsets[0]);
    }

    [Fact]
    public async Task PageUri_CarriesTheWorkUnitPathUnchanged()
    {
        var result = await ReaderHarness.RunAsync(
            FakeHttpMessageHandler.Respond(HttpStatusCode.OK, Samples.Empty),
            ReaderHarness.Unit(new DateOnly(2026, 8, 19)));

        var uri = result.Handler.Requests[0].ToString();
        Assert.Contains("dateFrom=2026-08-19", uri);
        Assert.Contains("dateTo=2026-08-19", uri);
        Assert.Contains("field=" + EvoRequestFields.Csv, uri);
        Assert.StartsWith(ReaderHarness.BaseUrl + "/v1/market-data/history?", uri);
    }

    [Fact]
    public async Task NoUrl_EverCarriesTheApiKey()
    {
        // The credential is a HEADER. If it ever leaked into a query string, every log line and every
        // arm.FileLog.RequestPath row would contain a secret.
        var result = await ReaderHarness.RunAsync(
            FakeHttpMessageHandler.Respond(HttpStatusCode.OK, Samples.LiveSlice));

        Assert.All(result.Handler.Requests,
            u => Assert.DoesNotContain(ReaderHarness.DummyApiKey, u.ToString()));
        Assert.All(result.Log.Messages,
            m => Assert.DoesNotContain(ReaderHarness.DummyApiKey, m));
    }

    // ---- the stop rule -------------------------------------------------------------------------

    [Fact]
    public async Task ShortFirstPage_StopsAfterOneRequest()
    {
        // 5 rows against a page size of 500: obviously the whole result. One request only.
        var result = await ReaderHarness.RunAsync(
            FakeHttpMessageHandler.Respond(HttpStatusCode.OK, Samples.LiveSlice),
            settings: Settings(500));

        Assert.Equal(1, result.Handler.Calls);
        Assert.Equal(5, result.Rows.Count);
        Assert.Equal(1, result.FileLog.Single.PageCount);
    }

    [Fact]
    public async Task FullPage_FollowedByAShortPage_ConcatenatesBoth()
    {
        var handler = FakeHttpMessageHandler.ByCall(n => n switch
        {
            1 => FakeHttpMessageHandler.Json(HttpStatusCode.OK, Samples.Page(10, idSeed: 0)),
            2 => FakeHttpMessageHandler.Json(HttpStatusCode.OK, Samples.Page(4, idSeed: 100)),
            _ => throw new InvalidOperationException("pager asked for an unexpected page " + n)
        });

        var result = await ReaderHarness.RunAsync(handler, settings: Settings(10));

        Assert.Equal(2, result.Handler.Calls);
        Assert.Equal(new[] { 0, 10 }, result.Handler.Offsets);
        Assert.Equal(14, result.Rows.Count);
        Assert.Equal(14, result.FileLog.Single.RowCount);
        Assert.Equal(2, result.FileLog.Single.PageCount);
    }

    [Fact]
    public async Task ThreeFullPages_ThenShort_PagesInOrder()
    {
        var handler = FakeHttpMessageHandler.ByCall(n => n switch
        {
            1 => FakeHttpMessageHandler.Json(HttpStatusCode.OK, Samples.Page(5, 0)),
            2 => FakeHttpMessageHandler.Json(HttpStatusCode.OK, Samples.Page(5, 100)),
            3 => FakeHttpMessageHandler.Json(HttpStatusCode.OK, Samples.Page(5, 200)),
            4 => FakeHttpMessageHandler.Json(HttpStatusCode.OK, Samples.Page(2, 300)),
            _ => throw new InvalidOperationException("unexpected page " + n)
        });

        var result = await ReaderHarness.RunAsync(handler, settings: Settings(5));

        Assert.Equal(new[] { 0, 5, 10, 15 }, result.Handler.Offsets);
        Assert.Equal(17, result.Rows.Count);
    }

    /// <summary>
    /// *** THE SUBTLE ONE. ***
    /// When the row count divides evenly by the page size, the final full page cannot be
    /// distinguished from "there is more", so the pager MUST issue one more request and see the empty
    /// array. Skipping it would silently truncate exactly those datasets.
    /// </summary>
    [Fact]
    public async Task ExactMultipleOfPageSize_FetchesTheFollowingEmptyPage()
    {
        var handler = FakeHttpMessageHandler.ByCall(n => n switch
        {
            1 => FakeHttpMessageHandler.Json(HttpStatusCode.OK, Samples.Page(10, 0)),
            2 => FakeHttpMessageHandler.Json(HttpStatusCode.OK, Samples.Page(10, 100)),
            3 => FakeHttpMessageHandler.Json(HttpStatusCode.OK, "[]"),
            _ => throw new InvalidOperationException("unexpected page " + n)
        });

        var result = await ReaderHarness.RunAsync(handler, settings: Settings(10));

        Assert.Equal(3, result.Handler.Calls);
        Assert.Equal(new[] { 0, 10, 20 }, result.Handler.Offsets);
        Assert.Equal(20, result.Rows.Count);
        Assert.Equal(EvoFileStatus.Success, result.FileLog.Single.Status);
    }

    [Fact]
    public async Task EmptyFirstPage_StopsImmediately()
    {
        var result = await ReaderHarness.RunAsync(
            FakeHttpMessageHandler.Respond(HttpStatusCode.OK, "[]"),
            settings: Settings(10));

        Assert.Equal(1, result.Handler.Calls);
        Assert.Empty(result.Rows);
    }

    // ---- de-duplication across pages -----------------------------------------------------------

    [Fact]
    public async Task DuplicateIdAcrossPages_IsDroppedAndCounted_FirstOccurrenceWins()
    {
        // Defensive: paging was verified collision-free live, but a concurrent vendor-side insert
        // could shift the offset window and re-serve a row. It must never reach the sink twice.
        var handler = FakeHttpMessageHandler.ByCall(n => n switch
        {
            1 => FakeHttpMessageHandler.Json(HttpStatusCode.OK, Samples.Page(3, 0)),
            2 => FakeHttpMessageHandler.Json(HttpStatusCode.OK, Samples.Page(3, 2)),  // ids 2,3,4 -> 2 overlaps
            3 => FakeHttpMessageHandler.Json(HttpStatusCode.OK, "[]"),
            _ => throw new InvalidOperationException("unexpected page " + n)
        });

        var result = await ReaderHarness.RunAsync(handler, settings: Settings(3));

        Assert.Equal(5, result.Rows.Count);                                  // 0,1,2,3,4
        Assert.Equal(5, result.Rows.Select(r => r.MarketDataId).Distinct().Count());
        Assert.Equal(1, result.FileLog.Single.DroppedRowCount);              // the overlap was COUNTED
        Assert.Contains(result.Log.Warnings, m => m.Contains("droppedDuplicateKey=1"));
    }

    // ---- page-size clamping --------------------------------------------------------------------

    [Theory]
    [InlineData(0, 1)]            // below the vendor floor
    [InlineData(-5, 1)]
    [InlineData(20000, 10000)]    // above the vendor ceiling
    [InlineData(5000, 5000)]      // in range, untouched
    public async Task PageSize_IsClampedIntoTheVendorsAcceptedRange(int configured, int expected)
    {
        // Clamped rather than rejected: a mis-set PageSize must not fail every unit with a vendor 400
        // the operator then has to decode.
        var result = await ReaderHarness.RunAsync(
            FakeHttpMessageHandler.Respond(HttpStatusCode.OK, "[]"),
            settings: Settings(configured));

        Assert.Equal(expected, result.Handler.Limits[0]);
    }

    [Fact]
    public void EffectivePageSize_MatchesTheVendorBounds()
    {
        Assert.Equal(10000, EvoSettings.MaxVendorPageSize);

        var reader = new EvoMarketDataSourceReader(
            new HttpClient(), Settings(99999), new FakeEvoFileLog(), new ListLogger());
        Assert.Equal(10000, reader.EffectivePageSize);
    }

    // ---- the runaway guard ---------------------------------------------------------------------

    [Fact]
    public async Task ARunawayPager_StopsAtTheSafetyCap_AndWarnsThatTheResultMayBeIncomplete()
    {
        // Simulates a vendor-side change that makes `offset` a no-op: every page comes back full, so
        // the short-page rule never fires. The cap must stop it AND say the result is suspect —
        // stopping quietly would look like a successful load.
        var handler = FakeHttpMessageHandler.ByCall(_ =>
            FakeHttpMessageHandler.Json(HttpStatusCode.OK, Samples.Page(2, 0)));

        var result = await ReaderHarness.RunAsync(handler, settings: Settings(2));

        Assert.Equal(200, result.Handler.Calls);                    // MaxPagesSafety
        Assert.Contains(result.Log.Warnings, m => m.Contains("safety cap"));
        Assert.Contains(result.Log.Warnings, m => m.Contains("INCOMPLETE"));

        // Every page repeated the same 2 ids, so de-dup collapsed them and counted the rest.
        Assert.Equal(2, result.Rows.Count);
        Assert.True(result.FileLog.Single.DroppedRowCount > 0);
    }

    // ---- a hard status mid-pagination ----------------------------------------------------------

    [Fact]
    public async Task AHardStatusOnALaterPage_FailsTheWholeUnit()
    {
        // A partial read must NOT be silently committed as if complete: the unit fails, the hub row
        // records Failed, and the next run retries the whole date.
        var handler = FakeHttpMessageHandler.ByCall(n => n switch
        {
            1 => FakeHttpMessageHandler.Json(HttpStatusCode.OK, Samples.Page(5, 0)),
            _ => FakeHttpMessageHandler.Json(HttpStatusCode.InternalServerError, "{\"message\":\"Something went wrong.\"}")
        });

        var fileLog = new FakeEvoFileLog();
        var reader = new EvoMarketDataSourceReader(
            handler.NewClient(), Settings(5), fileLog, new ListLogger());

        await Assert.ThrowsAsync<HttpRequestException>(
            () => reader.ReadAsync(ReaderHarness.Unit(), CancellationToken.None));

        Assert.Equal(EvoFileStatus.Failed, fileLog.Single.Status);
        Assert.Equal(500, fileLog.Single.HttpStatus);
    }

    [Fact]
    public async Task A404OnALaterPage_KeepsWhatWasAlreadyRead()
    {
        // 404 is classified as an empty read, so a mid-pagination 404 ends the read rather than
        // failing it. What earlier pages returned is real data and must be kept.
        var handler = FakeHttpMessageHandler.ByCall(n => n switch
        {
            1 => FakeHttpMessageHandler.Json(HttpStatusCode.OK, Samples.Page(5, 0)),
            _ => FakeHttpMessageHandler.Json(HttpStatusCode.NotFound, "")
        });

        var result = await ReaderHarness.RunAsync(handler, settings: Settings(5));

        Assert.Equal(5, result.Rows.Count);
        Assert.Equal(EvoFileStatus.Success, result.FileLog.Single.Status);
        Assert.Contains(result.Log.Warnings, m => m.Contains("unexpected HTTP 404"));
    }

    // ---- dropped records must not end paging early ---------------------------------------------

    /// <summary>
    /// The short-page test must use the count of ARRAY ELEMENTS the vendor sent, not the count of rows
    /// successfully mapped. If a record is dropped (e.g. unkeyable), a full page would otherwise look
    /// short and the pager would stop early, silently losing every later page.
    /// </summary>
    [Fact]
    public async Task ADroppedRecord_DoesNotEndPagingEarly()
    {
        // Page 1 has 3 elements but one is unkeyable -> 2 rows mapped. Element count is still 3 == the
        // page size, so the pager MUST ask for page 2.
        const string pageWithABadRecord = """
            [{"priceId":"00000000-0000-0000-0000-000000000001","ask":1},
             {"market":"no id here"},
             {"priceId":"00000000-0000-0000-0000-000000000003","ask":3}]
            """;

        var handler = FakeHttpMessageHandler.ByCall(n => n switch
        {
            1 => FakeHttpMessageHandler.Json(HttpStatusCode.OK, pageWithABadRecord),
            2 => FakeHttpMessageHandler.Json(HttpStatusCode.OK, Samples.Page(1, 500)),
            _ => throw new InvalidOperationException("unexpected page " + n)
        });

        var result = await ReaderHarness.RunAsync(handler, settings: Settings(3));

        Assert.Equal(2, result.Handler.Calls);                 // it did NOT stop after page 1
        Assert.Equal(3, result.Rows.Count);                    // 2 good from page 1 + 1 from page 2
        Assert.Equal(1, result.FileLog.Single.DroppedRowCount);
        Assert.Contains(result.Log.Warnings, m => m.Contains("droppedNoKey=1"));
    }
}
