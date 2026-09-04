using System.Globalization;
using System.Text.Json;

namespace DataLoader.Criterion;

/// <summary>What one <c>data</c> array unpivoted into, and what it cost.</summary>
/// <param name="Rows">The observations that survived, one per calendar date.</param>
/// <param name="Elements">Elements the array contained.</param>
/// <param name="Collapsed">
/// Observations discarded because another observation shared their calendar date.
/// This is the intraday loss the requester accepted — see <see cref="CriterionSeriesData"/>.
/// </param>
/// <param name="Unparseable">Elements dropped because their <c>date</c> could not be read.</param>
public readonly record struct SeriesDataParse(
    IReadOnlyList<(DateTime Date, double? Value)> Rows,
    int Elements,
    int Collapsed,
    int Unparseable);

/// <summary>
/// Unpivots the <c>data</c> JSON array of <c>data_series.financial_json_latest</c>
/// into one row per calendar date.
///
/// <para><b>The array has two shapes in production</b> (verified live 2026-09-03,
/// docs/apis/Criterion.md 4.3), and both appear in the same table:</para>
/// <code>
///   daily    ~97%:  [{"date":"09/02/2026","value":4966.60}, ...]
///   intraday  ~3%:  [{"date":"07/05/2026","timestamp":"07/05/2026 00:04:57",
///                     "time_zone":"Central Time","value":4966.60}, ...]
/// </code>
/// <para>
/// Only <c>date</c> and <c>value</c> are read. <c>timestamp</c> and <c>time_zone</c>
/// are deliberately ignored: the target's primary key is
/// <c>(FinancialJsonId, Date)</c>, so there is nowhere to put a time.
/// </para>
///
/// <para><b>⚠ THE INTRADAY COLLAPSE.</b> An intraday array carries up to 288
/// five-minute observations per calendar date (ERCOT Real-time Fuel Mix: 17,496
/// elements across 61 dates). The primary key admits one. The requester chose on
/// 2026-09-03 to keep the supplied key and accept last-wins rather than widen it
/// with a time column, so <b>287 of every 288 intraday observations are discarded
/// by design</b>.</para>
///
/// <para>
/// Two things make that acceptable rather than merely lossy. First, the winner is
/// DETERMINISTIC: the LAST element for a date in source array order wins, which for
/// a chronologically ordered intraday array is the day's closing observation — a
/// meaningful value, not an arbitrary one. Doing this in memory rather than leaving
/// it to SQL MERGE is what makes it deterministic at all; MERGE would otherwise pick
/// whichever duplicate the plan happened to reach last, and would in fact ERROR on
/// the duplicate key. Second, <see cref="SeriesDataParse.Collapsed"/> counts the
/// discards so the reader can log them per work unit — the loss is visible in every
/// run's output, never silent.
/// </para>
///
/// <para>
/// The parser streams with <see cref="Utf8JsonReader"/> over the raw bytes rather
/// than materialising a <c>JsonDocument</c>. At an average 165 KB per array and a
/// maximum of 1.7 MB, across ~46,000 arrays in a 30-day window, a document tree per
/// row would dominate the loader's memory profile for no benefit — nothing here
/// needs random access.
/// </para>
/// </summary>
internal static class CriterionSeriesData
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>
    /// Unpivots one array. A null, blank, or non-array payload yields zero rows
    /// rather than throwing: an empty <c>data</c> is a legitimate state for a series
    /// that has not published yet, and one malformed row must not fail a whole page.
    /// </summary>
    public static SeriesDataParse Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new SeriesDataParse(Array.Empty<(DateTime, double?)>(), 0, 0, 0);

        // Insertion-ordered so the output keeps the source's date order, which makes
        // a merge batch's rows land in key order and keeps the log readable.
        var byDate = new Dictionary<DateTime, double?>();
        var order = new List<DateTime>();

        var elements = 0;
        var collapsed = 0;
        var unparseable = 0;

        try
        {
            var reader = new Utf8JsonReader(
                System.Text.Encoding.UTF8.GetBytes(json),
                new JsonReaderOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });

            if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray)
                return new SeriesDataParse(Array.Empty<(DateTime, double?)>(), 0, 0, 0);

            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType != JsonTokenType.StartObject)
                {
                    // A scalar or nested array where an observation was expected.
                    reader.Skip();
                    unparseable++;
                    continue;
                }

                elements++;

                if (!TryReadObservation(ref reader, out var date, out var value))
                {
                    unparseable++;
                    continue;
                }

                // LAST wins — see the class remarks. Overwriting rather than skipping
                // is what makes "last" true.
                if (byDate.ContainsKey(date)) collapsed++;
                else order.Add(date);

                byDate[date] = value;
            }
        }
        catch (JsonException)
        {
            // Truncated or invalid JSON. Everything parsed so far is kept — a partial
            // series beats none — and the caller sees Elements > Rows.Count.
            unparseable++;
        }

        var rows = new List<(DateTime, double?)>(order.Count);
        foreach (var date in order) rows.Add((date, byDate[date]));

        return new SeriesDataParse(rows, elements, collapsed, unparseable);
    }

    /// <summary>
    /// Reads one observation object, leaving the reader on its <c>EndObject</c>.
    /// Returns false when the object carried no usable <c>date</c>.
    /// </summary>
    private static bool TryReadObservation(ref Utf8JsonReader reader, out DateTime date, out double? value)
    {
        date = default;
        value = null;

        var haveDate = false;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                reader.Skip();
                continue;
            }

            // Property names are compared case-insensitively: cheap, and the source's
            // sibling tables have been seen to differ in casing between feeds.
            var isDate = reader.ValueTextEquals("date") || reader.ValueTextEquals("Date");
            var isValue = !isDate && (reader.ValueTextEquals("value") || reader.ValueTextEquals("Value"));

            if (!reader.Read()) break;

            if (isDate)
            {
                haveDate = reader.TokenType == JsonTokenType.String
                           && CriterionTime.TryParseJsonDate(reader.GetString(), out date);
            }
            else if (isValue)
            {
                value = ReadValue(ref reader);
            }
            else
            {
                // timestamp / time_zone, or anything added later. Skipped by design.
                reader.Skip();
            }
        }

        return haveDate;
    }

    /// <summary>
    /// Reads a <c>value</c>, which is a JSON NUMBER in current data but was a
    /// QUOTED STRING in the sample the requester supplied
    /// (<c>{"date":"10/5/2021","value":"21453.5943776865"}</c>). Both are accepted,
    /// so a source that flips back does not silently null out every observation.
    /// </summary>
    private static double? ReadValue(ref Utf8JsonReader reader)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                return reader.TryGetDouble(out var d) ? d : null;

            case JsonTokenType.String:
            {
                var text = reader.GetString();
                if (string.IsNullOrWhiteSpace(text)) return null;
                return double.TryParse(text, NumberStyles.Float, Inv, out var parsed) ? parsed : null;
            }

            case JsonTokenType.Null:
                return null;

            default:
                reader.Skip();
                return null;
        }
    }
}
