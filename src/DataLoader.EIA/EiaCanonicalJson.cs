using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DataLoader.EIA;

/// <summary>
/// One field of an API data row, captured with enough fidelity to re-serialise it
/// exactly: the JSON token kind plus its text.
/// </summary>
/// <param name="Name">The property name as the API spelled it.</param>
/// <param name="Kind">The JSON token kind the API used.</param>
/// <param name="Text">
/// For a string, the DECODED value (<c>BTU\/CF</c> on the wire is <c>BTU/CF</c> here).
/// For a number, the RAW token text, so no precision is lost to a parse/reformat round
/// trip. Null for <c>null</c>.
/// </param>
internal readonly record struct EiaField(string Name, JsonValueKind Kind, string? Text);

/// <summary>
/// Renders the canonical JSON text stored in <c>arm.SeriesData.FacetJSON</c> and
/// <c>arm.SeriesData.DataJSON</c>, and hashed into <c>FacetKey</c> / <c>DataHash</c>.
///
/// <para>
/// Every rule below was derived from the incumbent <c>dbo.SeriesData</c>, not invented.
/// The text this produces IS the hash input, so a change to any of them re-keys the
/// table: every row would miss its merge target and the table would fork into
/// old-rendering and new-rendering copies. Treat this file as frozen, and change it
/// only with a planned re-key.
/// </para>
///
/// <list type="number">
///   <item><b>No whitespace.</b> <c>{"a":1,"b":2}</c>.</item>
///   <item><b>Keys sorted ORDINAL ascending.</b> Verified against 839 real objects
///         covering 175 distinct key names: all were already in ordinal order, and
///         ordinal / invariant / ordinal-ignore-case agreed on every one. Ordinal is
///         chosen because it is the only culture-independent option — a hash input
///         must not depend on the server's locale.</item>
///   <item><b>Strings escaped minimally.</b> <c>"</c> and <c>\</c> are escaped; the
///         five short control escapes are used; any other character below U+0020
///         becomes <c>\u00xx</c>. <c>/</c> is NOT escaped (the API sends
///         <c>BTU\/CF</c>, the incumbent stores <c>BTU/CF</c>) and non-ASCII is left
///         literal. The short-escape rule is evidenced: a live row stores
///         <c>{"average-price":"148.49\r", ...}</c>.</item>
///   <item><b>Numeric normalisation</b> — see <see cref="LooksLikeJsonNumber"/> and the
///         remarks below.</item>
/// </list>
///
/// <para>
/// <b>⚠ THE ONE DELIBERATE DIVERGENCE FROM THE INCUMBENT: numeric normalisation.</b>
/// Conduit preserved whatever JSON token type the API happened to send. EIA changed
/// those types between 2022 and 2024 — <c>"activityId":8</c> became
/// <c>"activityId":"8"</c>, <c>{"value":1117.538985}</c> became
/// <c>{"value":"1117.538985"}</c> — and because the key is a hash of the TEXT, the same
/// business row was stored TWICE under two different FacetKeys. That fork is visible in
/// the live table today (<c>docs/design/EIA.md</c> §6.3).
/// </para>
/// <para>
/// With <see cref="EiaSettings.CoerceNumericStrings"/> on (the default), a string whose
/// content is a valid JSON number is written UNQUOTED, using its own text verbatim. So
/// <c>"44163"</c> and <c>44163</c> both render <c>44163</c> and hash alike, and the
/// vendor can flip types without re-keying anything. This is also exactly what the
/// loader request specifies: its worked example takes <c>"sales":"1023740"</c> off the
/// wire and stores <c>"sales":44163</c>. Non-numeric strings are untouched — <c>"w"</c>
/// stays <c>"w"</c>, and <c>"148.49\r"</c> stays a string because the trailing carriage
/// return is not part of the JSON number grammar.
/// </para>
/// <para>
/// Setting it to <c>false</c> restores byte-for-byte Conduit behaviour. <b>Flipping it
/// on a populated table re-keys every row whose values are numeric strings</b> — it is
/// a migration, not a toggle.
/// </para>
/// </summary>
internal static class EiaCanonicalJson
{
    /// <summary>The canonical form of an object with no fields. Hashes to 1947253782.</summary>
    public const string EmptyObject = "{}";

