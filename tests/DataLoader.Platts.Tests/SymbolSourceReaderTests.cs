using System.Globalization;
using DataLoader.Core.Sources;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DataLoader.Platts.Tests;

/// <summary>
/// Group B (spec §6.2) — the reference-metadata CSV parser. Quote-aware, 14
/// positional columns, US-format dates parsed independent of machine locale.
/// </summary>
public class SymbolSourceReaderTests
{
    private const string CsvPath = "symbols/csv-version/AA_sym.csv";

    // Header + 3 data rows:
    //  - Row 1: full row; Description has a comma inside quotes and a doubled "" escape.
    //  - Row 2: missing Symbol -> skipped+logged.
    //  - Row 3: mostly blank cells -> nulls (Symbol present).
    private const string Csv =
        "MDC,Trans,Symbol,Bates,Freq,Curr,UOM,DEC,Conv,*/,To_UOM,Earliest,Latest,Description\n" +
        "GD,T1,AEWAA00,cuw,DA,BRL,LTR,3,1.5,F,GAL,4/1/2026,6/25/2026,\"Argus, Inc. \"\"premium\"\" grade\"\n" +
        "GD,T2,,xyz,DA,USD,BBL,2,2.0,G,GAL,4/1/2026,6/25/2026,No symbol row\n" +
        ",,BBBBB00,,,,,,,,,,,\n";

    private static SymbolWorkUnit Unit() =>
        new() { File = new RemoteFile(CsvPath, "AA_sym.csv", 1024, new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)) };

    private static async Task<IReadOnlyList<SymbolRow>> ParseAsync(string csv)
    {
        var sftp = new FakePlattsSftp();
        sftp.AddTextFile(CsvPath, csv);
        var settings = Options.Create(new PlattsSettings { SymbolsDirectory = "symbols/csv-version" });
        var reader = new SymbolSourceReader(sftp, settings, NullLogger<SymbolSourceReader>.Instance);
        return await reader.ReadAsync(Unit(), CancellationToken.None);
    }

    [Fact]
    public async Task Parse_SkipsHeader_MapsAll14ColumnsPositionally()
    {
        var rows = await ParseAsync(Csv);

        // Header skipped; row2 (missing Symbol) skipped -> 2 rows.
        Assert.Equal(2, rows.Count);

        var r = rows[0];
        Assert.Equal("GD", r.MDC);
        Assert.Equal("T1", r.Trans);
        Assert.Equal("AEWAA00", r.Symbol);
        Assert.Equal("cuw", r.Bates);
        Assert.Equal("DA", r.Freq);
        Assert.Equal("BRL", r.Curr);
        Assert.Equal("LTR", r.UOM);
        Assert.Equal(3, r.Dec);          // DEC -> int
        Assert.Equal(1.5m, r.Conv);      // Conv -> decimal
        Assert.Equal("F", r.Flag);       // the "*/" column
        Assert.Equal("GAL", r.ToUom);
        Assert.Equal(new DateTime(2026, 4, 1), r.Earliest);
        Assert.Equal(new DateTime(2026, 6, 25), r.Latest);
    }

    [Fact]
    public async Task Parse_QuotedComma_StaysInDescription_AndDoubledQuoteUnescaped()
    {
        var rows = await ParseAsync(Csv);

        // "Argus, Inc. ""premium"" grade" -> comma kept, "" -> literal "
        Assert.Equal("Argus, Inc. \"premium\" grade", rows[0].Description);
    }

    [Fact]
    public async Task Parse_MissingSymbolRow_IsSkipped()
    {
        var rows = await ParseAsync(Csv);

        // Row 2 (Symbol column empty) must not appear; the surviving rows are the good ones.
        Assert.Equal(new[] { "AEWAA00", "BBBBB00" }, rows.Select(r => r.Symbol).ToArray());
    }

    [Fact]
    public async Task Parse_BlankCells_BecomeNulls()
    {
        var rows = await ParseAsync(Csv);

        var blank = rows.Single(r => r.Symbol == "BBBBB00");
        Assert.Null(blank.MDC);
        Assert.Null(blank.Trans);
        Assert.Null(blank.Bates);
        Assert.Null(blank.Dec);
        Assert.Null(blank.Conv);
        Assert.Null(blank.Earliest);
        Assert.Null(blank.Latest);
        Assert.Null(blank.Description);
    }

    [Fact]
    public async Task Parse_UsDates_AreLocaleIndependent()
    {
        // Force a non-US, day-first culture. The parser hard-codes en-US M/d/yyyy,
        // so "4/1/2026" must be April 1 (not January 4) regardless of the machine locale.
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var rows = await ParseAsync(Csv);

            Assert.Equal(new DateTime(2026, 4, 1), rows[0].Earliest);
            Assert.Equal(new DateTime(2026, 6, 25), rows[0].Latest);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public async Task Parse_DecAndConv_AreInvariant()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            // de-DE uses ',' as the decimal separator; the parser must still read "1.5" as 1.5.
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var rows = await ParseAsync(Csv);

            Assert.Equal(3, rows[0].Dec);
            Assert.Equal(1.5m, rows[0].Conv);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
