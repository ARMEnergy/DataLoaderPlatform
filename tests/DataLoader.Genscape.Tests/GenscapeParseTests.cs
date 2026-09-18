using Xunit;

namespace DataLoader.Genscape.Tests;

/// <summary>
/// The JSON -&gt; row mapping for both feeds, driven by payloads captured verbatim from
/// the live API.
/// </summary>
public sealed class GenscapeParseTests
{
    private static readonly CrudeStorageWeeklyFeed Storage = new();
    private static readonly CrudeTransportationWeeklyFeed Transportation = new();

    private static GenscapeTableDescriptor StorageTable => GenscapeDescriptors.CrudeStorageWeeklyTable;
    private static GenscapeTableDescriptor TransportTable => GenscapeDescriptors.CrudeTransportationWeeklyTable;

    // ------------------------------------------------------------ the week fix

    /// <summary>
    /// ⚠ THE CORRECTION. The captured storage payload says <c>"week": 1</c> for
    /// 2025-12-05; the loaded row must say 49. If this ever fails, the loader has
    /// started trusting the endpoint's own field again.
    /// </summary>
    [Fact]
    public void Storage_overwrites_the_endpoints_week_of_month_with_the_week_of_year()
    {
        var rows = Storage.Parse(Samples.StorageJson, new GenscapeReadStats());

        var december = rows.Single(r =>
            (DateTime)r.Value(StorageTable, "ReportDate") == new DateTime(2025, 12, 5) &&
            (string)r.Value(StorageTable, "Product") == "Crude");

        Assert.Equal((byte)49, december.Value(StorageTable, "Week"));
        Assert.Equal((short)2025, december.Value(StorageTable, "Year"));

        // The payload's own value, for contrast — this is what would have been stored.
        Assert.NotEqual((byte)1, december.Value(StorageTable, "Week"));
    }

    /// <summary>
    /// The transportation endpoint already agrees, so deriving must be a NO-OP there.
    /// That is the property that makes the derivation trustworthy rather than merely
    /// different.
    /// </summary>
    [Fact]
    public void Transportation_derivation_reproduces_the_endpoints_own_week()
    {
        var rows = Transportation.Parse(Samples.TransportationJson, new GenscapeReadStats());

        var december = rows.First(r => (DateTime)r.Value(TransportTable, "ReportDate") == new DateTime(2025, 12, 5));
        Assert.Equal((byte)49, december.Value(TransportTable, "Week"));   // payload also says 49
        Assert.Equal((short)2025, december.Value(TransportTable, "Year"));

        var january = rows.Single(r => (DateTime)r.Value(TransportTable, "ReportDate") == new DateTime(2026, 1, 2));
        Assert.Equal((byte)1, january.Value(TransportTable, "Week"));     // payload also says 1
        Assert.Equal((short)2026, january.Value(TransportTable, "Year"));
    }

    // --------------------------------------------------------------- the mapping

    [Fact]
    public void Storage_maps_every_column_by_position()
    {
        var rows = Storage.Parse(Samples.StorageJson, new GenscapeReadStats());
        Assert.Equal(3, rows.Count);

        var diluent = rows.Single(r => (string)r.Value(StorageTable, "Product") == "Diluent");

        Assert.Equal(new DateTime(2025, 12, 5), diluent.Value(StorageTable, "ReportDate"));
        Assert.Equal("Canada", diluent.Value(StorageTable, "Region"));
        Assert.Equal("Storage Terminal", diluent.Value(StorageTable, "StorageFieldType"));
        Assert.Equal(1846227d, diluent.Value(StorageTable, "StorageAmount"));
        Assert.Equal(40.12d, diluent.Value(StorageTable, "CapacityUtilization"));
    }

    [Fact]
    public void Transportation_maps_every_column_by_position()
    {
        var rows = Transportation.Parse(Samples.TransportationJson, new GenscapeReadStats());
        Assert.Equal(3, rows.Count);

        var rail = rows.Single(r => (string)r.Value(TransportTable, "Type") == "Rail");

        Assert.Equal(new DateTime(2026, 1, 2), rail.Value(TransportTable, "ReportDate"));
        Assert.Equal("Canada to PADD 2", rail.Value(TransportTable, "Region"));
        Assert.Equal(78123d, rail.Value(TransportTable, "FlowBPD"));
    }

