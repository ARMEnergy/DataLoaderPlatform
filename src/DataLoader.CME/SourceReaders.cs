using System.Text;
using DataLoader.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.CME;

/// <summary>
/// Downloads one CME bulletin over SFTP, parses it, and writes its
/// <c>arm.FileLog</c> hub row.
///
/// <para>
/// One reader serves every feed: everything feed-specific lives in
/// <see cref="CmeFeed"/> (the exchange code, the file name, the tick convention).
/// The FileLog row is written on EVERY path — success and failure — so a bulletin
/// that could not be downloaded or parsed leaves an auditable record with its
/// error message rather than vanishing. The pipeline then fails just that work
/// unit and the run continues with the other feeds.
/// </para>
/// </summary>
public sealed class CmeSourceReader : ISourceReader<CmeWorkUnit, CmeFactRow>
{
    private readonly ICmeSftp _sftp;
    private readonly ICmeFileLog _fileLog;
    private readonly CmeSettings _settings;
    private readonly ILogger _logger;

    public CmeSourceReader(
        ICmeSftp sftp, ICmeFileLog fileLog, IOptions<CmeSettings> settings, ILogger logger)
    {
        _sftp = sftp;
        _fileLog = fileLog;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<CmeFactRow>> ReadAsync(CmeWorkUnit unit, CancellationToken cancellationToken)
    {
        // Host + path only — never the credentials.
        var requestPath = $"sftp://{_settings.SftpHost}:{_settings.SftpPort}/{unit.File.FullPath}";

        var context = new CmeFileContext(
            unit.File.Name,
            unit.Feed.FeedId,
            unit.Feed.ExchangeCode,
            unit.Feed.ProductCode,
            unit.TradeDate,
            unit.File.LastModifiedUtc,
            unit.File.Size,
            requestPath);

        try
        {
            var text = await DownloadTextAsync(unit, cancellationToken).ConfigureAwait(false);

            var parser = new CmeBulletinParser(unit.Feed, unit.TradeDate, _settings.StrictLineParsing);
            var (rows, stats) = parser.Parse(text);

            await _fileLog
                .UpsertAsync(context, "Success", stats.TotalRows, stats, WarningsFor(stats), cancellationToken)
                .ConfigureAwait(false);

            LogOutcome(unit, stats);

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
                await _fileLog.UpsertAsync(context, "Failed", 0, null, ex.Message, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception logEx)
            {
                _logger.LogError(logEx, "CME: could not record Failed FileLog row for {File}", unit.File.Name);
            }

            _logger.LogError(ex, "CME: failed to read {Feed} {File}", unit.Feed.FeedId, unit.File.Name);
            throw;
        }
    }

    private async Task<string> DownloadTextAsync(CmeWorkUnit unit, CancellationToken cancellationToken)
    {
        await using var stream = await _sftp.OpenReadAsync(unit.File.FullPath, cancellationToken).ConfigureAwait(false);

        // The bulletins are ASCII; Latin-1 decodes every byte without throwing, so a
        // stray high byte in a product name degrades to a character instead of
        // failing the whole file.
        using var reader = new StreamReader(stream, Encoding.Latin1, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Fold the parse counters that represent DROPPED data into the FileLog row's
    /// message, so a successful load that nonetheless discarded rows is visible in
    /// the database and not only in a log file.
    /// </summary>
    private static string? WarningsFor(CmeParseStats stats)
    {
        var parts = new List<string>();

        if (stats.DayLabelRowsSkipped > 0)
            parts.Add($"{stats.DayLabelRowsSkipped} BALMO/day-label future row(s) skipped (Future key has no day component)");

        if (stats.UnclassifiedLines > 0)
            parts.Add($"{stats.UnclassifiedLines} unclassified line(s)");

        if (stats.ValueParseFailures > 0)
            parts.Add($"{stats.ValueParseFailures} value(s) unparseable (stored NULL)");

        if (stats.TickAnomalies > 0)
            parts.Add($"{stats.TickAnomalies} tick value(s) outside this feed's convention (stored NULL)");

        if (stats.UnexpectedIndicators > 0)
            parts.Add($"{stats.UnexpectedIndicators} A/B indicator(s) on a field with no indicator column (dropped)");

        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    private void LogOutcome(CmeWorkUnit unit, CmeParseStats stats)
    {
        _logger.LogInformation(
            "CME: {Feed} {Date} {File} -> {Options} option + {Futures} future row(s) from {Sections} section(s)" +
            "{Ticks}{Cab}",
            unit.Feed.FeedId, CmeTime.Iso(unit.TradeDate), unit.File.Name,
            stats.OptionRows, stats.FutureRows, stats.ProductSections,
            stats.TickValues > 0 ? $"; {stats.TickValues} tick value(s) converted" : string.Empty,
            stats.CabinetValues > 0 ? $"; {stats.CabinetValues} CAB value(s) stored NULL" : string.Empty);

        // Skipped rows are DATA LOSS, so they get their own line at warning level
        // rather than being folded into the summary above.
        if (stats.DayLabelRowsSkipped > 0)
            _logger.LogWarning(
                "CME: {Feed} {Date} skipped {Count} future row(s) whose label is a day of month (BALMO/event " +
                "contracts) — arm.STLBASIC_Future has no day component in its key, so these cannot be stored " +
                "without collapsing onto one row per product/month",
                unit.Feed.FeedId, CmeTime.Iso(unit.TradeDate), stats.DayLabelRowsSkipped);

        if (stats.UnclassifiedLines > 0)
            _logger.LogWarning(
                "CME: {Feed} {Date} had {Count} unclassified line(s) — one is expected per STLEQT bulletin " +
                "(the 'KYP11 <br />' artifact); more than that suggests the layout has changed",
                unit.Feed.FeedId, CmeTime.Iso(unit.TradeDate), stats.UnclassifiedLines);

        if (stats.TickAnomalies > 0 || stats.UnexpectedIndicators > 0 || stats.ValueParseFailures > 0)
            _logger.LogWarning(
                "CME: {Feed} {Date} value anomalies — {Ticks} tick width(s) outside convention, " +
                "{Indicators} unexpected indicator(s), {Failures} unparseable value(s); all stored NULL",
                unit.Feed.FeedId, CmeTime.Iso(unit.TradeDate),
                stats.TickAnomalies, stats.UnexpectedIndicators, stats.ValueParseFailures);
    }
}
