using System.Text;
using Microsoft.Extensions.Logging;

namespace DataLoader.OilX;

/// <summary>What one file's streamed load produced.</summary>
/// <param name="RowsRead">Rows parsed out of the CSV (excluding the header).</param>
/// <param name="RowsDropped">Rows discarded because a REQUIRED value was missing or unparseable.</param>
/// <param name="CellsFailed">Non-blank cells that failed conversion and became NULL.</param>
/// <param name="RowsChanged">
/// What the merge procs reported — rows actually INSERTED or UPDATED. Deliberately not
/// the same as <paramref name="RowsRead"/>: the Checksum guard skips unchanged rows, so
/// a re-merge of a snapshot where nothing moved reports 0 here and is a healthy success
/// (<c>sql/OilX/003</c>).
/// </param>
internal sealed record OilXFileResult(int RowsRead, int RowsDropped, int CellsFailed, int RowsChanged);

/// <summary>
/// Downloads one published CSV and merges it in batches.
///
/// <para>
/// <b>Nothing is materialised whole.</b> A CargoTracking snapshot is 208 MB and
/// 396,866 rows, and a 31-day window moves ~6.5 GB, so the response is read with
/// <see cref="HttpCompletionOption.ResponseHeadersRead"/>, parsed incrementally by
/// <see cref="OilXCsv.ReadRecords"/>, and handed to the sink every
/// <see cref="OilXSettings.BatchSize"/> rows. Peak footprint is one batch.
/// </para>
/// <para>
/// Each batch is an independent TVP call, so a file that fails part-way leaves earlier
/// batches merged. That is safe and deliberate: every merge is idempotent on
/// <c>(RunDate, RowId)</c>, and the unit's resume key is only recorded on success, so
/// the whole day is re-merged next run and converges.
/// </para>
/// </summary>
internal sealed class OilXSourceReader
{
    private readonly OilXFeedDescriptor _feed;
    private readonly HttpClient _http;
    private readonly OilXSettings _settings;
    private readonly ILogger _logger;

