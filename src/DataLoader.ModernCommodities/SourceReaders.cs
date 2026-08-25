using System.Net;
using DataLoader.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace DataLoader.ModernCommodities;

/// <summary>
/// Why a ModCom request failed (design §5.6). The four <c>400</c> causes have <b>four different
/// fixes</b>, and they are distinguishable only by the response <b>body</b> — which is why a bare
/// "HTTP 400" is not actionable and why <c>HttpJsonSourceReaderBase</c> (whose
/// <c>EnsureSuccessStatusCode()</c> throws before the body can be read) is unusable here.
/// </summary>
public enum ModComFailureKind
{
    /// <summary>Body contains <c>more than the limit of</c> — the request would exceed the endpoint's row cap. The remedy is a smaller <c>ChunkDays</c> (decision D11).</summary>
    RowCap,

    /// <summary>Body contains <c>within the last six months</c> — our history clamp is wrong or was bypassed.</summary>
    History,

    /// <summary>Body starts <c>Invalid </c> — a malformed date parameter, i.e. our formatting bug.</summary>
    InvalidParam,

    /// <summary>Body contains <c>Invalid legalEntityName</c> — a bad <c>myTrades</c> scope setting. The body enumerates the valid values and is safe to log.</summary>
    InvalidLegalEntity,

    /// <summary><c>401</c> (wrong/revoked credential) or <c>403</c> (entitlement). <b>There is no token to refresh</b>, so neither is retried.</summary>
    Auth,

    /// <summary>A <c>200</c> whose body is empty or whose first record is not a recognisable header, or a header missing a KEY column.</summary>
    ShapeDrift,

    /// <summary>Anything else — an unclassified <c>400</c>, a <c>404</c> on the endpoint path (a deployment error), a <c>429</c>/<c>5xx</c> after retries.</summary>
    Other
}

/// <summary>
/// A classified ModCom request failure. It always fails <b>the work unit, not the run</b>: the
/// pipeline records a <c>core.LoadLog</c> failure and continues to the next unit, and the module
/// continues to the next pipeline (design §5.6 rule 4, §1.2).
/// </summary>
public sealed class ModComRequestException : Exception
{
    public ModComRequestException(ModComFailureKind kind, string endpointId, int? httpStatus, string message)
        : base(message)
    {
        Kind = kind;
        EndpointId = endpointId;
        HttpStatus = httpStatus;
    }

    public ModComFailureKind Kind { get; }
    public string EndpointId { get; }
    public int? HttpStatus { get; }

    /// <summary>
    /// <c>true</c> once the <c>arm.FileLog</c> <c>Failed</c> row for this failure has been written,
    /// so the reader's outer catch does not write a second one.
    /// </summary>
    internal bool HubRowWritten { get; init; }
}

/// <summary>
/// Shared HTTP + status-dispatch + CSV + <c>arm.FileLog</c> plumbing for both ModCom parse shapes
/// (design §5). Implements <see cref="ISourceReader{TUnit,TItem}"/> <b>directly</b>.
///
/// <para><b>⚠⚠ <c>HttpJsonSourceReaderBase</c> is FORBIDDEN here — twice over.</b> (1) Every
/// successful response is <c>Content-Type: text/csv</c>; there is no JSON representation of any
/// endpoint — no <c>?format=json</c>, no <c>Accept</c>-negotiated alternative, no <c>.json</c>
/// sibling — so a JSON deserializer has nothing to bind. (2) It calls
/// <c>EnsureSuccessStatusCode()</c>, which would throw <i>before</i> the reader could read the
/// response <b>body</b> — and the body is the only thing that distinguishes the four <c>400</c>s
/// from one another. The precedents to follow are StormVista and CWG (HTTP + CSV).</para>
///
/// <para><b>Status contract (design §5.6): there is NO legitimate non-2xx.</b> Emptiness is
/// expressed <i>inside</i> a <c>200</c>, as a header-only body — which <b>succeeds</b> with zero
/// rows (a quiet <c>myTrades</c> window, a weekend or not-yet-published settlements day). Every
/// <c>400</c>/<c>401</c> is our bug or a configuration failure and <b>throws</b>. Do not copy NGI's
/// 404 tolerance: tolerating a <c>400</c> as "no data" would swallow a real defect — e.g. a chunking
/// bug in which every request is over cap and the loader "succeeds" having loaded nothing.</para>
///
/// <para><b>Exception discipline</b> (the CWG/NGI posture): <see cref="OperationCanceledException"/>
/// is rethrown with <b>no <c>FileLog</c> write</b> (the per-unit timeout is already recorded by
/// <c>core.LoadLog</c>, and the token is spent); any other failure gets a best-effort <c>Failed</c>
/// hub row written with <see cref="CancellationToken.None"/> — never masking the original exception
/// — and is then rethrown.</para>
/// </summary>
public abstract class ModComCsvSourceReaderBase<TRow> : ISourceReader<ModComWorkUnit, TRow>
{
    private readonly HttpClient _http;
    private readonly IModComFileLog _fileLog;

