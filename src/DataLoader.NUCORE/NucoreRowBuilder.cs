using System.Globalization;
using System.Text.Json;

namespace DataLoader.NUCORE;

/// <summary>One target row: the endpoint's column values, in TVP order.</summary>
internal sealed class NucoreRow
{
    public required object[] Values { get; init; }
}

/// <summary>Why a row was rejected, for the per-unit error summary.</summary>
internal sealed record NucoreRowError(string Column, string Reason);

/// <summary>
/// Converts one JSON object from a NUCORE response into an <see cref="NucoreRow"/>, driven
/// entirely by the endpoint's column list so that the DataTable half of the TVP contract
/// cannot drift from the descriptor.
/// </summary>
internal sealed class NucoreRowBuilder
{
    private readonly NucoreEndpoint _endpoint;

    public NucoreRowBuilder(NucoreEndpoint endpoint) => _endpoint = endpoint;

    /// <summary>
    /// Builds a row, or returns <c>null</c> and sets <paramref name="error"/> if a value
    /// cannot be represented. A rejected row is counted and reported rather than merged
    /// with a silently wrong value.
    /// </summary>
    public NucoreRow? TryBuild(JsonElement source, out NucoreRowError? error)
    {
        error = null;
        var columns = _endpoint.Columns;
        var values = new object[columns.Count];

        for (var i = 0; i < columns.Count; i++)
        {
            var column = columns[i];

            if (!source.TryGetProperty(column.JsonName, out var element))
            {
                // The vendor dropped a property the spec declares. A nullable column can
                // absorb that; a NOT NULL one cannot, and guessing a value would be worse
                // than refusing the row.
                if (column.Nullable) { values[i] = DBNull.Value; continue; }
                if (column.Kind == NucoreColumnKind.String && column.IsKey) { values[i] = string.Empty; continue; }

                error = new NucoreRowError(column.Name, "required property missing from the response");
                return null;
            }

            if (!TryConvert(element, column, out var value, out var reason))
            {
                error = new NucoreRowError(column.Name, reason!);
                return null;
            }

            values[i] = value!;
        }

        return new NucoreRow { Values = values };
    }

    /// <summary>
    /// Converts one JSON value to the CLR type its column declares.
    /// </summary>
    internal static bool TryConvert(
        JsonElement element, NucoreColumn column, out object? value, out string? reason)
    {
        value = null;
        reason = null;

        if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            if (column.Nullable) { value = DBNull.Value; return true; }

            // A NULL arriving in a key column is normal for this vendor - most of the
            // dimension columns are declared nullable and some are genuinely empty. Key
            // strings collapse to '' so the natural key stays a real PRIMARY KEY and the
            // MERGE join needs no NULL-safe comparison. A blank key is a stable, intentional
            // identity; a NULL one would silently insert a duplicate on every run.
            if (column.Kind == NucoreColumnKind.String && column.IsKey) { value = string.Empty; return true; }

            reason = "null in a NOT NULL column";
            return false;
        }

        switch (column.Kind)
        {
            case NucoreColumnKind.String:
                {
                    var text = element.ValueKind == JsonValueKind.String
                        ? element.GetString() ?? string.Empty
                        : element.GetRawText();

                    // Several source columns are fixed-width CHAR and arrive space-padded -
                    // VarResults.productGroup is padded to 100 characters. Trimming keeps
                    // them joinable and comparable; it is applied uniformly so both sides of
                    // any later comparison agree.
                    text = text.Trim();

                    var max = column.MaxLength;
                    if (max is not null && text.Length > max)
                    {
                        // Named here rather than letting SqlClient raise "String or binary
                        // data would be truncated", which names no column and turns a
                        // one-line fix into an afternoon.
                        reason = $"value of {text.Length} chars exceeds {column.SqlType}";
                        return false;
                    }

                    if (text.Length == 0 && column.Nullable) { value = DBNull.Value; return true; }
                    value = text;
                    return true;
                }

            case NucoreColumnKind.DateTime:
                {
                    var text = element.ValueKind == JsonValueKind.String ? element.GetString() : null;
                    if (text is null) { reason = $"expected a date-time string, got {element.ValueKind}"; return false; }

                    // DateTimeStyles.None => Kind is Unspecified and the wall-clock value is
                    // stored exactly as written. RoundtripKind or AdjustToUniversal would
                    // shift a bare "2026-10-14T00:00:00" by the host's offset and move the
                    // business date across a day boundary.
                    if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                    { reason = $"unparseable date-time '{text}'"; return false; }

                    value = dt;
                    return true;
                }

            case NucoreColumnKind.DateTimeOffset:
                {
                    var text = element.ValueKind == JsonValueKind.String ? element.GetString() : null;
                    if (text is null) { reason = $"expected a date-time string, got {element.ValueKind}"; return false; }

                    if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto))
                    { reason = $"unparseable date-time offset '{text}'"; return false; }

