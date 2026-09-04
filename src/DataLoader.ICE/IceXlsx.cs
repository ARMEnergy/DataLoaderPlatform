using System.IO.Compression;
using System.Xml.Linq;

namespace DataLoader.ICE;

/// <summary>
/// Minimal OOXML worksheet reader for the one XLSX feed (<c>IFLL_Options</c>).
///
/// <para>
/// Written against <c>System.IO.Compression</c> + <c>System.Xml.Linq</c> rather
/// than pulling in ClosedXML or EPPlus: the feed is a single flat sheet of 12
/// string columns, and a spreadsheet library would be a large dependency (and a
/// supply-chain surface) for a job this small.
/// </para>
///
/// <para>
/// <b>Two hazards this exists to handle</b> (both verified live,
/// <c>docs/apis/ICE.md</c> 4.4):
/// </para>
/// <list type="number">
///   <item>
///     <b>Sparse cells.</b> A row omits the <c>&lt;c&gt;</c> element entirely for a
///     blank cell — row 2 of the real file is <c>A,B,C,D,F,G</c> with <b>no E</b>.
///     Reading cells in document order would shift <c>PUT/CALL</c> into the
///     <c>STRIKE</c> slot and every later column with it. Cells are therefore placed
///     by decoding their <c>r</c> reference (<c>E2</c> → index 4), never by order.
///   </item>
///   <item>
///     <b>Shared strings.</b> Nearly every value, including all the dates, is an
///     index into <c>xl/sharedStrings.xml</c> rather than an inline value.
///   </item>
/// </list>
///
/// <para>
/// Dates are returned as the raw shared-string text (<c>08/28/2026</c>,
/// <c>2026-09-01</c>) and parsed by <see cref="IceConvert"/> like every other feed.
/// Excel serial-number dates are deliberately NOT interpreted — this file does not
/// use them, and guessing at a bare number's style would be a way to invent wrong
/// dates rather than fail on them.
/// </para>
/// </summary>
internal static class IceXlsx
{
    private static readonly XNamespace Main =
        "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    private static readonly XNamespace RelNs =
        "http://schemas.openxmlformats.org/package/2006/relationships";

