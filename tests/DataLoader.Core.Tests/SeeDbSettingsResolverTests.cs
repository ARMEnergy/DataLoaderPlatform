using DataLoader.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DataLoader.Core.Tests;

/// <summary>
/// Unit tests for <see cref="SeeDbSettingsResolver{TSettings}"/> — the pure,
/// reflection-based "SEE_DB" config-indirection logic. No database is touched:
/// the resolver takes an <see cref="ISeeDbParamStore"/>, and every test feeds it
/// a hand-written <see cref="FakeParamStore"/> that records its calls and returns
/// scripted values. Each test builds its own settings + fake, so tests share no
/// state and can run in any order / in parallel.
///
/// Covered behaviors:
///   1. A "SEE_DB" property is replaced with the store value; the store is called
///      with the loader id and the exact property NAME.
///   2. Multiple sentinels each resolve; a non-sentinel string is left
///      byte-for-byte (same reference) unchanged; a non-string property is untouched.
///   3. The inherited <see cref="LoaderSettingsBase.ConnectionString"/> resolves.
///   4. Fail-fast: store returns null / "" / all-whitespace -> InvalidOperationException,
///      and the (would-be) value is never echoed into the message.
///   5. Zero writable sentinels -> zero store calls (no DB hit; read-only sentinel ignored).
///   6. Sentinel matching is trim-then-ordinal-exact: "  SEE_DB  " matches;
///      "see_db" / "SEE_DB_X" / etc. do not.
///   7. The resolved secret VALUE is never written to the logger (only the name).
/// </summary>
public class SeeDbSettingsResolverTests
{
    private const string LoaderId = "TestLoader";

    // ---------------------------------------------------------------------
    // Test doubles
    // ---------------------------------------------------------------------

    /// <summary>Settings under test: a mix of writable strings, a non-string, and a read-only string.</summary>
    private sealed class TestSettings : LoaderSettingsBase
    {
        public string ApiKey { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;

        /// <summary>An ordinary string that is never the sentinel in these tests.</summary>
        public string Endpoint { get; set; } = string.Empty;

        /// <summary>Non-string property: must be ignored by the resolver entirely.</summary>
        public int Port { get; set; } = 5432;

        /// <summary>
        /// Read-only string whose value IS the sentinel. Because it has no setter the
        /// resolver must skip it (never call the store, never attempt to write it).
        /// </summary>
        public string ReadOnlySentinel { get; } = "SEE_DB";
    }

    /// <summary>
    /// Records every (loaderName, paramName) call and returns scripted values.
    /// Unscripted params return null. A scripted value may itself be null/""/"   "
    /// to exercise the fail-fast paths.
    /// </summary>
    private sealed class FakeParamStore : ISeeDbParamStore
    {
        private readonly Dictionary<string, string?> _scripted = new(StringComparer.Ordinal);

        public List<(string LoaderName, string ParamName)> Calls { get; } = new();

        public FakeParamStore Script(string paramName, string? value)
        {
            _scripted[paramName] = value;
            return this;
        }

        public string? GetParam(string loaderName, string paramName)
        {
            Calls.Add((loaderName, paramName));
            return _scripted.TryGetValue(paramName, out var v) ? v : null;
        }
    }

    /// <summary>Captures formatted log messages so tests can assert what was (and was not) logged.</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = new();

        IDisposable? ILogger.BeginScope<TState>(TState state) => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }

    private static SeeDbSettingsResolver<TestSettings> NewResolver(
        ISeeDbParamStore store,
        string loaderId = LoaderId,
        ILogger<SeeDbSettingsResolver<TestSettings>>? logger = null)
        => new(loaderId, store, logger ?? NullLogger<SeeDbSettingsResolver<TestSettings>>.Instance);

    // ---------------------------------------------------------------------
    // 1. Single sentinel resolves; store called with loader id + property NAME
    // ---------------------------------------------------------------------

