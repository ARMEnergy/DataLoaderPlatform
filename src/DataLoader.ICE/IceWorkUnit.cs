using DataLoader.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace DataLoader.ICE;

/// <summary>
/// One work unit = one FEED × one TRADE DATE = one file = one merge.
///
/// <para>
/// <b>Resume key</b> (the idempotency contract — <c>core.LoadLog</c> skips a key
/// already recorded successful):
/// </para>
/// <code>
///   settled:                 ice:{FeedId}:{yyyy-MM-dd}
///   hot, clock strategies:   ice:{FeedId}:{yyyy-MM-dd}:run={token}
///   hot, LastModified:       ice:{FeedId}:{yyyy-MM-dd}:lm={yyyyMMddHHmmss}     file published, this version
///                            ice:{FeedId}:{yyyy-MM-dd}:lm=none                 HEAD saw no file
///                            ice:{FeedId}:{yyyy-MM-dd}:lm=unknown:run={RunId}  HEAD failed — always download
/// </code>
/// <para>
/// A trade date older than <see cref="IceSettings.SettledAfterDays"/> gets the
/// STABLE key — loaded once, then a cheap skip forever. A newer one gets a varying
/// HOT key so ICE's revised settlements are picked up.
/// </para>
/// <para>
/// With the shipped 30/30 defaults every date in the window is hot; see
/// <see cref="IceSettings.SettledAfterDays"/> for why that is deliberate.
/// </para>
/// </summary>
public sealed class IceWorkUnit : WorkUnit
{
    public required IceFeedDescriptor Feed { get; init; }
    public required DateOnly TradeDate { get; init; }

    /// <summary>Empty for a settled date; <c>:run=…</c> or <c>:lm=…</c> for a hot one.</summary>
    public string KeySuffix { get; init; } = string.Empty;

    /// <summary>
    /// <see cref="IceHotKeyStrategy.LastModified"/> only: the file version the
    /// <c>HEAD</c> reported, which a cached copy must match to be reused. Null means
    /// "no expectation" — any non-empty cached copy is acceptable, as under the clock
    /// strategies.
    /// </summary>
    public string? ExpectedVersion { get; init; }

    /// <summary>
    /// <see cref="IceHotKeyStrategy.LastModified"/> only: the <c>HEAD</c> failed, so the
    /// file's current version is unknown and a cached copy must NOT be trusted — the
    /// unit always downloads.
    /// </summary>
    public bool BypassCache { get; init; }

    /// <summary>True when this date is inside the hot zone. Logged, and asserted in tests.</summary>
    public bool IsHot => KeySuffix.Length > 0;

    public override string Key => $"ice:{Feed.FeedId}:{IceTime.Iso(TradeDate)}{KeySuffix}";

    public override string DisplayName => $"ICE {Feed.FeedId} {IceTime.Iso(TradeDate)}";
}

/// <summary>
/// Produces one work unit per trade date in the <see cref="IceSettings.DaysBack"/>
/// window for a single feed.
///
/// <para>
/// The window is <c>[today - DaysBack, today]</c> inclusive, in US Central
/// (<see cref="IceTime"/>). Weekends and holidays are NOT filtered out: ICE does not
/// publish a trading calendar with these files, and a missing day is already a
/// first-class non-error outcome (<c>NotAvailable</c>) that costs one small HTTP
/// response. Guessing at a calendar would risk skipping a day ICE did publish —
/// the failure mode that actually loses data.
/// </para>
/// <para>
/// Under <see cref="IceHotKeyStrategy.LastModified"/> a hot date is only enumerated
/// when it falls in this run's version-check window (see
/// <see cref="VersionCheckDays"/>); each such date costs one <c>HEAD</c>.
/// </para>
/// </summary>
public sealed class IceWorkUnitProvider : IWorkUnitProvider<IceWorkUnit>
{
    private readonly IceFeedDescriptor _feed;
    private readonly IceSettings _settings;
    private readonly IIceVersionProbe? _probe;
    private readonly ILogger _logger;

    public IceWorkUnitProvider(IceFeedDescriptor feed, IceSettings settings, ILogger logger)
        : this(feed, settings, probe: null, logger)
    {
    }

    public IceWorkUnitProvider(IceFeedDescriptor feed, IceSettings settings, IIceVersionProbe? probe, ILogger logger)
    {
        _feed = feed;
        _settings = settings;
        _probe = probe;
        _logger = logger;
    }

