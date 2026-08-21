using System.Net;
using System.Text.Json;
using DataLoader.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace DataLoader.NGI;

// =============================================================================
// Custom JSON source readers (design §5).
//
// *** BOTH IMPLEMENT ISourceReader DIRECTLY — NOT HttpJsonSourceReaderBase. ***
// That base calls EnsureSuccessStatusCode(), which would THROW on the 404 that is
// NGI's MAJORITY outcome: Bidweek is a MONTHLY feed, so over the default 60-day
// window ~58 of 60 units legitimately 404. Treating a 404 as a failure would produce
// ~58 false failures per run and drown every real signal.
//
// Both readers use the registered "NGI" client (auth + retry + throttle applied by
// handlers), parse with System.Text.Json under the invariant culture, write their own
// arm.FileLog outcome row (they hold the HTTP status + row count) and stamp the
// returned FileLogId onto every produced row. Neither ever logs a credential: no NGI
// URL carries one (the secret is in the POST /auth body) and the paths logged are the
// sanitized descriptors.
// =============================================================================

/// <summary>
/// How one HTTP status is dispatched — design §5.4, applied identically by both readers.
/// </summary>
internal enum NgiStatusAction
{
    /// <summary>2xx — parse the body.</summary>
    Parse,

    /// <summary>404 — a successful, EMPTY read: <c>NotAvailable</c> hub row, 0 rows, the unit SUCCEEDS.</summary>
    NotAvailable,

    /// <summary>Anything else — throw (the catch writes a <c>Failed</c> hub row and rethrows).</summary>
    Throw
}

/// <summary>The shared status matrix (design §5.4), so the two readers cannot drift apart.</summary>
internal static class NgiStatus
{
    /// <summary>
    /// <list type="bullet">
    ///   <item><b>200</b> → parse. The body is the complete result set (neither endpoint pages).</item>
    ///   <item><b>404</b> → <c>NotAvailable</c>, <c>RowCount = 0</c>, <b>the work unit SUCCEEDS</b>.
    ///     <b>THE NORMAL CASE</b> for <c>BidWeekData</c> (~58 of 60 units). Never a failure, never a
    ///     retry, never an alert. See design §3.5 for the settled-zone invariant this creates.</item>
    ///   <item><b>400</b> → <b>THROW</b>. A malformed <c>issue_date</c> can only mean the loader
    ///     formatted the date wrong: it is OUR bug, unfixable by retry, and must never be swallowed
    ///     like a 404.</item>
    ///   <item><b>401</b> → reaching here means <see cref="NgiTokenAuthHandler"/> already re-minted and
    ///     replayed once, so this is a genuine credential failure → throw.</item>
    ///   <item><b>403</b> → entitlement/subscription → throw.</item>
    ///   <item><b>429 / 5xx</b> → Polly has already exhausted its retries → throw (the unit fails,
    ///     the run continues).</item>
    /// </list>
    /// </summary>
    public static NgiStatusAction Classify(HttpStatusCode status)
    {
        if ((int)status is >= 200 and < 300) return NgiStatusAction.Parse;
        if (status == HttpStatusCode.NotFound) return NgiStatusAction.NotAvailable;
        return NgiStatusAction.Throw;
    }

    /// <summary>A message that names the likely cause, so a 400 is not mistaken for a data problem.</summary>
    public static string Describe(string label, string path, int httpStatus) => httpStatus switch
    {
        400 => $"[NGI {label}] {path} returned HTTP 400 — malformed request (issue_date must be yyyy-MM-dd, " +
               "invariant culture). This is a LOADER BUG, not a data condition.",
        401 => $"[NGI {label}] {path} returned HTTP 401 after a token re-mint and replay — verify " +
               "core.Param(LoaderName='NGI', ParamName='Username'/'Password').",
        403 => $"[NGI {label}] {path} returned HTTP 403 — the NGI account is not entitled to this feed.",
        _ => $"[NGI {label}] {path} returned HTTP {httpStatus}"
    };
}

