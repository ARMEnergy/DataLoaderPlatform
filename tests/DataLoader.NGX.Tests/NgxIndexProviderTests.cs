using Xunit;

namespace DataLoader.NGX.Tests;

/// <summary>
/// <see cref="NgxIndexPriceWorkUnitProvider"/> — id batching and window chunking.
///
/// <para>
/// This is where the loader's single most consequential vendor constraint lives: at most
/// TEN <c>indexId</c> parameters per request, above which the endpoint answers 403 for
/// the whole request and every index work unit fails. Asserting
/// <c>Math.Clamp(configured, 1, 10)</c> in isolation tests the BCL; these tests drive the
/// real provider with a stubbed catalogue and check the units it actually emits.
/// </para>
/// </summary>
public class NgxIndexProviderTests
{
    /// <summary>An in-memory catalogue, so the provider can run without a database.</summary>
    private sealed class StubCatalog : NgxIndexCatalog
    {
        private readonly IReadOnlyList<int> _ids;

        public StubCatalog(params int[] ids)
            : base("Server=(local);Database=NGX;Integrated Security=SSPI;", "IndexPrice", TestHelpers.Log) =>
            _ids = ids;

        internal override Task<IReadOnlyList<int>> GetIndexIdsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(_ids);
    }

    private static NgxIndexPriceWorkUnitProvider Provider(
        int[] ids, Action<NgxSettings>? configure = null) =>
        new(new StubCatalog(ids), TestHelpers.Settings(configure), TestHelpers.Log);

    private static int[] Ids(int count) => Enumerable.Range(1, count).ToArray();

    // --------------------------------------------------------------- batching

    /// <summary>
    /// The real shape: 47 entitled ids become 5 batches of at most 10, crossed with one
    /// window chunk — five units, five requests, for the whole feed.
    /// </summary>
    [Fact]
    public async Task FortySevenIds_BecomeFiveBatchesOfAtMostTen()
    {
        var units = await Provider(Ids(47)).GetWorkUnitsAsync(TestHelpers.Context());

        Assert.Equal(5, units.Count);
        Assert.All(units, u => Assert.InRange(u.IndexIds.Count, 1, 10));
        Assert.Equal(new[] { 10, 10, 10, 10, 7 }, units.Select(u => u.IndexIds.Count).ToArray());
    }

    /// <summary>Every id is requested exactly once — no id dropped at a batch seam, none duplicated.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(47)]
    [InlineData(50)]
    [InlineData(219)]
    public async Task EveryIdIsRequestedExactlyOnce(int idCount)
    {
        var units = await Provider(Ids(idCount)).GetWorkUnitsAsync(TestHelpers.Context());

        var requested = units.SelectMany(u => u.IndexIds).ToList();

        Assert.Equal(idCount, requested.Count);
        Assert.Equal(Ids(idCount), requested.OrderBy(i => i).ToArray());
    }

