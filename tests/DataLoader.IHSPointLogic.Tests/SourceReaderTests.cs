using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DataLoader.IHSPointLogic.Tests;

/// <summary>
/// <see cref="PlSourceReader{TRow}"/> — the single shared HTTP + page + parse + map + FileLog-stamp flow
/// (design §5.2). Covers: BOTH envelope shapes (flat root array AND {PagingInfo,Data} wrapper), 0-based
/// <c>pageIndex</c> paging with the short/empty-page stop, the <c>?</c>-vs-<c>&amp;</c> join, cross-page
/// clone survival, FileLogId stamping, 404 → NotAvailable, 401/403 → throw, and cancellation. HTTP is a
/// fake handler; the FileLog is an in-memory fake — no network, no database.
/// </summary>
public class SourceReaderTests
{
    private const int PageSize = 10000; // the API-fixed page size the reader pages against (design §8)

    private static IHSPointLogicSettings Settings() => new() { BaseUrl = "https://api.connect.ihsmarkit.com" };

    private static PlWorkUnit Unit(string path) => new()
    {
        EndpointId = "Region",
        RequestPath = path,
        KeyValue = "k"
    };

    private static PlSourceReader<RegionRow> RegionReader(FakeHttpMessageHandler handler, IPlFileLog fileLog) =>
        new(handler.NewClient(), Settings(), fileLog, PlDescriptors.Region, RegionRow.From, NullLogger.Instance);

