using System.ComponentModel;
using System.Diagnostics;
using DataLoader.Core.Configuration;
using DataLoader.Core.Hosting;
using DataLoader.Core.Resilience;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DataLoader.Core.Tests;

/// <summary>
/// Covers the fix for the 2026-11-03 production incident: Windows Update stalls
/// ARMH-OPSDB01 hard enough that Service Control Manager logs its own 30-second
/// timeouts, and any loader whose first TLS pre-login handshake lands inside the
/// stall died on ADO.NET's 15-second default connect timeout. SQL Server itself
/// never went down.
/// </summary>
public class SqlConnectionResilienceTests
{
    private static readonly SqlConnectionResiliencePolicy Fast =
        new() { MaxAttempts = 3, BaseDelay = TimeSpan.Zero };

    // ---------- connection-string defaults ----------

    [Fact]
    public void ApplyDefaults_InjectsConnectTimeout_WhenAbsent()
    {
        var result = SqlConnectionResilience.ApplyDefaults(
            "Server=S;Database=D;Integrated Security=SSPI;", 60);

        Assert.Equal(60, new SqlConnectionStringBuilder(result).ConnectTimeout);
    }

    [Fact]
    public void ApplyDefaults_KeepsOperatorSuppliedTimeout()
    {
        // An explicit value is a deliberate choice and must survive. Silently
        // overriding it is how a "safe default" becomes an unexplainable surprise.
        var result = SqlConnectionResilience.ApplyDefaults(
            "Server=S;Database=D;Connect Timeout=5;", 60);

        Assert.Equal(5, new SqlConnectionStringBuilder(result).ConnectTimeout);
    }

    [Fact]
    public void ApplyDefaults_KeepsOperatorSuppliedTimeout_UnderItsAliasSpelling()
    {
        // "Connection Timeout" is the same setting under a different name. Missing
        // the alias would silently overwrite a value the operator did set.
        var result = SqlConnectionResilience.ApplyDefaults(
            "Server=S;Database=D;Connection Timeout=7;", 60);

        Assert.Equal(7, new SqlConnectionStringBuilder(result).ConnectTimeout);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("this is not a connection string = = =")]
    public void ApplyDefaults_ReturnsUnusableInputUnchanged(string input)
    {
        // This runs during startup options post-configuration. The useful error is
        // the one the connection attempt raises (naming the server), not a parser
        // exception thrown from a defaults helper.
        Assert.Equal(input, SqlConnectionResilience.ApplyDefaults(input, 60));
    }

    [Fact]
    public void ApplyDefaults_HandlesNull()
    {
        Assert.Equal(string.Empty, SqlConnectionResilience.ApplyDefaults(null, 60));
    }

    [Fact]
    public void ApplyDefaults_PreservesEveryOtherSetting_OnARealConnectionString()
    {
        // Round-tripping through SqlConnectionStringBuilder REWRITES keys to their
        // canonical spellings — Server becomes Data Source, Database becomes Initial
        // Catalog, and "Integrated Security=SSPI" becomes "Integrated Security=True".
        // All 24 of this platform's connection strings use the short spellings, so this
        // pins that the rewrite preserves meaning rather than silently dropping, say,
        // TrustServerCertificate and turning every connection into a TLS trust failure.
        var result = SqlConnectionResilience.ApplyDefaults(
            "Server=ARMH-OPSDB01;Database=DataLoaderPlatform;" +
            "Integrated Security=SSPI;TrustServerCertificate=True;", 60);

        var b = new SqlConnectionStringBuilder(result);
        Assert.Equal(60, b.ConnectTimeout);
        Assert.Equal("ARMH-OPSDB01", b.DataSource);
        Assert.Equal("DataLoaderPlatform", b.InitialCatalog);
        Assert.True(b.IntegratedSecurity);
        Assert.True(b.TrustServerCertificate);
    }

    // ---------- transient classification ----------

