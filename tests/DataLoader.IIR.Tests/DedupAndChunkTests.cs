using Xunit;

namespace DataLoader.IIR.Tests;

/// <summary>
/// <see cref="IirSqlSinkBase{TRow}.DedupAndChunk"/> — the pure (no-DB) seam CODER extracted from
/// <c>WriteAsync</c> (follow-up #1). It collapses duplicate MERGE keys keeping the LAST occurrence
/// (matching the merge proc's <c>ROW_NUMBER</c> last-wins, and preventing "MERGE … same row more than
/// once" across chunk boundaries), preserves order, and splits the deduped set into
/// <c>MergeChunkSize</c>-row chunks. Invoked through a closed type; lightweight row instances only.
/// </summary>
public class DedupAndChunkTests
{
    private static IReadOnlyList<IReadOnlyList<IirPlantRow>> PlantChunks(IEnumerable<IirPlantRow> rows, int chunkSize) =>
        IirSqlSinkBase<IirPlantRow>.DedupAndChunk(rows, r => r.PlantId, chunkSize);

    private static IReadOnlyList<IirPlantRow> Flatten(IReadOnlyList<IReadOnlyList<IirPlantRow>> chunks) =>
        chunks.SelectMany(c => c).ToList();

    // ---------------------------------------------------------------- (a) last-wins dedup

    [Fact]
    public void Dedup_CollapsesDuplicateKeys_KeepingLastOccurrence()
    {
        var first = new IirPlantRow { FileLogId = 1, PlantId = 3207542, PlantName = "first" };
        var last = new IirPlantRow { FileLogId = 2, PlantId = 3207542, PlantName = "second" };

        var kept = Assert.Single(Flatten(PlantChunks(new[] { first, last }, 10000)));

        Assert.Same(last, kept);            // the LAST occurrence survives (ROW_NUMBER last-wins), not the first
        Assert.Equal("second", kept.PlantName);
    }

    // ---------------------------------------------------------------- (b) order preserved (first-appearance of key)

    [Fact]
    public void Dedup_PreservesOrder_ByFirstAppearanceOfKey_WithLastValuePerKey()
    {
        var rows = new[]
        {
            new IirPlantRow { PlantId = 3, PlantName = "3-a" },
            new IirPlantRow { PlantId = 1, PlantName = "1-a" },
            new IirPlantRow { PlantId = 3, PlantName = "3-b" }, // dup of key 3 → last value wins, keeps 3's slot
            new IirPlantRow { PlantId = 2, PlantName = "2-a" }
        };

        var flat = Flatten(PlantChunks(rows, 10000));

        Assert.Equal(new[] { 3, 1, 2 }, flat.Select(r => r.PlantId).ToArray());       // first-appearance order
        Assert.Equal(new[] { "3-b", "1-a", "2-a" }, flat.Select(r => r.PlantName).ToArray()); // last value for key 3
    }

    // ---------------------------------------------------------------- (c) chunk math: ceil(N/C)

    [Fact]
    public void Chunk_SplitsIntoCeilDivisionChunks_WithCorrectSizes()
    {
        // 112,003 distinct-key rows @ 10,000 → 12 chunks: eleven of 10,000, last of 2,003.
        var rows = Enumerable.Range(1, 112_003).Select(i => new IirPlantRow { FileLogId = 7, PlantId = i }).ToList();

        var chunks = PlantChunks(rows, 10_000);

        Assert.Equal(12, chunks.Count);
        Assert.All(chunks.Take(11), c => Assert.Equal(10_000, c.Count));
        Assert.Equal(2_003, chunks[^1].Count);
        Assert.Equal(112_003, chunks.Sum(c => c.Count));
        // No key crosses a chunk boundary twice and every row is present exactly once.
        Assert.Equal(112_003, Flatten(chunks).Select(r => r.PlantId).Distinct().Count());
    }

    [Fact]
    public void Chunk_ExactMultiple_HasNoRaggedLastChunk()
    {
        var rows = Enumerable.Range(1, 20_000).Select(i => new IirPlantRow { PlantId = i }).ToList();
        var chunks = PlantChunks(rows, 10_000);
        Assert.Equal(2, chunks.Count);
        Assert.All(chunks, c => Assert.Equal(10_000, c.Count));
    }

    [Fact]
    public void Chunk_EmptyInput_YieldsNoChunks()
    {
        Assert.Empty(PlantChunks(Array.Empty<IirPlantRow>(), 10_000));
    }

    // ---------------------------------------------------------------- (d) chunkSize < 1 clamps to 1

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Chunk_ChunkSizeBelowOne_ClampsToOne(int chunkSize)
    {
        var rows = new[]
        {
            new IirPlantRow { PlantId = 1 }, new IirPlantRow { PlantId = 2 }, new IirPlantRow { PlantId = 3 }
        };

        var chunks = PlantChunks(rows, chunkSize);

        Assert.Equal(3, chunks.Count);                       // one row per chunk
        Assert.All(chunks, c => Assert.Single(c));
        Assert.Equal(new[] { 1, 2, 3 }, Flatten(chunks).Select(r => r.PlantId).ToArray());
    }

    // ---------------------------------------------------------------- (e) per-chunk provenance preserved

    [Fact]
    public void Chunk_PlantRows_PreserveFileLogIdInEveryChunk()
    {
        var rows = Enumerable.Range(1, 5).Select(i => new IirPlantRow { FileLogId = 42, PlantId = i }).ToList();

        var chunks = PlantChunks(rows, 2); // → 2, 2, 1

        Assert.Equal(new[] { 2, 2, 1 }, chunks.Select(c => c.Count).ToArray());
        Assert.All(chunks, c => Assert.All(c, r => Assert.Equal(42, r.FileLogId)));
    }

    [Fact]
    public void Chunk_OfflineEventRows_PreserveFileLogIdAndRunDateInEveryChunk()
    {
        var runDate = new DateOnly(2026, 8, 19);
        var rows = Enumerable.Range(1, 5)
            .Select(i => new IirOfflineEventRow { FileLogId = 99, RunDate = runDate, EventId = i })
            .ToList();

        var chunks = IirSqlSinkBase<IirOfflineEventRow>.DedupAndChunk(rows, r => r.EventId, 2); // → 2, 2, 1

        Assert.Equal(new[] { 2, 2, 1 }, chunks.Select(c => c.Count).ToArray());
        // Every row in every chunk keeps its FileLogId and RunDate so a per-chunk MERGE (which reads
        // chunk[0].RunDate for the @RunDate scalar and each row's FileLogId) stamps the right provenance.
        Assert.All(chunks, c => Assert.All(c, r =>
        {
            Assert.Equal(99, r.FileLogId);
            Assert.Equal(runDate, r.RunDate);
        }));
    }

    [Fact]
    public void Chunk_OfflineEvent_LastWinsDedup_OnEventId()
    {
        var a = new IirOfflineEventRow { FileLogId = 1, RunDate = new DateOnly(2026, 8, 19), EventId = 500, EventStatusDesc = "Ongoing" };
        var b = new IirOfflineEventRow { FileLogId = 2, RunDate = new DateOnly(2026, 8, 19), EventId = 500, EventStatusDesc = "Future" };

        var kept = Assert.Single(IirSqlSinkBase<IirOfflineEventRow>.DedupAndChunk(new[] { a, b }, r => r.EventId, 10).SelectMany(c => c));

        Assert.Same(b, kept);
        Assert.Equal("Future", kept.EventStatusDesc);
    }
}
