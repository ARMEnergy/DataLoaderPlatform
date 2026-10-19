using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace DataLoader.NUCORE;

/// <summary>Outcome of reading one work unit.</summary>
internal sealed class NucoreReadResult
{
    public int RowsRead { get; set; }
    public int RowsChanged { get; set; }
    public int RowsDropped { get; set; }
    public int RowsSkippedByWatermark { get; set; }
    public int PagesFetched { get; set; }

    /// <summary>
    /// True when the vendor answered successfully with no rows at all. A weekend, a holiday
    /// or a pre-history date all land here, and all are legitimate - NOT failures.
    /// </summary>
    public bool EmptyRead => RowsRead == 0 && RowsSkippedByWatermark == 0;
}

/// <summary>
/// Raised when the vendor's answer is structurally wrong rather than merely unsuccessful -
/// most importantly when it echoes back a different date from the one requested.
/// </summary>
internal sealed class NucoreContractException : Exception
{
    public NucoreContractException(string message) : base(message) { }
}

/// <summary>
/// Reads one work unit by paging the endpoint, converting rows and handing batches to a
/// write callback.
///
/// <para>
/// Nothing is materialised whole. <c>PositionDetails</c> alone is 2,468,453 rows and ~10 GB
/// of JSON across all its trade months, and <c>Volatilities</c> returns 147,908 rows for a
/// single day, so pages are parsed and flushed in batches of
/// <see cref="NucoreSettings.BatchSize"/>.
/// </para>
/// </summary>
internal sealed class NucoreSourceReader
{
    private readonly NucoreEndpoint _endpoint;
    private readonly HttpClient _http;
    private readonly NucoreSettings _settings;
    private readonly NucoreRowBuilder _builder;
    private readonly ILogger _logger;

    public NucoreSourceReader(
        NucoreEndpoint endpoint, HttpClient http, NucoreSettings settings, ILogger logger)
    {
        _endpoint = endpoint;
        _http = http;
        _settings = settings;
        _builder = new NucoreRowBuilder(endpoint);
        _logger = logger;
    }

