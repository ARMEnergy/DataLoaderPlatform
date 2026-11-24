using DataLoader.Core.Abstractions;

namespace DataLoader.PJM;

/// <summary>
/// Common wrapper: delegates to <see cref="PjmDayReader{TRow}"/> and reports the unit's outcome to
/// <see cref="PjmRunGuard"/> for the consecutive-failure trip (design §6.3). Implements
/// <see cref="ISourceReader{TUnit,TItem}"/> directly — not <c>HttpJsonSourceReaderBase</c> — because
/// status classification, the 401 re-fetch and the guard are loader-specific (design §4).
/// </summary>
public abstract class PjmSourceReaderBase<TRow> : ISourceReader<PjmDayWorkUnit, TRow> where TRow : IPjmRow
{
    private readonly PjmDayReader<TRow> _reader;
    private readonly PjmRunGuard _guard;

    protected PjmSourceReaderBase(PjmDayReader<TRow> reader, PjmRunGuard guard)
    {
        _reader = reader;
        _guard = guard;
    }

    public async Task<IReadOnlyList<TRow>> ReadAsync(PjmDayWorkUnit unit, CancellationToken cancellationToken)
    {
        try
        {
            var rows = await _reader.ReadDayAsync(unit, cancellationToken).ConfigureAwait(false);
            _guard.RecordUnitAccepted();
            return rows;
        }
        catch (PjmRequestException ex)
        {
            // Request-level (400, 404, errors, transient-exhausted, contract): counts toward the trip.
            _guard.RecordUnitFailure(ex.FailureClass);
            throw;
        }
        catch (PjmDataException ex)
        {
            if (ex.FailureClass is { } failureClass)
                // Empty past day / empty snapshot: counts toward the trip — a bad/ignored filter that answers
                // 200/empty for every past day must trip rather than hammer the public pool hourly.
                // Sub-day units (very_short's 6-hour blocks) count once per UTC day (design §14.7).
                _guard.RecordUnitFailure(failureClass, CountOnceScope(unit));
            else
                // Echo/boundary/end-offset/duplicate/paging: unit-specific, PJM accepted the request — reset.
                _guard.RecordUnitAccepted();
            throw;
        }
        // PjmRunHaltedException and cancellation: the guard already knows / not a unit verdict.
    }

    /// <summary>The run-guard count-once scope: the UTC day for a sub-day unit, otherwise none (each unit counts).</summary>
    internal static string? CountOnceScope(PjmDayWorkUnit unit) =>
        !unit.IsSnapshot && unit.Feed.UnitHours < 24
            ? $"{unit.Feed.Name}:{unit.Day.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture)}"
            : null;
}

/// <summary><c>gen_by_fuel</c> source reader.</summary>
public sealed class PjmFuelMixSourceReader : PjmSourceReaderBase<PjmFuelMixRow>
{
    public PjmFuelMixSourceReader(PjmDayReader<PjmFuelMixRow> reader, PjmRunGuard guard) : base(reader, guard) { }
}

/// <summary><c>inst_load</c> source reader.</summary>
public sealed class PjmLoadSourceReader : PjmSourceReaderBase<PjmLoadRow>
{
    public PjmLoadSourceReader(PjmDayReader<PjmLoadRow> reader, PjmRunGuard guard) : base(reader, guard) { }
}

/// <summary>
/// The generic source reader every feed added in design §14 uses (§14.2): the per-feed subclasses
/// above carry no behaviour of their own and are kept only for the original two feeds' seams.
/// </summary>
public sealed class PjmSourceReader<TRow> : PjmSourceReaderBase<TRow> where TRow : IPjmRow
{
    public PjmSourceReader(PjmDayReader<TRow> reader, PjmRunGuard guard) : base(reader, guard) { }
}
