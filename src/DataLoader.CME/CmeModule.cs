using DataLoader.Core.Abstractions;
using DataLoader.Core.Hosting;
using DataLoader.Core.Pipeline;
using DataLoader.Core.Transforms;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.CME;

/// <summary>
/// One closed pipeline over the platform's standard ETL loop. The reader already
/// emits the sink's row type, so the transformer is the identity.
/// </summary>
public sealed class CmePipeline : LoaderPipelineBase<CmeWorkUnit, CmeFactRow, CmeFactRow>
{
    public CmePipeline(
        IWorkUnitProvider<CmeWorkUnit> provider,
        ISourceReader<CmeWorkUnit, CmeFactRow> source,
        ISink<CmeFactRow> sink,
        ILoadLogRepository loadLog,
        CmeSettings settings,
        ILogger logger)
        : base(CmeModule.Id, provider, source, new IdentityTransformer<CmeFactRow>(), sink, loadLog, settings, logger)
    {
    }
}

/// <summary>
/// Plugin entry point for the CME settlement-bulletin SFTP loader.
///
/// <para>
/// <b>What it loads.</b> CME publishes one fixed-width settlement bulletin per
/// trade date per exchange group on
/// <c>sftp.cmeprod.datahex.rozettatech.com</c>, laid out as
/// </para>
/// <code>
///   &lt;PRODUCT&gt;_&lt;EXCHANGE&gt; / EOD_&lt;EXCHANGE&gt; / yyyy / MM / dd / &lt;EXCHANGE&gt;_yyyyMMdd.txt
///   BAS_STLAGS         / EOD_STLAGS   / 2026 / 09 / 04 / STLAGS_20260904.txt
/// </code>
/// <para>
/// Each bulletin interleaves futures and options sections; a section whose
/// product line ends in <c>CALL</c> or <c>PUT</c> is an option and lands in
/// <c>arm.STLBASIC_Option</c>, everything else in <c>arm.STLBASIC_Future</c>. The
/// account carried 8 feeds × 10 business days when this loader was built, which
/// is why the loader simply loads everything it finds instead of walking a date
/// window.
/// </para>
/// <para>
/// <b>ONE pipeline, not one per feed.</b> The other multi-feed loaders in this
/// repo (EOX, Argus, ICE) give each feed its own pipeline because each feed has
/// its own table and column list. Here all 8 feeds share the same two tables and
/// the same parser, and every feed's files come from ONE directory walk — so a
/// single pipeline over (feed × date) work units is both simpler and cheaper.
/// Feed isolation is preserved where it matters: a failing bulletin fails only
/// its own work unit, and the other feeds' units carry on.
/// </para>
/// <para>
/// Services are built explicitly (the OPIS/Argus/EOX posture) rather than via
/// open-generic DI, so this loader's <c>IWorkUnitProvider&lt;T&gt;</c> and
/// <c>ISink&lt;T&gt;</c> registrations cannot collide with another loader's in the
/// shared container.
/// </para>
/// </summary>
public sealed class CmeModule : ILoaderModule
{
    public const string Id = "CME";

    public string LoaderId => Id;
    public string DisplayName => "CME settlement bulletins (SFTP, futures and options)";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        // AddLoaderSettings (not a bare Configure<>) — this is what wires the SEE_DB
        // resolver that turns the "SEE_DB" Username/Password sentinels into real
        // values from core.Param at run time.
        services.AddLoaderSettings<CmeSettings>(configuration, Id);

        services.AddSingleton<ICmeSftp, CmeSftpFileSystem>();
        services.AddSingleton<ICmeFileLog, SqlCmeFileLog>();

        // ONE walk of the drop per run, shared by every work unit.
        services.AddSingleton<CmeDiscoveryCache>();
        services.AddSingleton<CmeLoadValidator>();

        // Registered (rather than newed in the factory) so RunAsync can read back
        // what this run's own enumeration saw, for validation scoping and the
        // missing-feed warning, without walking the drop a second time.
        services.AddSingleton<CmeWorkUnitProvider>(sp => new CmeWorkUnitProvider(
            sp.GetRequiredService<CmeDiscoveryCache>(),
            sp.GetRequiredService<IOptions<CmeSettings>>().Value,
            sp.GetRequiredService<ILoggerFactory>().CreateLogger("CME.Units")));

