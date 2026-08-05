using DataLoader.Core.Abstractions;
using DataLoader.Core.Pipeline;
using DataLoader.Core.Transforms;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.Platts;

/// <summary>Marker so <see cref="PlattsModule.RunAsync"/> can enumerate all feed pipelines.</summary>
public interface IPlattsFeedPipeline : ILoaderPipeline
{
    string FeedId { get; }
}

/// <summary>One closed per-feed pipeline reusing the platform's standard ETL loop.</summary>
public sealed class PlattsFeedPipeline<TUnit, TRow> : LoaderPipelineBase<TUnit, TRow, TRow>, IPlattsFeedPipeline
    where TUnit : WorkUnit
{
    public PlattsFeedPipeline(
        string feedId,
        IWorkUnitProvider<TUnit> provider,
        ISourceReader<TUnit, TRow> source,
        ISink<TRow> sink,
        ILoadLogRepository loadLog,
        PlattsSettings settings,
        ILogger logger)
        : base(PlattsModule.Id, provider, source, new IdentityTransformer<TRow>(), sink, loadLog, settings, logger)
    {
        FeedId = feedId;
    }

    public string FeedId { get; }
}

/// <summary>
/// Plugin entry point for the Platts SFTP loader. One module, two closed
/// pipelines (SymbolData, Symbol). Because both use a file-shaped work unit, the
/// pipelines are built explicitly (Vulcan pattern) to avoid DI collisions on a
/// shared <c>IWorkUnitProvider&lt;T&gt;</c>.
/// </summary>
public sealed class PlattsModule : ILoaderModule
{
    public const string Id = "Platts";

    public string LoaderId => Id;
    public string DisplayName => "Platts SFTP (SymbolData + Symbol reference)";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<PlattsSettings>(configuration.GetSection($"Loaders:{Id}"));

        services.AddSingleton<IPlattsSftp, PlattsSftpFileSystem>();
        services.AddSingleton<IPlattsFileLog, PlattsFileLogWriter>();

        // Build the two closed pipelines explicitly.
        services.AddSingleton<IPlattsFeedPipeline>(BuildSymbolDataPipeline);
        services.AddSingleton<IPlattsFeedPipeline>(BuildSymbolPipeline);
    }

    private static IPlattsFeedPipeline BuildSymbolDataPipeline(IServiceProvider sp)
    {
        var options = sp.GetRequiredService<IOptions<PlattsSettings>>();
        var settings = options.Value;
        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
        var sftp = sp.GetRequiredService<IPlattsSftp>();
        var fileLog = sp.GetRequiredService<IPlattsFileLog>();

        var provider = new SymbolDataWorkUnitProvider(sftp, options, loggerFactory.CreateLogger<SymbolDataWorkUnitProvider>());
        var source = new SymbolDataSourceReader(sftp, loggerFactory.CreateLogger<SymbolDataSourceReader>());
        var innerSink = new SymbolDataSqlSink(options, loggerFactory.CreateLogger<SymbolDataSqlSink>());
        var sink = new FileLoggingSink<SymbolDataRow>(
            innerSink, fileLog, "SymbolData",
            r => (r.SourcePath, r.FileName, r.LastModifiedUtc, r.SizeBytes),
            loggerFactory.CreateLogger("Platts.SymbolData.FileLog"));

        return new PlattsFeedPipeline<SymbolDataWorkUnit, SymbolDataRow>(
            "SymbolData", provider, source, sink,
            sp.GetRequiredService<ILoadLogRepository>(), settings,
            loggerFactory.CreateLogger("Platts.SymbolData.Pipeline"));
    }

    private static IPlattsFeedPipeline BuildSymbolPipeline(IServiceProvider sp)
    {
        var options = sp.GetRequiredService<IOptions<PlattsSettings>>();
        var settings = options.Value;
        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
        var sftp = sp.GetRequiredService<IPlattsSftp>();
        var fileLog = sp.GetRequiredService<IPlattsFileLog>();

        var provider = new SymbolWorkUnitProvider(sftp, options, loggerFactory.CreateLogger<SymbolWorkUnitProvider>());
        var source = new SymbolSourceReader(sftp, options, loggerFactory.CreateLogger<SymbolSourceReader>());
        var innerSink = new SymbolSqlSink(options, loggerFactory.CreateLogger<SymbolSqlSink>());
        var sink = new FileLoggingSink<SymbolRow>(
            innerSink, fileLog, "Symbol",
            r => (r.SourcePath, r.FileName, r.LastModifiedUtc, r.SizeBytes),
            loggerFactory.CreateLogger("Platts.Symbol.FileLog"));

        return new PlattsFeedPipeline<SymbolWorkUnit, SymbolRow>(
            "Symbol", provider, source, sink,
            sp.GetRequiredService<ILoadLogRepository>(), settings,
            loggerFactory.CreateLogger("Platts.Symbol.Pipeline"));
    }

    public async Task<LoaderRunResult> RunAsync(IServiceProvider services, LoaderRunContext context)
    {
        var settings = services.GetRequiredService<IOptions<PlattsSettings>>().Value;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Platts.Module");
        var enabled = new HashSet<string>(settings.EnabledFeeds, StringComparer.OrdinalIgnoreCase);

        var allPipelines = services.GetServices<IPlattsFeedPipeline>().ToList();
        var pipelines = allPipelines.Where(p => enabled.Contains(p.FeedId)).ToList();

        var known = allPipelines.Select(p => p.FeedId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var feed in settings.EnabledFeeds.Where(f => !known.Contains(f)))
            logger.LogWarning("Enabled Platts feed '{Feed}' has no matching pipeline and will be skipped", feed);

        if (pipelines.Count == 0)
        {
            logger.LogWarning("No Platts feeds enabled");
            return new LoaderRunResult { LoaderId = Id, Success = true };
        }

        var results = new List<LoaderRunResult>();
        foreach (var pipeline in pipelines)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            results.Add(await pipeline.ExecuteAsync(context).ConfigureAwait(false));
        }

        return new LoaderRunResult
        {
            LoaderId = Id,
            Success = results.All(r => r.Success),
            WorkUnitsTotal = results.Sum(r => r.WorkUnitsTotal),
            WorkUnitsSucceeded = results.Sum(r => r.WorkUnitsSucceeded),
            WorkUnitsSkipped = results.Sum(r => r.WorkUnitsSkipped),
            WorkUnitsFailed = results.Sum(r => r.WorkUnitsFailed),
            RecordsProcessed = results.Sum(r => r.RecordsProcessed),
            ErrorMessage = string.Join("; ", results.Where(r => !r.Success && r.ErrorMessage != null).Select(r => r.ErrorMessage)),
            Duration = results.Aggregate(TimeSpan.Zero, (acc, r) => acc + r.Duration)
        };
    }
}
