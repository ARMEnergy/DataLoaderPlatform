using System.Globalization;
using System.Text.Json;

namespace DataLoader.IIR;

/// <summary>
/// Tolerant, invariant-culture parsers for IIR JSON values (design §6.1). Because the three field
/// schemas are RECONSTRUCTED / <c>⚠</c> (docs/apis/IIR.md), the row factories must degrade a
/// wrong-cased key, a missing field, or an unexpected flag encoding to <c>NULL</c> rather than
/// failing the row/run. Every accessor:
/// <list type="bullet">
///   <item>looks properties up <b>case-insensitively</b> and accepts a list of candidate names
///     (returns the first present, non-JSON-null value);</item>
///   <item>descends into a nested object case-insensitively (<see cref="Nested"/>);</item>
///   <item>tolerates a measure arriving as a JSON string;</item>
///   <item>strips a trailing <c>[UTC]</c>/<c>Z[UTC]</c> suffix before parsing dates
///     (<see cref="DateStripZ(System.Text.Json.JsonElement?)"/>);</item>
///   <item>normalizes the many boolean-flag encodings (0/1, true/false, "Y"/"N", "1"/"0",
///     "true"/"false") to the DDL <c>INT</c> 0/1 (<see cref="Flag(System.Text.Json.JsonElement?)"/>).</item>
/// </list>
/// A blank / missing / unparseable value yields <c>null</c> for that column, never a row failure.
/// </summary>
internal static class IirParse
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>
    /// Case-insensitive property lookup accepting a list of candidate names; returns the first present,
    /// non-JSON-null value (candidate order wins). Null when <paramref name="elem"/> is not an object or
    /// no candidate is present.
    /// </summary>
    public static JsonElement? Prop(JsonElement elem, params string[] names)
    {
        if (elem.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names)
        {
            if (elem.TryGetProperty(name, out var exact) && exact.ValueKind != JsonValueKind.Null)
                return exact;
            foreach (var p in elem.EnumerateObject())
            {
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind != JsonValueKind.Null)
                    return p.Value;
            }
        }
        return null;
    }

    /// <summary>
    /// Walks a nested object case-insensitively (e.g. <c>mailingAddress.city</c>, <c>parent.companyName</c>);
    /// a missing object or field yields <c>null</c>. Candidate lists tolerate the <c>⚠</c> object-name
    /// variants (e.g. <c>parent</c>/<c>plantParent</c>, <c>plantPhysicalAddress</c>/<c>plantAddress</c>).
    /// </summary>
    public static JsonElement? Nested(JsonElement elem, string[] objectNames, params string[] fieldNames)
    {
        var obj = Prop(elem, objectNames);
        if (obj is null || obj.Value.ValueKind != JsonValueKind.Object) return null;
        return Prop(obj.Value, fieldNames);
    }

    // ---- typed accessors over a resolved element (Nested/Prop result) --------------------------

    public static string? Str(JsonElement? v)
    {
        if (v is null) return null;
        var e = v.Value;
        var s = e.ValueKind == JsonValueKind.String ? e.GetString() : e.ToString();
        return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    public static int? Int(JsonElement? v)
    {
        if (v is null) return null;
        var e = v.Value;
        if (e.ValueKind == JsonValueKind.Number)
        {
            if (e.TryGetInt32(out var n)) return n;
            // Tolerate a float-formatted integer (e.g. 3207542.0) so a PK is never silently dropped.
            if (e.TryGetInt64(out var l) && l is >= int.MinValue and <= int.MaxValue) return (int)l;
            if (e.TryGetDouble(out var d) && d is >= int.MinValue and <= int.MaxValue) return (int)d;
            return null;
        }
        if (e.ValueKind == JsonValueKind.String)
        {
            var s = e.GetString();
            if (string.IsNullOrWhiteSpace(s)) return null;
            var t = s.Trim();
            if (int.TryParse(t, NumberStyles.Integer, Inv, out var n2)) return n2;
            // Tolerate a float-formatted integer string (e.g. "3207542.0").
            if (double.TryParse(t, NumberStyles.Float, Inv, out var d2) && d2 is >= int.MinValue and <= int.MaxValue) return (int)d2;
        }
        return null;
    }

    public static double? Float(JsonElement? v)
    {
        if (v is null) return null;
        var e = v.Value;
        if (e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out var d)) return d;
        if (e.ValueKind == JsonValueKind.String)
        {
            var s = e.GetString();
            if (!string.IsNullOrWhiteSpace(s) && double.TryParse(s.Trim(), NumberStyles.Float, Inv, out var d2)) return d2;
        }
        return null;
    }

    /// <summary>Normalizes any of the documented boolean-flag encodings to the DDL INT 0/1; anything else → NULL.</summary>
    public static int? Flag(JsonElement? v)
    {
        if (v is null) return null;
        var e = v.Value;
        switch (e.ValueKind)
        {
            case JsonValueKind.True: return 1;
            case JsonValueKind.False: return 0;
            case JsonValueKind.Number:
                if (e.TryGetInt32(out var n)) return n == 0 ? 0 : (n == 1 ? 1 : (int?)null);
                return null;
            case JsonValueKind.String:
                var s = e.GetString()?.Trim();
                if (string.IsNullOrEmpty(s)) return null;
                switch (s.ToLowerInvariant())
                {
                    case "1": case "true": case "y": case "yes": return 1;
                    case "0": case "false": case "n": case "no": return 0;
                    default: return null;
                }
            default: return null;
        }
    }

    /// <summary>
    /// <c>DATETIME2(0)</c> — strips a trailing <c>[UTC]</c> (e.g. <c>2019-01-29T22:39:21Z[UTC]</c>),
    /// then parses ISO-8601 as UTC. A plain date (<c>2019-01-29</c>) is also accepted.
    /// </summary>
    public static DateTime? DateStripZ(JsonElement? v)
    {
        if (v is null) return null;
        var e = v.Value;
        var s = (e.ValueKind == JsonValueKind.String ? e.GetString() : e.ToString())?.Trim();
        if (string.IsNullOrWhiteSpace(s)) return null;

        // Strip a trailing "[…]" IANA-zone suffix (e.g. "Z[UTC]" → "Z", "-05:00[America/Chicago]" → "-05:00").
        var bracket = s.IndexOf('[');
        if (bracket >= 0) s = s.Substring(0, bracket);
        s = s.Trim();
        if (s.Length == 0) return null;

        return DateTime.TryParse(s, Inv,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt)
            ? dt
            : (DateTime?)null;
    }

    // ---- convenience overloads reading straight from an element + candidate names ---------------

    public static string? Str(JsonElement elem, params string[] names) => Str(Prop(elem, names));
    public static int? Int(JsonElement elem, params string[] names) => Int(Prop(elem, names));
    public static double? Float(JsonElement elem, params string[] names) => Float(Prop(elem, names));
    public static int? Flag(JsonElement elem, params string[] names) => Flag(Prop(elem, names));
    public static DateTime? DateStripZ(JsonElement elem, params string[] names) => DateStripZ(Prop(elem, names));
}