    /// <summary>A flat JSON array page of region elements with ids [start, start+count).</summary>
    private static string FlatRegionPage(int start, int count)
    {
        var sb = new StringBuilder("[");
        for (var i = 0; i < count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"id\":").Append(start + i).Append(",\"name\":\"R").Append(start + i).Append("\"}");
        }
        return sb.Append(']').ToString();
    }

    /// <summary>
    /// A {PagingInfo,Data} wrapper page carrying the given region ids and a fixed page_size/page_count/
    /// total_record_count (so a small page_count=3 with tiny pages exercises the 1-based wrapper pager).
    /// </summary>
    private static string WrapperRegionPage(int pageSize, int pageCount, int totalRecordCount, params int[] ids)
    {
        var data = new StringBuilder("[");
        for (var i = 0; i < ids.Length; i++)
        {
            if (i > 0) data.Append(',');
            data.Append("{\"id\":").Append(ids[i]).Append(",\"name\":\"R").Append(ids[i]).Append("\"}");
        }
        data.Append(']');
        return $"{{\"PagingInfo\":{{\"page_size\":{pageSize},\"page_count\":{pageCount}," +
               $"\"total_record_count\":{totalRecordCount}}},\"Data\":{data}}}";
    }

    // ---------------------------------------------------------------- flat-array parse + FileLogId stamp

    [Fact]
    public async Task Read_FlatArray_MapsRows_StampsFileLogId_UpsertsSuccessOnce()
    {
        var handler = FakeHttpMessageHandler.Respond(HttpStatusCode.OK, FlatRegionPage(1, 3));
        var fileLog = new FakePlFileLog { FileLogIdToReturn = 999 };
        var reader = RegionReader(handler, fileLog);

        var rows = await reader.ReadAsync(Unit("cs/v1/pointlogic/lookup_region"), CancellationToken.None);

        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.Equal(999, r.FileLogId)); // stamped by the reader path
        Assert.Equal(new[] { 1, 2, 3 }, rows.Select(r => r.RegionId).ToArray());

        var call = Assert.Single(fileLog.Calls);
        Assert.Equal("Success", call.Status);
        Assert.Equal(200, call.HttpStatus);
        Assert.Equal(3, call.RowCount);
        Assert.Equal("Region", call.File.Endpoint);
        Assert.Equal("cs/v1/pointlogic/lookup_region", call.RequestPath);
    }

    // ---------------------------------------------------------------- wrapper (PagingInfo/Data) parse

    [Fact]
    public async Task Read_WrapperEnvelope_ParsesDataArray()
    {
        var wrapper = """
        {
            "PagingInfo": { "page_size": 10000, "page_count": 1, "total_record_count": 2 },
            "Data": [
                { "date": "2026-06-19", "wellhead": 124.063469, "totaldemand": 101.473554 },
                { "date": "2026-06-20", "wellhead": 123.876288, "totaldemand": 100.1 }
            ]
        }
        """;
        var handler = FakeHttpMessageHandler.Respond(HttpStatusCode.OK, wrapper);
        var fileLog = new FakePlFileLog { FileLogIdToReturn = 12 };
        var reader = new PlSourceReader<SupplyAndDemandRow>(
            handler.NewClient(), Settings(), fileLog, PlDescriptors.SupplyAndDemand,
            SupplyAndDemandRow.From, NullLogger.Instance);

        var rows = await reader.ReadAsync(Unit("cs/v1/pointlogic/supplyDemand/marketsHistory"), CancellationToken.None);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(12, r.FileLogId));
        Assert.Equal(
            new[] { new DateOnly(2026, 6, 19), new DateOnly(2026, 6, 20) },
            rows.Select(r => r.Date).OrderBy(d => d).ToArray());
    }

    // ---------------------------------------------------------------- paging: full page → next page; short page stops; clones survive

    [Fact]
    public async Task Read_FullPageThenShortPage_Pages0Then1_AccumulatesBoth_ClonesSurvive()
    {
        var handler = FakeHttpMessageHandler.ForRequest(req =>
        {
            var page = FakeHttpMessageHandler.PageIndexOf(req);
            // page 0 = a FULL page (exactly PageSize) → the reader must fetch page 1;
            // page 1 = a SHORT page (3 rows) → the reader stops.
            var body = page == 0 ? FlatRegionPage(1, PageSize) : FlatRegionPage(PageSize + 1, 3);
            return FakeHttpMessageHandler.Json(HttpStatusCode.OK, body);
        });
        var fileLog = new FakePlFileLog { FileLogIdToReturn = 5 };
        var reader = RegionReader(handler, fileLog);

        var rows = await reader.ReadAsync(Unit("cs/v1/pointlogic/lookup_region"), CancellationToken.None);

        // Exactly two page requests, 0-based, in order.
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(0, FakeHttpMessageHandler.PageIndexOf(handler.RequestMessages[0]));
        Assert.Equal(1, FakeHttpMessageHandler.PageIndexOf(handler.RequestMessages[1]));

        // Both pages accumulated (clones from page 0 survived page 0's JsonDocument disposal).
        Assert.Equal(PageSize + 3, rows.Count);
        Assert.Contains(rows, r => r.RegionId == 1);              // a page-0 row
        Assert.Contains(rows, r => r.RegionId == PageSize + 3);   // the last page-1 row
        Assert.Equal(PageSize + 3, Assert.Single(fileLog.Calls).RowCount);
    }

    // ---------------------------------------------------------------- WRAPPER paging is 1-based (regression: page 3 must not be lost)

    /// <summary>
    /// Regression for the data-loss paging bug (live-probed 2026-08-19): the wrapper family
    /// (<c>PagingInfo</c>/<c>Data</c>) is <b>1-based</b>, and <c>pageIndex=0</c> returns the SAME rows as
    /// <c>pageIndex=1</c> (page 1). The reader must keep the index-0 page as page 1, then fetch pages
    /// <c>2..page_count</c> at indices <c>2..page_count</c> (NEVER re-fetch index 1), so no page is dropped.
    /// The old pager assumed 0-based for both shapes and lost the last real page.
    /// </summary>
    [Fact]
    public async Task Read_WrapperEnvelope_Is1Based_KeepsPage1_FetchesPagesTwoThroughCount_NoPageLost()
    {
        // 1-based API quirk: index 0 and index 1 both return page 1 {1,2,3}; page 2 = {4,5,6}; page 3 (short) = {7,8}.
        var handler = FakeHttpMessageHandler.ForRequest(req =>
        {
            var page = FakeHttpMessageHandler.PageIndexOf(req);
            var body = page switch
            {
                0 => WrapperRegionPage(3, 3, 8, 1, 2, 3),   // page 1
                1 => WrapperRegionPage(3, 3, 8, 1, 2, 3),   // page 1 DUPLICATE — the fixed reader must NOT fetch this
                2 => WrapperRegionPage(3, 3, 8, 4, 5, 6),   // page 2
                3 => WrapperRegionPage(3, 3, 8, 7, 8),      // page 3 (short/last)
                _ => WrapperRegionPage(3, 3, 8),            // beyond page_count → empty
            };
            return FakeHttpMessageHandler.Json(HttpStatusCode.OK, body);
        });
        var fileLog = new FakePlFileLog { FileLogIdToReturn = 77 };
        var reader = RegionReader(handler, fileLog);

        var rows = await reader.ReadAsync(Unit("cs/v1/pointlogic/lookup_point"), CancellationToken.None);

        // All of {1..8} present — page 3 kept, nothing dropped.
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6, 7, 8 }, rows.Select(r => r.RegionId).OrderBy(x => x).ToArray());

        // Exactly indices 0,2,3 fetched — index 1 (the page-1 duplicate) is skipped.
        Assert.Equal(new[] { 0, 2, 3 }, handler.RequestMessages.Select(FakeHttpMessageHandler.PageIndexOf).ToArray());

        Assert.Equal(8, Assert.Single(fileLog.Calls).RowCount);
    }

    // ---------------------------------------------------------------- FLAT paging stays 0-based (pages 0,1,2; short last)

    [Fact]
    public async Task Read_FlatArray_Is0Based_ThreePagesDistinct_ShortLast_AllRows()
    {
        var handler = FakeHttpMessageHandler.ForRequest(req =>
        {
            var page = FakeHttpMessageHandler.PageIndexOf(req);
            var body = page switch
            {
                0 => FlatRegionPage(1, PageSize),                 // full
                1 => FlatRegionPage(PageSize + 1, PageSize),      // full, distinct
                2 => FlatRegionPage(2 * PageSize + 1, 3),         // short → stop
                _ => "[]",
            };
            return FakeHttpMessageHandler.Json(HttpStatusCode.OK, body);
        });
        var reader = RegionReader(handler, new FakePlFileLog());

        var rows = await reader.ReadAsync(Unit("cs/v1/plview/retrieve/pointmetadata_withids"), CancellationToken.None);

        Assert.Equal(new[] { 0, 1, 2 }, handler.RequestMessages.Select(FakeHttpMessageHandler.PageIndexOf).ToArray());
        Assert.Equal(2 * PageSize + 3, rows.Count);
        Assert.Contains(rows, r => r.RegionId == 1);                 // first page-0 row
        Assert.Contains(rows, r => r.RegionId == 2 * PageSize + 3);  // last page-2 row
    }

    [Fact]
    public async Task Read_SinglePartialPage_DoesNotRequestASecondPage()
    {
        var handler = FakeHttpMessageHandler.Respond(HttpStatusCode.OK, FlatRegionPage(1, 5)); // < PageSize
        var reader = RegionReader(handler, new FakePlFileLog());

        await reader.ReadAsync(Unit("cs/v1/pointlogic/lookup_region"), CancellationToken.None);

        Assert.Single(handler.Requests); // one page only
    }

    // ---------------------------------------------------------------- ? vs & page-index join

    [Fact]
    public async Task Read_PathWithoutQuery_JoinsPageIndexWithQuestionMark()
    {
        var handler = FakeHttpMessageHandler.Respond(HttpStatusCode.OK, FlatRegionPage(1, 1));
        var reader = RegionReader(handler, new FakePlFileLog());

        await reader.ReadAsync(Unit("cs/v1/pointlogic/lookup_region"), CancellationToken.None);

        Assert.Equal("?pageIndex=0", handler.Requests[0].Query);
    }

    [Fact]
    public async Task Read_PathWithQuery_JoinsPageIndexWithAmpersand()
    {
        var handler = FakeHttpMessageHandler.Respond(HttpStatusCode.OK, FlatRegionPage(1, 1));
        var reader = RegionReader(handler, new FakePlFileLog());

        // A D/E path already carrying a query (?reportDate=…) must append &pageIndex, not a second ?.
        await reader.ReadAsync(Unit("cs/v1/pointlogic/supplyDemand/region/10?reportDate=2026-08-18"), CancellationToken.None);

        Assert.Equal("?reportDate=2026-08-18&pageIndex=0", handler.Requests[0].Query);
    }

    // ---------------------------------------------------------------- 404 / empty → NotAvailable, no throw

    [Fact]
    public async Task Read_404_ReturnsEmpty_UpsertsNotAvailable_NoThrow()
    {
        var handler = FakeHttpMessageHandler.Respond(HttpStatusCode.NotFound, "<html>not found</html>");
        var fileLog = new FakePlFileLog();
        var reader = RegionReader(handler, fileLog);

        var rows = await reader.ReadAsync(Unit("cs/v1/pointlogic/lookup_region"), CancellationToken.None);

        Assert.Empty(rows);
        var call = Assert.Single(fileLog.Calls);
        Assert.Equal("NotAvailable", call.Status);
        Assert.Equal(404, call.HttpStatus);
        Assert.Equal(0, call.RowCount);
    }

    [Fact]
    public async Task Read_200_EmptyArray_ReturnsEmpty_UpsertsNotAvailable()
    {
        var handler = FakeHttpMessageHandler.Respond(HttpStatusCode.OK, "[]");
        var fileLog = new FakePlFileLog();
        var reader = RegionReader(handler, fileLog);

        var rows = await reader.ReadAsync(Unit("cs/v1/pointlogic/lookup_region"), CancellationToken.None);

        Assert.Empty(rows);
        var call = Assert.Single(fileLog.Calls);
        Assert.Equal("NotAvailable", call.Status);
        Assert.Equal(200, call.HttpStatus);
    }

    // ---------------------------------------------------------------- 401/403 → throw + Failed; 5xx → throw

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, 401)]
    [InlineData(HttpStatusCode.Forbidden, 403)]
    [InlineData(HttpStatusCode.InternalServerError, 500)]
    public async Task Read_AuthOr5xx_WritesFailed_ThenThrows(HttpStatusCode status, int expected)
    {
        var handler = FakeHttpMessageHandler.Respond(status, "boom");
        var fileLog = new FakePlFileLog();
        var reader = RegionReader(handler, fileLog);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => reader.ReadAsync(Unit("cs/v1/pointlogic/lookup_region"), CancellationToken.None));

        var call = Assert.Single(fileLog.Calls);
        Assert.Equal("Failed", call.Status);
        Assert.Equal(expected, call.HttpStatus);
        Assert.Equal(0, call.RowCount);
    }

    [Fact]
    public async Task Read_FileLogThrowsOnFailurePath_OriginalHttpErrorNotMasked()
    {
        var handler = FakeHttpMessageHandler.Respond(HttpStatusCode.InternalServerError, "boom");
        var reader = RegionReader(handler, new ThrowingPlFileLog());

        // The best-effort 'Failed' write throws internally, but the ORIGINAL HttpRequestException surfaces.
        await Assert.ThrowsAsync<HttpRequestException>(
            () => reader.ReadAsync(Unit("cs/v1/pointlogic/lookup_region"), CancellationToken.None));
    }

    // ---------------------------------------------------------------- cancellation → rethrow, no spurious hub row

    [Fact]
    public async Task Read_Cancellation_Rethrows_WithNoFileLogWrite()
    {
        var handler = FakeHttpMessageHandler.Respond(HttpStatusCode.OK, FlatRegionPage(1, 3));
        var fileLog = new FakePlFileLog();
        var reader = RegionReader(handler, fileLog);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => reader.ReadAsync(Unit("cs/v1/pointlogic/lookup_region"), cts.Token));

        Assert.Empty(fileLog.Calls); // cancellation must NOT write a hub row
    }

    // ---------------------------------------------------------------- unexpected shape → treated as no data

    [Fact]
    public async Task Read_UnexpectedJsonShape_TreatedAsNoData_NotAvailable()
    {
        // Neither a root array nor a {Data:[...]} wrapper.
        var handler = FakeHttpMessageHandler.Respond(HttpStatusCode.OK, """{ "message": "no data" }""");
        var fileLog = new FakePlFileLog();
        var reader = RegionReader(handler, fileLog);

        var rows = await reader.ReadAsync(Unit("cs/v1/pointlogic/lookup_region"), CancellationToken.None);

        Assert.Empty(rows);
        Assert.Equal("NotAvailable", Assert.Single(fileLog.Calls).Status);
    }
}
