using DataLoader.Core.Abstractions;
using DataLoader.Core.Configuration;
using DataLoader.Core.Hosting;
using DataLoader.Core.Notifications;
using DataLoader.Core.Persistence;
using DataLoader.Core.Watchdog;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DataLoader.Host;

/// <summary>
/// The platform entry point.
///
/// One executable, many loaders. Three invocation modes:
///
///   DataLoader.Host.exe                      → run every loader listed in
///                                              Platform:EnabledLoaders
///   DataLoader.Host.exe LoaderId             → run just that loader (the
///                                              argument overrides config)
///   DataLoader.Host.exe LoaderA LoaderB ...  → run several specific loaders
///   DataLoader.Host.exe --watchdog           → run the absence watchdog
///                                              instead of any loader
///
/// The argument form is the one a scheduler should use: one scheduled task
/// per loader, each invoking this same .exe with a different loader id.
/// Concurrent invocations are safe — each is a separate OS process, and
/// the SQL overlap guard prevents two invocations of the same loader from
/// trampling each other.
///
/// Adding a new vendor never modifies this file.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var configuration = BuildConfiguration();

        using var bootstrapLoggerFactory = LoggerFactory.Create(b =>
        {
            b.AddConsole();
            b.AddConfiguration(configuration.GetSection("Logging"));
        });
        var bootstrapLogger = bootstrapLoggerFactory.CreateLogger("Bootstrap");

        // ⚠ Everything below is inside the try, deliberately. BuildServiceProvider
        // used to sit OUTSIDE it, so a DI or configuration failure threw straight
        // out of Main with no catch and no notification at all — the one failure
        // class that breaks DI was also the one nobody heard about.
        //
        // The bootstrap notifier is built straight from IConfiguration rather than
        // resolved, for the same reason: it has to work when the container does not.
        // It needs no secret, because the configured relay accepts none.
        try
        {
            var modules = ModuleDiscovery.Discover(bootstrapLogger);
            if (modules.Count == 0)
            {
                bootstrapLogger.LogError("No loader modules discovered — nothing to do");
                return 1;
            }
            bootstrapLogger.LogInformation(
                "Discovered {Count} loader modules: {Names}",
                modules.Count, string.Join(", ", modules.Select(m => m.LoaderId)));

            var parsed = HostArguments.Parse(args, modules.Select(m => m.LoaderId).ToList());
            if (!parsed.IsValid)
            {
                bootstrapLogger.LogError("{Error}", parsed.Error);
                return 1;
            }

            // ⚠ NOT `await using`. Disposal used to happen inside this same try,
            // which meant it ran AFTER a run had already produced its exit code —
            // so a disposal failure (observed: a singleton whose DisposeAsync
            // throws) landed in the catch below, got logged as a STARTUP failure,
            // fired a false-alarm email, and overwrote a successful 0 with 1.
            // Several loaders hold disposable network clients that can throw from
            // Dispose after a dropped connection: Marex's SignalR hub, the
            // FTP/SFTP clients in Argus/OPIS/CME. RunWithSafeDisposalAsync keeps
            // disposal from ever reaching this catch, whatever the body returned.
            var services = BuildServiceProvider(
                configuration, modules, parsed.Loaders, bootstrapLogger);

            return await RunWithSafeDisposalAsync(services, async () =>
            {
                using var cts = new CancellationTokenSource();

                if (parsed.WatchdogMode)
                {
                    var watchdog = services.GetRequiredService<WatchdogRunner>();
                    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
                    return await watchdog.RunAsync(cts.Token);
                }

                var host = services.GetRequiredService<PlatformHost>();
                var logger = services.GetRequiredService<ILogger<PlatformHost>>();

                Console.CancelKeyPress += (_, e) =>
                {
                    e.Cancel = true;
                    logger.LogWarning("Cancellation requested");
                    cts.Cancel();
                };

                try
                {
                    return await host.RunAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    logger.LogWarning("Run cancelled");
                    return 2;
                }
                catch (Exception ex)
                {
                    logger.LogCritical(ex, "Unhandled exception");
                    await NotifyBootstrapFailureAsync(configuration, bootstrapLogger, ex, "run").ConfigureAwait(false);
                    return 1;
                }
            }, bootstrapLogger).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            bootstrapLogger.LogWarning("Run cancelled during startup");
            return 2;
        }
        catch (Exception ex)
        {
            bootstrapLogger.LogCritical(ex, "Unhandled exception during startup");
            await NotifyBootstrapFailureAsync(configuration, bootstrapLogger, ex, "startup").ConfigureAwait(false);
            return 1;
        }
    }

    /// <summary>
    /// Reports a failure that happened outside — or while building — the DI
    /// container, so nothing can be resolved from it.
    ///
    /// <para>Builds its own notifier from <see cref="IConfiguration"/>. That is
    /// only possible because the configured relay takes no credential: a
    /// <c>SEE_DB</c> secret would need the database AND the container, neither
    /// of which is available on this path.</para>
    ///
    /// <para>Best-effort and silent on failure — this is the last thing that
    /// runs before a non-zero exit, and it must not replace the real error with
    /// one of its own. That covers <see cref="NotificationSettings.FromConfiguration"/>
    /// throwing too (it rejects stray <c>SmtpUsername</c>/<c>SmtpPassword</c> keys) —
    /// there is no container here to fall back on, so the only choice on this path
    /// is "try to tell someone" or "give up silently", and this catch makes it the
    /// former without turning a config mistake into a second unhandled exception.</para>
    /// </summary>
    private static async Task NotifyBootstrapFailureAsync(
        IConfiguration configuration, ILogger logger, Exception exception, string stage)
    {
        try
        {
            var settings = NotificationSettings.FromConfiguration(configuration);
            if (!settings.Enabled) return;
            settings.Validate();

            INotificationSender sender = new SmtpNotificationSender(settings);
            var notifier = new FailureNotifier(
                sender, settings, NullLogger<FailureNotifier>.Instance);

            var report = new RunFailureReport
            {
                HostName = Environment.MachineName,
                RunId = Guid.Empty,
                StartedAtUtc = DateTime.UtcNow,
                CompletedAtUtc = DateTime.UtcNow,
                Exception = exception,
                FailedStage = stage
            };

            await notifier.NotifyAsync(report, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not send the startup-failure notification");
        }
    }

    /// <summary>
    /// Runs <paramref name="body"/> and disposes <paramref name="services"/>
    /// afterwards, no matter how <paramref name="body"/> exits — but a disposal
    /// failure must NEVER overwrite the result <paramref name="body"/> already
    /// produced.
    ///
    /// <para>⚠ Before this existed, container disposal ran inside the SAME try
    /// that reports startup failures. Disposal happens AFTER the body has
    /// already produced its exit code, so a disposal exception there relabelled
    /// a successful run as a STARTUP failure, fired a false-alarm email, and
    /// flipped a correct 0 to 1 — the one outcome that would train people to
    /// stop trusting these emails, which is worse than sending none. Several
    /// loaders hold disposable network clients that can throw from Dispose
    /// after a dropped connection: Marex's SignalR hub, the FTP/SFTP clients in
    /// Argus/OPIS/CME.</para>
    ///
    /// <para>internal rather than private so <c>DataLoader.Host.Tests</c> (a
    /// friend assembly via <c>InternalsVisibleTo</c>) can prove the invariant
    /// directly against a real container built by <see cref="BuildServiceProvider"/>,
    /// with a fake loader module registering a singleton whose disposal throws,
    /// rather than re-deriving the same conclusion against a hand-rolled
    /// try/finally that is not the code actually running in production.</para>
    /// </summary>
    internal static async Task<int> RunWithSafeDisposalAsync(
        ServiceProvider services, Func<Task<int>> body, ILogger bootstrapLogger)
    {
        try
        {
            return await body().ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await services.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                bootstrapLogger.LogError(ex, "Container disposal failed; the run's own outcome is unchanged");
            }
        }
    }

    private static IConfiguration BuildConfiguration() =>
        new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables(prefix: "DATALOADER_")
            .Build();

    /// <summary>
    /// internal rather than private: <c>DataLoader.Host.Tests</c> (a friend
    /// assembly via <c>InternalsVisibleTo</c> in the csproj) resolves
    /// <see cref="PlatformHost"/> and <see cref="INotificationSender"/> out of a
    /// container built here to prove the notification degrade path in Step 1
    /// below is a real DI outcome, not just a unit test of the settings object in
    /// isolation. Nothing else about <c>Program</c> is exposed.
    /// </summary>
    internal static ServiceProvider BuildServiceProvider(
        IConfiguration configuration,
        IReadOnlyList<ILoaderModule> modules,
        IReadOnlyList<string> explicitLoaders,
        ILogger bootstrapLogger)
    {
        var services = new ServiceCollection();

        services.AddSingleton(configuration);
        services.Configure<PlatformSettings>(configuration.GetSection(PlatformSettings.SectionName));

        // If the caller named loaders on the command line, override the
        // EnabledLoaders list in the bound options. This is option (a) from
        // the design discussion: argument wins over config.
        if (explicitLoaders.Count > 0)
        {
            services.PostConfigure<PlatformSettings>(s =>
            {
                s.EnabledLoaders = explicitLoaders.ToList();
            });
        }

        services.AddLogging(b =>
        {
            b.AddConsole();
            b.AddConfiguration(configuration.GetSection("Logging"));
        });

        // One instance, two roles: the per-work-unit load log and the once-per-run sweeper that
        // closes rows an earlier run opened but never closed. Resolve the concrete type for both
        // so they genuinely share it rather than each getting their own.
        services.AddSingleton<SqlLoadLogRepository>();
        services.AddSingleton<ILoadLogRepository>(sp => sp.GetRequiredService<SqlLoadLogRepository>());
        services.AddSingleton<ILoadLogSweeper>(sp => sp.GetRequiredService<SqlLoadLogRepository>());
        services.AddSingleton<ILoaderOverlapGuard, SqlLoaderOverlapGuard>();
        services.AddSingleton<IAdditionalProcessRunner, SqlAdditionalProcessRunner>();
        services.AddSingleton<IReadOnlyList<ILoaderModule>>(modules);
        services.AddSingleton<PlatformHost>();

        // Notifications. The settings object is registered as a singleton
        // instance rather than through IOptions, because the bootstrap path in
        // Main builds its own copy the same way and the two must not diverge.
        //
        // ⚠ A BAD NOTIFICATION CONFIG MUST NEVER STOP THE LOADERS. Both
        // FromConfiguration (stray SmtpUsername/SmtpPassword keys) and
        // Validate() (a missing SmtpHost/From/To, a bad port) can throw, and an
        // uncaught throw here aborts BuildServiceProvider before
        // host.RunAsync() is ever reached — so one typo in a side-channel
        // feature would mean zero loaders ran. That is precisely the failure
        // mode this whole component exists to prevent, so a broken config in
        // EITHER place degrades to no notifications, loudly, rather than
        // taking the ETL down with it.
        NotificationSettings notificationSettings;
        bool notificationsUsable;
        try
        {
            notificationSettings = NotificationSettings.FromConfiguration(configuration);
            notificationsUsable = notificationSettings.Enabled;
        }
        catch (Exception ex)
        {
            bootstrapLogger.LogError(
                ex, "Notification config could not be bound — continuing with notifications DISABLED. " +
                    "Loaders will run normally, but NOBODY WILL BE TOLD IF THEY FAIL.");
            notificationSettings = new NotificationSettings();
            notificationsUsable = false;
        }

        if (notificationsUsable)
        {
            try
            {
                notificationSettings.Validate();
            }
            catch (Exception ex)
            {
                bootstrapLogger.LogError(
                    ex, "Notification config is invalid — continuing with notifications DISABLED. " +
                        "Loaders will run normally, but NOBODY WILL BE TOLD IF THEY FAIL.");
                notificationsUsable = false;
            }
        }

        services.AddSingleton(notificationSettings);

        if (notificationsUsable)
            services.AddSingleton<INotificationSender, SmtpNotificationSender>();
        else
            services.AddSingleton<INotificationSender, NullNotificationSender>();

        services.AddSingleton<IFailureNotifier, FailureNotifier>();

        // Watchdog.
        services.Configure<WatchdogSettings>(configuration.GetSection(WatchdogSettings.SectionName));
        services.AddSingleton<IWatchdogRepository, SqlWatchdogRepository>();
        services.AddSingleton<WatchdogRunner>();

        // Every discovered module gets to register its services. The host
        // only resolves the modules selected to run; the rest sit unused.
        foreach (var module in modules)
            module.RegisterServices(services, configuration);

        return services.BuildServiceProvider();
    }
}
