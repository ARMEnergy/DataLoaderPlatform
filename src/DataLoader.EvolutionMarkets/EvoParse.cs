using System.Globalization;
using System.Text.Json;

namespace DataLoader.EvolutionMarkets;

/// <summary>
/// Tolerant, invariant-culture readers for Evolution Markets JSON values (design §5.1).
///
/// <para>Per the platform tolerant-parse contract: an unrecognised or unparseable value degrades to
/// <c>null</c> (never a row or run failure), and only a record whose <b>KEY</b>
/// (<c>marketDataId</c>) is unusable is dropped-and-counted.</para>
///
/// <para><b>Why every field goes through here rather than POCO binding.</b> Two reasons specific to
/// this API:</para>
/// <list type="number">
///   <item><b>Rows are not the same shape as each other.</b> The server OMITS a key entirely rather
///     than sending <c>null</c> — <c>tenor</c> is present on ~47% of rows and simply absent on the
///     rest, and nine other fields are absent on every row. A shape-strict binder is not wrong here,
///     but navigating explicitly makes "absent" and "null" one code path instead of two.</item>
///   <item><b>The response spelling is not the request spelling.</b> The projection is asked for as
///     <c>marketDataId</c>/<c>businessDate</c>/<c>pct_ret_daily</c> and comes back as
///     <c>priceId</c>/<c>date</c>/<c>pctRetDaily</c> (see <see cref="EvoFields"/>). Candidate-name
///     lookup absorbs the mismatch, and absorbs the vendor later settling on either spelling.</item>
/// </list>
///
/// <para><b>There is no string null-sentinel to defend against.</b> Unlike NGI (the literal
/// <c>"None"</c>) and OPIS, this API expresses absence structurally — by omitting the key — and every
/// numeric value observed across 8,118 rows was a real JSON number. The sentinel list below is
/// therefore short and defensive only; it exists so a vendor-side switch to <c>"N/A"</c> text
/// degrades to NULL instead of failing a parse.</para>
/// </summary>
internal static class EvoParse
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>
    /// Defensive-only sentinel spellings that map to SQL <c>NULL</c>. <b>None of these has ever been
    /// observed</b> — this API omits absent keys rather than stringifying them. Compared trimmed and
    /// case-insensitively.
    /// </summary>
    private static readonly string[] NullSentinels = { "null", "N/A", "NA", "None", "-", "" };

    /// <summary>
    /// Case-insensitive property lookup accepting a list of candidate names; returns the first
    /// present, non-JSON-null value (candidate order wins, so the live spelling is the cheap path).
    /// Returns <c>null</c> when <paramref name="elem"/> is not an object or no candidate is present.
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
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) &&
                    p.Value.ValueKind != JsonValueKind.Null)
                    return p.Value;
            }
        }
        return null;
    }

    /// <summary><c>true</c> for <c>null</c>, blank, or (trimmed, case-insensitive) any defensive sentinel.</summary>
    public static bool IsNullSentinel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        var t = value.Trim();
        foreach (var s in NullSentinels)
            if (string.Equals(t, s, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Raw scalar text of an element (strings and, defensively, numbers/bools), else <c>null</c>.</summary>
    private static string? Raw(JsonElement? v)
    {
        if (v is null) return null;
        var e = v.Value;
        return e.ValueKind switch
        {
            JsonValueKind.String => e.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.Object or JsonValueKind.Array => null,   // unexpected shape → NULL, never a throw
            _ => e.ToString()
        };
    }

    /// <summary>Trimmed string; sentinel → <c>null</c>.</summary>
    public static string? Str(JsonElement? v)
    {
        var raw = Raw(v);
        return IsNullSentinel(raw) ? null : raw!.Trim();
    }

    /// <summary>
    /// A trimmed string truncated to <paramref name="maxLength"/>, so an over-long value can never
    /// abort the whole batch at TVP bind time. <paramref name="truncated"/> is incremented (not
    /// reset) when truncation happens, so the caller can report it once per page.
    ///
    /// <para>The widths here mirror <c>sql/EvolutionMarkets/002</c>. Observed maxima are far below
    /// them (longest <c>instrument</c> = 63 of 250), so this should never fire — which is exactly why
    /// it must be counted and logged rather than silently swallowed.</para>
    /// </summary>
    public static string? StrCapped(JsonElement? v, int maxLength, ref int truncated)
    {
        var s = Str(v);
        if (s is null || s.Length <= maxLength) return s;
        truncated++;
        return s[..maxLength];
    }

    /// <summary>
    /// A <see cref="Guid"/>. Accepts any format <see cref="Guid.TryParse(string?, out Guid)"/> does
    /// (the live feed always sends canonical hyphenated 8-4-4-4-12). Unparseable → <c>null</c>.
    /// </summary>
    public static Guid? Guid(JsonElement? v)
    {
        var raw = Raw(v);
        if (IsNullSentinel(raw)) return null;
        return System.Guid.TryParse(raw!.Trim(), out var g) ? g : null;
    }

    /// <summary>
    /// A bare <c>yyyy-MM-dd</c> calendar date (the <c>date</c> field). Parsed with an EXACT format
    /// list under the invariant culture — never <c>DateOnly.Parse</c>, which would accept ambiguous
    /// forms and interpret them by ambient culture.
    /// </summary>
    public static DateOnly? Date(JsonElement? v)
    {
        var raw = Raw(v);
        if (IsNullSentinel(raw)) return null;

        var t = raw!.Trim();
        if (DateOnly.TryParseExact(t, "yyyy-MM-dd", Inv, DateTimeStyles.None, out var d))
            return d;

        // Defensive: a full ISO instant where a bare date was expected (e.g. the vendor switching
        // `date` to the `priceTs` shape). Take its DATE part rather than dropping the field.
        if (DateTimeOffset.TryParse(t, Inv, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto))
            return DateOnly.FromDateTime(dto.UtcDateTime);

        return null;
    }

    /// <summary>
    /// An ISO-8601 instant (the <c>priceTs</c> field, e.g. <c>2026-08-24T00:00:00.000Z</c>) returned
    /// as a <b>UTC</b> <see cref="DateTime"/> with <see cref="DateTimeKind.Utc"/>.
    ///
    /// <para><c>AssumeUniversal | AdjustToUniversal</c> is deliberate on both counts: every observed
    /// value carries an explicit <c>Z</c>, and if the vendor ever drops the offset the value is
    /// treated as UTC rather than silently reinterpreted in the host's local zone — which on a
    /// US-Central host would shift every timestamp by 5–6 hours and, at <c>DATETIME2(0)</c>, land
    /// <c>priceTs</c> on the PREVIOUS DAY.</para>
    /// </summary>
    public static DateTime? Instant(JsonElement? v)
    {
        var raw = Raw(v);
        if (IsNullSentinel(raw)) return null;

        return DateTimeOffset.TryParse(
            raw!.Trim(), Inv,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var dto)
            ? dto.UtcDateTime
            : null;
    }

    /// <summary>
    /// A signed decimal measure. <b>NEVER defaults to 0</b> — an unparseable price must become NULL,
    /// because a fabricated zero is a real, tradeable price and would be indistinguishable from a
    /// genuine flat print.
    ///
    /// <para>Reads a JSON number natively where possible (exact, no round trip through text) and
    /// falls back to <c>decimal.TryParse</c> with <c>Float | AllowLeadingSign</c> for a stringified
    /// number. <b>This is where the <c>change</c> vendor bug lands harmlessly:</b> when a short
    /// <c>field</c> projection makes the server send the <c>term</c> string
    /// (<c>"Sep'26-Oct'26"</c>) in a decimal slot, the parse fails and the column becomes NULL
    /// rather than nonsense.</para>
    ///
    /// <para><b>Out-of-range values degrade to <c>null</c> too</b> — see
    /// <see cref="InDecimal18x8Range"/>. A <see cref="decimal"/> holds values up to ~7.9e28, but the
    /// target column is <c>DECIMAL(18,8)</c> whose integral part maxes out at 10 digits. Passing a
    /// larger value would throw <i>at TVP bind time</i>, which fails the ENTIRE page — one absurd
    /// number would cost a whole business date. Degrading it to NULL keeps the tolerant-parse contract
    /// intact: no observed value is anywhere near this (every price seen is under 1 in magnitude), so
    /// this is a guard against vendor drift, not an expected path.</para>
    /// </summary>
    public static decimal? Dec(JsonElement? v)
    {
        if (v is null) return null;
        var e = v.Value;

        if (e.ValueKind == JsonValueKind.Number)
            return e.TryGetDecimal(out var native) && InDecimal18x8Range(native) ? native : null;

        var raw = Raw(v);
        if (IsNullSentinel(raw)) return null;

        return decimal.TryParse(
                   raw!.Trim(), NumberStyles.Float | NumberStyles.AllowLeadingSign, Inv, out var parsed)
               && InDecimal18x8Range(parsed)
            ? parsed
            : null;
    }

    /// <summary>
    /// The largest magnitude <c>DECIMAL(18,8)</c> can hold, exclusive: 18 total digits minus 8
    /// decimal places leaves 10 integral digits, so the column tops out just below 1e10.
    ///
    /// <para>Only the INTEGRAL part is guarded. Extra decimal places beyond 8 are fine — SQL Server
    /// rounds them to the column's scale on assignment, which is exactly what storing the value means
    /// and is not data loss worth rejecting a row over.</para>
    /// </summary>
    private const decimal Decimal18x8Limit = 10_000_000_000m;   // 1e10

    private static bool InDecimal18x8Range(decimal value) =>
        value > -Decimal18x8Limit && value < Decimal18x8Limit;

    /// <summary>
    /// A 32-bit integer count/size. Same no-default-zero rule as <see cref="Dec"/>.
    ///
    /// <para>Tolerates a decimal-looking integral value (e.g. <c>5.0</c>): the vendor types these as
    /// <c>number</c> in its OpenAPI document even though the target columns are <c>INT</c>, so a
    /// <c>5.0</c> must become <c>5</c>, not NULL. A genuinely fractional value degrades to
    /// <c>null</c> — silently truncating it would invent a count.</para>
    /// </summary>
    public static int? Int(JsonElement? v)
    {
        if (v is null) return null;
        var e = v.Value;

        if (e.ValueKind == JsonValueKind.Number)
        {
            if (e.TryGetInt32(out var native)) return native;
            if (e.TryGetDecimal(out var dec) && dec == decimal.Truncate(dec) &&
                dec >= int.MinValue && dec <= int.MaxValue)
                return (int)dec;
            return null;
        }

        var raw = Raw(v);
        if (IsNullSentinel(raw)) return null;

        var t = raw!.Trim();
        if (int.TryParse(t, NumberStyles.Integer | NumberStyles.AllowLeadingSign, Inv, out var parsed))
            return parsed;

        if (decimal.TryParse(t, NumberStyles.Float | NumberStyles.AllowLeadingSign, Inv, out var asDec) &&
            asDec == decimal.Truncate(asDec) && asDec >= int.MinValue && asDec <= int.MaxValue)
            return (int)asDec;

        return null;
    }
}

/// <summary>
/// Per-page tolerance counters (design §5.3). Reported once per page as a single warning line when
/// anything is non-zero, so a degraded parse is visible without one log line per row.
/// </summary>
internal sealed class EvoParseCounters
{
    /// <summary>Records dropped because <c>marketDataId</c> was missing or unparseable — an unkeyable row.</summary>
    public int DroppedNoKey { get; set; }

    /// <summary>Records dropped as a duplicate <c>marketDataId</c> within the same logical read (across pages).</summary>
    public int DroppedDuplicateKey { get; set; }

    /// <summary>Array elements that were not JSON objects at all.</summary>
    public int DroppedNotAnObject { get; set; }

    /// <summary>String values truncated to their column width.</summary>
    public int Truncated { get; set; }

    public bool IsClean => DroppedNoKey == 0 && DroppedDuplicateKey == 0 && DroppedNotAnObject == 0 && Truncated == 0;

    public override string ToString() =>
        $"droppedNoKey={DroppedNoKey} droppedDuplicateKey={DroppedDuplicateKey} " +
        $"droppedNotAnObject={DroppedNotAnObject} truncated={Truncated}";
}
