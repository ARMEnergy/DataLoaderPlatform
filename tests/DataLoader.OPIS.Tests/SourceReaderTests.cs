using DataLoader.Core.Sources;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DataLoader.OPIS.Tests;

/// <summary>
/// Whole-file parsing: the reader's <c>Parse</c> over verbatim live content —
/// header skipped, CRLF handled, bad rows dropped without losing the good ones.
/// The download and FileLog paths need a network and a database, so they are not
/// exercised here.
/// </summary>
public class SourceReaderTests
{
    private static OpisSourceReader Reader() =>
        new(new UnusedFtp(), new UnusedFileLog(),
            Options.Create(new OpisSettings { ConnectionString = "unused" }),
            NullLogger<OpisSourceReader>.Instance);

    private static OpisWorkUnit Unit(string name = "20260820LP.csv", int y = 2026, int m = 8, int d = 20) => new()
    {
        File = new RemoteFile("/" + name, name, 14401, new DateTime(2026, 8, 20, 21, 26, 2, DateTimeKind.Utc)),
        SourceFileDate = new DateOnly(y, m, d)
    };

    [Fact]
    public void Parse_SkipsTheHeader_AndReadsEveryDataRow()
    {
        var rows = Reader().Parse(Samples.File20260820, Unit());

        Assert.Equal(5, rows.Count);                                  // 6 lines, 1 is the header
        Assert.DoesNotContain(rows, r => r.MktProd == "Mkt_Prod");
        Assert.All(rows, r => Assert.Equal(new DateOnly(2026, 8, 20), r.Date));
        Assert.All(rows, r => Assert.Equal("I", r.Price));
    }

    [Fact]
    public void Parse_TrimsThePaddedKeyOnEveryRow()
    {
        var rows = Reader().Parse(Samples.File20260820, Unit());

        Assert.All(rows, r => Assert.Equal(r.MktProd.Trim(), r.MktProd));
        Assert.Contains(rows, r => r.MktProd == "LOS ANGELES BT MIX");
        Assert.Contains(rows, r => r.MktProd == "S.F. BAY AREA PRO");   // periods survive
    }

    [Fact]
    public void Parse_StampsTheWorkUnitsFileDateOnEveryRow()
    {
        // SourceFileDate is the merge ordering guard and comes from the FILE NAME,
        // not from the row's own Date column.
        var rows = Reader().Parse(Samples.File20260820, Unit("20260821LP.csv", 2026, 8, 21));

        Assert.All(rows, r => Assert.Equal(new DateOnly(2026, 8, 21), r.SourceFileDate));
        Assert.All(rows, r => Assert.Equal(new DateOnly(2026, 8, 20), r.Date));
    }

    [Fact]
    public void Parse_HandlesLfOnlyAndTrailingNewline()
    {
        var lfOnly = Samples.File20260820.Replace("\r\n", "\n");
        Assert.Equal(5, Reader().Parse(lfOnly, Unit()).Count);

        var noTrailing = Samples.File20260820.TrimEnd('\r', '\n');
        Assert.Equal(5, Reader().Parse(noTrailing, Unit()).Count);
    }

    [Fact]
    public void Parse_DropsOnlyTheBadRow_AndKeepsTheRest()
    {
        var withJunk = Samples.File20260820 + "I,BROKEN ROW,not-a-date,1,2,3,US,GAL,A,D\r\n";
        var rows = Reader().Parse(withJunk, Unit());

        Assert.Equal(5, rows.Count);                                  // the junk line is gone
        Assert.DoesNotContain(rows, r => r.MktProd == "BROKEN ROW");  // ...and only it
    }

    [Fact]
    public void Parse_HeaderOnlyFile_YieldsNoRowsWithoutThrowing()
    {
        var rows = Reader().Parse("Price,Mkt_Prod,Date,Low,High,Avg,Country,Unit,Timing,Freq\r\n", Unit());
        Assert.Empty(rows);
    }

    [Fact]
    public void Parse_EmptyFile_YieldsNoRowsWithoutThrowing() =>
        Assert.Empty(Reader().Parse(string.Empty, Unit()));

    [Fact]
    public void Parse_KeepsBasketRowsThatOnlyPublishAnAverage()
    {
        var file = "Price,Mkt_Prod,Date,Low,High,Avg,Country,Unit,Timing,Freq\r\n"
                 + Samples.BasketRowLine + "\r\n";

        var row = Assert.Single(Reader().Parse(file, Unit("20260720LP.csv", 2026, 7, 20)));

        Assert.Equal("MT BEL NT BSKT", row.MktProd);
        Assert.Null(row.Low);
        Assert.Null(row.High);
        Assert.Equal(73.0588m, row.Avg);
    }

    // ------------------------------------------------------------------ fakes

    private sealed class UnusedFtp : IOpisFtp
    {
        public Task<IReadOnlyList<RemoteFile>> ListAsync(string p, string q, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<Stream> OpenReadAsync(string p, CancellationToken ct) => throw new NotSupportedException();
        public Task MoveAsync(string a, string b, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class UnusedFileLog : IOpisFileLog
    {
        public Task<int> UpsertAsync(OpisFileContext f, string s, int n, string? e, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