    protected ModComCsvSourceReaderBase(
        HttpClient http,
        ModComSettings settings,
        IModComFileLog fileLog,
        ModComEndpointDescriptor descriptor,
        ILogger logger)
    {
        _http = http;
        _fileLog = fileLog;
        Settings = settings;
        Descriptor = descriptor;
        Logger = logger;
    }

    protected ModComSettings Settings { get; }
    protected ModComEndpointDescriptor Descriptor { get; }
    protected ILogger Logger { get; }

    /// <summary>The verbatim expected header names, in CSV order.</summary>
    protected abstract IReadOnlyList<string> ExpectedHeaders { get; }

    /// <summary>
    /// The columns without which the pull cannot be keyed at all. A missing one is a hard failure
    /// (<see cref="ModComFailureKind.ShapeDrift"/>) — an unkeyable pull must never be reported as a
    /// successful empty load.
    /// </summary>
    protected abstract IReadOnlyList<string> RequiredKeyColumns { get; }

    /// <summary>Maps the data records (record 0 is the header and is NOT passed in) to rows, de-duped on the merge key.</summary>
    private protected abstract IReadOnlyList<TRow> MapRecords(
        IReadOnlyList<string[]> dataRecords, ModComHeaderMap map, ModComWorkUnit unit, ModComParseCounters counters);

