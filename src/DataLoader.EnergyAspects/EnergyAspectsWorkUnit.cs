using DataLoader.Core.Abstractions;
using DataLoader.EnergyAspects.Models;

namespace DataLoader.EnergyAspects;

/// <summary>
/// A "load this mapping for this date window" unit of work. Matches what the
/// original orchestrator processed in its innermost loop.
/// </summary>
public sealed class EnergyAspectsWorkUnit : WorkUnit
{
    public required DatasetMapping Mapping { get; init; }
    public required DateTime WindowStart { get; init; }
    public required DateTime WindowEnd { get; init; }

    public override string Key =>
        $"ea:mapping={Mapping.MappingId};from={WindowStart:yyyy-MM-dd};to={WindowEnd:yyyy-MM-dd}";

    public override string DisplayName =>
        $"[{Mapping.MappingId}] {Mapping.Name} {WindowStart:yyyy-MM-dd}–{WindowEnd:yyyy-MM-dd}";
}
