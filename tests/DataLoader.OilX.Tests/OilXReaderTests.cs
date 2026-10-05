using Xunit;

namespace DataLoader.OilX.Tests;

/// <summary>
/// End-to-end mapping: a real CSV document in, positional target rows out.
///
/// <para>
/// These are the tests that prove the mapping matches PRODUCTION rather than being
/// merely self-consistent — every document here is content captured verbatim from the
/// files the vendor published for 2026-09-30.
/// </para>
/// </summary>
public sealed class OilXReaderTests
{
    // ------------------------------------------------------------ CargoTracking

    [Fact]
    public async Task CargoTracking_maps_every_column_from_the_real_file()
    {
        var feed = TestHelpers.Feed(OilXDescriptors.CargoTracking);
        var (rows, _, result) = await TestHelpers.ReadAsync(feed, Samples.CargoTrackingCsv);

        Assert.Equal(2, result.RowsRead);
        Assert.Equal(0, result.RowsDropped);
        Assert.Equal(0, result.CellsFailed);

        var row = rows[0];

        Assert.Equal(new DateTime(2026, 9, 30), row.Value(feed, "RunDate"));
        Assert.Equal("9405540", row.Value(feed, "IMO"));
        Assert.Equal("Zevulun", row.Value(feed, "VesselName"));
        Assert.Equal("MR2", row.Value(feed, "VesselClass"));
        Assert.Equal(new DateTime(2015, 12, 11, 1, 10, 30, 714), row.Value(feed, "LoadDate"));
        Assert.Equal("Central Mediterranean", row.Value(feed, "LoadArea"));
        Assert.Equal("Albania", row.Value(feed, "LoadCountry"));
        Assert.Equal("Vlore", row.Value(feed, "LoadPort"));
        Assert.Equal("Albania Crude Oil", row.Value(feed, "GradeName"));
        Assert.Equal("Bahamas", row.Value(feed, "DestinationCountry"));
        Assert.Equal(new DateTime(2026, 5, 20), row.Value(feed, "LastUpdateDate"));
        Assert.Equal("1c5f5b6538f59493841f0af4105c4ecb1a53be11bcd55ddacc9f575074b5fc5a",
            row.Value(feed, "FlowID"));

        // ⚠ The CSV publishes KT before KBBL; the table is the other way round. If the
        // mapping had gone by POSITION these two would be swapped.
        Assert.Equal(195d, row.Value(feed, "LoadQuantity_KBBL"));
        Assert.Equal(30.86d, row.Value(feed, "LoadQuantity_KT"));

        Assert.Equal(11.0d, row.Value(feed, "APIGravity"));
        Assert.Equal("5.40", row.Value(feed, "SulphurContent"));   // VARCHAR(MAX) per the DDL
    }

    /// <summary>
    /// The quoted-comma row again, but all the way through the mapping: a naive split
    /// would put the country name's tail into DischargeSubcountryArea and shift
    /// everything after it.
    /// </summary>
    [Fact]
    public async Task CargoTracking_quoted_commas_do_not_shift_later_columns()
    {
        var feed = TestHelpers.Feed(OilXDescriptors.CargoTracking);
        var (rows, _, _) = await TestHelpers.ReadAsync(feed, Samples.CargoTrackingCsv);

        var row = rows[1];

        Assert.Equal("Bonaire, Sint Eustatius and Saba", row.Value(feed, "DischargeCountry"));
        Assert.Equal("Bonaire, Sint Eustatius and Saba", row.Value(feed, "DestinationCountry"));
        Assert.Equal("St Eustatius", row.Value(feed, "DischargePort"));
        Assert.Equal("Lukoil", row.Value(feed, "Charterer"));
        Assert.Equal("Sonatrach", row.Value(feed, "Supplier"));
        Assert.Equal("ab8122f46016ce3c1dfb857aade40e0f7efcedd60e2d597e85023e0a36ee96f8",
            row.Value(feed, "FlowID"));
    }