    public async Task<IReadOnlyList<TRow>> ReadAsync(ModComWorkUnit unit, CancellationToken cancellationToken)
    {
        // Re-assert the window here as well as in ModComTime.ResolveWindow: an inverted range returns
        // a SILENT header-only 200, so a window bug would otherwise load zero rows forever with no
        // error at all (design §3.2 #5).
        if (unit.WindowStart > unit.WindowEnd)
            throw new ModComRequestException(ModComFailureKind.InvalidParam, Descriptor.EndpointId, null,
                $"[ModCom {Descriptor.EndpointId}] inverted window {ModComTime.Iso(unit.WindowStart)}..{ModComTime.Iso(unit.WindowEnd)} — " +
                "the API answers an inverted range with a SILENT empty 200, so this is failed loudly instead.");

        var requestUri = new Uri($"{Settings.BaseUrlRoot()}/{unit.RequestPath}", UriKind.Absolute);
        var file = new ModComFileContext(
            Descriptor.EndpointId, unit.WindowStart, unit.WindowEnd, unit.RunToken, unit.LegalEntityName, unit.RequestPath);

        int? httpStatus = null;
        var hubRowWritten = false;

        try
        {
            // The credential is a HEADER, so the request URI carries nothing sensitive — but the
            // sanitised relative path is what is logged, matching every other loader here.
            Logger.LogDebug("[ModCom {Endpoint}] GET {Path}", Descriptor.EndpointId, unit.RequestPath);

            using var response = await _http.GetAsync(requestUri, cancellationToken).ConfigureAwait(false);
            httpStatus = (int)response.StatusCode;

            if (!response.IsSuccessStatusCode)
            {
                // ALWAYS read the body of a non-2xx: it is the only thing that separates the four
                // 400s, and arm.FileLog is this loader's ONLY provenance (design §5.6 rule 1).
                var body = await ReadBodySafeAsync(response, cancellationToken).ConfigureAwait(false);
                var (kind, message) = Classify(response.StatusCode, body, unit);

                await _fileLog.UpsertAsync(file, "Failed", httpStatus, 0, 0, body, cancellationToken).ConfigureAwait(false);
                hubRowWritten = true;

                Logger.LogError("[ModCom {Endpoint}] {Path} → HTTP {Status} ({Kind}): {Message}",
                    Descriptor.EndpointId, unit.RequestPath, httpStatus, kind, message);

                throw new ModComRequestException(kind, Descriptor.EndpointId, httpStatus, message) { HubRowWritten = true };
            }

            // A non-text/csv content type is worth a warning, but the header assertion below is the
            // real gate — a vendor that starts sending "text/plain" must not fail the load.
            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (contentType is not null && !contentType.Contains("csv", StringComparison.OrdinalIgnoreCase))
                Logger.LogWarning("[ModCom {Endpoint}] {Path} returned Content-Type '{ContentType}' (expected text/csv); parsing anyway",
                    Descriptor.EndpointId, unit.RequestPath, contentType);

            var csv = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var records = ModComCsv.Parse(csv);

            // A 200 with zero bytes, or with no parseable first record, was NEVER observed: the header
            // is invariant across every capture, so its absence means something is wrong (an error
            // page, a truncated response). A "tolerant" reader returning zero rows here would report a
            // successful empty load (design §5.2 rule 3).
            if (records.Count == 0)
            {
                const string note = "200 with an empty body — the CSV header is invariant, so its absence is a shape failure, not 'no data'";
                await _fileLog.UpsertAsync(file, "Failed", httpStatus, 0, 0, note, cancellationToken).ConfigureAwait(false);
                hubRowWritten = true;
                throw new ModComRequestException(ModComFailureKind.ShapeDrift, Descriptor.EndpointId, httpStatus,
                    $"[ModCom {Descriptor.EndpointId}] {unit.RequestPath}: {note}") { HubRowWritten = true };
            }

            var map = ModComHeaderMap.Build(records[0], ExpectedHeaders);

            var missingKeys = RequiredKeyColumns.Where(c => !map.Has(c)).ToList();
            if (missingKeys.Count > 0)
            {
                var note = "ShapeDrift: header is missing key column(s) [" + string.Join(", ", missingKeys) + "]";
                await _fileLog.UpsertAsync(file, "Failed", httpStatus, 0, 0, note, cancellationToken).ConfigureAwait(false);
                hubRowWritten = true;
                throw new ModComRequestException(ModComFailureKind.ShapeDrift, Descriptor.EndpointId, httpStatus,
                    $"[ModCom {Descriptor.EndpointId}] {unit.RequestPath}: {note} — the pull cannot be keyed, so it fails rather " +
                    "than loading unkeyable rows. Modern Commodities publishes no schema document, so a vendor column change " +
                    "is silent by construction (design §5.2).") { HubRowWritten = true };
            }

            // Non-key drift is tolerated, reported once, and recorded in the hub row — with no per-row
            // provenance, that row is the only durable record that a pull was parsed against a drifted
            // header. A vendor RENAME must not drop the other 33 columns.
            var driftNote = map.DriftNote(ExpectedHeaders.Count);
            if (driftNote.Length > 0)
                Logger.LogWarning("[ModCom {Endpoint}] {Path}: {Drift} — the affected column(s) map to NULL for every row",
                    Descriptor.EndpointId, unit.RequestPath, driftNote);

            var counters = new ModComParseCounters();
            var dataRecords = records.Skip(1).ToList();
            var rows = MapRecords(dataRecords, map, unit, counters);
            counters.Parsed = rows.Count;

            if (counters.Dropped > 0)
                Logger.LogError("[ModCom {Endpoint}] {Path}: dropped {Dropped} unkeyable row(s) (blank/unparseable or over-width key)",
                    Descriptor.EndpointId, unit.RequestPath, counters.Dropped);
            if (counters.TruncatedFields > 0)
                Logger.LogWarning("[ModCom {Endpoint}] {Path}: clamped {Count} over-width non-key value(s) to their column width",
                    Descriptor.EndpointId, unit.RequestPath, counters.TruncatedFields);
            if (counters.UnparseableFields > 0)
                Logger.LogWarning("[ModCom {Endpoint}] {Path}: {Count} field(s) present but unparseable — degraded to NULL",
                    Descriptor.EndpointId, unit.RequestPath, counters.UnparseableFields);
            if (counters.DuplicateKeys > 0)
                Logger.LogWarning("[ModCom {Endpoint}] {Path}: collapsed {Count} duplicate merge key(s) within one response",
                    Descriptor.EndpointId, unit.RequestPath, counters.DuplicateKeys);

            // *** A header-only 200 is a SUCCESSFUL EMPTY READ. *** NotAvailable, zero rows, and the
            // WORK UNIT SUCCEEDS — never a failure, never a retry, never an alert. Because the resume
            // key is hot-only, the window is re-probed on the next clock hour anyway (design §3.5).
            var status = rows.Count == 0 ? "NotAvailable" : "Success";

            // ErrorMessage is DUAL USE: the verbatim body on a failure, or a non-fatal note here.
            var fileLogId = await _fileLog.UpsertAsync(
                file, status, httpStatus, rows.Count, counters.Dropped,
                driftNote.Length > 0 ? driftNote : null, cancellationToken).ConfigureAwait(false);
            hubRowWritten = true;

            Logger.LogInformation("[ModCom {Endpoint}] {Path} → {Status} ({Counters}) (FileLog #{Id})",
                Descriptor.EndpointId, unit.RequestPath, status, counters.ToString(), fileLogId);

            return rows;
        }
        catch (OperationCanceledException)
        {
            // Per-unit timeout / run cancellation is already recorded by core.LoadLog; do not write a
            // spurious hub row on a spent token.
            throw;
        }
        catch (Exception)
        {
            if (!hubRowWritten)
                await TryUpsertFailedAsync(file, httpStatus).ConfigureAwait(false);
            throw; // LoaderPipelineBase records the LoadLog failure and moves on (fail-a-block-not-the-run).
        }
    }

