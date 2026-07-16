using DataLoader.Core.Abstractions;
using DataLoader.Core.Sources;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.CsvExample;

/// <summary>
/// Enumerates CSV files in the configured drop directory using the
/// pluggable <see cref="IFileSystemDriver"/>. Could just as easily list
/// files from S3 or an FTP server by swapping the driver registration.
/// </summary>
public sealed class CsvWorkUnitProvider : IWorkUnitProvider<CsvFileWorkUnit>
{
    private readonly ICsvFileSystem _fs;
    private readonly CsvExampleSettings _settings;
    private readonly ILogger<CsvWorkUnitProvider> _logger;

    public CsvWorkUnitProvider(
        ICsvFileSystem fs,
        IOptions<CsvExampleSettings> settings,
        ILogger<CsvWorkUnitProvider> logger)
    {
        _fs = fs;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<CsvFileWorkUnit>> GetWorkUnitsAsync(LoaderRunContext context)
    {
        if (string.IsNullOrWhiteSpace(_settings.DropDirectory))
        {
            _logger.LogWarning("DropDirectory not configured");
            return Array.Empty<CsvFileWorkUnit>();
        }

        var files = await _fs.ListAsync(_settings.DropDirectory, _settings.FilePattern, context.CancellationToken)
                              .ConfigureAwait(false);

        _logger.LogInformation("Found {Count} files in {Dir} matching {Pattern}",
            files.Count, _settings.DropDirectory, _settings.FilePattern);

        return files.Select(f => new CsvFileWorkUnit { File = f }).ToList();
    }
}
