using System.Data;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DataLoader.Platts.Tests;

/// <summary>
/// Group E (spec §5.3 / §6.3 / §7) — the SQL sinks and the file-logging
/// decorator. The sinks' <c>BuildTable</c> is exercised directly (it is pure); no
/// database is touched. <c>BuildTable</c> is protected and the sinks are sealed,
/// so it is invoked via reflection.
/// </summary>
public class SinkTests
{
    private static DataTable BuildTable(object sink, object rows)
    {
        var method = sink.GetType().GetMethod("BuildTable", BindingFlags.Instance | BindingFlags.NonPublic)
                     ?? throw new InvalidOperationException("BuildTable not found");
        return (DataTable)method.Invoke(sink, new[] { rows })!;
    }

    private static SymbolDataSqlSink NewSymbolDataSink() =>
        new(Options.Create(new PlattsSettings { ConnectionString = "unused" }),
            NullLogger<SymbolDataSqlSink>.Instance);

    private static SymbolSqlSink NewSymbolSink() =>
        new(Options.Create(new PlattsSettings { ConnectionString = "unused" }),
            NullLogger<SymbolSqlSink>.Instance);

    // -------------------------------------------------------------------------
    // SymbolData sink — column order/types must match arm.SymbolDataTvp.
    // -------------------------------------------------------------------------

    [Fact]
    public void SymbolDataSink_BuildTable_HasExactTvpColumnOrderAndTypes()
    {
        var row = new SymbolDataRow
        {
            MDC = "GD", Symbol = "AEWAA00", Bate = "c",
            Date = new DateTime(2026, 7, 31), Action = "N", Value = 27.11m,
            ActionDate = new DateTime(2026, 7, 31, 23, 41, 0), SourcePath = "20260731\\market.ftp"
        };

        var table = BuildTable(NewSymbolDataSink(), new List<SymbolDataRow> { row });

        var expectedNames = new[] { "MDC", "Symbol", "Bate", "Date", "Action", "Value", "ActionDate", "SourcePath" };
        var expectedTypes = new[]
        {
            typeof(string), typeof(string), typeof(string), typeof(DateTime),
            typeof(string), typeof(decimal), typeof(DateTime), typeof(string)
        };
        Assert.Equal(expectedNames, table.Columns.Cast<DataColumn>().Select(c => c.ColumnName).ToArray());
        Assert.Equal(expectedTypes, table.Columns.Cast<DataColumn>().Select(c => c.DataType).ToArray());

        var r = table.Rows[0];
        Assert.Equal("GD", r["MDC"]);
        Assert.Equal("AEWAA00", r["Symbol"]);
        Assert.Equal("c", r["Bate"]);
        Assert.Equal(new DateTime(2026, 7, 31), r["Date"]);
        Assert.Equal("N", r["Action"]);
        Assert.Equal(27.11m, r["Value"]);
        Assert.Equal(new DateTime(2026, 7, 31, 23, 41, 0), r["ActionDate"]);
        Assert.Equal("20260731\\market.ftp", r["SourcePath"]);
    }

    [Fact]
    public void SymbolDataSink_BuildTable_NullValue_IsDbNull()
    {
        var row = new SymbolDataRow
        {
            MDC = "GD", Symbol = "ANTAE00", Bate = "u",
            Date = new DateTime(2026, 7, 31), Action = "X", Value = null,
            ActionDate = new DateTime(2026, 7, 31, 23, 41, 0), SourcePath = "20260731\\market.ftp"
        };

        var table = BuildTable(NewSymbolDataSink(), new List<SymbolDataRow> { row });

        Assert.Equal(DBNull.Value, table.Rows[0]["Value"]);
    }

    [Fact]
    public void SymbolDataSink_BuildTable_DedupsOnMergeKey_KeepingMaxActionDate()
    {
        // Same (Symbol,Bate,Date,Action) but different ActionDate/Value.
        var older = new SymbolDataRow
        {
            MDC = "GD", Symbol = "AEWAA00", Bate = "c", Date = new DateTime(2026, 7, 31), Action = "N",
            Value = 10m, ActionDate = new DateTime(2026, 7, 31, 10, 0, 0), SourcePath = "p"
        };
        var newer = new SymbolDataRow
        {
            MDC = "GD", Symbol = "AEWAA00", Bate = "c", Date = new DateTime(2026, 7, 31), Action = "N",
            Value = 20m, ActionDate = new DateTime(2026, 7, 31, 12, 0, 0), SourcePath = "p"
        };

        var table = BuildTable(NewSymbolDataSink(), new List<SymbolDataRow> { older, newer });

        var kept = Assert.Single(table.Rows.Cast<DataRow>());
        Assert.Equal(new DateTime(2026, 7, 31, 12, 0, 0), kept["ActionDate"]); // max ActionDate wins
        Assert.Equal(20m, kept["Value"]);
    }

    // -------------------------------------------------------------------------
    // Symbol sink — column order/types must match arm.SymbolTvp.
    // -------------------------------------------------------------------------

