using System.Text;

namespace DataLoader.ModernCommodities;

/// <summary>
/// Quote-aware RFC-4180 CSV tokenizer (design §5.1), adapted from
/// <c>src/DataLoader.CWG/CwgCsv.cs</c> (itself lifted from StormVista) with <b>the CWG-only
/// <c>END.</c> terminator special case removed</b> — ModCom files have no terminator line, and
/// keeping that branch would let a hypothetical single-field row silently truncate the payload.
///
/// <para>Kept: quote awareness, doubled <c>""</c> → a literal <c>"</c>, embedded CR/LF inside
/// quotes, a single leading UTF-8 BOM stripped, and truly blank lines dropped.</para>
///
/// <para><b>⚠⚠ NEVER <c>line.Split(',')</c>.</b> <b>Every</b> field in <b>every</b> row of
/// <b>all three</b> endpoints is double-quoted (an empty value appears as <c>""</c>), and five
/// columns legitimately carry commas <i>inside</i> the quotes: <c>Bid Legal Name</c>,
/// <c>Offer Legal Name</c>, <c>Bid Address</c>, <c>Offer Address</c>, <c>Contract Terms</c>. Real
/// observed values:
/// <code>
/// "P.O. Box 2844, 150 - 6 Avenue SW, Calgary, AB T2P 3E3"   &lt;- 4 embedded commas
/// "1001 Fannin Street, Suite 1500, Houston, TX 77002"        &lt;- 3 embedded commas
/// "ARM Energy Management, LLC"                               &lt;- a comma inside a LEGAL NAME
/// </code>
/// A naive split on the first value produces 4 extra columns and shifts every subsequent value
/// left, corrupting <c>Bid Commission</c> through <c>Product Type</c> on that row — <b>silently, on
/// an HTTP 200</b>.</para>
///
/// <para>Transport notes from the verified <c>Content-Length</c> arithmetic: the line terminator is
/// a single <b>LF</b> (not CRLF) — a parser that <i>requires</i> <c>\r\n</c> treats the whole
/// payload as one line, so both are accepted; there is <b>no</b> UTF-8 BOM (BOM tolerance is
/// harmless, but nothing may depend on one); and no observed field contains a <c>"</c> or an
/// embedded newline, so quote-escaping was never exercised live — which is exactly why a
/// hand-rolled splitter is not acceptable.</para>
/// </summary>
internal static class ModComCsv
{
    public static IReadOnlyList<string[]> Parse(string content)
    {
        // Strip a single leading UTF-8 BOM if present — Trim() does not remove U+FEFF, and a
        // BOM-prefixed body would otherwise defeat the header[0] literal-name match.
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
            // Drop TRULY blank lines only (a bare newline). A structural ",,,,," row survives as an
            // array of empty strings — a legitimately all-blank data row must not vanish silently.
            if (!(fields.Count == 1 && fields[0].Length == 0))
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
                        i++; // consume the LF of a CRLF pair
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
            EndRecord(); // flush a trailing record that is not newline-terminated

        return records;
    }
}
