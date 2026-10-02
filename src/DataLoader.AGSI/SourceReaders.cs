using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using DataLoader.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace DataLoader.AGSI;

// =============================================================================
// Custom JSON source readers (design §4). Both implement ISourceReader directly
// (NOT HttpJsonSourceReaderBase, which calls EnsureSuccessStatusCode and would
// throw on the 404/no-data cases we must tolerate). They reuse the registered
// rate-limited HttpClient + Polly policy, write the arm.FileLog outcome row
// themselves, and NEVER log the x-key value or a URL carrying a secret.
// =============================================================================

/// <summary>
/// Endpoint 1 — <c>GET /api/about</c> (no key). Walks the object tree
/// <c>SSO → region → country → [entities]</c>, projects each entity's <c>data</c>
/// to <c>(Code, Name, ParentCode, ParentName)</c>, skips entities with a null
/// country, and dedups to one tuple per <c>Code</c> (design §4.1). A non-2xx is
/// loud (writes a Failed hub row then throws — this is the public discovery call).
/// </summary>
public sealed class AgsiEntitiesSourceReader : ISourceReader<AgsiEntitiesWorkUnit, GasStorageEntityRow>
{
    private const string RelativePath = "/api/about";

    private readonly HttpClient _http;
    private readonly AgsiSettings _settings;
    private readonly IAgsiFileLog _fileLog;
    private readonly ILogger _logger;

    public AgsiEntitiesSourceReader(HttpClient http, AgsiSettings settings, IAgsiFileLog fileLog, ILogger logger)
    {
        _http = http;
        _settings = settings;
        _fileLog = fileLog;
        _logger = logger;
    }

    public async Task<IReadOnlyList<GasStorageEntityRow>> ReadAsync(AgsiEntitiesWorkUnit unit, CancellationToken cancellationToken)
    {
        var requestUri = new Uri($"{_settings.BaseUrl.TrimEnd('/')}{RelativePath}", UriKind.Absolute);
        var file = new AgsiFileContext("About", Region: null, RepresentativeDate: null, RelativePath);

        int? httpStatus = null;
        try
        {
            _logger.LogDebug("[AGSI About] GET {Path}", RelativePath); // no key on this endpoint
            using var response = await _http.GetAsync(requestUri, cancellationToken).ConfigureAwait(false);
            httpStatus = (int)response.StatusCode;

            if (!response.IsSuccessStatusCode)
                // The discovery call must be loud; the catch writes the Failed hub row.
                throw new HttpRequestException($"[AGSI About] {RelativePath} returned HTTP {httpStatus}");

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var rows = ParseEntities(json);

            // A 2xx that yields ZERO entities means the mandatory discovery endpoint is
            // structurally broken. Treat it as a FAILURE (the catch writes a Failed hub row and
            // rethrows) rather than NotAvailable+success: with HotZoneKeyStrategy=RunDate a
            // NotAvailable+success would mark discovery "done" and skip it for the rest of the
            // calendar day. Throwing lets it retry in-day and surfaces loudly.
            if (rows.Count == 0)
                throw new InvalidOperationException(
                    $"[AGSI About] {RelativePath}: HTTP {httpStatus} but 0 country entities parsed — the discovery endpoint appears broken");

            var fileLogId = await _fileLog.UpsertAsync(file, "Success", httpStatus, RelativePath, rows.Count, cancellationToken).ConfigureAwait(false);
            foreach (var r in rows) r.FileLogId = fileLogId;
            _logger.LogDebug("[AGSI About] {Path} → {Rows} distinct country row(s) (FileLog #{Id})", RelativePath, rows.Count, fileLogId);
            return rows;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            await TryUpsertFailedAsync(file, RelativePath, httpStatus).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Walk <c>SSO → region → country → [entities]</c> via <see cref="JsonNode"/> (do not trust
    /// the map keys — the authoritative values live in each entity's <c>data</c>). Skip an entity
    /// whose <c>data.country.code</c> is null/blank; dedup to one row per <c>Code</c>.
    /// </summary>
    private List<GasStorageEntityRow> ParseEntities(string json)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<GasStorageEntityRow>();

        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "[AGSI About] response was not valid JSON");
            return rows;
        }

        if (root?["SSO"] is not JsonObject sso) return rows;

        foreach (var region in sso)                          // region-name keys
        {
            if (region.Value is not JsonObject countries) continue;
            foreach (var country in countries)               // country-name keys
            {
                if (country.Value is not JsonArray entities) continue;
                foreach (var entity in entities)
                {
                    if (entity is not JsonObject e || e["data"] is not JsonObject data) continue;

                    var code = Str(data["country"]?["code"]);
                    var name = Str(data["country"]?["name"]);
                    var parentCode = Str(data["code"]);
                    var parentName = Str(data["name"]);

                    if (string.IsNullOrWhiteSpace(code)) continue;      // skip null-country entities defensively
                    if (!seen.Add(code.Trim())) continue;               // dedup on Code (Austria's 6 SSOs → one row)

                    rows.Add(new GasStorageEntityRow
                    {
                        Code = code.Trim(),
                        Name = (name ?? code).Trim(),
                        ParentCode = (parentCode ?? string.Empty).Trim(),
                        ParentName = (parentName ?? string.Empty).Trim()
                    });
                }
            }
        }

        return rows;
    }

