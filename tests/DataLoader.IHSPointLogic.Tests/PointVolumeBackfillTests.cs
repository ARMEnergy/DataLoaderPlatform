using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using DataLoader.Core.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DataLoader.IHSPointLogic.Tests;

/// <summary>
/// PointVolume incremental-backfill batching (design Section C) — the archetype-E work-unit provider
/// <see cref="PlBatchedFactWorkUnitProvider"/> reworked to group active points by their resolved start date
/// <c>S = MaxDateQueued ?? DefaultStartDateForPointVolume</c>, sub-chunk each group into
/// ≤ <see cref="IHSPointLogicSettings.PointVolumeBatchSize"/>-id slices, append <c>&amp;startDate={S}</c>,
/// and mint the start-date-stamped token <c>{S:yyyyMMdd}-{subIndex:D4}</c> with a per-group sub-index.
/// Points come from an in-memory <see cref="CountingPointProvider"/> carrying nullable watermarks — no DB,
/// no network. runDate/hot are fixed via a 12:00 UTC start.
/// </summary>
public class PointVolumeBackfillTests
{
    private static readonly DateTime StartedAt = new(2026, 8, 18, 12, 0, 0, DateTimeKind.Utc); // runDate 2026-08-18, hour 12

    private static LoaderRunContext Context(Guid? runId = null) => new()
    {
        RunId = runId ?? Guid.NewGuid(),
        StartedAtUtc = StartedAt,
        CancellationToken = CancellationToken.None
    };

    private static IHSPointLogicSettings Settings(
        int batchSize = 50,
        string defaultStart = "2020-01-01",
        PlHotKeyStrategy strategy = PlHotKeyStrategy.RunHour) => new()
    {
        PointVolumeBatchSize = batchSize,
        DefaultStartDateForPointVolume = defaultStart,
        HotZoneKeyStrategy = strategy
    };

    private static PlBatchedFactWorkUnitProvider Provider(CountingPointProvider points, IHSPointLogicSettings? settings = null) =>
        new(PlDescriptors.PointVolume, settings ?? Settings(), points, NullLogger.Instance);

    private static PlPointRef P(int id, string? maxDateQueued = null) =>
        new(id, maxDateQueued is null ? null : DateOnly.ParseExact(maxDateQueued, "yyyy-MM-dd", CultureInfo.InvariantCulture));

    /// <summary>The ?pointIds=… CSV that a unit's request path carries (design C.5).</summary>
    private static string PointIdsCsv(PlWorkUnit u)
    {
        var q = u.RequestPath.Split('?', 2)[1];
        var pair = q.Split('&').Single(p => p.StartsWith("pointIds=", StringComparison.Ordinal));
        return pair["pointIds=".Length..];
    }

    /// <summary>The &amp;startDate=… value that a unit's request path carries (design C.5).</summary>
    private static string StartDateOf(PlWorkUnit u)
    {
        var q = u.RequestPath.Split('?', 2)[1];
        var pair = q.Split('&').Single(p => p.StartsWith("startDate=", StringComparison.Ordinal));
        return pair["startDate=".Length..];
    }

    // ================================================================ 1. grouping by the resolved start date S

    [Fact]
    public async Task GroupsByResolvedStartDate_DistinctWatermarksLandInDistinctBatches_WithMatchingStartDate()
    {
        // p1 NULL and p2 explicitly at the default must COLLAPSE into one 2020-01-01 group (byte-identical
        // startDate). p5 (2025-01-01) and p3/p4 (2026-06-15) form two further distinct groups.
        var points = new CountingPointProvider(new[]
        {
            P(1),                    // NULL   → S = default 2020-01-01
            P(2, "2020-01-01"),      // explicit default → same group as p1
            P(3, "2026-06-15"),
            P(4, "2026-06-15"),      // same group as p3
            P(5, "2025-01-01"),
        });

        var units = await Provider(points).GetWorkUnitsAsync(Context());

        // Three groups → three batches, ordered by S for legibility.
        Assert.Equal(3, units.Count);
        Assert.Equal(new[] { "20200101-0000", "20250101-0000", "20260615-0000" }, units.Select(u => u.BatchToken).ToArray());

        // Each batch's &startDate matches its group's resolved S.
        Assert.Equal(new[] { "2020-01-01", "2025-01-01", "2026-06-15" }, units.Select(StartDateOf).ToArray());

        // NULL (p1) + explicit-default (p2) collapsed into the SAME first batch, ascending PointId.
        Assert.Equal("1,2", PointIdsCsv(units[0]));
        Assert.Equal("5", PointIdsCsv(units[1]));
        Assert.Equal("3,4", PointIdsCsv(units[2]));
    }

