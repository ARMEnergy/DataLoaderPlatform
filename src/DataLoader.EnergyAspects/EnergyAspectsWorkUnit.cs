using DataLoader.Core.Abstractions;
using DataLoader.EnergyAspects.Models;

namespace DataLoader.EnergyAspects;

/// <summary>
/// A "load these dataset ids for this date window" unit of work.
///
/// <para>
/// Normally this is one whole mapping for one window, exactly as the original
/// orchestrator's innermost loop processed it. Mappings whose id list is too
/// long for a single request URI are split into parts by
/// <see cref="EnergyAspectsRequest.ChunkDatasetIds"/>, and each part becomes its
/// own unit — so a part that fails or times out retries on its own rather than
/// dragging the whole mapping with it.
/// </para>
/// </summary>
public sealed class EnergyAspectsWorkUnit : WorkUnit
{
    public required DatasetMapping Mapping { get; init; }

    /// <summary>
    /// The dataset ids this unit requests. A subset of
    /// <c>Mapping.DatasetIds</c> when <see cref="PartCount"/> is above 1,
    /// otherwise all of them.
    /// </summary>
    public required IReadOnlyList<int> DatasetIds { get; init; }

    public required DateTime WindowStart { get; init; }
    public required DateTime WindowEnd { get; init; }

    /// <summary>1-based index of this part within the mapping's split.</summary>
    public int PartNumber { get; init; } = 1;

    /// <summary>How many parts the mapping was split into. 1 means no split.</summary>
    public int PartCount { get; init; } = 1;

    /// <summary>
    /// True when this window is old enough that it will not be revised again, so the
    /// unit carries a STABLE key and is loaded exactly once. See
    /// <see cref="EnergyAspectsSettings.SettledAfterDays"/>.
    /// </summary>
    public required bool Settled { get; init; }

    /// <summary>
    /// Run-varying token appended to a HOT window's key. Null for a settled window.
    /// Supplied by the provider from <see cref="EnergyAspectsSettings.HotKeyStrategy"/>.
    /// </summary>
    public string? HotToken { get; init; }

    /// <summary>
    /// Resume key.
    ///
    /// <para>
    /// The settled form is byte-identical to what this loader has always written, so
    /// existing <c>core.LoadLog</c> history stays valid: single-part mappings keep the
    /// original no-part format, and split ones keep the <c>;part=NofM</c> format
    /// introduced with chunking.
    /// </para>
    ///
    /// <para>
    /// A hot window appends <c>;run=&lt;token&gt;</c> so it is re-fetched. Without it,
    /// whichever run goes first each day claims today's window and every later run —
    /// including the scheduled one — skips it, so anything the vendor publishes in
    /// between is not seen until the next day.
    /// </para>
    /// </summary>
    public override string Key
    {
        get
        {
            var baseKey = PartCount <= 1
                ? $"ea:mapping={Mapping.MappingId};from={WindowStart:yyyy-MM-dd};to={WindowEnd:yyyy-MM-dd}"
                : $"ea:mapping={Mapping.MappingId};part={PartNumber}of{PartCount};" +
                  $"from={WindowStart:yyyy-MM-dd};to={WindowEnd:yyyy-MM-dd}";

            return Settled ? baseKey : $"{baseKey};run={HotToken}";
        }
    }

    public override string DisplayName =>
        PartCount <= 1
            ? $"[{Mapping.MappingId}] {Mapping.Name} {WindowStart:yyyy-MM-dd}–{WindowEnd:yyyy-MM-dd}"
            : $"[{Mapping.MappingId}] {Mapping.Name} (part {PartNumber}/{PartCount}, " +
              $"{DatasetIds.Count} ids) {WindowStart:yyyy-MM-dd}–{WindowEnd:yyyy-MM-dd}";
}
