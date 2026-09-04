using System.Text;
using DataLoader.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.ICE;

/// <summary>
/// Reads one ICE file for one (feed, trade date): fetch → classify → parse → map.
///
/// <para>
/// The order matters. <b>Classification happens before any parsing</b>, because
/// <c>downloads.ice.com</c> answers HTTP 200 for a missing file and for expired
/// authentication just as it does for real data (<c>docs/apis/ICE.md</c> 2). Handing
/// an unclassified body to the parser would either load an HTML page's stray text
/// as rows or, worse, record a clean zero-row success for an auth failure.
/// </para>
/// <para>
/// Every outcome — including the non-error <c>NotAvailable</c> — writes an
/// <c>arm.FileLog</c> row before the reader returns or throws.
/// </para>
/// </summary>
public sealed class IceSourceReader : ISourceReader<IceWorkUnit, IceRow>
{
    /// <summary>
    /// Columns read from an XLSX row. Generous on purpose: the sheet is 12 columns
    /// wide today, and reading past it costs nothing while leaving room for ICE to
    /// append one without the mapping shifting (values are placed by cell reference
    /// and resolved by header NAME, so extra columns are simply ignored).
    /// </summary>
    private const int MaxXlsxColumns = 64;

    private readonly IceFeedDescriptor _feed;
    private readonly HttpClient _http;
    private readonly IIceAuthenticator _auth;
    private readonly IceFileCache _cache;
    private readonly IIceFileLog _fileLog;
    private readonly IceSettings _settings;
    private readonly ILogger _logger;