    /// <summary>
    /// Classifies a non-2xx by <b>stable body substring</b>, not by whole sentence.
    ///
    /// <para><b>⚠⚠ The row-cap and history-cap messages MASK ONE ANOTHER.</b> A request that violates
    /// both (e.g. a 200-day <c>allTrades</c> window starting more than 6 months back) returns the
    /// <b>row-cap</b> message even though the start date is also out of range. That already caused
    /// one wrong conclusion during the API probe ("the history limit is 183 days"), so: <b>never
    /// infer one limit from the other's failure.</b> The row-cap message also interpolates the
    /// applicable cap (<c>…limit of 100000 rows…</c> for settlements), which is why the match is on
    /// <c>more than the limit of</c> rather than a full sentence.</para>
    /// </summary>
    internal (ModComFailureKind Kind, string Message) Classify(HttpStatusCode statusCode, string body, ModComWorkUnit unit)
    {
        var status = (int)statusCode;
        var text = body ?? string.Empty;

        if (statusCode == HttpStatusCode.Unauthorized)
            // The 401 body is EMPTY (zero bytes) — verified. Do not attempt to parse or quote it, and
            // NEVER name the credential values (design §4.2).
            return (ModComFailureKind.Auth,
                $"[ModCom {Descriptor.EndpointId}] HTTP 401 for {unit.RequestPath}: the configured Basic credential is wrong or " +
                "revoked. There is no token to refresh, so this is terminal and is not retried. Fix " +
                "core.Param(LoaderName='ModernCommodities', ParamName='Username'/'Password') or the matching " +
                "DATALOADER_Loaders__ModernCommodities__* environment variable.");

        if (statusCode == HttpStatusCode.Forbidden)
            return (ModComFailureKind.Auth,
                $"[ModCom {Descriptor.EndpointId}] HTTP 403 for {unit.RequestPath}: the credential authenticated but is not " +
                "entitled to this endpoint. Never observed during the API probe — raise it with the vendor.");

        if (statusCode == HttpStatusCode.NotFound)
            // NOT a data condition here (contrast NGI). It means the path or the /v1 segment is wrong.
            return (ModComFailureKind.Other,
                $"[ModCom {Descriptor.EndpointId}] HTTP 404 for {unit.RequestPath}: this is NOT a 'no data' outcome for this API — " +
                "emptiness arrives as a header-only 200. A 404 means the endpoint path or the '/v1' version segment is wrong " +
                $"(BaseUrl='{Settings.BaseUrlRoot()}', Path='{Descriptor.Path}') — a deployment error.");

        if (status == 400)
        {
            if (text.Contains("more than the limit of", StringComparison.OrdinalIgnoreCase))
                return (ModComFailureKind.RowCap, BuildRowCapMessage(unit, text));

            if (text.Contains("within the last six months", StringComparison.OrdinalIgnoreCase))
                return (ModComFailureKind.History,
                    $"[ModCom {Descriptor.EndpointId}] HTTP 400 HISTORY CAP for {unit.RequestPath}: \"{text}\". The requested " +
                    $"startDate {ModComTime.Iso(unit.WindowStart)} is older than the 6-calendar-month limit, which can only mean " +
                    "the ModComTime.HistoryFloor clamp is wrong or was bypassed (the clamp is today.AddMonths(-6).AddDays(1), " +
                    "NEVER a day count). Note the row-cap and history-cap messages mask one another — do not infer one from the other.");

            // Check the legalEntityName form BEFORE the generic "Invalid " prefix: its body starts
            // "Invalid legalEntityName, valid options: ..." and would otherwise be misclassified.
            if (text.Contains("Invalid legalEntityName", StringComparison.OrdinalIgnoreCase))
                return (ModComFailureKind.InvalidLegalEntity,
                    $"[ModCom {Descriptor.EndpointId}] HTTP 400 for {unit.RequestPath}: the configured " +
                    "Loaders:ModernCommodities:LegalEntityName is not a valid scope. The body enumerates the valid values " +
                    $"(it contains no secret): \"{text}\". Omitting the setting entirely returns BOTH ARM legal entities, " +
                    "which is the shipped default.");

            if (text.Contains("Invalid ", StringComparison.OrdinalIgnoreCase))
                return (ModComFailureKind.InvalidParam,
                    $"[ModCom {Descriptor.EndpointId}] HTTP 400 for {unit.RequestPath}: \"{text}\". A date parameter was rejected — " +
                    "this is OUR formatting bug. Both dates must be yyyy-MM-dd under CultureInfo.InvariantCulture.");

            return (ModComFailureKind.Other,
                $"[ModCom {Descriptor.EndpointId}] HTTP 400 for {unit.RequestPath} with an unclassified body: \"{text}\".");
        }

        return (ModComFailureKind.Other,
            $"[ModCom {Descriptor.EndpointId}] HTTP {status} for {unit.RequestPath}" +
            (text.Length > 0 ? $": \"{text}\"" : "."));
    }

