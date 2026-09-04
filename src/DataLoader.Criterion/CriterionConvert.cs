using System.Globalization;

namespace DataLoader.Criterion;

/// <summary>
/// Narrows a value read from PostgreSQL to the CLR type the target column wants.
///
/// <para>
/// Most conversions are identities — Npgsql already hands back <see cref="Guid"/>,
/// <see cref="DateTime"/>, <see cref="short"/> and so on for the matching
/// PostgreSQL types. The cases that are NOT identities are the ones worth having a
/// single place for:
/// </para>
/// <list type="bullet">
///   <item>
///     <c>numeric(13,10)</c> arrives as <see cref="decimal"/> but a <c>real</c> or
///     <c>double precision</c> column read into a decimal target would throw at the
///     DataTable rather than at a readable call site.
///   </item>
///   <item>
///     <c>text</c> / <c>character varying</c> can exceed the target's VARCHAR width.
///     SqlClient truncates a too-long TVP string SILENTLY in some paths and errors in
///     others; neither is a good outcome, so <see cref="Fit"/> makes the decision
///     explicit and the caller counts it.
///   </item>
///   <item>
///     Any residual <c>character(n)</c> padding. The SELECT already trims these
///     server-side (<see cref="CriterionColumn.Trimmed"/>), but a column added later
///     without the wrapper would otherwise carry padding into the target unnoticed.
///   </item>
/// </list>
/// </summary>
internal static class CriterionConvert
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>
    /// Converts one source value for one descriptor column.
    ///
    /// <para>
    /// <b>Two deliberate failure postures.</b> A string, GUID, boolean or date that
    /// cannot be read degrades to <see cref="DBNull.Value"/> — one unreadable
    /// descriptive value must not cost a 460,000-row work unit, and if the column is a
    /// key the caller drops and counts the row instead. A NUMERIC conversion is
    /// allowed to THROW: an overflow or format failure there means the source changed a
    /// column's type underneath us, and quietly nulling a quantity would corrupt
    /// analytics far more expensively than a failed work unit.
    /// </para>
    /// </summary>
    /// <param name="truncated">
    /// Set when a string had to be shortened to fit the target column. The reader
    /// accumulates these and logs a single count per work unit — a silent truncation
    /// is a data-quality event, not a non-event.
    /// </param>
    public static object Value(object? raw, CriterionColumn column, out bool truncated)
    {
        truncated = false;

        if (raw is null || raw is DBNull) return DBNull.Value;

        switch (column.Type)
        {
            case CriterionColumnType.String:
            case CriterionColumnType.Char:
            {
                // Trim defensively: the SELECT trims every character(n) column, but a
                // column added later without the wrapper would otherwise carry the
                // blank padding straight into the target.
                var text = (raw as string ?? Convert.ToString(raw, Inv))?.Trim();
                if (string.IsNullOrEmpty(text)) return DBNull.Value;
                return Fit(text, column, out truncated);
            }

            case CriterionColumnType.Guid:
                return raw switch
                {
                    Guid g => g,
                    string s when Guid.TryParse(s, out var parsed) => parsed,
                    _ => DBNull.Value
                };

            case CriterionColumnType.Boolean:
                return raw switch
                {
                    bool b => b,
                    string s when bool.TryParse(s, out var parsed) => parsed,
                    // PostgreSQL can also express a boolean as 't'/'f' through some
                    // drivers and as 0/1 through others.
                    string s when s.Length == 1 => s[0] is 't' or 'T' or '1' or 'y' or 'Y',
                    // Anything else degrades to NULL rather than throwing, matching the
                    // Guid case above. Npgsql returns a real bool for `boolean`, so this
                    // is only reachable if the source changes the column's type.
                    string => DBNull.Value,
                    _ => Convert.ToBoolean(raw, Inv)
                };

            case CriterionColumnType.Int16:
                return raw is short s16 ? s16 : Convert.ToInt16(raw, Inv);

            case CriterionColumnType.Int32:
                return raw is int i32 ? i32 : Convert.ToInt32(raw, Inv);

            case CriterionColumnType.Double:
                return raw is double d ? d : Convert.ToDouble(raw, Inv);

            case CriterionColumnType.Decimal:
                return raw is decimal m ? m : Convert.ToDecimal(raw, Inv);

            case CriterionColumnType.Date:
            case CriterionColumnType.DateTime:
                return raw switch
                {
                    DateTime dt => column.Type == CriterionColumnType.Date ? dt.Date : dt,
                    DateOnly d2 => d2.ToDateTime(TimeOnly.MinValue),
                    DateTimeOffset dto => column.Type == CriterionColumnType.Date
                        ? dto.UtcDateTime.Date
                        : dto.UtcDateTime,
                    _ => DBNull.Value
                };

            default:
                throw new NotSupportedException($"Unmapped CriterionColumnType '{column.Type}'.");
        }
    }

    /// <summary>
    /// Shortens <paramref name="text"/> to the target column's declared width.
    ///
    /// <para>
    /// No source value exceeds its target today — every column was measured against
    /// the live database on 2026-09-03 and the widest (<c>metadata_desc</c> and
    /// <c>series_desc</c>, 73 characters into VARCHAR(100)/(150)) has ample room.
    /// This exists because the source can widen a column at any time without telling
    /// anyone, and the alternative to truncating is a run that fails a whole work
    /// unit on one long description.
    /// </para>
    /// <para>
    /// VARCHAR(MAX) columns are returned untouched.
    /// </para>
    /// </summary>
    private static object Fit(string text, CriterionColumn column, out bool truncated)
    {
        truncated = false;

        var max = MaxLength(column.SqlType);
        if (max <= 0 || text.Length <= max) return text;

        truncated = true;
        return text[..max];
    }

    /// <summary>
    /// The declared width of a <c>VARCHAR(n)</c> / <c>CHAR(n)</c>, or <c>-1</c> for
    /// <c>VARCHAR(MAX)</c> and for a type with no length.
    /// </summary>
    internal static int MaxLength(string sqlType)
    {
        var open = sqlType.IndexOf('(');
        if (open < 0) return -1;

        var close = sqlType.IndexOf(')', open + 1);
        if (close < 0) return -1;

        var inner = sqlType[(open + 1)..close].Trim();
        if (inner.Equals("MAX", StringComparison.OrdinalIgnoreCase)) return -1;

        return int.TryParse(inner, NumberStyles.Integer, Inv, out var n) ? n : -1;
    }
}
