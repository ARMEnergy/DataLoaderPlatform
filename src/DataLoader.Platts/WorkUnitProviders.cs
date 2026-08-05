using DataLoader.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.Platts;

/// <summary>
/// Enumerates SymbolData work units: every <c>.ftp</c> file inside every date
/// folder under the configured root. One file = one work unit.
/// </summary>
public sealed class SymbolDataWorkUnitProvider : IWorkUnitProvider<SymbolDataWorkUnit>
{
    private readonly IPlattsSftp _sftp;
    private readonly PlattsSettings _settings;
    private readonly ILogger<SymbolDataWorkUnitProvider> _logger;

    public SymbolDataWorkUnitProvider(IPlattsSftp sftp, IOptions<PlattsSettings> settings, ILogger<SymbolDataWorkUnitProvider> logger)
    {
        _sftp = sftp;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<SymbolDataWorkUnit>> GetWorkUnitsAsync(LoaderRunContext context)
    {
        var ct = context.CancellationToken;

        var folders = await _sftp.ListDirectoriesAsync(_settings.RootDirectory, ct).ConfigureAwait(false);
        _logger.LogInformation("Platts SymbolData: {Count} date folders under {Root}", folders.Count, _settings.RootDirectory);

        var units = new List<SymbolDataWorkUnit>();
        foreach (var folder in folders)
        {
            ct.ThrowIfCancellationRequested();

            var files = await _sftp.ListAsync(folder, _settings.MarketDataFilePattern, ct).ConfigureAwait(false);
            var folderName = folder.TrimEnd('/').Split('/').Last();
            foreach (var file in files)
                units.Add(new SymbolDataWorkUnit { File = file, Folder = folderName });
        }

        _logger.LogInformation("Platts SymbolData: {Count} .ftp work units across {Folders} folders",
            units.Count, folders.Count);
        return units;
    }
}

/// <summary>
/// Enumerates Symbol reference work units: every CSV in the symbols directory.
/// One file = one work unit.
/// </summary>
public sealed class SymbolWorkUnitProvider : IWorkUnitProvider<SymbolWorkUnit>
{
    private readonly IPlattsSftp _sftp;
    private readonly PlattsSettings _settings;
    private readonly ILogger<SymbolWorkUnitProvider> _logger;

    public SymbolWorkUnitProvider(IPlattsSftp sftp, IOptions<PlattsSettings> settings, ILogger<SymbolWorkUnitProvider> logger)
    {
        _sftp = sftp;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<SymbolWorkUnit>> GetWorkUnitsAsync(LoaderRunContext context)
    {
        var files = await _sftp.ListAsync(_settings.SymbolsDirectory, _settings.SymbolFilePattern, context.CancellationToken)
                               .ConfigureAwait(false);
        _logger.LogInformation("Platts Symbol: {Count} CSV work units in {Dir}", files.Count, _settings.SymbolsDirectory);
        return files.Select(f => new SymbolWorkUnit { File = f }).ToList();
    }
}