    /// <summary>
    /// The row-cap message <b>names the remedy</b> (decision D11).
    ///
    /// <para>Anyone who "fixes" a row-cap <c>400</c> by moving <c>startDate</c> forward has fixed it
    /// for the wrong reason and hidden the real constraint: there is <b>no paging</b>, so narrowing
    /// the range is the only lever, and <c>ChunkDays</c> is the setting that does it.</para>
    /// </summary>
    private string BuildRowCapMessage(ModComWorkUnit unit, string body)
    {
        var days = unit.WindowEnd.DayNumber - unit.WindowStart.DayNumber + 1;
        var estimate = (long)Math.Round(days * Descriptor.EstimatedRowsPerCalendarDay);
        var chunkDays = Settings.EffectiveChunkDays(Descriptor.EndpointId);
        var recommended = Descriptor.ParseShape == ModComParseShape.Settlements ? 60 : 30;

        return
            $"[ModCom {Descriptor.EndpointId}] HTTP 400 ROW CAP for {unit.RequestPath}: \"{body}\". " +
            $"The requested window {ModComTime.Iso(unit.WindowStart)}..{ModComTime.Iso(unit.WindowEnd)} spans {days} calendar day(s), " +
            $"estimated ~{estimate} rows against this endpoint's cap of {Descriptor.RowCap}. " +
            $"There is NO paging — an over-cap request is rejected, not truncated — so the ONLY remedy is a narrower date range: " +
            $"reduce Loaders:ModernCommodities:Endpoints:{Descriptor.EndpointId}:ChunkDays " +
            $"(currently {(chunkDays <= 0 ? "0 = the whole window in one request" : chunkDays.ToString(ModComTime.Inv))}) — " +
            $"recommended {recommended} ({(Descriptor.ParseShape == ModComParseShape.Settlements ? "settlements" : "trades")}). " +
            "Do NOT 'fix' this by moving startDate forward.";
    }

    private static async Task<string> ReadBodySafeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return body?.Trim() ?? string.Empty;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // A body we cannot read must not mask the status code we already have.
            return string.Empty;
        }
    }

    private async Task TryUpsertFailedAsync(ModComFileContext file, int? httpStatus)
    {
        try
        {
            // Best-effort; CancellationToken.None so the Failed row is still written when the failure
            // was a timeout, and never mask the original exception.
            await _fileLog.UpsertAsync(file, "Failed", httpStatus, 0, 0, null, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[ModCom {Endpoint}] failed to write the 'Failed' FileLog row for {Path}",
                Descriptor.EndpointId, file.RequestPath);
        }
    }
}

