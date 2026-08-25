using System.Net;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DataLoader.ModernCommodities.Tests;

/// <summary>
/// The HTTP status matrix, applied identically by both readers and driven entirely by a fake handler.
///
/// <para><b>A header-only <c>200</c> is the legitimate "nothing to report" answer</b> - a successful
/// EMPTY read: an audit row, zero rows, and the work unit SUCCEEDS. Everything else is loud:</para>
/// <list type="bullet">
///   <item><b>Four distinct <c>400</c>s with four distinct fixes</b>, separable only by the response
///     BODY - which is why the reader always reads it and why <c>HttpJsonSourceReaderBase</c>
///     (whose <c>EnsureSuccessStatusCode()</c> throws before the body can be read) is unusable here.
///     The <b>row-cap</b> body is classified distinctly and its message names the remedy
///     (<c>ChunkDays</c>), because "fixing" it by moving <c>startDate</c> forward hides the real
///     constraint: there is NO paging.</item>
///   <item><b><c>401</c>/<c>403</c> are terminal</b> - there is no token to re-mint, so neither is
///     retried.</item>
///   <item><b><c>404</c> is NOT a data condition</b> here (contrast NGI): it means the path or the
///     <c>/v1</c> segment is wrong - a deployment error.</item>
///   <item><b>A <c>200</c> with an empty body, or a header missing a KEY column, is a SHAPE
///     failure</b>, not "no data": the header is invariant across every capture, and an unkeyable
///     pull must never be reported as a successful empty load.</item>
/// </list>
/// </summary>
public class StatusMatrixTests
{
    // The four verified 400 bodies.
    private const string RowCapBody =
        "Request returns more than the limit of 10000 rows. Please apply a more granular filter";
    private const string SettlementsRowCapBody =
        "Request returns more than the limit of 100000 rows. Please apply a more granular filter";
    private const string HistoryBody = "startDate must be within the last six months";
    private const string InvalidDateBody = "Invalid startDate";
    private const string InvalidLegalEntityBody =
        "Invalid legalEntityName, valid options: \"Acme Energy Management, LLC\", \"Acme Energy Management Canada ULC\"";

    // ============================================================ the legitimate empty read

    [Fact]
    public async Task AHeaderOnly200_IsASuccessfulEmptyRead_ZeroRows_AuditRowWritten_UnitSucceeds()
    {
        // The REAL captured header-only body, byte-for-byte.
        var result = await ReaderHarness.RunTradesAsync(
            HttpStatusCode.OK, Samples.MyTradesHeaderOnly, ModComDescriptors.MyTrades);

        Assert.Empty(result.Rows);                       // nothing thrown -> the work unit SUCCEEDS

        var call = Assert.Single(result.FileLog.Calls);   // the audit row IS written
        Assert.Equal("NotAvailable", call.Status);
        Assert.Equal(200, call.HttpStatus);
        Assert.Equal(0, call.RowCount);
        Assert.Equal(0, call.DroppedRowCount);
        Assert.Equal(ModComEndpoints.MyTrades, call.File.Endpoint);
        Assert.Equal(ReaderHarness.WindowStart, call.File.WindowStart);
        Assert.Equal(ReaderHarness.WindowEnd, call.File.WindowEnd);

        // It must never look like a problem: no warning, no error for the expected case.
        Assert.Empty(result.Log.OfLevel(LogLevel.Warning));
        Assert.Empty(result.Log.OfLevel(LogLevel.Error));
    }

    [Fact]
    public async Task AHeaderOnly200_OnSettlements_IsAlsoASuccessfulEmptyRead()
    {
        var result = await ReaderHarness.RunSettlementsAsync(HttpStatusCode.OK, Samples.SettlementsHeader);

        Assert.Empty(result.Rows);
        var call = Assert.Single(result.FileLog.Calls);
        Assert.Equal("NotAvailable", call.Status);
        Assert.Equal(0, call.RowCount);
        Assert.Empty(result.Log.OfLevel(LogLevel.Error));
    }

    // ============================================================ 401 / 403

