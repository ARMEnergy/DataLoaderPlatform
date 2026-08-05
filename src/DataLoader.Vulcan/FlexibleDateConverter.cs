using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataLoader.Vulcan;

/// <summary>
/// Parses a nullable date from the Vulcan API's inconsistent date fields. Some
/// date columns arrive as ISO (<c>"2026-01-16"</c>), some as US
/// <c>M/d/yyyy</c> (<c>"10/21/2026"</c>), and some carry non-date sentinels
/// (<c>"TBD"</c>, <c>"none"</c>, <c>""</c>) that the default strict System.Text.Json
/// DateTime reader would reject, failing the whole batch. Anything unrecognized is
/// coerced to null rather than throwing, so one odd value never drops a page of rows.
/// </summary>
public sealed class FlexibleDateConverter : JsonConverter<DateTime?>
{
    private static readonly string[] Formats =
    {
        "yyyy-MM-dd",
        "yyyy-MM-ddTHH:mm:ss",
        "yyyy-MM-ddTHH:mm:ssZ",
        "yyyy-MM-ddTHH:mm:ss.fffZ",
        "M/d/yyyy",
        "MM/dd/yyyy",
        "M/d/yyyy H:mm:ss",
    };

    public override DateTime? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.Null => null,
            JsonTokenType.String => ParseDate(reader.GetString()),
            _ => throw new JsonException($"Cannot convert JSON {reader.TokenType} to DateTime.")
        };

    /// <summary>Exposed for unit testing the format/sentinel handling.</summary>
    internal static DateTime? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var s = value.Trim();

        // Non-date placeholders the API emits in date fields.
        if (s.Equals("TBD", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("none", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("N/A", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("null", StringComparison.OrdinalIgnoreCase))
            return null;

        // These are logical dates; keep them in UTC so a trailing "Z" never shifts
        // the day across the local timezone boundary.
        const DateTimeStyles styles = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;

        if (DateTime.TryParseExact(s, Formats, CultureInfo.InvariantCulture, styles, out var exact))
            return exact;

        // Last resort: tolerate any other invariant-parseable form; null on failure.
        return DateTime.TryParse(s, CultureInfo.InvariantCulture, styles, out var loose)
            ? loose
            : null;
    }

    public override void Write(Utf8JsonWriter writer, DateTime? value, JsonSerializerOptions options)
    {
        if (value is null)
            writer.WriteNullValue();
        else
            writer.WriteStringValue(value.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
    }
}
