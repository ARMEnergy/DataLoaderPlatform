using DataLoader.Core.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.Core.Hosting;

/// <summary>
/// Helpers used inside <see cref="Abstractions.ILoaderModule.RegisterServices"/>
/// to register loader-local services without colliding with other loaders.
///
/// All loaders live in the same DI container, so naive
/// <c>services.AddSingleton&lt;IFoo, MyFoo&gt;()</c> registrations would let
/// two loaders' Foo implementations clash. Loaders should use keyed
/// registrations or unique types, and resolve their own services through
/// keyed APIs.
/// </summary>
public static class LoaderServiceCollectionExtensions
{
    /// <summary>
    /// Register a service keyed by loader id. Resolve from a module's
    /// <c>RunAsync</c> with <c>services.GetRequiredKeyedService&lt;T&gt;(loaderId)</c>.
    /// </summary>
    public static IServiceCollection AddLoaderKeyedSingleton<TService, TImplementation>(
        this IServiceCollection services, string loaderId)
        where TService : class
        where TImplementation : class, TService =>
        services.AddKeyedSingleton<TService, TImplementation>(loaderId);

    public static IServiceCollection AddLoaderKeyedSingleton<TService>(
        this IServiceCollection services, string loaderId,
        Func<IServiceProvider, object?, TService> factory)
        where TService : class =>
        services.AddKeyedSingleton<TService>(loaderId, (sp, key) => factory(sp, key));

    /// <summary>
    /// Binds a loader's settings from its <c>Loaders:{loaderId}</c> section and
    /// wires the "SEE_DB" config-indirection: after binding, any string setting
    /// whose value is the sentinel <c>SEE_DB</c> is resolved from the platform
    /// database via <c>core.usp_GetParam</c> (see
    /// <see cref="SeeDbSettingsResolver{TSettings}"/>). Call this from a module's
    /// <c>RegisterServices</c> in place of a bare
    /// <c>services.Configure&lt;TSettings&gt;(...)</c>.
    ///
    /// The shared <see cref="ISeeDbParamStore"/> is registered once
    /// (<see cref="ServiceCollectionDescriptorExtensions.TryAddSingleton{TService, TImplementation}"/>)
    /// so multiple loaders don't double-register it.
    /// </summary>
    public static IServiceCollection AddLoaderSettings<TSettings>(
        this IServiceCollection services, IConfiguration configuration, string loaderId)
        where TSettings : LoaderSettingsBase
    {
        services.TryAddSingleton<ISeeDbParamStore, SqlParamStore>();

        services.Configure<TSettings>(configuration.GetSection($"Loaders:{loaderId}"));

        // Plain AddSingleton (not TryAddEnumerable): TryAddEnumerable rejects a
        // factory-based descriptor whose implementation type it can't distinguish
        // (it throws "indistinguishable from other services"), and we need the
        // factory to inject the per-call loaderId. Each module registers exactly one
        // post-configure per unique TSettings; even a duplicate would be benign — the
        // second resolver sees the already-resolved (non-sentinel) value and no-ops.
        services.AddSingleton<IPostConfigureOptions<TSettings>>(sp =>
            new SeeDbSettingsResolver<TSettings>(
                loaderId,
                sp.GetRequiredService<ISeeDbParamStore>(),
                sp.GetRequiredService<ILogger<SeeDbSettingsResolver<TSettings>>>()));

        return services;
    }
}
