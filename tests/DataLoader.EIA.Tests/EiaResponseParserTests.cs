using Xunit;

namespace DataLoader.EIA.Tests;

/// <summary>
/// Response parsing, and above all the FACET/DATA PARTITION — which decides what goes
/// into <c>FacetJSON</c>, and so decides <c>FacetKey</c>, and so decides the row's
/// primary key. Getting it wrong does not fail; it forks the table.
///
/// <para>
/// The fixtures below are real response bodies captured from the live API on
/// 2026-10-16, trimmed to the first rows.
/// </para>
/// </summary>
public sealed class EiaResponseParserTests
{
    private static EiaParsedPage Parse(string json, EiaSeriesPlan plan, string frequencyId = "annual") =>
        EiaResponseParser.Parse(json, plan, frequencyId, coerceNumericStrings: true);

    // --------------------------------------------- the real coal response

    private const string CoalResponse = """
    {"response":{"total":"2","dateFormat":"YYYY","frequency":"annual","data":[
      {"period":"2021","stateRegionId":"MO","stateRegionDescription":"Missouri","marketTypeId":"OM","marketTypeDescription":"Open Market","price":"w","sales":"44163","price-units":"dollars per short ton","sales-units":"short tons"},
      {"period":"2021","stateRegionId":"MO","stateRegionDescription":"Missouri","marketTypeId":"TOT","marketTypeDescription":"Total","price":"53","sales":"44163","price-units":"dollars per short ton","sales-units":"short tons"}],
      "description":"Coal market sales prices"},
     "request":{"command":"/v2/coal/market-sales-price/data/"},"apiVersion":"2.1.14"}
    """;

    /// <summary>
    /// End to end against the loader request's own worked example: a live response
    /// body in, and the exact row the request says to store out — including both
    /// hashes, which were read from the incumbent table and not computed here.
    /// </summary>
    [Fact]
    public void Reproduces_the_worked_example_row_from_a_live_response_body()
    {
        var page = Parse(CoalResponse, TestHelpers.CoalPlan());

        Assert.Equal(2, page.Total);
        Assert.Equal("YYYY", page.DateFormat);
        Assert.Equal(2, page.Rows.Count);

        var openMarket = page.Rows[0];

        Assert.Equal("coal/market-sales-price", openMarket.SeriesId);
        Assert.Equal("annual", openMarket.FrequencyId);
        Assert.Equal("2021", openMarket.Period);

        Assert.Equal(
            """{"marketTypeDescription":"Open Market","marketTypeId":"OM","stateRegionDescription":"Missouri","stateRegionId":"MO"}""",
            openMarket.FacetJson);
        Assert.Equal(-1622649321, openMarket.FacetKey);

        Assert.Equal(
            """{"price":"w","price-units":"dollars per short ton","sales":44163,"sales-units":"short tons"}""",
            openMarket.DataJson);
        Assert.Equal(-764207628, openMarket.DataHash);
    }

    [Fact]
    public void Period_is_excluded_from_both_json_objects()
    {
        var page = Parse(CoalResponse, TestHelpers.CoalPlan());

        foreach (var row in page.Rows)
        {
            Assert.DoesNotContain("period", row.FacetJson, StringComparison.Ordinal);
            Assert.DoesNotContain("period", row.DataJson, StringComparison.Ordinal);
        }
    }

    // ------------------------------------------- the partition, per shape

    /// <summary>
    /// The <c>natural-gas/*</c> shape: the data-units field is the BARE <c>units</c>,
    /// not <c>value-units</c>. Verified against the live incumbent row
    /// <c>{"units":"BTU/CF","value":"1028"}</c>.
    /// </summary>
    [Fact]
    public void Treats_a_bare_units_field_as_data_not_facet()
    {
        const string json = """
        {"response":{"total":"1","dateFormat":"YYYY","frequency":"annual","data":[
          {"period":"2003","duoarea":"NUS","area-name":"U.S.","product":"EPG0","product-name":"Natural Gas","process":"VC0H","process-name":"Heat Content of Total Consumption","series":"NGA_EPG0_VC0H_NUS_BTUCF","series-description":"Total","value":"1028","units":"BTU\/CF"}]}}
        """;

        var dataTypes = new[] { TestHelpers.DataType("value", "natural-gas/cons/heat") };
        var plan = TestHelpers.Plan(
            TestHelpers.Series("natural-gas/cons/heat", "2003", "2026", "YYYY-MM", "monthly"),
            new[] { TestHelpers.Frequency("annual", "YYYY", "natural-gas/cons/heat") },
            dataTypes);

        var row = Assert.Single(Parse(json, plan).Rows);

        Assert.Equal("""{"units":"BTU/CF","value":1028}""", row.DataJson);
        Assert.DoesNotContain("units", row.FacetJson, StringComparison.Ordinal);
        Assert.Contains("duoarea", row.FacetJson, StringComparison.Ordinal);
    }

