using System.Data;
using System.Text.Json;
using System.Text.RegularExpressions;
using DataLoader.Core.Watchdog;
using Xunit;

namespace DataLoader.Core.Tests;

/// <summary>
/// DATA_VOLUME's persisted contract (design §D5/§D6/§D7): finding identity, severity,
/// Summary text, DetailJson shape, signal sets, closing hints (§A3) and the email section.
/// The proc's closure half is in sql/Core/tests/008_WatchdogDataVolume.Tests.sql.
/// </summary>
public class WatchdogVolumeMapperTests
{
    private static readonly DateOnly Thu1119 = new(2026, 11, 19);
    private static readonly DateOnly Mon1116 = new(2026, 11, 16);

    private static WatchdogSettings Settings(params string[] loaders)
    {
        var s = new WatchdogSettings();
        s.DataVolume.Enabled = true;
        foreach (var l in loaders) s.Loaders[l] = new WatchdogLoaderSettings { MaxGapHours = 31 };
        return s;
    }

    private static VolumeAnomaly Anomaly(
        string loader = "Marex", DateOnly? day = null, VolumeDirection dir = VolumeDirection.Low,
        long rows = 2_500, double median = 8_700, IReadOnlyList<int>? runIds = null,
        IReadOnlyList<DateOnly>? excluded = null, DateOnly? baselineFrom = null) =>
        new(loader, day ?? Thu1119, dir, rows, 1, 2, median, median == 0 ? 0 : rows / median, 50, 3,
            new[] { new VolumeSample(new DateOnly(2026, 11, 12), 8_000), new VolumeSample(new DateOnly(2026, 11, 5), 9_400) },
            excluded ?? Array.Empty<DateOnly>(), baselineFrom, runIds ?? new[] { 11, 12 });

    private static WatchdogReport EmptyReport() => new() { HostName = "ARMH-OPSDB01", GeneratedAtCentral = new DateTime(2026, 11, 20, 9, 0, 0) };

    private static JsonElement Json(WatchdogFindingRow row) => JsonDocument.Parse(row.DetailJson).RootElement;

    // -------------------------------------------------------------- the row ------

    [Fact]
    public void VolumeRow_HasTheDocumentedKey_NullRunId_AndLowIsWarning()
    {
        var row = WatchdogFindingMapper.VolumeRow(Anomaly(), Settings("Marex"));

        Assert.Equal("DATA_VOLUME|Marex|2026-11-19", row.FindingKey);
        Assert.Equal("DATA_VOLUME", row.Signal);
        Assert.Equal("Marex", row.LoaderName);
        Assert.Null(row.RunId);
        Assert.Equal("Warning", row.Severity);
        Assert.False(row.IsSuppressed);
    }

    [Theory]
    [InlineData(VolumeDirection.Zero, "Warning")]
    [InlineData(VolumeDirection.Low, "Warning")]
    [InlineData(VolumeDirection.High, "Info")]
    public void Item03_05_Severity_ByDirection(VolumeDirection dir, string severity) =>
        Assert.Equal(severity, WatchdogFindingMapper.VolumeRow(Anomaly(dir: dir), Settings("Marex")).Severity);

    [Fact]
    public void Summary_LowExample_FromTheRequirements()
    {
        var row = WatchdogFindingMapper.VolumeRow(Anomaly(), Settings("Marex"));
        Assert.Equal("Marex loaded 2,500 rows on Thu 2026-11-19 (Central), 29% of its usual Thursday (8,700).", row.Summary);
    }

    [Fact]
    public void Summary_HighExample_FromTheRequirements()
    {
        var row = WatchdogFindingMapper.VolumeRow(
            Anomaly(loader: "NUCORE", day: Mon1116, dir: VolumeDirection.High, rows: 684_000, median: 190_000), Settings("NUCORE"));
        Assert.Equal("NUCORE loaded 684,000 rows on Mon 2026-11-16 (Central), 3.6x its usual Monday (190,000).", row.Summary);
    }

    [Fact]
    public void Summary_Zero_ReadsAsZeroPercent()
    {
        var row = WatchdogFindingMapper.VolumeRow(Anomaly(dir: VolumeDirection.Zero, rows: 0), Settings("Marex"));
        Assert.Equal("Marex loaded 0 rows on Thu 2026-11-19 (Central), 0% of its usual Thursday (8,700).", row.Summary);
    }

