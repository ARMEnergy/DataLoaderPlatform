namespace DataLoader.Core.Configuration;

/// <summary>
/// Resolves a single loader parameter value from the platform database.
///
/// Backs the "SEE_DB" config-indirection mechanism: a loader setting whose
/// bound value is the sentinel string <c>SEE_DB</c> is looked up at startup
/// via <c>core.usp_GetParam @LoaderName, @ParamName</c> so secrets (API keys,
/// connection strings, …) live in the platform DB rather than in config files.
/// </summary>
public interface ISeeDbParamStore
{
    /// <summary>
    /// Returns the stored value for <paramref name="loaderName"/> /
    /// <paramref name="paramName"/>, or <c>null</c> when no such row exists.
    /// Synchronous by design — it runs once, at startup, during
    /// post-configuration.
    /// </summary>
    string? GetParam(string loaderName, string paramName);
}