    private string? Str(JsonNode? node)
    {
        if (node is null) return null;
        try { return node.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : node.ToString(); }
        catch (Exception ex)
        {
            // Unexpected node shape (not a scalar we can stringify) — log at Trace and treat as
            // null rather than silently swallowing, so a data-shape change stays diagnosable.
            _logger.LogTrace(ex, "[AGSI About] could not read a scalar string from a JSON node; treating as null");
            return null;
        }
    }

    private async Task TryUpsertFailedAsync(AgsiFileContext file, string relativePath, int? httpStatus)
    {
        try
        {
            await _fileLog.UpsertAsync(file, "Failed", httpStatus, relativePath, 0, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[AGSI About] failed to write 'Failed' FileLog for {Path}", relativePath);
        }
    }
}

/// <summary>
/// Endpoint 2 — <c>GET /api?country=&amp;date=</c> with header <c>x-key</c>. 404 /
/// empty <c>data[]</c> / a lone <c>status:"N"</c> record → NotAvailable (0 rows, no
/// unit failure); 401/403 → throw (bad/absent key); 429/5xx after retries → throw
/// (unit fails, run continues). Builds a <see cref="GasStorageRow"/> for each real
/// <c>data[]</c> element with tolerant numeric parsing (design §4.2).
/// </summary>
public sealed class AgsiStorageSourceReader : ISourceReader<AgsiStorageWorkUnit, GasStorageRow>
{
    private readonly HttpClient _http;
    private readonly AgsiSettings _settings;
    private readonly IAgsiFileLog _fileLog;
    private readonly ILogger _logger;

    public AgsiStorageSourceReader(HttpClient http, AgsiSettings settings, IAgsiFileLog fileLog, ILogger logger)
    {
        _http = http;
        _settings = settings;
        _fileLog = fileLog;
        _logger = logger;
    }

    public async Task<IReadOnlyList<GasStorageRow>> ReadAsync(AgsiStorageWorkUnit unit, CancellationToken cancellationToken)
    {
        var relativePath = unit.RequestPath; // "/api?country={code}&date={yyyy-MM-dd}" — no secret
        var requestUri = new Uri($"{_settings.BaseUrl.TrimEnd('/')}{relativePath}", UriKind.Absolute);
        var file = new AgsiFileContext("Storage", unit.CountryCode, unit.Date, relativePath);

        int? httpStatus = null;
        try
        {
            _logger.LogDebug("[AGSI Storage] GET {Path}", relativePath); // sanitized — the x-key is a header, never logged

            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            request.Headers.Add("x-key", _settings.ApiKey); // per-request header; NEVER on the shared client's defaults
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            httpStatus = (int)response.StatusCode;

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                _logger.LogDebug("[AGSI Storage] 404 not available: {Path}", relativePath);
                await _fileLog.UpsertAsync(file, "NotAvailable", httpStatus, relativePath, 0, cancellationToken).ConfigureAwait(false);
                return Array.Empty<GasStorageRow>();
            }

            if (!response.IsSuccessStatusCode)
                // 401/403 (bad/absent x-key) or 429/5xx after retries — loud.
                throw new HttpRequestException($"[AGSI Storage] {relativePath} returned HTTP {httpStatus}");

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var rows = ParseStorage(unit, body);

            var status = rows.Count == 0 ? "NotAvailable" : "Success";
            var fileLogId = await _fileLog.UpsertAsync(file, status, httpStatus, relativePath, rows.Count, cancellationToken).ConfigureAwait(false);

            if (rows.Count == 0)
                return Array.Empty<GasStorageRow>();

            foreach (var r in rows) r.FileLogId = fileLogId;
            _logger.LogDebug("[AGSI Storage] {Path} → {Rows} row(s) (FileLog #{Id})", relativePath, rows.Count, fileLogId);
            return rows;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            await TryUpsertFailedAsync(file, relativePath, httpStatus).ConfigureAwait(false);
            throw; // LoaderPipelineBase records the LoadLog failure and continues to the next unit.
        }
    }

