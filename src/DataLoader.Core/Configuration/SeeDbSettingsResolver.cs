using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.Core.Configuration;

/// <summary>
/// Post-configures a bound <typeparamref name="TSettings"/> so that any
/// <see cref="string"/> property whose value is the sentinel <c>SEE_DB</c> is
/// replaced with the value stored in the platform DB (via
/// <see cref="ISeeDbParamStore"/> / <c>core.usp_GetParam</c>).
///
/// This runs as an <see cref="IPostConfigureOptions{TOptions}"/> once, at first
/// settings access (when the loader runs), after the section has been bound.
/// Loaders that don't use <c>SEE_DB</c> never touch
/// the store. The reflection / sentinel / fail-fast logic here is independent
/// of the DB, so it is unit-testable with a fake <see cref="ISeeDbParamStore"/>.
/// The resolved value is a secret and is never logged — only the property name.
/// </summary>
public sealed class SeeDbSettingsResolver<TSettings> : IPostConfigureOptions<TSettings>
    where TSettings : LoaderSettingsBase
{
    private const string Sentinel = "SEE_DB";

    private readonly string _loaderId;
    private readonly ISeeDbParamStore _store;
    private readonly ILogger<SeeDbSettingsResolver<TSettings>> _logger;

    public SeeDbSettingsResolver(
        string loaderId,
        ISeeDbParamStore store,
        ILogger<SeeDbSettingsResolver<TSettings>> logger)
    {
        _loaderId = loaderId;
        _store = store;
        _logger = logger;
    }

    public void PostConfigure(string? name, TSettings options)
    {
        // Writable string properties (this includes the inherited ConnectionString).
        var sentinelProperties = typeof(TSettings)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(string) && p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0)
            .Where(p => string.Equals((p.GetValue(options) as string)?.Trim(), Sentinel, StringComparison.Ordinal))
            .ToList();

        // No SEE_DB sentinels → don't hit the store at all.
        if (sentinelProperties.Count == 0)
        {
            return;
        }

        foreach (var property in sentinelProperties)
        {
            var resolved = _store.GetParam(_loaderId, property.Name);
            if (string.IsNullOrWhiteSpace(resolved))
            {
                throw new InvalidOperationException(
                    $"Loader '{_loaderId}' setting '{property.Name}' is set to SEE_DB but " +
                    $"core.usp_GetParam('{_loaderId}','{property.Name}') returned no value.");
            }

            property.SetValue(options, resolved);
            _logger.LogInformation(
                "Resolved loader {LoaderId} setting {Param} from core.Param", _loaderId, property.Name);
        }
    }
}
