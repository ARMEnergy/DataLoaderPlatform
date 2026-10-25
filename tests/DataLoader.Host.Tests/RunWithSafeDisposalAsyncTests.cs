using DataLoader.Core.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DataLoader.Host.Tests;

/// <summary>
/// Covers the regression a code review caught in the notification/watchdog
/// wiring: container disposal used to run INSIDE the same try that reports
/// startup failures, so a disposal exception — thrown AFTER the real run had
/// already produced its exit code — relabelled a successful run as a startup
/// failure, fired a false-alarm email, and overwrote a correct 0 with 1.
///
/// <para>Several loaders hold disposable network clients that can throw from
/// Dispose after a dropped connection — Marex's SignalR hub, the FTP/SFTP
/// clients in Argus/OPIS/CME — so this is not hypothetical.</para>
///
/// <para>Drives the real <see cref="Program.BuildServiceProvider"/> with a fake
/// loader module registering a singleton whose <c>DisposeAsync</c> throws,
/// rather than asserting against a hand-rolled try/finally that is not the
/// code actually running in production.</para>
/// </summary>
public class RunWithSafeDisposalAsyncTests
{
    /// <summary>
    /// Stands in for a loader's own disposable network client (Marex's
    /// SignalR hub, an FTP/SFTP client) that can throw when a dropped
    /// connection surfaces at disposal time.
    /// </summary>
    private sealed class ThrowingDisposable : IAsyncDisposable
    {
        public ValueTask DisposeAsync() =>
            throw new InvalidOperationException("Simulated dropped connection on dispose.");
    }

    /// <summary>
    /// A minimal loader module whose only purpose is registering the
    /// throwing singleton above. <see cref="RunAsync"/> is never called by
    /// these tests — RunWithSafeDisposalAsync never resolves a loader module
    /// itself, it only runs the supplied body and then disposes the container.
    /// </summary>
    private sealed class FakeModuleWithThrowingDisposableSingleton : ILoaderModule
    {
        public string LoaderId => "FakeThrowingDispose";
        public string DisplayName => "Fake (throws on dispose)";

        public void RegisterServices(IServiceCollection services, IConfiguration configuration) =>
            services.AddSingleton<ThrowingDisposable>();

        public Task<LoaderRunResult> RunAsync(IServiceProvider services, LoaderRunContext context) =>
            throw new NotSupportedException("Not exercised by these tests.");
    }

    private static ServiceProvider BuildServicesWithThrowingDisposable()
    {
        var configuration = new ConfigurationBuilder().Build();
        var modules = new ILoaderModule[] { new FakeModuleWithThrowingDisposableSingleton() };

        var services = Program.BuildServiceProvider(
            configuration, modules, Array.Empty<string>(), NullLogger.Instance);

        // Force the singleton to be CREATED, the way a loader's own RunAsync
        // would resolve it mid-run. An unresolved singleton registration is
        // never instantiated, so DisposeAsync would never visit it and this
        // test would prove nothing.
        services.GetRequiredService<ThrowingDisposable>();

        return services;
    }

    [Fact]
    public async Task DisposalThrowing_DoesNotChangeASuccessfulExitCode()
    {
        var services = BuildServicesWithThrowingDisposable();

        var exitCode = await Program.RunWithSafeDisposalAsync(
            services, () => Task.FromResult(0), NullLogger.Instance);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task DisposalThrowing_DoesNotChangeAFailedExitCode()
    {
        // The double-notification case the reviewer flagged: a failing run
        // (already reported by the body itself) followed by a failing
        // disposal must stay ONE reported failure, not become a second one
        // naming the disposal exception instead of the real cause.
        var services = BuildServicesWithThrowingDisposable();

        var exitCode = await Program.RunWithSafeDisposalAsync(
            services, () => Task.FromResult(1), NullLogger.Instance);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task DisposalThrowing_DoesNotMaskAnExceptionFromTheBody()
    {
        // Swallowing the disposal exception must not turn into swallowing a
        // REAL exception from the body — only disposal failures are
        // defended against here.
        var services = BuildServicesWithThrowingDisposable();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Program.RunWithSafeDisposalAsync(
                services,
                () => throw new InvalidOperationException("the body's own failure"),
                NullLogger.Instance));
    }
}