    /// <summary>
    /// ⚠ The <c>Region</c> stored is the one on the ROW, not the request region. The two
    /// request regions return overlapping sets of these, which is why the merge has to
    /// be safe against the same key arriving from two work units.
    /// </summary>
    [Fact]
    public void Region_comes_from_the_row_not_from_the_request()
    {
        var rows = Storage.Parse(Samples.StorageJson, new GenscapeReadStats());

        var regions = rows.Select(r => (string)r.Value(StorageTable, "Region")).Distinct().ToList();

        Assert.Contains("Canada", regions);
        Assert.Contains("Cushing", regions);
        Assert.DoesNotContain("NorthAmerica", regions);
    }

    /// <summary>Every row must carry exactly the descriptor's column count, in its CLR types.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Rows_match_the_descriptor_arity_and_clr_types(bool storage)
    {
        var feed = storage ? (GenscapeFeedDescriptor)Storage : Transportation;
        var json = storage ? Samples.StorageJson : Samples.TransportationJson;

        var rows = feed.Parse(json, new GenscapeReadStats());

        foreach (var row in rows)
        {
            Assert.Equal(feed.Table.Columns.Count, row.Values.Length);

            for (var i = 0; i < feed.Table.Columns.Count; i++)
            {
                var column = feed.Table.Columns[i];
                var value = row.Values[i];

                Assert.False(value is null, $"{column.Name} is a CLR null; it must be DBNull.Value.");

                if (value is DBNull)
                {
                    Assert.False(column.Required, $"{column.Name} is required but came back NULL.");
                    continue;
                }

                Assert.Equal(column.ClrType, value.GetType());
            }
        }
    }

    // ---------------------------------------------------- required-field handling

    /// <summary>
    /// A record missing a primary-key component is DROPPED and counted, not merged under
    /// a blank key.
    /// </summary>
    [Theory]
    [InlineData(""" {"data":[{"reportDate":null,"region":"Canada","product":"Crude","storageFieldType":"T"}]} """)]
    [InlineData(""" {"data":[{"reportDate":"2026-01-02","region":"","product":"Crude","storageFieldType":"T"}]} """)]
    [InlineData(""" {"data":[{"reportDate":"2026-01-02","region":"Canada","product":null,"storageFieldType":"T"}]} """)]
    [InlineData(""" {"data":[{"reportDate":"2026-01-02","region":"Canada","product":"Crude","storageFieldType":"   "}]} """)]
    [InlineData(""" {"data":[{"reportDate":"not-a-date","region":"Canada","product":"Crude","storageFieldType":"T"}]} """)]
    public void Storage_records_missing_a_key_component_are_dropped_and_counted(string json)
    {
        var stats = new GenscapeReadStats();
        var rows = Storage.Parse(json, stats);

        Assert.Empty(rows);
        Assert.Equal(1, stats.SourceRecords);
        Assert.Equal(1, stats.DroppedRequired);
    }

    [Theory]
    [InlineData(""" {"data":[{"type":null,"reportDate":"2026-01-02","region":"X"}]} """)]
    [InlineData(""" {"data":[{"type":"Rail","reportDate":"2026-01-02","region":"  "}]} """)]
    public void Transportation_records_missing_a_key_component_are_dropped_and_counted(string json)
    {
        var stats = new GenscapeReadStats();

        Assert.Empty(Transportation.Parse(json, stats));
        Assert.Equal(1, stats.DroppedRequired);
    }

    /// <summary>The nullable measures are NOT key components — a null one keeps its row.</summary>
    [Fact]
    public void A_null_measure_keeps_the_row_as_DBNull()
    {
        var stats = new GenscapeReadStats();

        var rows = Storage.Parse(
            """
            {"data":[{"reportDate":"2026-01-02","region":"Canada","product":"Crude",
                      "storageFieldType":"Storage Terminal","storageAmount":null,"capacityUtilization":null}]}
            """, stats);

        var row = Assert.Single(rows);
        Assert.Equal(DBNull.Value, row.Value(StorageTable, "StorageAmount"));
        Assert.Equal(DBNull.Value, row.Value(StorageTable, "CapacityUtilization"));
        Assert.Equal(0, stats.DroppedRequired);
    }

