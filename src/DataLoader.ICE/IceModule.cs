using DataLoader.Core.Abstractions;
using DataLoader.Core.Hosting;
using DataLoader.Core.Pipeline;
using DataLoader.Core.Transforms;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.ICE;

/// <summary>
/// Marker so <see cref="IceModule.RunAsync"/> can enumerate the per-feed pipelines
/// without their generics colliding with another loader's registrations in the
/// shared DI container.
/// </summary>
public interface IIcePipeline : ILoaderPipeline
{
    /// <summary>The feed id, matched case-insensitively against <c>EnabledFeeds</c>.</summary>
    string FeedId { get; }
}

/// <summary>
/// One closed per-feed pipeline over the platform's standard ETL loop. The reader
/// already emits the sink's row type, so the transformer is the identity.
/// </summary>
public sealed class IcePipeline : LoaderPipelineBase<IceWorkUnit, IceRow, IceRow>, IIcePipeline
{
    public IcePipeline(
        string feedId,
        IWorkUnitProvider<IceWorkUnit> provider,
        ISourceReader<IceWorkUnit, IceRow> source,
        ISink<IceRow> sink,
        ILoadLogRepository loadLog,
        IceSettings settings,
        ILogger logger)
        : base(IceModule.Id, provider, source, new IdentityTransformer<IceRow>(), sink, loadLog, settings, logger)
    {
        FeedId = feedId;
    }

    public string FeedId { get; }
}

/// <summary>
/// Plugin entry point for the ICE settlement &amp; index download loader.
///
/// <para>
/// 18 feeds, one pipeline each, landing in 12 tables. Every feed's URL template,
/// file format, column mapping, TVP and proc live in <see cref="IceDescriptors"/>,
/// so this class only wires them up — adding a feed is a descriptor plus its SQL,
/// not new plumbing.
/// </para>
/// <para>
/// The pipelines are MUTUALLY INDEPENDENT: no barrier, no shared state, no FK. A
/// failure in one feed must not cost the other 17, so <see cref="RunAsync"/> records
/// the failure and carries on (the Argus posture).
/// </para>
/// <para>
/// Pipelines are built explicitly rather than via open-generic DI, so this loader's
/// <c>IWorkUnitProvider&lt;T&gt;</c> and <c>ISink&lt;T&gt;</c> registrations cannot
/// collide with another loader's.
/// </para>
/// </summary>
public sealed class IceModule : ILoaderModule
{
    public const string Id = "ICE";

    public string LoaderId => Id;
    public string DisplayName => "ICE settlement reports, crude index and options greeks";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        // AddLoaderSettings (not a bare Configure<>) — this is what wires the SEE_DB
        // resolver that turns the "SEE_DB" UserId/Password sentinels into real values
        // from core.Param at run time.
        services.AddLoaderSettings<IceSettings>(configuration, Id);

        // The pace limiter is a SINGLETON so the 30-requests-per-minute budget is
        // shared across all 18 pipelines; the handler that consumes it is transient,
        // as HttpClientFactory requires.
        services.AddSingleton<IceRateLimiter>();
        services.AddTransient<IceRateLimitingHandler>();

        // One HttpClient for the whole loader. Its timeout covers the 23 MB oil
        // options file as well as the 178-byte crude index one.
        //
        // Handler order is retry (OUTER) -> rate limit (INNER), the repo standard.
        // The throttle must be INNER so a RETRIED request also waits its turn —
        // otherwise a retry jumps the queue and spends the very budget the backoff
        // is waiting to recover.
        services.AddHttpClient(Id, (sp, client) =>
        {
            var settings = sp.GetRequiredService<IOptions<IceSettings>>().Value;
            client.Timeout = TimeSpan.FromSeconds(Math.Max(30, settings.HttpTimeoutSeconds));
        })
        .AddPolicyHandler((sp, _) =>
        {
            var settings = sp.GetRequiredService<IOptions<IceSettings>>().Value;
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("ICE.Http");
            return IceHttpPolicy.Build(settings.RetryCount, settings.RetryDelayMs, logger);
        })
        .AddHttpMessageHandler<IceRateLimitingHandler>();

        // Singletons shared by all 18 pipelines. The authenticator especially: it
        // holds ONE token behind a semaphore, so 18 concurrent readers that hit
        // expiry together trigger one re-authentication rather than eighteen.
        services.AddSingleton<IIceAuthenticator>(sp => new IceSsoAuthenticator(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(Id),
            sp.GetRequiredService<IOptions<IceSettings>>(),
            sp.GetRequiredService<ILogger<IceSsoAuthenticator>>()));

        services.AddSingleton<IceFileCache>();
        services.AddSingleton<IIceFileLog, SqlIceFileLog>();
        services.AddSingleton<IceLoadValidator>();

