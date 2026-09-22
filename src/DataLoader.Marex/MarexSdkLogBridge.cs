using log4net.Appender;
using log4net.Core;
using log4net.Repository.Hierarchy;
using Microsoft.Extensions.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger;
using Neon.API.Implementation;
using SignalRClient.Contracts;

namespace DataLoader.Marex;

/// <summary>
/// Routes the Neon SDK's internal log4net output into the platform's <see cref="ILogger"/>.
///
/// <para>The SDK logs through log4net and, unconfigured, writes
/// <c>"WARNING: GetLogger called before Init"</c> to the console and then nothing —
/// so a connection problem inside the SDK is invisible. This bridge makes that output
/// available on demand (<see cref="MarexSettings.EnableSdkLogging"/>), while removing the
/// two things that make it unusable in production.</para>
///
/// <para><b>1. It redacts the connection config line.</b> On every <c>Connect</c> the SDK
/// logs its whole <c>NeonClientConfig</c> as JSON at INFO — <b>including the bearer
/// token in full</b>. That line is dropped entirely rather than pattern-scrubbed:
/// a scrubber that misses once leaks a live credential into the platform log, and the
/// line carries nothing that is not already logged by
/// <see cref="MarexSnapshotSession"/>.</para>
///
/// <para><b>2. It demotes the heartbeat.</b> The gateway heartbeats every 2 seconds and
/// the SDK logs each one at DEBUG; over a run that is noise that buries the useful
/// lines.</para>
///
/// <para>log4net repositories are per-assembly and global to the process, so this is
/// configured once and left alone. It is only ever attached when the setting asks for
/// it.</para>
/// </summary>
internal sealed class MarexSdkLogBridge : AppenderSkeleton
{
    private readonly ILogger _logger;

    private MarexSdkLogBridge(ILogger logger)
    {
        _logger = logger;
        Threshold = Level.All;
    }

    /// <summary>
    /// Attaches the bridge to the three vendored assemblies' log4net repositories.
    /// Safe to call more than once — the second call replaces nothing and adds nothing,
    /// because the repositories are already configured.
    /// </summary>
    internal static void Attach(ILogger logger)
    {
        var appender = new MarexSdkLogBridge(logger);
        appender.ActivateOptions();

        // One repository per assembly: Neon.API, Common.Core and SignalRClient each
        // call LogManager.GetLogger() against their own.
        var assemblies = new[]
        {
            typeof(NeonApiClient).Assembly,
            typeof(Common.Core.Utility.BoolResult<string>).Assembly,
            typeof(ProductDto).Assembly
        };

        foreach (var assembly in assemblies)
        {
            if (log4net.LogManager.GetRepository(assembly) is not Hierarchy hierarchy) continue;
            if (hierarchy.Root.Appenders.Contains(appender)) continue;

            hierarchy.Root.AddAppender(appender);
            hierarchy.Root.Level = Level.Debug;
            hierarchy.Configured = true;
            hierarchy.RaiseConfigurationChanged(EventArgs.Empty);
        }
    }

    protected override void Append(LoggingEvent loggingEvent)
    {
        var message = loggingEvent.RenderedMessage ?? string.Empty;

        // ⚠ Drops the line that echoes the whole NeonClientConfig, bearer token included.
        // Matched on the SDK's own wording; if a future SDK reworks it, the test
        // MarexSdkLogBridgeTests.Redacts_TheConfigurationLine fails rather than the
        // token quietly starting to appear in the platform log.
        if (message.Contains("starting with configuration", StringComparison.OrdinalIgnoreCase)) return;

        // Belt and braces: never emit anything carrying a JWT, whatever produced it.
        if (message.Contains("eyJ", StringComparison.Ordinal)) return;

        var level = loggingEvent.Level switch
        {
            _ when message.Contains("Heartbeat", StringComparison.OrdinalIgnoreCase) => LogLevel.Trace,
            var l when l >= Level.Error => LogLevel.Error,
            var l when l >= Level.Warn => LogLevel.Warning,
            var l when l >= Level.Info => LogLevel.Debug,   // the SDK is chatty at INFO
            _ => LogLevel.Trace
        };

        var error = loggingEvent.ExceptionObject;
        if (error is null)
            _logger.Log(level, "Marex SDK [{Logger}] {Message}", loggingEvent.LoggerName, message);
        else
            _logger.Log(level, error, "Marex SDK [{Logger}] {Message}", loggingEvent.LoggerName, message);
    }
}