    [Fact]
    public void PostConfigure_SentinelProperty_IsReplacedWithStoreValue_CalledWithLoaderIdAndPropertyName()
    {
        var store = new FakeParamStore().Script("ApiKey", "resolved-api-key");
        var settings = new TestSettings { ApiKey = "SEE_DB" };

        NewResolver(store).PostConfigure(Options.DefaultName, settings);

        Assert.Equal("resolved-api-key", settings.ApiKey);

        var call = Assert.Single(store.Calls);
        Assert.Equal(LoaderId, call.LoaderName);
        Assert.Equal("ApiKey", call.ParamName); // the property NAME, not the value/section path
    }

    // ---------------------------------------------------------------------
    // 2. Multiple sentinels resolve; non-sentinel string unchanged; non-string untouched
    // ---------------------------------------------------------------------

    [Fact]
    public void PostConfigure_MultipleSentinels_EachResolved_OtherPropertiesUntouched()
    {
        // Distinct, non-interned reference so we can prove it was never reassigned.
        var endpoint = string.Concat("https://", "api.example.test/v1");

        var store = new FakeParamStore()
            .Script("ApiKey", "the-key")
            .Script("Password", "the-password");

        var settings = new TestSettings
        {
            ApiKey = "SEE_DB",
            Password = "SEE_DB",
            Endpoint = endpoint, // ordinary value, must survive verbatim
            Port = 5432,         // non-string, must be ignored
        };

        NewResolver(store).PostConfigure(Options.DefaultName, settings);

        Assert.Equal("the-key", settings.ApiKey);
        Assert.Equal("the-password", settings.Password);

        // Non-sentinel string: byte-for-byte AND reference-identical (never touched).
        Assert.Same(endpoint, settings.Endpoint);
        // Non-string property: untouched.
        Assert.Equal(5432, settings.Port);

        // Exactly one store call per sentinel property; the non-sentinel string was never looked up.
        Assert.Equal(2, store.Calls.Count);
        Assert.Contains((LoaderId, "ApiKey"), store.Calls);
        Assert.Contains((LoaderId, "Password"), store.Calls);
        Assert.DoesNotContain(store.Calls, c => c.ParamName == "Endpoint");
        Assert.DoesNotContain(store.Calls, c => c.ParamName == "Port");
    }

    // ---------------------------------------------------------------------
    // 3. Inherited ConnectionString (from LoaderSettingsBase) resolves
    // ---------------------------------------------------------------------

    [Fact]
    public void PostConfigure_InheritedConnectionString_IsResolved()
    {
        const string resolvedCs = "Server=tcp:dbhost,1433;Database=Platform;Encrypt=True";
        var store = new FakeParamStore().Script("ConnectionString", resolvedCs);
        var settings = new TestSettings { ConnectionString = "SEE_DB" };

        NewResolver(store).PostConfigure(Options.DefaultName, settings);

        Assert.Equal(resolvedCs, settings.ConnectionString);

        // Only the inherited property was a sentinel (the read-only SEE_DB prop is ignored).
        var call = Assert.Single(store.Calls);
        Assert.Equal(LoaderId, call.LoaderName);
        Assert.Equal("ConnectionString", call.ParamName);
    }