/// <summary>
/// Reader for <c>allTrades/v1</c> <b>and</b> <c>myTrades/v1</c> — <b>one class, one row type</b>
/// (design §5.4). The descriptor supplies the path; only the <i>sink</i> differs between the two
/// endpoints.
///
/// <para><b>The 14 anonymised columns are read exactly the same way for both endpoints</b> — they
/// simply arrive empty from <c>allTrades</c>. <b>Do not branch on the endpoint.</b></para>
/// </summary>
public sealed class ModComTradesSourceReader : ModComCsvSourceReaderBase<TradeRow>
{
    public ModComTradesSourceReader(
        HttpClient http, ModComSettings settings, IModComFileLog fileLog,
        ModComEndpointDescriptor descriptor, ILogger logger)
        : base(http, settings, fileLog, descriptor, logger) { }

    protected override IReadOnlyList<string> ExpectedHeaders => ModComColumns.Trades;

    protected override IReadOnlyList<string> RequiredKeyColumns => new[] { ModComColumns.TradeNumber };

    private protected override IReadOnlyList<TradeRow> MapRecords(
        IReadOnlyList<string[]> dataRecords, ModComHeaderMap map, ModComWorkUnit unit, ModComParseCounters counters)
    {
        var f = new ModComFieldReader(map, counters, Logger, Descriptor.EndpointId);
        var rows = new List<TradeRow>(dataRecords.Count);

        foreach (var rec in dataRecords)
        {
            // 1. Trade Number IS the primary key and the merge key. Blank or unparseable → DROP the
            //    row and count it: an unkeyable row cannot be stored, and a key including anything
            //    else would insert a duplicate instead of correcting the existing row.
            if (ModComParse.Int32(f.Raw(rec, ModComColumns.TradeNumber), out var tradeNumber) != ModComParseOutcome.Ok)
            {
                counters.Dropped++;
                continue;
            }

            f.RowKey = tradeNumber.ToString(ModComTime.Inv);

            // 2. The remaining 33 columns, by LITERAL header name, through the §5.3 helpers. Nothing
            //    is derived, computed or inferred. Location / Pipeline/Terminal / Price Basis are
            //    blank on exactly the ProductType='Financial' rows — that is SEMANTIC (a financial
            //    contract has no delivery location), so they map to NULL with no warning.
            rows.Add(new TradeRow
            {
                TradeNumber = tradeNumber,
                State = f.Text(rec, ModComColumns.State, 50),
                Product = f.Text(rec, ModComColumns.Product, 50),
                Location = f.Text(rec, ModComColumns.Location, 50),
                PipelineTerminal = f.Text(rec, ModComColumns.PipelineTerminal, 256),
                PriceBasis = f.Text(rec, ModComColumns.PriceBasis, 256),
                Term = f.Text(rec, ModComColumns.Term, 256),
                TermStart = f.IsoDate(rec, ModComColumns.TermStart),
                TermEnd = f.IsoDate(rec, ModComColumns.TermEnd),
                Price = f.Dec92(rec, ModComColumns.Price),
                Volume = f.Dec92(rec, ModComColumns.Volume),
                UnitOfMeasure = f.Text(rec, ModComColumns.UnitOfMeasure, 50),
                Executed = f.Timestamp(rec, ModComColumns.ExecutedTimestamp),
                LastUpdated = f.Timestamp(rec, ModComColumns.LastUpdatedTimestamp),
                TradeType = f.Text(rec, ModComColumns.TradeType, 50),
                Side = f.Text(rec, ModComColumns.Side, 256),
                BidTrader = f.Text(rec, ModComColumns.BidTrader, 256),
                BidLegalName = f.Text(rec, ModComColumns.BidLegalName, 256),
                BidAddress = f.Text(rec, ModComColumns.BidAddress, 256),
                BidCommission = f.Dec92(rec, ModComColumns.BidCommission),
                OfferTrader = f.Text(rec, ModComColumns.OfferTrader, 256),
                OfferLegalName = f.Text(rec, ModComColumns.OfferLegalName, 256),
                OfferAddress = f.Text(rec, ModComColumns.OfferAddress, 256),
                OfferCommission = f.Dec92(rec, ModComColumns.OfferCommission),
                SpreadTradeNumber = f.Text(rec, ModComColumns.SpreadTradeNumber, 50),
                ApportionmentProtected = f.Bit(rec, ModComColumns.ApportionmentProtected),
                ClearingID = f.Text(rec, ModComColumns.ClearingId, 50),
                SettlementCurrency = f.Text(rec, ModComColumns.SettlementCurrency, 50),
                ContractTerms = f.Text(rec, ModComColumns.ContractTerms, 256),
                GTandC = f.Text(rec, ModComColumns.GTandC, 256),
                Notes = f.Text(rec, ModComColumns.Notes, 8000),
                InIndex = f.Bit(rec, ModComColumns.InIndex),
                // Click & Trade blank → NULL, NOT false: conflating them destroys the anonymisation
                // signal (all 98 allTrades rows would read "not a click trade" instead of "unknown").
                ClickAndTrade = f.Bit(rec, ModComColumns.ClickAndTrade),
                ProductType = f.Text(rec, ModComColumns.ProductType, 50)
            });
        }

        // 3. De-dup on TradeNumber, LAST WINS ORDERED BY LastUpdated (a dated copy beats an undated
        //    one). Structurally unnecessary within one request (0 duplicates in 98/98 and 37/37) but
        //    cheap — and it keeps the C# consistent with the proc's ORDER BY LastUpdated DESC
        //    tie-break, so the two can never disagree (design §5.4 step 3, §11.2).
        //
        //    Spread trades are persisted EXACTLY as published, never filtered or reconciled: a Spread
        //    arrives as a 3-row group sharing one Spread Trade Number, the parent's Volume is NOT
        //    additive with its legs' and its Price is a DIFFERENTIAL. Naively summing Volume over the
        //    table triple-counts spread volume; the loader does nothing about it and the validator
        //    surfaces it (design §5.4).
        return Dedup(rows, counters);
    }

