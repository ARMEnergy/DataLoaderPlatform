using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Xunit;

namespace DataLoader.ICE.Tests;

/// <summary>
/// The XLSX reader, exercised against packages built to mirror the real
/// <c>IFLL_Options</c> file — including the sparse-cell layout that makes a
/// positional read wrong.
/// </summary>
public sealed class XlsxTests
{
    private static readonly string[] IfllHeaders =
    {
        "TRADE DATE", "CONTRACT", "STRIP", "EXPIRATION_DATE", "STRIKE", "PUT/CALL",
        "SETTLEMENT_PRICE", "VOLATILITY", "DELTA", "GAMMA", "THETA", "VEGA"
    };

    [Theory]
    [InlineData("A1", 0)]
    [InlineData("B2", 1)]
    [InlineData("E2", 4)]
    [InlineData("L16420", 11)]
    [InlineData("Z1", 25)]
    [InlineData("AA10", 26)]
    [InlineData("AB1", 27)]
    [InlineData(null, -1)]
    [InlineData("", -1)]
    [InlineData("123", -1)]
    public void Column_reference_decodes_to_a_zero_based_index(string? reference, int expected)
    {
        Assert.Equal(expected, IceXlsx.ColumnIndex(reference));
    }

    /// <summary>
    /// ⚠ The hazard this reader exists for. The real file's row 2 is
    /// <c>A,B,C,D,F,G</c> with <b>no E</b> — STRIKE is absent because it is an
    /// underlying-future row. Reading cells in document order would slide
    /// <c>PUT/CALL</c> into the STRIKE slot and shift everything after it.
    /// </summary>
    [Fact]
    public void Sparse_row_keeps_every_value_in_its_own_column()
    {
        // null = the <c> element is omitted entirely, exactly as Excel writes it.
        var xlsx = XlsxBuilder.Build(IfllHeaders, new[]
        {
            new string?[] { "08/28/2026", "ZAX", "2026-09-01", "09/18/2026", null, "F", "0", null, null, null, null, null }
        });

        var records = IceXlsx.ParseRecords(xlsx, 64);

        Assert.Equal(2, records.Count);

        var row = records[1];
        Assert.Equal("08/28/2026", row[0]);   // TRADE DATE
        Assert.Equal("ZAX", row[1]);          // CONTRACT
        Assert.Equal("2026-09-01", row[2]);   // STRIP
        Assert.Equal("09/18/2026", row[3]);   // EXPIRATION_DATE
        Assert.Equal(string.Empty, row[4]);   // STRIKE — absent, and STILL index 4
        Assert.Equal("F", row[5]);            // PUT/CALL — NOT shifted into index 4
        Assert.Equal("0", row[6]);            // SETTLEMENT_PRICE
    }

    [Fact]
    public void Header_row_is_returned_first()
    {
        var xlsx = XlsxBuilder.Build(IfllHeaders, Array.Empty<string?[]>());
        var records = IceXlsx.ParseRecords(xlsx, 64);

        Assert.Single(records);
        Assert.Equal("TRADE DATE", records[0][0]);
        Assert.Equal("PUT/CALL", records[0][5]);
        Assert.Equal("VEGA", records[0][11]);
    }

    [Fact]
    public void Wholly_empty_rows_are_skipped()
    {
        var xlsx = XlsxBuilder.Build(IfllHeaders, new[]
        {
            new string?[] { "08/28/2026", "ZAX", "2026-09-01", "09/18/2026", "100", "C", "1", null, null, null, null, null },
            new string?[] { null, null, null, null, null, null, null, null, null, null, null, null }
        });

        var records = IceXlsx.ParseRecords(xlsx, 64);

        // header + the one real row; the blank row is dropped so it never inflates
        // the "dropped for a missing required value" count.
        Assert.Equal(2, records.Count);
    }

    /// <summary>
    /// The full IFLL path end to end: sparse blank-strike row dropped, real option
    /// row kept, and the <c>PUT/CALL</c> spelling resolved against the descriptor's
    /// <c>PUT_CALL</c> column.
    /// </summary>
    [Fact]
    public void Ifll_feed_parses_and_drops_only_the_blank_strike_row()
    {
        var feed = IceDescriptors.Find("IfllOptions")!;
        var table = feed.Table;

        var xlsx = XlsxBuilder.Build(IfllHeaders, new[]
        {
            // underlying future — no strike, must be dropped
            new string?[] { "08/28/2026", "ZAX", "2026-09-01", "09/18/2026", null, "F", "0", null, null, null, null, null },
            // real option — must be kept
            new string?[] { "08/28/2026", "ZAX", "2026-09-01", "09/18/2026", "9750.0000", "C",
                            "12.5000", "18.2500", "0.4500", "0.0002", "-1.2500", "3.7500" }
        });

        var (rows, dropped) = TestHelpers.Reader(feed).Parse(xlsx, "https://downloads.ice.com/x.xlsx");

        Assert.Equal(1, dropped);
        var row = Assert.Single(rows);

        Assert.Equal(new DateTime(2026, 8, 28), row.Value(table, "TRADE_DATE"));
        Assert.Equal("ZAX", row.Value(table, "CONTRACT"));
        Assert.Equal("2026-09-01", row.Value(table, "STRIP"));
        Assert.Equal(new DateTime(2026, 9, 18), row.Value(table, "EXPIRATION_DATE"));
        Assert.Equal(9750.0000m, row.Value(table, "STRIKE"));
        Assert.Equal("C", row.Value(table, "PUT_CALL"));
        Assert.Equal(3.7500m, row.Value(table, "VEGA"));
    }

