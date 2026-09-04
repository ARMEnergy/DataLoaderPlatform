using DataLoader.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace DataLoader.Criterion;

/// <summary>
/// Adapts <see cref="ICriterionSource"/> to the platform's
/// <see cref="ISourceReader{TUnit,TItem}"/>.
///
/// <para>
/// The reader emits the sink's row type directly, so the pipeline's transformer is
/// the identity — there is no intermediate DTO to keep in sync. All the mapping
/// lives in <see cref="CriterionDescriptors"/>, which is also what the SELECT, the
/// DataTable and the TVP contract test are built from.
/// </para>
/// <para>
/// This class's real job is OBSERVABILITY. Every read reports what it discarded and
/// why — rows dropped for a NULL key, strings truncated to fit, and above all the
/// intraday date collapse — so a shape change in the source shows up as a number in
/// the log rather than as quietly missing rows six months later.
/// </para>
/// </summary>
public sealed class CriterionSourceReader : ISourceReader<CriterionWorkUnit, CriterionRow>
{
    private readonly CriterionFeedDescriptor _feed;
    private readonly ICriterionSource _source;
    private readonly ILogger _logger;

    public CriterionSourceReader(CriterionFeedDescriptor feed, ICriterionSource source, ILogger logger)
    {
        _feed = feed;
        _source = source;
        _logger = logger;
    }

    public async Task<IReadOnlyList<CriterionRow>> ReadAsync(
        CriterionWorkUnit unit, CancellationToken cancellationToken)
    {
        var result = await _source.ReadAsync(unit, cancellationToken).ConfigureAwait(false);

        // A slice with no source rows is a NORMAL outcome, not a failure: a gas day
        // the source has not published yet, a weekend, or a post_date with no
        // publications. It must not raise alarms — but it is worth a line, because a
        // run where EVERY day is empty means something else is wrong.
        if (result.SourceRows == 0)
        {
            _logger.LogDebug("Criterion {Unit}: source returned no rows", unit.DisplayName);
            return result.Rows;
        }

        if (result.DroppedRequired > 0)
            _logger.LogWarning(
                "Criterion {Unit}: dropped {Dropped} of {Total} source row(s) with a NULL key column — " +
                "they cannot be merged under a blank key",
                unit.DisplayName, result.DroppedRequired, result.SourceRows);

        if (result.Truncated > 0)
            _logger.LogWarning(
                "Criterion {Unit}: truncated {Count} string value(s) to fit their target column. " +
                "The source may have widened a column — check docs/apis/Criterion.md 4 against " +
                "src/DataLoader.Criterion/CriterionDescriptors.cs",
                unit.DisplayName, result.Truncated);

        if (result.Unparseable > 0)
            _logger.LogWarning(
                "Criterion {Unit}: {Count} JSON observation(s) had no readable date and were dropped",
                unit.DisplayName, result.Unparseable);

        // The intraday collapse. Logged at Information, not Debug: this is real data
        // loss that the requester accepted, and it should be visible in a normal run's
        // output rather than needing a log-level change to find.
        if (result.Collapsed > 0)
            _logger.LogInformation(
                "Criterion {Unit}: collapsed {Collapsed} intraday observation(s) into {Kept} daily row(s) " +
                "from {Source} series — PK (FinancialJsonId, Date) admits one row per day, so the LAST " +
                "observation of each date wins (see docs/design/Criterion.md 5)",
                unit.DisplayName, result.Collapsed, result.Rows.Count, result.SourceRows);

        _logger.LogDebug(
            "Criterion {Unit}: {Source} source row(s) -> {Rows} target row(s)",
            unit.DisplayName, result.SourceRows, result.Rows.Count);

        return result.Rows;
    }
}