    /// <summary>
    /// The <c>international</c> shape: SINGULAR <c>unit</c> and <c>unitName</c> are
    /// FACETS, even though the plural <c>units</c> is data. The two must not be
    /// conflated.
    /// </summary>
    [Fact]
    public void Treats_singular_unit_and_unitName_as_facets()
    {
        const string json = """
        {"response":{"total":"1","dateFormat":"YYYY","frequency":"annual","data":[
          {"period":"1949","activityId":"8","activityName":"Emissions","countryRegionId":"WORL","countryRegionName":"World","unit":"MMTCD","unitName":"million metric tonnes carbon dioxide","value":"2206.690829"}]}}
        """;

        var dataTypes = new[] { TestHelpers.DataType("value", "international") };
        var plan = TestHelpers.Plan(
            TestHelpers.Series("international", "1949-01", "2026-06", "YYYY-MM", "monthly"),
            new[] { TestHelpers.Frequency("annual", "YYYY", "international") },
            dataTypes);

        var row = Assert.Single(Parse(json, plan).Rows);

        Assert.Equal("""{"value":2206.690829}""", row.DataJson);
        Assert.Contains("\"unit\":\"MMTCD\"", row.FacetJson, StringComparison.Ordinal);
        Assert.Contains("unitName", row.FacetJson, StringComparison.Ordinal);
    }

    /// <summary>
    /// The incumbent has rows that put <c>value-units</c> in the FACET object for
    /// <c>electricity/rto/daily-region-data</c> — a Conduit defect that forked those
    /// rows onto a second FacetKey. This loader always places it with the data.
    /// </summary>
    [Fact]
    public void Always_places_datatype_units_with_the_data()
    {
        const string json = """
        {"response":{"total":"1","dateFormat":"YYYY-MM-DD","frequency":"daily","data":[
          {"period":"2019-01-01","respondent":"SRP","respondent-name":"Salt River Project","type":"D","type-name":"Demand","timezone":"Mountain","timezone-description":"Mountain","value":"82817","value-units":"megawatthours"}]}}
        """;

        var dataTypes = new[] { TestHelpers.DataType("value", "electricity/rto/daily-region-data") };
        var plan = TestHelpers.Plan(
            TestHelpers.Series("electricity/rto/daily-region-data", "2019-01-01", "2026-10-15", "YYYY-MM-DD", "daily"),
            new[] { TestHelpers.Frequency("daily", "YYYY-MM-DD", "electricity/rto/daily-region-data") },
            dataTypes);

        var row = Assert.Single(Parse(json, plan, "daily").Rows);

        Assert.Equal("""{"value":82817,"value-units":"megawatthours"}""", row.DataJson);
        Assert.DoesNotContain("value-units", row.FacetJson, StringComparison.Ordinal);
    }

    [Fact]
    public void A_row_with_no_facets_gets_the_empty_object_and_its_hash()
    {
        const string json = """
        {"response":{"total":"1","dateFormat":"YYYY-MM","frequency":"monthly","data":[
          {"period":"2016-01","average-price":"151.07","average-price-units":"USD per ton","quantity":"318480","quantity-units":"tons"}]}}
        """;

        var dataTypes = new[]
        {
            TestHelpers.DataType("average-price", "densified-biomass/export-sales-and-price"),
            TestHelpers.DataType("quantity", "densified-biomass/export-sales-and-price")
        };
        var plan = TestHelpers.Plan(
            TestHelpers.Series("densified-biomass/export-sales-and-price", "2016-01", "2026-06", "YYYY-MM", "monthly"),
            new[] { TestHelpers.Frequency("monthly", "YYYY-MM", "densified-biomass/export-sales-and-price") },
            dataTypes);

        var row = Assert.Single(Parse(json, plan, "monthly").Rows);

        Assert.Equal("{}", row.FacetJson);
        Assert.Equal(1947253782, row.FacetKey);
        // The live DataHash of that exact stored text.
        Assert.Equal(-94481908, row.DataHash);
    }

    [Fact]
    public void Preserves_json_nulls_in_the_data_object()
    {
        const string json = """
        {"response":{"total":"1","dateFormat":"YYYY","frequency":"annual","data":[
          {"period":"2001","coalRankId":"ANT","average-employees":null,"average-employees-units":"number of employees","production":341096,"production-units":"short tons"}]}}
        """;

        var dataTypes = new[]
        {
            TestHelpers.DataType("average-employees", "coal/aggregate-production"),
            TestHelpers.DataType("production", "coal/aggregate-production")
        };
        var plan = TestHelpers.Plan(
            TestHelpers.Series("coal/aggregate-production"),
            new[] { TestHelpers.Frequency("annual", "YYYY", "coal/aggregate-production") },
            dataTypes);

        var row = Assert.Single(Parse(json, plan).Rows);

        Assert.Contains("\"average-employees\":null", row.DataJson, StringComparison.Ordinal);
    }

