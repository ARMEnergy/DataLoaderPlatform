using System.Text;
using DataLoader.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.OPIS;

/// <summary>
/// Downloads one OPIS CSV over FTP, writes its <c>arm.FileLog</c> hub row, and
/// parses it into fact rows stamped with the resulting <c>FileLogId</c>.
///
/// <para>
/// The FileLog row is written on EVERY path — success and failure — so a file
/// that could not be downloaded or parsed still leaves an auditable record with
/// its error message, rather than vanishing. The pipeline then fails just this
/// work unit; the run continues with the remaining files.
/// </para>
/// </summary>
public sealed class OpisSourceReader : ISourceReader<OpisWorkUnit, OpisLpReportRow>
{
    private readonly IOpisFtp _ftp;
    private readonly IOpisFileLog _fileLog;
    private readonly OpisSettings _settings;
    private readonly ILogger<OpisSourceReader> _logger;

    public OpisSourceReader(
        IOpisFtp ftp, IOpisFileLog fileLog, IOptions<OpisSettings> settings, ILogger<OpisSourceReader> logger)
    {
        _ftp = ftp;
        _fileLog = fileLog;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<OpisLpReportRow>> ReadAsync(OpisWorkUnit unit, CancellationToken cancellationToken)
    {
        // Host + path only — never the credentials.
        var requestPath = $"ftp://{_settings.FtpHost}:{_settings.FtpPort}{unit.File.FullPath}";
        var context = new OpisFileContext(
            unit.File.Name, unit.SourceFileDate, unit.File.LastModifiedUtc, unit.File.Size, requestPath);

        try
        {
            var text = await DownloadTextAsync(unit, cancellationToken).ConfigureAwait(false);
            var rows = Parse(text, unit);

            var fileLogId = await _fileLog
                .UpsertAsync(context, "Success", rows.Count, null, cancellationToken)
                .ConfigureAwait(false);

            foreach (var row in rows) row.FileLogId = fileLogId;

            _logger.LogInformation("OPIS: {File} -> {Rows} row(s), FileLogId {FileLogId}",
                unit.File.Name, rows.Count, fileLogId);

            return rows;
        }
        catch (OperationCanceledException)
        {
            throw; // cancellation is not a file failure
        }
        catch (Exception ex)
        {
            // Record the failure in the hub before letting the pipeline fail this
            // unit, so the gap is visible in arm.FileLog and to usp_ValidateLoad.
            try
            {
                await _fileLog.UpsertAsync(context, "Failed", 0, ex.Message, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception logEx)
            {
                _logger.LogError(logEx, "OPIS: could not record Failed FileLog row for {File}", unit.File.Name);
            }

            _logger.LogError(ex, "OPIS: failed to read {File}", unit.File.Name);
            throw;
        }
    }

    private async Task<string> DownloadTextAsync(OpisWorkUnit unit, CancellationToken cancellationToken)
    {
        await using var stream = await _ftp.OpenReadAsync(unit.File.FullPath, cancellationToken).ConfigureAwait(false);

        // The feed is plain ASCII; Latin-1 decodes every byte without throwing, so a
        // stray high byte degrades to a character instead of failing the file.
        using var reader = new StreamReader(stream, Encoding.Latin1, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Parse a whole file. The header row is skipped, blank lines ignored, and a
    /// row that cannot be keyed is dropped and counted — one malformed line must
    /// not cost the other ~200 rows in the file.
    /// </summary>
    internal IReadOnlyList<OpisLpReportRow> Parse(string text, OpisWorkUnit unit)
    {
        var rows = new List<OpisLpReportRow>();
        var dropped = 0;
        string? firstDropReason = null;
        var degraded = 0;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (OpisCsv.IsHeaderOrBlank(line)) continue;

            var fields = OpisCsv.SplitLine(line);
            var row = OpisLpReportRow.From(fields, unit.SourceFileDate, _settings.DateFormats, out var reason);

            if (row is null)
            {
                dropped++;
                firstDropReason ??= reason;
                continue;
            }

            if (reason is not null) degraded++;
            rows.Add(row);
        }

        if (dropped > 0)
            _logger.LogWarning("OPIS: {File} — dropped {Dropped} unusable row(s) (first: {Reason})",
                unit.File.Name, dropped, firstDropReason);

        if (degraded > 0)
            _logger.LogWarning("OPIS: {File} — {Degraded} row(s) had a non-numeric price cell stored as NULL",
                unit.File.Name, degraded);

        if (rows.Count == 0)
            _logger.LogWarning("OPIS: {File} parsed to ZERO rows", unit.File.Name);

        return rows;
    }
}
