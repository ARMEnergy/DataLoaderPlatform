using System.Globalization;
using DataLoader.Core.Abstractions;
using DataLoader.Core.Sources;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.CME;

/// <summary>
/// One work unit = one FEED × one TRADE DATE = one bulletin file = one parse,
/// feeding TWO merges (options and futures).
///
/// <para>
/// <b>Resume key</b> (the idempotency contract — <c>core.LoadLog</c> skips a key
/// already recorded successful):
/// </para>
/// <code>
///   settled:  cme:{FeedId}:{yyyy-MM-dd}:{lastModifiedUtc}:{size}
///   hot:      cme:{FeedId}:{yyyy-MM-dd}:{lastModifiedUtc}:{size}:run={token}
/// </code>
/// <para>
/// Both halves are load-bearing and they cover different failure modes:
/// </para>
/// <list type="bullet">
///   <item>
///     The <b>content stamp</b> (the server's last-modified time and byte size)
///     means a bulletin CME republishes gets a new key at ANY age and is reloaded,
///     while an unchanged file is skipped however often the loader is scheduled.
///     This matters here because clearing genuinely does revise a settlement file
///     in place — the header even distinguishes <c>FINAL PRE-CLEARING</c> from
///     <c>FINAL POST-CLEARING</c> — and the revision keeps the same name.
///   </item>
///   <item>
///     The <b>hot suffix</b> forces a re-pull inside the
///     <see cref="CmeSettings.SettledAfterDays"/> window even when the stamp has
///     not moved — insurance against a republish that somehow preserves both the
///     mtime and the size.
///   </item>
/// </list>
/// </summary>
public sealed class CmeWorkUnit : WorkUnit
{
    public required CmeFeed Feed { get; init; }
    public required DateOnly TradeDate { get; init; }
    public required RemoteFile File { get; init; }

    /// <summary>Empty for a settled date; <c>:run=…</c> for a hot one. Also carries the force-reprocess salt.</summary>
    public string KeySuffix { get; init; } = string.Empty;

    /// <summary>True when this date is inside the hot zone. Logged, and asserted in tests.</summary>
    public bool IsHot { get; init; }

    public override string Key =>
        $"cme:{Feed.FeedId}:{CmeTime.Iso(TradeDate)}:{File.LastModifiedUtc:yyyyMMddHHmmss}:{File.Size}{KeySuffix}";

    public override string DisplayName => $"CME {Feed.FeedId} {CmeTime.Iso(TradeDate)} ({File.Name})";
}

/// <summary>
/// Walks the drop once per run and caches the result, so all feeds' providers
/// share ONE discovery.
///
/// <para>
/// The walk is five directory levels
/// (<c>product / feed / yyyy / MM / dd</c>) and costs ~100 listings; doing it per
/// feed would multiply that by eight for no new information.
/// </para>
/// </summary>
public sealed class CmeDiscoveryCache
{
    /// <summary>
    /// Directory levels below the root: product → feed → year → month → day.
    /// Files live at the bottom.
    /// </summary>
    public const int TreeDepth = 5;

    private readonly ICmeSftp _sftp;
    private readonly CmeSettings _settings;
    private readonly ILogger<CmeDiscoveryCache> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IReadOnlyList<CmeDiscoveredFile>? _files;

    public CmeDiscoveryCache(ICmeSftp sftp, IOptions<CmeSettings> settings, ILogger<CmeDiscoveryCache> logger)
    {
        _sftp = sftp;
        _settings = settings.Value;
        _logger = logger;
    }

    /// <summary>Every bulletin on the drop, parsed into (feed, trade date, file). Walked at most once per run.</summary>
    public async Task<IReadOnlyList<CmeDiscoveredFile>> GetAsync(CancellationToken ct)
    {
        if (_files is not null) return _files;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_files is not null) return _files;