    [Fact]
    public async Task Http401_Throws_AndTheHubRowRecordsTheRealHttpStatus()
    {
        // The live 401 body is EMPTY (verified). Nothing is parsed from it and no credential value
        // is ever named.
        var (reader, fileLog, log) = ReaderHarness.TradesReader(HttpStatusCode.Unauthorized, string.Empty);

        var ex = await Assert.ThrowsAsync<ModComRequestException>(
            () => reader.ReadAsync(ReaderHarness.Unit(), CancellationToken.None));

        Assert.Equal(ModComFailureKind.Auth, ex.Kind);
        Assert.Equal(401, ex.HttpStatus);
        Assert.Contains("HTTP 401", ex.Message);
        Assert.Contains("not retried", ex.Message);
        Assert.DoesNotContain(ReaderHarness.DummyPassword, ex.Message);

        var call = Assert.Single(fileLog.Calls);
        Assert.Equal("Failed", call.Status);
        Assert.Equal(401, call.HttpStatus);        // the REAL status reaches arm.FileLog
        Assert.Equal(0, call.RowCount);
        Assert.All(log.Messages, m => Assert.DoesNotContain(ReaderHarness.DummyPassword, m));
    }

    [Fact]
    public async Task Http403_Throws_AsAnEntitlementFailure()
    {
        var (reader, fileLog, _) = ReaderHarness.TradesReader(HttpStatusCode.Forbidden, string.Empty);

        var ex = await Assert.ThrowsAsync<ModComRequestException>(
            () => reader.ReadAsync(ReaderHarness.Unit(), CancellationToken.None));

        Assert.Equal(ModComFailureKind.Auth, ex.Kind);
        Assert.Equal(403, ex.HttpStatus);
        Assert.Equal(403, Assert.Single(fileLog.Calls).HttpStatus);
    }

    // ============================================================ the four distinct 400s

    [Fact]
    public async Task TheRowCap400_IsClassifiedDISTINCTLY_AndItsMessageNamesChunkDaysAsTheRemedy()
    {
        var (reader, fileLog, _) = ReaderHarness.TradesReader(HttpStatusCode.BadRequest, RowCapBody);

        var ex = await Assert.ThrowsAsync<ModComRequestException>(
            () => reader.ReadAsync(ReaderHarness.Unit(), CancellationToken.None));

        Assert.Equal(ModComFailureKind.RowCap, ex.Kind);              // NOT lumped in with the others
        Assert.NotEqual(ModComFailureKind.History, ex.Kind);
        Assert.NotEqual(ModComFailureKind.InvalidParam, ex.Kind);
        Assert.Contains("ROW CAP", ex.Message);
        Assert.Contains("ChunkDays", ex.Message);                     // the remedy is named...
        Assert.Contains("NO paging", ex.Message);                     // ...and so is why
        Assert.Contains("Do NOT", ex.Message);
        Assert.Contains("31 calendar day", ex.Message);               // the requested span

        var call = Assert.Single(fileLog.Calls);
        Assert.Equal("Failed", call.Status);
        Assert.Equal(400, call.HttpStatus);
        Assert.Equal(RowCapBody, call.ErrorMessage);                  // the VERBATIM body is persisted
    }

    [Fact]
    public async Task TheRowCap400_MatchesTheSettlementsCapTooBecauseTheMatchIsOnASubstring()
    {
        // The body interpolates the applicable cap (10000 vs 100000), so the match is on
        // "more than the limit of", never on a full sentence.
        var (reader, _, _) = ReaderHarness.SettlementsReader(HttpStatusCode.BadRequest, SettlementsRowCapBody);

        var ex = await Assert.ThrowsAsync<ModComRequestException>(
            () => reader.ReadAsync(ReaderHarness.Unit(ModComDescriptors.Settlements), CancellationToken.None));

        Assert.Equal(ModComFailureKind.RowCap, ex.Kind);
        Assert.Contains("100000", ex.Message);
    }

    [Fact]
    public async Task TheHistoryCap400_IsItsOwnKind_AndPointsAtTheClamp()
    {
        var (reader, fileLog, _) = ReaderHarness.TradesReader(HttpStatusCode.BadRequest, HistoryBody);

        var ex = await Assert.ThrowsAsync<ModComRequestException>(
            () => reader.ReadAsync(ReaderHarness.Unit(), CancellationToken.None));

        Assert.Equal(ModComFailureKind.History, ex.Kind);
        Assert.Contains("HISTORY CAP", ex.Message);
        Assert.Contains("AddMonths(-6)", ex.Message);
        Assert.Contains("mask one another", ex.Message);   // never infer one limit from the other
        Assert.Equal(HistoryBody, Assert.Single(fileLog.Calls).ErrorMessage);
    }