    /// <summary>
    /// The XLSX writes its trade date <c>MM/dd/yyyy</c> and its strip
    /// <c>yyyy-MM-dd</c> in the same row. Both must land correctly.
    /// </summary>
    [Fact]
    public void Mixed_date_formats_within_one_row_both_parse()
    {
        var feed = IceDescriptors.Find("IfllOptions")!;
        var table = feed.Table;

        var xlsx = XlsxBuilder.Build(IfllHeaders, new[]
        {
            new string?[] { "08/28/2026", "ZAX", "2026-09-01", "09/18/2026", "100.0", "P",
                            "1.0", null, null, null, null, null }
        });

        var (rows, _) = TestHelpers.Reader(feed).Parse(xlsx, "u");
        var row = Assert.Single(rows);

        Assert.Equal(new DateTime(2026, 8, 28), row.Value(table, "TRADE_DATE"));
        Assert.Equal(new DateTime(2026, 9, 18), row.Value(table, "EXPIRATION_DATE"));
        Assert.Equal("2026-09-01", row.Value(table, "STRIP"));   // STRIP is VARCHAR — kept verbatim
    }

    [Fact]
    public void Rich_text_shared_strings_are_concatenated()
    {
        var xlsx = XlsxBuilder.BuildWithRichTextHeader();
        var records = IceXlsx.ParseRecords(xlsx, 16);

        Assert.Equal("TRADE DATE", records[0][0]);
    }
}

/// <summary>
/// Builds minimal but structurally real .xlsx packages for the tests.
///
/// <para>
/// A <c>null</c> cell value OMITS the <c>&lt;c&gt;</c> element entirely rather than
/// writing an empty one — that is exactly how Excel (and ICE's generator) produce
/// the sparse rows the reader has to survive.
/// </para>
/// </summary>
internal static class XlsxBuilder
{
    private const string MainNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string PkgRelNs = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string DocRelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    public static byte[] Build(IReadOnlyList<string> headers, IReadOnlyList<string?[]> rows)
    {
        // Every value goes through the shared-string table, matching the real file.
        var strings = new List<string>();
        var lookup = new Dictionary<string, int>(StringComparer.Ordinal);

        int Intern(string value)
        {
            if (lookup.TryGetValue(value, out var existing)) return existing;
            lookup[value] = strings.Count;
            strings.Add(value);
            return strings.Count - 1;
        }

        var sheet = new StringBuilder();
        sheet.Append($"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"{MainNs}\"><sheetData>");

        sheet.Append("<row r=\"1\">");
        for (var c = 0; c < headers.Count; c++)
            sheet.Append($"<c r=\"{ColumnName(c)}1\" t=\"s\"><v>{Intern(headers[c])}</v></c>");
        sheet.Append("</row>");

        for (var r = 0; r < rows.Count; r++)
        {
            var rowNumber = r + 2;
            sheet.Append($"<row r=\"{rowNumber}\">");

            var row = rows[r];
            for (var c = 0; c < row.Length; c++)
            {
                // null => omit the cell entirely (the sparse-row case).
                if (row[c] is null) continue;
                sheet.Append($"<c r=\"{ColumnName(c)}{rowNumber}\" t=\"s\"><v>{Intern(row[c]!)}</v></c>");
            }

            sheet.Append("</row>");
        }

        sheet.Append("</sheetData></worksheet>");

        var sharedStrings = new StringBuilder();
        sharedStrings.Append($"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><sst xmlns=\"{MainNs}\" count=\"{strings.Count}\" uniqueCount=\"{strings.Count}\">");
        foreach (var value in strings)
            sharedStrings.Append($"<si><t>{Escape(value)}</t></si>");
        sharedStrings.Append("</sst>");

        return Package(sheet.ToString(), sharedStrings.ToString());
    }

    /// <summary>A package whose first shared string is split into rich-text runs.</summary>
    public static byte[] BuildWithRichTextHeader()
    {
        var sheet =
            $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"{MainNs}\">" +
            "<sheetData><row r=\"1\"><c r=\"A1\" t=\"s\"><v>0</v></c></row></sheetData></worksheet>";

        var sharedStrings =
            $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><sst xmlns=\"{MainNs}\" count=\"1\" uniqueCount=\"1\">" +
            "<si><r><t>TRADE </t></r><r><t>DATE</t></r></si></sst>";

        return Package(sheet, sharedStrings);
    }

    private static byte[] Package(string sheetXml, string sharedStringsXml)
    {
        using var buffer = new MemoryStream();

        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(archive, "[Content_Types].xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                "</Types>");

            Write(archive, "xl/workbook.xml",
                $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                $"<workbook xmlns=\"{MainNs}\" xmlns:r=\"{DocRelNs}\">" +
                "<sheets><sheet name=\"Sheet_1\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");

            Write(archive, "xl/_rels/workbook.xml.rels",
                $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                $"<Relationships xmlns=\"{PkgRelNs}\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
                "</Relationships>");

            Write(archive, "xl/sharedStrings.xml", sharedStringsXml);
            Write(archive, "xl/worksheets/sheet1.xml", sheetXml);
        }

        return buffer.ToArray();
    }

    private static void Write(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path);
        using var stream = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static string ColumnName(int index)
    {
        var name = string.Empty;
        var n = index;

        do
        {
            name = (char)('A' + n % 26) + name;
            n = n / 26 - 1;
        }
        while (n >= 0);

        return name;
    }

    private static string Escape(string value) => new XText(value).ToString();
}
