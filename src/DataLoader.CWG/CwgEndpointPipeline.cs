using DataLoader.Core.Abstractions;
using DataLoader.Core.Pipeline;
using DataLoader.Core.Transforms;
using Microsoft.Extensions.Logging;

namespace DataLoader.CWG;

/// <summary>Marker so <see cref="CwgModule.RunAsync"/> can enumerate and toggle endpoint pipelines.</summary>
public interface ICwgEndpointPipeline : ILoaderPipeline
{
    string EndpointId { get; }
}

/// <summary>
/// One closed per-endpoint pipeline reusing the platform's standard ETL loop
/// (design §1.2). Its provider/source/sink are supplied by
/// <c>CwgModule.BuildPipeline</c>, so the shared <see cref="CwgWorkUnit"/>
/// generics are never resolved by the DI container — avoiding the
/// shared-generic-service collision (design §1.1).
/// </summary>
public sealed class CwgEndpointPipeline<TRow> : LoaderPipelineBase<CwgWorkUnit, TRow, TRow>, ICwgEndpointPipeline
{
    public CwgEndpointPipeline(
        string endpointId,
        IWorkUnitProvider<CwgWorkUnit> provider,
        ISourceReader<CwgWorkUnit, TRow> source,
        ISink<TRow> sink,
        ILoadLogRepository loadLog,
        CwgSettings settings,
        ILogger logger)
        : base(CwgModule.Id, provider, source, new IdentityTransformer<TRow>(), sink, loadLog, settings, logger)
    {
        EndpointId = endpointId;
    }

    public string EndpointId { get; }
}