    [Fact]
    public void Summary_IsCultureInvariant()
    {
        var prior = System.Globalization.CultureInfo.CurrentCulture;
        var priorUi = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo("de-DE");
            var row = WatchdogFindingMapper.VolumeRow(
                Anomaly(day: Mon1116, dir: VolumeDirection.High, rows: 684_000, median: 190_000), Settings("Marex"));
            Assert.Contains("684,000 rows on Mon 2026-11-16", row.Summary);
            Assert.Contains("3.6x its usual Monday (190,000)", row.Summary);
            Assert.Contains("\"ratio\":3.6", row.DetailJson);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = prior;
            System.Globalization.CultureInfo.CurrentUICulture = priorUi;
        }
    }

    [Fact]
    public void Summary_IsCappedAt400Chars()
    {
        // core.WatchdogFinding.Summary / the TVP column are NVARCHAR(400) (design §D5: "≤ 400
        // chars"). ⚠ Fails today: ErrorTextRedactor.Truncate cuts at 400 and then APPENDS
        // "… [truncated, N chars total]", so the shared Summary() helper can exceed the column.
        var row = WatchdogFindingMapper.VolumeRow(Anomaly(loader: new string('L', 600)), Settings());
        Assert.True(row.Summary.Length <= WatchdogFindingMapper.MaxSummaryChars);
    }

    [Fact]
    public void VolumeRow_UsesTheCanonicalConfigSpelling()
    {
        var row = WatchdogFindingMapper.VolumeRow(Anomaly(loader: "MAREX"), Settings("Marex"));
        Assert.Equal("DATA_VOLUME|Marex|2026-11-19", row.FindingKey);
        Assert.Equal("Marex", row.LoaderName);
        Assert.StartsWith("Marex loaded", row.Summary);
    }

    [Fact]
    public void DetailJson_HasExactlyTheDocumentedFields_InOrder()
    {
        var json = Json(WatchdogFindingMapper.VolumeRow(Anomaly(), Settings("Marex")));

        Assert.Equal(new[]
        {
            "dayCentral", "weekday", "direction", "rows", "successfulRuns", "totalRuns", "medianRows", "ratio",
            "lowPercent", "highMultiple", "samples", "excludedSampleDays", "baselineFromCentral", "loaderRunIds"
        }, json.EnumerateObject().Select(p => p.Name));

        Assert.Equal("2026-11-19", json.GetProperty("dayCentral").GetString());
        Assert.Equal("Thursday", json.GetProperty("weekday").GetString());
        Assert.Equal("Low", json.GetProperty("direction").GetString());
        Assert.Equal(2_500, json.GetProperty("rows").GetInt64());
        Assert.Equal(1, json.GetProperty("successfulRuns").GetInt32());
        Assert.Equal(2, json.GetProperty("totalRuns").GetInt32());
        Assert.Equal(8_700, json.GetProperty("medianRows").GetDouble());
        Assert.Equal(0.29, json.GetProperty("ratio").GetDouble());     // 0.2873… rounded to 2 dp
        Assert.Equal(50, json.GetProperty("lowPercent").GetInt32());
        Assert.Equal(3, json.GetProperty("highMultiple").GetDouble());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("baselineFromCentral").ValueKind);
        Assert.Equal(new[] { 11, 12 }, json.GetProperty("loaderRunIds").EnumerateArray().Select(e => e.GetInt32()));

        var sample = json.GetProperty("samples")[0];
        Assert.Equal(new[] { "dayCentral", "rows" }, sample.EnumerateObject().Select(p => p.Name));
        Assert.Equal("2026-11-12", sample.GetProperty("dayCentral").GetString());
        Assert.Equal(8_000, sample.GetProperty("rows").GetInt64());
    }

    [Fact]
    public void DetailJson_DaysAreYyyyMmDd_IncludingExcludedAndBaselineFrom()
    {
        var json = Json(WatchdogFindingMapper.VolumeRow(
            Anomaly(excluded: new[] { new DateOnly(2026, 11, 12), new DateOnly(2026, 11, 5) },
                baselineFrom: new DateOnly(2026, 9, 3)), Settings("Marex")));

        Assert.Equal(new[] { "2026-11-12", "2026-11-05" },
            json.GetProperty("excludedSampleDays").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("2026-09-03", json.GetProperty("baselineFromCentral").GetString());
    }

    [Fact]
    public void DetailJson_LoaderRunIds_AreCappedAt20()
    {
        var ids = Enumerable.Range(1, 25).ToList();
        var json = Json(WatchdogFindingMapper.VolumeRow(Anomaly(runIds: ids), Settings("Marex")));

        Assert.Equal(20, WatchdogFindingMapper.MaxVolumeLoaderRunIds);
        Assert.Equal(Enumerable.Range(1, 20), json.GetProperty("loaderRunIds").EnumerateArray().Select(e => e.GetInt32()));
    }

    // ---------------------------------------------------------- BuildFindings ------

    [Fact]
    public void BuildFindings_WithoutVolumeAnomalies_IsUnchanged()
    {
        var settings = Settings("Marex");
        var withNull = WatchdogFindingMapper.BuildFindings(EmptyReport(), [], [], settings);
        var withEmpty = WatchdogFindingMapper.BuildFindings(EmptyReport(), [], [], settings, Array.Empty<VolumeAnomaly>());
        Assert.Empty(withNull);
        Assert.Empty(withEmpty);
    }

    [Fact]
    public void BuildFindings_PersistsVolumeAnomalies_FromTheParameter_NotFromTheReport()
    {
        // Notify=false: the report carries nothing, persistence still gets the rows.
        var rows = WatchdogFindingMapper.BuildFindings(EmptyReport(), [], [], Settings("Marex"),
            new[] { Anomaly(), Anomaly(loader: "MAREX") });   // same key, case-insensitively

        var row = Assert.Single(rows);
        Assert.Equal("DATA_VOLUME|Marex|2026-11-19", row.FindingKey);
    }

    // ---------------------------------------------------------- signal sets ------

    [Fact]
    public void DataVolume_IsNotInAll()
    {
        Assert.DoesNotContain(WatchdogSignals.DataVolume, WatchdogSignals.All);
        Assert.Equal("DATA_VOLUME", WatchdogSignals.DataVolume);
        Assert.Equal("VOLUME_QUERY", WatchdogDegradedCodes.VolumeQuery);
        Assert.Equal("KeepOpen", WatchdogResolutionHints.KeepOpen);
    }

    [Theory]
    [InlineData("")]
    [InlineData("STUCK_QUERY")]
    [InlineData("VOLUME_QUERY")]
    [InlineData("NO_LOADERS_CONFIGURED")]
    [InlineData("HISTORY_QUERY")]
    [InlineData("STUCK_QUERY,FAILURES_QUERY")]
    public void ComputeSignals_WithVolumeOutOfScope_IsIdenticalToTheOneArgumentOverload(string csv)
    {
        var codes = csv.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var (c1, d1) = WatchdogFindingMapper.ComputeSignals(codes);
        var (c2, d2) = WatchdogFindingMapper.ComputeSignals(codes, includeDataVolume: false);
        Assert.Equal(c1, c2);
        Assert.Equal(d1, d2);
        Assert.DoesNotContain("DATA_VOLUME", c2.Concat(d2));
    }

    [Fact]
    public void ComputeSignals_InScopeAndHealthy_ChecksDataVolumeLast()
    {
        var (chk, deg) = WatchdogFindingMapper.ComputeSignals(Array.Empty<string>(), includeDataVolume: true);
        Assert.Equal(WatchdogSignals.All.Append("DATA_VOLUME"), chk);
        Assert.Empty(deg);
    }

    [Theory]
    [InlineData("VOLUME_QUERY")]
    [InlineData("NO_LOADERS_CONFIGURED")]   // item 29
    [InlineData("HISTORY_QUERY")]
    public void ComputeSignals_BlindingCodes_MoveDataVolumeToDegraded(string code)
    {
        var (chk, deg) = WatchdogFindingMapper.ComputeSignals(new[] { code }, includeDataVolume: true);
        Assert.DoesNotContain("DATA_VOLUME", chk);
        Assert.Contains("DATA_VOLUME", deg);
    }

    [Fact]
    public void VolumeQuery_BlindsOnlyDataVolume()
    {
        Assert.Equal(new[] { "DATA_VOLUME" }, WatchdogDegradedCodes.SignalsBlindedBy("VOLUME_QUERY"));
        var (chk, _) = WatchdogFindingMapper.ComputeSignals(new[] { "VOLUME_QUERY" }, includeDataVolume: true);
        Assert.Equal(WatchdogSignals.All, chk);   // DEGRADED stays checked, so it can close later (item 14)
    }

    // ------------------------------------------------------- closing hints (A3) ------

    private static WatchdogVolumeResult Result(
        IEnumerable<VolumeLoaderDay>? judged = null, IDictionary<string, DateOnly>? baselines = null) =>
        new(new DateOnly(2026, 11, 17), new DateOnly(2026, 11, 19),
            Array.Empty<VolumeAnomaly>(),
            (judged ?? Array.Empty<VolumeLoaderDay>()).ToList(),
            Array.Empty<UnjudgedVolumeDay>(),
            Array.Empty<SkippedVolumeLoader>(),
            new Dictionary<string, DateOnly>(baselines ?? new Dictionary<string, DateOnly>(), StringComparer.OrdinalIgnoreCase));

    private static OpenWatchdogFinding Open(string loader, DateOnly day) =>
        new(WatchdogFindingMapper.VolumeFindingKey(loader, day), "DATA_VOLUME", loader, null);

    private static string? HintFor(OpenWatchdogFinding o, WatchdogSettings s, WatchdogVolumeResult? v,
        bool masterOff = false, IReadOnlyList<WatchdogFindingRow>? current = null) =>
        WatchdogFindingMapper.BuildVolumeHints(new[] { o }, current ?? Array.Empty<WatchdogFindingRow>(), s, v, masterOff)
            .SingleOrDefault()?.ResolutionReason;

    [Fact]
    public void Item09_JudgedInRange_GetsNoHint_SoTheProcClosesItNotDetected()
    {
        var hints = WatchdogFindingMapper.BuildVolumeHints(
            new[] { Open("Marex", Thu1119) }, [], Settings("Marex"),
            Result(new[] { new VolumeLoaderDay("MAREX", Thu1119) }), false);
        Assert.Empty(hints);
    }

    [Fact]
    public void Item10_DayOlderThanTheWindow_IsAgedOutUnresolved() =>
        Assert.Equal("AgedOutUnresolved", HintFor(Open("Marex", new DateOnly(2026, 11, 16)), Settings("Marex"), Result()));

    [Fact]
    public void Item17_VolumeDisabledLoader_IsLoaderDisabled()
    {
        var s = Settings("Marex");
        s.Loaders["Marex"].Volume = new WatchdogLoaderVolumeSettings { Enabled = false };
        Assert.Equal("LoaderDisabled", HintFor(Open("Marex", Thu1119), s, Result()));
    }

    [Fact]
    public void LoaderEnabledFalse_IsLoaderDisabled()
    {
        var s = Settings("Marex");
        s.Loaders["Marex"].Enabled = false;
        Assert.Equal("LoaderDisabled", HintFor(Open("Marex", Thu1119), s, Result()));
    }

    [Fact]
    public void LoaderGoneFromConfig_IsConfigRemoved() =>
        Assert.Equal("ConfigRemoved", HintFor(Open("Marex", Thu1119), Settings("ICE"), Result()));

    [Fact]
    public void Item25_InWindowButUnjudged_IsKeepOpen() =>
        Assert.Equal("KeepOpen", HintFor(Open("Marex", Thu1119), Settings("Marex"), Result()));

    [Fact]
    public void Item26_BaselineFromOnOrBeforeTheDay_GetsNoHint_NotDetected()
    {
        Assert.Null(HintFor(Open("Marex", Thu1119), Settings("Marex"),
            Result(baselines: new Dictionary<string, DateOnly> { ["Marex"] = Thu1119 })));
        Assert.Null(HintFor(Open("Marex", Thu1119), Settings("Marex"),
            Result(baselines: new Dictionary<string, DateOnly> { ["Marex"] = new DateOnly(2026, 11, 1) })));
    }

    [Fact]
    public void BaselineFromAfterTheDay_StaysKeepOpen() =>
        Assert.Equal("KeepOpen", HintFor(Open("Marex", new DateOnly(2026, 11, 17)), Settings("Marex"),
            Result(baselines: new Dictionary<string, DateOnly> { ["Marex"] = Thu1119 })));

    [Fact]
    public void AgedOut_WinsOverBaselineFrom()
    {
        // First match wins: a day older than the window ages out even if the owner has since
        // declared the level normal.
        Assert.Equal("AgedOutUnresolved", HintFor(Open("Marex", new DateOnly(2026, 11, 10)), Settings("Marex"),
            Result(baselines: new Dictionary<string, DateOnly> { ["Marex"] = new DateOnly(2026, 11, 1) })));
    }

    [Fact]
    public void ConfigRemoved_And_LoaderDisabled_WinOverAgedOut()
    {
        var old = new DateOnly(2026, 11, 1);
        Assert.Equal("ConfigRemoved", HintFor(Open("Marex", old), Settings("ICE"), Result()));
        var s = Settings("Marex");
        s.Loaders["Marex"].Enabled = false;
        Assert.Equal("LoaderDisabled", HintFor(Open("Marex", old), s, Result()));
    }

    [Fact]
    public void Item27_MasterSwitchOff_IsLoaderDisabled_WinningOverEverything()
    {
        Assert.Equal("LoaderDisabled", HintFor(Open("Marex", Thu1119), Settings("ICE"), null, masterOff: true));
        Assert.Equal("LoaderDisabled", HintFor(Open("Marex", Thu1119), Settings("Marex"),
            Result(new[] { new VolumeLoaderDay("Marex", Thu1119) }), masterOff: true));
    }

    [Fact]
    public void NoVolumeOutcome_NeverClosesAnything_KeepOpen() =>
        Assert.Equal("KeepOpen", HintFor(Open("Marex", Thu1119), Settings("Marex"), null));

    [Fact]
    public void ReDetected_Or_OtherSignals_GetNoVolumeHint()
    {
        var o = Open("Marex", Thu1119);
        var current = new[] { WatchdogFindingMapper.VolumeRow(Anomaly(loader: "MAREX"), Settings("Marex")) };
        Assert.Null(HintFor(o, Settings("Marex"), Result(), current: current));

        Assert.Empty(WatchdogFindingMapper.BuildVolumeHints(
            new[] { new OpenWatchdogFinding("OVERDUE|Marex", "OVERDUE", "Marex", null) }, [], Settings(), Result(), true));
    }

    [Fact]
    public void BuildHints_ExistingRules_IgnoreDataVolumeFindings()
    {
        // The volume decision lives only in BuildVolumeHints; the pre-existing BuildHints must
        // not start hinting DATA_VOLUME rows (e.g. ConfigRemoved for an unknown loader).
        var hints = WatchdogFindingMapper.BuildHints(new[] { Open("Gone", Thu1119) }, [], Settings("Marex"), null);
        Assert.Empty(hints);
    }

    // ------------------------------------------------------------ report/email ------

    [Fact]
    public void Report_WithNoVolumeAnomalies_DoesNotNotify()
    {
        Assert.False(EmptyReport().ShouldNotify);
        Assert.Empty(EmptyReport().VolumeAnomalies);
    }

    [Fact]
    public void Item16_Report_WithVolumeAnomalies_Notifies_WithRowVolumeSection()
    {
        var report = EmptyReport().WithVolumeAnomalies(new[] { Anomaly() });

        Assert.True(report.ShouldNotify);
        var msg = report.BuildMessage();
        Assert.Equal("WATCHDOG: 1 row-volume anomaly", msg.Subject);
        Assert.Contains("ROW VOLUME", msg.Body);
        Assert.Contains("  Marex loaded 2,500 rows on Thu 2026-11-19 (Central), 29% of its usual Thursday (8,700).", msg.Body);
    }

    [Fact]
    public void Report_PluralSubject_AndSectionOrder_AfterRecentFailures_BeforeMisconfigured()
    {
        var report = new WatchdogReport
        {
            HostName = "h",
            GeneratedAtCentral = new DateTime(2026, 11, 20, 9, 0, 0),
            RecentFailures = new[] { new RecentFailure(Guid.NewGuid(), "Platts", new DateTime(2026, 11, 20, 8, 0, 0), 1, 2, 1) },
            Misconfigured = new[] { "ICE" }
        }.WithVolumeAnomalies(new[] { Anomaly(), Anomaly(loader: "AGSI", dir: VolumeDirection.High, rows: 40_000, median: 10_000) });

        var msg = report.BuildMessage();
        Assert.Equal("WATCHDOG: 1 recent failure, 2 row-volume anomalies, 1 loader misconfigured", msg.Subject);

        var body = msg.Body;
        var failures = body.IndexOf("RECENT FAILURES", StringComparison.Ordinal);
        var volume = body.IndexOf("ROW VOLUME", StringComparison.Ordinal);
        var misconfigured = body.IndexOf("MISCONFIGURED", StringComparison.Ordinal);
        Assert.True(failures >= 0 && failures < volume && volume < misconfigured);
        // Sorted by loader name.
        Assert.True(body.IndexOf("AGSI loaded", StringComparison.Ordinal) < body.IndexOf("Marex loaded", StringComparison.Ordinal));
    }

    [Fact]
    public void Report_EmailLine_IsTheSameTextAsTheSummary()
    {
        var a = Anomaly();
        var summary = WatchdogFindingMapper.VolumeRow(a, Settings("Marex")).Summary;
        Assert.Contains("  " + summary, EmptyReport().WithVolumeAnomalies(new[] { a }).BuildMessage().Body);
    }

    [Fact]
    public void WithPersistenceWarning_KeepsVolumeAnomalies_AndViceVersa()
    {
        var a = EmptyReport().WithVolumeAnomalies(new[] { Anomaly() }).WithPersistenceWarning("Findings were NOT persisted: x");
        Assert.Single(a.VolumeAnomalies);
        Assert.Equal("Findings were NOT persisted: x", a.PersistenceWarning);

        var b = EmptyReport().WithPersistenceWarning("w").WithVolumeAnomalies(new[] { Anomaly() });
        Assert.Equal("w", b.PersistenceWarning);
        Assert.Single(b.VolumeAnomalies);
    }

    [Fact]
    public void WithVolumeAnomalies_CopiesEveryOtherMember()
    {
        var src = new WatchdogReport
        {
            HostName = "h",
            GeneratedAtCentral = new DateTime(2026, 11, 20, 9, 0, 0),
            Overdue = new[] { new OverdueLoader("ICE", null, double.PositiveInfinity, 5) },
            Stuck = new[] { new StuckRun(Guid.NewGuid(), DateTime.UtcNow, "x") },
            RecentFailures = new[] { new RecentFailure(Guid.NewGuid(), "Platts", DateTime.Now, 1, 2, 1) },
            Misconfigured = new[] { "M" },
            Degraded = new[] { "d" },
            PersistenceWarning = "p"
        };
        var copy = src.WithVolumeAnomalies(new[] { Anomaly() });

        Assert.Equal(src.HostName, copy.HostName);
        Assert.Equal(src.GeneratedAtCentral, copy.GeneratedAtCentral);
        Assert.Same(src.Overdue, copy.Overdue);
        Assert.Same(src.Stuck, copy.Stuck);
        Assert.Same(src.RecentFailures, copy.RecentFailures);
        Assert.Same(src.Misconfigured, copy.Misconfigured);
        Assert.Same(src.Degraded, copy.Degraded);
        Assert.Equal("p", copy.PersistenceWarning);
    }

    // ---------------------------------------------------------- SQL / TVP pins ------

    private static string RepoFile(params string[] parts) =>
        Path.Combine(new[] { AppContext.BaseDirectory, "..", "..", "..", "..", ".." }.Concat(parts).ToArray());

    [Fact]
    public void Tvp_VolumeRowFitsTheUnchangedFindingType()
    {
        // The TVP itself is pinned by WatchdogFindingSinkTests.DataTable_MatchesTheTvp_ByNameOrderAndType
        // against 007; 008 must not redefine it, and a volume row must load into it.
        var sql008 = File.ReadAllText(RepoFile("sql", "Core", "008_WatchdogDataVolume.sql"));
        Assert.DoesNotMatch(new Regex(@"^\s*(CREATE|ALTER|DROP)\s+TYPE\b", RegexOptions.IgnoreCase | RegexOptions.Multiline), sql008);
        Assert.DoesNotMatch(new Regex(@"^\s*(CREATE|ALTER)\s+TABLE\b", RegexOptions.IgnoreCase | RegexOptions.Multiline), sql008);

        var row = WatchdogFindingMapper.VolumeRow(Anomaly(), Settings("Marex"));
        var table = SqlWatchdogFindingStore.BuildFindingsTable(new[] { row });

        Assert.Equal(new[] { "FindingKey", "Signal", "LoaderName", "RunId", "Severity", "Summary", "DetailJson", "IsSuppressed" },
            table.Columns.Cast<DataColumn>().Select(c => c.ColumnName));
        Assert.Equal(DBNull.Value, table.Rows[0]["RunId"]);
        Assert.Equal("DATA_VOLUME", table.Rows[0]["Signal"]);
        Assert.Equal("Marex", table.Rows[0]["LoaderName"]);
    }

    [Fact]
    public void Tvp_KeepOpenHint_LoadsIntoTheHintType()
    {
        var table = SqlWatchdogFindingStore.BuildHintsTable(new[]
        {
            new WatchdogResolutionHint("DATA_VOLUME|Marex|2026-11-19", WatchdogResolutionHints.KeepOpen)
        });
        Assert.Equal("KeepOpen", table.Rows[0]["ResolutionReason"]);
    }

    [Fact]
    public void Sql008_StartsWithUseDataLoaderPlatform_AndHonoursKeepOpenForDataVolumeOnly()
    {
        var sql = File.ReadAllText(RepoFile("sql", "Core", "008_WatchdogDataVolume.sql"));
        var firstStatement = Regex.Replace(sql, @"/\*.*?\*/|--[^\n]*", "", RegexOptions.Singleline).TrimStart();
        Assert.Matches(new Regex(@"^USE\s+DataLoaderPlatform\s*;\s*GO\b", RegexOptions.IgnoreCase), firstStatement);
        Assert.Contains("KeepOpen", sql);
        // NOT (DATA_VOLUME AND hint = KeepOpen), NULL-safe over the LEFT JOIN.
        Assert.Matches(new Regex(
            @"AND\s+NOT\s*\(\s*w\.Signal\s*=\s*'DATA_VOLUME'\s+AND\s+ISNULL\(\s*h\.ResolutionReason\s*,\s*''\s*\)\s*=\s*'KeepOpen'\s*\)",
            RegexOptions.IgnoreCase), sql);
        // DATA_VOLUME honours exactly the three closing hints; anything else is NotDetected.
        Assert.Matches(new Regex(
            @"WHEN\s+w\.Signal\s*=\s*'DATA_VOLUME'\s+THEN\s+CASE\s+WHEN\s+h\.ResolutionReason\s+IN\s*\(\s*'AgedOutUnresolved'\s*,\s*'LoaderDisabled'\s*,\s*'ConfigRemoved'\s*\)",
            RegexOptions.IgnoreCase), sql);
    }

    [Fact]
    public void Item28_ExcludedDaysQuery_KeepsEveryClosureOtherThanNotDetected()
    {
        // GetVolumeExcludedDaysAsync's inline SQL cannot run here; pin its A5 predicate so a
        // LoaderDisabled/ConfigRemoved/AgedOutUnresolved closure stays excluded.
        var src = File.ReadAllText(RepoFile("src", "DataLoader.Core", "Watchdog", "SqlWatchdogRepository.cs"));
        Assert.Matches(new Regex(@"Signal\s*=\s*'DATA_VOLUME'\s*AND\s*\(ResolvedAtUtc IS NULL OR ResolutionReason <> 'NotDetected'\)"), src);
        // Sargable half-open StartTime range on the daily-volume read (§D3).
        Assert.Matches(new Regex(@"StartTime\s*>=\s*@FromCentral\s+AND\s+StartTime\s*<\s*DATEADD\(day,\s*1,\s*@ToCentral\)"), src);
    }
}
