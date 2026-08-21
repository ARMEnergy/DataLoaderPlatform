using Xunit;

namespace DataLoader.OPIS.Tests;

/// <summary>
/// CSV parsing and row mapping against the live file shape: comma-delimited, one
/// header row, ten fields, space-padded values, <c>MM/dd/yy</c> two-digit-year
/// dates, and blank Low/High on basket rows.
/// </summary>
public class ParseTests
{
    private static readonly string[] Formats =
        { "MM/dd/yy", "M/d/yy", "MM/dd/yyyy", "M/d/yyyy", "yyyy-MM-dd" };

    private static readonly DateOnly FileDate = new(2026, 8, 20);

    private static OpisLpReportRow? Row(string line, out string? reason, DateOnly? fileDate = null) =>
        OpisLpReportRow.From(OpisCsv.SplitLine(line), fileDate ?? FileDate, Formats, out reason);

    // ================================================================== happy path

    [Fact]
    public void MapsEveryColumn_AndTrimsThePaddedKey()
    {
        var r = Row("I,LOS ANGELES PRO     ,08/20/26, 85.8750, 89.8750, 87.8750,US,GAL,A,D", out var reason);

        Assert.NotNull(r);
        Assert.Null(reason);
        Assert.Equal("I", r!.Price);
        Assert.Equal("LOS ANGELES PRO", r.MktProd);           // padding stripped — it is a PK column
        Assert.Equal(new DateOnly(2026, 8, 20), r.Date);
        Assert.Equal(85.8750m, r.Low);
        Assert.Equal(89.8750m, r.High);
        Assert.Equal(87.8750m, r.Avg);
        Assert.Equal("US", r.Country);
        Assert.Equal("GAL", r.Unit);
        Assert.Equal("A", r.Timing);
        Assert.Equal("D", r.Freq);
        Assert.Equal(FileDate, r.SourceFileDate);
    }

    [Fact]
    public void TwoDigitYear_ResolvesIntoThe2000s()
    {
        // The feed publishes MM/dd/yy. 08/20/26 must become 2026-08-20, not 1926.
        var r = Row("I,X,08/20/26,1,2,1.5,US,GAL,A,D", out _);
        Assert.Equal(new DateOnly(2026, 8, 20), r!.Date);
    }

    [Theory]
    [InlineData("08/20/26")]     // live format
    [InlineData("8/20/26")]      // unpadded
    [InlineData("08/20/2026")]   // four-digit year
    [InlineData("8/20/2026")]    // the shape quoted in the loader request
    [InlineData("2026-08-20")]   // ISO, defensive
    public void AcceptsEveryConfiguredDateShape(string date)
    {
        var r = Row($"I,X,{date},1,2,1.5,US,GAL,A,D", out _);
        Assert.Equal(new DateOnly(2026, 8, 20), r!.Date);
    }

    [Fact]
    public void MonthAndDayAreNotSwapped_RegardlessOfMachineCulture()
    {
        // Pinned to InvariantCulture: 03/04/26 is 4 March, never 3 April.
        var r = Row("I,X,03/04/26,1,2,1.5,US,GAL,A,D", out _);
        Assert.Equal(new DateOnly(2026, 3, 4), r!.Date);
    }

    // ================================================================== null handling

    [Fact]
    public void BasketRow_BlankLowAndHigh_BecomeNull_AvgSurvives()
    {
        var r = Row(Samples.BasketRowLine, out var reason, new DateOnly(2026, 7, 20));

        Assert.NotNull(r);
        Assert.Null(reason);                       // a blank price cell is legitimate, not a defect
        Assert.Equal("MT BEL NT BSKT", r!.MktProd);
        Assert.Null(r.Low);
        Assert.Null(r.High);
        Assert.Equal(73.0588m, r.Avg);
    }

    [Fact]
    public void NonNumericPriceCell_IsStoredAsNull_AndFlagged_ButRowIsKept()
    {
        var r = Row("I,X,08/20/26,N/A, 89.8750, 87.8750,US,GAL,A,D", out var reason);

        Assert.NotNull(r);                         // one bad cell must not cost the row
        Assert.Null(r!.Low);
        Assert.Equal(89.8750m, r.High);
        Assert.NotNull(reason);                    // ...but it is reported
    }

