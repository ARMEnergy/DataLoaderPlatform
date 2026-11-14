using System.Text.Json;
using DataLoader.Core.Watchdog;
using Xunit;

namespace DataLoader.Core.Tests;

/// <summary>
/// The persisted contract the DBA Agent reads: finding identity, severity, DetailJson
/// shape, signal sets and closing hints. The proc's half is in
/// sql/Core/tests/007_WatchdogFindings.Tests.sql.
/// </summary>
public class WatchdogFindingMapperTests
{
    private static readonly DateTime NowCentral = new(2026, 11, 12, 9, 0, 0);

    private static WatchdogSettings Settings(params (string Name, int Gap, bool Enabled, bool SkipWeekends)[] loaders)
    {
        var s = new WatchdogSettings();
        foreach (var (name, gap, enabled, skip) in loaders)
            s.Loaders[name] = new WatchdogLoaderSettings { MaxGapHours = gap, Enabled = enabled, SkipWeekends = skip };
        return s;
    }

    private static WatchdogReport Report(
        IReadOnlyList<OverdueLoader>? overdue = null,
        IReadOnlyList<StuckRun>? stuck = null,
        IReadOnlyList<RecentFailure>? failures = null,
        IReadOnlyList<string>? misconfigured = null) => new()
    {
        HostName = "ARMH-OPSDB01",
        GeneratedAtCentral = NowCentral,
        Overdue = overdue ?? Array.Empty<OverdueLoader>(),
        Stuck = stuck ?? Array.Empty<StuckRun>(),
        RecentFailures = failures ?? Array.Empty<RecentFailure>(),
        Misconfigured = misconfigured ?? Array.Empty<string>()
    };

    private static JsonElement Json(WatchdogFindingRow row) => JsonDocument.Parse(row.DetailJson).RootElement;

    // -------------------------------------------------------------------- identity ---

    [Fact]
    public void Overdue_KeysOnTheCanonicalConfigSpelling_NotTheSpellingItArrivedWith()
    {
        var settings = Settings(("Platts", 31, true, true));
        var report = Report(overdue: new[] { new OverdueLoader("PLATTS", NowCentral.AddHours(-40), 40, 31) });

        var row = Assert.Single(WatchdogFindingMapper.BuildFindings(report, [], [], settings));

        Assert.Equal("OVERDUE|Platts", row.FindingKey);
        Assert.Equal("Platts", row.LoaderName);
        Assert.Equal("Critical", row.Severity);
        Assert.False(row.IsSuppressed);
        Assert.Null(row.RunId);
    }

    [Fact]
    public void EachSignal_HasTheDocumentedKeyAndDefaultSeverity()
    {
        var stuckId = Guid.NewGuid();
        var failedId = Guid.NewGuid();
        var settings = Settings(("Platts", 31, true, false), ("Vulcan", 0, true, false));
        var report = Report(
            stuck: new[] { new StuckRun(stuckId, new DateTime(2026, 11, 12, 1, 0, 0), "ARMH-OPSDB01") },
            failures: new[] { new RecentFailure(failedId, "Platts", NowCentral.AddHours(-1), 3, 14, 3) },
            misconfigured: new[] { "Vulcan" });
        var degraded = new[] { new DegradedCheck(WatchdogDegradedCodes.StuckQuery, "stuck runs (X: y)", "X: y") };

        var rows = WatchdogFindingMapper.BuildFindings(report, [], degraded, settings)
            .ToDictionary(r => r.FindingKey);

        Assert.Equal("Warning", rows[$"STUCK|{stuckId}"].Severity);
        Assert.Equal(stuckId, rows[$"STUCK|{stuckId}"].RunId);
        Assert.Equal("Warning", rows[$"FAILED|{failedId}"].Severity);
        Assert.Equal(failedId, rows[$"FAILED|{failedId}"].RunId);
        Assert.Equal("Warning", rows["MISCONFIGURED|Vulcan"].Severity);
        Assert.Equal("Critical", rows["DEGRADED|STUCK_QUERY"].Severity);
        Assert.Null(rows["DEGRADED|STUCK_QUERY"].LoaderName);
    }

    [Fact]
    public void SuppressedFailure_IsFlaggedSuppressed_WithInfoSeverity()
    {
        var id = Guid.NewGuid();
        var suppressed = new[] { new RecentFailure(id, "AGSI", NowCentral.AddHours(-1), 25, 2630, 0) };

        var row = Assert.Single(WatchdogFindingMapper.BuildFindings(Report(), suppressed, [], new WatchdogSettings()));

        Assert.Equal($"FAILED|{id}", row.FindingKey);
        Assert.True(row.IsSuppressed);
        Assert.Equal("Info", row.Severity);
    }

