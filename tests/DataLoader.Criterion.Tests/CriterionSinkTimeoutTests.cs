using System.Text.Json;
using DataLoader.Core.Sinks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DataLoader.Criterion.Tests;

/// <summary>
/// Criterion's override of <see cref="SqlSinkBase{TRow}.CommandTimeoutSeconds"/>:
/// that it resolves from <see cref="CriterionSettings.SinkCommandTimeoutSeconds"/>, that a
/// degenerate configured value is floored rather than reintroducing the 30-second ADO.NET
/// default, and — the one that actually keeps the fix safe — that the shipped configuration
/// never lets the sink timeout creep up to the loader's per-work-unit budget.
/// </summary>
public sealed class CriterionSinkTimeoutTests
{
    /// <summary>Exposes the protected, inherited CommandTimeoutSeconds without a connection.</summary>
    private sealed class ProbeSink : CriterionTvpSink
    {
        public ProbeSink(int commandTimeoutSeconds)
            : base(
                CriterionDescriptors.MiscUnitTable,
                "Server=(local);Database=Criterion;Integrated Security=SSPI;",
                commandTimeoutSeconds,
                NullLogger.Instance)
        {
        }

        public int ResolvedTimeoutSeconds => CommandTimeoutSeconds;
    }

    [Fact]
    public void CriterionSettings_SinkCommandTimeoutSeconds_defaults_to_300()
    {
        // 300, not ADO.NET's 30 and not an unbounded value — the same 10x floor every
        // other loader gets for free from SqlSinkBase's own default.
        Assert.Equal(300, new CriterionSettings().SinkCommandTimeoutSeconds);
    }

    [Fact]
    public void Sink_resolves_its_timeout_from_CriterionSettings_SinkCommandTimeoutSeconds()
    {
        var settings = TestHelpers.Settings(s => s.SinkCommandTimeoutSeconds = 777);

        // Mirrors exactly how CriterionModule.BuildPipelines wires the sink — the
        // settings value is the constructor argument with nothing in between that
        // could drift.
        var sink = new ProbeSink(settings.SinkCommandTimeoutSeconds);

        Assert.Equal(777, sink.ResolvedTimeoutSeconds);
    }

    [Theory]
    [InlineData(300, 300)]
    [InlineData(900, 900)]
    [InlineData(1, 30)]
    [InlineData(0, 30)]
    [InlineData(-100, 30)]
    public void A_zero_or_negative_configured_value_is_floored_at_30(int configured, int expected)
    {
        // Math.Max(30, ...) - the same shape as EiaLoadValidator - so a misconfigured
        // setting cannot reintroduce the very default this knob exists to move away from.
        Assert.Equal(expected, new ProbeSink(configured).ResolvedTimeoutSeconds);
    }

    /// <summary>
    /// ⚠ THE COUPLING GUARD. SqlSinkBase.CommandTimeoutSeconds documents why: the
    /// per-work-unit budget is what finally cancels a wedged unit, so if the sink's own
    /// timeout ever reached or passed it, a stuck MERGE would hold the unit open for the
    /// WHOLE work-unit budget instead of failing cleanly and letting the loader retry.
    ///
    /// <para>
    /// This reads the real <c>src/DataLoader.Host/appsettings.json</c>, not
    /// <see cref="CriterionSettings"/>' bare class defaults. The class default for
    /// <c>WorkUnitTimeoutSeconds</c> (inherited from <c>LoaderSettingsBase</c>) is 300 —
    /// equal to the sink's own default — so asserting against bare `new
    /// CriterionSettings()` would make this pass by coincidence rather than by checking
    /// what is actually deployed. The shipped <c>WorkUnitTimeoutSeconds</c> is 1800;
    /// reading it from the file is what makes this guard capable of failing if either
    /// number is ever edited into the other's way.
    /// </para>
    /// </summary>
    [Fact]
    public void Shipped_settings_keep_the_sink_timeout_under_the_work_unit_budget()
    {
        using var doc = JsonDocument.Parse(
            File.ReadAllText(RepoPaths.HostAppSettingsFile),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

        var criterion = doc.RootElement.GetProperty("Loaders").GetProperty("Criterion");

        var sinkTimeout = criterion.GetProperty("SinkCommandTimeoutSeconds").GetInt32();
        var workUnitTimeout = criterion.GetProperty("WorkUnitTimeoutSeconds").GetInt32();

        Assert.True(sinkTimeout < workUnitTimeout,
            $"Criterion's shipped SinkCommandTimeoutSeconds ({sinkTimeout}s) must stay below " +
            $"WorkUnitTimeoutSeconds ({workUnitTimeout}s). Otherwise a wedged MERGE is never " +
            "cancelled by its own timeout - the work-unit budget has to fire instead, holding " +
            "the unit open for the whole budget before anything gives up.");
    }
}