    /// <summary>
    /// Render <paramref name="fields"/> as canonical JSON. The caller may pass them in
    /// any order; this sorts.
    /// </summary>
    public static string Render(IReadOnlyList<EiaField> fields, bool coerceNumericStrings)
    {
        if (fields.Count == 0) return EmptyObject;

        // Sort a copy -- callers reuse their buffers across rows.
        var ordered = fields.ToArray();
        Array.Sort(ordered, static (a, b) => string.CompareOrdinal(a.Name, b.Name));

        var sb = new StringBuilder(64 + (ordered.Length * 24));
        sb.Append('{');

        for (var i = 0; i < ordered.Length; i++)
        {
            if (i > 0) sb.Append(',');
            AppendString(sb, ordered[i].Name);
            sb.Append(':');
            AppendValue(sb, ordered[i], coerceNumericStrings);
        }

        sb.Append('}');
        return sb.ToString();
    }

    private static void AppendValue(StringBuilder sb, in EiaField field, bool coerceNumericStrings)
    {
        switch (field.Kind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                sb.Append("null");
                break;

            case JsonValueKind.True:
                sb.Append("true");
                break;

            case JsonValueKind.False:
                sb.Append("false");
                break;

            // Raw token text, never a parse-and-reformat. A double round trip would
            // turn 6.0754044100385393E-05 into something else and re-key the row.
            case JsonValueKind.Number:
                sb.Append(field.Text ?? "null");
                break;

            case JsonValueKind.String:
                var text = field.Text;
                if (text is null) { sb.Append("null"); break; }
                if (coerceNumericStrings && TryNormaliseNumber(text, out var number)) sb.Append(number);
                else AppendString(sb, text);
                break;

            // An object or array inside a data row has never been observed. Rendering
            // it would require its own canonical ordering rules, which would be
            // invented rather than evidenced -- so refuse loudly.
            default:
                throw new NotSupportedException(
                    $"EIA field '{field.Name}' is a JSON {field.Kind}. Only string, number, " +
                    "boolean and null have ever been observed in a data row; a nested object " +
                    "or array would need canonical ordering rules this loader does not have.");
        }
    }