    // ---------------------------------------------------------------------
    // 4. Fail-fast: no usable value from the store -> throws; value never leaked
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(null)]   // no row
    [InlineData("")]     // empty
    [InlineData("   ")]  // all-whitespace (the M1 fix: IsNullOrWhiteSpace, not IsNullOrEmpty)
    public void PostConfigure_StoreReturnsNoUsableValue_ThrowsAndDoesNotLeakValue(string? scriptedReturn)
    {
        var store = new FakeParamStore().Script("ApiKey", scriptedReturn);
        var settings = new TestSettings { ApiKey = "SEE_DB" };

        var ex = Assert.Throws<InvalidOperationException>(
            () => NewResolver(store).PostConfigure(Options.DefaultName, settings));

        // The store WAS consulted for this property.
        Assert.Contains((LoaderId, "ApiKey"), store.Calls);

        // Message is actionable: names the loader and the failing property.
        Assert.Contains(LoaderId, ex.Message);
        Assert.Contains("ApiKey", ex.Message);

        // The (would-be) resolved value must never be echoed into the message.
        if (!string.IsNullOrEmpty(scriptedReturn))
        {
            Assert.DoesNotContain(scriptedReturn, ex.Message);
        }

        // Resolution failed before the setter ran: the property still holds the sentinel.
        Assert.Equal("SEE_DB", settings.ApiKey);
    }

    // ---------------------------------------------------------------------
    // 5. No sentinels -> no DB hit at all (and read-only sentinel is ignored)
    // ---------------------------------------------------------------------

    [Fact]
    public void PostConfigure_NoWritableSentinels_MakesZeroStoreCalls()
    {
        var store = new FakeParamStore();
        var settings = new TestSettings
        {
            ApiKey = "plain-key",
            Password = "plain-password",
            Endpoint = "https://api.example.test/v1",
            ConnectionString = "Server=tcp:dbhost,1433;Database=Platform",
            Port = 1234,
            // ReadOnlySentinel == "SEE_DB" but is read-only, so it must NOT trigger a call.
        };

        NewResolver(store).PostConfigure(Options.DefaultName, settings);

        // The whole point: nothing was looked up.
        Assert.Empty(store.Calls);

        // Everything is exactly as supplied.
        Assert.Equal("plain-key", settings.ApiKey);
        Assert.Equal("plain-password", settings.Password);
        Assert.Equal("https://api.example.test/v1", settings.Endpoint);
        Assert.Equal("Server=tcp:dbhost,1433;Database=Platform", settings.ConnectionString);
        Assert.Equal(1234, settings.Port);
        Assert.Equal("SEE_DB", settings.ReadOnlySentinel); // read-only, left as-is
    }

    // ---------------------------------------------------------------------
    // 6a. Whitespace-padded sentinel IS the sentinel (resolver trims first)
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("  SEE_DB  ")]
    [InlineData("\tSEE_DB\t")]
    [InlineData("SEE_DB ")]
    [InlineData(" SEE_DB")]
    public void PostConfigure_WhitespacePaddedSentinel_IsResolved(string sentinelValue)
    {
        var store = new FakeParamStore().Script("ApiKey", "resolved");
        var settings = new TestSettings { ApiKey = sentinelValue };

        NewResolver(store).PostConfigure(Options.DefaultName, settings);

        Assert.Equal("resolved", settings.ApiKey);
        Assert.Contains((LoaderId, "ApiKey"), store.Calls);
    }

    // ---------------------------------------------------------------------
    // 6b. Anything that is not the exact ordinal token "SEE_DB" is NOT resolved
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("see_db")]      // different case: ordinal comparison is case-sensitive
    [InlineData("SEE_DB_X")]    // longer token
    [InlineData("XSEE_DB")]     // prefixed token
    [InlineData("SEE DB")]      // inner whitespace is not trimmed away
    [InlineData("SEEDB")]       // missing underscore
    [InlineData("")]            // empty
    public void PostConfigure_NonSentinelToken_IsLeftUnchanged_AndNotLookedUp(string value)
    {
        var store = new FakeParamStore().Script("ApiKey", "SHOULD-NOT-BE-USED");
        var settings = new TestSettings { ApiKey = value };

        NewResolver(store).PostConfigure(Options.DefaultName, settings);

        // Not a sentinel -> value untouched and the store was never consulted.
        Assert.Equal(value, settings.ApiKey);
        Assert.Empty(store.Calls);
    }

    // ---------------------------------------------------------------------
    // 7. The resolved secret value is never logged (only the property name)
    // ---------------------------------------------------------------------

    [Fact]
    public void PostConfigure_OnSuccess_LogsPropertyName_ButNeverTheResolvedSecret()
    {
        const string secret = "super-secret-value-9f3a";
        var store = new FakeParamStore().Script("ApiKey", secret);
        var settings = new TestSettings { ApiKey = "SEE_DB" };
        var logger = new CapturingLogger<SeeDbSettingsResolver<TestSettings>>();

        NewResolver(store, logger: logger).PostConfigure(Options.DefaultName, settings);

        Assert.Equal(secret, settings.ApiKey);
        Assert.NotEmpty(logger.Messages);
        Assert.Contains(logger.Messages, m => m.Contains("ApiKey"));
        Assert.DoesNotContain(logger.Messages, m => m.Contains(secret));
    }
}