    internal static IReadOnlyList<TradeRow> Dedup(IReadOnlyList<TradeRow> rows, ModComParseCounters counters)
    {
        var groups = rows.GroupBy(r => r.TradeNumber).ToList();
        if (groups.Count == rows.Count) return rows;

        counters.DuplicateKeys += rows.Count - groups.Count;

        // OrderBy is a STABLE sort, so Last() takes the highest LastUpdated and, among ties, the last
        // occurrence in file order. A NULL timestamp sorts lowest (DateTime.MinValue) and therefore
        // never beats a dated copy — the same precedence the merge proc's ORDER BY … DESC applies.
        return groups.Select(g => g.OrderBy(r => r.LastUpdated ?? DateTime.MinValue).Last()).ToList();
    }
}

/// <summary>
/// Reader for <c>settlements/v1</c> (design §5.5).
///
/// <para><b>All six PK columns are <c>NOT NULL</c></b> and were never blank in 1,443 rows
/// (settlements contains <b>zero</b> empty fields anywhere), but the guard exists anyway: blank —
/// or over-width — in any key column <b>drops the row and counts it</b> rather than inserting an
/// empty-string key or silently MERGEing onto a different logical entity.</para>
///
/// <para><b>⚠ <c>Location</c> / <c>Pipeline/Terminal</c> = <c>-</c> is persisted VERBATIM.</b> 82 of
/// 1,443 rows, always <b>as a pair</b>, all <c>Product = 'Sweet Guernsey Blend'</c>: one blended
/// product with no single physical location or pipeline. It is a <b>key value</b>, not a null
/// sentinel (design "Rationale B").</para>
///
/// <para><b>A weekend-only or unpublished-day window returning nothing is normal, not an error.</b>
/// Settlements publish on business days only and the current day's curve is routinely not yet
/// published when the loader runs — both are legitimate gaps inside a <c>200</c>, and because the
/// resume key is hot-only the day's curve is picked up by a later run automatically. That is why
/// there is no business-day/holiday calendar here: settlements are requested as a <b>range</b>, and
/// unpublished days simply contribute no rows.</para>
/// </summary>
public sealed class ModComSettlementsSourceReader : ModComCsvSourceReaderBase<SettlementRow>
{
    public ModComSettlementsSourceReader(
        HttpClient http, ModComSettings settings, IModComFileLog fileLog,
        ModComEndpointDescriptor descriptor, ILogger logger)
        : base(http, settings, fileLog, descriptor, logger) { }

    protected override IReadOnlyList<string> ExpectedHeaders => ModComColumns.Settlements;

