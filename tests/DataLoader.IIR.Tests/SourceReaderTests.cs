using System.Globalization;
using System.Net;
using System.Text;
using DataLoader.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DataLoader.IIR.Tests;

/// <summary>
/// <see cref="IirSourceReader{TRow}"/> — the MANDATORY two-step summary→detail pull (design §4). One
/// work unit: STEP 1 pages <c>…/summary?physicalAddressCountryName=…</c> (records seed the id list +
/// lat/long carry-forward map + the id-catalog census side-write), STEP 2 batches the ids (≤50) into
/// <c>…/detail?&lt;idParam&gt;=…</c> calls whose DETAIL records become the fact rows. The fake
/// <see cref="FakeHttpMessageHandler"/> serves BOTH responses discriminated by path (<c>/summary</c> vs
/// <c>/detail</c>); the census is captured by a <see cref="FakeSummarySink"/>. No network, no DB.
/// </summary>
public class SourceReaderTests
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static IirSettings Settings(int summaryPageSize = 1000, int detailBatchSize = 50) => new()
    {
        BaseUrl = "https://apitest.industrialinfo.com",
        Version = "v2.7",
        SummaryPageSize = summaryPageSize,
        DetailBatchSize = detailBatchSize
    };

    private static IirWorkUnit PlantUnit() => new()
    {
        EndpointId = "Plant",
        RunDate = new DateOnly(2026, 8, 20),
        QueryString = "physicalAddressCountryName=U.S.A.",
        KeyValue = "iir:Plant:20260820",
        DisplayNameValue = "IIR Plant 2026-08-20"
    };

    private static IirSourceReader<IirPlantRow> PlantReader(
        FakeHttpMessageHandler handler, IIirFileLog fileLog, FakeSummarySink summarySink,
        IirSettings? settings = null, ILogger? logger = null) =>
        new(handler.NewClient(), settings ?? Settings(), fileLog, IirDescriptors.Plant, IirPlantRow.From,
            summarySink, logger ?? NullLogger.Instance);

    // ---------------------------------------------------------------- request helpers

    private static bool IsSummary(HttpRequestMessage r) => r.RequestUri!.AbsolutePath.Contains("/summary");
    private static bool IsDetail(HttpRequestMessage r) => r.RequestUri!.AbsolutePath.Contains("/detail");

    /// <summary>All repeated values of a query key (e.g. every <c>plantId=</c>), in order.</summary>
    private static int[] IdParamsOf(HttpRequestMessage r, string key)
    {
        var vals = new List<int>();
        foreach (var pair in r.RequestUri!.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            if (kv.Length == 2 && kv[0] == key && int.TryParse(kv[1], out var n)) vals.Add(n);
        }
        return vals.ToArray();
    }

    // ---------------------------------------------------------------- body builders

    private static string SummaryRecord(int id, double? lat, double? lon)
    {
        var sb = new StringBuilder($"{{\"plantId\":{id},\"plantName\":\"S{id}\"");
        if (lat.HasValue) sb.Append($",\"latitude\":{lat.Value.ToString(Inv)}");
        if (lon.HasValue) sb.Append($",\"longitude\":{lon.Value.ToString(Inv)}");
        return sb.Append('}').ToString();
    }

    /// <summary>A summary envelope slice of <paramref name="all"/> at [offset, offset+limit) with totalCount.</summary>
    private static string SummaryBody(IReadOnlyList<(int id, double? lat, double? lon)> all, int offset, int limit)
    {
        var slice = all.Skip(offset).Take(limit).ToList();
        var items = string.Join(",", slice.Select(r => SummaryRecord(r.id, r.lat, r.lon)));
        return $"{{\"limit\":{limit},\"offset\":{offset},\"resultCount\":{slice.Count},\"totalCount\":{all.Count},\"plants\":[{items}]}}";
    }

    /// <summary>A detail envelope echoing one record per requested id (totalCount == count so it stops in one page).</summary>
    private static string DetailBody(HttpRequestMessage req, Func<int, string> record)
    {
        var ids = IdParamsOf(req, "plantId");
        var items = string.Join(",", ids.Select(record));
        return $"{{\"limit\":{ids.Length},\"offset\":0,\"resultCount\":{ids.Length},\"totalCount\":{ids.Length},\"plants\":[{items}]}}";
    }

    // ================================================================ two-step happy path

    [Fact]
    public async Task Read_TwoStep_SummaryPagesThenDetailBatches_FactsFromDetail()
    {
        // 125 summary ids @ SummaryPageSize 50 → 3 summary pages; 125 ids @ DetailBatchSize 50 → 3 detail calls.
        var all = Enumerable.Range(1, 125).Select(i => (id: i, lat: (double?)null, lon: (double?)null)).ToList();
        var handler = FakeHttpMessageHandler.ForRequest(req =>
        {
            if (IsSummary(req))
                return FakeHttpMessageHandler.Json(HttpStatusCode.OK,
                    SummaryBody(all, FakeHttpMessageHandler.QueryIntOf(req, "offset"), 50));
            // DETAIL: the fact fields come from HERE (plantName "D{id}" proves detail, not summary "S{id}").
            return FakeHttpMessageHandler.Json(HttpStatusCode.OK,
                DetailBody(req, id => $"{{\"plantId\":{id},\"plantName\":\"D{id}\"}}"));
        });
        var fileLog = new FakeIirFileLog { FileLogIdToReturn = 777 };
        var census = new FakeSummarySink();

        var rows = await PlantReader(handler, fileLog, census, Settings(summaryPageSize: 50))
            .ReadAsync(PlantUnit(), CancellationToken.None);

        // STEP 1: three summary pages at offsets 0/50/100.
        var summaryReqs = handler.RequestMessages.Where(IsSummary).ToList();
        Assert.Equal(new[] { 0, 50, 100 }, summaryReqs.Select(r => FakeHttpMessageHandler.QueryIntOf(r, "offset")).ToArray());

        // STEP 2: ceil(125/50) = 3 detail calls carrying repeated plantId= keys (50, 50, 25).
        var detailReqs = handler.RequestMessages.Where(IsDetail).ToList();
        Assert.Equal(3, detailReqs.Count);
        Assert.Equal(new[] { 50, 50, 25 }, detailReqs.Select(r => IdParamsOf(r, "plantId").Length).ToArray());
        Assert.Equal(Enumerable.Range(1, 50), IdParamsOf(detailReqs[0], "plantId"));
        Assert.Equal(Enumerable.Range(101, 25), IdParamsOf(detailReqs[2], "plantId"));

        // Facts come from the DETAIL response (125 of them), FileLogId stamped, FileLog Success/125.
        Assert.Equal(125, rows.Count);
        Assert.All(rows, r => Assert.Equal(777, r.FileLogId));
        Assert.All(rows, r => Assert.StartsWith("D", r.PlantName));          // detail-sourced, not "S…"
        Assert.Equal(new[] { 1, 125 }, new[] { rows.Min(r => r.PlantId), rows.Max(r => r.PlantId) });
        var call = Assert.Single(fileLog.Calls);
        Assert.Equal("Success", call.Status);
        Assert.Equal(125, call.RowCount);
    }

    // ================================================================ census side-write

    [Fact]
    public async Task Read_Step1_WritesCensus_OneRowPerId_AndCentralRunDate()
    {
        // Two ids, both carrying summary lat/long (in-memory only — the census TVP is id-only,
        // the coordinates just seed the §6.3 carry-forward map).
        var all = new List<(int id, double? lat, double? lon)>
        {
            (10, 29.76, -95.36),
            (20, 40.71, -74.00)
        };
        var handler = FakeHttpMessageHandler.ForRequest(req =>
            IsSummary(req)
                ? FakeHttpMessageHandler.Json(HttpStatusCode.OK, SummaryBody(all, FakeHttpMessageHandler.QueryIntOf(req, "offset"), 50))
                : FakeHttpMessageHandler.Json(HttpStatusCode.OK, DetailBody(req, id => $"{{\"plantId\":{id}}}")));
        var census = new FakeSummarySink();
        var unit = PlantUnit();

        await PlantReader(handler, new FakeIirFileLog(), census, Settings(summaryPageSize: 50)).ReadAsync(unit, CancellationToken.None);

        Assert.Equal(1, census.WriteCalls);          // side-write happens once, before STEP 2
        Assert.Equal(2, census.Rows.Count);          // one census row per discovered id
        var r10 = Assert.Single(census.Rows, c => c.EntityId == 10);
        Assert.Equal(29.76, r10.Latitude);
        Assert.Equal(-95.36, r10.Longitude);
        Assert.All(census.Rows, c => Assert.Equal(unit.RunDate, c.RunDate)); // scalar RunDate = the Central run date
    }

    // ================================================================ lat/long carry-forward (§6.3)

    private static FakeHttpMessageHandler CarryForwardHandler(double? sLat, double? sLon, double? dLat, double? dLon) =>
        FakeHttpMessageHandler.ForRequest(req =>
        {
            if (IsSummary(req))
                return FakeHttpMessageHandler.Json(HttpStatusCode.OK,
                    SummaryBody(new[] { (42, sLat, sLon) }, FakeHttpMessageHandler.QueryIntOf(req, "offset"), 50));
            return FakeHttpMessageHandler.Json(HttpStatusCode.OK, DetailBody(req, id =>
            {
                var sb = new StringBuilder($"{{\"plantId\":{id},\"plantName\":\"D{id}\"");
                if (dLat.HasValue) sb.Append($",\"latitude\":{dLat.Value.ToString(Inv)}");
                if (dLon.HasValue) sb.Append($",\"longitude\":{dLon.Value.ToString(Inv)}");
                return sb.Append('}').ToString();
            }));
        });

    [Fact]
    public async Task Read_CarryForward_DetailOmitsLatLong_FilledFromSummary()
    {
        var handler = CarryForwardHandler(sLat: 29.76, sLon: -95.36, dLat: null, dLon: null);

        var row = Assert.Single(await PlantReader(handler, new FakeIirFileLog(), new FakeSummarySink(), Settings(summaryPageSize: 50))
            .ReadAsync(PlantUnit(), CancellationToken.None));

        Assert.Equal(29.76, row.Latitude);   // filled from the STEP-1 summary
        Assert.Equal(-95.36, row.Longitude);
    }

    [Fact]
    public async Task Read_CarryForward_DetailHasLatLong_DetailWins()
    {
        var handler = CarryForwardHandler(sLat: 1.0, sLon: 2.0, dLat: 29.76, dLon: -95.36);

        var row = Assert.Single(await PlantReader(handler, new FakeIirFileLog(), new FakeSummarySink(), Settings(summaryPageSize: 50))
            .ReadAsync(PlantUnit(), CancellationToken.None));

        Assert.Equal(29.76, row.Latitude);   // detail wins; the summary values are NOT applied
        Assert.Equal(-95.36, row.Longitude);
    }

    [Fact]
    public async Task Read_CarryForward_NeitherHasLatLong_Null_NoGeographyAttempted()
    {
        var handler = CarryForwardHandler(sLat: null, sLon: null, dLat: null, dLon: null);

        var row = Assert.Single(await PlantReader(handler, new FakeIirFileLog(), new FakeSummarySink(), Settings(summaryPageSize: 50))
            .ReadAsync(PlantUnit(), CancellationToken.None));

        Assert.Null(row.Latitude);
        Assert.Null(row.Longitude);
    }

    [Fact]
    public async Task Read_CarryForward_LandsOnPlantLatLongAliasedProperties_ForUnitFacts()
    {
        // Unit/OfflineEvent expose the interface lat/long as PlantLatitude/PlantLongitude — prove the
        // carry-forward fills those aliased columns.
        var unit = new IirWorkUnit
        {
            EndpointId = "Unit", RunDate = new DateOnly(2026, 8, 20), QueryString = "physicalAddressCountryName=Canada",
            KeyValue = "iir:Unit:20260820", DisplayNameValue = "IIR Unit"
        };
        var handler = FakeHttpMessageHandler.ForRequest(req =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("/summary"))
                return FakeHttpMessageHandler.Json(HttpStatusCode.OK,
                    "{\"limit\":50,\"offset\":0,\"resultCount\":1,\"totalCount\":1,\"units\":[{\"unitId\":7,\"plantLatitude\":51.05,\"plantLongitude\":-114.07}]}");
            // detail omits lat/long → carry-forward from summary
            return FakeHttpMessageHandler.Json(HttpStatusCode.OK,
                "{\"limit\":1,\"offset\":0,\"resultCount\":1,\"totalCount\":1,\"units\":[{\"unitId\":7,\"unitName\":\"D7\"}]}");
        });
        var reader = new IirSourceReader<IirUnitRow>(handler.NewClient(), Settings(summaryPageSize: 50),
            new FakeIirFileLog(), IirDescriptors.Unit, IirUnitRow.From, new FakeSummarySink(), NullLogger.Instance);

        var row = Assert.Single(await reader.ReadAsync(unit, CancellationToken.None));

        Assert.Equal(51.05, row.PlantLatitude);   // aliased PlantLatitude filled via the interface setter
        Assert.Equal(-114.07, row.PlantLongitude);
    }

    // ================================================================ empty STEP 1 → NotAvailable, no STEP 2

    [Fact]
    public async Task Read_EmptyStep1_404_NotAvailable_NoDetailCalls_NoCensus()
    {
        var handler = FakeHttpMessageHandler.ForRequest(req =>
            IsSummary(req)
                ? FakeHttpMessageHandler.Json(HttpStatusCode.NotFound, "<html/>")
                : throw new InvalidOperationException("STEP 2 must not run when STEP 1 is empty"));
        var fileLog = new FakeIirFileLog();
        var census = new FakeSummarySink();

        var rows = await PlantReader(handler, fileLog, census).ReadAsync(PlantUnit(), CancellationToken.None);

        Assert.Empty(rows);
        Assert.DoesNotContain(handler.RequestMessages, IsDetail); // no detail calls
        Assert.Equal(0, census.WriteCalls);                       // no census write
        var call = Assert.Single(fileLog.Calls);
        Assert.Equal("NotAvailable", call.Status);
        Assert.Equal(404, call.HttpStatus);
    }

    [Fact]
    public async Task Read_EmptyStep1_EmptyArray_NotAvailable_NoDetailCalls_NoCensus()
    {
        var handler = FakeHttpMessageHandler.ForRequest(req =>
            IsSummary(req)
                ? FakeHttpMessageHandler.Json(HttpStatusCode.OK, "{\"limit\":50,\"offset\":0,\"resultCount\":0,\"totalCount\":0,\"plants\":[]}")
                : throw new InvalidOperationException("STEP 2 must not run when STEP 1 is empty"));
        var fileLog = new FakeIirFileLog();
        var census = new FakeSummarySink();

        var rows = await PlantReader(handler, fileLog, census, Settings(summaryPageSize: 50)).ReadAsync(PlantUnit(), CancellationToken.None);

        Assert.Empty(rows);
        Assert.DoesNotContain(handler.RequestMessages, IsDetail);
        Assert.Equal(0, census.WriteCalls);
        Assert.Equal("NotAvailable", Assert.Single(fileLog.Calls).Status);
    }

    // ================================================================ per-batch detail 404 → skip, not fail

    [Fact]
    public async Task Read_DetailBatch404_SkipsThatBatch_ContinuesOthers()
    {
        // 3 ids @ batch size 2 → 2 detail calls; make the FIRST detail batch 404 (skipped), second OK.
        var all = Enumerable.Range(1, 3).Select(i => (id: i, lat: (double?)null, lon: (double?)null)).ToList();
        var detailCalls = 0;
        var handler = FakeHttpMessageHandler.ForRequest(req =>
        {
            if (IsSummary(req))
                return FakeHttpMessageHandler.Json(HttpStatusCode.OK, SummaryBody(all, FakeHttpMessageHandler.QueryIntOf(req, "offset"), 50));
            detailCalls++;
            return detailCalls == 1
                ? FakeHttpMessageHandler.Json(HttpStatusCode.NotFound, "<html/>")
                : FakeHttpMessageHandler.Json(HttpStatusCode.OK, DetailBody(req, id => $"{{\"plantId\":{id}}}"));
        });

        var rows = await PlantReader(handler, new FakeIirFileLog(), new FakeSummarySink(), Settings(summaryPageSize: 50, detailBatchSize: 2))
            .ReadAsync(PlantUnit(), CancellationToken.None);

        Assert.Equal(2, detailCalls);       // both batches attempted
        var row = Assert.Single(rows);      // only the second batch's single id (3) mapped
        Assert.Equal(3, row.PlantId);
    }

    // ================================================================ STEP-1 safety cap

    [Fact]
    public async Task Read_Step1_TotalCountAbsent_FullPagesForever_StopsAtSafetyCap_WithWarning()
    {
        // limit 1, every summary page a FULL page of 1 row, no totalCount → only the 5000-page cap stops it.
        var handler = FakeHttpMessageHandler.ForRequest(req =>
        {
            if (IsSummary(req))
            {
                var offset = FakeHttpMessageHandler.QueryIntOf(req, "offset");
                return FakeHttpMessageHandler.Json(HttpStatusCode.OK,
                    $"{{\"limit\":1,\"offset\":{offset},\"resultCount\":1,\"plants\":[{{\"plantId\":{offset + 1}}}]}}");
            }
            // keep STEP 2 cheap — every detail batch is empty
            return FakeHttpMessageHandler.Json(HttpStatusCode.OK, "{\"limit\":50,\"offset\":0,\"resultCount\":0,\"totalCount\":0,\"plants\":[]}");
        });
        var logger = new ListLogger();

        await PlantReader(handler, new FakeIirFileLog(), new FakeSummarySink(), Settings(summaryPageSize: 1), logger)
            .ReadAsync(PlantUnit(), CancellationToken.None);

        Assert.Equal(5000, handler.RequestMessages.Count(IsSummary)); // MaxPagesSafety
        Assert.Contains(logger.OfLevel(LogLevel.Warning), e => e.Message.Contains("safety cap"));
    }

    // ================================================================ tolerant array-key location (STEP 1)

    [Fact]
    public async Task Read_Step1_FindsArrayUnderDescriptorKey_CaseInsensitive()
    {
        var handler = FakeHttpMessageHandler.ForRequest(req =>
            IsSummary(req)
                ? FakeHttpMessageHandler.Json(HttpStatusCode.OK, "{\"limit\":50,\"offset\":0,\"resultCount\":1,\"totalCount\":1,\"Plants\":[{\"plantId\":42}]}")
                : FakeHttpMessageHandler.Json(HttpStatusCode.OK, DetailBody(req, id => $"{{\"plantId\":{id},\"plantName\":\"D{id}\"}}")));

        var row = Assert.Single(await PlantReader(handler, new FakeIirFileLog(), new FakeSummarySink(), Settings(summaryPageSize: 50))
            .ReadAsync(PlantUnit(), CancellationToken.None));
        Assert.Equal(42, row.PlantId);
    }

    [Fact]
    public async Task Read_Step1_FallsBackToFirstArrayProperty_WhenDescriptorKeyAbsent()
    {
        var handler = FakeHttpMessageHandler.ForRequest(req =>
            IsSummary(req)
                ? FakeHttpMessageHandler.Json(HttpStatusCode.OK, "{\"limit\":50,\"offset\":0,\"resultCount\":1,\"totalCount\":1,\"records\":[{\"plantId\":99}]}")
                : FakeHttpMessageHandler.Json(HttpStatusCode.OK, DetailBody(req, id => $"{{\"plantId\":{id},\"plantName\":\"D{id}\"}}")));

        var row = Assert.Single(await PlantReader(handler, new FakeIirFileLog(), new FakeSummarySink(), Settings(summaryPageSize: 50))
            .ReadAsync(PlantUnit(), CancellationToken.None));
        Assert.Equal(99, row.PlantId);
    }

    // ================================================================ 401/403/5xx (STEP 1) → Failed + throw

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, 401)]
    [InlineData(HttpStatusCode.Forbidden, 403)]
    [InlineData(HttpStatusCode.InternalServerError, 500)]
    [InlineData(HttpStatusCode.ServiceUnavailable, 503)]
    public async Task Read_Step1_AuthOr5xx_WritesFailed_WithActualHttpStatus_ThenThrows(HttpStatusCode status, int expected)
    {
        var handler = FakeHttpMessageHandler.Respond(status, "boom"); // STEP-1 summary errors
        var fileLog = new FakeIirFileLog();

        await Assert.ThrowsAsync<HttpRequestException>(
            () => PlantReader(handler, fileLog, new FakeSummarySink()).ReadAsync(PlantUnit(), CancellationToken.None));

        var call = Assert.Single(fileLog.Calls);
        Assert.Equal("Failed", call.Status);
        Assert.Equal(0, call.RowCount);
        Assert.Equal(expected, call.HttpStatus); // status reported via callback before the throw
    }

    [Fact]
    public async Task Read_FileLogThrowsOnFailurePath_OriginalHttpErrorNotMasked()
    {
        var handler = FakeHttpMessageHandler.Respond(HttpStatusCode.InternalServerError, "boom");

        await Assert.ThrowsAsync<HttpRequestException>(
            () => PlantReader(handler, new ThrowingIirFileLog(), new FakeSummarySink()).ReadAsync(PlantUnit(), CancellationToken.None));
    }

    // ================================================================ cancellation → rethrow, no hub row

    [Fact]
    public async Task Read_Cancellation_Rethrows_WithNoFileLogWrite()
    {
        var handler = FakeHttpMessageHandler.ForRequest(req =>
            FakeHttpMessageHandler.Json(HttpStatusCode.OK, "{\"limit\":50,\"offset\":0,\"resultCount\":1,\"totalCount\":1,\"plants\":[{\"plantId\":1}]}"));
        var fileLog = new FakeIirFileLog();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => PlantReader(handler, fileLog, new FakeSummarySink(), Settings(summaryPageSize: 50)).ReadAsync(PlantUnit(), cts.Token));

        Assert.Empty(fileLog.Calls); // cancellation must NOT write a hub row
    }

    // ================================================================ invalid STEP-1 JSON tolerated

    [Fact]
    public async Task Read_Step1_InvalidJson_TreatedAsNoData_NotAvailable_NoDetail()
    {
        var handler = FakeHttpMessageHandler.ForRequest(req =>
            IsSummary(req)
                ? FakeHttpMessageHandler.Json(HttpStatusCode.OK, "<not json>")
                : throw new InvalidOperationException("STEP 2 must not run"));
        var fileLog = new FakeIirFileLog();

        var rows = await PlantReader(handler, fileLog, new FakeSummarySink()).ReadAsync(PlantUnit(), CancellationToken.None);

        Assert.Empty(rows);
        Assert.DoesNotContain(handler.RequestMessages, IsDetail);
        Assert.Equal("NotAvailable", Assert.Single(fileLog.Calls).Status);
    }
}
