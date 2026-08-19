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

        // No-data tolerance (design §4.2): total==0 / empty data[] both mean "nothing to load".
        if (env.Total == 0 || env.Data is null || env.Data.Count == 0)
            return rows;

        // Multi-page tolerance: single-date mode is 1 page / 1 row; warn but process this page only.
        if (env.LastPage is > 1)
            _logger.LogWarning("[AGSI Storage] {Path}: last_page={LastPage} (>1); processing the returned page only", unit.RequestPath, env.LastPage);

        var gasDay = AgsiParse.Date(env.GasDay);

        foreach (var rec in env.Data)
        {
            // A lone status:"N" no-data record produces no fact row (design §4.2).
            if (rec.IsNoData()) continue;

            var gasDayStart = AgsiParse.Date(rec.GasDayStart) ?? unit.Date;             // ≡ Date in single-date mode
            var gasDayEnd = AgsiParse.Date(rec.GasDayEnd) ?? gasDayStart.AddDays(1);

            rows.Add(new GasStorageRow
            {
                // EntityId comes from the work unit (the FK to arm.GasStorageEntity), so the
                // response code echo / case-sensitivity is irrelevant and not persisted.
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

        return rows;
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
