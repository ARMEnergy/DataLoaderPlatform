using DataLoader.Core.Abstractions;
using DataLoader.Core.Sources;
using Microsoft.Extensions.Logging;

namespace DataLoader.EOX;

/// <summary>
/// One work unit = one FEED × one CURVE DATE = one file = one merge.
///
/// <para>
/// <b>Resume key</b> (the idempotency contract — <c>core.LoadLog</c> skips a key
/// already recorded successful):
/// </para>
/// <code>
///   settled:  eox:{FeedId}:{yyyy-MM-dd}:{lastModifiedUtc}:{size}
///   hot:      eox:{FeedId}:{yyyy-MM-dd}:{lastModifiedUtc}:{size}:run={token}
/// </code>
/// <para>
/// Both halves are load-bearing and they cover different failure modes:
/// </para>
/// <list type="bullet">
///   <item>
///     The <b>content stamp</b> (the server's last-modified time and byte size)
///     means a file EOX republishes gets a new key at ANY age and is reloaded,
///     while an unchanged file is skipped however often the loader is scheduled.
///     This is the OPIS/Argus posture and it is what makes a settled date cheap.
///   </item>
///   <item>
///     The <b>hot suffix</b> forces a re-pull inside the
///     <see cref="EoxSettings.SettledAfterDays"/> window even when the stamp has
///     not moved — insurance against a republish that somehow preserves both the
///     mtime and the size. This is the ICE/EvolutionMarkets/NGI posture and it is
///     what <c>SettledAfterDays</c> controls.
///   </item>
/// </list>
/// <para>
/// With the shipped 30/30 defaults every date in the window is hot; see
/// <see cref="EoxSettings.SettledAfterDays"/> for why that is deliberate and what
/// it costs.
/// </para>
/// </summary>
public sealed class EoxWorkUnit : WorkUnit
{
    public required EoxFeedDescriptor Feed { get; init; }
    public required DateOnly CurveDate { get; init; }
    public required RemoteFile File { get; init; }

    /// <summary>Empty for a settled date; <c>:run=…</c> for a hot one. Also carries the force-reprocess salt.</summary>
    public string KeySuffix { get; init; } = string.Empty;

    /// <summary>True when this date is inside the hot zone. Logged, and asserted in tests.</summary>
    public bool IsHot { get; init; }

    public override string Key =>
        $"eox:{Feed.FeedId}:{EoxTime.Iso(CurveDate)}:{File.LastModifiedUtc:yyyyMMddHHmmss}:{File.Size}{KeySuffix}";

    public override string DisplayName => $"EOX {Feed.FeedId} {EoxTime.Iso(CurveDate)} ({File.Name})";
}

/// <summary>
/// Produces one work unit per curve date in the <see cref="EoxSettings.DaysBack"/>
/// window for a single feed, for those dates the drop actually has a file for.
///
/// <para>
/// The window is <c>[today - DaysBack, today]</c> inclusive, in US Central
/// (<see cref="EoxTime"/>). Weekends and holidays are NOT filtered out by a
/// calendar: EOX publishes no trading calendar with these files, and guessing at
/// one risks skipping a day EOX did publish, which is the failure mode that
/// actually loses data. Instead a date with no file is recorded
/// <c>NotAvailable</c> in <c>arm.FileLog</c> and produces no unit — a first-class
/// non-error outcome that costs one dictionary lookup, because the whole
/// directory was listed once for the run.
/// </para>
/// <para>
/// Because the file name is CONSTRUCTED from the date rather than matched with a
/// glob, the ~20 <c>"…(HOST's conflicted copy DATE).csv"</c> files that share the
/// drop can never be picked up, and neither can the <c>.xls</c>/<c>.xlsx</c> twins
/// or the <c>EOD_CSV_20YR_NG_*</c> series.
/// </para>
/// </summary>
public sealed class EoxWorkUnitProvider : IWorkUnitProvider<EoxWorkUnit>
{
    private readonly EoxFeedDescriptor _feed;
    private readonly EoxListingCache _listings;
    private readonly IEoxFileLog _fileLog;
    private readonly EoxSettings _settings;
    private readonly ILogger _logger;

