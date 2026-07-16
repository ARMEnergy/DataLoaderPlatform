using DataLoader.Core.Abstractions;
using DataLoader.Core.Configuration;
using DataLoader.Core.Hosting;
using DataLoader.Core.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

        // Bootstrap logger — used only to log discovery itself.
        using var bootstrapLoggerFactory = LoggerFactory.Create(b =>
        {
            b.AddConsole();
            b.AddConfiguration(configuration.GetSection("Logging"));
        });
        var bootstrapLogger = bootstrapLoggerFactory.CreateLogger("Bootstrap");

        // Step 1 — find every loader module shipped with this host.
        var modules = ModuleDiscovery.Discover(bootstrapLogger);
        if (modules.Count == 0)
        {
            bootstrapLogger.LogError("No loader modules discovered — nothing to do");
            return 1;
        }
        bootstrapLogger.LogInformation(
            "Discovered {Count} loader modules: {Names}",
            modules.Count, string.Join(", ", modules.Select(m => m.LoaderId)));

        // Step 2 — if any command-line arguments were passed, treat them as
        // explicit loader names that override Platform:EnabledLoaders.
        // Validate them up front so a typo at the scheduler doesn't silently
        // run nothing.
        var requestedLoaders = ParseLoaderArguments(args, modules, bootstrapLogger);
        if (requestedLoaders is null) return 1;   // bad argument — already logged

        // Step 3 — build the DI container with every discovered module's
        // registrations. Even loaders that won't run this invocation get
        // their services registered, which is harmless — they just don't
        // get resolved. This keeps the registration code simple and means
        // we don't have to instantiate-then-throw-away modules.
        await using var services = BuildServiceProvider(configuration, modules, requestedLoaders);

        var host = services.GetRequiredService<PlatformHost>();
        var logger = services.GetRequiredService<ILogger<PlatformHost>>();

        using var cts = new CancellationTokenSource();
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
            return 1;
        }
    }

    /// <summary>
    /// Parse the command-line arguments into a list of loader ids to run.
    /// Returns:
    ///   - empty list if no arguments were given (means "use config")
    ///   - non-empty list if arguments were given and every one matched a known loader
    ///   - null if an argument didn't match anything — caller should bail with exit code 1
    /// </summary>
    private static IReadOnlyList<string>? ParseLoaderArguments(
        string[] args, IReadOnlyList<ILoaderModule> modules, ILogger logger)
    {
        if (args.Length == 0) return Array.Empty<string>();

        // Match case-insensitively for scheduler-friendliness.
        var known = modules.ToDictionary(m => m.LoaderId, StringComparer.OrdinalIgnoreCase);
        var resolved = new List<string>();
        bool ok = true;
        foreach (var arg in args)
        {
            if (known.TryGetValue(arg, out var module))
            {
                resolved.Add(module.LoaderId);   // canonical casing
            }
            else
            {
                logger.LogError(
                    "Unknown loader '{Arg}'. Known: {Known}",
                    arg, string.Join(", ", known.Keys));
                ok = false;
            }
        }
        return ok ? resolved : null;
    }

    private static IConfiguration BuildConfiguration() =>
        new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables(prefix: "DATALOADER_")
            .Build();

    private static ServiceProvider BuildServiceProvider(
        IConfiguration configuration,
        IReadOnlyList<ILoaderModule> modules,
        IReadOnlyList<string> explicitLoaders)
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

        services.AddSingleton<ILoadLogRepository, SqlLoadLogRepository>();
        services.AddSingleton<ILoaderOverlapGuard, SqlLoaderOverlapGuard>();
        services.AddSingleton<IReadOnlyList<ILoaderModule>>(modules);
        services.AddSingleton<PlatformHost>();

        // Every discovered module gets to register its services. The host
        // only resolves the modules selected to run; the rest sit unused.
        foreach (var module in modules)
            module.RegisterServices(services, configuration);

        return services.BuildServiceProvider();
    }
}