    [Fact]
    public async Task GroupsByResolvedStartDate_AllNullWatermarks_CollapseUnderTheDefaultFloor()
    {
        var points = new CountingPointProvider(new[] { P(7), P(8), P(9) }); // all NULL

        var unit = Assert.Single(await Provider(points).GetWorkUnitsAsync(Context()));

        Assert.Equal("20200101-0000", unit.BatchToken);  // one group at the default floor
        Assert.Equal("2020-01-01", StartDateOf(unit));
        Assert.Equal("7,8,9", PointIdsCsv(unit));
    }

    // ================================================================ 2. ≤50 sub-chunking + per-group subIndex reset

    [Fact]
    public async Task SubChunks_OneGroupOf120_Into50_50_20_WithSequentialSubIndex()
    {
        var points = new CountingPointProvider(Enumerable.Range(1, 120)); // all NULL → one 2020-01-01 group

        var units = await Provider(points, Settings(batchSize: 50)).GetWorkUnitsAsync(Context());

        Assert.Equal(3, units.Count);
        Assert.Equal(new[] { "20200101-0000", "20200101-0001", "20200101-0002" }, units.Select(u => u.BatchToken).ToArray());
        Assert.Equal(new[] { 50, 50, 20 }, units.Select(u => PointIdsCsv(u).Split(',').Length).ToArray());

        Assert.Equal("1,2,3", string.Join(",", PointIdsCsv(units[0]).Split(',').Take(3)));
        Assert.Equal(Enumerable.Range(51, 50).Select(i => i.ToString(CultureInfo.InvariantCulture)), PointIdsCsv(units[1]).Split(','));
        Assert.Equal(Enumerable.Range(101, 20).Select(i => i.ToString(CultureInfo.InvariantCulture)), PointIdsCsv(units[2]).Split(','));
    }

    [Fact]
    public async Task SubIndex_ResetsPerGroup_AcrossDistinctStartDates()
    {
        // Group A (S=2021-03-01): 60 points → two slices (…-0000, …-0001).
        // Group B (S=2022-05-01): 10 points → one slice — its sub-index MUST restart at 0000, not continue at 0002.
        var groupA = Enumerable.Range(1, 60).Select(i => P(i, "2021-03-01"));
        var groupB = Enumerable.Range(100, 10).Select(i => P(i, "2022-05-01"));
        var points = new CountingPointProvider(groupA.Concat(groupB));

        var units = await Provider(points, Settings(batchSize: 50)).GetWorkUnitsAsync(Context());

        Assert.Equal(new[] { "20210301-0000", "20210301-0001", "20220501-0000" }, units.Select(u => u.BatchToken).ToArray());
        Assert.Equal(new[] { 50, 10, 10 }, units.Select(u => PointIdsCsv(u).Split(',').Length).ToArray());
    }

    // ================================================================ 3. deterministic ordering (defensive OrderBy(PointId))

    [Fact]
    public async Task Batching_IsDeterministic_RegardlessOfInputPointOrder()
    {
        var refs = new[]
        {
            P(1, "2026-06-01"), P(2, "2026-06-01"), // group 2026-06-01
            P(3), P(4), P(5),                        // NULL → group 2020-01-01
        };
        var shuffled = new[] { refs[3], refs[0], refs[4], refs[1], refs[2] }; // same set, scrambled order

        var runId = Guid.NewGuid(); // fixed so the run-varying hot suffix matches between the two enumerations

        var sortedUnits = await Provider(new CountingPointProvider(refs)).GetWorkUnitsAsync(Context(runId));
        var shuffledUnits = await Provider(new CountingPointProvider(shuffled)).GetWorkUnitsAsync(Context(runId));

        // Byte-identical batch composition, tokens, request paths AND resume keys.
        Assert.Equal(sortedUnits.Select(u => u.BatchToken), shuffledUnits.Select(u => u.BatchToken));
        Assert.Equal(sortedUnits.Select(u => u.RequestPath), shuffledUnits.Select(u => u.RequestPath));
        Assert.Equal(sortedUnits.Select(u => u.Key), shuffledUnits.Select(u => u.Key));

        // And the CSVs are ascending-PointId within each group despite the scrambled input.
        Assert.Equal("3,4,5", PointIdsCsv(sortedUnits[0])); // 2020-01-01 group first (ordered by S)
        Assert.Equal("1,2", PointIdsCsv(sortedUnits[1]));   // 2026-06-01 group
    }

    // ================================================================ 4. startDate URL formatting + downstream pageIndex append

