using DataLoader.Core.Abstractions;
using DataLoader.Core.Pipeline;
using DataLoader.Core.Transforms;
using Microsoft.Extensions.Logging;

namespace DataLoader.IHSPointLogic;

/// <summary>Marker so <see cref="IHSPointLogicModule.RunAsync"/> can enumerate, toggle and TIER-order the pipelines.</summary>
public interface IPlPipeline : ILoaderPipeline
{
    string EndpointId { get; }
    int Tier { get; }
}

/// <summary>
/// One closed per-endpoint pipeline reusing the platform's standard ETL loop (design §1.2). Its
/// provider/source/sink are supplied by <c>IHSPointLogicModule.BuildPipeline</c>, so the shared
/// <see cref="PlWorkUnit"/> / <see cref="PlSourceReader{TRow}"/> generics are never resolved by the
/// DI container — avoiding the shared-generic-service collision (design §1.1). Identity transform:
/// the reader already produces the sink's row type. The <see cref="Tier"/> marker lets
/// <see cref="IHSPointLogicModule.RunAsync"/> group and order the pipelines with barriers.
/// </summary>
public sealed class PlPipeline<TRow> : LoaderPipelineBase<PlWorkUnit, TRow, TRow>, IPlPipeline
    where TRow : class, IPlFactRow
{
    public PlPipeline(
        string endpointId,
        int tier,
        IWorkUnitProvider<PlWorkUnit> provider,
        ISourceReader<PlWorkUnit, TRow> source,
        ISink<TRow> sink,
        ILoadLogRepository loadLog,
        IHSPointLogicSettings settings,
        ILogger logger)
        : base(IHSPointLogicModule.Id, provider, source, new IdentityTransformer<TRow>(), sink, loadLog, settings, logger)
    {
        EndpointId = endpointId;
        Tier = tier;
    }

    public string EndpointId { get; }
    public int Tier { get; }
}
