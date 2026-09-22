using System.Data;
using DataLoader.Marex;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DataLoader.Marex.Tests;

/// <summary>
/// The DataTable half of the TVP contract, asserted without opening a connection.
/// </summary>
public class MarexSinkTests
{
    /// <summary>Exposes the protected <c>BuildTable</c>.</summary>
    private sealed class TestableSink : MarexTvpSink
    {
        internal TestableSink(MarexTableDescriptor table, ILogger logger)
            : base(table, "Server=nowhere;Database=none;", logger) { }

        internal DataTable Build(IReadOnlyList<MarexRow> rows) => BuildTable(rows);
    }

    private static readonly DateTime ExchangeDate = new(2026, 9, 21);

    [Theory]
    [InlineData("ClosingPrice")]
    [InlineData("MarketStatistic")]
    [InlineData("Period")]
    [InlineData("PeriodGroup")]
    [InlineData("Product")]
    public void DataTableColumns_MatchTheDescriptor_ByNameOrderAndType(string feedId)
    {
        var table = MarexDescriptors.All.Single(t => t.FeedId == feedId);
        var built = new TestableSink(table, new RecordingLogger()).Build(Array.Empty<MarexRow>());

        Assert.Equal(
            table.Columns.Select(c => c.Name).ToArray(),
            built.Columns.Cast<DataColumn>().Select(c => c.ColumnName).ToArray());

        Assert.Equal(
            table.Columns.Select(c => c.ClrType).ToArray(),
            built.Columns.Cast<DataColumn>().Select(c => c.DataType).ToArray());
    }

    [Fact]
    public void RowsWithTheWrongValueCount_Throw_RatherThanLoadShifted()
    {
        var sink = new TestableSink(MarexDescriptors.PeriodGroup, new RecordingLogger());
        var bad = new MarexRow { Values = new object[] { 1L, "only", "three" } };

        var ex = Assert.Throws<InvalidOperationException>(() => sink.Build(new[] { bad }));

        Assert.Contains("arm.PeriodGroup", ex.Message);
        Assert.Contains("3 value(s)", ex.Message);
    }

    [Fact]
    public void OversizedNonKeyString_IsTruncatedAndWarnedAbout()
    {
        // SqlClient would otherwise abort the WHOLE batch with "String or binary data
        // would be truncated", losing every row over one long label.
        var log = new RecordingLogger();
        var sink = new TestableSink(MarexDescriptors.Product, log);
        var row = MarexMapper.Map(Dto.Product(name: new string('x', 300)));

        var built = sink.Build(new[] { row });

        var nameIndex = MarexDescriptors.Product.Columns.ToList().FindIndex(c => c.Name == "Name");
        Assert.Equal(256, ((string)built.Rows[0][nameIndex]).Length);
        Assert.Contains(log.Records, r => r.Level == LogLevel.Warning && r.Message.Contains("truncated"));
    }

    [Fact]
    public void TruncationDoesNotMutateTheCallersRow()
    {
        // The rows are also what a retry would re-send.
        var sink = new TestableSink(MarexDescriptors.Product, new RecordingLogger());
        var row = MarexMapper.Map(Dto.Product(name: new string('x', 300)));
        var nameIndex = MarexDescriptors.Product.Columns.ToList().FindIndex(c => c.Name == "Name");

        sink.Build(new[] { row });

        Assert.Equal(300, ((string)row.Values[nameIndex]).Length);
    }

    [Fact]
    public void OversizedKeyString_DropsTheRowAndLogsAnError()
    {
        // ⚠ Truncating a key does not fail — it merges the row into a DIFFERENT one via
        // the merge predicate. That is silent corruption, so the row is discarded.
        var log = new RecordingLogger();
        var sink = new TestableSink(MarexDescriptors.ClosingPrice, log);

        // Built by hand rather than through the mapper: ClosingPriceDto.Id is computed as
        // $"{ProductId}:{PeriodId}" and so is bounded at 32 characters, meaning the vendor
        // CANNOT produce this row today. The guard is what protects the invariant if a
        // future SDK makes the id a free-text field.
        var row = MarexMapper.Map(Dto.ClosingPrice(), ExchangeDate);
        row.Values[1] = new string('9', 80);

        var built = sink.Build(new[] { row });

        Assert.Empty(built.Rows);
        Assert.Contains(log.Records, r =>
            r.Level == LogLevel.Error &&
            r.Message.Contains("DROPPED") &&
            r.Message.Contains("ClosingPriceId"));
    }

    [Fact]
    public void WellSizedRows_AllSurvive()
    {
        var log = new RecordingLogger();
        var sink = new TestableSink(MarexDescriptors.ClosingPrice, log);
        var rows = new[]
        {
            MarexMapper.Map(Dto.ClosingPrice(productId: 1001, periodId: 5001), ExchangeDate),
            MarexMapper.Map(Dto.ClosingPrice(productId: 1001, periodId: 5002), ExchangeDate)
        };

        var built = sink.Build(rows);

        Assert.Equal(2, built.Rows.Count);
        Assert.DoesNotContain(log.Records, r => r.Level >= LogLevel.Warning);
    }

    [Fact]
    public void DbNullValues_SurviveIntoTheDataTable()
    {
        var sink = new TestableSink(MarexDescriptors.ClosingPrice, new RecordingLogger());
        var row = MarexMapper.Map(
            Dto.ClosingPrice(previousPrice: null, previousTime: DateTime.MinValue), ExchangeDate);

        var built = sink.Build(new[] { row });

        var index = MarexDescriptors.ClosingPrice.Columns.ToList().FindIndex(c => c.Name == "PreviousTime");
        Assert.Equal(DBNull.Value, built.Rows[0][index]);
    }

    [Fact]
    public void EachFeed_UsesItsOwnProcAndTvpType()
    {
        // The write gate keys on the proc name, so two feeds sharing one would serialize
        // against each other for no reason — and a shared TVP type would be a contract bug.
        var procs = MarexDescriptors.All.Select(t => t.MergeProc).ToArray();
        var types = MarexDescriptors.All.Select(t => t.TvpType).ToArray();

        Assert.Equal(procs.Length, procs.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(types.Length, types.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