/// <summary>
/// Endpoint 2 — <c>GET /bidweekLocations?format=json</c> (design §5.2). Produces the name↔code
/// crosswalk rows for <c>arm.BidWeekLocation</c>.
///
/// <para>🛑 <b>DIRECTION-CRITICAL: THE JSON KEY IS THE NAME, THE VALUE IS THE POINT CODE.</b>
/// <c>{"Bidweek Locations":{"Agua Dulce":"STXAGUAD"}}</c> → <c>PointCode = "STXAGUAD"</c>,
/// <c>LocationName = "Agua Dulce"</c>. Inverting this is the single easiest bug in this loader and
/// produces <b>no exception, no parse error and no warning</b> — just 163 rows with the name in
/// <c>PointCode</c> and the code in <c>LocationName</c>, after which every join silently misses. The
/// vendor spec declares "No response body" for this endpoint, so the unit test against
/// <c>tests/DataLoader.NGI.Tests/Samples/bidweekLocations.json</c> is the only regression net.</para>
/// </summary>
public sealed class NgiLocationsSourceReader : ISourceReader<NgiLocationsWorkUnit, BidWeekLocationRow>
{
    /// <summary>
    /// <c>format=json</c> is sent EXPLICITLY — do not rely on the server default. Note the path-style
    /// inconsistency with endpoint 1, which carries its format as a path extension
    /// (<c>/bidweekDatafeed.json</c>): do not "normalise" the two into one shape.
    /// </summary>
    private const string RelativePath = "/bidweekLocations?format=json";

    private readonly HttpClient _http;
    private readonly NgiSettings _settings;
    private readonly INgiFileLog _fileLog;
    private readonly ILogger _logger;

    public NgiLocationsSourceReader(HttpClient http, NgiSettings settings, INgiFileLog fileLog, ILogger logger)
    {
        _http = http;
        _settings = settings;
        _fileLog = fileLog;
        _logger = logger;
    }

