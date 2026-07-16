using Microsoft.Extensions.DependencyInjection;

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
}