    [Fact]
    public void DuplicateKeys_DifferingOnlyByCase_AreSentOnce()
    {
        // The TVP's primary key compares case-insensitively; a duplicate would fail the proc call.
        var report = Report(misconfigured: new[] { "Vulcan", "VULCAN" });

        var rows = WatchdogFindingMapper.BuildFindings(report, [], [], new WatchdogSettings());

        Assert.Single(rows);
    }

    // ----------------------------------------------------------------- DetailJson ----

    [Fact]
    public void NeverRunLoader_SerialisesHoursAsNull_InsteadOfThrowingOnInfinity()
    {
        var settings = Settings(("OilX", 39, true, false));
        var report = Report(overdue: new[] { new OverdueLoader("OilX", null, double.PositiveInfinity, 39) });

        var json = Json(Assert.Single(WatchdogFindingMapper.BuildFindings(report, [], [], settings)));

        Assert.Equal(JsonValueKind.Null, json.GetProperty("hoursSinceLastRun").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("lastStartCentral").ValueKind);
        Assert.True(json.GetProperty("neverRun").GetBoolean());
        Assert.Equal(39, json.GetProperty("maxGapHours").GetInt32());
    }

    [Fact]
    public void Overdue_DetailCarriesSkipWeekendsFromConfig()
    {
        var settings = Settings(("ICE", 26, true, true));
        var report = Report(overdue: new[] { new OverdueLoader("ICE", NowCentral.AddHours(-30), 30.123, 26) });

        var json = Json(Assert.Single(WatchdogFindingMapper.BuildFindings(report, [], [], settings)));

        Assert.True(json.GetProperty("skipWeekends").GetBoolean());
        Assert.False(json.GetProperty("neverRun").GetBoolean());
        Assert.Equal(30.12, json.GetProperty("hoursSinceLastRun").GetDouble());
    }

    [Fact]
    public void Timestamps_CentralValuesKeepTheirCentralName_AndUtcValuesEndInZ()
    {
        // Acceptance #15: never mix zones. Central has no offset and is named *Central;
        // UTC is named *Utc and carries Z.
        var id = Guid.NewGuid();
        var report = Report(
            overdue: new[] { new OverdueLoader("Platts", new DateTime(2026, 11, 11, 17, 5, 0), 40, 31) },
            stuck: new[] { new StuckRun(id, new DateTime(2026, 11, 12, 1, 2, 3, 456), null) },
            failures: new[] { new RecentFailure(id, "Platts", new DateTime(2026, 11, 12, 8, 0, 0), 1, 2, 1) });

        var rows = WatchdogFindingMapper.BuildFindings(report, [], [], Settings(("Platts", 31, true, false)))
            .ToDictionary(r => r.Signal);

        Assert.Equal("2026-11-11T17:05:00", Json(rows["OVERDUE"]).GetProperty("lastStartCentral").GetString());
        Assert.Equal("2026-11-12T08:00:00", Json(rows["FAILED"]).GetProperty("startTimeCentral").GetString());
        Assert.Equal("2026-11-12T01:02:03.456Z", Json(rows["STUCK"]).GetProperty("startedAtUtc").GetString());
        Assert.Equal(JsonValueKind.Null, Json(rows["STUCK"]).GetProperty("hostName").ValueKind);
        Assert.Equal(6, Json(rows["STUCK"]).GetProperty("stuckAfterHours").GetInt32());
    }

    [Fact]
    public void Failure_DetailCarriesUnitCounts()
    {
        var report = Report(failures: new[] { new RecentFailure(Guid.NewGuid(), "Platts", NowCentral, 3, 14, 2) });

        var json = Json(Assert.Single(WatchdogFindingMapper.BuildFindings(report, [], [], new WatchdogSettings())));

        Assert.Equal(3, json.GetProperty("unitsFailed").GetInt32());
        Assert.Equal(14, json.GetProperty("unitsTotal").GetInt32());
        Assert.Equal(2, json.GetProperty("unresolvedUnits").GetInt32());
    }

    [Fact]
    public void CredentialLikeText_IsRedactedInSummaryAndDetailJson()
    {
        // Acceptance #13.
        const string secret = "s3cr3tvalue123";
        var check = new DegradedCheck(
            WatchdogDegradedCodes.FailuresQuery,
            $"recent failures (HttpRequestException: GET https://x/?api_key={secret} failed)",
            $"HttpRequestException: GET https://x/?api_key={secret} failed password={secret}");

        var row = WatchdogFindingMapper.DegradedRow(check);

        Assert.DoesNotContain(secret, row.Summary);
        Assert.DoesNotContain(secret, row.DetailJson);
        Assert.Equal(WatchdogDegradedCodes.FailuresQuery, Json(row).GetProperty("checkCode").GetString());
    }