    [Fact]
    public void PreLoginHandshakeWin32_258_IsTransient_ViaInnerChain()
    {
        // THE production case. The SNI layer reports "the wait operation timed out"
        // through an inner Win32Exception, not through SqlException.Number — so
        // classifying on the number alone misses the exact error this exists for.
        var root = new InvalidOperationException("pre-login handshake",
            new Win32Exception(258));

        Assert.True(SqlConnectionResilience.IsTransientFailure(0, root));
    }

    [Fact]
    public void ClientTimeout_MinusTwo_IsTransient()
    {
        Assert.True(SqlConnectionResilience.IsTransientFailure(-2, null));
    }

    [Fact]
    public void LoginFailed_IsNotTransient()
    {
        // Retrying a bad password is a good way to lock out the service account.
        Assert.False(SqlConnectionResilience.IsTransientFailure(18456, null));
    }

    [Fact]
    public void PermanentNumber_WinsOverTransientInnerException()
    {
        // Ordering matters: the permanent check runs FIRST. A login failure that
        // happens to carry a socket-ish inner exception must still fail fast.
        var root = new InvalidOperationException("login failed", new Win32Exception(258));

        Assert.False(SqlConnectionResilience.IsTransientFailure(18456, root));
    }

    [Fact]
    public void UnknownNumber_WithNoTransientInner_IsNotTransient()
    {
        Assert.False(SqlConnectionResilience.IsTransientFailure(
            50000, new InvalidOperationException("boom")));
    }

    [Fact]
    public void IsTransientConnectFailure_IgnoresNonSqlExceptions()
    {
        Assert.False(SqlConnectionResilience.IsTransientConnectFailure(
            new InvalidOperationException("not a sql error")));
        Assert.False(SqlConnectionResilience.IsTransientConnectFailure(null));
    }

    // ---------- the retry loop ----------

    private static Func<CancellationToken, Task> FailsThenSucceeds(
        int failures, Exception error, Counter counter) =>
        _ =>
        {
            counter.Attempts++;
            if (counter.Attempts <= failures) throw error;
            return Task.CompletedTask;
        };

    private sealed class Counter { public int Attempts; }

    [Fact]
    public async Task RetryAsync_SucceedsFirstTime_MakesOneAttempt()
    {
        var c = new Counter();
        await SqlConnectionResilience.RetryAsync(
            FailsThenSucceeds(0, new Exception(), c), _ => true, Fast, CancellationToken.None);

        Assert.Equal(1, c.Attempts);
    }

    [Fact]
    public async Task RetryAsync_RetriesTransientFailures_UntilSuccess()
    {
        var c = new Counter();
        await SqlConnectionResilience.RetryAsync(
            FailsThenSucceeds(2, new InvalidOperationException(), c),
            _ => true, Fast, CancellationToken.None);

        Assert.Equal(3, c.Attempts);
    }

    [Fact]
    public async Task RetryAsync_RethrowsAfterMaxAttempts()
    {
        var c = new Counter();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SqlConnectionResilience.RetryAsync(
                FailsThenSucceeds(99, new InvalidOperationException("still down"), c),
                _ => true, Fast, CancellationToken.None));

