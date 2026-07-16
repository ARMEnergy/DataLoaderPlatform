using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DataLoader.Core.Abstractions;

/// <summary>
/// The seam between the host and a loader plugin.
///
/// Each loader assembly exposes exactly one implementation of this interface.
/// The host discovers them all, builds one DI container by calling
/// <see cref="RegisterServices"/> on each, and then runs each via
/// <see cref="RunAsync"/>.
///
/// Implementations must be parameterless-constructible — the host instantiates
/// them with <c>Activator.CreateInstance</c> before the DI container exists.
/// Hold no state in the module itself; everything goes through DI.
/// </summary>
public interface ILoaderModule
{
    /// <summary>
    /// Stable identifier used in configuration, logs, and the load-log table.
    /// Must be unique across all loaders running on the platform.
    /// </summary>
    string LoaderId { get; }

    /// <summary>
    /// Human-readable name for log output.
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Register all services the loader needs: settings, sources, sinks,
    /// the pipeline, work-unit providers, HTTP clients, etc.
    ///
    /// Loaders should namespace their registrations — use keyed services or
    /// loader-specific interfaces so two loaders never collide on the same
    /// service type. The <see cref="Hosting.LoaderServiceCollectionExtensions"/>
    /// helpers make this easy.
    /// </summary>
    void RegisterServices(IServiceCollection services, IConfiguration configuration);

    /// <summary>
    /// Run one pass of the loader. The host passes its root
    /// <see cref="IServiceProvider"/>; the loader is expected to resolve its
    /// own pipeline (or anything else it registered) from it.
    /// </summary>
    Task<LoaderRunResult> RunAsync(IServiceProvider services, LoaderRunContext context);
}