    public OilXSourceReader(
        OilXFeedDescriptor feed, HttpClient http, OilXSettings settings, ILogger logger)
    {
        _feed = feed;
        _http = http;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>Download, parse and merge one file. Returns what it did.</summary>
    public async Task<OilXFileResult> LoadAsync(
        OilXManifestFile file,
        Func<IReadOnlyList<OilXRow>, CancellationToken, Task<int>> writeBatch,
        CancellationToken cancellationToken)
    {
        using var response = await _http
            .GetAsync(file.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            // The URL is NEVER quoted into this message -- it carries the AWS signature.
            throw new InvalidOperationException(
                $"OilX {_feed.FeedId}: downloading '{file.FileName}' returned HTTP " +
                $"{(int)response.StatusCode}. Presigned URLs expire five hours after the " +
                "manifest call, so a 403 here most likely means the run outlasted them.");

        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        // detectEncodingFromByteOrderMarks:true so a UTF-8 BOM does not become part of
        // the first header name -- which would make that column unresolvable and NULL
        // every row's first value.
        using var reader = new StreamReader(
            stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true,
            bufferSize: 128 * 1024, leaveOpen: false);

        return await MergeRecordsAsync(
            OilXCsv.ReadRecords(reader), file.FileName, writeBatch, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The parse-convert-batch loop, separated from the download so tests can drive it
    /// from a literal CSV without an HTTP server.
    /// </summary>
    internal async Task<OilXFileResult> MergeRecordsAsync(
        IEnumerable<string[]> records,
        string fileName,
        Func<IReadOnlyList<OilXRow>, CancellationToken, Task<int>> writeBatch,
        CancellationToken cancellationToken)
    {
        var columns = _feed.Columns;

        // Resolved once per file, from THIS file's header. Historic files predate some
        // columns, so an index of -1 is a legitimate "not in this file" and yields NULL.
        int[]? indexes = null;

        var batch = new List<OilXRow>(Math.Max(1, _settings.BatchSize));
        var rowsRead = 0;
        var rowsDropped = 0;
        var cellsFailed = 0;
        var rowsChanged = 0;
        var errorsLogged = 0;

        // Reused across rows: the converted values keyed by column name, which RowId and
        // Checksum both read. Cleared per row rather than reallocated -- at 2M rows a
        // run, the allocation shows up.
        var byName = new Dictionary<string, object?>(columns.Count, StringComparer.Ordinal);

        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (indexes is null)
            {
                indexes = ResolveHeader(record, fileName);
                continue;
            }

            rowsRead++;
            byName.Clear();

            var values = new object[columns.Count];
            var drop = false;

            // ---- pass 1: the CSV-sourced columns -------------------------------
            for (var i = 0; i < columns.Count; i++)
            {
                var column = columns[i];
                if (!column.FromCsv) continue;

                var index = indexes[i];
                var raw = index >= 0 && index < record.Length ? record[index] : string.Empty;

                if (!OilXCsv.TryConvert(column.Type, raw, out var value))
                {
                    cellsFailed++;
                    if (errorsLogged < _settings.MaxRowErrorsLogged)
                    {
                        errorsLogged++;
                        _logger.LogWarning(
                            "OilX {Feed} {File} row {Row}: column {Column} could not parse '{Raw}' as {Type} — NULL",
                            _feed.FeedId, fileName, rowsRead, column.Name, Truncate(raw), column.Type);
                    }
                    value = DBNull.Value;
                }

                if (column.Required && value is DBNull)
                {
                    drop = true;
                    if (errorsLogged < _settings.MaxRowErrorsLogged)
                    {
                        errorsLogged++;
                        _logger.LogWarning(
                            "OilX {Feed} {File} row {Row}: required column {Column} is missing — row dropped",
                            _feed.FeedId, fileName, rowsRead, column.Name);
                    }
                }

                values[i] = value;
                byName[column.Name] = value is DBNull ? null : value;
            }

            if (drop) { rowsDropped++; continue; }

            // ---- pass 2: the derived columns ------------------------------------
            // RowId and Checksum both read pass 1's values, so they cannot be folded
            // into the loop above.
            for (var i = 0; i < columns.Count; i++)
            {
                switch (columns[i].Derived)
                {
                    case OilXDerived.None:
                        break;
                    case OilXDerived.RowId:
                        values[i] = OilXRowId.Compute(_feed, byName);
                        break;
                    case OilXDerived.FileName:
                        values[i] = fileName;
                        break;
                    case OilXDerived.Checksum:
                        values[i] = OilXChecksum.Compute(_feed, byName);
                        break;
                    default:
                        throw new NotSupportedException($"Unmapped OilXDerived '{columns[i].Derived}'.");
                }
            }

            batch.Add(new OilXRow(values));

            if (batch.Count >= _settings.BatchSize)
            {
                rowsChanged += await writeBatch(batch, cancellationToken).ConfigureAwait(false);
                batch.Clear();
            }
        }

        if (indexes is null)
            throw new InvalidOperationException(
                $"OilX {_feed.FeedId}: '{fileName}' contained no header row.");

        if (batch.Count > 0)
            rowsChanged += await writeBatch(batch, cancellationToken).ConfigureAwait(false);

        if (cellsFailed > errorsLogged)
            _logger.LogWarning(
                "OilX {Feed} {File}: {Failed} cell conversion failure(s) in total; " +
                "only the first {Logged} were logged",
                _feed.FeedId, fileName, cellsFailed, errorsLogged);

        return new OilXFileResult(rowsRead, rowsDropped, cellsFailed, rowsChanged);
    }

    /// <summary>
    /// Map each descriptor column to its index in this file's header.
    ///
    /// <para>
    /// A column whose header is absent resolves to -1 and loads as NULL — historic files
    /// genuinely have fewer columns than today's. A REQUIRED column that is absent is
    /// fatal for the file instead: every row would be dropped one at a time, reporting a
    /// successful empty load, which is the silent failure worth being loud about.
    /// </para>
    /// </summary>
    private int[] ResolveHeader(string[] header, string fileName)
    {
        var map = OilXCsv.BuildHeaderMap(header);
        var columns = _feed.Columns;
        var indexes = new int[columns.Count];
        var missing = new List<string>();

        for (var i = 0; i < columns.Count; i++)
        {
            var column = columns[i];

            if (!column.FromCsv) { indexes[i] = -1; continue; }

            indexes[i] = OilXCsv.ResolveIndex(map, column);

            if (indexes[i] < 0)
            {
                if (column.Required) missing.Add(column.Name);
                else
                    _logger.LogInformation(
                        "OilX {Feed} {File}: no header for column {Column} (tried {Headers}) — NULL for every row",
                        _feed.FeedId, fileName, column.Name, string.Join(", ", column.SourceHeaders));
            }
        }

        if (missing.Count > 0)
            throw new InvalidOperationException(
                $"OilX {_feed.FeedId}: '{fileName}' has no header for required column(s) " +
                $"{string.Join(", ", missing)}. Header was: {string.Join(", ", header)}");

        return indexes;
    }

    private static string Truncate(string value) =>
        value.Length <= 80 ? value : value[..80] + "...";
}
