using DataLoader.EnergyAspects;
using Xunit;

namespace DataLoader.EnergyAspects.Tests;

/// <summary>
/// Pins the dataset-id chunking that keeps the timeseries request URI inside
/// the API's limit. Mappings 375 (7,092 ids) and 371 (2,496 ids) answered
/// 414 Request-URI Too Large on every window until these splits existed, and
/// the numbers below are measured against the live API, not assumed.
/// </summary>
public class RequestChunkingTests
{
    // A 36-character key, the length the real one happens to be, so the fixed
    // overhead these tests measure matches production's.
    private static EnergyAspectsSettings Settings(int maxUriLength = 8000) => new()
    {
        ApiKey = new string('k', 36),
        TimeseriesEndpoint = "/data/timeseries/",
        MaxRequestUriLength = maxUriLength
    };

    private static IReadOnlyList<int> Ids(int count, int first = 100000) =>
        Enumerable.Range(first, count).ToList();

    private static int LongestUri(
        EnergyAspectsSettings settings, IReadOnlyList<IReadOnlyList<int>> chunks) =>
        chunks.Max(c => EnergyAspectsRequest.BuildTimeseriesUri(
            settings, c, new DateTime(2026, 9, 1), new DateTime(2026, 9, 30)).Length);

    // ---- the thing that actually broke ----

    [Fact]
    public void EveryChunkFitsTheBudget()
    {
        var settings = Settings();
        var chunks = EnergyAspectsRequest.ChunkDatasetIds(
            Ids(7092), EnergyAspectsRequest.FixedUriLength(settings), settings.MaxRequestUriLength);

        Assert.True(LongestUri(settings, chunks) <= settings.MaxRequestUriLength);
    }

    [Theory]
    [InlineData(7092)]  // mapping 375, OilX supplydemand
    [InlineData(2496)]  // mapping 371, OilX terminalsweekly
    [InlineData(1470)]  // mapping 515, the largest that used to fit in one request
    public void RealMappingSizesStayUnderTheServerLimit(int idCount)
    {
        var settings = Settings();
        var chunks = EnergyAspectsRequest.ChunkDatasetIds(
            Ids(idCount), EnergyAspectsRequest.FixedUriLength(settings), settings.MaxRequestUriLength);

        // The budget is the contract, but the server limit is what 414s.
        Assert.True(LongestUri(settings, chunks) < EnergyAspectsRequest.ServerRequestUriLimit);
        Assert.True(chunks.Count >= 1);
    }

    [Fact]
    public void UnsplitListWouldHaveBlownTheServerLimit()
    {
        // Guards the premise: without chunking, mapping 375 is far past 16,384
        // and the old single-request code could only ever have got a 414.
        var settings = Settings();
        var whole = EnergyAspectsRequest.BuildTimeseriesUri(
            settings, Ids(7092), new DateTime(2026, 9, 1), new DateTime(2026, 9, 30));

        Assert.True(whole.Length > EnergyAspectsRequest.ServerRequestUriLimit);
    }

    // ---- chunking preserves the data ----

    [Fact]
    public void ChunksCoverEveryIdExactlyOnceInOrder()
    {
        var settings = Settings();
        var ids = Ids(7092);
        var chunks = EnergyAspectsRequest.ChunkDatasetIds(
            ids, EnergyAspectsRequest.FixedUriLength(settings), settings.MaxRequestUriLength);

        Assert.Equal(ids, chunks.SelectMany(c => c).ToList());
        Assert.All(chunks, c => Assert.NotEmpty(c));
    }

    [Fact]
    public void ChunkingIsDeterministic()
    {
        // Part numbers are resume keys, so the same id list has to split the
        // same way on every run or a part's key would stop meaning the same ids.
        var settings = Settings();
        var fixedLength = EnergyAspectsRequest.FixedUriLength(settings);

        var first = EnergyAspectsRequest.ChunkDatasetIds(Ids(7092), fixedLength, settings.MaxRequestUriLength);
        var second = EnergyAspectsRequest.ChunkDatasetIds(Ids(7092), fixedLength, settings.MaxRequestUriLength);

        Assert.Equal(first.Select(c => c.Count), second.Select(c => c.Count));
    }

    [Fact]
    public void ShortListIsNotSplit()
    {
        var settings = Settings();
        var chunks = EnergyAspectsRequest.ChunkDatasetIds(
            Ids(50), EnergyAspectsRequest.FixedUriLength(settings), settings.MaxRequestUriLength);

        Assert.Single(chunks);
        Assert.Equal(50, chunks[0].Count);
    }

