using Xunit;

namespace DataLoader.Argus.Tests;

/// <summary>
/// <see cref="ArgusSourceReader.Parse"/> against the REAL headers and rows
/// captured from the live drop (<see cref="Samples"/>).
/// </summary>
public sealed class ParseTests
{
    public static TheoryData<string> AllFeedIds()
    {
        var data = new TheoryData<string>();
        foreach (var feed in ArgusDescriptors.All) data.Add(feed.FeedId);
        return data;
    }

    /// <summary>
    /// The blanket check: every feed's descriptor must actually match the header the
    /// server publishes. This is what catches a typo like <c>TimestampID</c> where
    /// the file says <c>TimeStampID</c> — a mismatch that would otherwise only
    /// surface at run time as a rejected file.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllFeedIds))]
    public void Every_feed_parses_its_real_sample(string feedId)
    {
        var feed = ArgusDescriptors.Find(feedId)!;
        var unit = feed.IsDatedFeed ? TestHelpers.TimeSeriesUnit() : TestHelpers.Unit(feed);

        var rows = TestHelpers.Reader().Parse(Samples.For(feedId), unit);

        Assert.NotEmpty(rows);
        Assert.All(rows, row => Assert.Equal(feed.Columns.Count, row.Values.Length));
        // No CLR nulls ever reach the DataTable — absent values are DBNull.
        Assert.All(rows, row => Assert.DoesNotContain(row.Values, v => v is null));
    }

    // ------------------------------------------------------------------ header contract

    [Fact]
    public void A_missing_column_fails_the_whole_file()
    {
        // The TVP binds by position, so loading from a guess would write plausible
        // garbage. Rejecting the file keeps the previous data intact.
        const string missingCategory = "Code,DisplayName\r\nPA0016168,Coffee\r\n";

        var ex = Assert.Throws<ArgusHeaderContractException>(() =>
            TestHelpers.Reader().Parse(missingCategory, TestHelpers.Unit(ArgusDescriptors.Category)));

        Assert.Contains("Category", ex.Message);
    }

    [Fact]
    public void An_empty_file_fails_rather_than_loading_nothing_silently()
    {
        Assert.Throws<ArgusHeaderContractException>(() =>
            TestHelpers.Reader().Parse("", TestHelpers.Unit(ArgusDescriptors.PriceType)));
    }

    [Fact]
    public void Extra_unmapped_columns_are_ignored()
    {
        const string withExtra = "PriceTypeID,Description,SomethingNew\r\n1,value low,xyz\r\n";

        var rows = TestHelpers.Reader().Parse(withExtra, TestHelpers.Unit(ArgusDescriptors.PriceType));

        Assert.Single(rows);
        Assert.Equal((short)1, rows[0].Values[0]);
    }

    [Fact]
    public void Columns_are_matched_by_name_not_position()
    {
        // Same data, columns reordered. A positional reader would put the Category
        // value into DisplayName and corrupt the primary key.
        const string reordered =
            "Category,Code,DisplayName\r\n->Agriculture->Coffee,PA0016168,Coffee Uberaba\r\n";

        var feed = ArgusDescriptors.Category;
        var rows = TestHelpers.Reader().Parse(reordered, TestHelpers.Unit(feed));

        Assert.Equal("PA0016168", rows[0].Values[feed.Ordinal("Code")]);
        Assert.Equal("->Agriculture->Coffee", rows[0].Values[feed.Ordinal("Category")]);
        Assert.Equal("Coffee Uberaba", rows[0].Values[feed.Ordinal("DisplayName")]);
    }

    [Fact]
    public void Header_matching_is_case_insensitive()
    {
        const string lowered = "pricetypeid,description\r\n1,value low\r\n";

        Assert.Single(TestHelpers.Reader().Parse(lowered, TestHelpers.Unit(ArgusDescriptors.PriceType)));
    }

    // ------------------------------------------------------------------ row-level policy

