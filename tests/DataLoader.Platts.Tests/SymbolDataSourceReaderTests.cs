using DataLoader.Core.Sources;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DataLoader.Platts.Tests;

/// <summary>
/// Group A (spec §5.2) — the <c>.ftp</c> market-file parser. Fed from an in-memory
/// <see cref="FakePlattsSftp"/>; no live SFTP.
/// </summary>
public class SymbolDataSourceReaderTests
{
    private const string FullPath = "/20260731/market.ftp";
    private static readonly DateTime LastMod = new(2026, 7, 31, 23, 45, 0, DateTimeKind.Utc);
    private static readonly RemoteFile File = new(FullPath, "market.ftp", 4096, LastMod);

    // Exact sample from the spec: copyright banner, PlattsMarketData header (with
    // extra middle tokens), an N line with a value, an X line with a trailing space
    // and NO value, an N line with a large integer value, plus lines that must be
    // skipped: a length-1 symbol token, a non-numeric value, and a bad date.
    private const string Sample =
        "© 2026 by S&P Global Inc.\n" +
        "PlattsMarketData 202607312341 10511 FINAL     GD  20260731\n" +
        "N AEWAA00c 202607310000 27.11\n" +
        "X ANTAE00u 202607310000 \n" +          // trailing space, no value -> null
        "N ANTAL00w 202607300000 7500000\n" +   // large integer value
        "N x 202607310000 1.0\n" +              // symbol/bate token length 1 -> skipped
        "N YYYYYY0c 202607310000 not-a-number\n" + // unparseable value -> skipped
        "N ZZZZZ00c abcdefghijkl 5.0\n";        // unparseable date -> skipped

    private static SymbolDataWorkUnit Unit(RemoteFile file, string folder = "20260731") =>
        new() { File = file, Folder = folder };

    private static SymbolDataSourceReader ReaderFor(FakePlattsSftp sftp) =>
        new(sftp, NullLogger<SymbolDataSourceReader>.Instance);

    private static async Task<IReadOnlyList<SymbolDataRow>> ParseAsync(string content, RemoteFile file, string folder = "20260731")
    {
        var sftp = new FakePlattsSftp();
        sftp.AddTextFile(file.FullPath, content);
        return await ReaderFor(sftp).ReadAsync(Unit(file, folder), CancellationToken.None);
    }

    [Fact]
    public async Task Parse_SkipsCopyright_AndParsesHeaderOntoEveryRow()
    {
        var rows = await ParseAsync(Sample, File);

        // 3 good rows survive (AEWAA00, ANTAE00, ANTAL00); 3 malformed lines skipped.
        Assert.Equal(3, rows.Count);

        // Header stamped on every row: MDC = token[^2] = "GD",
        // ActionDate = token[1] "202607312341" -> 2026-07-31 23:41.
        var actionDate = new DateTime(2026, 7, 31, 23, 41, 0);
        Assert.All(rows, r => Assert.Equal("GD", r.MDC));
        Assert.All(rows, r => Assert.Equal(actionDate, r.ActionDate));
    }

    [Fact]
    public async Task Parse_SplitsSymbolAndBate()
    {
        var rows = await ParseAsync(Sample, File);

        var first = rows[0];
        Assert.Equal("AEWAA00", first.Symbol);
        Assert.Equal("c", first.Bate);

        // The length-1 symbol/bate token ("x") was skipped, so no row has an empty symbol.
        Assert.All(rows, r => Assert.False(string.IsNullOrEmpty(r.Symbol)));
        Assert.DoesNotContain(rows, r => r.Symbol == "" || r.Bate == "");
    }

    [Fact]
    public async Task Parse_PerRowDate_FromThirdToken()
    {
        var rows = await ParseAsync(Sample, File);

        Assert.Equal(new DateTime(2026, 7, 31, 0, 0, 0), rows[0].Date); // 202607310000
        Assert.Equal(new DateTime(2026, 7, 30, 0, 0, 0), rows[2].Date); // 202607300000
    }

    [Fact]
    public async Task Parse_Value_PresentBlankAndLargeInteger()
    {
        var rows = await ParseAsync(Sample, File);

        Assert.Equal(27.11m, rows[0].Value);      // present decimal
        Assert.Null(rows[1].Value);               // X line, trailing space, no 4th token -> null
        Assert.Equal(7500000m, rows[2].Value);    // large integer -> decimal
    }

    [Fact]
    public async Task Parse_StampsSourcePathAndCarryFields()
    {
        var rows = await ParseAsync(Sample, File);

        Assert.All(rows, r =>
        {
            Assert.Equal("20260731\\market.ftp", r.SourcePath); // {folder}\{file}
            Assert.Equal("market.ftp", r.FileName);
            Assert.Equal(LastMod, r.LastModifiedUtc);
            Assert.Equal(4096L, r.SizeBytes);
        });
    }

    [Fact]
    public async Task Parse_ActionColumn_FromFirstToken()
    {
        var rows = await ParseAsync(Sample, File);

        Assert.Equal("N", rows[0].Action);
        Assert.Equal("X", rows[1].Action);
        Assert.Equal("N", rows[2].Action);
    }

    [Fact]
    public async Task Parse_HeaderFileDateMatchesFolder_NoThrow()
    {
        // token[^1] "20260731" matches folder "20260731"; parsing must succeed cleanly.
        var rows = await ParseAsync(Sample, File, folder: "20260731");
        Assert.Equal(3, rows.Count);
    }

    [Fact]
    public async Task Parse_M1_BadLineInMiddle_IsSkipped_GoodRowsAfterStillParse()
    {
        // Regression M1: a non-numeric value / unparseable date must be skipped
        // WITHOUT throwing and WITHOUT losing the good rows that follow.
        const string content =
            "© 2026 by S&P Global Inc.\n" +
            "PlattsMarketData 202607312341 10511 FINAL     GD  20260731\n" +
            "N AEWAA00c 202607310000 27.11\n" +        // good (before bad)
            "N YYYYYY0c 202607310000 not-a-number\n" + // bad value (middle) -> skipped
            "N BADDATE0c abcdefghijkl 5.0\n" +         // bad date (middle) -> skipped
            "N ANTAL00w 202607300000 7500000\n";       // good (after bad)

        var rows = await ParseAsync(content, File);

        Assert.Equal(2, rows.Count);
        Assert.Equal(new[] { "AEWAA00", "ANTAL00" }, rows.Select(r => r.Symbol).ToArray());
    }

    [Fact]
    public async Task Parse_MalformedHeader_TooFewTokens_Throws()
    {
        // A header with < 4 tokens is a structural error and must fail the unit.
        const string content =
            "© 2026 by S&P Global Inc.\n" +
            "PlattsMarketData 202607312341\n"; // only 2 tokens

        var sftp = new FakePlattsSftp();
        sftp.AddTextFile(FullPath, content);

        await Assert.ThrowsAsync<FormatException>(
            () => ReaderFor(sftp).ReadAsync(Unit(File), CancellationToken.None));
    }

    [Fact]
    public async Task Parse_FewerThanTwoLines_ReturnsEmpty()
    {
        // Header-only / near-empty files produce no rows and don't throw.
        var rows = await ParseAsync("© only the banner\n", File);
        Assert.Empty(rows);
    }
}
