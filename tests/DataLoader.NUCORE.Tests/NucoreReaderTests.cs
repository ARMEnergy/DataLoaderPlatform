using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DataLoader.NUCORE.Tests;

/// <summary>
/// The reader against a stubbed transport: paging, the status matrix, the date-echo guard
/// and the watermark stop condition.
/// </summary>
public class NucoreReaderTests
{
    // ------------------------------------------------------------------- the harness

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, (HttpStatusCode Status, string Body)> _respond;
        public List<string> Requests { get; } = new();

        public StubHandler(Func<HttpRequestMessage, (HttpStatusCode, string)> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.ToString());
            var (status, body) = _respond(request);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    private static NucoreSettings Settings(int pageSize = 2, int batchSize = 1000) => new()
    {
        BaseUrl = "https://example.invalid",
        ApiKey = "test",
        PageSize = pageSize,
        BatchSize = batchSize,
        MaxPagesPerUnit = 20
    };

    private static string Envelope(string rows, int returned, int total) =>
        $$"""{"success":true,"recordsReturned":{{returned}},"totalRecords":{{total}},"pageSize":2,"page":1,"totalPages":1,"data":[{{rows}}]}""";

    private static string VolatilitiesRow(string systemDate, string productSpec = "NG", string strike = "3.25") =>
        $$"""
        {"createdAt":"2026-10-14T13:29:54.623437+00:00","modifiedAt":"2026-10-14T13:29:54.623437+00:00",
         "createdByUser":"SYS","modifiedByUser":"SYS","systemDate":"{{systemDate}}","productSpec":"{{productSpec}}",
         "instrumentType":"Gas Option","startTradeMonth":"202701","endTradeMonth":"202701",
         "putCallFlag":"C","strikeType":"A","strikePrice":{{strike}},"volatility":0.44,
         "atmFlag":true,"updatedFlag":false}
        """;

    private static (NucoreSourceReader Reader, StubHandler Handler) Build(
        NucoreEndpoint endpoint, NucoreSettings settings,
        Func<HttpRequestMessage, (HttpStatusCode, string)> respond)
    {
        var handler = new StubHandler(respond);
        var client = new HttpClient(handler);
        return (new NucoreSourceReader(endpoint, client, settings, NullLogger.Instance), handler);
    }

    private static Func<IReadOnlyList<NucoreRow>, CancellationToken, Task<int>> Collect(List<NucoreRow> sink) =>
        (batch, _) => { sink.AddRange(batch); return Task.FromResult(batch.Count); };

    // ------------------------------------------------------------------------ paging

    [Fact]
    public async Task PagesUntilAShortPageEndsTheUnit()
    {
        var endpoint = NucoreDescriptors.Find("Volatilities")!;
        var settings = Settings(pageSize: 2);
        var rows = new List<NucoreRow>();

        var (reader, handler) = Build(endpoint, settings, request =>
        {
            var page = int.Parse(System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)["page"]!);
            return page switch
            {
                1 => (HttpStatusCode.OK, Envelope($"{VolatilitiesRow("2026-10-14T00:00:00", "NG")},{VolatilitiesRow("2026-10-14T00:00:00", "PW")}", 2, 3)),
                2 => (HttpStatusCode.OK, Envelope(VolatilitiesRow("2026-10-14T00:00:00", "CL"), 1, 3)),
                _ => (HttpStatusCode.OK, Envelope(string.Empty, 0, 3))
            };
        });

        var unit = new NucoreWorkUnit
        {
            Endpoint = endpoint, Date = new DateTime(2026, 10, 14), IsHot = true, ResumeKey = "k"
        };

        var result = await reader.ReadAsync(unit, null, Collect(rows), CancellationToken.None);

        Assert.Equal(3, result.RowsRead);
        Assert.Equal(2, result.PagesFetched);   // the short second page ends it
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task AnEmptyResponseIsALegitimateEmptyReadNotAFailure()
    {
        // Weekends, holidays and pre-history dates all answer 200 with zero rows.
        var endpoint = NucoreDescriptors.Find("Volatilities")!;
        var rows = new List<NucoreRow>();

        var (reader, _) = Build(endpoint, Settings(), _ =>
            (HttpStatusCode.OK, """{"success":true,"recordsReturned":0,"totalRecords":0,"pageSize":2,"page":1,"totalPages":0,"data":[]}"""));

        var unit = new NucoreWorkUnit
        {
            Endpoint = endpoint, Date = new DateTime(2026, 10, 11), IsHot = true, ResumeKey = "k"
        };

        var result = await reader.ReadAsync(unit, null, Collect(rows), CancellationToken.None);

        Assert.True(result.EmptyRead);
        Assert.Equal(0, result.RowsRead);
        Assert.Empty(rows);
    }

