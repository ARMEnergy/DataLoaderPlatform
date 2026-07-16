using DataLoader.Core.Abstractions;
using DataLoader.Core.Pipeline;
using DataLoader.Core.Sources;
using DataLoader.Core.Transforms;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.CsvExample;

/// <summary>
/// Plugin entry point for the CSV-drop loader.
///
/// Demonstrates a non-HTTP, non-time-windowed loader: work units are files
/// discovered in a directory; source is the file system; sink is SQL. No
/// API key, no date range, no metadata refresh — none of the Energy-Aspects
/// machinery is dragged in.
/// </summary>
public sealed class CsvExampleModule : ILoaderModule
{
    public const string Id = "CsvExample";

    public string LoaderId => Id;
    public string DisplayName => "CSV Drop Loader (local filesystem)";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<CsvExampleSettings>(configuration.GetSection($"Loaders:{Id}"));

        // This loader uses the local-disk driver. Swap for an FTP or S3
        // implementation of ICsvFileSystem to point at a different source —
        // no other code in the loader needs to change.
        services.AddSingleton<ICsvFileSystem, LocalCsvFileSystem>();

        // Loader-local services
        services.AddSingleton<CsvWorkUnitProvider>();
        services.AddSingleton<CsvSourceReader>();
        services.AddSingleton<CsvSqlSink>();
        services.AddSingleton<IdentityTransformer<CsvRow>>();

        // Pipeline wiring (TUnit = CsvFileWorkUnit, TItem = CsvRow, TRow = CsvRow)
        services.AddSingleton<IWorkUnitProvider<CsvFileWorkUnit>>(sp => sp.GetRequiredService<CsvWorkUnitProvider>());
        services.AddSingleton<ISourceReader<CsvFileWorkUnit, CsvRow>>(sp => sp.GetRequiredService<CsvSourceReader>());
        services.AddSingleton<ITransformer<CsvRow, CsvRow>>(sp => sp.GetRequiredService<IdentityTransformer<CsvRow>>());
        services.AddSingleton<Core.Abstractions.ISink<CsvRow>>(sp => sp.GetRequiredService<CsvSqlSink>());

        services.AddSingleton<CsvExamplePipeline>();
    }

    public async Task<LoaderRunResult> RunAsync(IServiceProvider services, LoaderRunContext context)
    {
        var pipeline = services.GetRequiredService<CsvExamplePipeline>();
        return await pipeline.ExecuteAsync(context).ConfigureAwait(false);
    }
}

public sealed class CsvExamplePipeline : LoaderPipelineBase<CsvFileWorkUnit, CsvRow, CsvRow>
{
    public CsvExamplePipeline(
        IWorkUnitProvider<CsvFileWorkUnit> workUnits,
        ISourceReader<CsvFileWorkUnit, CsvRow> source,
        ITransformer<CsvRow, CsvRow> transformer,
        Core.Abstractions.ISink<CsvRow> sink,
        ILoadLogRepository loadLog,
        IOptions<CsvExampleSettings> settings,
        ILogger<CsvExamplePipeline> logger)
        : base(CsvExampleModule.Id, workUnits, source, transformer, sink, loadLog, settings.Value, logger)
    {
    }
}