    /// <summary>
    /// ⚠ THE VENDOR CEILING. No emitted unit may ever name more than ten ids, whatever
    /// the configuration says — eleven returns 403 for the entire request.
    /// </summary>
    [Theory]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(25)]
    [InlineData(1000)]
    public async Task NoUnitEverNamesMoreThanTenIds(int configured)
    {
        var units = await Provider(Ids(47), s => s.IndexIdsPerRequest = configured)
            .GetWorkUnitsAsync(TestHelpers.Context());

        Assert.NotEmpty(units);
        Assert.All(units, u => Assert.True(
            u.IndexIds.Count <= 10,
            $"A unit named {u.IndexIds.Count} ids; the vendor limit is 10."));
    }

    /// <summary>A non-positive setting must not produce empty or infinite batches.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task NonPositiveBatchSize_IsClampedToOneIdPerRequest(int configured)
    {
        var units = await Provider(Ids(3), s => s.IndexIdsPerRequest = configured)
            .GetWorkUnitsAsync(TestHelpers.Context());

        Assert.Equal(3, units.Count);
        Assert.All(units, u => Assert.Single(u.IndexIds));
    }

    /// <summary>
    /// An empty catalogue produces no work rather than a request with no
    /// <c>indexId</c> — which would ask for every index on the exchange and be refused.
    /// </summary>
    [Fact]
    public async Task EmptyCatalogue_ProducesNoWorkUnits()
    {
        var units = await Provider(Array.Empty<int>()).GetWorkUnitsAsync(TestHelpers.Context());
        Assert.Empty(units);
    }

    /// <summary>
    /// Batches are cut from the catalogue's ascending order, so the same catalogue always
    /// yields the same batches — and therefore the same resume keys. An unstable order
    /// would re-key every unit on every run and defeat the load log.
    /// </summary>
    [Fact]
    public async Task Batching_IsStableAcrossCalls()
    {
        var provider = Provider(Ids(47));

        var a = await provider.GetWorkUnitsAsync(TestHelpers.Context());
        var b = await provider.GetWorkUnitsAsync(TestHelpers.Context());

        Assert.Equal(a.Select(u => u.Key), b.Select(u => u.Key));
    }

    /// <summary>
    /// Adding an index reshuffles the batches and therefore the keys, so that day's
    /// window reloads under fresh keys. That is the SAFE direction — the merge is
    /// idempotent — but it is a real consequence and is pinned here deliberately.
    /// </summary>
    [Fact]
    public async Task AddingAnIndex_ChangesTheAffectedBatchKeys()
    {
        var before = await Provider(Ids(47)).GetWorkUnitsAsync(TestHelpers.Context());
        var after = await Provider(Ids(48)).GetWorkUnitsAsync(TestHelpers.Context());

        // The full batches are untouched...
        Assert.Equal(4, before.Select(u => u.Key).Intersect(after.Select(u => u.Key)).Count());

        // ...and only the trailing partial batch is re-keyed.
        Assert.Single(before.Select(u => u.Key).Except(after.Select(u => u.Key)));
    }

    // --------------------------------------------------------------- windows

    /// <summary>
    /// The specified window: first of the month 3 back to first of the month 6 forward.
    /// On 2026-09-18 that is 2026-06-01 .. 2027-03-01, and it fits one chunk.
    /// </summary>
    [Fact]
    public async Task Window_IsThreeMonthsBackToSixMonthsForward()
    {
        var units = await Provider(Ids(10)).GetWorkUnitsAsync(TestHelpers.Context());

        Assert.Single(units);
        Assert.Equal(new DateOnly(2026, 6, 1), units[0].Start);
        Assert.Equal(new DateOnly(2027, 3, 1), units[0].End);
    }

    /// <summary>Every unit carries the run's US-Central date as its snapshot stamp.</summary>
    [Fact]
    public async Task EveryUnitCarriesTheCentralExecutionDate()
    {
        var units = await Provider(Ids(47)).GetWorkUnitsAsync(
            TestHelpers.Context(new DateTime(2026, 9, 19, 3, 0, 0, DateTimeKind.Utc)));

        // 03:00 UTC on the 19th is still the evening of the 18th in Chicago.
        Assert.All(units, u => Assert.Equal(new DateOnly(2026, 9, 18), u.ExecutionDate));
    }

    /// <summary>Chunked months tile the window: no gap, no overlap, no day lost at a seam.</summary>
    [Fact]
    public async Task MonthChunks_TileTheWindow()
    {
        var units = await Provider(Ids(10), s => s.IndexWindowChunkMonths = 1)
            .GetWorkUnitsAsync(TestHelpers.Context());

        var ordered = units.OrderBy(u => u.Start).ToList();

        Assert.Equal(new DateOnly(2026, 6, 1), ordered[0].Start);
        Assert.True(ordered[^1].End >= new DateOnly(2027, 3, 1));

        for (var i = 1; i < ordered.Count; i++)
            Assert.Equal(ordered[i - 1].End.AddDays(1), ordered[i].Start);
    }

    /// <summary>Batches are crossed with chunks: 47 ids in 5 batches x 3 chunks = 15 units.</summary>
    [Fact]
    public async Task BatchesAreCrossedWithWindowChunks()
    {
        var units = await Provider(Ids(47), s => s.IndexWindowChunkMonths = 4)
            .GetWorkUnitsAsync(TestHelpers.Context());

        var chunks = units.Select(u => (u.Start, u.End)).Distinct().Count();
        var batches = units.Select(u => string.Join(",", u.IndexIds)).Distinct().Count();

        Assert.Equal(5, batches);
        Assert.Equal(batches * chunks, units.Count);
        Assert.Equal(units.Count, units.Select(u => u.Key).Distinct().Count());
    }

    /// <summary>A non-positive chunk size must not step the loop by zero months and hang.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public async Task NonPositiveChunkMonths_IsClampedAndTerminates(int configured)
    {
        var units = await Provider(Ids(10), s => s.IndexWindowChunkMonths = configured)
            .GetWorkUnitsAsync(TestHelpers.Context());

        Assert.NotEmpty(units);
        Assert.All(units, u => Assert.True(u.Start <= u.End));
    }

    /// <summary>Every emitted unit has a unique resume key — two units must never collide.</summary>
    [Fact]
    public async Task AllUnitKeysAreUnique()
    {
        var units = await Provider(Ids(219), s => s.IndexWindowChunkMonths = 1)
            .GetWorkUnitsAsync(TestHelpers.Context());

        Assert.Equal(units.Count, units.Select(u => u.Key).Distinct().Count());
    }
}
