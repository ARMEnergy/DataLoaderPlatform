using System.Text;
using DataLoader.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.EOX;

/// <summary>
/// Raised when a file's header does not carry every column its descriptor needs.
///
/// <para>
/// This is a HARD failure by design. The TVP binds by position, so continuing with
/// a missing column would mean guessing — and a wrong guess writes plausible
/// garbage rather than raising. Failing the file leaves the previous data intact,
/// records the reason in <c>arm.FileLog</c>, and lets the other feeds finish.
/// </para>
/// <para>
/// The one column allowed to be absent is <c>FP</c>, which
/// <see cref="EoxColumn.HeaderOptional"/> marks — NaturalGas files older than
/// roughly 2016 genuinely do not have it, and loading them with <c>FP</c> NULL is
/// right rather than lenient.
/// </para>
/// </summary>
public sealed class EoxHeaderContractException : Exception
{
    public EoxHeaderContractException(string message) : base(message) { }
}

/// <summary>
/// Downloads one EOX CSV over FTP, writes its <c>arm.FileLog</c> hub row, and
/// parses it into <see cref="EoxRow"/> values ordered by the feed descriptor.
///
/// <para>
/// One reader serves all three feeds: everything feed-specific lives in the
/// descriptor. The FileLog row is written on EVERY path — success and failure — so
/// a file that could not be downloaded or parsed leaves an auditable record with
/// its error message rather than vanishing. The pipeline then fails just that work
/// unit and the run continues.
/// </para>
/// </summary>
public sealed class EoxSourceReader : ISourceReader<EoxWorkUnit, EoxRow>
{
    private readonly IEoxFtp _ftp;
    private readonly IEoxFileLog _fileLog;
    private readonly EoxSettings _settings;
    private readonly ILogger _logger;