    /// <summary>
    /// A label wider than VARCHAR(50) is truncated to fit and COUNTED, rather than
    /// failing the work unit or being silently shortened by SqlClient.
    /// </summary>
    [Fact]
    public void An_over_wide_region_is_truncated_and_counted()
    {
        var wide = new string('R', 80);
        var stats = new GenscapeReadStats();

        var rows = Storage.Parse(
            $$"""
              {"data":[{"reportDate":"2026-01-02","region":"{{wide}}","product":"Crude",
                        "storageFieldType":"Storage Terminal","storageAmount":1}]}
              """, stats);

        var row = Assert.Single(rows);
        Assert.Equal(50, ((string)row.Value(StorageTable, "Region")).Length);
        Assert.Equal(1, stats.Truncated);
    }

    /// <summary>Surrounding whitespace is trimmed — a padded label must not fork the key.</summary>
    [Fact]
    public void Key_text_is_trimmed()
    {
        var rows = Storage.Parse(
            """
            {"data":[{"reportDate":"2026-01-02","region":"  Cushing  ","product":" Crude ",
                      "storageFieldType":"Storage Terminal","storageAmount":1}]}
            """, new GenscapeReadStats());

        var row = Assert.Single(rows);
        Assert.Equal("Cushing", row.Value(StorageTable, "Region"));
        Assert.Equal("Crude", row.Value(StorageTable, "Product"));
    }

    /// <summary>Quantities quoted as strings still parse — cheap insurance against a re-typed field.</summary>
    [Fact]
    public void A_quantity_sent_as_a_string_still_parses()
    {
        var rows = Transportation.Parse(
            """
            {"data":[{"type":"Rail","reportDate":"2026-01-02","region":"X","flowBPD":"12345.5"}]}
            """, new GenscapeReadStats());

        Assert.Equal(12345.5d, Assert.Single(rows).Value(TransportTable, "FlowBPD"));
    }

    /// <summary>
    /// NaN and infinity become NULL. SQL Server's FLOAT accepts neither, and one bad
    /// quantity must not fail the whole TVP call.
    /// </summary>
    [Fact]
    public void A_non_finite_quantity_becomes_null_rather_than_failing_the_merge()
    {
        var rows = Transportation.Parse(
            """
            {"data":[{"type":"Rail","reportDate":"2026-01-02","region":"X","flowBPD":"NaN"}]}
            """, new GenscapeReadStats());

        Assert.Equal(DBNull.Value, Assert.Single(rows).Value(TransportTable, "FlowBPD"));
    }

    // ------------------------------------------------------------- the envelope

    /// <summary>
    /// <c>{"data":[]}</c> is a LEGITIMATE EMPTY READ — a window before the first report
    /// or after the latest published one. It must not throw.
    /// </summary>
    [Fact]
    public void An_empty_data_array_is_a_successful_empty_read()
    {
        var stats = new GenscapeReadStats();

        Assert.Empty(Storage.Parse(Samples.EmptyJson, stats));
        Assert.Empty(Transportation.Parse(Samples.EmptyJson, stats));
        Assert.Equal(0, stats.DroppedRequired);
    }

    /// <summary>
    /// ⚠ A MISSING <c>data</c> property is NOT an empty read — it is a contract change.
    /// Reading it as "no rows" would report a clean, successful, empty load forever.
    /// </summary>
    [Theory]
    [InlineData("""{ "items": [] }""")]
    [InlineData("{ }")]
    [InlineData("""{ "data": null }""")]
    public void A_missing_data_array_throws_rather_than_loading_nothing(string json)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Storage.Parse(json, new GenscapeReadStats()));

        Assert.Contains("data", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_body_that_is_not_json_throws_with_the_feed_named()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => Storage.Parse("<html>502 Bad Gateway</html>", new GenscapeReadStats()));

        Assert.Contains(GenscapeFeed.CrudeStorageWeekly, ex.Message);
    }
}
