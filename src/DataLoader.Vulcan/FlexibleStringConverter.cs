using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataLoader.Vulcan;

/// <summary>
/// Reads a string property from any JSON scalar. The Vulcan API is inconsistent
/// about scalar typing — e.g. <c>plant_id</c> arrives quoted (<c>"68347"</c>) on
/// some tables and as a bare number (<c>68347</c>) on others. A number, boolean,
/// or string is coerced to its source text; null stays null.
/// </summary>
public sealed class FlexibleStringConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Null => null,
            // Preserve the source representation without forcing a numeric CLR type.
            JsonTokenType.Number => reader.HasValueSequence
                ? Encoding.UTF8.GetString(reader.ValueSequence.ToArray())
                : Encoding.UTF8.GetString(reader.ValueSpan),
            JsonTokenType.True => "true",
            JsonTokenType.False => "false",
            _ => throw new JsonException($"Cannot convert JSON {reader.TokenType} to string.")
        };

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}