    /// <summary>
    /// Minimal RFC 8259 string escaping. Deliberately does NOT escape <c>/</c> or
    /// non-ASCII — see the class remarks for the evidence.
    /// </summary>
    internal static void AppendString(StringBuilder sb, string value)
    {
        sb.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"':  sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b");  break;
                case '\f': sb.Append("\\f");  break;
                case '\n': sb.Append("\\n");  break;
                case '\r': sb.Append("\\r");  break;
                case '\t': sb.Append("\\t");  break;
                default:
                    if (c < ' ')
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }

    /// <summary>
    /// Decide whether a STRING value should be written as a number, and if so with what
    /// text.
    ///
    /// <para>
    /// Most values that pass are emitted <b>verbatim</b> — no parse, no reformat, so no
    /// precision can be lost. Two shapes are repaired first, because EIA sends decimals
    /// that are legal almost everywhere except in JSON itself:
    /// </para>
    /// <list type="bullet">
    ///   <item><b>Leading dot.</b> <c>".43"</c> has no integer part, which JSON requires.
    ///         Live-verified 2026-10-16: 5 of the 400 rows in
    ///         <c>coal/aggregate-production</c> for 2001 carry <c>productivity</c> in
    ///         this form, and the incumbent stores them as <c>0.43</c>. Emitting
    ///         <c>.43</c> unquoted would put INVALID JSON in the column and break
    ///         <c>ISJSON</c>/<c>OPENJSON</c> for every consumer; leaving it a string
    ///         would make one row in eighty non-numeric for no reason. So a <c>0</c> is
    ///         inserted — pure text surgery, not arithmetic.</item>
    ///   <item><b>Trailing dot.</b> <c>"5."</c> likewise; the dot is dropped.</item>
    /// </list>
    /// <para>
    /// Everything else still has to satisfy <see cref="LooksLikeJsonNumber"/> exactly.
    /// In particular a leading <c>+</c> and leading zeros are NOT repaired: <c>"007"</c>
    /// is an identifier, and turning it into <c>7</c> would destroy information.
    /// </para>
    /// </summary>
    internal static bool TryNormaliseNumber(string s, out string normalised)
    {
        if (LooksLikeJsonNumber(s)) { normalised = s; return true; }

        // ".43" / "-.43"  ->  "0.43" / "-0.43"
        if (s.Length > 1)
        {
            if (s[0] == '.')
            {
                var candidate = "0" + s;
                if (LooksLikeJsonNumber(candidate)) { normalised = candidate; return true; }
            }
            else if (s[0] == '-' && s[1] == '.')
            {
                var candidate = "-0" + s[1..];
                if (LooksLikeJsonNumber(candidate)) { normalised = candidate; return true; }
            }

            // "5." -> "5"
            if (s[^1] == '.')
            {
                var candidate = s[..^1];
                if (LooksLikeJsonNumber(candidate)) { normalised = candidate; return true; }
            }
        }

        normalised = s;
        return false;
    }

    /// <summary>
    /// True when <paramref name="s"/> is a valid JSON number <b>in full</b>:
    /// <c>-? (0 | [1-9][0-9]*) ('.' [0-9]+)? ([eE] [+-]? [0-9]+)?</c>.
    ///
    /// <para>
    /// Strict on purpose. The grammar's own rules do the right thing for the cases that
    /// matter:
    /// </para>
    /// <list type="bullet">
    ///   <item><c>"007"</c> stays a STRING — leading zeros are not legal JSON, and an
    ///         identifier-like facet value must not lose them.</item>
    ///   <item><c>"+5"</c>, <c>".5"</c>, <c>"5."</c>, <c>"1,000"</c>, <c>"w"</c>,
    ///         <c>"NA"</c> all stay strings.</item>
    ///   <item><c>"148.49\r"</c> stays a string — trailing junk is rejected, which is
    ///         what the incumbent does with that exact value.</item>
    ///   <item>An empty string stays a string.</item>
    /// </list>
    /// <para>
    /// Hand-rolled rather than a regex: this runs once per field on the order of 90
    /// million rows.
    /// </para>
    /// </summary>
    internal static bool LooksLikeJsonNumber(string s)
    {
        if (s.Length == 0) return false;

        var i = 0;
        if (s[i] == '-') i++;

        // int part: 0 | [1-9][0-9]*
        if (i >= s.Length) return false;
        if (s[i] == '0')
        {
            i++;
        }
        else if (s[i] >= '1' && s[i] <= '9')
        {
            while (i < s.Length && s[i] >= '0' && s[i] <= '9') i++;
        }
        else
        {
            return false;
        }

        // frac: '.' [0-9]+
        if (i < s.Length && s[i] == '.')
        {
            i++;
            var digits = 0;
            while (i < s.Length && s[i] >= '0' && s[i] <= '9') { i++; digits++; }
            if (digits == 0) return false;
        }

        // exp: [eE] [+-]? [0-9]+
        if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))
        {
            i++;
            if (i < s.Length && (s[i] == '+' || s[i] == '-')) i++;
            var digits = 0;
            while (i < s.Length && s[i] >= '0' && s[i] <= '9') { i++; digits++; }
            if (digits == 0) return false;
        }

        return i == s.Length;
    }
}
