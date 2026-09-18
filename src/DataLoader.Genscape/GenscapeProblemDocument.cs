using System.Text.Json;

namespace DataLoader.Genscape;

/// <summary>
/// Summarises the RFC 7807 body a <c>400</c> carries, for the exception message.
///
/// <para>
/// The useful part is <c>invalidParameters</c>, which names the offending parameter and
/// why it was rejected — e.g. <c>startDate &amp; endDate: [endDate] must be after
/// [startDate]</c>. Without it the message would say only "400", and the operator would
/// have to reproduce the request by hand to find out which parameter was wrong.
/// </para>
/// <para>
/// ⚠ Only the response BODY is summarised. The request URI carries no secret (the key
/// travels in the <c>Gen-Api-Key</c> header), but nothing here should ever grow to
/// include request headers.
/// </para>
/// </summary>
internal static class GenscapeProblemDocument
{
    public static string Describe(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "(empty response body)";

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object) return Excerpt(body);

            var title = root.TryGetProperty("title", out var t) ? t.GetString() : null;
            var detail = root.TryGetProperty("detail", out var d) ? d.GetString() : null;

            var parameters = new List<string>();
            if (root.TryGetProperty("invalidParameters", out var invalid) &&
                invalid.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in invalid.EnumerateArray())
                {
                    var name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var reason = item.TryGetProperty("reason", out var r) ? r.GetString() : null;
                    parameters.Add($"{name ?? "?"}: {reason ?? "?"}");
                }
            }

            var summary = string.Join("; ", new[]
            {
                title,
                detail,
                parameters.Count > 0 ? string.Join(" | ", parameters) : null
            }.Where(s => !string.IsNullOrWhiteSpace(s)));

            return string.IsNullOrWhiteSpace(summary) ? Excerpt(body) : summary;
        }
        catch (JsonException)
        {
            // A 404 answers with a plain {"statusCode":…,"message":…} rather than a
            // problem document, and a CDN can answer with HTML. Both end up here.
            return Excerpt(body);
        }
    }

    /// <summary>At most 400 characters, newlines flattened — enough to diagnose, bounded for a log line.</summary>
    private static string Excerpt(string body)
    {
        var flat = body.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return flat.Length <= 400 ? flat : flat[..400] + "…";
    }
}
