using DataLoader.Core.Abstractions;

namespace DataLoader.Marex;

/// <summary>
/// One feed's slice of one run's snapshot.
///
/// <para>Every Marex feed produces exactly ONE work unit per run. That is not a
/// simplification — it is the shape of the source. The gateway pushes each entity as a
/// single complete set on connect; there is no window to carve, no page to walk and no
/// id to iterate, so there is nothing a second unit could ask for.</para>
/// </summary>
internal sealed class MarexSnapshotWorkUnit : WorkUnit
{
    internal MarexSnapshotWorkUnit(string feedId, string resumeToken)
    {
        FeedId = feedId;
        ResumeToken = resumeToken;
    }

    internal string FeedId { get; }

    /// <summary>The run-varying half of <see cref="Key"/>. See <see cref="MarexResumeKeyStrategy"/>.</summary>
    internal string ResumeToken { get; }

    /// <summary>
    /// <c>feed=ClosingPrice;at=2026092116</c>.
    ///
    /// <para>There is no settled/hot split here, because no Marex work unit is ever
    /// settled. The gateway serves the CURRENT state of the market — it has no history
    /// endpoint for these five entities — so every run necessarily re-reads the same
    /// live set, and the resume key exists only to stop a re-run within the same period
    /// (hour, by default) from repeating the work. Giving any unit a stable key would
    /// load it once and then skip it forever, which for a live snapshot means the table
    /// silently stops tracking the market.</para>
    /// </summary>
    public override string Key => $"feed={FeedId};at={ResumeToken}";

    public override string DisplayName => $"Marex {FeedId} snapshot @ {ResumeToken}";
}

/// <summary>
/// Yields the single work unit for one feed, stamping it with the run-varying resume
/// token.
/// </summary>
internal sealed class MarexSnapshotWorkUnitProvider : IWorkUnitProvider<MarexSnapshotWorkUnit>
{
    private readonly string _feedId;
    private readonly MarexSettings _settings;

    internal MarexSnapshotWorkUnitProvider(string feedId, MarexSettings settings)
    {
        _feedId = feedId;
        _settings = settings;
    }

    public Task<IReadOnlyList<MarexSnapshotWorkUnit>> GetWorkUnitsAsync(LoaderRunContext context)
    {
        var token = ResumeToken(_settings.ResumeKeyStrategy, context);
        IReadOnlyList<MarexSnapshotWorkUnit> units = new[] { new MarexSnapshotWorkUnit(_feedId, token) };
        return Task.FromResult(units);
    }

    /// <summary>
    /// The run-varying token. UTC throughout — a local-time hour token goes BACKWARDS on
    /// a fall-back night, which would let an already-recorded success suppress a
    /// legitimate reload for an hour.
    /// </summary>
    internal static string ResumeToken(MarexResumeKeyStrategy strategy, LoaderRunContext context) =>
        strategy switch
        {
            MarexResumeKeyStrategy.RunDate => context.StartedAtUtc.ToUniversalTime().ToString("yyyyMMdd"),
            MarexResumeKeyStrategy.RunId => context.RunId.ToString("N"),
            _ => context.StartedAtUtc.ToUniversalTime().ToString("yyyyMMddHH")
        };
}
