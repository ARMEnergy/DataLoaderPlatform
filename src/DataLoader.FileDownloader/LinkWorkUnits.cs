using DataLoader.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.FileDownloader;

/// <summary>
/// Builds a download URL from a source's <c>BaseURL</c> template and a queue row's
/// <c>Value</c>.
/// </summary>
public static class UrlTemplate
{
    /// <summary>
    /// <c>BaseURL</c> is a <see cref="string.Format(string,object?[])"/> template and
    /// <c>Value</c> is its <c>;</c>-delimited argument list — the original app's
    /// convention, kept so the 17 configured sources work unchanged.
    ///
    /// <para>⚠ A blank <c>Value</c> yields ZERO arguments, which is correct: several
    /// sources have a constant URL and store <c>''</c>. <c>"a;;b"</c> keeps the empty
    /// middle argument rather than dropping it, because argument POSITION is what the
    /// template binds to — silently collapsing it would shift every later one.</para>
    /// </summary>
    public static string Build(string baseUrl, string? value, string sourceName)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new InvalidOperationException($"Source '{sourceName}' has a blank BaseURL.");

        var args = string.IsNullOrEmpty(value)
            ? Array.Empty<object>()
            // No RemoveEmptyEntries: see the note above.
            : value.Split(';').Cast<object>().ToArray();

        try
        {
            return string.Format(baseUrl, args);
        }
        catch (FormatException ex)
        {
            // The vendor-agnostic failure here is a template/argument mismatch, and the
            // default message ("Index (zero based) must be greater than...") never says
            // which of 17 sources is at fault.
            throw new InvalidOperationException(
                $"Source '{sourceName}': BaseURL template and its {args.Length} Value " +
                $"argument(s) do not match.", ex);
        }
    }
}

/// <summary>
/// Enumerates Link-mode work units: every pending row of every active source's queue.
///
/// <para>One unit per FILE, not per source. The standalone app loops files inside one
/// try/catch per source, so a single bad file ends that source's batch and nothing
/// records which files had already succeeded.</para>
/// </summary>
public sealed class LinkWorkUnitProvider : IWorkUnitProvider<FileDownloadUnit>
{
    private readonly IFileDownloaderCatalog _catalog;
    private readonly IOptions<FileDownloaderSettings> _options;
    private readonly ILogger<LinkWorkUnitProvider> _logger;

    public LinkWorkUnitProvider(
        IFileDownloaderCatalog catalog,
        IOptions<FileDownloaderSettings> options,
        ILogger<LinkWorkUnitProvider> logger)
    {
        _catalog = catalog;
        _options = options;
        _logger = logger;
    }

    public async Task<IReadOnlyList<FileDownloadUnit>> GetWorkUnitsAsync(LoaderRunContext context)
    {
        var settings = _options.Value;

        if (!settings.EnabledModes.Contains(
                nameof(FileAcquisitionMode.Link), StringComparer.OrdinalIgnoreCase))
        {
            _logger.LogInformation("[FileDownloader] Link mode is not in EnabledModes — no units.");
            return Array.Empty<FileDownloadUnit>();
        }

        var sources = await _catalog
            .GetActiveSourcesAsync(context.CancellationToken).ConfigureAwait(false);

        var units = new List<FileDownloadUnit>();

        foreach (var source in sources)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            var queue = await _catalog
                .GetQueueAsync(source.Id, context.CancellationToken).ConfigureAwait(false);

            foreach (var row in queue)
            {
                string url;
                try
                {
                    url = UrlTemplate.Build(source.BaseUrl, row.Value, source.Name);
                }
                catch (InvalidOperationException ex)
                {
                    // ⚠ Skip the ROW, do not fail enumeration. A malformed template on one
                    // of 17 sources must not stop the other 16 from running — the standalone
                    // app's single process-wide try/catch is exactly what made one bad
                    // source able to end a whole batch.
                    _logger.LogError(ex,
                        "[FileDownloader] {Source} row {RowId}: cannot build URL — skipped",
                        source.Name, row.Id);
                    continue;
                }

                units.Add(new FileDownloadUnit
                {
                    Mode = FileAcquisitionMode.Link,
                    SourceName = source.Name,
                    RemoteLocator = url,
                    DestinationPath = source.DestinationPath,
                    FileName = row.FileName,
                    InfoId = source.Id,
                    ListRowId = row.Id,
                    FileDate = null
                });
            }

            _logger.LogInformation(
                "[FileDownloader] {Source}: {Count} pending", source.Name, queue.Count);
        }

        _logger.LogInformation(
            "[FileDownloader] enumerated {Units} Link unit(s) across {Sources} source(s)",
            units.Count, sources.Count);

        return units;
    }
}
