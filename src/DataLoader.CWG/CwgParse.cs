using System.Globalization;

namespace DataLoader.CWG;

/// <summary>
/// The 24 exact hour-beginning EST clock labels used by shapes C and D
/// (docs/apis §5). Index = <c>HourOfDay</c> 0–23. Matching is exact after
/// <see cref="string.Trim()"/>.
/// </summary>
internal static class CwgHours
{
    public static readonly string[] Labels =
    {
        "12:00 AM", "1:00 AM", "2:00 AM", "3:00 AM", "4:00 AM", "5:00 AM", "6:00 AM", "7:00 AM",
        "8:00 AM", "9:00 AM", "10:00 AM", "11:00 AM", "12:00 PM", "1:00 PM", "2:00 PM", "3:00 PM",
        "4:00 PM", "5:00 PM", "6:00 PM", "7:00 PM", "8:00 PM", "9:00 PM", "10:00 PM", "11:00 PM"
    };

    private static readonly Dictionary<string, int> Index = BuildIndex();

    private static Dictionary<string, int> BuildIndex()
    {
        var d = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < Labels.Length; i++) d[Labels[i]] = i;
        return d;
    }

    /// <summary>0–23 for a known label (exact match after Trim); −1 otherwise.</summary>
    public static int IndexOf(string label) => Index.TryGetValue(label.Trim(), out var i) ? i : -1;
}

/// <summary>
/// Invariant-culture value/date parsers (design §4). Each is a tolerant
/// <c>TryParse</c>: a blank or the literal <c>"NULL"</c> sentinel is treated as
/// unparseable so the caller (a row factory) drops the cell/row rather than
/// storing a spurious zero.
/// </summary>
internal static class CwgParse
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Blank / whitespace / literal "NULL" → treat as no value.</summary>
    public static bool IsSentinel(string? v) =>
        string.IsNullOrWhiteSpace(v) || v.Trim().Equals("NULL", StringComparison.OrdinalIgnoreCase);

    /// <summary>Plain decimal (signed). Sentinel → false.</summary>
    public static bool Decimal(string? v, out decimal result)
    {
        result = 0m;
        if (IsSentinel(v)) return false;
        return decimal.TryParse(v!.Trim(), NumberStyles.Number, Inv, out result);
    }

    /// <summary>Decimal after stripping one trailing <c>%</c> (keeps sign); degrades to plain decimal when absent.</summary>
    public static bool Percent(string? v, out decimal result)
    {
        result = 0m;
        if (IsSentinel(v)) return false;
        var s = v!.Trim();
        if (s.EndsWith("%", StringComparison.Ordinal)) s = s.Substring(0, s.Length - 1).Trim();
        return decimal.TryParse(s, NumberStyles.Number, Inv, out result);
    }

    /// <summary>SMALLINT integer (signed). Sentinel → false.</summary>
    public static bool Short(string? v, out short result)
    {
        result = 0;
        if (IsSentinel(v)) return false;
        return short.TryParse(v!.Trim(), NumberStyles.Integer, Inv, out result);
    }

    /// <summary><c>M/d/yy</c> — 2-digit year (.NET pivot → <c>26</c> = 2026).</summary>
    public static bool DateMdyy(string? v, out DateOnly result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(v)) return false;
        return DateOnly.TryParseExact(v.Trim(), "M/d/yy", Inv, DateTimeStyles.None, out result);
    }

    /// <summary><c>yyyy-MM-dd</c>.</summary>
    public static bool DateIso(string? v, out DateOnly result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(v)) return false;
        return DateOnly.TryParseExact(v.Trim(), "yyyy-MM-dd", Inv, DateTimeStyles.None, out result);
    }

    /// <summary>Both <c>M/d/yyyy</c> (non-padded) and <c>MM/dd/yyyy</c> (padded) — caller need not know which.</summary>
    public static bool DateMdyyyy(string? v, out DateOnly result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(v)) return false;
        var s = v.Trim();
        return DateOnly.TryParseExact(s, "M/d/yyyy", Inv, DateTimeStyles.None, out result)
            || DateOnly.TryParseExact(s, "MM/dd/yyyy", Inv, DateTimeStyles.None, out result);
    }

    /// <summary><c>yyyy-MM-dd HH:mm:ss</c> → DATETIME2(0).</summary>
    public static bool DateTimeIso(string? v, out DateTime result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(v)) return false;
        return DateTime.TryParseExact(v.Trim(), "yyyy-MM-dd HH:mm:ss", Inv, DateTimeStyles.None, out result);
    }

    /// <summary>Text <c>True</c>/<c>False</c> → BIT.</summary>
    public static bool Bit(string? v, out bool result)
    {
        result = false;
        if (string.IsNullOrWhiteSpace(v)) return false;
        var s = v.Trim();
        if (s.Equals("True", StringComparison.OrdinalIgnoreCase)) { result = true; return true; }
        if (s.Equals("False", StringComparison.OrdinalIgnoreCase)) { result = false; return true; }
        return false;
    }

    /// <summary>Trimmed string, or null when blank/whitespace (kept literal otherwise — no sentinel remap).</summary>
    public static string? NullIfBlank(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
}
