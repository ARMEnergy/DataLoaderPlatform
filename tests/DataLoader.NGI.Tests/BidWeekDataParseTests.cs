using System.Globalization;
using System.Net;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DataLoader.NGI.Tests;

/// <summary>
/// <see cref="NgiBidWeekSourceReader"/> mapping, driven by the REAL captured live payload
/// <c>Samples/bidweekDatafeed_20260801.json</c> (a 200 for issue date 2026-08-01, a SATURDAY, with
/// 163 records).
///
/// <para>Two things make this the loader's highest-risk parse:</para>
/// <list type="number">
///   <item><b>Five field names contain SPACES</b> (<c>"Point Code"</c>, <c>"Issue Date"</c>,
///     <c>"Survey Start"</c>, <c>"Survey End"</c>, <c>"Pricing Point"</c>). No
///     <c>JsonNamingPolicy</c> can produce them, so default POCO binding would yield 163 rows of
///     NULL keys and NULL dates.</item>
///   <item><b>The null sentinel is the literal string <c>"None"</c></b> on EVERY field type,
///     strings included - so a naive passthrough persists the text "None" into
///     <c>Region</c>/<c>PricingPoint</c>, and an unguarded numeric fallback invents a price of 0.</item>
/// </list>
/// </summary>
public class BidWeekDataParseTests
{
    // ================================================== full 11-field mapping over the real fixture

    [Fact]
    public async Task LiveFixture_Maps_All163Records_WithFileLogIdStamped()
    {
        var result = await ReaderHarness.RunDatafeedAsync(HttpStatusCode.OK, Samples.Datafeed20260801);

        Assert.Equal(Samples.ExpectedRecordCount, result.Rows.Count);   // 163
        Assert.Equal(163, result.Rows.Select(r => r.PointCode).Distinct(StringComparer.Ordinal).Count());
        Assert.All(result.Rows, r => Assert.Equal(4242, r.FileLogId));  // stamped from the hub upsert

        var call = Assert.Single(result.FileLog.Calls);
        Assert.Equal("Success", call.Status);
        Assert.Equal(200, call.HttpStatus);
        Assert.Equal(163, call.RowCount);
        Assert.Equal("BidWeekData", call.File.Endpoint);
        Assert.Equal(Samples.FixtureIssueDate, call.File.RepresentativeDate);
        Assert.Equal("/bidweekDatafeed.json?issue_date=2026-08-01", call.RequestPath);
    }

    [Fact]
    public async Task LiveFixture_PinsOneRecord_FieldByField_AllElevenFields()
    {
        var rows = await ReaderHarness.ReadDatafeedAsync(Samples.Datafeed20260801);

        // Raw source record (verbatim from the fixture):
        // "STXAGUAD":{"Point Code":"STXAGUAD","Issue Date":"2026-08-01","Survey Start":"2026-07-27",
        //  "Survey End":"2026-07-29","Region":"South Texas","Pricing Point":"Agua Dulce",
        //  "Low":"2.360","High":"2.390","Average":"2.375","Volume":"None","Deals":"None"}
        var r = Assert.Single(rows, x => x.PointCode == "STXAGUAD");

        Assert.Equal(4242, r.FileLogId);                        // TVP col 1  (provenance)
        Assert.Equal(new DateOnly(2026, 8, 1), r.IssueDate);    // TVP col 2  "Issue Date"
        Assert.Equal("STXAGUAD", r.PointCode);                  // TVP col 3  "Point Code"
        Assert.Equal(new DateOnly(2026, 7, 27), r.SurveyStart);  // TVP col 4  "Survey Start" (READ, never computed)
        Assert.Equal(new DateOnly(2026, 7, 29), r.SurveyEnd);    // TVP col 5  "Survey End"
        Assert.Equal("South Texas", r.Region);                   // TVP col 6  "Region"
        Assert.Equal("Agua Dulce", r.PricingPoint);              // TVP col 7  "Pricing Point"
        Assert.Equal(2.360m, r.Low);                             // TVP col 8  "Low"
        Assert.Equal(2.390m, r.High);                            // TVP col 9  "High"
        Assert.Equal(2.375m, r.Average);                         // TVP col 10 "Average" (deal-weighted, not the midpoint)
        Assert.Null(r.Volume);                                   // TVP col 11 "Volume"  = "None" -> NULL
        Assert.Null(r.Deals);                                    // TVP col 12 "Deals"   = "None" -> NULL
    }

