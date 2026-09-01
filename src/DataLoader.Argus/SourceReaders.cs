using System.Text;
using DataLoader.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.Argus;

/// <summary>
/// Raised when a file's header does not carry every column its descriptor needs.
///
/// <para>
/// This is a HARD failure by design. The TVP binds by position, so continuing with
/// a missing column would mean guessing — and a wrong guess writes plausible
/// garbage rather than raising. Failing the file leaves the previous data intact,
/// records the reason in <c>dlp.FileLog</c>, and lets the other feeds finish.
/// </para>
/// </summary>
public sealed class ArgusHeaderContractException : Exception
{
    public ArgusHeaderContractException(string message) : base(message) { }
}

/// <summary>
/// Downloads one Argus CSV over FTP, writes its <c>dlp.FileLog</c> hub row, and
/// parses it into <see cref="ArgusRow"/> values ordered by the feed descriptor.
///
/// <para>
/// One reader serves all 16 feeds: everything feed-specific lives in the
/// descriptor. The FileLog row is written on EVERY path — success and failure — so
/// a file that could not be downloaded or parsed leaves an auditable record with
/// its error message rather than vanishing. The pipeline then fails just that work
/// unit and the run continues.
/// </para>
/// </summary>
public sealed class ArgusSourceReader : ISourceReader<ArgusWorkUnit, ArgusRow>
{
    private readonly IArgusFtp _ftp;
    private readonly IArgusFileLog _fileLog;
    private readonly ArgusSettings _settings;
    private readonly ILogger _logger;

