using System.Globalization;
using System.Text.RegularExpressions;
using DataLoader.Core.Abstractions;
using DataLoader.Core.Sources;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.Argus;

/// <summary>
/// One work unit = one CSV file on the Argus FTP drop.
///
/// <para>
/// <b>Resume key</b> (the idempotency contract — <c>core.LoadLog</c> skips a key
/// already recorded successful): the folder, file name, server-reported
/// last-modified stamp and size. A file whose content Argus republishes gets a new
/// stamp and therefore a new key, so it is reprocessed; an unchanged file is
/// skipped however often the loader is scheduled.
/// </para>
/// <para>
/// The DOCUMENTATION snapshots are overwritten daily, so their stamp changes daily
/// and they reload every day. The DCRDEUS dated files stop changing once
/// published, so after the first run only the newest one or two do any work — and
/// because re-merging identical content is a no-op, that skip cannot change the
/// database's final state. <c>ForceReprocess</c> folds the run date into
/// <see cref="KeySalt"/> to disable it.
/// </para>
/// </summary>
public sealed class ArgusWorkUnit : WorkUnit
{
    public required ArgusFeedDescriptor Feed { get; init; }
    public required RemoteFile File { get; init; }

    /// <summary>
    /// Publication date parsed from the file name. Set for DCRDEUS files only; it
    /// is the merge ordering guard AND the value stored in
    /// <c>RecordStatusDate</c>. Null for the reference snapshots, whose names carry
    /// no date.
    /// </summary>
    public DateOnly? SourceFileDate { get; init; }

    /// <summary>
    /// <c>Module</c> for DCRDEUS rows: the UPPERCASED file-name suffix, always.
    /// Null for reference feeds.
    /// </summary>
    public string? Module { get; init; }

    /// <summary>Extra key component that defeats the LoadLog skip. Empty unless <c>ForceReprocess</c>.</summary>
    public string KeySalt { get; init; } = string.Empty;

    public override string Key =>
        $"argus:{Feed.RemoteFolder}:{File.Name}:{File.LastModifiedUtc:yyyyMMddHHmmss}:{File.Size}{KeySalt}";

    public override string DisplayName =>
        SourceFileDate is null
            ? $"Argus {Feed.FeedId} ({File.Name})"
            : $"Argus {Feed.FeedId} {File.Name} [{Module} {SourceFileDate:yyyy-MM-dd}]";
}

/// <summary>
/// Work units for one DOCUMENTATION reference feed: exactly the one file the
/// descriptor names, if the server is offering it.
///
/// <para>
/// A missing file is NOT an error — it is recorded <c>NotAvailable</c> in
/// <c>dlp.FileLog</c> and yields zero units, so the pipeline reports success with
/// nothing done and the other 14 feeds carry on. Only a failure to LIST the
/// directory at all propagates.
/// </para>
/// </summary>
public sealed class ArgusDocumentationWorkUnitProvider : IWorkUnitProvider<ArgusWorkUnit>
{
    private readonly ArgusFeedDescriptor _feed;
    private readonly ArgusListingCache _listings;
    private readonly IArgusFileLog _fileLog;
    private readonly ArgusSettings _settings;
    private readonly ILogger _logger;

