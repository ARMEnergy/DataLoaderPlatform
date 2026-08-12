using System.Net;
using DataLoader.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace DataLoader.CWG;

/// <summary>
/// Shared HTTP + FileLog + parse + map plumbing for every CWG endpoint (design
/// §5). Implements <see cref="ISourceReader{TUnit,TRow}"/> directly (NOT via
/// <c>HttpJsonSourceReaderBase</c>, which would <c>EnsureSuccessStatusCode</c> +
/// JSON-deserialize) so it can stay 404-tolerant and parse CSV.
///
/// <para>Per-request flow:</para>
/// <list type="number">
///   <item>Build the sanitized path and the absolute URI (<c>?apikey=</c>). Only the
///     sanitized path is logged — never the query string / api key.</item>
///   <item>GET. 404 → <c>NotAvailable</c> (0 rows, no unit failure); any other
///     non-success → throw; 200 → parse the CSV via the shape parser and map each
///     record with the row factory, dropping nulls.</item>
///   <item>Upsert the <c>arm.FileLog</c> hub row (Success when rows &gt; 0, else
///     NotAvailable) and read back its <c>FileLogId</c>; stamp it onto every row.</item>
/// </list>
/// <see cref="OperationCanceledException"/> is rethrown WITHOUT a FileLog write;
/// any other failure writes a best-effort <c>Failed</c> row then rethrows so the
/// pipeline records a LoadLog failure and continues (fail-a-block-not-the-run).
/// </summary>
public sealed class CwgSourceReader<TRecord, TRow> : ISourceReader<CwgWorkUnit, TRow>
    where TRow : class, ICwgFactRow
{
    private readonly HttpClient _http;
    private readonly CwgSettings _settings;
    private readonly ICwgFileLog _fileLog;
    private readonly CwgEndpointDescriptor _descriptor;
    private readonly ICwgShapeParser<TRecord> _shapeParser;
    private readonly Func<TRecord, CwgWorkUnit, TRow?> _rowFactory;
    private readonly Func<TRecord, bool>? _sentinelPredicate;
    private readonly ILogger _logger;

    /// <param name="sentinelPredicate">
    /// Optional: identifies a record the row factory intentionally drops as an EXPECTED
    /// sentinel (e.g. an hourly "NULL"/blank cell). Such drops are logged at Debug; any
    /// other dropped record is treated as a genuine parse failure and logged at Warning so
    /// data-quality regressions stay visible (Fix 5). Null = every drop is a genuine failure.
    /// </param>
    public CwgSourceReader(
        HttpClient http,
        CwgSettings settings,
        ICwgFileLog fileLog,
        CwgEndpointDescriptor descriptor,
        ICwgShapeParser<TRecord> shapeParser,
        Func<TRecord, CwgWorkUnit, TRow?> rowFactory,
        ILogger logger,
        Func<TRecord, bool>? sentinelPredicate = null)
    {
        _http = http;
        _settings = settings;
        _fileLog = fileLog;
        _descriptor = descriptor;
        _shapeParser = shapeParser;
        _rowFactory = rowFactory;
        _logger = logger;
        _sentinelPredicate = sentinelPredicate;
    }

    public async Task<IReadOnlyList<TRow>> ReadAsync(CwgWorkUnit unit, CancellationToken cancellationToken)
    {
        var relativePath = "/" + unit.Filename;
        var requestUri = new Uri(
            $"{_settings.BaseUrl.TrimEnd('/')}{relativePath}?apikey={Uri.EscapeDataString(_settings.ApiKey)}",
            UriKind.Absolute);

        var file = new CwgFileContext(_descriptor.EndpointId, unit.Region, unit.Variant, unit.RepresentativeDate, relativePath);

        int? httpStatus = null;
        try
        {
            _logger.LogDebug("[{Endpoint}] GET {Path}", _descriptor.EndpointId, relativePath); // sanitized — no query/apikey
            using var response = await _http.GetAsync(requestUri, cancellationToken).ConfigureAwait(false);
            httpStatus = (int)response.StatusCode;

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                _logger.LogDebug("[{Endpoint}] 404 not available: {Path}", _descriptor.EndpointId, relativePath);
                await _fileLog.UpsertAsync(file, "NotAvailable", httpStatus, relativePath, 0, cancellationToken).ConfigureAwait(false);
                return Array.Empty<TRow>();
            }

            if (!response.IsSuccessStatusCode)
            {
                // 401/403 (auth) or 429/5xx after retries — loud.
                throw new HttpRequestException($"[{_descriptor.EndpointId}] {relativePath} returned HTTP {httpStatus}");
            }

            var csv = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var records = _shapeParser.Parse(_descriptor, unit, csv, _logger);

            var rows = new List<TRow>(records.Count);
            var sentinelDrops = 0;
            var invalidDrops = 0;
            foreach (var record in records)
            {
                var row = _rowFactory(record, unit);
                if (row is not null) { rows.Add(row); continue; }

                // Classify the drop: an EXPECTED sentinel (e.g. hourly "NULL"/blank) is Debug;
                // anything else is a genuine parse failure and warns so it stays visible (Fix 5).
                if (_sentinelPredicate is not null && _sentinelPredicate(record)) sentinelDrops++;
                else invalidDrops++;
            }
            if (sentinelDrops > 0)
                _logger.LogDebug("[{Endpoint}] {Path}: skipped {Count} sentinel (NULL/blank) cell(s)",
                    _descriptor.EndpointId, relativePath, sentinelDrops);
            if (invalidDrops > 0)
                _logger.LogWarning("[{Endpoint}] {Path}: dropped {Count} unparseable/invalid record(s)",
                    _descriptor.EndpointId, relativePath, invalidDrops);

            // A 404 OR a zero-row 200 both log NotAvailable, no failure (design §5, decision 5).
            var status = rows.Count == 0 ? "NotAvailable" : "Success";
            var fileLogId = await _fileLog.UpsertAsync(file, status, httpStatus, relativePath, rows.Count, cancellationToken).ConfigureAwait(false);

            if (rows.Count == 0)
                return Array.Empty<TRow>();

            foreach (var r in rows) r.FileLogId = fileLogId;

            _logger.LogDebug("[{Endpoint}] {Path} → {Rows} rows (FileLog #{Id})",
                _descriptor.EndpointId, relativePath, rows.Count, fileLogId);
            return rows;
        }
        catch (OperationCanceledException)
        {
            // Per-unit timeout / run cancellation is already recorded by LoadLog;
            // don't write a spurious hub row on a spent token.
            throw;
        }
        catch (Exception)
        {
            await TryUpsertFailedAsync(file, relativePath, httpStatus).ConfigureAwait(false);
            throw; // LoaderPipelineBase logs + records the LoadLog failure and moves on.
        }
    }

    private async Task TryUpsertFailedAsync(CwgFileContext file, string relativePath, int? httpStatus)
    {
        try
        {
            // Best-effort; use None so the Failed row is still written even when the
            // failure was a timeout, and never mask the original exception.
            await _fileLog.UpsertAsync(file, "Failed", httpStatus, relativePath, 0, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{Endpoint}] failed to write 'Failed' FileLog for {Path}", _descriptor.EndpointId, relativePath);
        }
    }
}