    /// <summary>All six PK columns — the pull cannot be keyed without any one of them.</summary>
    protected override IReadOnlyList<string> RequiredKeyColumns => new[]
    {
        ModComColumns.SettlementDate, ModComColumns.Product, ModComColumns.Location,
        ModComColumns.PipelineTerminal, ModComColumns.PriceBasis, ModComColumns.Term
    };

    private protected override IReadOnlyList<SettlementRow> MapRecords(
        IReadOnlyList<string[]> dataRecords, ModComHeaderMap map, ModComWorkUnit unit, ModComParseCounters counters)
    {
        var f = new ModComFieldReader(map, counters, Logger, Descriptor.EndpointId);
        var rows = new List<SettlementRow>(dataRecords.Count);
        var overWidthKeyDrops = 0;

        foreach (var rec in dataRecords)
        {
            if (ModComParse.IsoDate(f.Raw(rec, ModComColumns.SettlementDate), out var settlementDate) != ModComParseOutcome.Ok)
            {
                counters.Dropped++;
                continue;
            }

            // Key columns are never truncated — a truncated key silently MERGEs onto a DIFFERENT
            // logical entity, which is worse than a missing row (design §5.3).
            var product = f.KeyText(rec, ModComColumns.Product, 50, out var w1);
            var location = f.KeyText(rec, ModComColumns.Location, 50, out var w2);
            var pieplineTerminal = f.KeyText(rec, ModComColumns.PipelineTerminal, 50, out var w3);   // [sic] target column spelling
            var priceBasis = f.KeyText(rec, ModComColumns.PriceBasis, 100, out var w4);
            var term = f.KeyText(rec, ModComColumns.Term, 50, out var w5);

            if (product is null || location is null || pieplineTerminal is null || priceBasis is null || term is null)
            {
                counters.Dropped++;
                if (w1 || w2 || w3 || w4 || w5) overWidthKeyDrops++;
                continue;
            }

            f.RowKey = $"{ModComTime.Iso(settlementDate)}|{product}|{location}|{pieplineTerminal}|{priceBasis}|{term}";

            rows.Add(new SettlementRow
            {
                SettlementDate = settlementDate,
                Product = product,
                // '-' arrives here VERBATIM: ModComParse.Text applies no sentinel remapping of any kind.
                Location = location,
                PieplineTerminal = pieplineTerminal,
                // 'USD $' contains a space and a '$' and is a KEY — no stripping, no normalisation.
                PriceBasis = priceBasis,
                // Always a single MMM-YY month here — never '/', '~' or a quarter (contrast the trades
                // Term). Persisted as published; the loader parses neither shape.
                Term = term,
                TermStart = f.IsoDate(rec, ModComColumns.TermStart),
                TermEnd = f.IsoDate(rec, ModComColumns.TermEnd),
                Price = f.Dec92(rec, ModComColumns.Price)
            });
        }

        if (overWidthKeyDrops > 0)
            Logger.LogError("[ModCom {Endpoint}] dropped {Count} row(s) whose KEY value exceeded its column width — " +
                            "a truncated key would MERGE onto a different logical row, so the row is dropped instead",
                Descriptor.EndpointId, overWidthKeyDrops);

        return Dedup(rows, counters);
    }

    /// <summary>
    /// De-dup on the 6-column key, <b>last in file order wins</b>. There is no recency column to
    /// order by — the payload carries no revision, version, status or as-of field of any kind. That
    /// is acceptable here (and not for trades) because a duplicate 6-column key <i>within one
    /// request</i> would mean the vendor published two prices for the same
    /// (date, product, location, pipeline, basis, term), which never occurred in 1,443 rows and is a
    /// vendor defect to report rather than arbitrate (design §11.3).
    /// </summary>
    internal static IReadOnlyList<SettlementRow> Dedup(IReadOnlyList<SettlementRow> rows, ModComParseCounters counters)
    {
        var groups = rows
            .GroupBy(r => (r.SettlementDate,
                           Product: r.Product.ToUpperInvariant(),
                           Location: r.Location.ToUpperInvariant(),
                           Piepline: r.PieplineTerminal.ToUpperInvariant(),
                           PriceBasis: r.PriceBasis.ToUpperInvariant(),
                           Term: r.Term.ToUpperInvariant()))
            .ToList();

        if (groups.Count == rows.Count) return rows;

        counters.DuplicateKeys += rows.Count - groups.Count;
        return groups.Select(g => g.Last()).ToList();
    }
}