    private static readonly XNamespace DocRelNs =
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    /// <summary>
    /// Read the first worksheet into records, each padded to <paramref name="width"/>
    /// columns so a short or sparse row is never ragged.
    /// </summary>
    /// <param name="content">The raw .xlsx bytes.</param>
    /// <param name="width">Column count to pad to — the descriptor's header count.</param>
    public static List<string[]> ParseRecords(byte[] content, int width)
    {
        using var stream = new MemoryStream(content, writable: false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        var sharedStrings = ReadSharedStrings(archive);
        var sheetEntry = ResolveFirstSheet(archive)
            ?? throw new InvalidDataException("IFLL xlsx contains no worksheet part.");

        using var sheetStream = sheetEntry.Open();
        var sheet = XDocument.Load(sheetStream);

        var records = new List<string[]>();

        foreach (var row in sheet.Descendants(Main + "row"))
        {
            var cells = new string[width];
            Array.Fill(cells, string.Empty);
            var any = false;

            foreach (var cell in row.Elements(Main + "c"))
            {
                var reference = (string?)cell.Attribute("r");
                var index = ColumnIndex(reference);

                // Outside the descriptor's width: a trailing note column or similar.
                // Ignored rather than treated as an error.
                if (index < 0 || index >= width) continue;

                var text = CellText(cell, sharedStrings);
                if (text.Length > 0) any = true;
                cells[index] = text;
            }

            // Skip a wholly empty row rather than emitting a record of blanks that
            // would later be dropped for a missing required column and inflate the
            // drop count.
            if (any) records.Add(cells);
        }

        return records;
    }

    /// <summary>
    /// Decode the column part of a cell reference to a zero-based index:
    /// <c>A1</c> → 0, <c>E2</c> → 4, <c>AA10</c> → 26. Returns -1 for a malformed or
    /// absent reference.
    /// </summary>
    internal static int ColumnIndex(string? cellReference)
    {
        if (string.IsNullOrEmpty(cellReference)) return -1;

        var index = 0;
        var sawLetter = false;

        foreach (var ch in cellReference)
        {
            if (ch is >= 'A' and <= 'Z') { index = index * 26 + (ch - 'A' + 1); sawLetter = true; }
            else if (ch is >= 'a' and <= 'z') { index = index * 26 + (ch - 'a' + 1); sawLetter = true; }
            else break; // digits begin the row part
        }

        return sawLetter ? index - 1 : -1;
    }

    /// <summary>Resolve one cell's text, following the shared-string table when needed.</summary>
    private static string CellText(XElement cell, IReadOnlyList<string> sharedStrings)
    {
        var type = (string?)cell.Attribute("t");

        // Inline string: the text lives under <is>, not <v>.
        if (type == "inlineStr")
            return string.Concat(cell.Element(Main + "is")?.Descendants(Main + "t").Select(t => t.Value)
                                 ?? Enumerable.Empty<string>()).Trim();

        var raw = cell.Element(Main + "v")?.Value;
        if (raw is null) return string.Empty;

        if (type == "s")
        {
            return int.TryParse(raw, out var idx) && idx >= 0 && idx < sharedStrings.Count
                ? sharedStrings[idx]
                : string.Empty;
        }

        // "str" (formula result), "n"/absent (number), "b" (boolean) all carry their
        // value directly. Numbers keep their invariant text form, which is what
        // IceConvert expects.
        return raw.Trim();
    }

    /// <summary>
    /// Read <c>xl/sharedStrings.xml</c>. Handles both the plain
    /// <c>&lt;si&gt;&lt;t&gt;</c> form and the rich-text <c>&lt;si&gt;&lt;r&gt;&lt;t&gt;</c>
    /// runs, which must be concatenated.
    /// </summary>
    private static List<string> ReadSharedStrings(ZipArchive archive)
    {
        var result = new List<string>();

        var entry = archive.GetEntry("xl/sharedStrings.xml");
        if (entry is null) return result;

        using var stream = entry.Open();
        var doc = XDocument.Load(stream);

        foreach (var si in doc.Root?.Elements(Main + "si") ?? Enumerable.Empty<XElement>())
            result.Add(string.Concat(si.Descendants(Main + "t").Select(t => t.Value)).Trim());

        return result;
    }

    /// <summary>
    /// Find the part backing the workbook's FIRST sheet, following
    /// <c>xl/_rels/workbook.xml.rels</c>. Falls back to <c>xl/worksheets/sheet1.xml</c>
    /// and then to any worksheet part, so an unusual package layout still loads.
    /// </summary>
    private static ZipArchiveEntry? ResolveFirstSheet(ZipArchive archive)
    {
        var workbook = archive.GetEntry("xl/workbook.xml");
        var rels = archive.GetEntry("xl/_rels/workbook.xml.rels");

        if (workbook is not null && rels is not null)
        {
            try
            {
                using var workbookStream = workbook.Open();
                var workbookDoc = XDocument.Load(workbookStream);

                var relId = workbookDoc.Descendants(Main + "sheet")
                    .Select(s => (string?)s.Attribute(DocRelNs + "id"))
                    .FirstOrDefault(id => !string.IsNullOrEmpty(id));

                if (!string.IsNullOrEmpty(relId))
                {
                    using var relsStream = rels.Open();
                    var relsDoc = XDocument.Load(relsStream);

                    var target = relsDoc.Descendants(RelNs + "Relationship")
                        .Where(r => (string?)r.Attribute("Id") == relId)
                        .Select(r => (string?)r.Attribute("Target"))
                        .FirstOrDefault();

                    if (!string.IsNullOrEmpty(target))
                    {
                        var path = target!.StartsWith('/')
                            ? target.TrimStart('/')
                            : "xl/" + target.Replace("./", string.Empty);

                        var resolved = archive.GetEntry(path);
                        if (resolved is not null) return resolved;
                    }
                }
            }
            catch (System.Xml.XmlException)
            {
                // Malformed workbook part — fall through to the conventional path.
            }
        }

        return archive.GetEntry("xl/worksheets/sheet1.xml")
            ?? archive.Entries.FirstOrDefault(e =>
                   e.FullName.StartsWith("xl/worksheets/", StringComparison.OrdinalIgnoreCase) &&
                   e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));
    }
}
