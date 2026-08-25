using DataLoader.Core.Abstractions;
using DataLoader.Core.Pipeline;
using DataLoader.Core.Transforms;
using Microsoft.Extensions.Logging;

namespace DataLoader.ModernCommodities;

/// <summary>
/// Marker so <see cref="ModernCommoditiesModule.RunAsync"/> can enumerate, toggle and order the
/// three pipelines.
/// </summary>
public interface IModComPipeline : ILoaderPipeline
{
    /// <summary>
    /// <c>AllTrades</c> | <c>MyTrades</c> | <c>Settlements</c> — matched case-insensitively against
    /// <see cref="ModComSettings.EnabledEndpoints"/>.
    /// </summary>
    string EndpointId { get; }
}

/// <summary>
/// One closed per-endpoint pipeline reusing the platform's standard ETL loop <b>directly</b>
/// (design §1.4 — the CWG/AGSI/NGI shape, <b>not</b> StormVista's windowed orchestrator).
///
/// <para>Unit counts are trivial: <b>1 unit per endpoint at the shipped default</b>
/// (<c>ChunkDays = 0</c> = the whole window in a single request → 3 requests per run), rising to a
/// handful only for a chunked deep backfill. The whole list materialises for free, so the vetted
/// per-unit loop is reused unchanged: <c>BeginAsync</c> idempotency skip → <c>ReadAsync</c> →
/// identity transform → <c>WriteAsync</c> → <c>CompleteSuccess</c>/<c>CompleteFailure</c>, bounded
/// by <c>ParallelRunner</c> at <c>MaxConcurrentWorkUnits</c>, with a per-unit timeout and
/// <b>fail-a-block-not-the-run</b> semantics.</para>
///
/// <para>Its provider/source/sink are supplied by <see cref="ModernCommoditiesModule"/>'s factory
/// closures, so the shared <see cref="ModComWorkUnit"/> generics are never resolved by the DI
/// container and cannot collide with another loader's registrations (design §1.1). The transform is
/// the identity: the readers already produce the sink's row type.</para>
///
/// <para><b>The three instances are mutually INDEPENDENT.</b> Nothing here couples them — no
/// reference provider, no barrier, no FK, no shared state (design §1.2).</para>
/// </summary>
public sealed class ModComPipeline<TRow> : LoaderPipelineBase<ModComWorkUnit, TRow, TRow>, IModComPipeline
{
    public ModComPipeline(
        string endpointId,
        IWorkUnitProvider<ModComWorkUnit> provider,
        ISourceReader<ModComWorkUnit, TRow> source,
        ISink<TRow> sink,
        ILoadLogRepository loadLog,
        ModComSettings settings,
        ILogger logger)
        : base(ModernCommoditiesModule.Id, provider, source, new IdentityTransformer<TRow>(), sink, loadLog, settings, logger)
    {
        EndpointId = endpointId;
    }

    public string EndpointId { get; }
}