        services.AddSingleton<CmePipeline>(BuildPipeline);
    }

    private static CmePipeline BuildPipeline(IServiceProvider sp)
    {
        var options = sp.GetRequiredService<IOptions<CmeSettings>>();
        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();

        var source = new CmeSourceReader(
            sp.GetRequiredService<ICmeSftp>(),
            sp.GetRequiredService<ICmeFileLog>(),
            options,
            loggerFactory.CreateLogger("CME.Reader"));

        var sink = new CmeFactSink(
            options.Value.ConnectionString,
            options.Value.MergeBatchSize,
            loggerFactory.CreateLogger("CME.Sink"));

        return new CmePipeline(
            sp.GetRequiredService<CmeWorkUnitProvider>(),
            source,
            sink,
            sp.GetRequiredService<ILoadLogRepository>(),
            options.Value,
            loggerFactory.CreateLogger("CME.Pipeline"));
    }

    public async Task<LoaderRunResult> RunAsync(IServiceProvider services, LoaderRunContext context)
    {
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("CME.Module");
        var settings = services.GetRequiredService<IOptions<CmeSettings>>().Value;

        // The descriptor registry is the contract for two TVPs. A bad edit there
        // would otherwise surface as a server-side type error mid-run, or worse, as
        // silently shifted columns — so fail before touching the network.
        var problems = CmeDescriptors.Validate();
        if (problems.Count > 0)
        {
            var message = "CME descriptor registry is invalid: " + string.Join("; ", problems);
            logger.LogCritical("{Message}", message);
            return LoaderRunResult.Failed(Id, message, TimeSpan.Zero);
        }

        WarnAboutConfiguration(settings, logger);

        var pipeline = services.GetRequiredService<CmePipeline>();
        var result = await pipeline.ExecuteAsync(context).ConfigureAwait(false);

        var provider = services.GetRequiredService<CmeWorkUnitProvider>();
        WarnAboutMissingFeeds(provider, logger);

        // Post-load validation is observational and must not change the run's
        // outcome — CmeLoadValidator swallows its own failures. Scope it with the
        // newest trade date this run's own enumeration saw (null when nothing ran,
        // which validates the whole table) — no second directory walk.
        await services.GetRequiredService<CmeLoadValidator>()
            .ValidateAsync(provider.LastEnumeratedMaxDate, context.CancellationToken)
            .ConfigureAwait(false);

        logger.LogInformation(
            "CME run complete: {Succeeded} succeeded, {Skipped} skipped, {Failed} failed, {Records} record(s)",
            result.WorkUnitsSucceeded, result.WorkUnitsSkipped, result.WorkUnitsFailed, result.RecordsProcessed);

        return result;
    }

    /// <summary>
    /// A feed that exists in <see cref="CmeDescriptors.KnownFeedIds"/> but produced
    /// no work units this run is worth a line: the usual cause is an entitlement
    /// change or a rotated folder name, and silence would look identical to
    /// "nothing new to load".
    /// </summary>
    private static void WarnAboutMissingFeeds(CmeWorkUnitProvider provider, ILogger logger)
    {
        var seen = provider.LastEnumeratedFeeds;
        if (seen.Count == 0) return; // already warned by the provider

        var missing = CmeDescriptors.KnownFeedIds
            .Where(id => !seen.Contains(id, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (missing.Count > 0)
            logger.LogWarning(
                "CME: {Count} previously-seen feed(s) produced no bulletins this run: {Feeds}. " +
                "Check the account's entitlements and whether the folder names changed",
                missing.Count, string.Join(", ", missing));

        var unexpected = seen
            .Where(id => !CmeDescriptors.KnownFeedIds.Contains(id, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (unexpected.Count > 0)
            logger.LogInformation(
                "CME: {Count} feed(s) not in the known list were discovered and loaded: {Feeds}. " +
                "This is expected when the account gains entitlements; the parser is feed-agnostic except for " +
                "tick notation, which is configured per exchange code",
                unexpected.Count, string.Join(", ", unexpected));
    }

    /// <summary>
    /// Startup warnings for configurations that are legal but nearly always a
    /// mistake, or that change behaviour enough to be worth seeing in the run log
    /// rather than only in appsettings.
    /// </summary>
    private static void WarnAboutConfiguration(CmeSettings settings, ILogger logger)
    {
        // A typo in EnabledFeeds would otherwise silently skip a feed. Warn rather
        // than throw — a stale config entry should not stop the whole loader.
        foreach (var id in settings.EnabledFeeds)
            if (!CmeDescriptors.KnownFeedIds.Contains(id, StringComparer.OrdinalIgnoreCase))
                logger.LogWarning(
                    "CME: EnabledFeeds contains '{FeedId}', which is not a feed id seen on this account. " +
                    "If it is not discovered on the drop this run, nothing will load for it",
                    id);

        if (CmeTime.UsingUtcFallback)
            logger.LogWarning(
                "CME: no US Central time-zone entry on this host — the trade-date window falls back to UTC, so " +
                "'today' arrives up to 6 hours early and the hot/settled split shifts by a day");

        if (settings.SettledAfterDays < 0)
            logger.LogWarning("CME: SettledAfterDays={Days} is negative — clamped to 0", settings.SettledAfterDays);

        if (settings.ForceReprocess)
            logger.LogWarning(
                "CME: ForceReprocess is ON — every discovered bulletin re-downloads and re-merges, regardless of " +
                "whether the file changed");

        if (settings.StrictLineParsing)
            logger.LogWarning(
                "CME: StrictLineParsing is ON — a single unclassifiable line fails the whole bulletin. Note that " +
                "STLEQT legitimately contains one such line (the 'KYP11 <br />' artifact), so STLEQT is expected " +
                "to fail while this is on");

        if (settings.MergeBatchSize is > 0 and < 1000)
            logger.LogInformation(
                "CME: MergeBatchSize={Size} is small; a bulletin of ~150k rows will take ~{Calls} merge calls",
                settings.MergeBatchSize, 150000 / Math.Max(1, settings.MergeBatchSize));

        if (string.IsNullOrWhiteSpace(settings.ConnectionString))
            logger.LogError("CME: ConnectionString is empty — every merge will fail");
    }
}
