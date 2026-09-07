using Xunit;

namespace DataLoader.EOX.Tests;

/// <summary>
/// <see cref="EoxSourceReader.Parse"/> against the verbatim live samples: the
/// column mapping, the header-drift tolerance, and the drop/degrade policy.
/// </summary>
public sealed class ParseTests
{
    private static IReadOnlyList<EoxRow> Parse(EoxFeedDescriptor feed, string csv, DateOnly? curveDate = null) =>
        TestHelpers.Reader().Parse(csv, TestHelpers.Unit(feed, curveDate ?? CurveDateOf(csv)));

    private static DateOnly CurveDateOf(string csv) =>
        csv == Samples.CrudeOil2014 || csv == Samples.NaturalGas2014 || csv == Samples.Ngl2014
            ? new DateOnly(2014, 5, 19)
            : new DateOnly(2026, 9, 4);

    // ---------------------------------------------------------------- CrudeOil

    [Fact]
    public void CrudeOil_maps_every_column_by_name()
    {
        var feed = EoxDescriptors.CrudeOil;
        var rows = Parse(feed, Samples.CrudeOil2026);

        Assert.Equal(4, rows.Count);
        var first = rows[0].Values;

        Assert.Equal(new DateTime(2026, 9, 4), first[feed.Ordinal("CurveDate")]);
        Assert.Equal("A", first[feed.Ordinal("LocationCode")]);
        Assert.Equal("MB01", first[feed.Ordinal("TimeKey")]);
        Assert.Equal("1", first[feed.Ordinal("Line")]);
        Assert.Equal("20260904_A_MB01", first[feed.Ordinal("Code")]);
        Assert.Equal("Month", first[feed.Ordinal("ContractTerm")]);
        Assert.Equal("Sep_2026", first[feed.Ordinal("ContractName")]);
        Assert.Equal(new DateTime(2026, 9, 1), first[feed.Ordinal("ContractBegin")]);
        Assert.Equal(new DateTime(2026, 9, 30), first[feed.Ordinal("ContractEnd")]);
        Assert.Equal("Financial_WTI", first[feed.Ordinal("Location")]);
        Assert.Equal(93.639, (double)first[feed.Ordinal("Mid")], 6);
        Assert.Equal(93.489, (double)first[feed.Ordinal("Bid")], 6);
        Assert.Equal(93.789, (double)first[feed.Ordinal("Ask")], 6);
        Assert.Equal("EOD_CSV_C_20260904_1430.csv", first[feed.Ordinal("FileName")]);
    }

    [Fact]
    public void CrudeOil_keeps_negative_prices()
    {
        var feed = EoxDescriptors.CrudeOil;
        var wcs = Parse(feed, Samples.CrudeOil2026)
            .Single(r => (string)r.Values[feed.Ordinal("Location")] == "Canada_WCS");

        Assert.Equal(-15.804, (double)wcs.Values[feed.Ordinal("Mid")], 6);
        Assert.Equal(-15.954, (double)wcs.Values[feed.Ordinal("Bid")], 6);
    }

    /// <summary>The 2014 header rename: <c>Number</c> instead of <c>Line</c>.</summary>
    [Fact]
    public void CrudeOil_2014_header_still_loads_via_the_Number_alias()
    {
        var feed = EoxDescriptors.CrudeOil;
        var rows = Parse(feed, Samples.CrudeOil2014);

        Assert.Equal(2, rows.Count);
        Assert.Equal("1", rows[0].Values[feed.Ordinal("Line")]);
        Assert.Equal("20140519_A_M01", rows[0].Values[feed.Ordinal("Code")]);
    }

    // ---------------------------------------------------------------- NaturalGas

