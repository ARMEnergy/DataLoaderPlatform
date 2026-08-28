using DataLoader.Core.Abstractions;
using DataLoader.Core.Pipeline;
using DataLoader.Core.Transforms;
using Microsoft.Extensions.Logging;

namespace DataLoader.EvolutionMarkets;

/// <summary>
/// Marker so <see cref="EvolutionMarketsModule.RunAsync"/> can enumerate and toggle pipelines by id.
///
/// <para>There is exactly ONE pipeline today. The interface exists anyway so that adding the sibling
/// <c>GET /v1/market-data</c> snapshot endpoint later is a registration, not a refactor — and so the
/// <c>EnabledEndpoints</c> toggle behaves identically to every other loader in the repo.</para>
/// </summary>
public interface IEvoPipeline : ILoaderPipeline
{
    /// <summary>
    /// <c>MarketDataHistory</c> — matched case-insensitively against
    /// <see cref="EvoSettings.EnabledEndpoints"/>.
    /// </summary>
    string EndpointId { get; }
}

/// <summary>
/// One closed pipeline reusing the platform's standard ETL loop directly (design §1.3 — the
/// NGI/AGSI/CWG shape, NOT StormVista's windowed orchestrator, because the unit count is trivial:
/// <c>DaysBack</c> units, 30 at the default). The vetted per-unit loop is reused unchanged:
/// <c>BeginAsync</c> idempotency skip → <c>ReadAsync</c> → identity transform → <c>WriteAsync</c> →
/// <c>CompleteSuccess</c>/<c>CompleteFailure</c>, bounded by <c>ParallelRunner</c> at
/// <c>MaxConcurrentWorkUnits</c>, with a per-unit timeout and <b>fail-a-unit-not-the-run</b>
/// semantics.
///
/// <para>Its provider/source/sink are supplied by <see cref="EvolutionMarketsModule"/>'s factory
/// closure, so the shared generics are never resolved by the DI container (design §1.1) and cannot
/// collide with another loader's. Identity transform: the reader already produces the sink's row
/// type, fully stamped with its <c>FileLogId</c> and <c>Checksum</c>.</para>
/// </summary>
public sealed class EvoPipeline<TUnit, TRow> : LoaderPipelineBase<TUnit, TRow, TRow>, IEvoPipeline
    where TUnit : WorkUnit
{
    public EvoPipeline(
        string endpointId,
        IWorkUnitProvider<TUnit> provider,
        ISourceReader<TUnit, TRow> source,
        ISink<TRow> sink,
        ILoadLogRepository loadLog,
        EvoSettings settings,
        ILogger logger)
        : base(EvolutionMarketsModule.Id, provider, source, new IdentityTransformer<TRow>(), sink, loadLog, settings, logger)
    {
        EndpointId = endpointId;
    }

    public string EndpointId { get; }
}