    [Fact]
    public async Task LiveFixture_PinsASecondRecord_AllMeasuresNone_StringsStillMapped()
    {
        var rows = await ReaderHarness.ReadDatafeedAsync(Samples.Datafeed20260801);

        // "ETXCARTH": every measure is the literal "None"; the two strings are real values.
        var r = Assert.Single(rows, x => x.PointCode == "ETXCARTH");

        Assert.Equal(new DateOnly(2026, 8, 1), r.IssueDate);
        Assert.Equal(new DateOnly(2026, 7, 27), r.SurveyStart);
        Assert.Equal(new DateOnly(2026, 7, 29), r.SurveyEnd);
        Assert.Equal("East Texas", r.Region);
        Assert.Equal("Carthage", r.PricingPoint);
        Assert.Null(r.Low);
        Assert.Null(r.High);
        Assert.Null(r.Average);
        Assert.Null(r.Volume);
        Assert.Null(r.Deals);
    }

    [Fact]
    public async Task LiveFixture_EveryDocumentedFieldIsMapped_ForEveryRecord()
    {
        // Cross-check the parse output against the RAW fixture, record by record, so a field that
        // silently stopped being mapped cannot hide behind the aggregate counts.
        var raw = Samples.RawDatafeedRecords();
        var rows = (await ReaderHarness.ReadDatafeedAsync(Samples.Datafeed20260801))
            .ToDictionary(r => r.PointCode, StringComparer.Ordinal);

        Assert.Equal(raw.Count, rows.Count);

        foreach (var (code, rec) in raw)
        {
            var row = rows[code];

            string? Text(string name)
            {
                var v = rec.GetProperty(name).GetString();
                return v == "None" ? null : v;
            }

            DateOnly? Day(string name) =>
                Text(name) is { } s ? DateOnly.ParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture) : null;
            decimal? Money(string name) =>
                Text(name) is { } s ? decimal.Parse(s, CultureInfo.InvariantCulture) : null;
            int? Count(string name) =>
                Text(name) is { } s ? int.Parse(s, CultureInfo.InvariantCulture) : null;

            Assert.Equal(code, row.PointCode);
            Assert.Equal(rec.GetProperty("Point Code").GetString(), row.PointCode);
            Assert.Equal(Day("Issue Date"), row.IssueDate);
            Assert.Equal(Day("Survey Start"), row.SurveyStart);
            Assert.Equal(Day("Survey End"), row.SurveyEnd);
            Assert.Equal(Text("Region"), row.Region);
            Assert.Equal(Text("Pricing Point"), row.PricingPoint);
            Assert.Equal(Money("Low"), row.Low);
            Assert.Equal(Money("High"), row.High);
            Assert.Equal(Money("Average"), row.Average);
            Assert.Equal(Count("Volume"), row.Volume);
            Assert.Equal(Count("Deals"), row.Deals);
        }
    }

    [Fact]
    public async Task LiveFixture_MetaMatchesEveryRecord_IssueAndSurveyWindow()
    {
        var rows = await ReaderHarness.ReadDatafeedAsync(Samples.Datafeed20260801);

        // meta {issue_date, start_date, end_date} was identical to the record fields on all 163.
        Assert.All(rows, r =>
        {
            Assert.Equal(Samples.FixtureIssueDate, r.IssueDate);
            Assert.Equal(Samples.FixtureSurveyStart, r.SurveyStart);
            Assert.Equal(Samples.FixtureSurveyEnd, r.SurveyEnd);
        });
    }

    [Fact]
    public async Task LiveFixture_ParsesCleanly_NoToleranceCountersLogged()
    {
        var result = await ReaderHarness.RunDatafeedAsync(HttpStatusCode.OK, Samples.Datafeed20260801);

        // The steady state: nothing needed tolerating, so no counter warning is emitted.
        Assert.DoesNotContain(result.Log.OfLevel(LogLevel.Warning), e => e.Message.Contains("tolerance counters"));
        Assert.DoesNotContain(result.Log.OfLevel(LogLevel.Warning), e => e.Message.Contains("dropped"));
    }

    // ================================================== "None" -> NULL on EVERY field, strings too

    [Fact]
    public async Task LiveFixture_NullSentinelCounts_MatchTheCapture_45Prices_47Activity()
    {
        var rows = await ReaderHarness.ReadDatafeedAsync(Samples.Datafeed20260801);

        Assert.Equal(Samples.ExpectedPriceNullCount, rows.Count(r => r.Low is null));      // 45
        Assert.Equal(Samples.ExpectedPriceNullCount, rows.Count(r => r.High is null));     // 45
        Assert.Equal(Samples.ExpectedPriceNullCount, rows.Count(r => r.Average is null));  // 45
        Assert.Equal(Samples.ExpectedActivityNullCount, rows.Count(r => r.Volume is null)); // 47
        Assert.Equal(Samples.ExpectedActivityNullCount, rows.Count(r => r.Deals is null));  // 47

        // Quirk A (verified live): the 45 price-null rows are a STRICT SUBSET of the 47 activity-null
        // rows - exactly 2 records are priced with NULL Volume/Deals. A "price implies volume" rule
        // would false-positive, which is why usp_ValidateLoad keeps PricedWithoutActivity informational.
        var pricedWithoutActivity = rows.Where(r => r.Average is not null && r.Volume is null)
            .Select(r => r.PointCode).OrderBy(c => c, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "CALSPGE", "STXAGUAD" }, pricedWithoutActivity);

        // No measure was ever silently coerced to 0 by a failed parse of "None".
        Assert.DoesNotContain(rows, r => r.Low == 0m || r.High == 0m || r.Average == 0m);
        Assert.DoesNotContain(rows, r => r.Volume == 0 || r.Deals == 0);
    }

    [Fact]
    public async Task LiveFixture_NoStringFieldEverHoldsTheLiteralNone()
    {
        var rows = await ReaderHarness.ReadDatafeedAsync(Samples.Datafeed20260801);

        Assert.DoesNotContain(rows, r => r.Region == "None");
        Assert.DoesNotContain(rows, r => r.PricingPoint == "None");
        // In this capture both strings are always populated (0 nulls) - the usp_ValidateLoad
        // UnexpectedNulls baseline.
        Assert.DoesNotContain(rows, r => r.Region is null);
        Assert.DoesNotContain(rows, r => r.PricingPoint is null);
    }

    [Fact]
    public async Task NoneOnEveryField_IncludingRegionAndPricingPoint_YieldsNull_NotTheText()
    {
        // Empty meta, so nothing can mask a "None" behind the documented meta fallback.
        var rows = await ReaderHarness.ReadDatafeedAsync(Samples.Envelope(Samples.AllNoneRecord, metaJson: "{}"));

        var r = Assert.Single(rows);
        Assert.Equal("ETXCARTH", r.PointCode);       // the key survives
        Assert.Null(r.Region);                       // NOT the literal text "None"
        Assert.Null(r.PricingPoint);                 // NOT the literal text "None"
        Assert.Null(r.SurveyStart);
        Assert.Null(r.SurveyEnd);
        Assert.Null(r.Low);
        Assert.Null(r.High);
        Assert.Null(r.Average);
        Assert.Null(r.Volume);
        Assert.Null(r.Deals);
    }

    [Fact]
    public async Task NoneOnTheSurveyWindow_FallsBackToMeta_ButTheStringsStayNull()
    {
        // Documents the precedence: record -> meta -> NULL. A record "None" is a MISSING value, so the
        // meta block legitimately supplies it; Region/PricingPoint have no meta source, so they stay
        // NULL rather than becoming the literal text "None".
        var rows = await ReaderHarness.ReadDatafeedAsync(Samples.Envelope(Samples.AllNoneRecord));

        var r = Assert.Single(rows);
        Assert.Equal(Samples.FixtureSurveyStart, r.SurveyStart);
        Assert.Equal(Samples.FixtureSurveyEnd, r.SurveyEnd);
        Assert.Null(r.Region);
        Assert.Null(r.PricingPoint);
    }

    [Fact]
    public async Task LiveFixture_UsavgRegionQuirk_PersistedAsPublished_NeverCorrected()
    {
        var rows = await ReaderHarness.ReadDatafeedAsync(Samples.Datafeed20260801);

        // Quirk B (verified live): the NATIONAL aggregate carries Region = "California". Persist as
        // published; never "fix" it in the loader.
        var usavg = Assert.Single(rows, r => r.PointCode == "USAVG");
        Assert.Equal("California", usavg.Region);
        Assert.Equal("National Avg.", usavg.PricingPoint);
        Assert.Equal(2.455m, usavg.Average);
        Assert.Equal(8647, usavg.Volume);
        Assert.Equal(1663, usavg.Deals);
    }

    [Fact]
    public async Task LiveFixture_RegionIsReadFromTheField_NotDerivedFromThePointCodePrefix()
    {
        var rows = (await ReaderHarness.ReadDatafeedAsync(Samples.Datafeed20260801))
            .ToDictionary(r => r.PointCode, StringComparer.Ordinal);

        // The code prefix frequently disagrees with the published Region - deriving it would be wrong.
        Assert.Equal("Midwest", rows["NEALEB"].Region);                    // NEA* prefix, Midwest region
        Assert.Equal("Northeast", rows["MCWNIAGR"].Region);                // MCW* prefix, Northeast region
        Assert.Equal("Southeast", rows["SLAFGTZ3"].Region);                // SLA* prefix, Southeast region
        Assert.Equal("North Louisiana/Arkansas", rows["ETXTGT"].Region);   // ETX* prefix, N. LA/AR region

        // 14 distinct values in this capture - free text, not an enum, and never a load gate.
        Assert.Equal(14, rows.Values.Select(r => r.Region).Distinct(StringComparer.Ordinal).Count());
    }

    // ================================================== dictionary enumeration semantics

    [Fact]
    public async Task RecordPointCodeWins_OverTheMapKey_AndTheMismatchIsCounted()
    {
        var result = await ReaderHarness.RunDatafeedAsync(
            HttpStatusCode.OK, Samples.Envelope(Samples.MapKeyMismatchRecord));

        var r = Assert.Single(result.Rows);
        Assert.Equal("STXAGUAD", r.PointCode);  // the RECORD's own "Point Code", never the map key
        Assert.NotEqual("WRONG-KEY", r.PointCode);

        // Counted, not silent.
        Assert.Contains(result.Log.OfLevel(LogLevel.Warning),
            e => e.Message.Contains("tolerance counters") && e.Message.Contains("mapKeyMismatch=1"));
    }

    [Fact]
    public async Task BlankRecordPointCode_FallsBackToTheMapKey_AndIsCounted()
    {
        var result = await ReaderHarness.RunDatafeedAsync(
            HttpStatusCode.OK, Samples.Envelope(Samples.NoPointCodeFieldRecord));

        var r = Assert.Single(result.Rows);
        Assert.Equal("STXAGUAD", r.PointCode);  // the map key is the documented fallback
        Assert.Contains(result.Log.OfLevel(LogLevel.Warning),
            e => e.Message.Contains("mapKeyFallback=1"));
    }

    [Fact]
    public async Task UnkeyableRecord_IsDroppedAndCounted_NotThrown()
    {
        // Blank map key AND "Point Code":"None" -> no usable merge key -> drop-and-count.
        var result = await ReaderHarness.RunDatafeedAsync(
            HttpStatusCode.OK, Samples.Envelope(Samples.UnkeyableRecord));

        Assert.Empty(result.Rows);                        // dropped, and NOTHING was thrown
        Assert.Contains(result.Log.OfLevel(LogLevel.Warning),
            e => e.Message.Contains("dropped 1 unkeyable record(s)"));
        Assert.Contains(result.Log.OfLevel(LogLevel.Warning), e => e.Message.Contains("dropped=1"));

        // Zero surviving rows is reported as NotAvailable with a real 200 status.
        var call = Assert.Single(result.FileLog.Calls);
        Assert.Equal("NotAvailable", call.Status);
        Assert.Equal(200, call.HttpStatus);
        Assert.Equal(0, call.RowCount);
    }

    [Fact]
    public async Task GoodAndUnkeyableRecordsTogether_KeepsTheGoodOne_DropsTheOther()
    {
        var body = Samples.Envelope("""
        {"STXNGPL":{"Point Code":"STXNGPL","Issue Date":"2026-08-01","Region":"South Texas","Pricing Point":"NGPL S. TX","Low":"2.325","High":"2.365","Average":"2.335","Volume":"240","Deals":"15"},
         "":{"Point Code":"None","Issue Date":"2026-08-01","Region":"South Texas","Pricing Point":"Nowhere"}}
        """);

        var result = await ReaderHarness.RunDatafeedAsync(HttpStatusCode.OK, body);

        var r = Assert.Single(result.Rows);
        Assert.Equal("STXNGPL", r.PointCode);
        Assert.Equal(1, Assert.Single(result.FileLog.Calls).RowCount);
    }

    [Fact]
    public async Task NonObjectRecordValue_IsDroppedAndCounted_NotThrown()
    {
        // data is an object, but one of its VALUES is a scalar -> that entry is dropped, the run lives.
        var body = Samples.Envelope("""
        {"STXNGPL":{"Point Code":"STXNGPL","Issue Date":"2026-08-01","Low":"2.325"},"BROKEN":"not-an-object"}
        """);

        var result = await ReaderHarness.RunDatafeedAsync(HttpStatusCode.OK, body);

        var r = Assert.Single(result.Rows);
        Assert.Equal("STXNGPL", r.PointCode);
        Assert.Contains(result.Log.OfLevel(LogLevel.Warning), e => e.Message.Contains("dropped=1"));
    }

    // ================================================== tolerant naming + meta fallbacks

    [Fact]
    public async Task SnakeCaseFieldNames_AreAccepted_AsTheTolerantFallback()
    {
        var rows = await ReaderHarness.ReadDatafeedAsync(Samples.Envelope(Samples.SnakeCaseRecord));

        var r = Assert.Single(rows);
        Assert.Equal("STXNGPL", r.PointCode);
        Assert.Equal(new DateOnly(2026, 8, 1), r.IssueDate);
        Assert.Equal(new DateOnly(2026, 7, 27), r.SurveyStart);
        Assert.Equal(new DateOnly(2026, 7, 29), r.SurveyEnd);
        Assert.Equal("South Texas", r.Region);
        Assert.Equal("NGPL S. TX", r.PricingPoint);
        Assert.Equal(2.325m, r.Low);
        Assert.Equal(2.365m, r.High);
        Assert.Equal(2.335m, r.Average);
        Assert.Equal(240, r.Volume);
        Assert.Equal(15, r.Deals);
    }

    [Fact]
    public async Task MissingSurveyWindowOnTheRecord_FallsBackToMeta()
    {
        var rows = await ReaderHarness.ReadDatafeedAsync(Samples.Envelope(Samples.NoSurveyWindowRecord));

        var r = Assert.Single(rows);
        Assert.Equal(Samples.FixtureSurveyStart, r.SurveyStart); // from meta.start_date
        Assert.Equal(Samples.FixtureSurveyEnd, r.SurveyEnd);     // from meta.end_date
    }

    [Fact]
    public async Task MissingSurveyWindowEverywhere_DegradesToNull_AndIsCounted()
    {
        var result = await ReaderHarness.RunDatafeedAsync(
            HttpStatusCode.OK, Samples.Envelope(Samples.NoSurveyWindowRecord, metaJson: "{}"));

        var r = Assert.Single(result.Rows);
        Assert.Null(r.SurveyStart);
        Assert.Null(r.SurveyEnd);
        Assert.Contains(result.Log.OfLevel(LogLevel.Warning), e => e.Message.Contains("missingSurveyWindow=1"));
    }

    [Fact]
    public async Task MissingIssueDateEverywhere_FallsBackToTheRequestedDate()
    {
        var body = Samples.Envelope("""
        {"STXNGPL":{"Point Code":"STXNGPL","Low":"2.325"}}
        """, metaJson: "{}");

        var rows = await ReaderHarness.ReadDatafeedAsync(body);

        // Last-resort fallback: the requested issue_date (the work unit's own date).
        Assert.Equal(Samples.FixtureIssueDate, Assert.Single(rows).IssueDate);
    }

    [Fact]
    public async Task RecordIssueDateDisagreeingWithTheRequest_IsPersistedAndCounted()
    {
        var body = Samples.Envelope("""
        {"STXNGPL":{"Point Code":"STXNGPL","Issue Date":"2026-07-01","Low":"2.325"}}
        """);

        var result = await ReaderHarness.RunDatafeedAsync(HttpStatusCode.OK, body); // requested 2026-08-01

        Assert.Equal(new DateOnly(2026, 7, 1), Assert.Single(result.Rows).IssueDate); // the record wins
        Assert.Contains(result.Log.OfLevel(LogLevel.Warning), e => e.Message.Contains("issueDateMismatch=1"));
    }

    [Fact]
    public async Task UnparseableNumbersAndDates_DegradeToNull_AndAreCounted()
    {
        var body = Samples.Envelope("""
        {"STXNGPL":{"Point Code":"STXNGPL","Issue Date":"2026-08-01","Survey Start":"07/27/2026","Survey End":"2026-07-29","Low":"n/a","Volume":"240.5"}}
        """);

        var result = await ReaderHarness.RunDatafeedAsync(HttpStatusCode.OK, body);

        var r = Assert.Single(result.Rows);
        Assert.Equal(Samples.FixtureSurveyStart, r.SurveyStart); // unparseable -> null -> meta fallback
        Assert.Null(r.Low);                                      // "n/a" is a sentinel -> NULL
        Assert.Null(r.Volume);                                   // "240.5" is NOT silently truncated
        Assert.Contains(result.Log.OfLevel(LogLevel.Warning), e => e.Message.Contains("unparseableNumeric=1"));
    }
}