    [Fact]
    public void Summary_IsTheSameTextAsTheEmailLine()
    {
        var o = new OverdueLoader("Platts", NowCentral.AddHours(-40), 40, 31);
        var report = Report(overdue: new[] { o });

        var row = Assert.Single(WatchdogFindingMapper.BuildFindings(report, [], [], Settings(("Platts", 31, true, false))));
        var body = report.BuildMessage().Body;

        Assert.Contains($"  {row.Summary}{Environment.NewLine}", body);
    }

    [Fact]
    public void EmailLines_AreByteIdenticalToThePreChangeFormat()
    {
        // Golden text, transcribed from WatchdogReport.BuildBody as it was before the
        // line formatters were extracted (spec §2.1: no change to the email).
        var id = Guid.Parse("3f2a0000-0000-0000-0000-000000000001");
        var report = Report(
            overdue: new[]
            {
                new OverdueLoader("Platts", new DateTime(2026, 11, 11, 7, 5, 0), 25.94, 24),
                new OverdueLoader("OilX", null, double.PositiveInfinity, 39)
            },
            stuck: new[] { new StuckRun(id, new DateTime(2026, 11, 12, 1, 2, 0), null) },
            failures: new[]
            {
                new RecentFailure(id, "AGSI", new DateTime(2026, 11, 12, 8, 0, 0), 3, 14, 1),
                new RecentFailure(id, "ICE", new DateTime(2026, 11, 12, 7, 0, 0), 2, 9, 2)
            },
            misconfigured: new[] { "Vulcan" });

        var body = report.BuildMessage().Body;

        Assert.Contains("  OilX: has NEVER run (threshold 39h)", body);
        Assert.Contains("  Platts: last started 2026-11-11 07:05 Central, 25.9h ago (threshold 24h)", body);
        Assert.Contains("  3f2a0000-0000-0000-0000-000000000001 started 2026-11-12 01:02 UTC on unknown host", body);
        Assert.Contains("  AGSI: 2026-11-12 08:00 Central, 3/14 units failed (1 unresolved)", body);
        Assert.Contains("  ICE: 2026-11-12 07:00 Central, 2/9 units failed" + Environment.NewLine, body);
        Assert.Contains("  Vulcan: MaxGapHours is missing or <= 0 (Platform:Watchdog:Loaders:Vulcan:MaxGapHours)", body);
        Assert.DoesNotContain("NOT persisted", body);
    }

    [Fact]
    public void LongDegradedReason_IsCappedAt200InDetailJson()
    {
        var row = WatchdogFindingMapper.DegradedRow(
            new DegradedCheck(WatchdogDegradedCodes.StuckQuery, "stuck runs (...)", new string('x', 1000)));

        var reason = Json(row).GetProperty("reason").GetString()!;
        Assert.StartsWith(new string('x', 200), reason);
        Assert.DoesNotContain(new string('x', 201), reason);
    }

    // -------------------------------------------------------------------- signals ----

    [Fact]
    public void CleanPass_ChecksEverySignal_IncludingDegraded()
    {
        var (chk, deg) = WatchdogFindingMapper.ComputeSignals([]);

        Assert.Equal(new[] { "OVERDUE", "MISCONFIGURED", "STUCK", "FAILED", "DEGRADED" }, chk);
        Assert.Empty(deg);
    }

    [Fact]
    public void HistoryQueryFailure_ChecksNothing_SoNothingCanClose()
    {
        var (chk, deg) = WatchdogFindingMapper.ComputeSignals([WatchdogDegradedCodes.HistoryQuery]);

        Assert.Empty(chk);
        Assert.Equal(WatchdogSignals.All, deg);
    }

    [Fact]
    public void StuckQueryFailure_DegradesOnlyStuck()
    {
        var (chk, deg) = WatchdogFindingMapper.ComputeSignals([WatchdogDegradedCodes.StuckQuery]);

        Assert.Equal(new[] { "STUCK" }, deg);
        Assert.Contains("DEGRADED", chk);   // so DEGRADED|FAILURES_QUERY from an earlier pass can close
        Assert.DoesNotContain("STUCK", chk);
    }

    [Fact]
    public void NoLoadersConfigured_DegradesOverdueAndMisconfigured()
    {
        // An empty Loaders section reports nothing; treating that as "checked" would close
        // every open OVERDUE finding — silence mistaken for health.
        var (chk, deg) = WatchdogFindingMapper.ComputeSignals([WatchdogDegradedCodes.NoLoadersConfigured]);

        Assert.Equal(new[] { "OVERDUE", "MISCONFIGURED" }, deg);
        Assert.Equal(new[] { "STUCK", "FAILED", "DEGRADED" }, chk);
    }