        Assert.Equal(3, c.Attempts);
        Assert.Equal("still down", ex.Message);
    }

    [Fact]
    public async Task RetryAsync_DoesNotRetry_NonTransientFailure()
    {
        var c = new Counter();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SqlConnectionResilience.RetryAsync(
                FailsThenSucceeds(99, new InvalidOperationException(), c),
                _ => false, Fast, CancellationToken.None));

        Assert.Equal(1, c.Attempts);
    }

    [Fact]
    public async Task RetryAsync_MaxAttemptsOne_DisablesRetrying()
    {
        var c = new Counter();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SqlConnectionResilience.RetryAsync(
                FailsThenSucceeds(99, new InvalidOperationException(), c),
                _ => true,
                new SqlConnectionResiliencePolicy { MaxAttempts = 1, BaseDelay = TimeSpan.Zero },
                CancellationToken.None));

        Assert.Equal(1, c.Attempts);
    }

    [Fact]
    public async Task RetryAsync_BackoffGrows_BetweenAttempts()
    {
        var c = new Counter();
        var sw = Stopwatch.StartNew();

        await SqlConnectionResilience.RetryAsync(
            FailsThenSucceeds(2, new InvalidOperationException(), c),
            _ => true,
            new SqlConnectionResiliencePolicy
            {
                MaxAttempts = 3,
                BaseDelay = TimeSpan.FromMilliseconds(50)
            },
            CancellationToken.None);

        sw.Stop();
        // 50ms before attempt 2, then 150ms before attempt 3. Asserting the lower
        // bound only — a loaded CI box may take longer, but it can never take less.
        Assert.Equal(3, c.Attempts);
        Assert.True(sw.ElapsedMilliseconds >= 200,
            $"expected at least 200ms of backoff, took {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task RetryAsync_DoesNotRetry_WhenTokenAlreadyCancelled()
    {
        // A cancelled token means the work unit's own budget expired. Retrying there
        // would quietly extend a deadline somebody else chose.
        var c = new Counter();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SqlConnectionResilience.RetryAsync(
                FailsThenSucceeds(99, new InvalidOperationException(), c),
                _ => true, Fast, cts.Token));

        Assert.Equal(1, c.Attempts);
    }

    // ---------- budget ----------

    [Fact]
    public void WorstCaseOpenBudget_StaysBounded()
    {
        // ⚠ Deliberately NOT compared against each loader's WorkUnitTimeoutSeconds.
        // Ten of 23 loaders ship a unit budget at or below this worst case, and that
        // is correct rather than misconfigured: the unit budget cancels the token and
        // simply wins when it is lower, exactly as documented for
        // SqlSinkBase.CommandTimeoutSeconds. What this guards is the OTHER direction —
        // the startup connection is NOT inside any work unit, so nothing but these
        // numbers bounds it. Raising them carelessly would hang a scheduled task for
        // an unbounded time with no output at all.
        var p = new SqlConnectionResiliencePolicy();

        var worstCase = TimeSpan.FromSeconds(p.MaxAttempts * p.ConnectTimeoutSeconds)
                        + TimeSpan.FromTicks(p.BaseDelay.Ticks * 4); // 1x + 3x backoff

        Assert.True(worstCase <= TimeSpan.FromMinutes(10),
            $"worst-case open budget is {worstCase.TotalSeconds:N0}s");

        // The stalls this survives outlast one attempt (SCM timeouts were logged at
        // 05:02:38, 05:03:08 and 05:03:38 on 2026-11-02), so a single try is not enough.
        Assert.True(p.MaxAttempts >= 2);
        Assert.True(p.ConnectTimeoutSeconds >= 30);
    }

    // ---------- wiring ----------

    private sealed class FakeParamStore : ISeeDbParamStore
    {
        public string? GetParam(string loaderName, string paramName) =>
            "Server=Resolved;Database=D;Integrated Security=SSPI;";
    }

    private sealed class TestSettings : LoaderSettingsBase { }

    [Fact]
    public void AddLoaderSettings_NormalisesConnectionString_AfterSeeDbResolution()
    {
        // ⚠ The ordering trap. If normalisation ran before the SEE_DB resolver it
        // would parse the literal "SEE_DB", inject nothing, and leave the real
        // connection string — fetched a moment later — on the 15-second default.
        var services = new ServiceCollection();
        services.AddSingleton<ISeeDbParamStore>(new FakeParamStore());
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>),
            typeof(NullLogger<>));

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Loaders:Test:ConnectionString"] = "SEE_DB"
            })
            .Build();

        services.AddLoaderSettings<TestSettings>(configuration, "Test");

        var resolved = services.BuildServiceProvider()
            .GetRequiredService<IOptions<TestSettings>>().Value.ConnectionString;

        var builder = new SqlConnectionStringBuilder(resolved);
        Assert.Equal("Resolved", builder.DataSource);
        Assert.Equal(SqlConnectionResilience.Policy.ConnectTimeoutSeconds, builder.ConnectTimeout);
    }
}