                    value = dto;
                    return true;
                }

            case NucoreColumnKind.Int32:
                {
                    if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var i)) { value = i; return true; }
                    if (element.ValueKind == JsonValueKind.String &&
                        int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var si))
                    { value = si; return true; }
                    reason = $"not an INT: {Describe(element)}";
                    return false;
                }

            case NucoreColumnKind.Int64:
                {
                    if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var l)) { value = l; return true; }
                    if (element.ValueKind == JsonValueKind.String &&
                        long.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var sl))
                    { value = sl; return true; }
                    reason = $"not a BIGINT: {Describe(element)}";
                    return false;
                }

            case NucoreColumnKind.Decimal:
                {
                    decimal d;
                    if (element.ValueKind == JsonValueKind.Number)
                    {
                        if (!element.TryGetDecimal(out d))
                        {
                            // The vendor declares these as double, so a value outside
                            // decimal's range is possible in principle. Refuse it loudly:
                            // the alternative is a silently clamped P&L number.
                            reason = $"numeric value outside DECIMAL range: {element.GetRawText()}";
                            return false;
                        }
                    }
                    else if (element.ValueKind == JsonValueKind.String &&
                             decimal.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var sd))
                    {
                        d = sd;
                    }
                    else
                    {
                        reason = $"not a DECIMAL: {Describe(element)}";
                        return false;
                    }

                    // Round to the column's scale here, so an over-precise value is a
                    // decision taken in code rather than a silent server-side truncation.
                    d = Math.Round(d, NucoreColumns.DecimalScale, MidpointRounding.ToEven);

                    if (Math.Abs(d) >= MaxDecimalMagnitude)
                    {
                        reason = $"value {d} overflows {column.SqlType}";
                        return false;
                    }

                    value = d;
                    return true;
                }

            case NucoreColumnKind.Boolean:
                {
                    if (element.ValueKind is JsonValueKind.True or JsonValueKind.False) { value = element.GetBoolean(); return true; }
                    if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var bi) && bi is 0 or 1)
                    { value = bi == 1; return true; }
                    reason = $"not a BIT: {Describe(element)}";
                    return false;
                }

            case NucoreColumnKind.Guid:
                {
                    if (element.ValueKind == JsonValueKind.String &&
                        Guid.TryParse(element.GetString(), out var g))
                    { value = g; return true; }
                    reason = $"not a UNIQUEIDENTIFIER: {Describe(element)}";
                    return false;
                }

            default:
                reason = $"unmapped column kind {column.Kind}";
                return false;
        }
    }

    /// <summary>
    /// 10^(precision - scale): the first magnitude a DECIMAL(28,12) cannot hold.
    /// </summary>
    private static readonly decimal MaxDecimalMagnitude =
        Pow10(NucoreColumns.DecimalPrecision - NucoreColumns.DecimalScale);

    private static decimal Pow10(int exponent)
    {
        var result = 1m;
        for (var i = 0; i < exponent; i++) result *= 10m;
        return result;
    }

    private static string Describe(JsonElement element)
    {
        var raw = element.GetRawText();
        return raw.Length <= 40 ? raw : raw[..40] + "...";
    }
}