    // ------------------------------------------------- warnings and totals

    [Fact]
    public void Reads_total_whether_it_is_a_string_or_a_number()
    {
        var asString = Parse("""{"response":{"total":"143","data":[]}}""", TestHelpers.CoalPlan());
        var asNumber = Parse("""{"response":{"total":143,"data":[]}}""", TestHelpers.CoalPlan());

        Assert.Equal(143, asString.Total);
        Assert.Equal(143, asNumber.Total);
    }

    /// <summary>
    /// <c>warnings</c> sits at the ROOT, outside <c>response</c>. Reading it from the
    /// wrong level is how a truncated answer gets mistaken for a complete one.
    /// </summary>
    [Fact]
    public void Reads_the_incomplete_return_warning_from_the_root()
    {
        const string json = """
        {"warnings":[{"warning":"incomplete return","description":"The API can only return 5000 rows in JSON format."}],
         "response":{"total":"4031645","dateFormat":"YYYY-MM-DD","frequency":"daily","data":[]}}
        """;

        var page = Parse(json, TestHelpers.CoalPlan());

        Assert.True(page.IncompleteReturn);
        Assert.False(page.ParameterOutOfRange);
        Assert.Equal(4031645, page.Total);
    }

    [Fact]
    public void Flags_a_parameter_out_of_range_warning_separately()
    {
        const string json = """
        {"warnings":[{"warning":"parameter out of range: length","description":"The maximum value for length is 5000 records"}],
         "response":{"total":"3739","data":[]}}
        """;

        var page = Parse(json, TestHelpers.CoalPlan());

        Assert.True(page.ParameterOutOfRange);
        Assert.False(page.IncompleteReturn);
    }

    [Fact]
    public void An_empty_data_array_is_a_legitimate_empty_window()
    {
        var page = Parse("""{"response":{"total":"0","dateFormat":"YYYY","frequency":"annual","data":[]}}""",
                         TestHelpers.CoalPlan());

        Assert.Empty(page.Rows);
        Assert.Equal(0, page.Total);
        Assert.Equal(0, page.RowsDropped);
    }

    [Fact]
    public void A_body_with_no_response_object_yields_nothing_rather_than_throwing()
    {
        var page = Parse("""{"error":{"code":"API_KEY_INVALID"}}""", TestHelpers.CoalPlan());

        Assert.Empty(page.Rows);
        Assert.Equal(-1, page.Total);
    }

    // ------------------------------------------------------------ drops

    /// <summary>
    /// A row with no period cannot be keyed. Merging it would put every such row of the
    /// window onto one primary key, so it is dropped and counted instead.
    /// </summary>
    [Fact]
    public void Drops_and_counts_a_row_with_no_period()
    {
        const string json = """
        {"response":{"total":"3","dateFormat":"YYYY","frequency":"annual","data":[
          {"period":"2021","stateRegionId":"MO","price":"53"},
          {"stateRegionId":"MO","price":"54"},
          {"period":"","stateRegionId":"MO","price":"55"}]}}
        """;

        var page = Parse(json, TestHelpers.CoalPlan());

        Assert.Single(page.Rows);
        Assert.Equal(2, page.RowsDropped);
    }

    [Fact]
    public void Accepts_a_numeric_period_token()
    {
        const string json = """
        {"response":{"total":"1","dateFormat":"YYYY","frequency":"annual","data":[
          {"period":2021,"stateRegionId":"MO","price":"53"}]}}
        """;

        var row = Assert.Single(Parse(json, TestHelpers.CoalPlan()).Rows);
        Assert.Equal("2021", row.Period);
    }

    // ----------------------------------------------- the field-name set

    [Fact]
    public void Builds_the_data_field_name_set_from_the_catalog()
    {
        var names = EiaWorkUnitProvider.BuildDataFieldNames(new[]
        {
            TestHelpers.DataType("price"),
            TestHelpers.DataType("sales")
        });

        Assert.Contains("price", names);
        Assert.Contains("price-units", names);
        Assert.Contains("sales", names);
        Assert.Contains("sales-units", names);
        Assert.Contains("units", names);

        // Singular forms are facets.
        Assert.DoesNotContain("unit", names);
        Assert.DoesNotContain("unitName", names);
        Assert.DoesNotContain("period", names);
    }
}
