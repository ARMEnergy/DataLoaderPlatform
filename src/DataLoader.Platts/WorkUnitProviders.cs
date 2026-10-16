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

        if (_settings.ReloadAllFiles)
            _logger.LogWarning(
                "Platts SymbolData: ReloadAllFiles is ON — every .ftp file in the window will be re-read " +
                "and re-merged, ignoring core.LoadLog history");

        // One reference date for the whole pass, so a run that straddles midnight
        // UTC cannot classify its first folders differently from its last.
        var todayUtc = DateOnly.FromDateTime(context.StartedAtUtc);

        var folders = await _sftp.ListDirectoriesAsync(_settings.RootDirectory, ct).ConfigureAwait(false);
        _logger.LogInformation("Platts SymbolData: {Count} date folders under {Root}", folders.Count, _settings.RootDirectory);

        var units = new List<SymbolDataWorkUnit>();
        var listed = 0;
        var outOfWindow = 0;
        var hotFolders = 0;
        foreach (var folder in folders)
        {
            ct.ThrowIfCancellationRequested();

            var folderName = folder.TrimEnd('/').Split('/').Last();
            var ageDays = PlattsFolderWindow.AgeDays(folderName, todayUtc);

            // Outside DaysBack: skip before listing, so an excluded folder costs no
            // SFTP round-trip at all.
            if (!PlattsFolderWindow.InWindow(ageDays, _settings.DaysBack))
            {
                outOfWindow++;
                continue;
            }

            if (ageDays is null)
                _logger.LogWarning(
                    "Platts SymbolData: folder '{Folder}' is not a {Format} date; keeping it, but it cannot be " +
                    "windowed and is treated as settled", folderName, "yyyyMMdd");

            var runToken = PlattsFolderWindow.RunTokenFor(ageDays, _settings, context);
            if (runToken is not null) hotFolders++;

            var files = await _sftp.ListAsync(folder, _settings.MarketDataFilePattern, ct).ConfigureAwait(false);
            listed++;
            foreach (var file in files)
                units.Add(new SymbolDataWorkUnit { File = file, Folder = folderName, RunToken = runToken });
        }

        _logger.LogInformation(
            "Platts SymbolData: {Count} .ftp work units across {Listed} folders " +
            "({Hot} hot, {Settled} settled, {Skipped} outside DaysBack={DaysBack}, SettledAfterDays={Settle})",
            units.Count, listed, hotFolders, listed - hotFolders, outOfWindow,
            _settings.DaysBack, _settings.SettledAfterDays);
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
        var runToken = PlattsKey.RunToken(_settings, context);
        if (runToken is not null)
            _logger.LogWarning(
                "Platts Symbol: ReloadAllFiles is ON — every reference CSV will be re-read and re-merged, " +
                "ignoring core.LoadLog history");

        var files = await _sftp.ListAsync(_settings.SymbolsDirectory, _settings.SymbolFilePattern, context.CancellationToken)
                               .ConfigureAwait(false);
        _logger.LogInformation("Platts Symbol: {Count} CSV work units in {Dir}", files.Count, _settings.SymbolsDirectory);
        return files.Select(f => new SymbolWorkUnit { File = f, RunToken = runToken }).ToList();
    }
}
