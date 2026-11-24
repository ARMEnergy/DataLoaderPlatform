using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DataLoader.PJM;

/// <summary>
/// One source item: its fields keyed case-insensitively (design §4.3). Built by the day reader
/// after the required/extra-field checks.
/// </summary>
public sealed class PjmItem
{
    private readonly IReadOnlyDictionary<string, JsonElement> _fields;

    public PjmItem(string feed, int index, IReadOnlyDictionary<string, JsonElement> fields)
    {
        Feed = feed;
        Index = index;
        _fields = fields;
    }

    public string Feed { get; }

    /// <summary>Zero-based position within the unit (for messages; never a value).</summary>
    public int Index { get; }

    /// <summary>The field, or <c>null</c> when absent.</summary>
    public JsonElement? Get(string field) => _fields.TryGetValue(field, out var v) ? v : null;
}

/// <summary>
/// Strict value parsing (design §4.3). Every failure throws <see cref="PjmContractException"/>
/// naming the feed and the field — 🔒 NEVER the value.
/// </summary>
public static class PjmValueParser
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // yyyy-MM-ddTHH:mm:ss[.fff…][Z]; nothing else (no offsets, no space separator).
    private static readonly Regex IsoNoOffset = new(
        @"^(?<core>\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})(?:\.(?<frac>\d+))?(?<z>Z)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public const int MemberMaxLength = 50;

    private static PjmContractException Fail(PjmItem item, string field, string what) =>
        new($"PJM {item.Feed}: item {item.Index} field '{field}' {what}");

    /// <summary>
    /// ISO-8601 with no offset → whole seconds (Kind Unspecified). A trailing <c>Z</c> is accepted
    /// only when <paramref name="allowZ"/> (the UTC field). A NON-ZERO fractional part fails:
    /// <c>DATETIME2(0)</c> would otherwise round two distinct instants onto one key.
    /// </summary>
    public static DateTime Timestamp(PjmItem item, string field, bool allowZ)
    {
        var e = item.Get(field);
        if (e is not { ValueKind: JsonValueKind.String } s)
            throw Fail(item, field, "is not a timestamp string");

        var m = IsoNoOffset.Match(s.GetString() ?? string.Empty);
        if (!m.Success)
            throw Fail(item, field, "is not an offset-free ISO-8601 timestamp (yyyy-MM-ddTHH:mm:ss)");
        if (m.Groups["z"].Success && !allowZ)
            throw Fail(item, field, "carries a 'Z' offset, which is accepted on the UTC field only");
        if (m.Groups["frac"].Success && m.Groups["frac"].Value.Any(c => c != '0'))
            throw Fail(item, field, "has fractional seconds; DATETIME2(0) would round distinct instants onto one key");

        if (!DateTime.TryParseExact(m.Groups["core"].Value, "yyyy-MM-ddTHH:mm:ss", Inv, DateTimeStyles.None, out var dt))
            throw Fail(item, field, "is not a valid calendar timestamp");

        return DateTime.SpecifyKind(dt, DateTimeKind.Unspecified);
    }

    /// <summary>Non-null, non-blank string, stored AS RECEIVED (not trimmed, not case-folded), ≤ 50 chars.</summary>
    public static string Member(PjmItem item, string field)
    {
        var e = item.Get(field);
        if (e is not { ValueKind: JsonValueKind.String } s)
            throw Fail(item, field, "is not a non-null string");

        var value = s.GetString();
        if (string.IsNullOrWhiteSpace(value))
            throw Fail(item, field, "is empty");
        if (value.Length > MemberMaxLength)
            throw Fail(item, field, $"is longer than the {MemberMaxLength}-character column");

        return value;
    }

    /// <summary>
    /// Non-key string (<c>nerc_region</c>, <c>mkt_region</c>, <c>zone</c>; design §14.2): JSON
    /// <c>null</c>/absent → <c>null</c>; a non-string → fail; more than 50 characters → fail naming
    /// the field. Stored AS RECEIVED — never trimmed.
    /// </summary>
    public static string? NullableString(PjmItem item, string field)
    {
        var e = item.Get(field);
        if (e is null || e.Value.ValueKind == JsonValueKind.Null) return null;
        if (e.Value.ValueKind != JsonValueKind.String)
            throw Fail(item, field, "is not a string or null");

        var value = e.Value.GetString() ?? string.Empty;
        if (value.Length > MemberMaxLength)
            throw Fail(item, field, $"is longer than the {MemberMaxLength}-character column");

        return value;
    }

    /// <summary>
    /// JSON number or numeric string (invariant culture) → decimal rounded to <paramref name="scale"/>;
    /// <c>null</c>/absent → <c>null</c>; anything else, or more than <c>precision − scale</c> integer digits → fail.
    /// </summary>
    public static decimal? Decimal(PjmItem item, string field, int precision, int scale)
    {
        var e = item.Get(field);
        if (e is null || e.Value.ValueKind == JsonValueKind.Null) return null;

        decimal value;
        switch (e.Value.ValueKind)
        {
            case JsonValueKind.Number:
                if (!e.Value.TryGetDecimal(out value))
                    throw Fail(item, field, $"is out of range for DECIMAL({precision},{scale})");
                break;
            case JsonValueKind.String:
                if (!decimal.TryParse(e.Value.GetString(), NumberStyles.Float, Inv, out value))
                    throw Fail(item, field, "is not a number or numeric string");
                break;
            default:
                throw Fail(item, field, "is not a number or numeric string");
        }

        var rounded = Math.Round(value, scale, MidpointRounding.AwayFromZero);
        var limit = Pow10(precision - scale);
        if (Math.Abs(rounded) >= limit)
            throw Fail(item, field, $"is out of range for DECIMAL({precision},{scale})");

        return rounded;
    }

    /// <summary>JSON true/false, "true"/"false" (case-insensitive), or 1/0 → bool; <c>null</c>/absent → <c>null</c>; anything else → fail.</summary>
    public static bool? Boolean(PjmItem item, string field)
    {
        var e = item.Get(field);
        if (e is null || e.Value.ValueKind == JsonValueKind.Null) return null;

        switch (e.Value.ValueKind)
        {
            case JsonValueKind.True: return true;
            case JsonValueKind.False: return false;
            case JsonValueKind.String:
                var s = e.Value.GetString();
                if (string.Equals(s, "true", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(s, "false", StringComparison.OrdinalIgnoreCase)) return false;
                break;
            case JsonValueKind.Number:
                if (e.Value.TryGetInt32(out var n) && n is 0 or 1) return n == 1;
                break;
        }

        throw Fail(item, field, "is not a boolean (true/false, \"true\"/\"false\", 1/0)");
    }

    private static decimal Pow10(int exponent)
    {
        var r = 1m;
        for (var i = 0; i < exponent; i++) r *= 10m;
        return r;
    }
}