    [Fact]
    public void EmptyListProducesNoChunks() =>
        Assert.Empty(EnergyAspectsRequest.ChunkDatasetIds(
            Array.Empty<int>(), EnergyAspectsRequest.FixedUriLength(Settings()), 8000));

    // ---- the budget is honoured, not approximated ----

    [Fact]
    public void ChunksArePackedNotPadded()
    {
        // Each chunk bar the last must be full enough that the next id would
        // not have fitted — otherwise we would be issuing more requests than
        // necessary.
        var settings = Settings();
        var ids = Ids(7092);
        var chunks = EnergyAspectsRequest.ChunkDatasetIds(
            ids, EnergyAspectsRequest.FixedUriLength(settings), settings.MaxRequestUriLength);

        for (var i = 0; i < chunks.Count - 1; i++)
        {
            var overfilled = chunks[i].Concat(new[] { chunks[i + 1][0] });
            var length = EnergyAspectsRequest.BuildTimeseriesUri(
                settings, overfilled, new DateTime(2026, 9, 1), new DateTime(2026, 9, 30)).Length;
            Assert.True(length > settings.MaxRequestUriLength,
                $"chunk {i + 1} had room for one more id — the split is wasteful");
        }
    }

    [Fact]
    public void TighterBudgetProducesMoreChunks()
    {
        // Two-sided check so the budget cannot rot into being ignored.
        var wide = Settings(8000);
        var narrow = Settings(2000);

        var wideChunks = EnergyAspectsRequest.ChunkDatasetIds(
            Ids(7092), EnergyAspectsRequest.FixedUriLength(wide), wide.MaxRequestUriLength);
        var narrowChunks = EnergyAspectsRequest.ChunkDatasetIds(
            Ids(7092), EnergyAspectsRequest.FixedUriLength(narrow), narrow.MaxRequestUriLength);

        Assert.True(narrowChunks.Count > wideChunks.Count);
        Assert.True(LongestUri(narrow, narrowChunks) <= 2000);
    }

    [Fact]
    public void IdsOfDifferingWidthAreMeasuredNotCounted()
    {
        // A count-based splitter would mis-size these; a length-based one
        // must still land inside the budget.
        var settings = Settings(1000);
        var ids = new List<int>();
        for (var i = 0; i < 400; i++) ids.Add(i % 2 == 0 ? 7 : 1234567);

        var chunks = EnergyAspectsRequest.ChunkDatasetIds(
            ids, EnergyAspectsRequest.FixedUriLength(settings), settings.MaxRequestUriLength);

        Assert.True(LongestUri(settings, chunks) <= 1000);
        Assert.Equal(ids, chunks.SelectMany(c => c).ToList());
    }

    // ---- misconfiguration fails loudly ----

    [Fact]
    public void BudgetAtOrAboveTheServerLimitIsRejected()
    {
        var settings = Settings(EnergyAspectsRequest.ServerRequestUriLimit);
        Assert.Throws<ArgumentOutOfRangeException>(() => EnergyAspectsRequest.ChunkDatasetIds(
            Ids(10), EnergyAspectsRequest.FixedUriLength(settings), settings.MaxRequestUriLength));
    }

    [Fact]
    public void BudgetTooSmallForAnyIdIsRejected()
    {
        var settings = Settings(10);
        Assert.Throws<ArgumentOutOfRangeException>(() => EnergyAspectsRequest.ChunkDatasetIds(
            Ids(10), EnergyAspectsRequest.FixedUriLength(settings), settings.MaxRequestUriLength));
    }

    // ---- the URI itself ----

    [Fact]
    public void FixedLengthIsIndependentOfTheDates()
    {
        var settings = Settings();
        var jan = EnergyAspectsRequest.BuildTimeseriesUri(
            settings, Array.Empty<int>(), new DateTime(2026, 1, 1), new DateTime(2026, 1, 31)).Length;
        var dec = EnergyAspectsRequest.BuildTimeseriesUri(
            settings, Array.Empty<int>(), new DateTime(2026, 12, 1), new DateTime(2026, 12, 31)).Length;

        Assert.Equal(jan, dec);
        Assert.Equal(jan, EnergyAspectsRequest.FixedUriLength(settings));
    }

    [Fact]
    public void UriCarriesTheIdsCommaSeparatedAndTheWindow()
    {
        var uri = EnergyAspectsRequest.BuildTimeseriesUri(
            Settings(), new[] { 1, 22, 333 }, new DateTime(2026, 9, 1), new DateTime(2026, 9, 30));

        Assert.Contains("dataset_id=1,22,333", uri);
        Assert.Contains("date_from=2026-09-01", uri);
        Assert.Contains("date_to=2026-09-30", uri);
    }
}
