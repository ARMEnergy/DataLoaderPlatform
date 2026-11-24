using DataLoader.Core.Abstractions;
using DataLoader.Core.Pipeline;
using DataLoader.Core.Transforms;
using Microsoft.Extensions.Logging;

namespace DataLoader.PJM;

/// <summary>Marker so <see cref="PjmModule.RunAsync"/> can enumerate, toggle and ORDER the two pipelines.</summary>
public interface IPjmPipeline : ILoaderPipeline
{
    string FeedId { get; }
}

/// <summary>
/// One closed pipeline per feed reusing the platform's standard loop directly (design §1.2):
/// idempotency skip → read → identity transform → write → complete. Built in the module's factory
/// closure, so the shared generics are never resolved from DI (design §1.1).
/// </summary>
public sealed class PjmPipeline<TRow> : LoaderPipelineBase<PjmDayWorkUnit, TRow, TRow>, IPjmPipeline
{
    public PjmPipeline(
        string feedId,
        IWorkUnitProvider<PjmDayWorkUnit> provider,
        ISourceReader<PjmDayWorkUnit, TRow> source,
        ISink<TRow> sink,
        ILoadLogRepository loadLog,
        PjmSettings settings,
        ILogger logger)
        : base(PjmModule.Id, provider, source, new IdentityTransformer<TRow>(), sink, loadLog, settings, logger)
    {
        FeedId = feedId;
    }

    public string FeedId { get; }
}
