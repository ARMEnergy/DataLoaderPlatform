using DataLoader.Core.Abstractions;
using DataLoader.Core.Pipeline;
using DataLoader.Core.Transforms;
using Microsoft.Extensions.Logging;

namespace DataLoader.NGI;

/// <summary>
/// Marker so <see cref="NgiModule.RunAsync"/> can enumerate, toggle and order the two pipelines.
/// </summary>
public interface INgiPipeline : ILoaderPipeline
{
    /// <summary>
    /// <c>BidWeekLocations</c> or <c>BidWeekData</c> — matched case-insensitively against
    /// <see cref="NgiSettings.EnabledEndpoints"/>.
    /// </summary>
    string EndpointId { get; }
}

/// <summary>
/// One closed pipeline reusing the platform's standard ETL loop directly (design §1.3 — the AGSI/CWG
/// shape, NOT StormVista's windowed orchestrator, because the unit count is trivial: Locations = 1
/// unit, BidWeekData = <c>DaysBack</c> units, 60 at the default). The vetted per-unit loop is reused
/// unchanged: <c>BeginAsync</c> idempotency skip → <c>ReadAsync</c> → identity transform →
/// <c>WriteAsync</c> → <c>CompleteSuccess</c>/<c>CompleteFailure</c>, bounded by
/// <c>ParallelRunner</c> at <c>MaxConcurrentWorkUnits</c>, with a per-unit timeout and
/// <b>fail-a-block-not-the-run</b> semantics.
///
/// <para>Its provider/source/sink are supplied by <see cref="NgiModule"/>'s factory closures, so the
/// shared generics are never resolved by the DI container (design §1.1) and cannot collide with
/// another loader's. Identity transform: the readers already produce the sink's row type.</para>
///
/// <para><b>The two instances are mutually INDEPENDENT</b> — see the divergence note in
/// <c>WorkUnits.cs</c> and design §1.2. Nothing here couples them.</para>
/// </summary>
public sealed class NgiPipeline<TUnit, TRow> : LoaderPipelineBase<TUnit, TRow, TRow>, INgiPipeline
    where TUnit : WorkUnit
{
    public NgiPipeline(
        string endpointId,
        IWorkUnitProvider<TUnit> provider,
        ISourceReader<TUnit, TRow> source,
        ISink<TRow> sink,
        ILoadLogRepository loadLog,
        NgiSettings settings,
        ILogger logger)
        : base(NgiModule.Id, provider, source, new IdentityTransformer<TRow>(), sink, loadLog, settings, logger)
    {
        EndpointId = endpointId;
    }

    public string EndpointId { get; }
}