            var raw = await _sftp.WalkAsync(_settings.RootDirectory, TreeDepth, ct).ConfigureAwait(false);
            _files = Interpret(raw);
            return _files;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Turn raw walk results into feed + trade date, dropping anything that does not
    /// match the documented layout.
    ///
    /// <para>
    /// The trade date is taken from the FILE NAME and then CROSS-CHECKED against the
    /// <c>yyyy/MM/dd</c> folders that contain it. Both encode the same date, so
    /// requiring them to agree turns a mis-filed bulletin into a logged skip
    /// instead of a row stamped with the wrong trade date — which no downstream
    /// check would catch, because every value in the row would be internally
    /// consistent.
    /// </para>
    /// </summary>
    private IReadOnlyList<CmeDiscoveredFile> Interpret(IReadOnlyList<RemoteFile> files)
    {
        var result = new List<CmeDiscoveredFile>(files.Count);
        var skippedShape = 0;
        var skippedDateMismatch = 0;
        var skippedExtension = 0;

        foreach (var file in files)
        {
            var segments = file.FullPath.Split('/', StringSplitOptions.RemoveEmptyEntries);

            // product / feed / yyyy / MM / dd / name  — relative to the root, which
            // may itself be "." (dropped by RemoveEmptyEntries).
            if (segments.Length < 6)
            {
                skippedShape++;
                continue;
            }

            var name = segments[^1];
            var day = segments[^2];
            var month = segments[^3];
            var year = segments[^4];
            var feedDirectory = segments[^5];
            var productDirectory = string.Join('/', segments[..^5]);

            if (!name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            {
                skippedExtension++;
                continue;
            }

            // Feed folder is <ProductCode>_<ExchangeCode>, split on the FIRST
            // underscore per the requester's rule.
            var underscore = feedDirectory.IndexOf('_');
            if (underscore <= 0 || underscore == feedDirectory.Length - 1)
            {
                skippedShape++;
                continue;
            }

            var feed = new CmeFeed(
                ProductCode: feedDirectory[..underscore],
                ExchangeCode: feedDirectory[(underscore + 1)..],
                ProductDirectory: productDirectory,
                FeedDirectory: feedDirectory);

            if (!TryParseFolderDate(year, month, day, out var folderDate))
            {
                skippedShape++;
                continue;
            }

            if (!TryParseFileDate(name, out var fileDate))
            {
                skippedShape++;
                continue;
            }

            if (fileDate != folderDate)
            {
                _logger.LogWarning(
                    "CME: {Path} is filed under {Folder} but its name says {Name} — skipped rather than " +
                    "loaded under a wrong trade date",
                    file.FullPath, CmeTime.Iso(folderDate), CmeTime.Iso(fileDate));

                skippedDateMismatch++;
                continue;
            }

            result.Add(new CmeDiscoveredFile(feed, fileDate, file));
        }

        if (skippedShape > 0 || skippedDateMismatch > 0 || skippedExtension > 0)
            _logger.LogInformation(
                "CME discovery: {Kept} bulletin(s) kept; skipped {Shape} off-layout, {Mismatch} date-mismatched, " +
                "{Ext} non-.txt path(s)",
                result.Count, skippedShape, skippedDateMismatch, skippedExtension);

        return result;
    }

    private static bool TryParseFolderDate(string year, string month, string day, out DateOnly date)
    {
        date = default;

        if (year.Length != 4 || month.Length != 2 || day.Length != 2) return false;

        return DateOnly.TryParseExact(
            $"{year}{month}{day}", "yyyyMMdd", CmeTime.Inv, DateTimeStyles.None, out date);
    }

    /// <summary>Parse the <c>yyyyMMdd</c> token out of <c>STLAGS_20260904.txt</c>.</summary>
    internal static bool TryParseFileDate(string fileName, out DateOnly date)
    {
        date = default;

        var stem = fileName.AsSpan()[..^4]; // strip ".txt"
        var underscore = stem.LastIndexOf('_');
        if (underscore < 0) return false;

        var token = stem[(underscore + 1)..];
        if (token.Length != 8) return false;

        return DateOnly.TryParseExact(token, "yyyyMMdd", CmeTime.Inv, DateTimeStyles.None, out date);
    }
}

/// <summary>One bulletin found on the drop, with its feed and trade date resolved.</summary>
public sealed record CmeDiscoveredFile(CmeFeed Feed, DateOnly TradeDate, RemoteFile File);

/// <summary>
/// Produces one work unit per (feed × trade date) the drop actually holds, for the
/// feeds this pass has enabled.
///
/// <para>
/// <b>Enumerate-what-exists, not enumerate-a-window.</b> Unlike the date-window
/// loaders in this repo, CME's drop is a short rolling window (10 business days
/// per feed when this loader was built) and the requester asked to "always load
/// all the available files". So the provider takes the discovery listing as the
/// authority and does not construct candidate dates. That also means there is no
/// such thing as a "missing" date here: a weekend simply has no folder, which is
/// an absence of work rather than a gap to record.
/// </para>
/// <para>
/// One provider instance serves ALL feeds, because a single discovery already
/// carries every feed's files. Units are ordered newest-date-first so the day
/// everyone cares about lands before the backfill tail if a run is cut short;
/// correctness never depends on the order, because every unit writes a disjoint
/// key set (TradeDate and ExchangeCode both sit in both primary keys).
/// </para>
/// </summary>
public sealed class CmeWorkUnitProvider : IWorkUnitProvider<CmeWorkUnit>
{
    private readonly CmeDiscoveryCache _discovery;
    private readonly CmeSettings _settings;
    private readonly ILogger _logger;