    [Fact]
    public async Task AnInvalidDate400_IsOurFormattingBug()
    {
        var (reader, _, _) = ReaderHarness.TradesReader(HttpStatusCode.BadRequest, InvalidDateBody);

        var ex = await Assert.ThrowsAsync<ModComRequestException>(
            () => reader.ReadAsync(ReaderHarness.Unit(), CancellationToken.None));

        Assert.Equal(ModComFailureKind.InvalidParam, ex.Kind);
        Assert.Contains("OUR formatting bug", ex.Message);
        Assert.Contains("InvariantCulture", ex.Message);
    }

    [Fact]
    public async Task AnInvalidLegalEntity400_IsCheckedBEFORETheGenericInvalidPrefix()
    {
        // Its body STARTS "Invalid legalEntityName, ...", so a naive "Invalid " check first would
        // misclassify it as a date-format bug and send the operator to the wrong setting.
        var (reader, _, _) = ReaderHarness.TradesReader(
            HttpStatusCode.BadRequest, InvalidLegalEntityBody, ModComDescriptors.MyTrades);

        var ex = await Assert.ThrowsAsync<ModComRequestException>(
            () => reader.ReadAsync(
                ReaderHarness.Unit(ModComDescriptors.MyTrades, scope: "Acme Energy Management, LLC"),
                CancellationToken.None));

        Assert.Equal(ModComFailureKind.InvalidLegalEntity, ex.Kind);
        Assert.NotEqual(ModComFailureKind.InvalidParam, ex.Kind);
        Assert.Contains("LegalEntityName", ex.Message);
        Assert.Contains("valid options", ex.Message);       // the body is quoted: it holds no secret
    }

    [Fact]
    public async Task AnUnclassified400_FallsBackToOther_WithTheBodyQuoted()
    {
        var (reader, _, _) = ReaderHarness.TradesReader(HttpStatusCode.BadRequest, "Something entirely new");

        var ex = await Assert.ThrowsAsync<ModComRequestException>(
            () => reader.ReadAsync(ReaderHarness.Unit(), CancellationToken.None));

        Assert.Equal(ModComFailureKind.Other, ex.Kind);
        Assert.Contains("unclassified body", ex.Message);
        Assert.Contains("Something entirely new", ex.Message);
    }

    [Fact]
    public void TheFourHundredBodies_AreClassifiedIntoFourDIFFERENTKinds()
    {
        var (reader, _, _) = ReaderHarness.TradesReader(HttpStatusCode.OK, string.Empty);
        var unit = ReaderHarness.Unit();

        var kinds = new[] { RowCapBody, HistoryBody, InvalidLegalEntityBody, InvalidDateBody }
            .Select(body => reader.Classify(HttpStatusCode.BadRequest, body, unit).Kind)
            .ToArray();

        Assert.Equal(
            new[]
            {
                ModComFailureKind.RowCap, ModComFailureKind.History,
                ModComFailureKind.InvalidLegalEntity, ModComFailureKind.InvalidParam
            },
            kinds);
        Assert.Equal(4, kinds.Distinct().Count());   // four bodies -> four DIFFERENT fixes
    }

    [Fact]
    public void TheRowCapBodyWinsWhenBothCapsAreViolated_WhichIsWhyNeitherIsInferredFromTheOther()
    {
        // Verified vendor behaviour: a request violating BOTH caps answers with the ROW-CAP message.
        var (reader, _, _) = ReaderHarness.TradesReader(HttpStatusCode.OK, string.Empty);
        var both = RowCapBody + " " + HistoryBody;

        Assert.Equal(ModComFailureKind.RowCap,
            reader.Classify(HttpStatusCode.BadRequest, both, ReaderHarness.Unit()).Kind);
    }

    // ============================================================ 404 and the retryable statuses

