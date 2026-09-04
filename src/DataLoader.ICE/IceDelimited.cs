using System.Text;

namespace DataLoader.ICE;

/// <summary>
/// Record splitting for both ICE text shapes: the pipe-delimited <c>.dat</c>
/// settlement files and the RFC 4180 comma CSV of the Crude Index feeds.
///
/// <para>
/// One parser with a delimiter parameter, and it honours quoting for <b>both</b>.
/// The <c>.dat</c> feeds show no quoting today and no value contains a <c>|</c>,
/// but a TVP binds BY POSITION: a future embedded delimiter handled naively would
/// shift every column one place left and corrupt rows silently rather than fail
/// them. The Crude Index CSV genuinely mixes quoted strings with bare numbers
/// (<c>"08/26/2026-09/25/2026",273,"BGS",1416,...</c>), which this handles by
/// treating quotes as optional per field.
/// </para>
/// <para>
/// Values are returned unquoted and trimmed.
/// </para>
/// </summary>
internal static class IceDelimited
{
    /// <summary>
    /// Split a whole document into records, honouring RFC 4180 quoting — including
    /// a newline INSIDE a quoted field, which a line-by-line split would break.
    ///
    /// <para>
    /// Blank lines are skipped, including a trailing one (every ICE file ends with
    /// a newline). A blank line cannot be a valid record for any feed here — all of
    /// them have at least 11 columns.
    /// </para>
    /// </summary>
    public static List<string[]> ParseRecords(string text, char delimiter)
    {
        var records = new List<string[]>();
        var fields = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var sawAnyChar = false;

        void EndField()
        {
            fields.Add(current.ToString().Trim());
            current.Clear();
        }

        void EndRecord()
        {
            EndField();
            // A "record" of one empty field is a blank line — not data.
            if (!(fields.Count == 1 && fields[0].Length == 0))
                records.Add(fields.ToArray());
            fields.Clear();
            sawAnyChar = false;
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    // "" inside a quoted field is one literal quote.
                    if (i + 1 < text.Length && text[i + 1] == '"') { current.Append('"'); i++; }
                    else inQuotes = false;
                }
                else current.Append(c);

                sawAnyChar = true;
                continue;
            }

            if (c == '"')
            {
                inQuotes = true;
                sawAnyChar = true;
            }
            else if (c == delimiter)
            {
                EndField();
                sawAnyChar = true;
            }
            else if (c == '\r')
            {
                // CRLF or a lone CR both terminate the record; swallow the LF.
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                EndRecord();
            }
            else if (c == '\n')
            {
                EndRecord();
            }
            else
            {
                current.Append(c);
                sawAnyChar = true;
            }
        }

        // Final record when the file does not end with a newline.
        if (sawAnyChar || current.Length > 0 || fields.Count > 0)
            EndRecord();

        return records;
    }

    /// <summary>
    /// Build a case-insensitive header-name to column-index map. A duplicated header
    /// keeps the FIRST occurrence — none of the live files has one, and silently
    /// preferring the last would be the more surprising choice.
    /// </summary>
    public static Dictionary<string, int> BuildHeaderMap(string[] header)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < header.Length; i++)
        {
            var name = header[i].Trim();
            if (name.Length > 0 && !map.ContainsKey(name))
                map[name] = i;
        }
        return map;
    }
}
