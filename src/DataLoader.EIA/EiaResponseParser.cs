using System.Globalization;
using System.Text.Json;

namespace DataLoader.EIA;

/// <summary>A <c>warnings[]</c> entry from an EIA response.</summary>
internal readonly record struct EiaWarning(string Warning, string Description);

/// <summary>One page of a data response, after parsing.</summary>
internal sealed class EiaParsedPage
{
    /// <summary>
    /// <c>response.total</c> — rows matching the request, independent of paging. The
    /// vendor sends it as a STRING (<c>"total":"143"</c>) but has been seen to send a
    /// number; both are accepted. -1 when absent.
    /// </summary>
    public long Total { get; init; } = -1;

    /// <summary><c>response.dateFormat</c>, e.g. <c>YYYY-MM</c>.</summary>
    public string? DateFormat { get; init; }

    /// <summary><c>response.frequency</c>, echoed back.</summary>
    public string? Frequency { get; init; }

    public required IReadOnlyList<EiaRow> Rows { get; init; }

    /// <summary>Rows skipped because they carried no usable <c>period</c>.</summary>
    public int RowsDropped { get; init; }

    public required IReadOnlyList<EiaWarning> Warnings { get; init; }

    /// <summary>
    /// True when the vendor says the answer is truncated. NOT a reliable
    /// "there is more" signal on its own — it is also returned when <c>offset</c> is
    /// past the end and the page is EMPTY. The pager treats an empty page as the stop
    /// condition and uses this only for logging.
    /// </summary>
    public bool IncompleteReturn =>
        Warnings.Any(w => w.Warning.Contains("incomplete return", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True when the vendor rejected a parameter value and silently substituted its
    /// own — today only <c>length</c> above 5,000. This is a LOADER bug, not a vendor
    /// problem, and <see cref="EiaSourceReader"/> throws on it rather than quietly
    /// running at a page size it did not choose.
    /// </summary>
    public bool ParameterOutOfRange =>
        Warnings.Any(w => w.Warning.Contains("parameter out of range", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Turns one EIA data response into <see cref="EiaRow"/>s.
///
/// <para>
/// <b>The field partition is the whole job.</b> Every property of a data row goes to
/// exactly one of three places:
/// </para>
/// <list type="bullet">
///   <item><c>period</c> → the <c>Period</c> column, stored as the API spelled it.</item>
///   <item>a name in <see cref="EiaSeriesPlan.DataFieldNames"/> → <c>DataJSON</c>.</item>
///   <item>anything else → <c>FacetJSON</c>.</item>
/// </list>
/// <para>
/// See <see cref="EiaSeriesPlan.DataFieldNames"/> for how that set is built and for the
/// 134-series check that confirmed it against the incumbent table.
/// </para>
/// </summary>
internal static class EiaResponseParser
{
    private const string PeriodField = "period";

    public static EiaParsedPage Parse(
        string json,
        EiaSeriesPlan plan,
        string frequencyId,
        bool coerceNumericStrings)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var warnings = ReadWarnings(root);

        if (!root.TryGetProperty("response", out var response) ||
            response.ValueKind != JsonValueKind.Object)
        {
            return new EiaParsedPage
            {
                Rows = Array.Empty<EiaRow>(),
                Warnings = warnings
            };
        }

        var total = ReadTotal(response);
        var dateFormat = ReadOptionalString(response, "dateFormat");
        var frequency = ReadOptionalString(response, "frequency");

        var rows = new List<EiaRow>();
        var dropped = 0;

        if (response.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            // Reused across rows; cleared per row. A wide series has ~15 fields and a
            // page has 5,000 rows, so this saves ~75,000 list allocations per request.
            var facetFields = new List<EiaField>(16);
            var dataFields = new List<EiaField>(8);

            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) { dropped++; continue; }

                facetFields.Clear();
                dataFields.Clear();
                string? period = null;

                foreach (var property in item.EnumerateObject())
                {
                    var name = property.Name;
                    var value = property.Value;

                    if (string.Equals(name, PeriodField, StringComparison.Ordinal))
                    {
                        period = value.ValueKind switch
                        {
                            JsonValueKind.String => value.GetString(),
                            JsonValueKind.Number => value.GetRawText(),
                            _ => null
                        };
                        continue;
                    }

                    var field = ToField(name, value);
                    if (plan.DataFieldNames.Contains(name)) dataFields.Add(field);
                    else facetFields.Add(field);
                }

                // A row with no period cannot be keyed. Dropped and counted rather than
                // merged under a blank Period, which would collide every such row of
                // the window onto one primary key.
                if (string.IsNullOrWhiteSpace(period)) { dropped++; continue; }

                var facetJson = EiaCanonicalJson.Render(facetFields, coerceNumericStrings);
                var dataJson = EiaCanonicalJson.Render(dataFields, coerceNumericStrings);

                rows.Add(new EiaRow(
                    plan.Series.SeriesId,
                    frequencyId,
                    period!,
                    EiaLegacyHash.Compute(facetJson),
                    facetJson,
                    dataJson,
                    EiaLegacyHash.Compute(dataJson)));
            }
        }

        return new EiaParsedPage
        {
            Total = total,
            DateFormat = dateFormat,
            Frequency = frequency,
            Rows = rows,
            RowsDropped = dropped,
            Warnings = warnings
        };
    }

    /// <summary>
    /// Capture a property with enough fidelity to re-serialise it.
    ///
    /// <para>
    /// A number keeps its RAW token text. Parsing it to a <c>double</c> and
    /// re-rendering would change <c>6.0754044100385393E-05</c> — a real value in the
    /// incumbent table — and a changed rendering is a changed hash is a re-keyed row.
    /// </para>
    /// </summary>
    private static EiaField ToField(string name, JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => new EiaField(name, JsonValueKind.String, value.GetString()),
        JsonValueKind.Number => new EiaField(name, JsonValueKind.Number, value.GetRawText()),
        JsonValueKind.True   => new EiaField(name, JsonValueKind.True, null),
        JsonValueKind.False  => new EiaField(name, JsonValueKind.False, null),
        JsonValueKind.Null or JsonValueKind.Undefined => new EiaField(name, JsonValueKind.Null, null),
        // Object/array: EiaCanonicalJson refuses it loudly rather than inventing an
        // ordering rule. Carried through so the error names the offending field.
        _ => new EiaField(name, value.ValueKind, value.GetRawText())
    };

    /// <summary><c>response.total</c>, sent as a string but tolerated as a number.</summary>
    private static long ReadTotal(JsonElement response)
    {
        if (!response.TryGetProperty("total", out var total)) return -1;

        return total.ValueKind switch
        {
            JsonValueKind.Number => total.TryGetInt64(out var n) ? n : -1,
            JsonValueKind.String => long.TryParse(total.GetString(), NumberStyles.Integer,
                                                  CultureInfo.InvariantCulture, out var s) ? s : -1,
            _ => -1
        };
    }

    private static string? ReadOptionalString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    /// <summary>
    /// <c>warnings</c> sits at the ROOT, outside <c>response</c> — easy to miss, and
    /// missing it is how a truncated answer gets mistaken for a complete one. Observed
    /// as an array; an object form is tolerated.
    /// </summary>
    private static IReadOnlyList<EiaWarning> ReadWarnings(JsonElement root)
    {
        if (!root.TryGetProperty("warnings", out var warnings))
            return Array.Empty<EiaWarning>();

        var list = new List<EiaWarning>();

        if (warnings.ValueKind == JsonValueKind.Array)
        {
            foreach (var w in warnings.EnumerateArray()) AddWarning(list, w);
        }
        else if (warnings.ValueKind == JsonValueKind.Object)
        {
            AddWarning(list, warnings);
        }

        return list;
    }

    private static void AddWarning(List<EiaWarning> list, JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return;

        list.Add(new EiaWarning(
            ReadOptionalString(element, "warning") ?? string.Empty,
            ReadOptionalString(element, "description") ?? string.Empty));
    }
}