    public ArgusDocumentationWorkUnitProvider(
        ArgusFeedDescriptor feed,
        ArgusListingCache listings,
        IArgusFileLog fileLog,
        ArgusSettings settings,
        ILogger logger)
    {
        _feed = feed;
        _listings = listings;
        _fileLog = fileLog;
        _settings = settings;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ArgusWorkUnit>> GetWorkUnitsAsync(LoaderRunContext context)
    {
        var ct = context.CancellationToken;
        var files = await _listings.ListAsync(_settings.DocumentationDirectory, ct).ConfigureAwait(false);

        var match = files.FirstOrDefault(f =>
            string.Equals(f.Name, _feed.FileName, StringComparison.OrdinalIgnoreCase));

        if (match is null)
        {
            _logger.LogWarning("Argus: {Feed} — {File} is not in {Dir}; recorded NotAvailable and skipped",
                _feed.FeedId, _feed.FileName, _settings.DocumentationDirectory);

            await RecordNotAvailableAsync(ct).ConfigureAwait(false);
            return Array.Empty<ArgusWorkUnit>();
        }

        return new[]
        {
            new ArgusWorkUnit
            {
                Feed = _feed,
                File = match,
                KeySalt = KeySaltFor(_settings, context)
            }
        };
    }

    private async Task RecordNotAvailableAsync(CancellationToken ct)
    {
        // Best-effort: a hub write failure must not turn a benign missing file into
        // a run failure.
        try
        {
            var path = $"ftp://{_settings.FtpHost}:{_settings.FtpPort}" +
                       ArgusFtpFileSystem.CombinePath(
                           ArgusFtpFileSystem.NormalizeDirectory(_settings.DocumentationDirectory),
                           _feed.FileName!);

            var context = new ArgusFileContext(
                _feed.FileName!, _feed.FeedId, _feed.RemoteFolder, null, null, null, path);

            await _fileLog.UpsertAsync(context, "NotAvailable", 0, "File not present in listing", ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Argus: could not record NotAvailable FileLog row for {File}", _feed.FileName);
        }
    }

    internal static string KeySaltFor(ArgusSettings settings, LoaderRunContext context) =>
        settings.ForceReprocess ? $":force={DateTime.UtcNow:yyyyMMdd}:{context.RunId}" : string.Empty;
}

/// <summary>
/// Work units for the DCRDEUS fact feed: every file whose name matches
/// <see cref="ArgusSettings.FileNameDatePattern"/>.
///
/// <para>
/// That regex is the entire exclusion mechanism (design §3.2): it rejects
/// <c>latestdhc.csv</c> / <c>previousdhc.csv</c> — byte-identical aliases of dated
/// files that would otherwise merge the same rows twice — and <c>7667.csv</c>,
/// which has no date and therefore no ordering guard. A non-matching name is
/// counted and logged at debug rather than warned about, because those four files
/// are present on EVERY run and warning about them would be noise.
/// </para>
/// <para>
/// Units come back in ascending file-date order. That is best-effort only:
/// <c>ParallelRunner</c> may still complete them out of order, so correctness
/// rests on the merge proc's <c>SourceFileDate</c> guard, never on this ordering.
/// </para>
/// </summary>
public sealed class ArgusTimeSeriesWorkUnitProvider : IWorkUnitProvider<ArgusWorkUnit>
{
    private readonly ArgusListingCache _listings;
    private readonly ArgusSettings _settings;
    private readonly ILogger _logger;
    private readonly Regex _fromName;

    public ArgusTimeSeriesWorkUnitProvider(
        ArgusListingCache listings, ArgusSettings settings, ILogger logger)
    {
        _listings = listings;
        _settings = settings;
        _logger = logger;
        _fromName = new Regex(_settings.FileNameDatePattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    /// <summary>
    /// Newest file date from the most recent enumeration, or null when it produced
    /// nothing. Lets the module scope its post-load validation to the day it just
    /// loaded without listing the drop a second time.
    /// </summary>
    public DateOnly? LastEnumeratedMaxDate { get; private set; }

    public async Task<IReadOnlyList<ArgusWorkUnit>> GetWorkUnitsAsync(LoaderRunContext context)
    {
        var ct = context.CancellationToken;
        var files = await _listings.ListAsync(_settings.TimeSeriesDirectory, ct).ConfigureAwait(false);

        DateOnly? cutoff = _settings.DaysBack > 0
            ? DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(-_settings.DaysBack))
            : null;

        var units = new List<ArgusWorkUnit>();
        var excluded = 0;
        var filtered = 0;
        var salt = ArgusDocumentationWorkUnitProvider.KeySaltFor(_settings, context);

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            var parsed = TryParseName(file.Name);
            if (parsed is null)
            {
                excluded++;
                _logger.LogDebug("Argus: {File} does not match {Pattern} — excluded", file.Name, _settings.FileNameDatePattern);
                continue;
            }

            var (date, suffix) = parsed.Value;

            if (cutoff is not null && date < cutoff.Value)
            {
                filtered++;
                continue;
            }

            units.Add(new ArgusWorkUnit
            {
                Feed = ArgusDescriptors.TimeSeries,
                File = file,
                SourceFileDate = date,
                Module = ResolveModule(suffix),
                KeySalt = salt
            });
        }

        units.Sort((a, b) => Nullable.Compare(a.SourceFileDate, b.SourceFileDate));
        LastEnumeratedMaxDate = units.Count == 0 ? null : units[^1].SourceFileDate;

        _logger.LogInformation(
            "Argus TimeSeries: {Count} work unit(s) from {Total} listed file(s) ({Excluded} name-excluded, {Filtered} outside DaysBack={DaysBack})",
            units.Count, files.Count, excluded, filtered, _settings.DaysBack);

        return units;
    }

    /// <summary>Extract (publication date, module suffix); null when the name does not match.</summary>
    internal (DateOnly Date, string Suffix)? TryParseName(string fileName)
    {
        var match = _fromName.Match(fileName);
        if (!match.Success || match.Groups.Count < 3) return null;

        return DateOnly.TryParseExact(
            match.Groups[1].Value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? (parsed, match.Groups[2].Value)
            : null;
    }

    /// <summary>
    /// Suffix to <c>Module</c>: the uppercased file-name suffix, always
    /// (<c>dhc</c> -> <c>DHC</c>, <c>dhca</c> -> <c>DHCA</c>). This is the stored
    /// value — <c>Module</c> is never written in any other case.
    ///
    /// <para>
    /// <b>Known limitation, accepted deliberately.</b> The strict authority for a
    /// module name is <c>latestModules.FileName</c>, and across the 200 modules it
    /// differs from <c>LOWER(Module)</c> in 124 cases (<c>DAMCOAL</c>/<c>dcm</c>,
    /// <c>DADR</c>/<c>dusem</c>, <c>DAPI10</c>/<c>dcm2</c>). Uppercasing is exact
    /// for the two suffixes DCRDEUS actually publishes; for a NEW suffix it would
    /// produce a module name that does not exist in <c>dlp.ModuleLookup</c>.
    /// <c>dlp.usp_ValidateLoad</c>'s <c>FactModulesNotInModuleLookup</c> check is
    /// the safety net for exactly that case — it reports any Module in the fact
    /// table with no matching lookup row.
    /// </para>
    /// </summary>
    internal static string ResolveModule(string suffix) => suffix.ToUpperInvariant();
}