    /// <summary>
    /// Reads <paramref name="unit"/>, writing batches through <paramref name="writeAsync"/>.
    /// </summary>
    /// <param name="watermark">
    /// For the trade-month partition only: the newest date already stored for this trade
    /// month. Paging runs newest-first and stops once it reaches it, which is what makes an
    /// endpoint with no date filter incremental. <c>null</c> loads the whole trade month.
    /// </param>
    public async Task<NucoreReadResult> ReadAsync(
        NucoreWorkUnit unit,
        DateTime? watermark,
        Func<IReadOnlyList<NucoreRow>, CancellationToken, Task<int>> writeAsync,
        CancellationToken ct)
    {
        var result = new NucoreReadResult();
        var batch = new List<NucoreRow>(Math.Max(1, _settings.BatchSize));
        var errors = new List<NucoreRowError>();
        var batchSize = Math.Max(1, _settings.BatchSize);

        // ⚠ The generated replace procedures DELETE the scopes present in their batch and
        // then INSERT. That is only correct if a scope is never split across two calls -
        // otherwise the second call deletes the rows the first just wrote. These two track
        // that: scopes already flushed (to fail loudly on a repeat) and the scope of the
        // row currently being buffered (to flush on a boundary).
        var flushedScopes = _endpoint.IsReplaceMode
            ? new HashSet<string>(StringComparer.Ordinal)
            : null;
        string? currentScope = null;

        // For a MERGE endpoint, the same guarantee matters for a different reason: a
        // duplicate key WITHIN a batch raises a primary key violation, and a duplicate key
        // ACROSS batches silently overwrites. Both are bugs in the declared key, and both
        // should be loud. This is what the original PositionDetails key was missing.
        var seenKeys = _endpoint.WriteMode == NucoreWriteMode.Merge && _endpoint.KeyColumns.Count > 0
            ? new HashSet<string>(StringComparer.Ordinal)
            : null;

        var page = 1;
        var maxPages = Math.Max(1, _settings.MaxPagesPerUnit);
        var reachedWatermark = false;
        int? declaredTotal = null;

        while (page <= maxPages && !reachedWatermark)
        {
            ct.ThrowIfCancellationRequested();

            using var response = await SendAsync(unit, page, ct).ConfigureAwait(false);
            using var document = await ReadDocumentAsync(response, unit, ct).ConfigureAwait(false);

            var root = document.RootElement;
            EnsureSuccessEnvelope(root, unit);

            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                throw new NucoreContractException(
                    $"{unit.DisplayName}: response has no 'data' array.");

            // The envelope states how many rows the filter matched. Captured on page 1 so
            // the whole read can be reconciled against it afterwards.
            if (page == 1 && root.TryGetProperty("totalRecords", out var total) &&
                total.ValueKind == JsonValueKind.Number && total.TryGetInt32(out var declared))
                declaredTotal = declared;

            var rowsOnPage = 0;

            foreach (var element in data.EnumerateArray())
            {
                rowsOnPage++;

                var row = _builder.TryBuild(element, out var error);
                if (row is null)
                {
                    result.RowsDropped++;
                    if (errors.Count < _settings.MaxRowErrorsLogged) errors.Add(error!);
                    continue;
                }

                // ⚠ The single most important check in this loader. Omitting or misspelling
                // a date parameter does NOT produce an error on this API - it returns 200
                // with the LATEST day's data, and two endpoints ignore a date parameter that
                // looks right. So the date the vendor echoed back is verified against the
                // one that was asked for, and a mismatch fails the unit loudly instead of
                // storing today's numbers under a historical date.
                if (unit.Date is not null && !EchoedDateMatches(row, unit.Date.Value, out var echoed))
                    throw new NucoreContractException(
                        $"{unit.DisplayName}: requested {_endpoint.DateParam}=" +
                        $"{NucoreTime.FormatDate(unit.Date.Value)} but the response carries " +
                        $"{_endpoint.DateColumn}={echoed:yyyy-MM-dd}. The endpoint ignored the " +
                        $"parameter; loading this would store the wrong day's data.");

                // Trade-month partition: newest-first paging means everything BELOW the
                // watermark is already stored, and because the sort is descending the first
                // such row ends the unit.
                //
                // ⚠ Strictly less-than, NOT less-than-or-equal. The watermark is
                // MAX(SystemDate) already stored, and that date may have been only PARTLY
                // written if a previous run was interrupted mid-date. Stopping at it would
                // leave the remainder of that date unfetched forever. Re-reading it costs
                // one date's rows and the replace makes it whole again.
                if (watermark is not null && RowDate(row) is { } rowDate && rowDate < watermark.Value)
                {
                    result.RowsSkippedByWatermark++;
                    reachedWatermark = true;
                    break;
                }

                if (seenKeys is not null && !seenKeys.Add(KeyToken(row)))
                    throw new NucoreContractException(
                        $"{unit.DisplayName}: two rows share the declared primary key " +
                        $"({string.Join(", ", _endpoint.KeyColumns.Select(k => k.Name))}) = {KeyToken(row)}. " +
                        "The key does not identify a row for this endpoint; merging would " +
                        "either violate the primary key or silently overwrite one row with " +
                        "the other. The endpoint needs a replace write mode, not a key.");

                // Replace modes: a scope change is the only safe place to flush.
                if (flushedScopes is not null)
                {
                    var scope = ScopeToken(row);

                    if (currentScope is not null && !string.Equals(scope, currentScope, StringComparison.Ordinal))
                    {
                        // Boundary. Flush now if the batch has earned it; otherwise keep
                        // accumulating whole scopes.
                        if (batch.Count >= batchSize)
                        {
                            result.RowsChanged += await FlushAsync(batch, flushedScopes, writeAsync, unit, ct)
                                .ConfigureAwait(false);
                        }
                    }

                    currentScope = scope;
                    batch.Add(row);
                    result.RowsRead++;

                    // A ReplaceByDate unit is a SINGLE scope, so its batch is the whole
                    // unit - that is deliberate, and it is what makes the delete and the
                    // insert cover the same rows. The days involved are small
                    // (PositionSummary ~13,600 rows, PnlExplain ~11,400), but if the vendor
                    // ever published a day far larger, silently splitting it would corrupt
                    // the load and growing without bound would exhaust memory. Fail instead.
                    if (batch.Count > _settings.MaxUnitBufferRows)
                        throw new NucoreContractException(
                            $"{unit.DisplayName}: buffered {batch.Count:N0} rows for a single " +
                            $"replace scope, over the MaxUnitBufferRows limit of " +
                            $"{_settings.MaxUnitBufferRows:N0}. A scope cannot be split across " +
                            "two writes without the second deleting the first, so this has to " +
                            "fail rather than silently truncate. Raise MaxUnitBufferRows if the " +
                            "vendor's volumes have genuinely grown.");

                    continue;
                }

                batch.Add(row);
                result.RowsRead++;

                if (batch.Count >= batchSize)
                {
                    result.RowsChanged += await writeAsync(batch, ct).ConfigureAwait(false);
                    batch.Clear();
                }
            }

            result.PagesFetched++;

            // A page shorter than requested - or empty - is the end. This API answers a page
            // past the end with 200 and recordsReturned 0 rather than an error, so an empty
            // page is the stop condition, not a fault.
            if (rowsOnPage < _settings.PageSize) break;

            page++;
        }

        if (batch.Count > 0)
        {
            result.RowsChanged += flushedScopes is not null
                ? await FlushAsync(batch, flushedScopes, writeAsync, unit, ct).ConfigureAwait(false)
                : await writeAsync(batch, ct).ConfigureAwait(false);
        }

        // ⚠ Reconcile what was read against what the vendor said it had.
        //
        // This is the backstop for the paging bug the SortFields clause fixes. Even with a
        // sort, a tie between two rows could in principle let one be returned twice and
        // another dropped across a page boundary - and the dropped half is invisible. The
        // envelope's own totalRecords is an independent count of the same filter, so
        // comparing against it turns a silent shortfall into a failed unit.
        //
        // Skipped when the unit stopped early at a watermark (it deliberately read only
        // part of the filter) or at the page cap.
        if (declaredTotal is not null && !reachedWatermark && page <= maxPages)
        {
            var accountedFor = result.RowsRead + result.RowsDropped;
            if (accountedFor != declaredTotal.Value)
                throw new NucoreContractException(
                    $"{unit.DisplayName}: read {accountedFor:N0} row(s) but the response " +
                    $"envelope reported totalRecords = {declaredTotal.Value:N0}. Paging lost " +
                    "or duplicated rows; the loaded data for this unit would be incomplete. " +
                    $"SortFields was '{_endpoint.SortFields}'.");
        }

        if (page > maxPages)
            _logger.LogWarning(
                "NUCORE {Unit}: stopped at the {MaxPages}-page cap (MaxPagesPerUnit) with rows " +
                "still available; raise the cap or narrow the unit",
                unit.DisplayName, maxPages);

        if (result.RowsDropped > 0)
            _logger.LogWarning(
                "NUCORE {Unit}: dropped {Dropped} of {Total} row(s). First {Shown}: {Details}",
                unit.DisplayName, result.RowsDropped, result.RowsDropped + result.RowsRead,
                errors.Count, string.Join("; ", errors.Select(e => $"{e.Column}: {e.Reason}")));

        return result;
    }