        services.AddSingleton<IReadOnlyList<IIcePipeline>>(BuildPipelines);
    }

    private static IReadOnlyList<IIcePipeline> BuildPipelines(IServiceProvider sp)
    {
        var options = sp.GetRequiredService<IOptions<IceSettings>>();
        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
        var loadLog = sp.GetRequiredService<ILoadLogRepository>();
        var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient(Id);
        var auth = sp.GetRequiredService<IIceAuthenticator>();
        var cache = sp.GetRequiredService<IceFileCache>();
        var fileLog = sp.GetRequiredService<IIceFileLog>();

        var pipelines = new List<IIcePipeline>(IceDescriptors.All.Count);

        foreach (var feed in IceDescriptors.All)
        {
            var provider = new IceWorkUnitProvider(
                feed, options.Value, loggerFactory.CreateLogger($"ICE.{feed.FeedId}.Units"));

            var source = new IceSourceReader(
                feed, http, auth, cache, fileLog, options,
                loggerFactory.CreateLogger($"ICE.{feed.FeedId}.Reader"));

            // Note the sink is keyed on the TABLE, not the feed: the six feeds that
            // write arm.Futures all get a sink over the same descriptor and proc.
            var sink = new IceTableSink(
                feed.Table, options.Value.ConnectionString,
                loggerFactory.CreateLogger($"ICE.{feed.FeedId}.Sink"));

            pipelines.Add(new IcePipeline(
                feed.FeedId, provider, source, sink, loadLog, options.Value,
                loggerFactory.CreateLogger($"ICE.{feed.FeedId}.Pipeline")));
        }

        return pipelines;
    }

    public async Task<LoaderRunResult> RunAsync(IServiceProvider services, LoaderRunContext context)
    {
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("ICE.Module");
        var settings = services.GetRequiredService<IOptions<IceSettings>>().Value;
        var pipelines = services.GetRequiredService<IReadOnlyList<IIcePipeline>>();
        var cache = services.GetRequiredService<IceFileCache>();

        WarnAboutConfiguration(settings, logger);

        // Housekeeping first, so a run never enumerates against a folder it is about
        // to prune. Best-effort by contract — it cannot fail the run.
        cache.SweepExpired();

        var enabled = pipelines
            .Where(p => settings.EnabledFeeds.Contains(p.FeedId, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (enabled.Count == 0)
        {
            logger.LogWarning("ICE: no feeds enabled — nothing to do");
            return new LoaderRunResult { LoaderId = Id, Success = true, Duration = TimeSpan.Zero };
        }

        // Worst case is a cold cache: every feed × every date is a real request. The
        // estimate is logged because at 25 requests/minute a first run legitimately
        // takes ~20 minutes, and an operator who does not expect that will assume the
        // loader has hung.
        var worstCaseRequests = enabled.Count * (Math.Max(0, settings.DaysBack) + 1);
        var pacedMinutes = settings.RequestsPerMinute > 0
            ? worstCaseRequests / (double)settings.RequestsPerMinute
            : 0;

        logger.LogInformation(
            "ICE: running {Count} of {Total} feed(s) over {DaysBack} day(s) = up to {Requests} request(s); " +
            "paced at {Rpm}/min (ICE allows 30/min) -> ~{Minutes:N0} min on a cold cache; cache {Root}",
            enabled.Count, pipelines.Count, settings.DaysBack, worstCaseRequests,
            settings.RequestsPerMinute, pacedMinutes, cache.Root);

        var aggregate = new LoaderRunResult { LoaderId = Id, Success = true };
        var failures = new List<string>();

        foreach (var pipeline in enabled)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            // A feed's failure is recorded and the run continues: the pipelines are
            // independent, and losing one feed must not cost the other 17.
            LoaderRunResult result;
            try
            {
                result = await pipeline.ExecuteAsync(context).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "ICE: feed {Feed} threw out of its pipeline", pipeline.FeedId);
                failures.Add(pipeline.FeedId);
                continue;
            }

            if (!result.Success) failures.Add(pipeline.FeedId);

            aggregate = new LoaderRunResult
            {
                LoaderId = Id,
                Success = aggregate.Success && result.Success,
                WorkUnitsTotal = aggregate.WorkUnitsTotal + result.WorkUnitsTotal,
                WorkUnitsSucceeded = aggregate.WorkUnitsSucceeded + result.WorkUnitsSucceeded,
                WorkUnitsSkipped = aggregate.WorkUnitsSkipped + result.WorkUnitsSkipped,
                WorkUnitsFailed = aggregate.WorkUnitsFailed + result.WorkUnitsFailed,
                RecordsProcessed = aggregate.RecordsProcessed + result.RecordsProcessed,
                Duration = aggregate.Duration + result.Duration
            };
        }

        if (failures.Count > 0)
        {
            // LoaderRunResult is an init-only class, not a record — rebuild it.
            aggregate = new LoaderRunResult
            {
                LoaderId = Id,
                Success = false,
                ErrorMessage = $"Failed feed(s): {string.Join(", ", failures)}",
                WorkUnitsTotal = aggregate.WorkUnitsTotal,
                WorkUnitsSucceeded = aggregate.WorkUnitsSucceeded,
                WorkUnitsSkipped = aggregate.WorkUnitsSkipped,
                WorkUnitsFailed = aggregate.WorkUnitsFailed,
                RecordsProcessed = aggregate.RecordsProcessed,
                Duration = aggregate.Duration
            };
        }

        // Post-load validation is observational and must not change the run's
        // outcome — IceLoadValidator swallows its own failures. Scoped to the newest
        // trade date the window covered.
        await services.GetRequiredService<IceLoadValidator>()
            .ValidateAsync(IceTime.Today(context.StartedAtUtc), context.CancellationToken)
            .ConfigureAwait(false);

        logger.LogInformation(
            "ICE run complete: {Succeeded} succeeded, {Skipped} skipped, {Failed} failed, {Records} record(s)",
            aggregate.WorkUnitsSucceeded, aggregate.WorkUnitsSkipped, aggregate.WorkUnitsFailed,
            aggregate.RecordsProcessed);

        return aggregate;
    }

    /// <summary>
    /// Startup checks that warn rather than throw. Each of these is a configuration
    /// that "works" but silently does less than the operator expects, which is worse
    /// than a loud failure.
    /// </summary>
    private static void WarnAboutConfiguration(IceSettings settings, ILogger logger)
    {
        foreach (var id in settings.EnabledFeeds)
            if (IceDescriptors.Find(id) is null)
                logger.LogWarning("ICE: EnabledFeeds contains unknown feed id '{FeedId}' — ignored", id);

        if (IceTime.UsingUtcFallback)
            logger.LogWarning(
                "ICE could not resolve the US Central time zone (neither 'Central Standard Time' nor " +
                "'America/Chicago'); falling back to UTC for trade-date enumeration — verify the host " +
                "time-zone database");

        var daysBack = Math.Max(0, settings.DaysBack);
        var settledAfterDays = Math.Max(0, settings.SettledAfterDays);

        if (settledAfterDays < daysBack)
            logger.LogWarning(
                "ICE SettledAfterDays={SettledAfterDays} is below DaysBack={DaysBack}: trade dates aged " +
                "{From}..{To} days get a STABLE resume key and will never be re-pulled, so ICE's revised " +
                "settlements for those days would be missed. Prefer SettledAfterDays >= DaysBack (the " +
                "shipped 30/30 default, all-hot)",
                settledAfterDays, daysBack, settledAfterDays + 1, daysBack);

        if (settings.FileRetentionDays > 0 && settings.FileRetentionDays < daysBack)
            logger.LogInformation(
                "ICE FileRetentionDays={Retention} is below DaysBack={DaysBack}: trade dates older than " +
                "{Retention} day(s) are re-downloaded each run rather than served from the local cache " +
                "(a bandwidth/disk trade-off, not a correctness issue)",
                settings.FileRetentionDays, daysBack, settings.FileRetentionDays);

        if (settings.ForceDownload)
            logger.LogWarning(
                "ICE ForceDownload=true: the local file cache is bypassed and every work unit re-downloads");

        // ICE's documented ceiling, quoted in the body of its own 429. Exceeding it
        // blocks EVERY request for 60 seconds, so a too-high setting does not run
        // faster — it stalls.
        const int IceStatedLimitPerMinute = 30;

        if (settings.RequestsPerMinute > IceStatedLimitPerMinute)
            logger.LogWarning(
                "ICE RequestsPerMinute={Rpm} exceeds the limit ICE states in its own 429 response " +
                "({Limit}/minute). Going over blocks every request for 60 seconds, so this will make " +
                "the run slower, not faster. The shipped default is 25, which leaves headroom for the " +
                "sliding window",
                settings.RequestsPerMinute, IceStatedLimitPerMinute);
        else if (settings.RequestsPerMinute <= 0)
            logger.LogWarning(
                "ICE RequestsPerMinute={Rpm}: client-side pacing is DISABLED. ICE allows only {Limit} " +
                "requests/minute and answers 429 with Retry-After: 60 beyond that — expect throttling " +
                "on any run that fetches more files than that",
                settings.RequestsPerMinute, IceStatedLimitPerMinute);
    }
}