    [Fact]
    public void SymbolSink_BuildTable_HasExact14ColumnOrderAndTypes()
    {
        var row = new SymbolRow
        {
            MDC = "GD", Trans = "T1", Symbol = "AEWAA00", Bates = "cuw", Freq = "DA", Curr = "BRL",
            UOM = "LTR", Dec = 3, Conv = 1.5m, Flag = "F", ToUom = "GAL",
            Earliest = new DateTime(2026, 4, 1), Latest = new DateTime(2026, 6, 25), Description = "desc"
        };

        var table = BuildTable(NewSymbolSink(), new List<SymbolRow> { row });

        var expectedNames = new[]
        {
            "MDC", "Trans", "Symbol", "Bates", "Freq", "Curr", "UOM",
            "DEC", "Conv", "Flag", "To_UOM", "Earliest", "Latest", "Description"
        };
        var expectedTypes = new[]
        {
            typeof(string), typeof(string), typeof(string), typeof(string), typeof(string),
            typeof(string), typeof(string), typeof(int), typeof(decimal), typeof(string),
            typeof(string), typeof(DateTime), typeof(DateTime), typeof(string)
        };
        Assert.Equal(expectedNames, table.Columns.Cast<DataColumn>().Select(c => c.ColumnName).ToArray());
        Assert.Equal(expectedTypes, table.Columns.Cast<DataColumn>().Select(c => c.DataType).ToArray());

        var r = table.Rows[0];
        Assert.Equal(3, r["DEC"]);
        Assert.Equal(1.5m, r["Conv"]);
        Assert.Equal("F", r["Flag"]);
        Assert.Equal(new DateTime(2026, 4, 1), r["Earliest"]);
        Assert.Equal(new DateTime(2026, 6, 25), r["Latest"]);
    }

    [Fact]
    public void SymbolSink_BuildTable_NullCells_AreDbNull()
    {
        var row = new SymbolRow { Symbol = "BBBBB00" }; // everything else null

        var table = BuildTable(NewSymbolSink(), new List<SymbolRow> { row });

        var r = table.Rows[0];
        Assert.Equal(DBNull.Value, r["MDC"]);
        Assert.Equal(DBNull.Value, r["DEC"]);
        Assert.Equal(DBNull.Value, r["Conv"]);
        Assert.Equal(DBNull.Value, r["Earliest"]);
        Assert.Equal(DBNull.Value, r["Latest"]);
        Assert.Equal(DBNull.Value, r["Description"]);
    }

    [Fact]
    public void SymbolSink_BuildTable_M2_DuplicateSymbol_CollapsesToLast()
    {
        // Regression M2: two rows with the same Symbol must collapse to one (keep last).
        var first = new SymbolRow { Symbol = "AEWAA00", Description = "first" };
        var last = new SymbolRow { Symbol = "AEWAA00", Description = "last" };

        var table = BuildTable(NewSymbolSink(), new List<SymbolRow> { first, last });

        var kept = Assert.Single(table.Rows.Cast<DataRow>());
        Assert.Equal("last", kept["Description"]);
    }

    // -------------------------------------------------------------------------
    // FileLoggingSink<TRow> decorator.
    // -------------------------------------------------------------------------

    private static FileLoggingSink<SymbolDataRow> NewDecorator(
        FakeSink<SymbolDataRow> inner, FakeFileLog fileLog, string feed = "SymbolData") =>
        new(inner, fileLog, feed,
            r => (r.SourcePath, r.FileName, r.LastModifiedUtc, r.SizeBytes),
            NullLogger.Instance);

    private static SymbolDataRow MetaRow() => new()
    {
        SourcePath = "20260731\\market.ftp",
        FileName = "market.ftp",
        LastModifiedUtc = new DateTime(2026, 7, 31, 23, 45, 0, DateTimeKind.Utc),
        SizeBytes = 4096
    };

    [Fact]
    public async Task FileLoggingSink_Success_ReturnsInnerCount_AndLogsSuccessWithRowCount()
    {
        var inner = new FakeSink<SymbolDataRow> { ReturnCount = 99 };
        var fileLog = new FakeFileLog();
        var sink = NewDecorator(inner, fileLog);
        var rows = new List<SymbolDataRow> { MetaRow(), MetaRow() };

        using var cts = new CancellationTokenSource();
        var count = await sink.WriteAsync(rows, cts.Token);

        Assert.Equal(99, count); // inner (merge) count returned, not rows.Count
        var call = Assert.Single(fileLog.Calls);
        Assert.Equal("SymbolData", call.Feed);
        Assert.Equal("Success", call.Status);
        Assert.Equal(rows.Count, call.RowCount); // audit records the file's own row count
        Assert.Equal("20260731\\market.ftp", call.SourcePath);
        Assert.Equal("market.ftp", call.FileName);
        Assert.Equal(4096L, call.SizeBytes);
        Assert.Equal(cts.Token, call.Token); // success path uses the caller's token
    }

    [Fact]
    public async Task FileLoggingSink_InnerThrows_LogsFailedWithTokenNone_ThenRethrows()
    {
        var boom = new InvalidOperationException("merge blew up");
        var inner = new FakeSink<SymbolDataRow> { ThrowOnWrite = boom };
        var fileLog = new FakeFileLog();
        var sink = NewDecorator(inner, fileLog);
        var rows = new List<SymbolDataRow> { MetaRow() };

        using var cts = new CancellationTokenSource();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => sink.WriteAsync(rows, cts.Token));

        Assert.Same(boom, ex); // original exception is not masked
        var call = Assert.Single(fileLog.Calls);
        Assert.Equal("Failed", call.Status);
        Assert.Equal(CancellationToken.None, call.Token); // failure audit uses CancellationToken.None
    }

    [Fact]
    public async Task FileLoggingSink_EmptyBatch_DoesNotLog_AndReturnsZero()
    {
        var inner = new FakeSink<SymbolDataRow> { ReturnCount = 0 };
        var fileLog = new FakeFileLog();
        var sink = NewDecorator(inner, fileLog);

        var count = await sink.WriteAsync(new List<SymbolDataRow>(), CancellationToken.None);

        Assert.Equal(0, count);
        Assert.Empty(fileLog.Calls); // no arm.FileLog row for a zero-row file
    }
}