    [Fact]
    public void A_row_with_the_wrong_field_count_is_dropped_not_fatal()
    {
        const string csv =
            "PriceTypeID,Description\r\n" +
            "1,value low\r\n" +
            "2\r\n" +               // short row
            "3,diff midpoint\r\n";

        var rows = TestHelpers.Reader().Parse(csv, TestHelpers.Unit(ArgusDescriptors.PriceType));

        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public void A_row_with_a_blank_required_column_is_dropped()
    {
        // Code is the PK — a blank one cannot be keyed, so merging it under a blank
        // would be worse than losing it.
        const string csv =
            "Code,DisplayName,Category\r\n" +
            "PA0016168,Coffee,->Agriculture\r\n" +
            ",Orphan,->Agriculture\r\n";

        var rows = TestHelpers.Reader().Parse(csv, TestHelpers.Unit(ArgusDescriptors.Category));

        Assert.Single(rows);
        Assert.Equal("PA0016168", rows[0].Values[0]);
    }

    [Fact]
    public void A_row_with_an_unparseable_required_column_is_dropped()
    {
        const string csv =
            "PriceTypeID,Description\r\n" +
            "1,value low\r\n" +
            "not-a-number,junk\r\n";

        var rows = TestHelpers.Reader().Parse(csv, TestHelpers.Unit(ArgusDescriptors.PriceType));

        Assert.Single(rows);
    }

    [Fact]
    public void An_unparseable_optional_column_becomes_null_and_keeps_the_row()
    {
        // ValidTo is optional; a bad value must not cost the conversion ratio.
        const string csv =
            "UnitID,BaseUnitID,ValidFrom,ValidTo,CodeID,Ratio\r\n" +
            "44,9,01-Apr-1996,rubbish,5000827,6.8232\r\n";

        var feed = ArgusDescriptors.UnitCodeConv;
        var rows = TestHelpers.Reader().Parse(csv, TestHelpers.Unit(feed));

        Assert.Single(rows);
        Assert.Equal(DBNull.Value, rows[0].Values[feed.Ordinal("ValidTo")]);
        Assert.Equal(6.8232m, rows[0].Values[feed.Ordinal("Ratio")]);
    }

    [Fact]
    public void A_blank_optional_column_becomes_null()
    {
        var feed = ArgusDescriptors.Codes;
        var rows = TestHelpers.Reader().Parse(Samples.Codes, TestHelpers.Unit(feed));

        // Row 0's Specification is empty; row 2's is a quoted single space. Both NULL.
        Assert.Equal(DBNull.Value, rows[0].Values[feed.Ordinal("Specification")]);
        Assert.Equal(DBNull.Value, rows[2].Values[feed.Ordinal("Specification")]);
    }

    // ------------------------------------------------------------------ per-feed specifics

    [Fact]
    public void Category_trims_the_quoted_padded_key()
    {
        var feed = ArgusDescriptors.Category;
        var rows = TestHelpers.Reader().Parse(Samples.Category, TestHelpers.Unit(feed));

        // The source publishes "->Ferrous scrap->Shredded->Rotterdam " WITH a
        // trailing space. Category is a PK component, so an untrimmed value would
        // create a second, near-duplicate key.
        Assert.Equal("->Ferrous scrap->Shredded->Rotterdam", rows[2].Values[feed.Ordinal("Category")]);
    }

    [Fact]
    public void ModuleDetails_maps_the_renamed_headers()
    {
        var feed = ArgusDescriptors.ModuleDetails;
        var rows = TestHelpers.Reader().Parse(Samples.ModuleDetails, TestHelpers.Unit(feed));

        Assert.Equal((short)0, rows[0].Values[feed.Ordinal("TimestampTypeID")]);   // from TimeStampID
        Assert.Equal(new DateTime(2022, 4, 13), rows[0].Values[feed.Ordinal("StartDate")]); // StartDateInModule
        Assert.Equal(new DateTime(2038, 1, 1), rows[0].Values[feed.Ordinal("EndDate")]);    // EndDateInModule
    }

    [Fact]
    public void Modules_parses_the_local_time_column()
    {
        var feed = ArgusDescriptors.Modules;
        var rows = TestHelpers.Reader().Parse(Samples.Modules, TestHelpers.Unit(feed));

        Assert.Equal(new TimeSpan(20, 0, 0), rows[0].Values[feed.Ordinal("LocalTime")]);
        Assert.Equal("15:00:00 EST", rows[0].Values[feed.Ordinal("Time")]);
    }

    [Fact]
    public void NewsCategory_keeps_the_id_that_overflows_int32()
    {
        var feed = ArgusDescriptors.NewsCategory;
        var rows = TestHelpers.Reader().Parse(Samples.NewsCategory, TestHelpers.Unit(feed));

        Assert.Equal(10000004936L, rows[1].Values[feed.Ordinal("CategoryID")]);
        Assert.Equal(10000004756L, rows[1].Values[feed.Ordinal("ParentID")]);
        Assert.Equal("N", rows[1].Values[feed.Ordinal("Active")]);
    }

    [Fact]
    public void UnitCodeConv_keeps_the_seventeen_place_ratio()
    {
        var feed = ArgusDescriptors.UnitCodeConv;
        var rows = TestHelpers.Reader().Parse(Samples.UnitCodeConv, TestHelpers.Unit(feed));

        Assert.Equal(0.00334112930170398m, rows[1].Values[feed.Ordinal("Ratio")]);
        Assert.Equal(DBNull.Value, rows[0].Values[feed.Ordinal("ValidTo")]);
    }

    [Fact]
    public void QuoteHolidayRegion_allows_a_blank_second_region()
    {
        var feed = ArgusDescriptors.QuoteHolidayRegion;
        var rows = TestHelpers.Reader().Parse(Samples.QuoteHolidayRegion, TestHelpers.Unit(feed));

        Assert.Equal(DBNull.Value, rows[1].Values[feed.Ordinal("HolidayRegionID2")]);
        Assert.Equal((short)10, rows[1].Values[feed.Ordinal("HolidayRegionID1")]);
    }

    [Fact]
    public void Quotes_keeps_rows_that_share_the_short_key_but_differ_by_start_date()
    {
        // The reason QuoteLookup is a full replace rather than a keyed merge.
        var rows = TestHelpers.Reader().Parse(Samples.Quotes, TestHelpers.Unit(ArgusDescriptors.Quotes));

        Assert.Equal(3, rows.Count);
    }

    // ------------------------------------------------------------------ derived columns

    [Fact]
    public void TimeSeries_fills_module_record_status_date_and_source_path_from_the_file()
    {
        var feed = ArgusDescriptors.TimeSeries;
        var unit = TestHelpers.TimeSeriesUnit("20260827dhc.csv", "DHC");

        var rows = TestHelpers.Reader().Parse(Samples.TimeSeries, unit);

        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.Equal("DHC", r.Values[feed.Ordinal("Module")]));

        // RecordStatusDate is not in the CSV — it is the file-name date.
        Assert.All(rows, r => Assert.Equal(new DateTime(2026, 8, 27), r.Values[feed.Ordinal("RecordStatusDate")]));

        // SourceFileDate carries the same value but exists only in the TVP, as the
        // merge ordering guard.
        Assert.All(rows, r => Assert.Equal(new DateTime(2026, 8, 27), r.Values[feed.Ordinal("SourceFileDate")]));

        Assert.All(rows, r => Assert.Equal("/DCRDEUS/20260827dhc.csv", r.Values[feed.Ordinal("SourcePath")]));
    }

