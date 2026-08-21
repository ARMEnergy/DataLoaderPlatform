using System.Globalization;
using System.Text.Json;

namespace DataLoader.NGI;

/// <summary>
/// Tolerant, invariant-culture readers for NGI JSON values (design §5.1). Everything
/// in both payloads goes through here, for two reasons that would each silently
/// corrupt a load if bypassed:
///
/// <list type="number">
///   <item><b>Five record field names contain SPACES</b> — <c>"Point Code"</c>,
///     <c>"Issue Date"</c>, <c>"Survey Start"</c>, <c>"Survey End"</c>,
///     <c>"Pricing Point"</c> (plus the locations envelope key
///     <c>"Bidweek Locations"</c>). <b>No <c>JsonNamingPolicy</c> will ever produce
///     them</b>: <c>CamelCase</c> gives <c>pointCode</c>, <c>SnakeCaseLower</c> gives
///     <c>point_code</c>, never <c>Point Code</c>. Default POCO binding therefore
///     matches nothing and yields a full 163-row batch of NULL point codes and NULL
///     dates — an unkeyable batch. So we navigate <see cref="JsonElement"/> via
///     <see cref="Prop"/> with candidate names, <b>by NAME and never by position</b>
///     (JSON object order is not a contract).</item>
///   <item><b>The null sentinel is the literal string <c>"None"</c></b> — not JSON
///     <c>null</c>, not empty, not a missing property. It appears on EVERY field type,
///     strings included, so a naive string passthrough would persist the literal text
///     <c>"None"</c> into <c>Region</c>/<c>PricingPoint</c>. Numerics are safe only by
///     accident (a <c>TryParse</c> of <c>"None"</c> fails), and an unguarded fallback
///     to <c>0</c> would invent a real price of zero. Every field is routed through
///     <see cref="IsNullSentinel"/> BEFORE any conversion.</item>
/// </list>
///
/// Per the platform tolerant-parse contract: an unrecognised or unparseable value
/// degrades to <c>null</c> (never a row/run failure) and only a record whose KEY is
/// unusable is dropped-and-counted.
/// </summary>
internal static class NgiParse
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>
    /// The sentinel spellings that map to SQL <c>NULL</c>. The <b>observed</b> value is the literal
    /// 4-char <c>None</c>; the rest are defensive (the vendor publishes no response contract, so any
    /// change on their side is silent). Compared trimmed and case-insensitively.
    /// </summary>
    private static readonly string[] NullSentinels = { "None", "null", "N/A", "NA", "-" };

    /// <summary>
    /// The ONLY vendor-observed sentinel — the literal 4-char <c>None</c>. It is the whole list for a
    /// KEY field (<see cref="IsKeyNullSentinel"/>), where a false positive drops a record instead of
    /// merely NULLing a measure.
    /// </summary>
    private const string KeyNullSentinel = "None";

    /// <summary>
    /// Case-insensitive property lookup accepting a list of candidate names; returns the first
    /// present, non-JSON-null value (candidate order wins). The exact-name probe runs first so the
    /// space-bearing live spelling (<c>"Point Code"</c>) is the cheap path. Returns <c>null</c> when
    /// <paramref name="elem"/> is not an object or no candidate is present.
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

    /// <summary>
    /// <c>true</c> for <c>null</c>, blank, or (trimmed, case-insensitive) any of
    /// <see cref="NullSentinels"/> — most importantly the literal <c>"None"</c> NGI actually
    /// publishes. Applied to EVERY field, strings included.
    /// </summary>
    public static bool IsNullSentinel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        var t = value.Trim();
        foreach (var s in NullSentinels)
            if (string.Equals(t, s, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// The narrow, <b>key-field</b> sentinel test: <c>true</c> only for <c>null</c>/blank or the trimmed,
    /// case-insensitive literal <see cref="KeyNullSentinel"/> (<c>"None"</c>) that NGI actually
    /// publishes. See <see cref="KeyStr(JsonElement?)"/> for why a key must NOT use the wide
    /// <see cref="NullSentinels"/> list.
    /// </summary>
    public static bool IsKeyNullSentinel(string? value) =>
        string.IsNullOrWhiteSpace(value) ||
        string.Equals(value.Trim(), KeyNullSentinel, StringComparison.OrdinalIgnoreCase);

    /// <summary>Raw scalar text of an element (strings and, defensively, numbers), else <c>null</c>.</summary>
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

    /// <summary>Trimmed string; sentinel (including the literal <c>"None"</c>) → <c>null</c>.</summary>
    public static string? Str(JsonElement? v)
    {
        var raw = Raw(v);
        return IsNullSentinel(raw) ? null : raw!.Trim();
    }

    /// <summary>
    /// <b>KEY-field</b> variant of <see cref="Str(JsonElement?)"/>: trims, and maps only a BLANK value
    /// or the vendor's literal <c>"None"</c> to <c>null</c> (<see cref="IsKeyNullSentinel"/> rather than
    /// the wide <see cref="NullSentinels"/> list).
    ///
    /// <para>Used for <c>PointCode</c> — the merge key of BOTH tables — and for the locations map's
    /// <c>LocationName</c>. The wide list's defensive extras (<c>null</c> / <c>N/A</c> / <c>NA</c> /
    /// <c>-</c>) are NOT vendor-observed, and on a key they cost more than they buy: a real point code
    /// spelled <c>NA</c> would be silently DROPPED and a location genuinely named <c>-</c> silently
    /// NULLed. The measure fields keep the wide list, where a false positive costs only one NULL.</para>
    /// </summary>
    public static string? KeyStr(JsonElement? v)
    {
        var raw = Raw(v);
        return IsKeyNullSentinel(raw) ? null : raw!.Trim();
    }

    /// <summary>
    /// Price/measure. Sentinel → <c>null</c>. Otherwise <c>decimal.TryParse</c> with
    /// <c>Float | AllowLeadingSign</c> under the invariant culture.
    /// <b>A leading minus MUST parse — negative gas prices are real</b> (Waha has printed negative
    /// cash prices), which is also why no price column carries a non-negative CHECK.
    /// Unparseable → <c>null</c> and <paramref name="unparseable"/> is set so the reader can count it.
    /// </summary>
    public static decimal? Dec(JsonElement? v, out bool unparseable)
    {
        unparseable = false;
        var raw = Raw(v);
        if (IsNullSentinel(raw)) return null;
        if (decimal.TryParse(raw!.Trim(), NumberStyles.Float | NumberStyles.AllowLeadingSign, Inv, out var d))
            return d;
        unparseable = true;
        return null;
    }

    /// <summary>
    /// Integer measure (<c>Volume</c>/<c>Deals</c>). Sentinel → <c>null</c>. Tries
    /// <c>int.TryParse</c> (<c>Integer | AllowThousands</c>) first; failing that, accepts a
    /// <c>decimal</c> only when it has no fractional part (so <c>"240.0"</c> lands while
    /// <c>"240.5"</c> is not silently truncated). Unparseable → <c>null</c> + counted.
    /// </summary>
    public static int? Int(JsonElement? v, out bool unparseable)
    {
        unparseable = false;
        var raw = Raw(v);
        if (IsNullSentinel(raw)) return null;

        var t = raw!.Trim();
        if (int.TryParse(t, NumberStyles.Integer | NumberStyles.AllowThousands, Inv, out var n))
            return n;

        if (decimal.TryParse(t, NumberStyles.Float | NumberStyles.AllowLeadingSign, Inv, out var d) &&
            decimal.Truncate(d) == d && d >= int.MinValue && d <= int.MaxValue)
            return (int)d;

        unparseable = true;
        return null;
    }

    /// <summary>
    /// <c>yyyy-MM-dd</c> → <see cref="DateOnly"/>. Sentinel → <c>null</c>; unparseable → <c>null</c>
    /// + counted. Exact-format parse under the invariant culture: this feed has no time component
    /// anywhere and no other date format was ever observed.
    /// </summary>
    public static DateOnly? Date(JsonElement? v, out bool unparseable)
    {
        unparseable = false;
        var raw = Raw(v);
        if (IsNullSentinel(raw)) return null;
        if (DateOnly.TryParseExact(raw!.Trim(), "yyyy-MM-dd", Inv, DateTimeStyles.None, out var d))
            return d;
        unparseable = true;
        return null;
    }

    // ---- convenience overloads reading straight from a parent element + candidate names ---------

    /// <summary>
    /// Reads a named property off a parent element.
    ///
    /// <para>⚠ <b><c>names</c> is deliberately NOT <c>params</c>, and that is load-bearing.</b> While it
    /// was, a one-argument call meant to read a BARE SCALAR element — <c>Str(entry.Value)</c>, where
    /// <c>entry.Value</c> is a non-nullable <see cref="JsonElement"/> — bound HERE (the identity
    /// conversion beats the <see cref="JsonElement"/>→<c>JsonElement?</c> lift) with an EMPTY name
    /// array: <see cref="Prop"/> then saw a non-object element and returned <c>null</c>, so the result
    /// was ALWAYS <c>null</c> with no exception and no warning. That silently dropped every entry of
    /// the locations crosswalk. Without <c>params</c> the arities no longer overlap, so a
    /// one-argument call can only bind to <see cref="Str(JsonElement?)"/> — the intended overload.</para>
    /// </summary>
    public static string? Str(JsonElement elem, string[] names) => Str(Prop(elem, names));

    /// <summary>
    /// KEY-field variant of <see cref="Str(JsonElement, string[])"/> — see
    /// <see cref="KeyStr(JsonElement?)"/> for why a key uses the narrow sentinel list.
    /// </summary>
    public static string? KeyStr(JsonElement elem, string[] names) => KeyStr(Prop(elem, names));

    public static decimal? Dec(JsonElement elem, string[] names, out bool unparseable) =>
        Dec(Prop(elem, names), out unparseable);

    public static int? Int(JsonElement elem, string[] names, out bool unparseable) =>
        Int(Prop(elem, names), out unparseable);

    public static DateOnly? Date(JsonElement elem, string[] names, out bool unparseable) =>
        Date(Prop(elem, names), out unparseable);
}

/// <summary>
/// Per-work-unit tolerance counters (design §5.3 step 7). The readers log these at the
/// end of a unit — the platform dropped-and-counted convention — so a silent NGI shape
/// change surfaces as a rising counter rather than as quietly missing data.
/// </summary>
internal sealed class NgiParseCounters
{
    /// <summary>Records dropped because no usable point code could be resolved (record field AND map key blank).</summary>
    public int Dropped;

    /// <summary>Records whose blank <c>"Point Code"</c> fell back to the <c>data</c> map key.</summary>
    public int MapKeyFallback;

    /// <summary>Records whose map key disagreed with their own <c>"Point Code"</c> (the record value is persisted).</summary>
    public int MapKeyMismatch;

    /// <summary>Records whose resolved issue date differed from the requested <c>issue_date</c>.</summary>
    public int IssueDateMismatch;

    /// <summary>Values that were present but unparseable as a number (→ NULL).</summary>
    public int UnparseableNumeric;

    /// <summary>Values that were present but unparseable as a date (→ NULL).</summary>
    public int UnparseableDate;

    /// <summary>Records with no resolvable survey window from either the record or <c>meta</c> (→ NULL).</summary>
    public int MissingSurveyWindow;

    /// <summary>In-batch duplicate merge keys collapsed last-wins.</summary>
    public int DuplicateKeys;

    /// <summary>
    /// Non-key string values that exceeded their target column width and were CLAMPED
    /// (<c>Region</c>/<c>PricingPoint</c>/<c>LocationName</c> — see <see cref="NgiWidths"/>). The row
    /// survives: a truncation <c>SqlException</c> from the MERGE would otherwise fail the whole work
    /// unit and keep failing every run. An over-long <c>PointCode</c> is NOT counted here — it is the
    /// merge key, so it is dropped-and-counted in <see cref="Dropped"/> instead of being shortened.
    /// </summary>
    public int Truncated;

    public override string ToString() =>
        $"dropped={Dropped}, mapKeyFallback={MapKeyFallback}, mapKeyMismatch={MapKeyMismatch}, " +
        $"issueDateMismatch={IssueDateMismatch}, unparseableNumeric={UnparseableNumeric}, " +
        $"unparseableDate={UnparseableDate}, missingSurveyWindow={MissingSurveyWindow}, " +
        $"duplicates={DuplicateKeys}, truncated={Truncated}";

    /// <summary>True when nothing at all needed tolerating — the expected steady state.</summary>
    public bool IsClean =>
        Dropped == 0 && MapKeyFallback == 0 && MapKeyMismatch == 0 && IssueDateMismatch == 0 &&
        UnparseableNumeric == 0 && UnparseableDate == 0 && MissingSurveyWindow == 0 &&
        DuplicateKeys == 0 && Truncated == 0;
}

/// <summary>
/// The target column widths from <c>sql/NGI/001</c> + <c>002</c>, mirrored here so the readers can
/// enforce them BEFORE the TVP bind (design §5.1). Without this guard the only symptom of a
/// longer-than-declared value is a server-side truncation <c>SqlException</c> raised by the MERGE,
/// which fails the WHOLE work unit (all ~163 rows for that issue date) and keeps failing every run —
/// inverting the tolerant-parse contract, under which a non-key field degrades (here: is clamped)
/// and only an unusable KEY drops its record.
///
/// <para>Live maxima are well inside these widths (PointCode 12, Region 24, PricingPoint 33,
/// LocationName 33), so nothing is clamped today — this is the guard for a silent NGI-side change.</para>
///
/// <para>⚠ Keep these in step with <c>sql/NGI/001</c> / <c>002</c>: widening a column there without
/// widening it here would silently keep clamping at the old width.</para>
/// </summary>
internal static class NgiWidths
{
    /// <summary>
    /// <c>arm.BidWeekLocation.PointCode</c> / <c>arm.BidWeekData.PointCode</c> — <c>VARCHAR(20)</c>.
    /// <b>NEVER truncate this one: it is the MERGE key.</b> A silently shortened key would merge onto a
    /// DIFFERENT row, so an over-long code is dropped-and-counted instead.
    /// </summary>
    public const int PointCode = 20;

    /// <summary><c>arm.BidWeekData.Region</c> — <c>VARCHAR(64)</c>. Non-key → clamped.</summary>
    public const int Region = 64;

    /// <summary><c>arm.BidWeekData.PricingPoint</c> — <c>VARCHAR(100)</c>. Non-key → clamped.</summary>
    public const int PricingPoint = 100;

    /// <summary><c>arm.BidWeekLocation.LocationName</c> — <c>VARCHAR(100)</c>. Non-key → clamped.</summary>
    public const int LocationName = 100;
}
