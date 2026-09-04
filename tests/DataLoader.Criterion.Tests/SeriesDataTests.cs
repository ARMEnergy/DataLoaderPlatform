using Xunit;

namespace DataLoader.Criterion.Tests;

/// <summary>
/// The JSON unpivot — the one place in this loader where a value is derived rather
/// than copied, and therefore the one place a silent mistake could change what the
/// data means.
///
/// <para>
/// The fixtures in <see cref="Samples"/> are REAL payloads captured from the live
/// source on 2026-09-03, not invented ones. That is what makes these tests evidence
/// that the parser matches production rather than merely evidence that it is
/// self-consistent.
/// </para>
/// </summary>
public sealed class SeriesDataTests
{
    // ------------------------------------------------------------------ shapes

    [Fact]
    public void Daily_shape_yields_one_row_per_element()
    {
        var result = CriterionSeriesData.Parse(Samples.DailyArray);

        Assert.Equal(5, result.Elements);
        Assert.Equal(5, result.Rows.Count);
        Assert.Equal(0, result.Collapsed);
        Assert.Equal(0, result.Unparseable);
    }

    [Fact]
    public void Daily_shape_reads_dates_and_values()
    {
        var result = CriterionSeriesData.Parse(Samples.DailyArray);

        Assert.Equal(new DateTime(2021, 10, 5), result.Rows[0].Date);
        Assert.Equal(21453.5943776865, result.Rows[0].Value!.Value, 9);

        Assert.Equal(new DateTime(2021, 10, 9), result.Rows[4].Date);
        Assert.Equal(21125.4122354563, result.Rows[4].Value!.Value, 9);
    }

    /// <summary>
    /// The requester's own sample quoted its values; live data sends bare numbers.
    /// Both must work, or a source that flips back would null out every observation.
    /// </summary>
    [Fact]
    public void Value_is_read_whether_quoted_or_bare()
    {
        var quoted = CriterionSeriesData.Parse("""[{"date":"10/5/2021","value":"21453.59"}]""");
        var bare = CriterionSeriesData.Parse("""[{"date":"10/5/2021","value":21453.59}]""");

        Assert.Equal(21453.59, quoted.Rows.Single().Value!.Value, 6);
        Assert.Equal(21453.59, bare.Rows.Single().Value!.Value, 6);
    }

    /// <summary>
    /// The intraday shape carries two extra keys. They must be SKIPPED, not treated as
    /// a parse failure — half the live rows look like this.
    /// </summary>
    [Fact]
    public void Intraday_shape_ignores_timestamp_and_time_zone()
    {
        var result = CriterionSeriesData.Parse(Samples.IntradayArray);

        Assert.Equal(0, result.Unparseable);
        Assert.NotEmpty(result.Rows);
    }

    // --------------------------------------------------- ⚠ the intraday collapse

    /// <summary>
    /// ⚠ THE ACCEPTED DATA LOSS. Six five-minute observations across two dates collapse
    /// to two rows, because PK (FinancialJsonId, Date) admits one row per calendar day.
    /// The requester chose this on 2026-09-03 over widening the key.
    /// </summary>
    [Fact]
    public void Intraday_observations_collapse_to_one_row_per_date()
    {
        var result = CriterionSeriesData.Parse(Samples.IntradayArray);

        Assert.Equal(6, result.Elements);
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(4, result.Collapsed);

        Assert.Equal(new DateTime(2026, 7, 5), result.Rows[0].Date);
        Assert.Equal(new DateTime(2026, 7, 6), result.Rows[1].Date);
    }

    /// <summary>
    /// ⚠ LAST WINS, and "last" means last in SOURCE ARRAY ORDER — for a chronologically
    /// ordered intraday array that is the day's closing observation, a meaningful value
    /// rather than an arbitrary one.
    ///
    /// <para>
    /// This is the single assertion that pins the collapse's semantics. If it ever
    /// starts failing, the loaded value for every intraday series has changed meaning.
    /// </para>
    /// </summary>
    [Fact]
    public void The_last_observation_of_a_date_wins()
    {
        var result = CriterionSeriesData.Parse(Samples.IntradayArray);

        // 2026-07-05 carries 4966.60, 4967.90 then 4968.30 — the third must survive.
        Assert.Equal(4968.30, result.Rows[0].Value!.Value, 6);

        // 2026-07-06 carries 5001.10, 5002.20 then 5003.30.
        Assert.Equal(5003.30, result.Rows[1].Value!.Value, 6);
    }

    [Fact]
    public void Collapse_is_deterministic_across_repeated_parses()
    {
        var first = CriterionSeriesData.Parse(Samples.IntradayArray);
        var second = CriterionSeriesData.Parse(Samples.IntradayArray);

        Assert.Equal(
            first.Rows.Select(r => (r.Date, r.Value)),
            second.Rows.Select(r => (r.Date, r.Value)));
    }

    [Fact]
    public void Rows_keep_source_date_order()
    {
        var result = CriterionSeriesData.Parse(Samples.IntradayArray);

        Assert.Equal(
            result.Rows.Select(r => r.Date).OrderBy(d => d),
            result.Rows.Select(r => r.Date));
    }

    // ------------------------------------------------------------ date formats