    [Fact]
    public void TimeSeries_maps_the_spaced_headers()
    {
        var feed = ArgusDescriptors.TimeSeries;
        var rows = TestHelpers.Reader().Parse(Samples.TimeSeries, TestHelpers.TimeSeriesUnit());

        Assert.Equal((short)2, rows[0].Values[feed.Ordinal("TimestampTypeID")]);  // "TS Type"
        Assert.Equal((short)6, rows[0].Values[feed.Ordinal("PriceTypeID")]);      // "PT Code"
        Assert.Equal((short)0, rows[0].Values[feed.Ordinal("ContFwd")]);          // "Cont Fwd"
        Assert.Equal((short)0, rows[0].Values[feed.Ordinal("FwdPeriod")]);        // "Fwd Period"
        Assert.Equal((short)10, rows[0].Values[feed.Ordinal("DiffBaseRoll")]);    // "Diff Base Roll"
        Assert.Equal("N", rows[0].Values[feed.Ordinal("RecordStatus")]);          // "Record Status"
        Assert.Equal(6.02m, rows[0].Values[feed.Ordinal("Value")]);
        Assert.Equal(new DateTime(2026, 8, 26), rows[0].Values[feed.Ordinal("Date")]);
    }

    [Fact]
    public void TimeSeries_keeps_a_row_with_a_negative_value()
    {
        var feed = ArgusDescriptors.TimeSeries;
        var rows = TestHelpers.Reader().Parse(Samples.TimeSeries, TestHelpers.TimeSeriesUnit());

        Assert.Equal(-5.14m, rows[1].Values[feed.Ordinal("Value")]);
    }

    /// <summary>
    /// The correction path. Only ever seen in 7667.csv, which the loader excludes,
    /// so this test is the only exercise it gets — see docs/apis/Argus.md §5.4.
    /// </summary>
    [Fact]
    public void TimeSeries_parses_a_corrected_record_status()
    {
        var feed = ArgusDescriptors.TimeSeries;
        var rows = TestHelpers.Reader().Parse(Samples.TimeSeriesCorrected, TestHelpers.TimeSeriesUnit());

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal("C", r.Values[feed.Ordinal("RecordStatus")]));
    }

    /// <summary>
    /// A dated-feed unit with no parsed date is a loader bug, not bad data — the
    /// guard column would otherwise silently become NULL and disable the merge's
    /// ordering protection.
    /// </summary>
    [Fact]
    public void TimeSeries_refuses_a_unit_with_no_source_file_date()
    {
        var unit = TestHelpers.Unit(ArgusDescriptors.TimeSeries, "20260827dhc.csv", sourceFileDate: null, module: "DHC");

        var ex = Assert.Throws<ArgusHeaderContractException>(() =>
            TestHelpers.Reader().Parse(Samples.TimeSeries, unit));

        Assert.Contains("SourceFileDate", ex.Message);
    }

    [Fact]
    public void TimeSeries_refuses_a_unit_with_no_module()
    {
        var unit = TestHelpers.Unit(
            ArgusDescriptors.TimeSeries, "20260827dhc.csv",
            sourceFileDate: new DateOnly(2026, 8, 27), module: null);

        var ex = Assert.Throws<ArgusHeaderContractException>(() =>
            TestHelpers.Reader().Parse(Samples.TimeSeries, unit));

        Assert.Contains("Module", ex.Message);
    }
}