    /// <summary>The 20 columns the table does not use must simply not appear.</summary>
    [Fact]
    public async Task CargoTracking_ignores_the_columns_the_table_does_not_declare()
    {
        var feed = TestHelpers.Feed(OilXDescriptors.CargoTracking);

        foreach (var absent in new[]
                 {
                     "VoyageID", "RunDateTime", "IsExporter", "IsImporter", "CargoType",
                     "SanctionEntities", "LoadGeoAsset", "GradeSource"
                 })
            Assert.DoesNotContain(feed.Columns, c => c.Name == absent);

        var (rows, _, _) = await TestHelpers.ReadAsync(feed, Samples.CargoTrackingCsv);
        Assert.Equal(feed.Columns.Count, rows[0].Values.Length);
    }

    // ----------------------------------------------------------- the other feeds

    [Fact]
    public async Task FloatingStorage_maps_the_real_file()
    {
        var feed = TestHelpers.Feed(OilXDescriptors.FloatingStorageVessels);
        var (rows, _, result) = await TestHelpers.ReadAsync(feed, Samples.FloatingStorageCsv);

        Assert.Equal(2, result.RowsRead);

        var row = rows[0];
        Assert.Equal("9387578", row.Value(feed, "IMO"));
        Assert.Equal("Nushen", row.Value(feed, "VesselName"));
        Assert.Equal("VLCC", row.Value(feed, "VesselClass"));
        Assert.Equal(new DateTime(2015, 1, 1), row.Value(feed, "ReferenceDate"));
        Assert.Equal(new DateTime(2015, 1, 28, 3, 56, 20), row.Value(feed, "StartDate"));
        Assert.Equal(1945m, row.Value(feed, "QuantityKiloBarrels"));
        Assert.Equal("Singapore / Malaysia", row.Value(feed, "Area"));
    }

    /// <summary>
    /// The same origin/destination/grade under Exports and Imports must become TWO rows,
    /// which is what makes GroupByDateIndicator a key component.
    /// </summary>
    [Fact]
    public async Task Flow_maps_the_lowercase_b_header_and_keeps_exports_and_imports_apart()
    {
        var feed = TestHelpers.Feed(OilXDescriptors.Flows);
        var (rows, _, _) = await TestHelpers.ReadAsync(feed, Samples.FlowCsv);

        Assert.Equal("Exports", rows[0].Value(feed, "GroupByDateIndicator"));
        Assert.Equal("Imports", rows[1].Value(feed, "GroupByDateIndicator"));

        Assert.NotEqual(rows[0].Value(feed, "RowId"), rows[1].Value(feed, "RowId"));

        Assert.Equal("Albania", rows[0].Value(feed, "OriginCountryName"));
        Assert.Equal(6.29m, rows[0].Value(feed, "QuantityKBD"));
        Assert.Equal(195m, rows[0].Value(feed, "QuantityKBBL"));
        Assert.Equal("Heavy & Sour", rows[0].Value(feed, "GradeCategory"));

        // ⚠ The subcountry columns are blank here -- legitimately so, and still part of
        // the key. A blank CSV cell converts to NULL (not ""), so it hashes as the null
        // token: a stable, intentional identity rather than a blank key. That is why key
        // columns must never be Required.
        Assert.Equal(DBNull.Value, rows[0].Value(feed, "OriginSubCountry"));
        Assert.Equal(DBNull.Value, rows[0].Value(feed, "DestinationSubCountry"));
    }

    [Fact]
    public async Task GlobalBalance_maps_the_real_file()
    {
        var feed = TestHelpers.Feed(OilXDescriptors.GlobalBalance);
        var (rows, _, _) = await TestHelpers.ReadAsync(feed, Samples.GlobalBalanceCsv);

        Assert.Equal("OPEC", rows[0].Value(feed, "GroupName"));
        Assert.Equal(new DateTime(2010, 1, 1), rows[0].Value(feed, "ReferenceDate"));
        Assert.Equal("Crude Production", rows[0].Value(feed, "FlowBreakdown"));
        Assert.Equal("KBD", rows[0].Value(feed, "UnitMeasure"));
        Assert.Equal(24534m, rows[0].Value(feed, "ObservedValue"));
    }