    /// <summary>
    /// All three formats are live in the SAME table as of 2026-09-03 — the shape depends
    /// on which upstream loader wrote the series, so the parser must handle any of them
    /// in any row.
    /// </summary>
    [Theory]
    [InlineData("09/02/2026", 2026, 9, 2)]
    [InlineData("10/5/2021", 2021, 10, 5)]
    [InlineData("2023-08-24", 2023, 8, 24)]
    [InlineData("2016-08-21T00:00:00.000Z", 2016, 8, 21)]
    public void Every_live_date_format_parses(string text, int year, int month, int day)
    {
        Assert.True(CriterionTime.TryParseJsonDate(text, out var parsed));
        Assert.Equal(new DateTime(year, month, day), parsed);
    }

    /// <summary>
    /// ⚠ <c>"10/5/2021"</c> is MONTH-first: 5 October, not 10 May. Criterion is a US
    /// vendor and every sampled series is month-first. This is pinned because an
    /// ambient culture of en-GB on the host would otherwise silently reinterpret every
    /// date in the table — the parser must use the invariant culture, never the
    /// ambient one.
    /// </summary>
    [Fact]
    public void Ambiguous_slash_dates_are_month_first_regardless_of_host_culture()
    {
        var original = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("en-GB");

            Assert.True(CriterionTime.TryParseJsonDate("10/5/2021", out var parsed));
            Assert.Equal(new DateTime(2021, 10, 5), parsed);
            Assert.Equal(10, parsed.Month);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }

    /// <summary>An ISO timestamp's time component is discarded — the target column is DATE.</summary>
    [Fact]
    public void Time_component_is_discarded()
    {
        Assert.True(CriterionTime.TryParseJsonDate("2016-08-21T13:45:59.123Z", out var parsed));
        Assert.Equal(new DateTime(2016, 8, 21), parsed);
        Assert.Equal(TimeSpan.Zero, parsed.TimeOfDay);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a date")]
    public void Unreadable_dates_are_rejected(string? text)
    {
        Assert.False(CriterionTime.TryParseJsonDate(text, out _));
    }

    // -------------------------------------------------------------- resilience

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("[]")]
    public void Empty_payloads_yield_no_rows(string? json)
    {
        var result = CriterionSeriesData.Parse(json);

        Assert.Empty(result.Rows);
        Assert.Equal(0, result.Collapsed);
    }

    /// <summary>
    /// A series that has not published yet legitimately carries an empty array. That is
    /// a normal state, not a failure, and must not be counted as unparseable.
    /// </summary>
    [Fact]
    public void An_empty_array_is_not_an_error()
    {
        var result = CriterionSeriesData.Parse("[]");

        Assert.Equal(0, result.Elements);
        Assert.Equal(0, result.Unparseable);
    }

    /// <summary>A non-array payload yields nothing rather than throwing.</summary>
    [Theory]
    [InlineData("{\"date\":\"10/5/2021\",\"value\":1}")]
    [InlineData("\"a string\"")]
    [InlineData("42")]
    public void A_non_array_payload_yields_no_rows(string json)
    {
        Assert.Empty(CriterionSeriesData.Parse(json).Rows);
    }

    /// <summary>
    /// Truncated JSON keeps what parsed rather than losing the whole series. A partial
    /// series beats none, and the caller sees Elements &gt; Rows.Count.
    /// </summary>
    [Fact]
    public void Truncated_json_keeps_what_parsed()
    {
        var result = CriterionSeriesData.Parse(
            """[{"date":"10/5/2021","value":1.5},{"date":"10/6/2021","value":2.5},{"date":"10/7""");

        Assert.Equal(2, result.Rows.Count);
        Assert.True(result.Unparseable > 0);
    }

    /// <summary>An element with no readable date is dropped and counted, not merged under a wrong date.</summary>
    [Fact]
    public void Elements_without_a_readable_date_are_dropped_and_counted()
    {
        var result = CriterionSeriesData.Parse(
            """[{"date":"10/5/2021","value":1},{"value":2},{"date":"oops","value":3},{"date":"10/6/2021","value":4}]""");

        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(2, result.Unparseable);
        Assert.Equal(4, result.Elements);
    }

    /// <summary>A null or unreadable value is stored as NULL — the DATE still lands.</summary>
    [Fact]
    public void A_null_value_becomes_a_null_row_value()
    {
        var result = CriterionSeriesData.Parse(
            """[{"date":"10/5/2021","value":null},{"date":"10/6/2021","value":"abc"}]""");

        Assert.Equal(2, result.Rows.Count);
        Assert.Null(result.Rows[0].Value);
        Assert.Null(result.Rows[1].Value);
    }

    /// <summary>Property names are matched case-insensitively.</summary>
    [Fact]
    public void Property_names_are_case_insensitive()
    {
        var result = CriterionSeriesData.Parse("""[{"Date":"10/5/2021","Value":7.5}]""");

        Assert.Equal(new DateTime(2021, 10, 5), result.Rows.Single().Date);
        Assert.Equal(7.5, result.Rows.Single().Value!.Value, 6);
    }

    /// <summary>An unrecognised extra key is skipped, so the source can add fields safely.</summary>
    [Fact]
    public void Unknown_keys_are_skipped()
    {
        var result = CriterionSeriesData.Parse(
            """[{"date":"10/5/2021","value":1.5,"quality":"good","nested":{"a":[1,2,3]}}]""");

        Assert.Equal(1.5, result.Rows.Single().Value!.Value, 6);
        Assert.Equal(0, result.Unparseable);
    }

    /// <summary>A negative value is a legitimate observation (basis spreads go negative).</summary>
    [Fact]
    public void Negative_values_survive()
    {
        var result = CriterionSeriesData.Parse("""[{"date":"10/5/2021","value":-3.625}]""");

        Assert.Equal(-3.625, result.Rows.Single().Value!.Value, 6);
    }
}