    public CmeWorkUnitProvider(CmeDiscoveryCache discovery, CmeSettings settings, ILogger logger)
    {
        _discovery = discovery;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>Newest trade date this run enumerated, or null when it produced no units.</summary>
    public DateOnly? LastEnumeratedMaxDate { get; private set; }

    /// <summary>Feed ids this run enumerated at least one file for. Drives the zero-file warning.</summary>
    public IReadOnlyCollection<string> LastEnumeratedFeeds { get; private set; } = Array.Empty<string>();

    public async Task<IReadOnlyList<CmeWorkUnit>> GetWorkUnitsAsync(LoaderRunContext context)
    {
        var ct = context.CancellationToken;
        var discovered = await _discovery.GetAsync(ct).ConfigureAwait(false);

        var today = CmeTime.Today(context.StartedAtUtc);

        // ⚠ CLAMPED. A negative SettledAfterDays would make every date settled, so a
        // republished-but-same-size bulletin inside the hot window would stop being
        // re-pulled.
        var settledAfterDays = Math.Max(0, _settings.SettledAfterDays);

        var minDate = ParseMinTradeDate();
        var hotToken = HotToken(context);
        var forceSalt = _settings.ForceReprocess ? $":force={context.RunId:N}" : string.Empty;

        var enabled = _settings.EnabledFeeds is { Length: > 0 }
            ? new HashSet<string>(_settings.EnabledFeeds, StringComparer.OrdinalIgnoreCase)
            : null;

        var units = new List<CmeWorkUnit>();
        var feeds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hot = 0;
        var filteredFeed = 0;
        var filteredDate = 0;
        var future = 0;

        foreach (var item in discovered)
        {
            ct.ThrowIfCancellationRequested();

            if (enabled is not null && !enabled.Contains(item.Feed.FeedId))
            {
                filteredFeed++;
                continue;
            }

            if (minDate.HasValue && item.TradeDate < minDate.Value)
            {
                filteredDate++;
                continue;
            }

            // A bulletin dated after Chicago's today is not a normal state; load it
            // anyway (the data is real and the drop is the authority) but say so,
            // because it usually means this host's clock or time zone is wrong.
            if (item.TradeDate > today) future++;

            var ageDays = today.DayNumber - item.TradeDate.DayNumber;
            var isHot = ageDays <= settledAfterDays;
            if (isHot) hot++;

            feeds.Add(item.Feed.FeedId);

            units.Add(new CmeWorkUnit
            {
                Feed = item.Feed,
                TradeDate = item.TradeDate,
                File = item.File,
                IsHot = isHot,
                KeySuffix = (isHot ? $":run={hotToken}" : string.Empty) + forceSalt
            });
        }

        // Newest first, then feed id, so a truncated run still gets the latest day
        // for every feed before any backfill.
        units = units
            .OrderByDescending(u => u.TradeDate)
            .ThenBy(u => u.Feed.FeedId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        LastEnumeratedMaxDate = units.Count == 0 ? null : units[0].TradeDate;
        LastEnumeratedFeeds = feeds;

        _logger.LogInformation(
            "CME: {Count} work unit(s) across {Feeds} feed(s) ({Hot} hot, {Settled} settled); " +
            "skipped {FeedFiltered} by EnabledFeeds and {DateFiltered} by MinTradeDate",
            units.Count, feeds.Count, hot, units.Count - hot, filteredFeed, filteredDate);

        if (future > 0)
            _logger.LogWarning(
                "CME: {Count} bulletin(s) are dated after {Today} (US Central today) — loaded anyway, but check " +
                "this host's clock and time zone",
                future, CmeTime.Iso(today));

        if (units.Count == 0)
            _logger.LogWarning(
                "CME: the drop yielded no bulletins to load — check the account's entitlements, RootDirectory " +
                "({Root}) and EnabledFeeds",
                _settings.RootDirectory);

        return units;
    }

    private DateOnly? ParseMinTradeDate()
    {
        if (string.IsNullOrWhiteSpace(_settings.MinTradeDate)) return null;

        if (DateOnly.TryParseExact(
                _settings.MinTradeDate.Trim(), "yyyy-MM-dd", CmeTime.Inv, DateTimeStyles.None, out var parsed))
            return parsed;

        // A typo must not silently become "no floor at all" without saying so.
        _logger.LogWarning(
            "CME: MinTradeDate '{Value}' is not yyyy-MM-dd — ignored, so every available date is enumerated",
            _settings.MinTradeDate);

        return null;
    }

    /// <summary>
    /// The run-varying component of a hot key. UTC in every case — see
    /// <see cref="CmeTime"/> for why a local-zone hour token would break
    /// monotonicity across a DST fall-back.
    /// </summary>
    internal string HotToken(LoaderRunContext context) => _settings.HotKeyStrategy switch
    {
        CmeHotKeyStrategy.RunHour => context.StartedAtUtc.ToString("yyyyMMddHH", CmeTime.Inv),
        CmeHotKeyStrategy.RunId => context.RunId.ToString("N"),
        _ => context.StartedAtUtc.ToString("yyyyMMdd", CmeTime.Inv)
    };
}
