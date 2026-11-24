using DataLoader.Core.Abstractions;
using DataLoader.Core.Pipeline;
using DataLoader.Core.Transforms;
using Microsoft.Extensions.Logging;

namespace DataLoader.MISO;

/// <summary>Marker so <see cref="MisoModule.RunAsync"/> can enumerate, toggle and order the four pipelines.</summary>
public interface IMisoPipeline : ILoaderPipeline
{
    string FeedId { get; }
}

/// <summary>
/// One closed pipeline per feed on the platform's standard loop: idempotency skip → read →
/// identity transform → write → complete. Built in the module's factory closure.
/// </summary>
public sealed class MisoPipeline<TRow> : LoaderPipelineBase<MisoWorkUnit, TRow, TRow>, IMisoPipeline
{
    public MisoPipeline(
        string feedId,
        IWorkUnitProvider<MisoWorkUnit> provider,
        ISourceReader<MisoWorkUnit, TRow> source,
        ISink<TRow> sink,
        ILoadLogRepository loadLog,
        MisoSettings settings,
        ILogger logger)
        : base(MisoModule.Id, provider, source, new IdentityTransformer<TRow>(), sink, loadLog, settings, logger)
    {
        FeedId = feedId;
    }

    public string FeedId { get; }
}