    [Fact]
    public async Task Http404_IsADeploymentError_NotANoDataOutcome()
    {
        var (reader, fileLog, _) = ReaderHarness.TradesReader(HttpStatusCode.NotFound, string.Empty);

        var ex = await Assert.ThrowsAsync<ModComRequestException>(
            () => reader.ReadAsync(ReaderHarness.Unit(), CancellationToken.None));

        Assert.Equal(ModComFailureKind.Other, ex.Kind);
        Assert.Equal(404, ex.HttpStatus);
        Assert.Contains("NOT a 'no data' outcome", ex.Message);
        Assert.Contains("/v1", ex.Message);
        Assert.Equal("Failed", Assert.Single(fileLog.Calls).Status);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, 429)]
    [InlineData(HttpStatusCode.InternalServerError, 500)]
    [InlineData(HttpStatusCode.BadGateway, 502)]
    [InlineData(HttpStatusCode.ServiceUnavailable, 503)]
    [InlineData(HttpStatusCode.RequestTimeout, 408)]
    public async Task AnyOtherNonSuccess_Throws_AndLogsFailedWithTheRealHttpStatus(HttpStatusCode status, int expected)
    {
        var (reader, fileLog, _) = ReaderHarness.TradesReader(status, "boom");

        var ex = await Assert.ThrowsAsync<ModComRequestException>(
            () => reader.ReadAsync(ReaderHarness.Unit(), CancellationToken.None));

        Assert.Equal(ModComFailureKind.Other, ex.Kind);
        Assert.Equal(expected, ex.HttpStatus);

        var call = Assert.Single(fileLog.Calls);
        Assert.Equal("Failed", call.Status);
        Assert.Equal(expected, call.HttpStatus);      // the real status on the failure path
        Assert.Equal("boom", call.ErrorMessage);
    }

    // ============================================================ shape failures on a 200

    [Fact]
    public async Task A200WithAnEmptyBody_IsAShapeFailure_NotAnEmptyRead()
    {
        // The CSV header is invariant across every capture, so its absence means an error page or a
        // truncated response. A "tolerant" zero-row success here would report a clean empty load.
        var (reader, fileLog, _) = ReaderHarness.TradesReader(HttpStatusCode.OK, string.Empty);

        var ex = await Assert.ThrowsAsync<ModComRequestException>(
            () => reader.ReadAsync(ReaderHarness.Unit(), CancellationToken.None));

        Assert.Equal(ModComFailureKind.ShapeDrift, ex.Kind);
        Assert.Equal(200, ex.HttpStatus);
        Assert.Contains("empty body", ex.Message);

        var call = Assert.Single(fileLog.Calls);
        Assert.Equal("Failed", call.Status);
        Assert.Equal(200, call.HttpStatus);
    }

    [Fact]
    public async Task A200WhoseHeaderIsMissingTheTradesKeyColumn_IsAShapeFailure()
    {
        var header = string.Join(",", ModComColumns.Trades
            .Where(n => n != ModComColumns.TradeNumber)
            .Select(Samples.Q));

        var (reader, fileLog, _) = ReaderHarness.TradesReader(HttpStatusCode.OK, header);

        var ex = await Assert.ThrowsAsync<ModComRequestException>(
            () => reader.ReadAsync(ReaderHarness.Unit(), CancellationToken.None));

        Assert.Equal(ModComFailureKind.ShapeDrift, ex.Kind);
        Assert.Contains("missing key column(s) [Trade Number]", ex.Message);
        Assert.Contains("cannot be keyed", ex.Message);

        var call = Assert.Single(fileLog.Calls);
        Assert.Equal("Failed", call.Status);
        Assert.Contains("Trade Number", call.ErrorMessage!);
    }

    [Theory]
    [InlineData(ModComColumns.SettlementDate)]
    [InlineData(ModComColumns.Product)]
    [InlineData(ModComColumns.Location)]
    [InlineData(ModComColumns.PipelineTerminal)]
    [InlineData(ModComColumns.PriceBasis)]
    [InlineData(ModComColumns.Term)]
    public async Task A200MissingAnyOfTheSixSettlementsKeyColumns_IsAShapeFailure(string missing)
    {
        var header = string.Join(",", ModComColumns.Settlements.Where(n => n != missing).Select(Samples.Q));

        var (reader, fileLog, _) = ReaderHarness.SettlementsReader(HttpStatusCode.OK, header);

        var ex = await Assert.ThrowsAsync<ModComRequestException>(
            () => reader.ReadAsync(ReaderHarness.Unit(ModComDescriptors.Settlements), CancellationToken.None));

        Assert.Equal(ModComFailureKind.ShapeDrift, ex.Kind);
        Assert.Contains(missing, ex.Message);
        Assert.Equal("Failed", Assert.Single(fileLog.Calls).Status);
    }

    [Fact]
    public async Task AMissingNonKeyColumn_IsNOTAShapeFailure_TheOtherColumnsStillLoad()
    {
        var header = string.Join(",", ModComColumns.Trades.Where(n => n != ModComColumns.Notes).Select(Samples.Q));
        var record = string.Join(",", ModComColumns.Trades
            .Where(n => n != ModComColumns.Notes)
            .Select(n => Samples.Q(n == ModComColumns.TradeNumber ? "68043" : n == ModComColumns.State ? "Finalized" : "")));

        var result = await ReaderHarness.RunTradesAsync(HttpStatusCode.OK, Samples.Lf(header, record));

        var row = Assert.Single(result.Rows);
        Assert.Equal(68043, row.TradeNumber);
        Assert.Equal("Finalized", row.State);
        Assert.Null(row.Notes);
        Assert.Equal("Success", Assert.Single(result.FileLog.Calls).Status);
    }

    // ============================================================ the inverted window

    [Fact]
    public async Task AnInvertedWindow_FailsLoudlyBEFOREAnyRequestIsSent()
    {
        // Verified: an inverted range returns a SILENT header-only 200, so a window-computation bug
        // would otherwise load zero rows forever with no error at all.
        var handler = FakeHttpMessageHandler.Respond(HttpStatusCode.OK, Samples.TradesHeader);
        var fileLog = new FakeModComFileLog();
        var reader = new ModComTradesSourceReader(
            handler.NewClient(), ReaderHarness.Settings(), fileLog, ModComDescriptors.AllTrades, new ListLogger());

        var inverted = ReaderHarness.Unit(start: new DateOnly(2026, 8, 24), end: new DateOnly(2026, 7, 25));

        var ex = await Assert.ThrowsAsync<ModComRequestException>(
            () => reader.ReadAsync(inverted, CancellationToken.None));

        Assert.Equal(ModComFailureKind.InvalidParam, ex.Kind);
        Assert.Contains("inverted window", ex.Message);
        Assert.Equal(0, handler.Calls);      // no request was ever sent
        Assert.Empty(fileLog.Calls);         // and no hub row was written for a request never made
    }

    // ============================================================ cancellation and hub-write failure

    [Fact]
    public async Task ACancelledUnit_RethrowsWithoutWritingASpuriousHubRow()
    {
        // The per-unit timeout is already recorded by core.LoadLog; the token is spent, so a hub
        // write on it would only fail.
        var (reader, fileLog, _) = ReaderHarness.TradesReader(HttpStatusCode.OK, Samples.AllTradesLive);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => reader.ReadAsync(ReaderHarness.Unit(), cts.Token));

        Assert.Empty(fileLog.Calls);
    }

    [Fact]
    public async Task AFailingHubWriteOnTheFailurePath_StillFailsTheWorkUnitLoudly()
    {
        // Pinning ACTUAL behaviour, not a wish. On a non-2xx the hub row is written BEFORE the
        // classified ModComRequestException is constructed, so if arm.FileLog itself is unreachable
        // the hub error is what propagates. Either way the unit FAILS - it can never be mistaken for
        // a successful empty read - which is the property that matters.
        var (reader, _) = ReaderHarness.TradesReaderWith(
            new ThrowingModComFileLog(), HttpStatusCode.BadRequest, RowCapBody);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => reader.ReadAsync(ReaderHarness.Unit(), CancellationToken.None));

        Assert.Equal("filelog boom", ex.Message);
    }

    [Fact]
    public async Task AFailingHubWriteOnASuccessfulRead_AlsoFailsTheUnit_AndNeverReturnsRowsSilently()
    {
        // A 200 that parsed fine, but the hub row cannot be written: the read must NOT quietly
        // return rows with no provenance recorded, because arm.FileLog is this loader's ONLY
        // provenance (there is no FileLogId on any fact row).
        var (reader, _) = ReaderHarness.TradesReaderWith(
            new ThrowingModComFileLog(), HttpStatusCode.OK, Samples.AllTradesLive);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => reader.ReadAsync(ReaderHarness.Unit(), CancellationToken.None));
    }
}