    private List<GasStorageRow> ParseStorage(AgsiStorageWorkUnit unit, string body)
    {
        var rows = new List<GasStorageRow>();

        AgsiStorageEnvelope? env;
        try { env = JsonSerializer.Deserialize<AgsiStorageEnvelope>(body, AgsiJson.Options); }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "[AGSI Storage] {Path}: response was not valid JSON", unit.RequestPath);
            return rows;
        }

        if (env is null) return rows;

        // HTTP 200 + a body-level error. AGSI reports a missing / invalid / revoked x-key
        // as 200 with total:0 and an empty data[] — the SAME shape as a legitimate empty
        // day — and also uses this channel for transient server faults ("Try/Catch error").
        // Probed BEFORE the no-data tolerance below, and made LOUD: classified as no-data it
        // would write a NotAvailable hub row and SUCCEED the unit, so with
        // HotZoneKeyStrategy=RunDate a revoked key would mark every day of the hot window
        // done for the whole CET day while loading nothing. The caught throw writes the
        // Failed hub row and fails only this unit; the run continues.
        //
        // ⚠ `message` is NEVER surfaced: AGSI echoes the SECRET x-key back inside it
        // (observed live: error 'Try/Catch error', message 'API key: <the key>'), so
        // repeating it would write the secret into logs and core.LoadLog. Only `error` and
        // `dataset` are quoted, and both are redacted defensively in case the vendor ever
        // moves the echo into another field.
        if (!string.IsNullOrWhiteSpace(env.Error))
            throw new InvalidOperationException(
                $"[AGSI Storage] {unit.RequestPath}: HTTP 200 but the body reports error " +
                $"'{Redact(env.Error.Trim())}' (dataset: '{Redact(env.Dataset?.Trim())}'; " +
                "message suppressed — the vendor echoes the API key in it) " +
                "— failing the unit rather than recording it as no-data");

        // No-data tolerance (design §4.2): total==0 / empty data[] both mean "nothing to load".
        if (env.Total == 0 || env.Data is null || env.Data.Count == 0)
            return rows;

        // Multi-page tolerance: single-date mode is 1 page / 1 row; warn but process this page only.
        if (env.LastPage is > 1)
            _logger.LogWarning("[AGSI Storage] {Path}: last_page={LastPage} (>1); processing the returned page only", unit.RequestPath, env.LastPage);

        // A request does NOT guarantee a matching answer: `?country=eu` and `?country=ne`
        // both return BOTH aggregates (total:2 — data[0]=eu, data[1]=ne), so more than one
        // record here is expected for the aggregate axis, not a malformed response. Each
        // element is attributed by its OWN code below; the extras are dropped (their own
        // work unit fetches them). Warned once so an unexpected new multi-record shape
        // stays diagnosable.
        if (env.Data.Count > 1)
            _logger.LogWarning(
                "[AGSI Storage] {Path}: requested code '{Requested}' but the response carried {Count} record(s) [{Codes}]; keeping only the requested one",
                unit.RequestPath, unit.CountryCode, env.Data.Count,
                string.Join(", ", env.Data.Select(d => string.IsNullOrWhiteSpace(d.Code) ? "(no code)" : d.Code!.Trim())));

        var gasDay = AgsiParse.Date(env.GasDay);
        var foreignRecords = 0;

        foreach (var rec in env.Data)
        {
            // Attribution BEFORE the no-data test: a record describing a DIFFERENT entity is
            // never this unit's no-data, it is simply not ours.
            if (!BelongsToRequestedEntity(unit, rec, env.Data.Count))
            {
                foreignRecords++;
                continue;
            }

            // A lone status:"N" no-data record produces no fact row (design §4.2).
            if (rec.IsNoData()) continue;

            // AGSI silently CLAMPS an unpublished / future `date` to the latest available gas
            // day instead of answering empty (verified: ?country=at&date=2026-10-15 answers
            // gasDayStart 2026-09-27). Storing that would break the load-bearing single-date
            // invariant (resume key uses the request Date, the MERGE keys on GasDayStart —
            // design §10) AND overwrite the PREVIOUS day's row. The requested day genuinely
            // has no data yet, so drop it: the hot window re-pulls it once it is published.
            var reportedStart = AgsiParse.Date(rec.GasDayStart);
            if (reportedStart is { } reported && reported != unit.Date)
            {
                _logger.LogWarning(
                    "[AGSI Storage] {Path}: requested gas day {Requested:yyyy-MM-dd} but the response carried {Returned:yyyy-MM-dd} (AGSI clamps an unpublished date to the latest available); dropping the record",
                    unit.RequestPath, unit.Date, reported);
                continue;
            }

            var gasDayStart = reportedStart ?? unit.Date;                               // ≡ Date in single-date mode
            var gasDayEnd = AgsiParse.Date(rec.GasDayEnd) ?? gasDayStart.AddDays(1);

            rows.Add(new GasStorageRow
            {
                // EntityId comes from the work unit (the FK to arm.GasStorageEntity) and is
                // stamped only after the record has been confirmed to BE this entity's.
                EntityId = unit.EntityId,
                Date = unit.Date,
                GasDay = gasDay ?? gasDayStart,
                UpdatedAt = AgsiParse.DateTime(rec.UpdatedAt),
                GasDayStart = gasDayStart,
                GasDayEnd = gasDayEnd,
                GasInStorage = AgsiParse.Decimal(rec.GasInStorage),
                Consumption = AgsiParse.Decimal(rec.Consumption),
                ConsumptionFull = AgsiParse.Decimal(rec.ConsumptionFull),
                Injection = AgsiParse.Decimal(rec.Injection),
                Withdrawal = AgsiParse.Decimal(rec.Withdrawal),
                NetWithdrawal = AgsiParse.Decimal(rec.NetWithdrawal),
                WorkingGasVolume = AgsiParse.Decimal(rec.WorkingGasVolume),
                InjectionCapacity = AgsiParse.Decimal(rec.InjectionCapacity),
                WithdrawalCapacity = AgsiParse.Decimal(rec.WithdrawalCapacity),
                ContractedCapacity = AgsiParse.Decimal(rec.ContractedCapacity),
                AvailableCapacity = AgsiParse.Decimal(rec.AvailableCapacity),
                CoveredCapacity = AgsiParse.Decimal(rec.CoveredCapacity),
                // NOT NULL in the schema; a real data row always carries it. Default a blank to 'N'
                // (no quality asserted) so a malformed row still lands rather than failing the unit.
                Status = string.IsNullOrWhiteSpace(rec.Status) ? "N" : rec.Status!.Trim(),
                Trend = AgsiParse.Decimal(rec.Trend),
                Full = AgsiParse.Decimal(rec.Full)
            });
        }

        // A single-record response that describes someone else is NOT a no-data day — it is
        // an attribution failure that would otherwise be invisible (0 rows → NotAvailable →
        // unit success). The multi-record case is already warned about above, so this only
        // fires for the lone-foreign-record shape and never duplicates that warning.
        if (foreignRecords > 0 && env.Data.Count == 1)
            _logger.LogWarning(
                "[AGSI Storage] {Path}: requested code '{Requested}' but the only record returned was '{Returned}'; storing no row for this entity",
                unit.RequestPath, unit.CountryCode, env.Data[0].Code?.Trim());

        return rows;
    }

    /// <summary>
    /// Replaces the configured <c>x-key</c> with <c>***</c> anywhere it appears in vendor
    /// text before that text reaches a log, an exception message or <c>core.LoadLog</c>.
    /// AGSI really does echo the secret back: an error body observed live on 2026-09-28
    /// carried <c>"message":"API key: &lt;the key&gt;"</c>. The platform rule is that a secret
    /// is never logged, so nothing from a response body is quoted without passing through
    /// here. A blank/absent key disables the substitution (nothing to hide).
    /// </summary>
    private string? Redact(string? text)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(_settings.ApiKey)) return text;
        return text.Replace(_settings.ApiKey, "***", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when a <c>data[]</c> element describes the entity this unit requested. The
    /// element's own <c>code</c> is the only authority: a request does not guarantee a
    /// matching answer (<c>?country=eu</c> and <c>?country=ne</c> both return BOTH
    /// aggregates), and blindly stamping <c>unit.EntityId</c> onto every element is what
    /// let the Non-EU aggregate be stored as EU. Matching is case-insensitive because the
    /// URL carries the lowercased code while the response echoes the vendor's casing
    /// (<c>de</c> → <c>DE</c>). A LONE element with no <c>code</c> echo is unambiguous, so
    /// it is still accepted (pre-existing tolerance for a sparse vendor record).
    /// </summary>
    private static bool BelongsToRequestedEntity(AgsiStorageWorkUnit unit, AgsiStorageRecord rec, int recordCount)
    {
        var code = rec.Code?.Trim();
        if (string.IsNullOrEmpty(code)) return recordCount == 1;
        return string.Equals(code, unit.CountryCode.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private async Task TryUpsertFailedAsync(AgsiFileContext file, string relativePath, int? httpStatus)
    {
        try
        {
            await _fileLog.UpsertAsync(file, "Failed", httpStatus, relativePath, 0, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[AGSI Storage] failed to write 'Failed' FileLog for {Path}", relativePath);
        }
    }
}
