using System.Reflection;
using DataLoader.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace DataLoader.Core.Hosting;

/// <summary>
/// Finds <see cref="ILoaderModule"/> implementations.
///
/// Two ways to register loaders:
///   1. Reference the loader assemblies from the host project. They get
///      copied to the host's output directory and discovered automatically.
///   2. Pass a custom list to <see cref="ModuleDiscovery.Discover"/>.
///
/// Modules must have a public parameterless constructor — the host creates
/// them via reflection before the DI container exists.
/// </summary>
public static class ModuleDiscovery
{
    public static IReadOnlyList<ILoaderModule> Discover(ILogger logger, IReadOnlyList<Assembly>? assemblies = null)
    {
        assemblies ??= LoadAssemblies(logger);

        var moduleTypes = assemblies
            .SelectMany(SafeGetTypes)
            .Where(t => !t.IsAbstract
                     && !t.IsInterface
                     && typeof(ILoaderModule).IsAssignableFrom(t))
            .ToList();

        var modules = new List<ILoaderModule>();
        foreach (var t in moduleTypes)
        {
            try
            {
                if (Activator.CreateInstance(t) is ILoaderModule m)
                {
                    modules.Add(m);
                    logger.LogDebug("Discovered loader module {Module} ({LoaderId})", t.FullName, m.LoaderId);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not instantiate module {Type}", t.FullName);
            }
        }
        return modules;
    }

    /// <summary>
    /// Load every <c>DataLoader.*.dll</c> next to the host executable. This
    /// is the file-drop deployment model — drop a new loader DLL in and it
    /// gets picked up.
    /// </summary>
    private static IReadOnlyList<Assembly> LoadAssemblies(ILogger logger)
    {
        var loaded = AppDomain.CurrentDomain.GetAssemblies().ToList();
        var loadedPaths = new HashSet<string>(
            loaded.Where(a => !a.IsDynamic).Select(a => a.Location),
            StringComparer.OrdinalIgnoreCase);

        var baseDir = AppContext.BaseDirectory;
        foreach (var dll in Directory.EnumerateFiles(baseDir, "DataLoader.*.dll"))
        {
            if (loadedPaths.Contains(dll)) continue;
            try
            {
                loaded.Add(Assembly.LoadFrom(dll));
                logger.LogDebug("Loaded plugin assembly {Dll}", Path.GetFileName(dll));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not load assembly {Dll}", dll);
            }
        }
        return loaded;
    }

    private static IEnumerable<Type> SafeGetTypes(Assembly a)
    {
        try { return a.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t is not null)!; }
    }
}
