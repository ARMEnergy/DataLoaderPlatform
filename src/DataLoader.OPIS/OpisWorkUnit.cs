using System.Globalization;
using System.Text.RegularExpressions;
using DataLoader.Core.Abstractions;
using DataLoader.Core.Sources;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.OPIS;

/// <summary>
/// One work unit = one CSV file on the OPIS FTP drop.
///
/// <para>
/// <b>Resume key</b> (the idempotency contract — <c>core.LoadLog</c> skips a key
/// already recorded successful): the file name plus the server-reported
/// last-modified stamp and size, the Platts posture. A file whose content OPIS
/// republishes gets a new stamp and therefore a new key, so it is reprocessed;
/// an unchanged file is skipped on every subsequent run no matter how often the
/// loader is scheduled.
/// </para>
/// </summary>
public sealed class OpisWorkUnit : WorkUnit
{
    public required RemoteFile File { get; init; }

    /// <summary>Publication date parsed out of the file name (<c>yyyyMMdd</c>). The merge ordering guard.</summary>
    public required DateOnly SourceFileDate { get; init; }

    public override string Key =>
        $"opis:{File.Name}:{File.LastModifiedUtc:yyyyMMddHHmmss}:{File.Size}";

    public override string DisplayName => $"OPIS {File.Name} ({SourceFileDate:yyyy-MM-dd})";
}

/// <summary>
/// Enumerates the OPIS drop: every file in <see cref="OpisSettings.RemoteDirectory"/>
/// matching <see cref="OpisSettings.FilePattern"/> whose name carries a parseable
/// <c>yyyyMMdd</c> date.
///
/// <para>
/// Units are returned in ASCENDING file-date order. That is a best-effort
/// ordering only — <c>ParallelRunner</c> may still complete them out of order, so
/// correctness rests on the merge proc's <c>SourceFileDate</c> guard, not on this.
/// </para>
/// <para>
/// A file whose name does not match <see cref="OpisSettings.FileNameDatePattern"/>
/// is SKIPPED with a warning rather than silently dropped: without a date there is
/// no ordering guard, so merging it could let stale values win.
/// </para>
/// </summary>
public sealed class OpisWorkUnitProvider : IWorkUnitProvider<OpisWorkUnit>
{
    private readonly IOpisFtp _ftp;
    private readonly OpisSettings _settings;
    private readonly ILogger<OpisWorkUnitProvider> _logger;
    private readonly Regex _dateFromName;

    public OpisWorkUnitProvider(IOpisFtp ftp, IOptions<OpisSettings> settings, ILogger<OpisWorkUnitProvider> logger)
    {
        _ftp = ftp;
        _settings = settings.Value;
        _logger = logger;
        _dateFromName = new Regex(_settings.FileNameDatePattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    /// <summary>
    /// Newest file date from the most recent <see cref="GetWorkUnitsAsync"/> call,
    /// or null if that call produced no units. Lets the module scope its post-load
    /// validation to the day it just loaded without listing the drop a second time.
    /// </summary>
    public DateOnly? LastEnumeratedMaxDate { get; private set; }

    public async Task<IReadOnlyList<OpisWorkUnit>> GetWorkUnitsAsync(LoaderRunContext context)
    {
        var ct = context.CancellationToken;

        var files = await _ftp.ListAsync(_settings.RemoteDirectory, _settings.FilePattern, ct).ConfigureAwait(false);

        DateOnly? cutoff = _settings.DaysBack > 0
            ? DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(-_settings.DaysBack))
            : null;

        var units = new List<OpisWorkUnit>();
        var unparseable = 0;
        var filtered = 0;

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            var date = TryParseFileDate(file.Name);
            if (date is null)
            {
                unparseable++;
                _logger.LogWarning(
                    "OPIS: file {File} does not match FileNameDatePattern {Pattern} — skipped (no date = no merge ordering guard)",
                    file.Name, _settings.FileNameDatePattern);
                continue;
            }

            if (cutoff is not null && date.Value < cutoff.Value)
            {
                filtered++;
                continue;
            }

            units.Add(new OpisWorkUnit { File = file, SourceFileDate = date.Value });
        }

        units.Sort((a, b) => a.SourceFileDate.CompareTo(b.SourceFileDate));
        LastEnumeratedMaxDate = units.Count == 0 ? null : units[^1].SourceFileDate;

        _logger.LogInformation(
            "OPIS: {Count} work unit(s) from {Total} listed file(s) ({Filtered} outside DaysBack={DaysBack}, {Bad} unparseable name)",
            units.Count, files.Count, filtered, _settings.DaysBack, unparseable);

        return units;
    }

    /// <summary>Extract the <c>yyyyMMdd</c> publication date from a file name; null when it does not match.</summary>
    internal DateOnly? TryParseFileDate(string fileName)
    {
        var match = _dateFromName.Match(fileName);
        if (!match.Success || match.Groups.Count < 2) return null;

        return DateOnly.TryParseExact(
            match.Groups[1].Value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;
    }
}