    [Fact]
    public void NaturalGas_maps_every_column_including_FP_and_the_two_digit_dates()
    {
        var feed = EoxDescriptors.NaturalGas;
        var rows = Parse(feed, Samples.NaturalGas2026);

        Assert.Equal(3, rows.Count);
        var first = rows[0].Values;

        Assert.Equal(new DateTime(2026, 9, 4), first[feed.Ordinal("CurveDate")]);
        Assert.Equal("AA", first[feed.Ordinal("MarketCode")]);
        Assert.Equal("MB01", first[feed.Ordinal("TimeKey")]);
        Assert.Equal("20260904_AAMB01", first[feed.Ordinal("Code")]);
        Assert.Equal("NYMEX Settlement", first[feed.Ordinal("Region")]);
        Assert.Equal("NYMEX", first[feed.Ordinal("Market")]);
        Assert.Equal(new DateTime(2026, 9, 1), first[feed.Ordinal("ContractBegin")]);
        Assert.Equal(new DateTime(2026, 9, 30), first[feed.Ordinal("ContractEnd")]);
        Assert.Equal(2.6691, (double)first[feed.Ordinal("FP")], 6);
        Assert.Equal("EOD_CSV_NG_20260904_1430.csv", first[feed.Ordinal("FileName")]);
    }

    /// <summary>
    /// A NaturalGas file older than roughly 2016 has 14 fields and no <c>FP</c>.
    /// That must LOAD with FP NULL, not fail — the alternative loses 5 years of
    /// history on any backfill.
    /// </summary>
    [Fact]
    public void NaturalGas_without_an_FP_column_loads_with_FP_null()
    {
        var feed = EoxDescriptors.NaturalGas;
        var rows = Parse(feed, Samples.NaturalGas2014);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(DBNull.Value, r.Values[feed.Ordinal("FP")]));