    [Fact]
    public async Task RegionalBalance_and_SupplyDemand_map_their_real_files()
    {
        var regional = TestHelpers.Feed(OilXDescriptors.RegionalBalance);
        var (regionalRows, _, _) = await TestHelpers.ReadAsync(regional, Samples.RegionalBalanceCsv);

        Assert.Equal("Africa", regionalRows[0].Value(regional, "GroupName"));
        Assert.Equal("OilX", regionalRows[0].Value(regional, "GeneralizedSource"));
        Assert.Equal(130664m, regionalRows[0].Value(regional, "ObservedValue"));

        var supply = TestHelpers.Feed(OilXDescriptors.SupplyDemand);
        var (supplyRows, _, _) = await TestHelpers.ReadAsync(supply, Samples.SupplyDemandCsv);

        Assert.Equal("AF", supplyRows[0].Value(supply, "CountryISOCode"));
        Assert.Equal("Afghanistan", supplyRows[0].Value(supply, "CountryName"));
        Assert.Equal(0m, supplyRows[0].Value(supply, "ObservedValue"));
    }

    /// <summary>
    /// Two rows differing only in UnitMeasure must get different RowIds — which is why
    /// UnitMeasure is in the key.
    /// </summary>
    [Fact]
    public async Task OilFieldProduction_separates_rows_by_unit()
    {
        var feed = TestHelpers.Feed(OilXDescriptors.OilFieldsProduction);
        var (rows, _, _) = await TestHelpers.ReadAsync(feed, Samples.OilFieldProductionCsv);

        Assert.Equal("Abana", rows[0].Value(feed, "OilFieldName"));
        Assert.Equal("FPSO Massongo", rows[0].Value(feed, "PortName"));
        Assert.Equal("Cameroon", rows[0].Value(feed, "CountryName"));
        Assert.Equal("Events", rows[0].Value(feed, "UnitMeasure"));
        Assert.Equal("MWatts", rows[1].Value(feed, "UnitMeasure"));

        Assert.NotEqual(rows[0].Value(feed, "RowId"), rows[1].Value(feed, "RowId"));
    }

    [Fact]
    public async Task Terminal_maps_the_real_file_and_ignores_IsForwardFilled()
    {
        var feed = TestHelpers.Feed(OilXDescriptors.Terminals);
        var (rows, _, _) = await TestHelpers.ReadAsync(feed, Samples.TerminalCsv);

        Assert.DoesNotContain(feed.Columns, c => c.Name == "IsForwardFilled");

        Assert.Equal("Ain Sukhna", rows[0].Value(feed, "TerminalName"));
        Assert.Equal("Total Stocks", rows[0].Value(feed, "FlowBreakdown"));
        Assert.Equal("Capacity", rows[1].Value(feed, "FlowBreakdown"));
        Assert.Equal("Egypt", rows[0].Value(feed, "CountryName"));
        Assert.Equal(11916m, rows[0].Value(feed, "ObservedValue"));
    }

    // --------------------------------------------------------- derived columns

    [Fact]
    public async Task Every_row_carries_a_derived_RowId_FileName_and_Checksum()
    {
        var feed = TestHelpers.Feed(OilXDescriptors.GlobalBalance);
        var (rows, _, _) = await TestHelpers.ReadAsync(
            feed, Samples.GlobalBalanceCsv, "GlobalBalance.2026-09-30T09-55.csv");

        foreach (var row in rows)
        {
            Assert.IsType<Guid>(row.Value(feed, "RowId"));
            Assert.NotEqual(Guid.Empty, (Guid)row.Value(feed, "RowId"));
            Assert.Equal("GlobalBalance.2026-09-30T09-55.csv", row.Value(feed, "FileName"));
            Assert.IsType<int>(row.Value(feed, "Checksum"));
        }

        Assert.NotEqual(rows[0].Value(feed, "RowId"), rows[1].Value(feed, "RowId"));
    }

