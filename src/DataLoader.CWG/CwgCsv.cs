using System.Text;

namespace DataLoader.CWG;

/// <summary>
/// Minimal quote-aware RFC-4180 CSV tokenizer, lifted verbatim from StormVista's
/// <c>StormVistaCsv.Parse</c> (design §4). Honours quoted fields with embedded
/// commas, doubled <c>""</c> escapes and embedded CR/LF, strips a single leading
/// UTF-8 BOM, and <b>drops only truly blank lines</b> (a bare newline) — a
/// structural <c>,,,,,</c> row survives as an array of empty strings, which
/// shapes C/D/E rely on to detect block separators and matrix widths.
/// </summary>
internal static class CwgCsv
{
    public static IReadOnlyList<string[]> Parse(string content)
    {
        // Strip a single leading UTF-8 BOM if present — Trim() does not remove
        // U+FEFF, and a BOM-prefixed body would otherwise defeat header[0] checks.
        if (content.Length > 0 && content[0] == '﻿')
            content = content.Substring(1);

        var records = new List<string[]>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        void EndField()
        {
            fields.Add(field.ToString());
            field.Clear();
        }

        void EndRecord()
        {
            EndField();
            if (!(fields.Count == 1 && fields[0].Length == 0)) // drop truly blank lines
                records.Add(fields.ToArray());
            fields.Clear();
        }

        for (var i = 0; i < content.Length; i++)
        {
            var c = content[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < content.Length && content[i + 1] == '"')
                    {
                        field.Append('"'); // doubled quote → literal quote
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(c);
                }
                continue;
            }

            switch (c)
            {
                case '"':
                    inQuotes = true;
                    break;
                case ',':
                    EndField();
                    break;
                case '\r':
                    if (i + 1 < content.Length && content[i + 1] == '\n')
                        i++; // consume LF of a CRLF pair
                    EndRecord();
                    break;
                case '\n':
                    EndRecord();
                    break;
                default:
                    field.Append(c);
                    break;
            }
        }

        if (field.Length > 0 || fields.Count > 0)
            EndRecord(); // flush a trailing record not newline-terminated

        return records;
    }
}