    public ArgusSourceReader(
        IArgusFtp ftp, IArgusFileLog fileLog, IOptions<ArgusSettings> settings, ILogger logger)
    {
        _ftp = ftp;
        _fileLog = fileLog;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ArgusRow>> ReadAsync(ArgusWorkUnit unit, CancellationToken cancellationToken)
    {
        // Host + path only — never the credentials.
        var requestPath = $"ftp://{_settings.FtpHost}:{_settings.FtpPort}{unit.File.FullPath}";
        var context = new ArgusFileContext(
            unit.File.Name, unit.Feed.FeedId, unit.Feed.RemoteFolder, unit.SourceFileDate,
            unit.File.LastModifiedUtc, unit.File.Size, requestPath);

        try
        {
            var text = await DownloadTextAsync(unit, cancellationToken).ConfigureAwait(false);
            var rows = Parse(text, unit);

            await _fileLog.UpsertAsync(context, "Success", rows.Count, null, cancellationToken)
                .ConfigureAwait(false);

            _logger.LogInformation("Argus: {Feed} {File} -> {Rows} row(s)",
                unit.Feed.FeedId, unit.File.Name, rows.Count);

            return rows;
        }
        catch (OperationCanceledException)
        {
            throw; // cancellation is not a file failure
        }
        catch (Exception ex)
        {
            // Record the failure in the hub BEFORE letting the pipeline fail this
            // unit, so the gap is visible in dlp.FileLog and to usp_ValidateLoad.
            try
            {
                await _fileLog.UpsertAsync(context, "Failed", 0, ex.Message, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception logEx)
            {
                _logger.LogError(logEx, "Argus: could not record Failed FileLog row for {File}", unit.File.Name);
            }

            _logger.LogError(ex, "Argus: failed to read {Feed} {File}", unit.Feed.FeedId, unit.File.Name);
            throw;
        }
    }

    private async Task<string> DownloadTextAsync(ArgusWorkUnit unit, CancellationToken cancellationToken)
    {
        await using var stream = await _ftp.OpenReadAsync(unit.File.FullPath, cancellationToken).ConfigureAwait(false);

        // The feed is ASCII; Latin-1 decodes every byte without throwing, so a
        // stray high byte degrades to a character instead of failing the file.
        using var reader = new StreamReader(stream, Encoding.Latin1, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Parse a whole file against its descriptor.
    ///
    /// <para>Failure policy, in order of severity:</para>
    /// <list type="bullet">
    ///   <item>Empty file, or a header missing a needed column -> throw. Contract
    ///         drift must be loud.</item>
    ///   <item>Wrong field count on a row -> drop that row, count it.</item>
    ///   <item>Blank/unparseable REQUIRED column -> drop that row, count it. It
    ///         cannot be keyed, so merging it under a blank would be worse.</item>
    ///   <item>Blank/unparseable optional column -> store NULL, count as degraded.</item>
    /// </list>
    /// <para>One malformed line never costs the rest of the file.</para>
    /// </summary>
    internal IReadOnlyList<ArgusRow> Parse(string text, ArgusWorkUnit unit)
    {
        var feed = unit.Feed;
        var records = ArgusCsv.ParseRecords(text);

        if (records.Count == 0)
            throw new ArgusHeaderContractException(
                $"Argus {feed.FeedId}: '{unit.File.Name}' is empty — no header row.");

        var header = records[0];
        var headerMap = ArgusCsv.BuildHeaderMap(header);

        // Resolve every descriptor column to a source index ONCE, up front.
        var indexes = new int[feed.Columns.Count];
        var missing = new List<string>();

        for (var c = 0; c < feed.Columns.Count; c++)
        {
            var column = feed.Columns[c];
            if (column.Derived != ArgusDerived.None) { indexes[c] = -1; continue; }

            if (headerMap.TryGetValue(column.SourceHeader!, out var index)) indexes[c] = index;
            else missing.Add(column.SourceHeader!);
        }

        if (missing.Count > 0)
            throw new ArgusHeaderContractException(
                $"Argus {feed.FeedId}: '{unit.File.Name}' header is missing required column(s) " +
                $"[{string.Join(", ", missing)}]. Header was [{string.Join(", ", header)}]. " +
                "The TVP binds by position, so the file is rejected rather than loaded from a guess.");

        var derived = BuildDerivedValues(unit);

        var rows = new List<ArgusRow>(Math.Max(0, records.Count - 1));
        var droppedFieldCount = 0;
        var droppedRequired = 0;
        var degraded = 0;
        string? firstDropReason = null;

        for (var r = 1; r < records.Count; r++)
        {
            var record = records[r];

            if (record.Length != header.Length)
            {
                droppedFieldCount++;
                firstDropReason ??= $"row {r + 1}: expected {header.Length} fields, got {record.Length}";
                continue;
            }

            var values = new object[feed.Columns.Count];
            var drop = false;

            for (var c = 0; c < feed.Columns.Count; c++)
            {
                var column = feed.Columns[c];

                if (column.Derived != ArgusDerived.None)
                {
                    values[c] = derived[column.Derived];
                    // A derived REQUIRED value that the unit could not supply (e.g. a
                    // dated feed unit with no parsed date) is a bug, not bad data.
                    if (column.Required && values[c] == DBNull.Value)
                        throw new ArgusHeaderContractException(
                            $"Argus {feed.FeedId}: derived column '{column.Name}' has no value for '{unit.File.Name}'.");
                    continue;
                }

                var raw = record[indexes[c]];

                if (!ArgusCsv.TryConvert(column.Type, raw, out var converted))
                {
                    if (column.Required)
                    {
                        drop = true;
                        firstDropReason ??= $"row {r + 1}: unparseable required '{column.Name}' = '{Clip(raw)}'";
                        break;
                    }

                    degraded++;
                    values[c] = DBNull.Value;
                    continue;
                }

                if (column.Required && converted == DBNull.Value)
                {
                    drop = true;
                    firstDropReason ??= $"row {r + 1}: blank required '{column.Name}'";
                    break;
                }

                values[c] = converted;
            }

            if (drop) { droppedRequired++; continue; }

            rows.Add(new ArgusRow(values));
        }

        if (droppedFieldCount > 0 || droppedRequired > 0)
            _logger.LogWarning(
                "Argus: {Feed} {File} — dropped {FieldCount} row(s) on field count and {Required} on a required column (first: {Reason})",
                feed.FeedId, unit.File.Name, droppedFieldCount, droppedRequired, firstDropReason);

        if (degraded > 0)
            _logger.LogWarning("Argus: {Feed} {File} — {Degraded} unparseable optional cell(s) stored as NULL",
                feed.FeedId, unit.File.Name, degraded);

        if (rows.Count == 0)
            _logger.LogWarning("Argus: {Feed} {File} parsed to ZERO rows", feed.FeedId, unit.File.Name);

        return rows;
    }

    /// <summary>
    /// The non-CSV values for this unit. Reference feeds have none; the DCRDEUS
    /// feed supplies Module, the file date (which lands in RecordStatusDate AND in
    /// the TVP-only SourceFileDate guard) and the remote path.
    /// </summary>
    private static Dictionary<ArgusDerived, object> BuildDerivedValues(ArgusWorkUnit unit) => new()
    {
        [ArgusDerived.Module] = (object?)unit.Module ?? DBNull.Value,
        [ArgusDerived.SourceFileDate] = unit.SourceFileDate.HasValue
            ? unit.SourceFileDate.Value.ToDateTime(TimeOnly.MinValue)
            : DBNull.Value,
        // The FTP path, per the spec for SourcePath — path only, no host, no
        // credentials. dlp.FileLog.RequestPath carries the host-qualified form.
        [ArgusDerived.SourcePath] = unit.File.FullPath
    };

    private static string Clip(string value) => value.Length <= 40 ? value : value[..40] + "...";
}