    // ================================================================== drop rules

    [Theory]
    [InlineData(" ,LOS ANGELES PRO,08/20/26,1,2,1.5,US,GAL,A,D", "Price")]
    [InlineData("I,                ,08/20/26,1,2,1.5,US,GAL,A,D", "Mkt_Prod")]
    [InlineData("I,LOS ANGELES PRO,08/20/26,1,2,1.5,US,GAL, ,D", "Timing")]
    public void RowMissingAKeyColumn_IsDropped(string line, string which)
    {
        var r = Row(line, out var reason);

        Assert.Null(r);
        Assert.Contains(which, reason);            // says WHICH key was missing
    }

    [Fact]
    public void UnparseableDate_DropsTheRow()
    {
        var r = Row("I,X,not-a-date,1,2,1.5,US,GAL,A,D", out var reason);
        Assert.Null(r);
        Assert.Contains("Date", reason);
    }

    [Theory]
    [InlineData("I,X,08/20/26,1,2,1.5,US,GAL,A")]                // 9 fields
    [InlineData("I,X,08/20/26,1,2,1.5,US,GAL,A,D,EXTRA")]        // 11 fields
    public void WrongFieldCount_IsDropped_NotShifted(string line)
    {
        // Critical: the TVP binds by position, so a short/long row must be dropped
        // rather than mapped with every column shifted one place.
        var r = Row(line, out var reason);
        Assert.Null(r);
        Assert.Contains("fields", reason);
    }

    // ================================================================== splitter

    [Fact]
    public void Splitter_HandlesQuotedFieldContainingAComma()
    {
        // The live feed never quotes, but an embedded comma must not shift columns.
        var fields = OpisCsv.SplitLine("I,\"HOUSTON, TX PRO\",08/20/26,1,2,1.5,US,GAL,A,D");

        Assert.Equal(10, fields.Length);
        Assert.Equal("HOUSTON, TX PRO", fields[1]);
        Assert.Equal("D", fields[9]);
    }

    [Fact]
    public void HeaderAndBlankLines_AreNotData()
    {
        Assert.True(OpisCsv.IsHeaderOrBlank("Price,Mkt_Prod,Date,Low,High,Avg,Country,Unit,Timing,Freq"));
        Assert.True(OpisCsv.IsHeaderOrBlank("   "));
        Assert.True(OpisCsv.IsHeaderOrBlank(""));
        Assert.False(OpisCsv.IsHeaderOrBlank("I,LOS ANGELES PRO,08/20/26,1,2,1.5,US,GAL,A,D"));
    }

    // ================================================================== the revision pair

    [Fact]
    public void InitialAndUpdated_ShareTheCurrentKey_ButDifferOnTheHistoryKey()
    {
        // This is the behaviour arm.LPReportHistory exists for. Both rows key the
        // SAME arm.LPReport row (Mkt_Prod, Date, Timing) — so the later 'U' supersedes
        // the 'I' there — but they are DISTINCT arm.LPReportHistory rows because Price
        // joins the key, which is what preserves the price change.
        var initial = Row(Samples.InitialSarniaLine, out _, new DateOnly(2026, 7, 30))!;
        var updated = Row(Samples.UpdatedSarniaLine, out _, new DateOnly(2026, 8, 3))!;

        // Same current-table key.
        Assert.Equal(initial.MktProd, updated.MktProd);
        Assert.Equal(initial.Date, updated.Date);
        Assert.Equal(initial.Timing, updated.Timing);

        // Different history-table key.
        Assert.NotEqual(initial.Price, updated.Price);
        Assert.Equal("I", initial.Price);
        Assert.Equal("U", updated.Price);

        // ...and the values genuinely changed, which is the point.
        Assert.Equal(81.5000m, initial.High);
        Assert.Equal(82.0000m, updated.High);
        Assert.Equal(81.3750m, initial.Avg);
        Assert.Equal(81.6250m, updated.Avg);

        // The revision comes from a LATER file, so the merge's ordering guard lets it win.
        Assert.True(updated.SourceFileDate > initial.SourceFileDate);
    }
}