    // ------------------------------------------------------------ the date-echo guard

    /// <summary>
    /// ⚠ The single most important check in this loader.
    ///
    /// Omitting or misspelling a date parameter returns HTTP 200 with the LATEST day's data,
    /// and two endpoints ignore a parameter that looks correct. Without this guard the
    /// loader would cheerfully write today's numbers under every historical date and the
    /// load log would record 400 successful days.
    /// </summary>
    [Fact]
    public async Task AMismatchedEchoedDateFailsTheUnitLoudly()
    {
        var endpoint = NucoreDescriptors.Find("Volatilities")!;
        var rows = new List<NucoreRow>();

        // Asked for the 14th; the vendor answers with the 15th.
        var (reader, _) = Build(endpoint, Settings(), _ =>
            (HttpStatusCode.OK, Envelope(VolatilitiesRow("2026-10-15T00:00:00"), 1, 1)));

        var unit = new NucoreWorkUnit
        {
            Endpoint = endpoint, Date = new DateTime(2026, 10, 14), IsHot = true, ResumeKey = "k"
        };

        var ex = await Assert.ThrowsAsync<NucoreContractException>(
            () => reader.ReadAsync(unit, null, Collect(rows), CancellationToken.None));

        Assert.Contains("ignored the parameter", ex.Message);
        Assert.Empty(rows);   // nothing was written
    }

    [Fact]
    public async Task AMatchingEchoedDateIsAccepted()
    {
        var endpoint = NucoreDescriptors.Find("Volatilities")!;
        var rows = new List<NucoreRow>();

        var (reader, _) = Build(endpoint, Settings(), _ =>
            (HttpStatusCode.OK, Envelope(VolatilitiesRow("2026-10-14T00:00:00"), 1, 1)));

        var unit = new NucoreWorkUnit
        {
            Endpoint = endpoint, Date = new DateTime(2026, 10, 14), IsHot = true, ResumeKey = "k"
        };

        var result = await reader.ReadAsync(unit, null, Collect(rows), CancellationToken.None);

        Assert.Equal(1, result.RowsRead);
        Assert.Single(rows);
    }