    // ---------------------------------------------------------------------- hints ----

    [Fact]
    public void OpenOverdue_ForADisabledLoader_GetsLoaderDisabled()
    {
        // Acceptance #8.
        var open = new[] { new OpenWatchdogFinding("OVERDUE|Platts", "OVERDUE", "Platts", null) };
        var settings = Settings(("Platts", 31, false, false));

        var hint = Assert.Single(WatchdogFindingMapper.BuildHints(open, [], settings, null));

        Assert.Equal(new WatchdogResolutionHint("OVERDUE|Platts", "LoaderDisabled"), hint);
    }

    [Fact]
    public void OpenMisconfigured_ForALoaderRemovedFromConfig_GetsConfigRemoved()
    {
        var open = new[] { new OpenWatchdogFinding("MISCONFIGURED|Vulcan", "MISCONFIGURED", "Vulcan", null) };

        var hint = Assert.Single(WatchdogFindingMapper.BuildHints(open, [], Settings(("Platts", 31, true, false)), null));

        Assert.Equal("ConfigRemoved", hint.ResolutionReason);
    }

    [Fact]
    public void OpenOverdue_ThatIsSimplyFixed_GetsNoHint_SoTheProcSaysNotDetected()
    {
        var open = new[] { new OpenWatchdogFinding("OVERDUE|Platts", "OVERDUE", "Platts", null) };

        Assert.Empty(WatchdogFindingMapper.BuildHints(open, [], Settings(("PLATTS", 31, true, false)), null));
    }

    [Fact]
    public void FindingStillDetected_GetsNoHint()
    {
        var open = new[] { new OpenWatchdogFinding("OVERDUE|Platts", "OVERDUE", "Platts", null) };
        var current = new[] { new WatchdogFindingRow("OVERDUE|Platts", "OVERDUE", "Platts", null, "Critical", "s", "{}", false) };

        Assert.Empty(WatchdogFindingMapper.BuildHints(open, current, Settings(("Platts", 31, false, false)), null));
    }

    [Fact]
    public void OpenFailed_SelfHealedOrStillBroken_GetsTheResolversVerdict()
    {
        // Acceptance #5 and #6.
        var healed = Guid.NewGuid();
        var broken = Guid.NewGuid();
        var missing = Guid.NewGuid();
        var open = new[]
        {
            new OpenWatchdogFinding($"FAILED|{healed}", "FAILED", "Platts", healed),
            new OpenWatchdogFinding($"FAILED|{broken}", "FAILED", "Platts", broken),
            new OpenWatchdogFinding($"FAILED|{missing}", "FAILED", "Platts", missing)
        };
        var resolution = new RecentFailuresResult(
            new[] { new RecentFailure(broken, "Platts", NowCentral, 3, 14, 2) },
            new[] { new RecentFailure(healed, "Platts", NowCentral, 3, 14, 0) });

        Assert.Equal(new[] { healed, broken, missing }, WatchdogFindingMapper.FailedRunIdsToResolve(open, []));

        var hints = WatchdogFindingMapper.BuildHints(open, [], new WatchdogSettings(), resolution)
            .ToDictionary(h => h.FindingKey, h => h.ResolutionReason);

        Assert.Equal("SelfHealed", hints[$"FAILED|{healed}"]);
        Assert.Equal("AgedOutUnresolved", hints[$"FAILED|{broken}"]);
        // No verdict at all → no hint → the proc defaults to AgedOutUnresolved.
        Assert.False(hints.ContainsKey($"FAILED|{missing}"));
    }

    // ----------------------------------------------------------------- AppVersion ----

    [Theory]
    [InlineData(@"C:\ETL_Tools\DataLoaderPlatform\Releases\2026_11_12_09_00_00\", "2026_11_12_09_00_00")]
    [InlineData(@"C:\ETL_Tools\DataLoaderPlatform\Releases\2026_11_12_09_00_00", "2026_11_12_09_00_00")]
    [InlineData(@"C:\repo\src\DataLoader.Host\bin\Release\net8.0\", null)]
    [InlineData(@"C:\ETL_Tools\DataLoaderPlatform\Loader\", null)]
    [InlineData("", null)]
    public void AppVersion_IsTheReleaseFolder_OnlyWhenRunningFromReleases(string baseDir, string? expected)
    {
        Assert.Equal(expected, WatchdogFindingMapper.AppVersionFromBaseDirectory(baseDir));
    }
}