    public async Task<IReadOnlyList<IceWorkUnit>> GetWorkUnitsAsync(LoaderRunContext context)
    {
        var today = IceTime.Today(context.StartedAtUtc);

        // ⚠ CLAMPED. A negative DaysBack would invert the window and produce zero
        // units — a loader that silently does nothing. A negative SettledAfterDays
        // would make `age > SettledAfterDays` true for EVERY date, settling the whole
        // window on stable keys so revisions are never re-pulled again.
        var daysBack = Math.Max(0, _settings.DaysBack);
        var settledAfterDays = Math.Max(0, _settings.SettledAfterDays);

        var byVersion = _settings.HotKeyStrategy == IceHotKeyStrategy.LastModified;
        if (byVersion && _probe is null)
            throw new InvalidOperationException(
                "ICE HotKeyStrategy=LastModified needs a version probe; the provider was built without one.");

        var checkDays = byVersion ? VersionCheckDays(_settings, context.StartedAtUtc) : daysBack;
        var start = today.AddDays(-daysBack);
        var hotToken = byVersion ? null : HotToken(context);

        var units = new List<IceWorkUnit>();
        var hot = 0;
        var notChecked = 0;

        // Newest first: the day everyone actually cares about lands before the
        // backfill tail if the run is cut short — and, under LastModified, its HEAD is
        // spent first too. Correctness never depends on this — ParallelRunner may
        // complete units in any order, and the merges are order-independent (see
        // sql/ICE/003).
        for (var date = today; date >= start; date = date.AddDays(-1))
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            var ageDays = today.DayNumber - date.DayNumber;
            var isHot = ageDays <= settledAfterDays;

            if (!isHot)
            {
                units.Add(new IceWorkUnit { Feed = _feed, TradeDate = date });
                continue;
            }

            if (!byVersion)
            {
                hot++;
                units.Add(new IceWorkUnit { Feed = _feed, TradeDate = date, KeySuffix = $":run={hotToken}" });
                continue;
            }

            // Hot, by version, but outside this run's check window: NOT enumerated.
            // Emitting it would need a key, and the only honest key needs a HEAD. The
            // next full check (FullVersionCheckHoursCentral) covers it.
            if (ageDays > checkDays)
            {
                notChecked++;
                continue;
            }

            hot++;
            var version = await _probe!.ProbeAsync(_feed, date, context.CancellationToken).ConfigureAwait(false);
            units.Add(UnitFor(date, version, context.RunId));
        }

        if (byVersion)
            _logger.LogInformation(
                "ICE {Feed}: {Count} work unit(s) for {Start}..{End} ({Hot} hot checked by version over {CheckDays} day(s), " +
                "{NotChecked} hot not checked this run, {Settled} settled)",
                _feed.FeedId, units.Count, IceTime.Iso(start), IceTime.Iso(today), hot, checkDays, notChecked,
                units.Count - hot);
        else
            _logger.LogInformation(
                "ICE {Feed}: {Count} work unit(s) for {Start}..{End} ({Hot} hot, {Settled} settled)",
                _feed.FeedId, units.Count, IceTime.Iso(start), IceTime.Iso(today), hot, units.Count - hot);

        return units;
    }

    /// <summary>The unit for one hot date, keyed on what the HEAD reported.</summary>
    internal IceWorkUnit UnitFor(DateOnly date, IceFileVersion version, Guid runId) => version.Kind switch
    {
        // This exact version is either already loaded (skip — no download, no merge)
        // or new/changed (load it, and refuse a cached copy of any other version).
        IceFileVersionKind.Published => new IceWorkUnit
        {
            Feed = _feed, TradeDate = date,
            KeySuffix = $":lm={version.Token}",
            ExpectedVersion = version.Token
        },

        // Not published. STABLE on purpose: the first time, the GET confirms it through
        // the content classifier (NotAvailable → 0 rows, success); after that the HEAD
        // alone is the per-run check, and the day a version appears the key changes.
        // Without this, every weekend date would be re-downloaded every run.
        IceFileVersionKind.Absent => new IceWorkUnit
        {
            Feed = _feed, TradeDate = date,
            KeySuffix = ":lm=none"
        },

        // The HEAD could not tell us. Run-varying AND cache-bypassing, so this run
        // always downloads: a HEAD problem must never be able to hide a file behind a
        // completed key, nor merge a cached copy ICE has since replaced.
        _ => new IceWorkUnit
        {
            Feed = _feed, TradeDate = date,
            KeySuffix = $":lm=unknown:run={runId:N}",
            BypassCache = true
        }
    };

    /// <summary>
    /// How many days back this run checks by version: the whole
    /// <see cref="IceSettings.DaysBack"/> window when it starts in one of
    /// <see cref="IceSettings.FullVersionCheckHoursCentral"/>, otherwise
    /// <see cref="IceSettings.VersionCheckDaysBack"/>, both clamped to <c>[0, DaysBack]</c>.
    /// </summary>
    internal static int VersionCheckDays(IceSettings settings, DateTime startedAtUtc)
    {
        var daysBack = Math.Max(0, settings.DaysBack);
        return IsFullVersionCheck(settings, startedAtUtc)
            ? daysBack
            : Math.Clamp(settings.VersionCheckDaysBack, 0, daysBack);
    }

    /// <summary>True when a run starting at <paramref name="startedAtUtc"/> checks the whole window.</summary>
    internal static bool IsFullVersionCheck(IceSettings settings, DateTime startedAtUtc) =>
        settings.FullVersionCheckHoursCentral is { Count: > 0 } hours &&
        hours.Contains(IceTime.CentralHour(startedAtUtc));

    /// <summary>
    /// The run-varying component of a hot key under the clock strategies. UTC in every
    /// case — see <see cref="IceTime"/> for why a local-zone hour token would break
    /// monotonicity. Not used by <see cref="IceHotKeyStrategy.LastModified"/>.
    /// </summary>
    internal string HotToken(LoaderRunContext context) => _settings.HotKeyStrategy switch
    {
        IceHotKeyStrategy.RunHour => context.StartedAtUtc.ToString("yyyyMMddHH", IceTime.Inv),
        IceHotKeyStrategy.RunId => context.RunId.ToString("N"),
        _ => context.StartedAtUtc.ToString("yyyyMMdd", IceTime.Inv)
    };
}