    public IceSourceReader(
        IceFeedDescriptor feed,
        HttpClient http,
        IIceAuthenticator auth,
        IceFileCache cache,
        IIceFileLog fileLog,
        IOptions<IceSettings> settings,
        ILogger logger)
    {
        _feed = feed;
        _http = http;
        _auth = auth;
        _cache = cache;
        _fileLog = fileLog;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<IceRow>> ReadAsync(IceWorkUnit unit, CancellationToken cancellationToken)
    {
        var url = BuildUrl(unit.TradeDate);
        var fileName = _feed.FileNameFor(unit.TradeDate);

        var fileContext = new IceFileContext(
            FeedId: _feed.FeedId,
            TradeDate: unit.TradeDate,
            FileName: fileName,
            TargetTable: _feed.Table.TableName,
            SizeBytes: null,
            RequestPath: url);

        var (content, kind, detail) = await FetchAsync(unit, url, cancellationToken).ConfigureAwait(false);

        fileContext = fileContext with { SizeBytes = content?.LongLength };

        switch (kind)
        {
            case IceResponseKind.NotAvailable:
                // A weekend, a holiday, a future date, or a feed that did not exist
                // yet. NOT an error: zero rows, the unit succeeds, the run continues.
                _logger.LogDebug("ICE {Feed} {Date}: no file published — {Detail}",
                    _feed.FeedId, IceTime.Iso(unit.TradeDate), detail);

                await TryLogAsync(fileContext, "NotAvailable", 0, 0, detail, cancellationToken).ConfigureAwait(false);
                return Array.Empty<IceRow>();

            case IceResponseKind.AuthExpired:
                await TryLogAsync(fileContext, "AuthExpired", 0, 0, detail, cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException(
                    $"ICE {_feed.FeedId} {IceTime.Iso(unit.TradeDate)}: authentication still rejected after refresh — {detail}");

            case IceResponseKind.Malformed:
                await TryLogAsync(fileContext, "Malformed", 0, 0, detail, cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException(
                    $"ICE {_feed.FeedId} {IceTime.Iso(unit.TradeDate)}: {detail}");
        }

        try
        {
            var (rows, dropped) = Parse(content!, url);

            _logger.LogInformation("ICE {Feed} {Date}: {Rows} row(s){Dropped}",
                _feed.FeedId, IceTime.Iso(unit.TradeDate), rows.Count,
                dropped > 0 ? $", {dropped} dropped" : string.Empty);

            await TryLogAsync(fileContext, "Success", rows.Count, dropped, null, cancellationToken).ConfigureAwait(false);
            return rows;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await TryLogAsync(fileContext, "Failed", 0, 0, ex.Message, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Absolute URL for one trade date. The SSO token is never part of it.</summary>
    internal string BuildUrl(DateOnly tradeDate)
    {
        var baseUrl = _settings.DownloadBaseUrl.TrimEnd('/');
        return $"{baseUrl}/{_feed.PathFor(tradeDate)}";
    }

    /// <summary>
    /// Get the bytes, from the local cache when possible, and classify them.
    ///
    /// <para>
    /// A cached body is classified too rather than trusted: only <c>Data</c> is ever
    /// written to the cache, but a file that was valid last week can stop matching if
    /// ICE changes a header, and that must surface as <c>Malformed</c> rather than be
    /// parsed against a stale mapping.
    /// </para>
    /// </summary>
    private async Task<(byte[]? Content, IceResponseKind Kind, string Detail)> FetchAsync(
        IceWorkUnit unit, string url, CancellationToken cancellationToken)
    {
        var cached = await _cache.TryReadAsync(_feed, unit.TradeDate, cancellationToken).ConfigureAwait(false);
        if (cached is not null)
        {
            var cachedKind = IceResponseClassifier.Classify(cached, _feed, out var cachedDetail);
            if (cachedKind == IceResponseKind.Data)
            {
                _logger.LogDebug("ICE {Feed} {Date}: using cached file ({Bytes} bytes)",
                    _feed.FeedId, IceTime.Iso(unit.TradeDate), cached.Length);
                return (cached, cachedKind, cachedDetail);
            }

            _logger.LogWarning("ICE {Feed} {Date}: cached file is {Kind} ({Detail}) — re-downloading",
                _feed.FeedId, IceTime.Iso(unit.TradeDate), cachedKind, cachedDetail);
        }

        var token = await _auth.GetTokenAsync(cancellationToken).ConfigureAwait(false);
        var content = await DownloadAsync(url, token, cancellationToken).ConfigureAwait(false);
        var kind = IceResponseClassifier.Classify(content, _feed, out var detail);

        // One re-auth and retry. The token embeds its own issue time and ICE
        // publishes no TTL, so expiry is detected rather than predicted.
        if (kind == IceResponseKind.AuthExpired)
        {
            _logger.LogInformation("ICE {Feed} {Date}: token rejected — refreshing and retrying once",
                _feed.FeedId, IceTime.Iso(unit.TradeDate));

            var fresh = await _auth.RefreshAsync(token, cancellationToken).ConfigureAwait(false);
            content = await DownloadAsync(url, fresh, cancellationToken).ConfigureAwait(false);
            kind = IceResponseClassifier.Classify(content, _feed, out detail);
        }

        // ⚠ Only real data is cached. Writing a sentinel page to disk under the data
        // file's name would poison that date for every later run.
        if (kind == IceResponseKind.Data)
            await _cache.SaveAsync(_feed, unit.TradeDate, content, cancellationToken).ConfigureAwait(false);

        return (content, kind, detail);
    }

    private async Task<byte[]> DownloadAsync(string url, string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        // The SSO Set-Cookie is scoped to .sso.theice.com and is NOT sent to
        // downloads.ice.com automatically — different registrable domains. Setting
        // the header explicitly is the whole auth mechanism for downloads.
        request.Headers.TryAddWithoutValidation("Cookie", $"iceSsoCookie={token}");

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);

        // A non-200 is genuinely exceptional here — ICE uses 200 for data, missing
        // files AND auth failures alike, so anything else is infrastructure.
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ parsing

    /// <summary>
    /// Turn a validated body into rows, returning how many rows were dropped for a
    /// blank or unparseable REQUIRED value.
    /// </summary>
    internal (List<IceRow> Rows, int Dropped) Parse(byte[] content, string sourcePath)
    {
        var records = _feed.Format switch
        {
            IceFileFormat.Xlsx => IceXlsx.ParseRecords(content, MaxXlsxColumns),
            IceFileFormat.Csv => IceDelimited.ParseRecords(Decode(content), ','),
            _ => IceDelimited.ParseRecords(Decode(content), '|')
        };

        if (records.Count == 0)
            throw new InvalidDataException($"ICE {_feed.FeedId}: file contains no records, not even a header row.");

        var headerMap = IceDelimited.BuildHeaderMap(records[0]);
        var indices = ResolveColumnIndices(headerMap);

        var columns = _feed.Table.Columns;
        var rows = new List<IceRow>(Math.Max(0, records.Count - 1));
        var dropped = 0;
        var truncatedSourcePath = Truncate(sourcePath, 500);

        for (var r = 1; r < records.Count; r++)
        {
            var record = records[r];
            var values = new object[columns.Count];
            var drop = false;

            for (var c = 0; c < columns.Count; c++)
            {
                var column = columns[c];

                if (column.Derived == IceDerived.SourcePath)
                {
                    values[c] = truncatedSourcePath;
                    continue;
                }

                var index = indices[c];
                var raw = index >= 0 && index < record.Length ? record[index] : string.Empty;

                if (!IceConvert.TryConvert(column.Type, raw, out var value))
                {
                    // A non-blank value that will not convert. Required -> the row
                    // cannot be keyed and is dropped; optional -> stored as NULL.
                    if (column.Required) drop = true;
                    values[c] = DBNull.Value;
                    continue;
                }

                // A blank REQUIRED value is the common case, not an error: it is how
                // every options feed marks its underlying-future rows (no STRIKE).
                if (column.Required && value == DBNull.Value) drop = true;

                values[c] = value;
            }

            if (drop) { dropped++; continue; }

            rows.Add(new IceRow(values));
        }

        return (rows, dropped);
    }

    /// <summary>
    /// Map each descriptor column to its index in the file's header row.
    ///
    /// <para>
    /// <b>By NAME, never by position.</b> The files lead with
    /// <c>TRADE DATE|HUB|PRODUCT|STRIP|CONTRACT</c> while the tables lead with
    /// <c>TradeDate, Contract, ContractType, Strip</c>; a positional load would swap
    /// <c>Hub</c> and <c>Contract</c> and corrupt the primary key of every row.
    /// </para>
    /// <para>
    /// A missing REQUIRED header throws — but in practice the classifier has already
    /// rejected such a file as <c>Malformed</c>, so this is the backstop. A missing
    /// OPTIONAL header loads NULL and warns, since it means the feed changed shape.
    /// </para>
    /// </summary>
    private int[] ResolveColumnIndices(Dictionary<string, int> headerMap)
    {
        var columns = _feed.Table.Columns;
        var indices = new int[columns.Count];

        for (var c = 0; c < columns.Count; c++)
        {
            var column = columns[c];

            if (column.Derived != IceDerived.None) { indices[c] = -1; continue; }

            var found = -1;
            foreach (var header in column.SourceHeaders)
            {
                if (headerMap.TryGetValue(header, out var index)) { found = index; break; }
            }

            if (found < 0)
            {
                if (column.Required)
                    throw new InvalidDataException(
                        $"ICE {_feed.FeedId}: required column '{column.Name}' not found under any of " +
                        $"[{string.Join(", ", column.SourceHeaders)}].");

                _logger.LogWarning(
                    "ICE {Feed}: optional column {Column} not found under [{Headers}] — loading NULL",
                    _feed.FeedId, column.Name, string.Join(", ", column.SourceHeaders));
            }

            indices[c] = found;
        }

        return indices;
    }

    /// <summary>
    /// Decode a text body. The feeds are plain ASCII with no BOM; UTF-8 is a
    /// superset of that and <c>detectEncodingFromByteOrderMarks</c> handles a BOM if
    /// one ever appears.
    /// </summary>
    private static string Decode(byte[] content)
    {
        using var stream = new MemoryStream(content, writable: false);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// FileLog writes are best-effort. Losing an audit row must not turn a good load
    /// into a failed one, nor mask the real exception on the failure path.
    /// </summary>
    private async Task TryLogAsync(
        IceFileContext file, string status, int rowCount, int rowsDropped, string? error,
        CancellationToken cancellationToken)
    {
        try
        {
            await _fileLog.UpsertAsync(file, status, rowCount, rowsDropped, error, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ICE: could not write FileLog row for {Feed}/{Date} ({Status})",
                file.FeedId, IceTime.Iso(file.TradeDate), status);
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