    public EoxSourceReader(
        IEoxFtp ftp, IEoxFileLog fileLog, IOptions<EoxSettings> settings, ILogger logger)
    {
        _ftp = ftp;
        _fileLog = fileLog;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<EoxRow>> ReadAsync(EoxWorkUnit unit, CancellationToken cancellationToken)
    {
        // Host + path only — never the credentials.
        var requestPath = $"ftp://{_settings.FtpHost}:{_settings.FtpPort}{unit.File.FullPath}";
        var context = new EoxFileContext(
            unit.File.Name, unit.Feed.FeedId, unit.CurveDate,
            unit.File.LastModifiedUtc, unit.File.Size, requestPath);

        try
        {
            var text = await DownloadTextAsync(unit, cancellationToken).ConfigureAwait(false);
            var rows = Parse(text, unit);

            await _fileLog.UpsertAsync(context, "Success", rows.Count, null, cancellationToken)
                .ConfigureAwait(false);

            _logger.LogInformation("EOX: {Feed} {File} -> {Rows} row(s)",
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
            // unit, so the gap is visible in arm.FileLog and to usp_ValidateLoad.
            try
            {
                await _fileLog.UpsertAsync(context, "Failed", 0, ex.Message, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception logEx)
            {
                _logger.LogError(logEx, "EOX: could not record Failed FileLog row for {File}", unit.File.Name);
            }

            _logger.LogError(ex, "EOX: failed to read {Feed} {File}", unit.Feed.FeedId, unit.File.Name);
            throw;
        }
    }

    private async Task<string> DownloadTextAsync(EoxWorkUnit unit, CancellationToken cancellationToken)
    {
        await using var stream = await _ftp.OpenReadAsync(unit.File.FullPath, cancellationToken).ConfigureAwait(false);

        // The feed is ASCII; Latin-1 decodes every byte without throwing, so a stray
        // high byte in a location name degrades to a character instead of failing
        // the file.
        using var reader = new StreamReader(stream, Encoding.Latin1, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Parse a whole file against its descriptor.
    ///
    /// <para>Failure policy, in order of severity:</para>
    /// <list type="bullet">
    ///   <item>Empty file, or a header missing a REQUIRED column -> throw. Contract
    ///         drift must be loud.</item>
    ///   <item>Header missing an optional column (<c>FP</c> only) -> that column is
    ///         NULL for every row; the file loads.</item>
    ///   <item>Wrong field count on a row -> drop that row, count it.</item>
    ///   <item>Blank/unparseable REQUIRED column -> drop that row, count it. It
    ///         cannot be keyed, so merging it under a blank would be worse.</item>
    ///   <item>Blank/unparseable optional column -> store NULL, count as degraded.</item>
    /// </list>
    /// <para>One malformed line never costs the rest of the file.</para>
    /// <para>
    /// A row whose <c>CurveDate</c> disagrees with the date in the file NAME is
    /// kept — the file's own value is authoritative — but counted and warned. That
    /// equality is the assumption behind having no separate ordering column in the
    /// fact tables (see <c>sql/EOX/001</c>), so it must be visible if it ever
    /// breaks. <c>arm.usp_ValidateLoad</c> reports the same thing from the loaded
    /// data.
    /// </para>
    /// </summary>
    internal IReadOnlyList<EoxRow> Parse(string text, EoxWorkUnit unit)
    {
        var feed = unit.Feed;
        var records = EoxCsv.ParseRecords(text);

        if (records.Count == 0)
            throw new EoxHeaderContractException(
                $"EOX {feed.FeedId}: '{unit.File.Name}' is empty — no header row.");

        var header = records[0];
        var headerMap = EoxCsv.BuildHeaderMap(header);

        // Resolve every descriptor column to a source index ONCE, up front.
        var indexes = new int[feed.Columns.Count];
        var missing = new List<string>();
        var absentOptional = new List<string>();

        for (var c = 0; c < feed.Columns.Count; c++)
        {
            var column = feed.Columns[c];

            if (column.Derived != EoxDerived.None) { indexes[c] = -1; continue; }

            var index = EoxCsv.ResolveIndex(headerMap, column);
            indexes[c] = index;

            if (index >= 0) continue;

            if (column.HeaderOptional) absentOptional.Add(column.Name);
            else missing.Add($"{column.Name} (any of: {string.Join(" | ", column.SourceHeaders)})");
        }

        if (missing.Count > 0)
            throw new EoxHeaderContractException(
                $"EOX {feed.FeedId}: '{unit.File.Name}' header is missing required column(s) " +
                $"[{string.Join(", ", missing)}]. Header was [{string.Join(", ", header)}]. " +
                "The TVP binds by position, so the file is rejected rather than loaded from a guess.");

        if (absentOptional.Count > 0)
            _logger.LogInformation(
                "EOX: {Feed} {File} has no {Columns} column — expected for files published before EOX added it; stored NULL",
                feed.FeedId, unit.File.Name, string.Join(", ", absentOptional));

        var fileNameValue = unit.File.Name;
        var expectedCurveDate = unit.CurveDate.ToDateTime(TimeOnly.MinValue);
        var curveDateOrdinal = feed.Columns.ToList().FindIndex(c => c.Name == "CurveDate");

        var rows = new List<EoxRow>(Math.Max(0, records.Count - 1));
        var droppedFieldCount = 0;
        var droppedRequired = 0;
        var degraded = 0;
        var curveDateMismatch = 0;
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

                if (column.Derived == EoxDerived.FileName)
                {
                    values[c] = fileNameValue;
                    continue;
                }

                // A header the file does not have: NULL, already reported above.
                if (indexes[c] < 0)
                {
                    values[c] = DBNull.Value;
                    continue;
                }

                var raw = record[indexes[c]];

                if (!EoxCsv.TryConvert(column.Type, raw, out var converted))
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

            if (curveDateOrdinal >= 0 && values[curveDateOrdinal] is DateTime actual && actual != expectedCurveDate)
                curveDateMismatch++;

            rows.Add(new EoxRow(values));
        }

        if (droppedFieldCount > 0 || droppedRequired > 0)
            _logger.LogWarning(
                "EOX: {Feed} {File} — dropped {FieldCount} row(s) on field count and {Required} on a required column (first: {Reason})",
                feed.FeedId, unit.File.Name, droppedFieldCount, droppedRequired, firstDropReason);

        if (degraded > 0)
            _logger.LogWarning("EOX: {Feed} {File} — {Degraded} unparseable optional cell(s) stored as NULL",
                feed.FeedId, unit.File.Name, degraded);

        if (curveDateMismatch > 0)
            _logger.LogWarning(
                "EOX: {Feed} {File} — {Count} row(s) carry a Curve_Date other than {Expected}. " +
                "The fact tables have no separate ordering column because that never happens; " +
                "two files can now collide on one primary key, resolved by FileName order",
                feed.FeedId, unit.File.Name, curveDateMismatch, EoxTime.Iso(unit.CurveDate));

        if (rows.Count == 0)
            _logger.LogWarning("EOX: {Feed} {File} parsed to ZERO rows", feed.FeedId, unit.File.Name);

        return rows;
    }

    private static string Clip(string value) => value.Length <= 40 ? value : value[..40] + "...";
}
