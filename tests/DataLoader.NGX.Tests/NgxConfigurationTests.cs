using Microsoft.Extensions.Logging;
using Xunit;

namespace DataLoader.NGX.Tests;

/// <summary>
/// The startup warnings. Each one guards a configuration that "works" — no exception,
/// no failed unit — while silently doing less than the operator expects, which is the
/// worst failure mode a loader has.
/// </summary>
public class NgxConfigurationTests
{
    private sealed class CapturingLogger : ILogger
    {
        public List<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }

    private static List<string> Warnings(Action<NgxSettings> configure)
    {
        var logger = new CapturingLogger();
        NgxModule.WarnAboutConfiguration(TestHelpers.Settings(configure), logger);
        return logger.Messages;
    }

    /// <summary>A correctly configured loader says nothing at startup.</summary>
    [Fact]
    public void DefaultSettings_ProduceNoWarnings() =>
        Assert.Empty(Warnings(_ => { }));

    /// <summary>
    /// The 10-id ceiling is a measured vendor limit. Above it EVERY index work unit
    /// fails 403, so this misconfiguration is total rather than partial.
    /// </summary>
    [Fact]
    public void IdsPerRequestAboveTen_IsWarnedAbout()
    {
        var warnings = Warnings(s => s.IndexIdsPerRequest = 25);
        Assert.Contains(warnings, m => m.Contains("hard limit of 10"));
    }

    [Fact]
    public void IdsPerRequestBelowOne_IsWarnedAbout() =>
        Assert.Contains(Warnings(s => s.IndexIdsPerRequest = 0), m => m.Contains("clamped to 1"));

    /// <summary>
    /// An unresolved SEE_DB sentinel means every request goes out with the literal
    /// string as a credential and is redirected to the SSO login page.
    /// </summary>
    [Theory]
    [InlineData("SEE_DB")]
    [InlineData("see_db")]
    [InlineData("")]
    public void UnresolvedCredentialSentinel_IsWarnedAbout(string value)
    {
        Assert.Contains(Warnings(s => s.Username = value), m => m.Contains("Username"));
        Assert.Contains(Warnings(s => s.Password = value), m => m.Contains("Password"));
    }

    /// <summary>The password itself must never reach a log line.</summary>
    [Fact]
    public void Warnings_NeverContainThePassword()
    {
        var warnings = Warnings(s =>
        {
            s.Password = "hunter2-should-never-appear";
            s.Username = "SEE_DB";
            s.IndexIdsPerRequest = 99;
            s.StripChunkDays = 90;
        });

        Assert.All(warnings, m => Assert.DoesNotContain("hunter2", m));
    }

    /// <summary>
    /// Widening the IndexType filter pulls CrudeIndexPrice ids into the batches, and one
    /// unentitled id fails its whole batch.
    /// </summary>
    [Fact]
    public void NonDefaultIndexTypeFilter_IsWarnedAbout() =>
        Assert.Contains(
            Warnings(s => s.IndexTypeFilter = "CrudeIndexPrice"),
            m => m.Contains("403"));

    /// <summary>
    /// This endpoint has no truncation flag, so a month-sized chunk has nothing to
    /// distinguish a capped response from a complete one.
    /// </summary>
    [Fact]
    public void OversizedStripChunk_IsWarnedAbout() =>
        Assert.Contains(Warnings(s => s.StripChunkDays = 90), m => m.Contains("truncation flag"));

    [Fact]
    public void PageSizeAboveServerCap_IsWarnedAbout() =>
        Assert.Contains(Warnings(s => s.IndexPageSize = 100000), m => m.Contains("20,000"));

    [Fact]
    public void PageSizeBelowVendorDefault_IsWarnedAbout() =>
        Assert.Contains(Warnings(s => s.IndexPageSize = 10), m => m.Contains("more requests"));

    /// <summary>
    /// A zero settled horizon makes every chunk settled immediately, so amendments would
    /// never be re-pulled.
    /// </summary>
    [Fact]
    public void NegativeSettledHorizon_IsWarnedAbout() =>
        Assert.Contains(
            Warnings(s => s.StripSettledAfterDays = -1),
            m => m.Contains("never be re-pulled"));

    [Fact]
    public void UnknownFeedId_IsWarnedAbout() =>
        Assert.Contains(
            Warnings(s => s.EnabledFeeds = new[] { "IndexPrice", "NotAFeed" }),
            m => m.Contains("NotAFeed"));

    /// <summary>Both real feed ids must be accepted without complaint.</summary>
    [Fact]
    public void BothFeedIds_AreRecognised() =>
        Assert.Empty(Warnings(s => s.EnabledFeeds =
            new[] { NgxDescriptors.IndexPriceFeedId, NgxDescriptors.StripFeedId }));

    /// <summary>The shipped appsettings values must be the ones the loader considers healthy.</summary>
    [Fact]
    public void ShippedDefaults_MatchTheSpecifiedWindows()
    {
        var settings = new NgxSettings();

        Assert.Equal(3, settings.IndexMonthsBack);
        Assert.Equal(6, settings.IndexMonthsForward);
        Assert.Equal(1, settings.StripMonthsBack);
        Assert.Equal(1, settings.StripMonthsForward);
        Assert.Equal(10, settings.IndexIdsPerRequest);
        Assert.Equal("IndexPrice", settings.IndexTypeFilter);
        Assert.Equal("Natural Gas", settings.IndexCommodityType);
    }
}
