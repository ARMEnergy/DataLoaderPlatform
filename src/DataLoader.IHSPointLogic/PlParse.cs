using System.Globalization;
using System.Text.Json;

namespace DataLoader.IHSPointLogic;

/// <summary>
/// Tolerant, invariant-culture parsers for IHSPointLogic JSON values (design §5.3). Each reads a
/// named property from a JSON object element and returns <c>null</c> for a missing / JSON-null /
/// blank / <c>" "</c> / unparseable value — never a row failure (assume any measure MAY arrive as a
/// JSON string and parse defensively). The mixed date formats verified in the API doc are each
/// handled: <c>yyyy-MM-dd</c> AND <c>MM/dd/yyyy</c> for <see cref="Date"/>; <c>yyyy-MM-dd HH:mm</c>
/// for <see cref="DateTime2"/>; <c>… -05:00</c> offsets for <see cref="DateTimeOffset"/>.
/// </summary>
internal static class PlParse
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static readonly string[] DateFormats = { "yyyy-MM-dd", "MM/dd/yyyy", "M/d/yyyy" };
    private static readonly string[] DateTime2Formats = { "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd" };
    private static readonly string[] DateTimeOffsetFormats =
    {
        "yyyy-MM-dd HH:mm:ss.fff zzz", "yyyy-MM-dd HH:mm:ss zzz",
        "yyyy-MM-ddTHH:mm:ss.fffzzz", "yyyy-MM-ddTHH:mm:sszzz"
    };

    /// <summary>Fetches a non-null property element by name; false if missing or JSON <c>null</c>.</summary>
    private static bool TryGet(JsonElement obj, string name, out JsonElement value)
    {
        if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out value) && value.ValueKind != JsonValueKind.Null)
            return true;
        value = default;
        return false;
    }

    /// <summary>Trimmed string, or null when missing / blank / <c>" "</c>.</summary>
    public static string? String(JsonElement obj, string name)
    {
        if (!TryGet(obj, name, out var v)) return null;
        var s = v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString();
        return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    /// <summary>Decimal from a JSON number OR string-number (e.g. <c>designcapacity "0.00000000"</c>, signed measures).</summary>
    public static decimal? Decimal(JsonElement obj, string name)
    {
        if (!TryGet(obj, name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d)) return d;
        if (v.ValueKind == JsonValueKind.String)
        {
            var s = v.GetString();
            if (!string.IsNullOrWhiteSpace(s) && decimal.TryParse(s.Trim(), NumberStyles.Float, Inv, out var d2)) return d2;
        }
        return null;
    }

    /// <summary>Int from a JSON number OR string (e.g. <c>facilitytypeid "1"</c>).</summary>
    public static int? Int(JsonElement obj, string name)
    {
        if (!TryGet(obj, name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String)
        {
            var s = v.GetString();
            if (!string.IsNullOrWhiteSpace(s) && int.TryParse(s.Trim(), NumberStyles.Integer, Inv, out var n2)) return n2;
        }
        return null;
    }

    /// <summary>Long from a JSON number OR string (notice <c>id</c>).</summary>
    public static long? Long(JsonElement obj, string name)
    {
        if (!TryGet(obj, name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String)
        {
            var s = v.GetString();
            if (!string.IsNullOrWhiteSpace(s) && long.TryParse(s.Trim(), NumberStyles.Integer, Inv, out var n2)) return n2;
        }
        return null;
    }

    /// <summary>Bit from a JSON bool OR <c>"true"/"false"</c> string.</summary>
    public static bool? Bit(JsonElement obj, string name)
    {
        if (!TryGet(obj, name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.True) return true;
        if (v.ValueKind == JsonValueKind.False) return false;
        if (v.ValueKind == JsonValueKind.String)
        {
            var s = v.GetString()?.Trim();
            if (bool.TryParse(s, out var b)) return b;
        }
        return null;
    }

    /// <summary><c>DATE</c> — accepts <c>yyyy-MM-dd</c> AND <c>MM/dd/yyyy</c> (pipelineflow/stateflows flowdate).</summary>
    public static DateOnly? Date(JsonElement obj, string name)
    {
        if (!TryGet(obj, name, out var v)) return null;
        var s = (v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString())?.Trim();
        if (string.IsNullOrWhiteSpace(s)) return null;
        return DateOnly.TryParseExact(s, DateFormats, Inv, DateTimeStyles.None, out var d) ? d : (DateOnly?)null;
    }

    /// <summary><c>DATETIME2(0)</c> — <c>yyyy-MM-dd HH:mm</c> (gasproduction.reporteddate) and tolerant variants.</summary>
    public static DateTime? DateTime2(JsonElement obj, string name)
    {
        if (!TryGet(obj, name, out var v)) return null;
        var s = (v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString())?.Trim();
        if (string.IsNullOrWhiteSpace(s)) return null;
        return DateTime.TryParseExact(s, DateTime2Formats, Inv, DateTimeStyles.None, out var dt) ? dt : (DateTime?)null;
    }

    /// <summary><c>DATETIMEOFFSET(3)</c> — <c>yyyy-MM-dd HH:mm:ss.fff -05:00</c> (notice dates), tolerant fallback.</summary>
    public static DateTimeOffset? DateTimeOffset(JsonElement obj, string name)
    {
        if (!TryGet(obj, name, out var v)) return null;
        var s = (v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString())?.Trim();
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (System.DateTimeOffset.TryParseExact(s, DateTimeOffsetFormats, Inv, DateTimeStyles.None, out var dto)) return dto;
        return System.DateTimeOffset.TryParse(s, Inv, DateTimeStyles.None, out var dto2) ? dto2 : (DateTimeOffset?)null;
    }
}
