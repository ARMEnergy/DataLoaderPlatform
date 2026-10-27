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
        services.AddSingleton<IVulcanTableRegistry, VulcanTableRegistry>();

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
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Vulcan.Module");
        var registry = services.GetRequiredService<IVulcanTableRegistry>();
        var ct = context.CancellationToken;

        // Which tables to load comes from dbo.LoaderTable, not appsettings, so the set is
        // operable without a redeploy. A missing or empty registry is a FAILURE, not an empty
        // run: silently loading nothing would hide an undeployed sql/Vulcan/001.
        IReadOnlyList<VulcanTableRegistration> registrations;
        try
        {
            registrations = await registry.GetAllAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not read dbo.LoaderTable; deploy sql/Vulcan/001 before running");
            return LoaderRunResult.Failed(Id,
                $"Could not read the Vulcan table registry (dbo.LoaderTable): {ex.Message}", TimeSpan.Zero);
        }

        if (registrations.Count == 0)
        {
            logger.LogError("dbo.LoaderTable is empty — deploy sql/Vulcan/001 to seed it");
            return LoaderRunResult.Failed(Id,
                "The Vulcan table registry (dbo.LoaderTable) is empty; deploy sql/Vulcan/001 to seed it.",
                TimeSpan.Zero);
        }

        foreach (var r in registrations.Where(r => !r.Enabled))
            logger.LogInformation("Vulcan table '{Table}' is disabled in dbo.LoaderTable — skipping " +
                                  "(last successful load {LastRun})",
                                  r.TableName, r.LastRun?.ToString("u") ?? "never");

        var enabled = new HashSet<string>(
            registrations.Where(r => r.Enabled).Select(r => r.TableName), StringComparer.OrdinalIgnoreCase);

        var allPipelines = services.GetServices<IVulcanTablePipeline>().ToList();
        var pipelines = allPipelines.Where(p => enabled.Contains(p.TableId)).ToList();

        var known = allPipelines.Select(p => p.TableId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var t in enabled.Where(t => !known.Contains(t)))
            logger.LogWarning("Vulcan table '{Table}' is enabled in dbo.LoaderTable but has no matching " +
                              "pipeline and will be skipped", t);

        if (pipelines.Count == 0)
        {
            logger.LogError("No Vulcan tables are enabled in dbo.LoaderTable");
            return LoaderRunResult.Failed(Id,
                "No Vulcan tables are enabled in dbo.LoaderTable.", TimeSpan.Zero);
        }

        var results = new List<LoaderRunResult>();
        foreach (var pipeline in pipelines)
        {
            ct.ThrowIfCancellationRequested();
            var result = await pipeline.ExecuteAsync(context).ConfigureAwait(false);
            results.Add(result);

            // LastRun means "last SUCCESSFUL load", so only a clean run stamps it. Best-effort:
            // failing to record the stamp must not turn a good load into a failed one.
            if (result.Success)
            {
                try
                {
                    await registry.SetLastRunAsync(pipeline.TableId, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Loaded {Table} but could not stamp dbo.LoaderTable.LastRun",
                        pipeline.TableId);
                }
            }
        }

        var duration = results.Aggregate(TimeSpan.Zero, (acc, r) => acc + r.Duration);
        return LoaderRunResult.Aggregate(Id, results, duration);
    }
}
