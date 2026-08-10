using DataLoader.Core.Abstractions;
using DataLoader.Core.Hosting;
using DataLoader.Core.Pipeline;
using DataLoader.Core.Resilience;
using DataLoader.Core.Transforms;
using DataLoader.Vulcan.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.Vulcan;

/// <summary>Marker so VulcanModule.RunAsync can enumerate all per-table pipelines.</summary>
public interface IVulcanTablePipeline : ILoaderPipeline
{
    string TableId { get; }
}

/// <summary>One closed per-table pipeline. Reuses the platform's standard ETL loop.</summary>
public sealed class VulcanTablePipeline<TRow> : LoaderPipelineBase<VulcanWorkUnit, TRow, TRow>, IVulcanTablePipeline
{
    public VulcanTablePipeline(
        string tableId,
        IWorkUnitProvider<VulcanWorkUnit> workUnits,
        ISourceReader<VulcanWorkUnit, TRow> source,
        ISink<TRow> sink,
        ILoadLogRepository loadLog,
        VulcanSettings settings,
        ILogger logger)
        : base(VulcanModule.Id, workUnits, source, new IdentityTransformer<TRow>(), sink, loadLog, settings, logger)
    {
        TableId = tableId;
    }

    public string TableId { get; }
}

public sealed class VulcanModule : ILoaderModule
{
    public const string Id = "Vulcan";
    public const string HttpClientName = "Vulcan";

    public string LoaderId => Id;
    public string DisplayName => "SynMax Vulcan (POST SQL query_datalinks, incremental)";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddLoaderSettings<VulcanSettings>(configuration, Id);

        services.AddHttpClient(HttpClientName, (sp, client) =>
        {
            var s = sp.GetRequiredService<IOptions<VulcanSettings>>().Value;
            if (!string.IsNullOrWhiteSpace(s.BaseUrl))
                client.BaseAddress = new Uri(s.BaseUrl);
            client.Timeout = TimeSpan.FromSeconds(s.HttpTimeoutSeconds);
            client.DefaultRequestHeaders.Add("Accept", "application/json");
            if (!string.IsNullOrWhiteSpace(s.ApiKey))
                client.DefaultRequestHeaders.Add("Access-Key", s.ApiKey);
        })
        .AddPolicyHandler((sp, _) =>
        {
            var s = sp.GetRequiredService<IOptions<VulcanSettings>>().Value;
            var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Vulcan.Http");
            return RetryPolicyFactory.BuildHttpRetryPolicy(
                s.RetryCount, s.RetryDelayMs,
                (outcome, delay, attempt) =>
                    log.LogWarning("HTTP retry {Attempt}: status {Status}, waiting {Delay}ms",
                        attempt, outcome.Result?.StatusCode, delay.TotalMilliseconds));
        });

        services.AddSingleton<IVulcanWatermarkStore, VulcanWatermarkStore>();

        services.AddSingleton<UnderConstructionSink>();
        services.AddSingleton<DataCentersSink>();
        services.AddSingleton<LngProjectsSink>();
        services.AddSingleton<ProjectRankingsSink>();
        services.AddSingleton<MetadataHistorySink>();

        // Build the five closed pipelines explicitly (the shared VulcanWorkUnit type
        // means an interface-keyed IWorkUnitProvider<VulcanWorkUnit> would collide).
        RegisterTablePipeline<UnderConstructionRow>(services, VulcanTableSpec.UnderConstruction,
            sp => sp.GetRequiredService<UnderConstructionSink>());
        RegisterTablePipeline<DataCenterRow>(services, VulcanTableSpec.DataCenters,
            sp => sp.GetRequiredService<DataCentersSink>());
        RegisterTablePipeline<LngProjectRow>(services, VulcanTableSpec.LngProjects,
            sp => sp.GetRequiredService<LngProjectsSink>());
        RegisterTablePipeline<ProjectRankingRow>(services, VulcanTableSpec.ProjectRankings,
            sp => sp.GetRequiredService<ProjectRankingsSink>());
        RegisterTablePipeline<MetadataHistoryRow>(services, VulcanTableSpec.MetadataHistory,
            sp => sp.GetRequiredService<MetadataHistorySink>());
    }

    private static void RegisterTablePipeline<TRow>(
        IServiceCollection services, VulcanTableSpec spec, Func<IServiceProvider, ISink<TRow>> sinkFactory)
    {
        services.AddSingleton<IVulcanTablePipeline>(sp =>
        {
            var settings = sp.GetRequiredService<IOptions<VulcanSettings>>().Value;
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName);

            var provider = new VulcanWorkUnitProvider(
                spec, sp.GetRequiredService<IVulcanWatermarkStore>(),
                loggerFactory.CreateLogger($"Vulcan.{spec.TableId}.Provider"));

            var source = new VulcanQuerySourceReader<TRow>(
                httpClient, sp.GetRequiredService<IOptions<VulcanSettings>>(),
                loggerFactory.CreateLogger($"Vulcan.{spec.TableId}.Source"));

            return new VulcanTablePipeline<TRow>(
                spec.TableId, provider, source, sinkFactory(sp),
                sp.GetRequiredService<ILoadLogRepository>(), settings,
                loggerFactory.CreateLogger($"Vulcan.{spec.TableId}.Pipeline"));
        });
    }

    public async Task<LoaderRunResult> RunAsync(IServiceProvider services, LoaderRunContext context)
    {
        var settings = services.GetRequiredService<IOptions<VulcanSettings>>().Value;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Vulcan.Module");
        var enabled = new HashSet<string>(settings.EnabledTables, StringComparer.OrdinalIgnoreCase);

        var pipelines = services.GetServices<IVulcanTablePipeline>()
            .Where(p => enabled.Contains(p.TableId))
            .ToList();

        var known = services.GetServices<IVulcanTablePipeline>().Select(p => p.TableId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var t in settings.EnabledTables.Where(t => !known.Contains(t)))
            logger.LogWarning("Enabled Vulcan table '{Table}' has no matching pipeline and will be skipped", t);

        if (pipelines.Count == 0)
        {
            logger.LogWarning("No Vulcan tables enabled");
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