    public async Task<IReadOnlyList<BidWeekLocationRow>> ReadAsync(NgiLocationsWorkUnit unit, CancellationToken cancellationToken)
    {
        var requestUri = new Uri($"{_settings.BaseUrlRoot()}{RelativePath}", UriKind.Absolute);
        var file = new NgiFileContext(NgiEndpoints.BidWeekLocations, RepresentativeDate: null, RelativePath);

        int? httpStatus = null;
        try
        {
            _logger.LogDebug("[NGI BidWeekLocations] GET {Path}", RelativePath); // no credential in any NGI URL
            using var response = await _http.GetAsync(requestUri, cancellationToken).ConfigureAwait(false);
            httpStatus = (int)response.StatusCode;

            switch (NgiStatus.Classify(response.StatusCode))
            {
                case NgiStatusAction.NotAvailable:
                    // Unexpected for this endpoint (it is an undated snapshot with no date to miss), so
                    // warn — but still a successful empty read per the shared matrix (design §5.4).
                    _logger.LogWarning("[NGI BidWeekLocations] unexpected 404 for {Path}; treating as an empty snapshot", RelativePath);
                    await _fileLog.UpsertAsync(file, NgiFileStatus.NotAvailable, httpStatus, RelativePath, 0, cancellationToken).ConfigureAwait(false);
                    return Array.Empty<BidWeekLocationRow>();

                case NgiStatusAction.Throw:
                    throw new HttpRequestException(NgiStatus.Describe("BidWeekLocations", RelativePath, httpStatus.Value));
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var counters = new NgiParseCounters();
            var rows = ParseLocations(body, counters);

            var status = rows.Count == 0 ? NgiFileStatus.NotAvailable : NgiFileStatus.Success;
            if (rows.Count == 0)
                // Unexpected: NGI 404s rather than returning an empty set (design §5.2 step 3).
                _logger.LogWarning("[NGI BidWeekLocations] {Path}: HTTP {Status} but 0 crosswalk entries parsed", RelativePath, httpStatus);

            var fileLogId = await _fileLog.UpsertAsync(file, status, httpStatus, RelativePath, rows.Count, cancellationToken).ConfigureAwait(false);
            foreach (var r in rows) r.FileLogId = fileLogId;

            if (!counters.IsClean)
                _logger.LogWarning("[NGI BidWeekLocations] {Path} tolerance counters: {Counters}", RelativePath, counters);

            _logger.LogDebug("[NGI BidWeekLocations] {Path} → {Rows} crosswalk row(s) (FileLog #{Id})", RelativePath, rows.Count, fileLogId);
            return rows;
        }
        catch (OperationCanceledException)
        {
            throw; // no FileLog write on a spent token
        }
        catch (Exception)
        {
            await TryUpsertFailedAsync(file, RelativePath, httpStatus, "BidWeekLocations").ConfigureAwait(false);
            throw; // LoaderPipelineBase records the LoadLog failure and the run continues
        }
    }

    /// <summary>
    /// Locates the single top-level node (<c>"Bidweek Locations"</c> — note the <b>space</b> and the
    /// <b>capital L</b>) and projects each property to a row.
    /// </summary>
    private List<BidWeekLocationRow> ParseLocations(string body, NgiParseCounters counters)
    {
        // An empty (or whitespace-only) 2xx body is shape drift as well. Without this guard
        // JsonDocument.Parse surfaces it as the opaque "The input does not contain any JSON tokens",
        // which reads like a loader bug; the outcome is otherwise unchanged (Failed hub row, failed
        // unit) so the message is the same drift wording every other drift path uses.
        if (string.IsNullOrWhiteSpace(body))
            throw new InvalidOperationException(
                "[NGI BidWeekLocations] locations envelope shape drift — expected a top-level " +
                "'Bidweek Locations' JSON object mapping location NAME → point CODE; the response body was empty.");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var node = root.ValueKind == JsonValueKind.Object
            ? NgiParse.Prop(root, NgiFields.BidweekLocations)
            : null;

        // Node ABSENT, or present but NOT a JSON object → shape drift → THROW. A silent zero-row
        // success here is exactly the failure mode docs/apis/NGI.md was written to prevent (the vendor
        // spec declares "No response body", so there is no schema to catch it). The caller's catch
        // writes the Failed hub row.
        if (node is null || node.Value.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException(
                "[NGI BidWeekLocations] locations envelope shape drift — expected a top-level " +
                "'Bidweek Locations' JSON object mapping location NAME → point CODE.");

        var rows = new List<BidWeekLocationRow>(200);
        var byCode = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var droppedBlank = 0;
        var truncatedNames = 0;

        foreach (var entry in node.Value.EnumerateObject())
        {
            // 🛑 KEY = LocationName, VALUE = PointCode. Codes are UPPERCASE and unspaced; the
            // uppercase side is always the VALUE. Do NOT swap these two lines.
            //
            // Both sides go through the KEY-field sentinel test (blank or the literal "None" only),
            // NOT the wide measure-field list: a point code spelled "NA" is a plausible real code and
            // a location genuinely named "-" is plausible too, and on a key a false positive drops the
            // record outright (see NgiParse.KeyStr).
            var locationName = NgiParse.IsKeyNullSentinel(entry.Name) ? null : entry.Name.Trim();

            // ⚠ THE (JsonElement?) CAST IS LOAD-BEARING. entry.Value is a NON-nullable JsonElement, so
            // an uncast one-argument call used to bind to the candidate-name overload (identity
            // conversion beats the JsonElement → JsonElement? lift) with an EMPTY name array: Prop saw
            // a non-object element, returned null, and EVERY one of the 163 entries silently dropped —
            // HTTP 200, no exception, and arm.BidWeekLocation never got a row. The cast pins the
            // bare-scalar overload; NgiParse's candidate-name overload also lost its `params`, so the
            // arities no longer overlap and this class of mistake can no longer compile.
            var pointCode = NgiParse.KeyStr((JsonElement?)entry.Value);

            if (string.IsNullOrWhiteSpace(pointCode))
            {
                // The code is the merge key — drop and count (design §5.2 step 4).
                counters.Dropped++;
                droppedBlank++;
                continue;
            }

            // ⚠ THE MERGE KEY IS NEVER TRUNCATED. Clamping an over-long code to VARCHAR(20) would
            // silently merge this entry onto a DIFFERENT point's row, so it is dropped-and-counted
            // instead — with its own warning naming the offending code (contrast the non-key fields
            // below, which are clamped so the row survives).
            if (pointCode!.Length > NgiWidths.PointCode)
            {
                counters.Dropped++;
                _logger.LogWarning(
                    "[NGI BidWeekLocations] dropped location '{Name}': point code '{Code}' is {Length} chars, longer " +
                    "than the arm.BidWeekLocation.PointCode VARCHAR({Max}) merge key — a shortened key would merge " +
                    "onto a different point",
                    locationName, pointCode, pointCode.Length, NgiWidths.PointCode);
                continue;
            }

            // LocationName is NOT the key: clamp to the column width so an NGI-side widening degrades
            // the value instead of failing the whole unit on a server-side truncation SqlException
            // (one aggregate warning per parse, below — never one per row).
            if (locationName is not null && locationName.Length > NgiWidths.LocationName)
            {
                locationName = locationName[..NgiWidths.LocationName];
                counters.Truncated++;
                truncatedNames++;
            }

            // Dedup on PointCode, last wins — belt-and-braces (the sink and the proc dedup too). A
            // duplicate code would mean NGI mapped one code onto two names.
            if (byCode.TryGetValue(pointCode, out var existing))
            {
                counters.DuplicateKeys++;
                _logger.LogWarning(
                    "[NGI BidWeekLocations] duplicate point code {Code} (names '{Old}' / '{New}'); keeping the last",
                    pointCode, rows[existing].LocationName, locationName);
                rows[existing] = new BidWeekLocationRow { PointCode = pointCode, LocationName = locationName };
                continue;
            }

            byCode[pointCode] = rows.Count;
            rows.Add(new BidWeekLocationRow { PointCode = pointCode, LocationName = locationName });
        }

        if (droppedBlank > 0)
            _logger.LogWarning("[NGI BidWeekLocations] dropped {Count} entr(y/ies) with a blank point code", droppedBlank);

        if (truncatedNames > 0)
            _logger.LogWarning(
                "[NGI BidWeekLocations] clamped {Count} over-long LocationName value(s) to {Max} chars — the row is " +
                "KEPT (a non-key field degrades; only an unusable key drops a record)",
                truncatedNames, NgiWidths.LocationName);

        return rows;
    }

    private async Task TryUpsertFailedAsync(NgiFileContext file, string relativePath, int? httpStatus, string label)
    {
        try
        {
            await _fileLog.UpsertAsync(file, NgiFileStatus.Failed, httpStatus, relativePath, 0, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[NGI {Label}] failed to write the 'Failed' FileLog row for {Path}", label, relativePath);
        }
    }
}

/// <summary>
/// Endpoint 1 — <c>GET /bidweekDatafeed.json?issue_date=YYYY-MM-DD</c> (design §5.3). Produces the
/// price/volume fact rows for <c>arm.BidWeekData</c>.
///
/// <para><b>⚠ NEVER call the parameterless <c>/bidweekDatafeed.json</c>.</b> Omitting
/// <c>issue_date</c> returns "the latest issue", which is <b>non-deterministic</b>: the work-unit
/// <c>Key</c> could not encode what was actually fetched, breaking the <c>core.LoadLog</c> idempotency
/// contract (two runs with the same key could return different issues; a settled key would pin the
/// wrong issue forever). Today's window already covers the latest issue date, so the parameterless
/// form buys nothing — it is a one-off human diagnostic only.</para>
///
/// <para><b>404 is the normal case</b> and completes the unit successfully with zero rows; <b>400
/// throws</b> (a caller bug); <c>data</c> present but not a JSON object throws (shape drift).</para>
/// </summary>
public sealed class NgiBidWeekSourceReader : ISourceReader<NgiBidWeekWorkUnit, BidWeekDataRow>
{
    private readonly HttpClient _http;
    private readonly NgiSettings _settings;
    private readonly INgiFileLog _fileLog;
    private readonly ILogger _logger;

    public NgiBidWeekSourceReader(HttpClient http, NgiSettings settings, INgiFileLog fileLog, ILogger logger)
    {
        _http = http;
        _settings = settings;
        _fileLog = fileLog;
        _logger = logger;
    }

    public async Task<IReadOnlyList<BidWeekDataRow>> ReadAsync(NgiBidWeekWorkUnit unit, CancellationToken cancellationToken)
    {
        // Precomputed by the provider: "/bidweekDatafeed.json?issue_date={yyyy-MM-dd}" (invariant —
        // a wrong format is a 400). No credential; safe to log verbatim.
        var relativePath = unit.RequestPath;
        var requestUri = new Uri($"{_settings.BaseUrlRoot()}{relativePath}", UriKind.Absolute);
        var file = new NgiFileContext(NgiEndpoints.BidWeekData, unit.IssueDate, relativePath);

        int? httpStatus = null;
        try
        {
            _logger.LogDebug("[NGI BidWeekData] GET {Path}", relativePath);
            using var response = await _http.GetAsync(requestUri, cancellationToken).ConfigureAwait(false);
            httpStatus = (int)response.StatusCode;

            switch (NgiStatus.Classify(response.StatusCode))
            {
                case NgiStatusAction.NotAvailable:
                    // THE NORMAL CASE — Bidweek is monthly, so ~58 of 60 units land here. Debug, not
                    // warning: this must never look like a problem (design §5.4).
                    _logger.LogDebug("[NGI BidWeekData] 404 no publication on {Date} ({Path})", NgiTime.Iso(unit.IssueDate), relativePath);
                    await _fileLog.UpsertAsync(file, NgiFileStatus.NotAvailable, httpStatus, relativePath, 0, cancellationToken).ConfigureAwait(false);
                    return Array.Empty<BidWeekDataRow>();

                case NgiStatusAction.Throw:
                    throw new HttpRequestException(NgiStatus.Describe("BidWeekData", relativePath, httpStatus.Value));
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var counters = new NgiParseCounters();
            var rows = ParseDatafeed(unit, body, counters);

            var status = rows.Count == 0 ? NgiFileStatus.NotAvailable : NgiFileStatus.Success;
            var fileLogId = await _fileLog.UpsertAsync(file, status, httpStatus, relativePath, rows.Count, cancellationToken).ConfigureAwait(false);

            if (!counters.IsClean)
                _logger.LogWarning("[NGI BidWeekData] {Path} tolerance counters: {Counters}", relativePath, counters);

            if (rows.Count == 0)
                return Array.Empty<BidWeekDataRow>();

            foreach (var r in rows) r.FileLogId = fileLogId;
            _logger.LogDebug("[NGI BidWeekData] {Path} → {Rows} row(s) (FileLog #{Id})", relativePath, rows.Count, fileLogId);
            return rows;
        }
        catch (OperationCanceledException)
        {
            throw; // no FileLog write on a spent token
        }
        catch (Exception)
        {
            await TryUpsertFailedAsync(file, relativePath, httpStatus).ConfigureAwait(false);
            throw; // LoaderPipelineBase records the LoadLog failure and continues to the next unit
        }
    }

    private List<BidWeekDataRow> ParseDatafeed(NgiBidWeekWorkUnit unit, string body, NgiParseCounters counters)
    {
        // An empty (or whitespace-only) 2xx body is envelope drift as well. Without this guard
        // JsonDocument.Parse surfaces it as the opaque "The input does not contain any JSON tokens",
        // which reads like a loader bug; the outcome is otherwise unchanged (Failed hub row, failed
        // unit) so the message is the same drift wording the other drift paths use.
        if (string.IsNullOrWhiteSpace(body))
            throw new InvalidOperationException(
                $"[NGI BidWeekData] {unit.RequestPath}: response envelope shape drift — expected a JSON object " +
                "of the form {meta, data}; the response body was empty.");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException(
                $"[NGI BidWeekData] {unit.RequestPath}: response envelope shape drift — expected a JSON object " +
                "of the form {meta, data}.");

        var meta = ReadMeta(root, counters);
        var data = NgiParse.Prop(root, NgiFields.Data);

        // ⚠ LOAD-BEARING GUARD: `data` is a MAP keyed by point code, NOT an array. A "tolerant" reader
        // that quietly yielded zero records from an array would report a SUCCESSFUL EMPTY LOAD — the
        // exact silent-corruption mode this loader is written to prevent. The caller's catch writes the
        // Failed hub row (design §5.3).
        if (data is not null && data.Value.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException(
                $"[NGI BidWeekData] {unit.RequestPath}: datafeed 'data' shape drift — expected an OBJECT keyed by " +
                $"point code, got {data.Value.ValueKind}.");

        var rows = new List<BidWeekDataRow>(200);

        if (data is null || !data.Value.EnumerateObject().Any())
        {
            // Unexpected — NGI 404s when there is no publication (design §5.3 step 3).
            _logger.LogWarning(
                "[NGI BidWeekData] {Path}: HTTP 200 but 'data' is absent/empty; treating as no publication",
                unit.RequestPath);
            return rows;
        }

        var byKey = new Dictionary<(DateOnly, string), int>();
        var droppedUnkeyable = 0;
        var truncatedCount = 0;
        var truncatedFields = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var entry in data.Value.EnumerateObject())
        {
            var mapKey = entry.Name?.Trim();
            var record = entry.Value;
            if (record.ValueKind != JsonValueKind.Object)
            {
                counters.Dropped++;
                droppedUnkeyable++;
                continue;
            }

            // ---- PointCode: prefer the record's own field, fall back to the map key -------------
            // KeyStr / IsKeyNullSentinel, NOT Str / IsNullSentinel: on the merge key only a BLANK
            // value or the vendor's literal "None" may mean NULL. The wide sentinel list's defensive
            // extras would DROP a record whose real code happened to be spelled "NA" (NgiParse.KeyStr).
            var pointCode = NgiParse.KeyStr(record, NgiFields.PointCode);
            if (string.IsNullOrWhiteSpace(pointCode))
            {
                if (NgiParse.IsKeyNullSentinel(mapKey))
                {
                    counters.Dropped++;   // unkeyable: neither the record field nor the map key is usable
                    droppedUnkeyable++;
                    continue;
                }
                pointCode = mapKey;
                counters.MapKeyFallback++;
            }
            else if (!NgiParse.IsKeyNullSentinel(mapKey) &&
                     !string.Equals(mapKey, pointCode, StringComparison.OrdinalIgnoreCase))
            {
                // Persist the RECORD's Point Code, never the key (design §5.3).
                counters.MapKeyMismatch++;
            }

            // ⚠ THE MERGE KEY IS NEVER TRUNCATED. Clamping an over-long code to VARCHAR(20) would
            // silently merge this record onto a DIFFERENT point's row, so it is dropped-and-counted
            // instead — with its own warning naming the offending code (contrast Region/PricingPoint
            // below, which are clamped so the row survives).
            if (pointCode!.Length > NgiWidths.PointCode)
            {
                counters.Dropped++;
                _logger.LogWarning(
                    "[NGI BidWeekData] {Path}: dropped record with point code '{Code}' — {Length} chars, longer than " +
                    "the arm.BidWeekData.PointCode VARCHAR({Max}) merge key; a shortened key would merge onto a " +
                    "different point",
                    unit.RequestPath, pointCode, pointCode.Length, NgiWidths.PointCode);
                continue;
            }

            // ---- IssueDate (PK component): record → meta → the REQUESTED date -------------------
            var issueDate = Date(record, NgiFields.IssueDate) ?? meta.IssueDate ?? unit.IssueDate;
            if (issueDate != unit.IssueDate)
                counters.IssueDateMismatch++;

            // ---- Survey window: record → meta → NULL. READ IT, NEVER COMPUTE IT ----------------
            // Both the offset from the issue date AND the window length vary month to month (offset
            // 5/7/10 days, length 3/3/6 days across the three probed issues), so a hard-coded offset
            // would have been wrong in 2 of 3 observed months.
            var surveyStart = Date(record, NgiFields.SurveyStart) ?? meta.StartDate;
            var surveyEnd = Date(record, NgiFields.SurveyEnd) ?? meta.EndDate;
            if (surveyStart is null || surveyEnd is null)
                counters.MissingSurveyWindow++;

            var row = new BidWeekDataRow
            {
                IssueDate = issueDate,
                PointCode = pointCode!,
                SurveyStart = surveyStart,
                SurveyEnd = surveyEnd,
                // Take Region from the FIELD, never derive it from PointCode: the code prefix is a
                // legacy artefact that frequently disagrees (NEALEB is Midwest, MCWNIAGR is Northeast,
                // SLAFGTZ3 is Southeast, ETXTGT is North Louisiana/Arkansas). An unrecognised value is
                // persisted as published — the 14 observed values are NOT an enum and must never gate
                // the load. Non-key, so an over-long value is CLAMPED to the column width rather than
                // failing the whole unit on a server-side truncation SqlException.
                Region = Clamp(NgiParse.Str(record, NgiFields.Region), NgiWidths.Region, "Region"),
                PricingPoint = Clamp(NgiParse.Str(record, NgiFields.PricingPoint), NgiWidths.PricingPoint, "PricingPoint"),
                Low = Dec(record, NgiFields.Low),
                High = Dec(record, NgiFields.High),
                // Average is DEAL-WEIGHTED, not the Low/High midpoint — read it, never compute it.
                Average = Dec(record, NgiFields.Average),
                Volume = Int(record, NgiFields.Volume),
                Deals = Int(record, NgiFields.Deals)
            };

            // Dedup on (IssueDate, PointCode), last wins. Structurally impossible from a JSON object
            // (duplicate keys cannot be expressed) but cheap insurance; the sink and the proc dedup
            // identically.
            var key = (row.IssueDate, row.PointCode.ToUpperInvariant());
            if (byKey.TryGetValue(key, out var existing))
            {
                counters.DuplicateKeys++;
                rows[existing] = row;
                continue;
            }

            byKey[key] = rows.Count;
            rows.Add(row);
        }

        if (droppedUnkeyable > 0)
            _logger.LogWarning(
                "[NGI BidWeekData] {Path}: dropped {Count} unkeyable record(s) (no usable point code)",
                unit.RequestPath, droppedUnkeyable);

        // ONE aggregate warning per parse (never one per row), naming the field(s) that were clamped.
        if (truncatedCount > 0)
            _logger.LogWarning(
                "[NGI BidWeekData] {Path}: clamped {Count} over-long value(s) in {Fields} to the target column " +
                "width(s) — the row is KEPT (a non-key field degrades; only an unusable key drops a record)",
                unit.RequestPath, truncatedCount, string.Join("/", truncatedFields));

        return rows;

        // ---- local helpers: parse + count in one call ------------------------------------------
        DateOnly? Date(JsonElement rec, string[] names)
        {
            var v = NgiParse.Date(rec, names, out var bad);
            if (bad) counters.UnparseableDate++;
            return v;
        }

        decimal? Dec(JsonElement rec, string[] names)
        {
            var v = NgiParse.Dec(rec, names, out var bad);
            if (bad) counters.UnparseableNumeric++;
            return v;
        }

        int? Int(JsonElement rec, string[] names)
        {
            var v = NgiParse.Int(rec, names, out var bad);
            if (bad) counters.UnparseableNumeric++;
            return v;
        }

        // Non-key width guard: a value longer than its target column is SHORTENED and counted, so the
        // row still loads. The alternative is a server-side truncation SqlException from the MERGE,
        // which fails the ENTIRE work unit (all ~163 rows for the issue date) and keeps failing every
        // run — the opposite of the tolerant-parse contract for a non-key field.
        string? Clamp(string? value, int maxLength, string fieldName)
        {
            if (value is null || value.Length <= maxLength) return value;
            counters.Truncated++;
            truncatedCount++;
            truncatedFields.Add(fieldName);
            return value[..maxLength];
        }
    }

    /// <summary>
    /// Reads the <c>meta</c> block tolerantly. It is <b>not persisted</b> (all three values were
    /// verified identical to the record fields on all 163 records) — it serves only as a fallback
    /// source for a missing record field and as a validation assertion (design §8.2).
    /// </summary>
    private static NgiMeta ReadMeta(JsonElement root, NgiParseCounters counters)
    {
        var meta = NgiParse.Prop(root, NgiFields.Meta);
        if (meta is null || meta.Value.ValueKind != JsonValueKind.Object)
            return new NgiMeta(null, null, null);

        var issue = NgiParse.Date(meta.Value, NgiFields.MetaIssueDate, out var badIssue);
        var start = NgiParse.Date(meta.Value, NgiFields.MetaStartDate, out var badStart);
        var end = NgiParse.Date(meta.Value, NgiFields.MetaEndDate, out var badEnd);
        if (badIssue) counters.UnparseableDate++;
        if (badStart) counters.UnparseableDate++;
        if (badEnd) counters.UnparseableDate++;

        return new NgiMeta(issue, start, end);
    }

    private async Task TryUpsertFailedAsync(NgiFileContext file, string relativePath, int? httpStatus)
    {
        try
        {
            await _fileLog.UpsertAsync(file, NgiFileStatus.Failed, httpStatus, relativePath, 0, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[NGI BidWeekData] failed to write the 'Failed' FileLog row for {Path}", relativePath);
        }
    }
}