    private async Task<HttpResponseMessage> SendAsync(NucoreWorkUnit unit, int page, CancellationToken ct)
    {
        var uri = BuildRequestUri(unit, page);

        // The query string carries no secret (the key is a header), so logging the path and
        // parameters is safe and is what makes a vendor-side problem diagnosable.
        _logger.LogDebug("NUCORE GET {Uri}", uri);

        var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct)
                                  .ConfigureAwait(false);

        if (response.IsSuccessStatusCode) return response;

        var status = (int)response.StatusCode;
        response.Dispose();

        // Each of these is a different operator action, so they are not collapsed into one
        // "request failed" message.
        var hint = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized =>
                " - the X-Api-Key header was missing, empty or wrong. Check " +
                "core.Param(LoaderName='NUCORE', ParamName='ApiKey').",
            HttpStatusCode.InternalServerError =>
                " - on this API a 500 is what a MALFORMED request gets (a bad date string, " +
                "for example), not a transient fault, so it is not retried.",
            HttpStatusCode.GatewayTimeout =>
                $" - the gateway gave up. PageSize is {_settings.PageSize}; this endpoint " +
                "504s on very large pages.",
            HttpStatusCode.BadRequest =>
                " - check SortFields: an unknown sort field is rejected by name.",
            _ => string.Empty
        };

        throw new HttpRequestException(
            NucoreHttp.Redact($"NUCORE {unit.DisplayName}: HTTP {status}{hint}"),
            null, response.StatusCode);
    }

    private async Task<JsonDocument> ReadDocumentAsync(
        HttpResponseMessage response, NucoreWorkUnit unit, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        try
        {
            return await JsonDocument.ParseAsync(stream, default, ct).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new NucoreContractException(
                $"{unit.DisplayName}: response was not valid JSON - {NucoreHttp.Redact(ex.Message)}");
        }
    }

    /// <summary>
    /// The envelope carries its own <c>success</c> flag alongside the HTTP status. It has
    /// been true on every observed response, including empty ones, but a false here would
    /// mean the body is not data - so it is read rather than assumed.
    /// </summary>
    private static void EnsureSuccessEnvelope(JsonElement root, NucoreWorkUnit unit)
    {
        if (root.TryGetProperty("success", out var success) &&
            success.ValueKind == JsonValueKind.False)
            throw new NucoreContractException(
                $"{unit.DisplayName}: the response envelope reports success=false.");
    }

    private bool EchoedDateMatches(NucoreRow row, DateTime requested, out DateTime echoed)
    {
        echoed = default;
        var value = row.Values[_endpoint.DateColumnIndex];

        echoed = value switch
        {
            DateTime dt => dt.Date,
            DateTimeOffset dto => dto.Date,
            _ => default
        };

        return echoed == requested.Date;
    }

    /// <summary>
    /// Writes one batch of a replace-mode endpoint, after proving that none of the scopes it
    /// carries has already been written in this unit.
    ///
    /// <para>
    /// ⚠ This guard is the whole reason the generated DELETE is safe. If a scope were split
    /// across two calls, the second call's DELETE would remove the rows the first call had
    /// just INSERTed, and the loss would be completely silent - the run would report
    /// success and the table would simply be short. Failing loudly here is the alternative.
    /// </para>
    /// </summary>
    private async Task<int> FlushAsync(
        List<NucoreRow> batch,
        HashSet<string> flushedScopes,
        Func<IReadOnlyList<NucoreRow>, CancellationToken, Task<int>> writeAsync,
        NucoreWorkUnit unit,
        CancellationToken ct)
    {
        if (batch.Count == 0) return 0;

        foreach (var scope in batch.Select(ScopeToken).Distinct(StringComparer.Ordinal))
        {
            if (!flushedScopes.Add(scope))
                throw new NucoreContractException(
                    $"{unit.DisplayName}: scope ({string.Join(", ", _endpoint.ScopeColumns)}) = {scope} " +
                    "appears in two separate write batches. The merge procedure deletes a " +
                    "scope before inserting it, so the second write would delete the first " +
                    "one's rows. This means the response was not ordered by the scope as " +
                    "the SortFields clause requires.");
        }

        var written = await writeAsync(batch, ct).ConfigureAwait(false);
        batch.Clear();
        return written;
    }

    /// <summary>The replace scope of one row, as a comparable token.</summary>
    private string ScopeToken(NucoreRow row) =>
        string.Join("", _endpoint.ScopeColumnIndexes.Select(i => Format(row.Values[i])));

    /// <summary>The declared primary key of one row, as a comparable token.</summary>
    private string KeyToken(NucoreRow row)
    {
        var parts = new List<string>(_endpoint.KeyColumns.Count);
        foreach (var key in _endpoint.KeyColumns)
        {
            var index = IndexOf(key);
            parts.Add(index >= 0 ? Format(row.Values[index]) : "?");
        }
        return string.Join("", parts);
    }

    private int IndexOf(NucoreColumn column)
    {
        for (var i = 0; i < _endpoint.Columns.Count; i++)
            if (ReferenceEquals(_endpoint.Columns[i], column)) return i;
        return -1;
    }

    private static string Format(object value) => value switch
    {
        DBNull => string.Empty,
        DateTime dt => dt.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString("O", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty
    };

    private DateTime? RowDate(NucoreRow row) =>
        row.Values[_endpoint.DateColumnIndex] switch
        {
            DateTime dt => dt.Date,
            DateTimeOffset dto => dto.Date,
            _ => null
        };

    internal string BuildRequestUri(NucoreWorkUnit unit, int page)
    {
        var query = new List<string>
        {
            $"pageSize={_settings.PageSize.ToString(CultureInfo.InvariantCulture)}",
            $"page={page.ToString(CultureInfo.InvariantCulture)}"
        };

        if (unit.Date is not null && _endpoint.DateParam is not null)
            query.Add($"{_endpoint.DateParam}={NucoreTime.FormatDate(unit.Date.Value)}");

        if (unit.TradeMonth is not null)
        {
            query.Add($"TradeMonthFrom={unit.TradeMonth}");
            query.Add($"TradeMonthTo={unit.TradeMonth}");
        }

        // ⚠ Sent on EVERY request, for every endpoint, not just the paged trade-month one.
        //
        // Paging this API without a sort is NOT stable. Pages 1 and 2 of a single
        // Volatilities day at pageSize=20000 shared 111 rows when fetched unsorted - which
        // also means ~111 rows of that day were never returned at all. The same two pages
        // sorted overlap by zero. An unsorted paged read silently duplicates some rows and
        // silently drops others, and the drop is the half nothing would ever notice.
        query.Add($"SortFields={_endpoint.SortFields}");

        return $"{_settings.BaseUrl.TrimEnd('/')}{_endpoint.Path}?{string.Join("&", query)}";
    }
}
