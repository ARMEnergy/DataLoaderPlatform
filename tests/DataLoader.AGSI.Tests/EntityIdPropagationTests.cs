using System.Data;
using System.Net;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DataLoader.AGSI.Tests;

/// <summary>
/// The key new invariant introduced by the schema normalization (Name/Code/Url →
/// EntityId FK): the storage work unit's <see cref="AgsiStorageWorkUnit.EntityId"/>
/// (sourced from the provider's <c>(Id, Code)</c> pair) is stamped onto the parsed
/// <see cref="GasStorageRow"/> by <see cref="AgsiStorageSourceReader"/>, then must land
/// at the CORRECT TVP position (index 1, right after FileLogId) in
/// <see cref="GasStorageSqlSink"/>'s BuildTable. FK integrity to
/// <c>arm.GasStorageEntity(Id)</c> depends on this value surviving reader → row → TVP
/// intact and in the right column. Fully offline: stub HTTP handler + in-memory FileLog,
/// no DB. BuildTable is invoked via reflection (protected on a sealed sink).
/// </summary>
public class EntityIdPropagationTests
{
    // A distinct sentinel that is NOT any date/measure/FileLogId in the sample, so a
    // stray value landing in the EntityId column would be unmistakable.
    private const int SentinelEntityId = 987654;

    private static AgsiSettings Settings() => new() { BaseUrl = "https://agsi.gie.eu", ApiKey = "SECRET-XKEY" };

    private static AgsiStorageWorkUnit Unit() => new()
    {
        EntityId = SentinelEntityId,
        CountryCode = "de",
        Date = new DateOnly(2026, 8, 13),
        RequestPath = "/api?country=de&date=2026-08-13",
        KeyValue = "k"
    };

    private static DataTable BuildTable(object sink, object rows)
    {
        var method = sink.GetType().GetMethod("BuildTable", BindingFlags.Instance | BindingFlags.NonPublic)
                     ?? throw new InvalidOperationException("BuildTable not found");
        return (DataTable)method.Invoke(sink, new[] { rows })!;
    }

    [Fact]
    public async Task WorkUnitEntityId_SurvivesReaderToRowToTvp_AtColumnIndex1()
    {
        // 1) Reader parses the sample and stamps the work unit's EntityId onto the fact row.
        var handler = StubHttpMessageHandler.Respond(HttpStatusCode.OK, Samples.StorageDe);
        var reader = new AgsiStorageSourceReader(
            handler.NewClient(), Settings(), new FakeAgsiFileLog { FileLogIdToReturn = 55 }, NullLogger.Instance);

        var rows = await reader.ReadAsync(Unit(), CancellationToken.None);

        var row = Assert.Single(rows);
        Assert.Equal(SentinelEntityId, row.EntityId); // reader → row: stamped from the work unit, not the JSON "code" echo

        // 2) Sink → TVP: the same EntityId lands at column index 1 (FileLogId is index 0).
        var sink = new GasStorageSqlSink(
            Options.Create(Settings()), NullLogger<GasStorageSqlSink>.Instance);

        var table = BuildTable(sink, rows.ToList());

        Assert.Equal("EntityId", table.Columns[1].ColumnName);   // position is load-bearing (FK column)
        var built = Assert.Single(table.Rows.Cast<DataRow>());
        Assert.Equal(SentinelEntityId, (int)built[1]);           // by ordinal — proves the value is in the right slot
        Assert.Equal(SentinelEntityId, (int)built["EntityId"]);  // and by name
    }
}