    // ---------------------------------------------------------------- the status matrix

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "X-Api-Key")]
    [InlineData(HttpStatusCode.InternalServerError, "MALFORMED")]
    [InlineData(HttpStatusCode.GatewayTimeout, "PageSize")]
    [InlineData(HttpStatusCode.BadRequest, "SortFields")]
    public async Task EachFailureStatusCarriesItsOwnOperatorHint(HttpStatusCode status, string expectedHint)
    {
        var endpoint = NucoreDescriptors.Find("Volatilities")!;
        var (reader, _) = Build(endpoint, Settings(), _ => (status, """{"error":"x"}"""));

        var unit = new NucoreWorkUnit
        {
            Endpoint = endpoint, Date = new DateTime(2026, 10, 14), IsHot = true, ResumeKey = "k"
        };

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => reader.ReadAsync(unit, null, Collect(new List<NucoreRow>()), CancellationToken.None));

        Assert.Contains(expectedHint, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ASuccessFalseEnvelopeIsRejected()
    {
        var endpoint = NucoreDescriptors.Find("Volatilities")!;
        var (reader, _) = Build(endpoint, Settings(), _ =>
            (HttpStatusCode.OK, """{"success":false,"recordsReturned":0,"totalRecords":0,"data":[]}"""));

        var unit = new NucoreWorkUnit
        {
            Endpoint = endpoint, Date = new DateTime(2026, 10, 14), IsHot = true, ResumeKey = "k"
        };

        await Assert.ThrowsAsync<NucoreContractException>(
            () => reader.ReadAsync(unit, null, Collect(new List<NucoreRow>()), CancellationToken.None));
    }

    [Fact]
    public async Task AMissingDataArrayIsRejected()
    {
        var endpoint = NucoreDescriptors.Find("Volatilities")!;
        var (reader, _) = Build(endpoint, Settings(), _ =>
            (HttpStatusCode.OK, """{"success":true,"recordsReturned":0,"totalRecords":0}"""));

        var unit = new NucoreWorkUnit
        {
            Endpoint = endpoint, Date = new DateTime(2026, 10, 14), IsHot = true, ResumeKey = "k"
        };

        await Assert.ThrowsAsync<NucoreContractException>(
            () => reader.ReadAsync(unit, null, Collect(new List<NucoreRow>()), CancellationToken.None));
    }

    // ------------------------------------------------------------------ the watermark

    /// <summary>
    /// The mechanism that makes an endpoint with no date filter incremental: rows come back
    /// newest-first, and the first row BELOW the watermark ends the unit.
    ///
    /// <para>
    /// ⚠ The watermark date itself is deliberately RE-READ, not skipped. The watermark is
    /// MAX(SystemDate) already stored, and that date may have been only partly written if a
    /// previous run was interrupted mid-date. Stopping at it would leave the remainder
    /// unfetched forever; re-reading it costs one date and the replace makes it whole.
    /// </para>
    /// </summary>
    [Fact]
    public async Task PagingStopsBelowTheWatermarkAndReReadsTheWatermarkDateItself()
    {
        var endpoint = NucoreDescriptors.Find("PositionDetails")!;
        var settings = Settings(pageSize: 10);
        var rows = new List<NucoreRow>();
        var pagesServed = 0;

        var (reader, _) = Build(endpoint, settings, _ =>
        {
            pagesServed++;
            // Descending by system date, as SortFields=-systemDate guarantees.
            var body = string.Join(",", new[]
            {
                PositionDetailsRow("2026-10-16T00:00:00", 1),
                PositionDetailsRow("2026-10-15T00:00:00", 2),
                PositionDetailsRow("2026-10-14T00:00:00", 3),   // the watermark date - re-read
                PositionDetailsRow("2026-10-13T00:00:00", 4)    // below it - stop here
            });
            return (HttpStatusCode.OK, Envelope(body, 4, 4));
        });

        var unit = new NucoreWorkUnit
        {
            Endpoint = endpoint, TradeMonth = "202701", IsHot = true, ResumeKey = "k"
        };

        var result = await reader.ReadAsync(
            unit, new DateTime(2026, 10, 14), Collect(rows), CancellationToken.None);

        // The two newer dates plus the watermark date itself.
        Assert.Equal(3, result.RowsRead);
        Assert.Equal(1, result.RowsSkippedByWatermark);
        Assert.Equal(1, pagesServed);   // stopped inside the first page
        Assert.Equal(3, rows.Count);
    }

    /// <summary>
    /// ⚠ The guard that stands in for the primary key this endpoint cannot have.
    ///
    /// A replace procedure DELETEs the scopes in its batch and then INSERTs. If one scope
    /// were split across two batches, the second batch's DELETE would remove the rows the
    /// first had just written - and the run would report success with a silently short
    /// table. The reader flushes only on a scope boundary; this proves it refuses the
    /// alternative rather than corrupting the load.
    /// </summary>
    [Fact]
    public async Task AScopeArrivingInTwoSeparateBatchesIsRefused()
    {
        var endpoint = NucoreDescriptors.Find("PositionDetails")!;
        var settings = Settings(pageSize: 4, batchSize: 1);

        // Out of order: the 16th comes back AFTER the 15th, so its scope would be written
        // twice. The vendor's SortFields contract forbids this; the loader must not trust it.
        var (reader, _) = Build(endpoint, settings, _ =>
            (HttpStatusCode.OK, Envelope(string.Join(",", new[]
            {
                PositionDetailsRow("2026-10-16T00:00:00", 1),
                PositionDetailsRow("2026-10-15T00:00:00", 2),
                PositionDetailsRow("2026-10-16T00:00:00", 3)
            }), 3, 3)));

        var unit = new NucoreWorkUnit
        {
            Endpoint = endpoint, TradeMonth = "202701", IsHot = true, ResumeKey = "k"
        };

        var ex = await Assert.ThrowsAsync<NucoreContractException>(
            () => reader.ReadAsync(unit, null, Collect(new List<NucoreRow>()), CancellationToken.None));

        Assert.Contains("two separate write batches", ex.Message);
    }

    /// <summary>
    /// A ReplaceByDate unit is one scope, so the whole unit must go down as ONE batch -
    /// otherwise the second write deletes the first.
    /// </summary>
    [Fact]
    public async Task AReplaceByDateUnitIsWrittenAsASingleBatch()
    {
        var endpoint = NucoreDescriptors.Find("PnlExplain")!;
        var settings = Settings(pageSize: 10, batchSize: 2);   // deliberately tiny
        var batchSizes = new List<int>();

        var (reader, _) = Build(endpoint, settings, _ =>
            (HttpStatusCode.OK, Envelope(string.Join(",",
                Enumerable.Range(0, 7).Select(i => PnlExplainRow("2026-10-14T00:00:00", i))), 7, 7)));

        var unit = new NucoreWorkUnit
        {
            Endpoint = endpoint, Date = new DateTime(2026, 10, 14), IsHot = true, ResumeKey = "k"
        };

        await reader.ReadAsync(unit, null,
            (batch, _) => { batchSizes.Add(batch.Count); return Task.FromResult(batch.Count); },
            CancellationToken.None);

        // One write of all 7 rows, despite BatchSize being 2.
        Assert.Equal(new[] { 7 }, batchSizes);
    }

    /// <summary>
    /// The merge-mode guard: a duplicate key inside a unit is a bug in the declared key and
    /// must be loud. Within a batch it would raise a primary key violation; ACROSS batches
    /// it would silently overwrite, which is how a wrong key hides.
    /// </summary>
    [Fact]
    public async Task ADuplicateKeyInAMergeEndpointIsRefused()
    {
        var endpoint = NucoreDescriptors.Find("Volatilities")!;
        var settings = Settings(pageSize: 10, batchSize: 1);

        var (reader, _) = Build(endpoint, settings, _ =>
            (HttpStatusCode.OK, Envelope(string.Join(",", new[]
            {
                VolatilitiesRow("2026-10-14T00:00:00", "NG", "3.25"),
                VolatilitiesRow("2026-10-14T00:00:00", "NG", "3.25")   // same key
            }), 2, 2)));

        var unit = new NucoreWorkUnit
        {
            Endpoint = endpoint, Date = new DateTime(2026, 10, 14), IsHot = true, ResumeKey = "k"
        };

        var ex = await Assert.ThrowsAsync<NucoreContractException>(
            () => reader.ReadAsync(unit, null, Collect(new List<NucoreRow>()), CancellationToken.None));

        Assert.Contains("declared primary key", ex.Message);
    }

    [Fact]
    public async Task ANullWatermarkReadsTheWholeTradeMonth()
    {
        var endpoint = NucoreDescriptors.Find("PositionDetails")!;
        var settings = Settings(pageSize: 10);
        var rows = new List<NucoreRow>();

        var (reader, _) = Build(endpoint, settings, _ =>
            (HttpStatusCode.OK, Envelope(
                $"{PositionDetailsRow("2026-10-16T00:00:00", 1)},{PositionDetailsRow("2026-10-15T00:00:00", 2)}",
                2, 2)));

        var unit = new NucoreWorkUnit
        {
            Endpoint = endpoint, TradeMonth = "202701", IsHot = true, ResumeKey = "k"
        };

        var result = await reader.ReadAsync(unit, null, Collect(rows), CancellationToken.None);

        Assert.Equal(2, result.RowsRead);
        Assert.Equal(0, result.RowsSkippedByWatermark);
    }

    /// <summary>
    /// PositionDetails has no date parameter, so a unit of it must NOT be date-echo checked -
    /// its rows legitimately span hundreds of system dates.
    /// </summary>
    [Fact]
    public async Task TradeMonthUnitsAreNotDateEchoChecked()
    {
        var endpoint = NucoreDescriptors.Find("PositionDetails")!;
        var rows = new List<NucoreRow>();

        var (reader, _) = Build(endpoint, Settings(pageSize: 10), _ =>
            (HttpStatusCode.OK, Envelope(
                $"{PositionDetailsRow("2026-10-16T00:00:00", 1)},{PositionDetailsRow("2025-04-02T00:00:00", 2)}",
                2, 2)));

        var unit = new NucoreWorkUnit
        {
            Endpoint = endpoint, TradeMonth = "202701", IsHot = true, ResumeKey = "k"
        };

        var result = await reader.ReadAsync(unit, null, Collect(rows), CancellationToken.None);
        Assert.Equal(2, result.RowsRead);
    }

    // ----------------------------------------------------------------------- batching

    [Fact]
    public async Task RowsAreFlushedInBatchesRatherThanHeldWhole()
    {
        var endpoint = NucoreDescriptors.Find("Volatilities")!;
        var settings = Settings(pageSize: 6, batchSize: 2);
        var batchSizes = new List<int>();

        var (reader, _) = Build(endpoint, settings, _ =>
        {
            var body = string.Join(",", Enumerable.Range(0, 5)
                .Select(i => VolatilitiesRow("2026-10-14T00:00:00", $"P{i}")));
            return (HttpStatusCode.OK, Envelope(body, 5, 5));
        });

        var unit = new NucoreWorkUnit
        {
            Endpoint = endpoint, Date = new DateTime(2026, 10, 14), IsHot = true, ResumeKey = "k"
        };

        await reader.ReadAsync(unit, null,
            (batch, _) => { batchSizes.Add(batch.Count); return Task.FromResult(batch.Count); },
            CancellationToken.None);

        // 5 rows at batch size 2 => 2 + 2 + a final flush of 1.
        Assert.Equal(new[] { 2, 2, 1 }, batchSizes);
    }

    [Fact]
    public async Task AnUnconvertibleRowIsDroppedAndCountedRatherThanFailingTheUnit()
    {
        var endpoint = NucoreDescriptors.Find("Volatilities")!;
        var rows = new List<NucoreRow>();

        var (reader, _) = Build(endpoint, Settings(pageSize: 10), _ =>
            (HttpStatusCode.OK, Envelope(
                $$"""{{VolatilitiesRow("2026-10-14T00:00:00", "NG")}},{"systemDate":"2026-10-14T00:00:00"}""",
                2, 2)));

        var unit = new NucoreWorkUnit
        {
            Endpoint = endpoint, Date = new DateTime(2026, 10, 14), IsHot = true, ResumeKey = "k"
        };

        var result = await reader.ReadAsync(unit, null, Collect(rows), CancellationToken.None);

        Assert.Equal(1, result.RowsRead);
        Assert.Equal(1, result.RowsDropped);
    }

    private static string PnlExplainRow(string curDate, int detailId) =>
        BuildFullRow(NucoreDescriptors.Find("PnlExplain")!, new Dictionary<string, string>
        {
            ["curDate"] = $"\"{curDate}\"",
            ["tradeId"] = "1",
            ["detailId"] = detailId.ToString(),
            ["tradeMonth"] = "\"202701\""
        });

    private static string PositionDetailsRow(string systemDate, int detailId) =>
        BuildFullRow(NucoreDescriptors.Find("PositionDetails")!, new Dictionary<string, string>
        {
            ["systemDate"] = $"\"{systemDate}\"",
            ["tradeId"] = "1",
            ["detailId"] = detailId.ToString(),
            ["tradeMonth"] = "\"202701\""
        });

    /// <summary>
    /// Builds a JSON object carrying every property the endpoint declares, so the row
    /// builder has no missing-property rejections to report. Named overrides win.
    /// </summary>
    private static string BuildFullRow(NucoreEndpoint endpoint, Dictionary<string, string> overrides)
    {
        var parts = new List<string>(endpoint.Columns.Count);

        foreach (var column in endpoint.Columns)
        {
            if (overrides.TryGetValue(column.JsonName, out var supplied))
            {
                parts.Add($"\"{column.JsonName}\":{supplied}");
                continue;
            }

            if (column.Nullable) { parts.Add($"\"{column.JsonName}\":null"); continue; }

            var value = column.Kind switch
            {
                NucoreColumnKind.String => "\"x\"",
                NucoreColumnKind.DateTime => "\"2026-10-14T00:00:00\"",
                NucoreColumnKind.DateTimeOffset => "\"2026-10-14T00:00:00+00:00\"",
                NucoreColumnKind.Int32 or NucoreColumnKind.Int64 => "0",
                NucoreColumnKind.Decimal => "0",
                NucoreColumnKind.Boolean => "false",
                NucoreColumnKind.Guid => "\"00000000-0000-0000-0000-000000000000\"",
                _ => "null"
            };

            parts.Add($"\"{column.JsonName}\":{value}");
        }

        return "{" + string.Join(",", parts) + "}";
    }
}
