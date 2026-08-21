using DataLoader.Core.Abstractions;
using DataLoader.Core.Pipeline;
using DataLoader.Core.Transforms;
using Microsoft.Extensions.Logging;

namespace DataLoader.IIR;

/// <summary>Marker so <see cref="IirModule.RunAsync"/> can enumerate and toggle endpoint pipelines.</summary>
public interface IIirPipeline : ILoaderPipeline
{
    string EndpointId { get; }
}

/// <summary>
/// One closed per-endpoint pipeline reusing the platform's standard ETL loop (design §1.2). Its
/// provider/source/sink are supplied by <c>IirModule.BuildPipeline</c>, so the shared
/// <see cref="IirWorkUnit"/> generics are never resolved by the DI container — avoiding the
/// shared-generic-service collision (design §1.1).
/// </summary>
public sealed class IirPipeline<TRow> : LoaderPipelineBase<IirWorkUnit, TRow, TRow>, IIirPipeline
    where TRow : class, IIirFactRow
{
    public IirPipeline(
        string endpointId,
        IWorkUnitProvider<IirWorkUnit> provider,
        ISourceReader<IirWorkUnit, TRow> source,
        ISink<TRow> sink,
        ILoadLogRepository loadLog,
        IirSettings settings,
        ILogger logger)
        : base(IirModule.Id, provider, source, new IdentityTransformer<TRow>(), sink, loadLog, settings, logger)
    {
        EndpointId = endpointId;
    }

    public string EndpointId { get; }
}