    public EoxWorkUnitProvider(
        EoxFeedDescriptor feed,
        EoxListingCache listings,
        IEoxFileLog fileLog,
        EoxSettings settings,
        ILogger logger)
    {
        _feed = feed;
        _listings = listings;
        _fileLog = fileLog;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>
    /// Newest curve date this feed produced a unit for on the most recent call, or
    /// null when it produced none. Lets the module scope its post-load validation
    /// to the day it just loaded without listing the drop a second time.
    /// </summary>
    public DateOnly? LastEnumeratedMaxDate { get; private set; }

    public async Task<IReadOnlyList<EoxWorkUnit>> GetWorkUnitsAsync(LoaderRunContext context)
    {
        var ct = context.CancellationToken;
        var listing = await _listings.ListAsync(_settings.RemoteDirectory, ct).ConfigureAwait(false);

        var today = EoxTime.Today(context.StartedAtUtc);

        // ⚠ CLAMPED. A negative DaysBack would invert the window and produce zero
        // units — a loader that silently does nothing. A negative SettledAfterDays
        // would make every date settled, so a republished-but-same-size file inside
        // the hot window would stop being re-pulled.
        var daysBack = Math.Max(0, _settings.DaysBack);
        var settledAfterDays = Math.Max(0, _settings.SettledAfterDays);

        var start = today.AddDays(-daysBack);
        var hotToken = HotToken(context);
        var forceSalt = _settings.ForceReprocess ? $":force={context.RunId:N}" : string.Empty;

        var units = new List<EoxWorkUnit>();
        var hot = 0;
        var missing = new List<DateOnly>();

        for (var date = start; date <= today; date = date.AddDays(1))
        {
            ct.ThrowIfCancellationRequested();

            var fileName = _feed.FileNameFor(date, _settings.FileNameTimeToken);
            if (!listing.TryGetValue(fileName, out var file))
            {
                missing.Add(date);
                continue;
            }

            var ageDays = today.DayNumber - date.DayNumber;
            var isHot = ageDays <= settledAfterDays;
            if (isHot) hot++;

            units.Add(new EoxWorkUnit
            {
                Feed = _feed,
                CurveDate = date,
                File = file,
                IsHot = isHot,
                KeySuffix = (isHot ? $":run={hotToken}" : string.Empty) + forceSalt
            });
        }

        // Record the absent dates in the hub so a gap is auditable rather than only
        // visible in a log line. Best-effort: a hub write failure must not turn a
        // benign weekend into a run failure.
        await RecordNotAvailableAsync(missing, ct).ConfigureAwait(false);

        // Newest first: the day everyone actually cares about lands before the
        // backfill tail if the run is cut short. Correctness never depends on this —
        // ParallelRunner may complete units in any order, and the merges resolve
        // overlap by file name (see sql/EOX/003).
        units.Reverse();
        LastEnumeratedMaxDate = units.Count == 0 ? null : units[0].CurveDate;

        _logger.LogInformation(
            "EOX {Feed}: {Count} work unit(s) for {Start}..{End} ({Hot} hot, {Settled} settled, {Missing} date(s) with no file)",
            _feed.FeedId, units.Count, EoxTime.Iso(start), EoxTime.Iso(today), hot, units.Count - hot, missing.Count);

        return units;
    }

    private async Task RecordNotAvailableAsync(IReadOnlyList<DateOnly> dates, CancellationToken ct)
    {
        if (dates.Count == 0) return;

        foreach (var date in dates)
        {
            var fileName = _feed.FileNameFor(date, _settings.FileNameTimeToken);

            try
            {
                var path = $"ftp://{_settings.FtpHost}:{_settings.FtpPort}" +
                           EoxFtpFileSystem.CombinePath(
                               EoxFtpFileSystem.NormalizeDirectory(_settings.RemoteDirectory), fileName);

                var file = new EoxFileContext(fileName, _feed.FeedId, date, null, null, path);

                await _fileLog.UpsertAsync(file, "NotAvailable", 0, "No file for this curve date on the drop", ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "EOX: could not record NotAvailable FileLog row for {File}", fileName);
            }
        }

        _logger.LogDebug("EOX {Feed}: {Count} date(s) had no file — recorded NotAvailable: {Dates}",
            _feed.FeedId, dates.Count, string.Join(", ", dates.Select(EoxTime.Iso)));
    }

    /// <summary>
    /// The run-varying component of a hot key. UTC in every case — see
    /// <see cref="EoxTime"/> for why a local-zone hour token would break
    /// monotonicity across a DST fall-back.
    /// </summary>
    internal string HotToken(LoaderRunContext context) => _settings.HotKeyStrategy switch
    {
        EoxHotKeyStrategy.RunHour => context.StartedAtUtc.ToString("yyyyMMddHH", EoxTime.Inv),
        EoxHotKeyStrategy.RunId => context.RunId.ToString("N"),
        _ => context.StartedAtUtc.ToString("yyyyMMdd", EoxTime.Inv)
    };
}