        // Everything else still landed.
        Assert.Equal(new DateTime(2014, 5, 19), rows[0].Values[feed.Ordinal("CurveDate")]);
        Assert.Equal("AA", rows[0].Values[feed.Ordinal("MarketCode")]);
        Assert.Equal(4.47, (double)rows[0].Values[feed.Ordinal("Mid")], 6);
    }

    // ---------------------------------------------------------------- NGL

    [Fact]
    public void Ngl_maps_every_column_by_name()
    {
        var feed = EoxDescriptors.Ngl;
        var rows = Parse(feed, Samples.Ngl2026);

        Assert.Equal(2, rows.Count);
        Assert.Equal("E", rows[0].Values[feed.Ordinal("LocationCode")]);
        Assert.Equal("20260904_E_MB01", rows[0].Values[feed.Ordinal("Code")]);
        Assert.Equal("Mont Belvieu_Propane_LST", rows[0].Values[feed.Ordinal("Location")]);
        Assert.Equal(81.625, (double)rows[0].Values[feed.Ordinal("Mid")], 6);
    }

    /// <summary>The 2014 NGL file carries BOTH renames at once.</summary>
    [Fact]
    public void Ngl_2014_header_resolves_both_Number_and_Code()
    {
        var feed = EoxDescriptors.Ngl;
        var rows = Parse(feed, Samples.Ngl2014);

        Assert.Equal(2, rows.Count);
        Assert.Equal("1", rows[0].Values[feed.Ordinal("Line")]);
        Assert.Equal("20140519_E_M01", rows[0].Values[feed.Ordinal("Code")]);
    }

    // ---------------------------------------------------------------- policy

    [Fact]
    public void Every_row_has_exactly_the_descriptor_column_count_and_never_a_clr_null()
    {
        foreach (var (feed, csv) in new[]
                 {
                     (EoxDescriptors.CrudeOil, Samples.CrudeOil2026),
                     (EoxDescriptors.NaturalGas, Samples.NaturalGas2026),
                     (EoxDescriptors.NaturalGas, Samples.NaturalGas2014),
                     (EoxDescriptors.Ngl, Samples.Ngl2026)
                 })
        {
            foreach (var row in Parse(feed, csv))
            {
                Assert.Equal(feed.Columns.Count, row.Values.Length);
                Assert.All(row.Values, Assert.NotNull);
            }
        }
    }

    [Fact]
    public void A_missing_required_header_fails_the_whole_file()
    {
        // Time_Key is a primary-key component. Without it there is nothing to key on,
        // and the TVP binds by position, so guessing would write garbage.
        var csv = Samples.CrudeOil2026.Replace("Time_Key", "Tick_Key");

        var ex = Assert.Throws<EoxHeaderContractException>(
            () => Parse(EoxDescriptors.CrudeOil, csv));

        Assert.Contains("TimeKey", ex.Message);
        Assert.Contains("Time_Key", ex.Message);
    }

    [Fact]
    public void An_empty_file_fails_rather_than_loading_zero_rows_quietly()
    {
        Assert.Throws<EoxHeaderContractException>(() => Parse(EoxDescriptors.CrudeOil, ""));
    }

    [Fact]
    public void A_row_with_the_wrong_field_count_is_dropped_and_the_rest_survive()
    {
        var csv = Samples.CrudeOil2026 + "999,short,row\r\n";

        Assert.Equal(4, Parse(EoxDescriptors.CrudeOil, csv).Count);
    }

    [Fact]
    public void A_row_with_a_blank_primary_key_component_is_dropped()
    {
        // Blank Locat._Code (field 4) — cannot be keyed, so it must not be merged.
        var csv = Samples.CrudeOil2026 +
                  "999,20260904_X_M99,2026-09-04,,Month,Sep_2026,2026-09-01,2026-09-30,M99,Nowhere,1,1,1\r\n";

        Assert.Equal(4, Parse(EoxDescriptors.CrudeOil, csv).Count);
    }

    [Fact]
    public void A_row_with_an_unparseable_price_is_KEPT_with_that_cell_null()
    {
        var feed = EoxDescriptors.CrudeOil;
        var csv = Samples.CrudeOil2026 +
                  "999,20260904_X_M99,2026-09-04,X,Month,Sep_2026,2026-09-01,2026-09-30,M99,Nowhere,n/a,1,1\r\n";

        var rows = Parse(feed, csv);
        var added = rows.Single(r => (string)r.Values[feed.Ordinal("TimeKey")] == "M99");

        Assert.Equal(5, rows.Count);
        Assert.Equal(DBNull.Value, added.Values[feed.Ordinal("Mid")]);
        Assert.Equal(1d, (double)added.Values[feed.Ordinal("Bid")], 6);
    }

    /// <summary>
    /// FileName is derived from the work unit, never from the file's contents, and
    /// it is the merge ordering guard — so it must be present on every row of every
    /// feed regardless of what the CSV contains.
    /// </summary>
    [Fact]
    public void FileName_is_stamped_on_every_row_from_the_work_unit()
    {
        var feed = EoxDescriptors.NaturalGas;
        var unit = TestHelpers.Unit(feed, new DateOnly(2026, 9, 4));
        var rows = TestHelpers.Reader().Parse(Samples.NaturalGas2026, unit);

        Assert.All(rows, r => Assert.Equal("EOD_CSV_NG_20260904_1430.csv", r.Values[feed.Ordinal("FileName")]));
    }

    /// <summary>
    /// A Curve_Date that disagrees with the file name is KEPT — the file's own value
    /// is authoritative — because dropping it would lose data over an assumption.
    /// The reader warns and <c>arm.usp_ValidateLoad</c> reports it from the table.
    /// </summary>
    [Fact]
    public void A_row_whose_curve_date_disagrees_with_the_file_name_is_still_loaded()
    {
        var feed = EoxDescriptors.CrudeOil;
        var unit = TestHelpers.Unit(feed, new DateOnly(2026, 9, 3));   // file says the 4th

        var rows = TestHelpers.Reader().Parse(Samples.CrudeOil2026, unit);

        Assert.Equal(4, rows.Count);
        Assert.All(rows, r => Assert.Equal(new DateTime(2026, 9, 4), r.Values[feed.Ordinal("CurveDate")]));
    }
}
