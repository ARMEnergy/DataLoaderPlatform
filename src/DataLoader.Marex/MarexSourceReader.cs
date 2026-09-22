using DataLoader.Core.Abstractions;

namespace DataLoader.Marex;

/// <summary>
/// Reads one feed's rows out of the run's shared snapshot.
///
/// <para>"Reading" here is projection, not I/O: the first pipeline to call
/// <see cref="ReadAsync"/> makes <see cref="MarexSnapshotSession"/> connect and capture,
/// and every later call gets the same captured object back. So the gateway is contacted
/// once per run no matter how many feeds are enabled, and the five feeds cannot disagree
/// about the exchange date — which matters, because that date is a primary key component
/// in two of the five tables and a run that straddled midnight would otherwise split its
/// own output across two days.</para>
/// </summary>
internal sealed class MarexSnapshotReader : ISourceReader<MarexSnapshotWorkUnit, MarexRow>
{
    private readonly IMarexSnapshotSource _snapshots;
    private readonly Func<MarexSnapshot, IReadOnlyList<MarexRow>> _project;

    internal MarexSnapshotReader(
        IMarexSnapshotSource snapshots,
        Func<MarexSnapshot, IReadOnlyList<MarexRow>> project)
    {
        _snapshots = snapshots;
        _project = project;
    }

    public async Task<IReadOnlyList<MarexRow>> ReadAsync(
        MarexSnapshotWorkUnit unit, CancellationToken cancellationToken)
    {
        var snapshot = await _snapshots.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return _project(snapshot);
    }

    // ------------------------------------------------------------------ projections
    //
    // One per feed. Each maps the whole captured set; an empty vendor set yields an
    // empty row list, which the pipeline records as a legitimate zero-record load rather
    // than a failure.

    internal static IReadOnlyList<MarexRow> ClosingPrices(MarexSnapshot s) =>
        s.ClosingPrices.Select(x => MarexMapper.Map(x, s.ExchangeDate)).ToArray();

    internal static IReadOnlyList<MarexRow> MarketStatistics(MarexSnapshot s) =>
        s.MarketStatistics.Select(x => MarexMapper.Map(x, s.ExchangeDate)).ToArray();

    internal static IReadOnlyList<MarexRow> Periods(MarexSnapshot s) =>
        s.Periods.Select(MarexMapper.Map).ToArray();

    internal static IReadOnlyList<MarexRow> PeriodGroups(MarexSnapshot s) =>
        s.PeriodGroups.Select(MarexMapper.Map).ToArray();

    internal static IReadOnlyList<MarexRow> Products(MarexSnapshot s) =>
        s.Products.Select(MarexMapper.Map).ToArray();

    /// <summary>The projection for a feed id, used when the module builds the pipelines.</summary>
    internal static Func<MarexSnapshot, IReadOnlyList<MarexRow>> For(string feedId) => feedId switch
    {
        MarexDescriptors.ClosingPriceFeedId => ClosingPrices,
        MarexDescriptors.MarketStatisticFeedId => MarketStatistics,
        MarexDescriptors.PeriodFeedId => Periods,
        MarexDescriptors.PeriodGroupFeedId => PeriodGroups,
        MarexDescriptors.ProductFeedId => Products,
        _ => throw new ArgumentOutOfRangeException(nameof(feedId), feedId, "Unknown Marex feed id")
    };
}