    /// <summary>
    /// The same series read from two different snapshots of the same day must produce the
    /// SAME RowId — that is what makes the later snapshot update the row rather than
    /// insert a second one.
    /// </summary>
    [Fact]
    public async Task The_same_series_from_two_snapshots_merges_onto_one_RowId()
    {
        var feed = TestHelpers.Feed(OilXDescriptors.GlobalBalance);

        var (early, _, _) = await TestHelpers.ReadAsync(
            feed, Samples.GlobalBalanceCsv, "GlobalBalance.2026-09-30T03-39.csv");

        // The later snapshot, with a REVISED value for the same series.
        var revised = Samples.GlobalBalanceCsv.Replace("24534.0", "24999.0");
        var (late, _, _) = await TestHelpers.ReadAsync(
            feed, revised, "GlobalBalance.2026-09-30T21-10.csv");

        Assert.Equal(early[0].Value(feed, "RowId"), late[0].Value(feed, "RowId"));
        Assert.Equal(early[0].Value(feed, "RunDate"), late[0].Value(feed, "RunDate"));

        // ... but a different Checksum, so the merge's guard lets the UPDATE through.
        Assert.NotEqual(early[0].Value(feed, "Checksum"), late[0].Value(feed, "Checksum"));
    }

    /// <summary>A re-read of the same snapshot must be byte-identical, so the merge no-ops.</summary>
    [Fact]
    public async Task Re_reading_the_same_snapshot_produces_identical_rows()
    {
        var feed = TestHelpers.Feed(OilXDescriptors.SupplyDemand);

        var (first, _, _) = await TestHelpers.ReadAsync(feed, Samples.SupplyDemandCsv);
        var (second, _, _) = await TestHelpers.ReadAsync(feed, Samples.SupplyDemandCsv);

        for (var i = 0; i < first.Count; i++)
            Assert.Equal(first[i].Values, second[i].Values);
    }

    // ---------------------------------------------------------------- batching

    [Fact]
    public async Task Rows_are_merged_in_batches_of_the_configured_size()
    {
        var feed = TestHelpers.Feed(OilXDescriptors.GlobalBalance);

        var csv = Samples.GlobalBalanceHeader + "\r\n" +
                  string.Join("\r\n", Enumerable.Range(1, 5)
                      .Select(i => $"G{i},2010-01-01,Crude Production,KBD,{i}.0,2026-09-30")) + "\r\n";

        var (rows, batches, result) = await TestHelpers.ReadAsync(
            feed, csv, settings: TestHelpers.Settings(s => s.BatchSize = 2));

        Assert.Equal(5, result.RowsRead);
        Assert.Equal(5, rows.Count);
        Assert.Equal(new[] { 2, 2, 1 }, batches);   // the tail batch is flushed
    }

    [Fact]
    public async Task A_file_with_only_a_header_reads_zero_rows_without_failing()
    {
        var feed = TestHelpers.Feed(OilXDescriptors.GlobalBalance);
        var (rows, batches, result) = await TestHelpers.ReadAsync(
            feed, Samples.GlobalBalanceHeader + "\r\n");

        Assert.Equal(0, result.RowsRead);
        Assert.Empty(rows);
        Assert.Empty(batches);
    }

    // ------------------------------------------------------------ header faults

    /// <summary>
    /// A missing REQUIRED header is fatal for the file. Dropping its rows one at a time
    /// would report a successful empty load — the silent failure worth being loud about.
    /// </summary>
    [Fact]
    public async Task A_file_missing_the_RunDate_header_fails_loudly()
    {
        var feed = TestHelpers.Feed(OilXDescriptors.GlobalBalance);

        var csv = "GroupName,ReferenceDate,FlowBreakdown,UnitMeasure,ObservedValue\r\n" +
                  "OPEC,2010-01-01,Crude Production,KBD,24534.0\r\n";

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => TestHelpers.ReadAsync(feed, csv));