    [Fact]
    public async Task RequestPath_AppendsStartDate_WithAmpersand_InvariantYyyyMMdd()
    {
        var points = new CountingPointProvider(new[] { P(7, "2026-06-15"), P(8, "2026-06-15"), P(9, "2026-06-15") });

        var unit = Assert.Single(await Provider(points).GetWorkUnitsAsync(Context()));

        Assert.Equal("cs/v1/pointlogic/volumeHistory/point?pointIds=7,8,9&startDate=2026-06-15", unit.RequestPath);
        Assert.Equal(1, unit.RequestPath.Count(c => c == '?')); // exactly one '?' — startDate joined with '&'
        Assert.Contains("&startDate=2026-06-15", unit.RequestPath);
    }

    [Fact]
    public async Task RequestPath_StartDateIsInvariant_EvenUnderANonInvariantThreadCulture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            // A culture whose native short date is dd/MM/yyyy — the token/path must NOT pick it up.
            CultureInfo.CurrentCulture = new CultureInfo("fr-FR");
            var points = new CountingPointProvider(new[] { P(1, "2026-01-05") });

            var unit = Assert.Single(await Provider(points).GetWorkUnitsAsync(Context()));

            Assert.Equal("2026-01-05", StartDateOf(unit)); // invariant yyyy-MM-dd, zero-padded, dashes
            Assert.StartsWith("20260105-", unit.BatchToken);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public async Task DownstreamPager_AppendsPageIndexWithAmpersand_ToTheProviderBuiltPath()
    {
        // True end-to-end: the provider builds ?pointIds=…&startDate=…, the shared reader then appends the
        // 0-based &pageIndex — proving the '?'/'&' separators compose correctly through the pipeline.
        var points = new CountingPointProvider(new[] { P(7, "2026-06-15"), P(8, "2026-06-15"), P(9, "2026-06-15") });
        var unit = Assert.Single(await Provider(points).GetWorkUnitsAsync(Context()));

        var handler = FakeHttpMessageHandler.Respond(
            HttpStatusCode.OK,
            """{ "PagingInfo": { "page_size": 10000, "page_count": 1, "total_record_count": 0 }, "Data": [] }""");
        var reader = new PlSourceReader<PointVolumeRow>(
            handler.NewClient(),
            new IHSPointLogicSettings { BaseUrl = "https://api.connect.ihsmarkit.com" },
            new FakePlFileLog(),
            PlDescriptors.PointVolume,
            PointVolumeRow.From,
            NullLogger.Instance);

        await reader.ReadAsync(unit, CancellationToken.None);

        Assert.Equal("?pointIds=7,8,9&startDate=2026-06-15&pageIndex=0", handler.Requests[0].Query);
    }

    // ================================================================ 5. token / resume-key shape

    [Fact]
    public async Task BatchToken_And_ResumeKey_MatchTheSection_C_Shape()
    {
        var points = new CountingPointProvider(Enumerable.Range(1, 120)); // → 3 batches under the default floor
        var units = await Provider(points, Settings(batchSize: 50, strategy: PlHotKeyStrategy.RunHour)).GetWorkUnitsAsync(Context());

        var tokenShape = new Regex(@"^\d{8}-\d{4}$");
        foreach (var u in units)
        {
            Assert.Matches(tokenShape, u.BatchToken!);                       // {yyyyMMdd}-{subIndex:D4}
            Assert.Equal($"pl:PointVolume:{u.BatchToken}:run=2026081812", u.Key); // pl:PointVolume:{token}:run={hot}
            Assert.Equal(u.BatchToken, u.ParamKey);                          // FileLog ParamKey slot == the token
        }

        Assert.Equal("20200101-0002", units[2].BatchToken); // D4 zero-padding on the in-group sub-index
    }

    // ================================================================ 6. fail-fast parse of the default floor

    [Theory]
    [InlineData("not-a-date")]
    [InlineData("")]
    [InlineData("2020/01/01")]   // wrong separators — must not parse as yyyy-MM-dd
    [InlineData("01-01-2020")]   // wrong field order
    [InlineData("2020-1-1")]     // not zero-padded
    public async Task InvalidDefaultStartDate_FailsFast_WithAClearMessage(string bad)
    {
        var points = new CountingPointProvider(new[] { P(1) }); // NULL watermark would resolve to the (bad) default

        var provider = Provider(points, Settings(defaultStart: bad));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetWorkUnitsAsync(Context()));
        Assert.Contains("DefaultStartDateForPointVolume", ex.Message);
        Assert.Contains(bad, ex.Message); // echoes the offending value so the operator can find the typo
    }

    [Fact]
    public async Task ValidDefaultStartDate_DoesNotThrow()
    {
        var points = new CountingPointProvider(new[] { P(1) });
        var units = await Provider(points, Settings(defaultStart: "2019-12-31")).GetWorkUnitsAsync(Context());
        Assert.Equal("20191231-0000", Assert.Single(units).BatchToken);
    }
}
