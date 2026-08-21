namespace DataLoader.OPIS;

/// <summary>
/// One parsed OPIS LP row — the payload of BOTH fact tables. arm.LPReport and
/// arm.LPReportHistory take an identical column set and differ only in grain, so
/// a single row type feeds a single TVP and the merge proc fans it into both.
///
/// <para>
/// Property names mirror the CSV header (and the SQL columns) so the mapping
/// stays obvious end to end: <c>Price, Mkt_Prod, Date, Low, High, Avg, Country,
/// Unit, Timing, Freq</c>.
/// </para>
/// <para>
/// <b>Key columns:</b> <see cref="MktProd"/>, <see cref="Date"/> and
/// <see cref="Timing"/> key arm.LPReport; <see cref="Price"/> joins them to key
/// arm.LPReportHistory. <see cref="Price"/> is not a number — it is the record
/// status code, <c>'I'</c> for the initial quote and <c>'U'</c> for a later
/// revision of it, which is exactly what makes the history table a history.
/// </para>
/// </summary>
public sealed class OpisLpReportRow
{
    /// <summary>Provenance — the arm.FileLog row for the file this came from.</summary>
    public int FileLogId { get; set; }

    /// <summary>Market + product label, TRIMMED (the source right-pads it with spaces).</summary>
    public required string MktProd { get; init; }

    /// <summary>The report date (CSV <c>Date</c>, published as <c>MM/dd/yy</c>).</summary>
    public required DateOnly Date { get; init; }

    /// <summary>Timing code — observed values <c>A</c>, <c>O</c>, <c>P</c>.</summary>
    public required string Timing { get; init; }

    /// <summary>Record status code — <c>I</c> = initial, <c>U</c> = updated/revision.</summary>
    public required string Price { get; init; }

    public decimal? Low { get; init; }
    public decimal? High { get; init; }
    public decimal? Avg { get; init; }

    public string? Country { get; init; }
    public string? Unit { get; init; }
    public string? Freq { get; init; }

    /// <summary>Publication date from the file name — the merge ordering guard, not a key.</summary>
    public required DateOnly SourceFileDate { get; init; }

    /// <summary>
    /// Build a row from one split CSV line. Returns null — and sets
    /// <paramref name="reason"/> — when the line cannot become a valid row, so the
    /// caller can count and log drops instead of failing the file.
    /// </summary>
    public static OpisLpReportRow? From(
        string[] fields, DateOnly sourceFileDate, string[] dateFormats, out string? reason)
    {
        reason = null;

        if (fields.Length != OpisCsv.FieldCount)
        {
            reason = $"expected {OpisCsv.FieldCount} fields, got {fields.Length}";
            return null;
        }

        // Column order is fixed by the header:
        // 0 Price | 1 Mkt_Prod | 2 Date | 3 Low | 4 High | 5 Avg | 6 Country | 7 Unit | 8 Timing | 9 Freq
        var price = OpisCsv.Field(fields, 0);
        var mktProd = OpisCsv.Field(fields, 1);
        var timing = OpisCsv.Field(fields, 8);

        // All three are PK components across the two tables — a row missing any of
        // them cannot be keyed and must be dropped rather than merged under a blank.
        if (price.Length == 0) { reason = "blank Price (PK)"; return null; }
        if (mktProd.Length == 0) { reason = "blank Mkt_Prod (PK)"; return null; }
        if (timing.Length == 0) { reason = "blank Timing (PK)"; return null; }

        var date = OpisCsv.Date(OpisCsv.Field(fields, 2), dateFormats);
        if (date is null) { reason = $"unparseable Date '{OpisCsv.Field(fields, 2)}'"; return null; }

        var low = OpisCsv.Price(fields, 3, out var badLow);
        var high = OpisCsv.Price(fields, 4, out var badHigh);
        var avg = OpisCsv.Price(fields, 5, out var badAvg);
        if (badLow || badHigh || badAvg)
            reason = "non-numeric price cell(s) stored as NULL"; // kept, not dropped

        return new OpisLpReportRow
        {
            MktProd = mktProd,
            Date = date.Value,
            Timing = timing,
            Price = price,
            Low = low,
            High = high,
            Avg = avg,
            Country = OpisCsv.NullableField(fields, 6),
            Unit = OpisCsv.NullableField(fields, 7),
            Freq = OpisCsv.NullableField(fields, 9),
            SourceFileDate = sourceFileDate
        };
    }
}