        Assert.Contains("RunDate", ex.Message);
        Assert.Contains("required column", ex.Message);
    }

    /// <summary>
    /// An absent OPTIONAL header is not fatal — historic files really do have fewer
    /// columns — it just loads as NULL.
    /// </summary>
    [Fact]
    public async Task An_absent_optional_header_loads_as_null()
    {
        var feed = TestHelpers.Feed(OilXDescriptors.RegionalBalance);

        var csv = "GroupName,ReferenceDate,FlowBreakdown,UnitMeasure,ObservedValue,RunDate\r\n" +
                  "Africa,2010-01-01,CLOSTLV,KBBL,130664.0,2026-09-30\r\n";

        var (rows, _, result) = await TestHelpers.ReadAsync(feed, csv);

        Assert.Equal(1, result.RowsRead);
        Assert.Equal(DBNull.Value, rows[0].Value(feed, "GeneralizedSource"));
    }

    [Fact]
    public async Task An_empty_document_fails_rather_than_loading_nothing()
    {
        var feed = TestHelpers.Feed(OilXDescriptors.GlobalBalance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => TestHelpers.ReadAsync(feed, string.Empty));

        Assert.Contains("no header row", ex.Message);
    }

    // --------------------------------------------------------------- bad cells

    /// <summary>A bad VALUE cell becomes NULL and is counted; the row still loads.</summary>
    [Fact]
    public async Task An_unparseable_value_cell_becomes_null_and_is_counted()
    {
        var feed = TestHelpers.Feed(OilXDescriptors.GlobalBalance);

        var csv = Samples.GlobalBalanceHeader + "\r\n" +
                  "OPEC,2010-01-01,Crude Production,KBD,not-a-number,2026-09-30\r\n";

        var (rows, _, result) = await TestHelpers.ReadAsync(feed, csv);

        Assert.Equal(1, result.RowsRead);
        Assert.Equal(0, result.RowsDropped);
        Assert.Equal(1, result.CellsFailed);
        Assert.Equal(DBNull.Value, rows[0].Value(feed, "ObservedValue"));
    }

    /// <summary>A bad REQUIRED cell drops the row rather than merging under a blank key.</summary>
    [Fact]
    public async Task A_row_whose_RunDate_cannot_parse_is_dropped_and_counted()
    {
        var feed = TestHelpers.Feed(OilXDescriptors.GlobalBalance);

        var csv = Samples.GlobalBalanceHeader + "\r\n" +
                  "OPEC,2010-01-01,Crude Production,KBD,24534.0,not-a-date\r\n" +
                  "OPEC,2010-02-01,Crude Production,KBD,24700.0,2026-09-30\r\n";

        var (rows, _, result) = await TestHelpers.ReadAsync(feed, csv);

        Assert.Equal(2, result.RowsRead);
        Assert.Equal(1, result.RowsDropped);
        Assert.Single(rows);
        Assert.Equal(new DateTime(2026, 9, 30), rows[0].Value(feed, "RunDate"));
    }

    /// <summary>
    /// RunDate comes FROM THE FILE, never from the loader's clock — which is what makes
    /// all of a day's snapshots merge onto one another.
    /// </summary>
    [Fact]
    public async Task RunDate_is_read_from_the_file_not_the_clock()
    {
        var feed = TestHelpers.Feed(OilXDescriptors.GlobalBalance);

        var csv = Samples.GlobalBalanceHeader + "\r\n" +
                  "OPEC,2010-01-01,Crude Production,KBD,24534.0,2024-03-15\r\n";

        var (rows, _, _) = await TestHelpers.ReadAsync(feed, csv, "GlobalBalance.2026-09-30T09-55.csv");

        Assert.Equal(new DateTime(2024, 3, 15), rows[0].Value(feed, "RunDate"));
    }
}
