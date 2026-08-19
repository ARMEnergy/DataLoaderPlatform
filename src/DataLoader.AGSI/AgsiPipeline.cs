using DataLoader.Core.Abstractions;
using DataLoader.Core.Pipeline;
using DataLoader.Core.Transforms;
using Microsoft.Extensions.Logging;

namespace DataLoader.AGSI;

/// <summary>Marker so <see cref="AgsiModule.RunAsync"/> can enumerate, toggle and ORDER the two pipelines.</summary>
public interface IAgsiPipeline : ILoaderPipeline
{
    string EndpointId { get; }
}

/// <summary>
/// One closed pipeline reusing the platform's standard ETL loop directly
/// (design §1.2 — CWG-style, not StormVista's windowed orchestrator, because the
/// unit count is tiny). Its provider/source/sink are supplied by
/// <c>AgsiModule</c>'s factory closures, so the shared generics are never resolved
/// by the DI container (design §1.1). Identity transform: the readers already
/// produce the sink's row type.
/// </summary>
public sealed class AgsiPipeline<TUnit, TRow> : LoaderPipelineBase<TUnit, TRow, TRow>, IAgsiPipeline
    where TUnit : WorkUnit
{
    public AgsiPipeline(
        string endpointId,
        IWorkUnitProvider<TUnit> provider,
        ISourceReader<TUnit, TRow> source,
        ISink<TRow> sink,
        ILoadLogRepository loadLog,
        AgsiSettings settings,
        ILogger logger)
        : base(AgsiModule.Id, provider, source, new IdentityTransformer<TRow>(), sink, loadLog, settings, logger)
    {
        EndpointId = endpointId;
    }

    public string EndpointId { get; }
}
