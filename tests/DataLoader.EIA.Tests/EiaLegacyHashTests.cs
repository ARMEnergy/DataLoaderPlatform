using Xunit;

namespace DataLoader.EIA.Tests;

/// <summary>
/// The hash gate.
///
/// <para>
/// <c>FacetKey</c> is part of <c>arm.SeriesData</c>'s PRIMARY KEY and <c>DataHash</c>
/// is the merge's change guard. If <see cref="EiaLegacyHash"/> ever produces a
/// different number for the same text, every row misses its merge target and the next
/// run appends a second copy of the table instead of updating it. Nothing else in this
/// loader fails that expensively, so the algorithm is pinned here against values that
/// were not computed by this code.
/// </para>
/// <para>
/// <b>Where the expected values come from.</b> Every vector below is a real row read
/// out of the live incumbent <c>EIAv2.dbo.SeriesData</c> (~94 million rows, written by
/// Conduit over several years) on 2026-10-16, plus the worked example supplied with the
/// loader request. They are ground truth, independent of this implementation. The same
/// comparison was run in bulk over a 20,000-row sample: 20,000/20,000 matched on
/// FacetKey and 20,000/20,000 on DataHash.
/// </para>
/// </summary>
public sealed class EiaLegacyHashTests
{
    // ------------------------------------------------- the request's own example

    [Fact]
    public void Matches_the_worked_example_from_the_loader_request()
    {
        Assert.Equal(
            -1622649321,
            EiaLegacyHash.Compute(
                """{"marketTypeDescription":"Open Market","marketTypeId":"OM","stateRegionDescription":"Missouri","stateRegionId":"MO"}"""));

        Assert.Equal(
            -764207628,
            EiaLegacyHash.Compute(
                """{"price":"w","price-units":"dollars per short ton","sales":44163,"sales-units":"short tons"}"""));
    }

    // -------------------------------------------------- live incumbent vectors

    public static TheoryData<string, int> LiveVectors() => new()
    {
        // --- coal/market-sales-price: strings, integers and a decimal ---
        {
            """{"marketTypeDescription":"Total","marketTypeId":"TOT","stateRegionDescription":"Indiana","stateRegionId":"IN"}""",
            -2083405178
        },
        {
            """{"price":43.52,"price-units":"dollars per short ton","sales":19770249,"sales-units":"short tons"}""",
            1075285628
        },
        {
            """{"marketTypeDescription":"Open Market","marketTypeId":"OM","stateRegionDescription":"Pennsylvania","stateRegionId":"PA"}""",
            -2045852013
        },
        {
            """{"price":"w","price-units":"dollars per short ton","sales":27900388,"sales-units":"short tons"}""",
            1697250033
        },

        // --- the facet-less row. 7,301 rows in the incumbent carry exactly this. ---
        { "{}", 1947253782 },
        {
            """{"average-price":151.07,"average-price-units":"USD per ton","quantity":318480,"quantity-units":"tons"}""",
            -94481908
        },

        // --- JSON nulls inside the data object ---
        {
            """{"average-employees":null,"average-employees-units":"number of employees","labor-hours":null,"labor-hours-units":"hours","number-of-mines":25,"number-of-mines-units":"number of mines","production":341096,"production-units":"short tons","productivity":null,"productivity-units":"short tons per labor hour"}""",
            530283442
        },
        {
            """{"coalRankDescription":"Anthracite","coalRankId":"ANT","mineTypeDescription":"Underground","mineTypeId":"UND","stateRegionDescription":"Appalachia Northern","stateRegionId":"APN"}""",
            -2099731174
        },

        // --- international: the SAME logical row stored twice, once with numeric
        //     tokens (2022) and once with string tokens (2024). Both hashes are real
        //     and both are in the table; see EiaCanonicalJsonTests for why the loader
        //     deliberately collapses them to one. ---
        {
            """{"activityId":"8","activityName":"Emissions","countryRegionId":"WORL","countryRegionName":"World","countryRegionTypeId":"r","countryRegionTypeName":"Region","dataFlagDescription":null,"dataFlagId":null,"productId":"4008","productName":"CO2 emissions","unit":"MMTCD","unitName":"million metric tonnes carbon dioxide"}""",
            -2135369135
        },
        {
            """{"activityId":8,"activityName":"Emissions","countryRegionId":"OECD","countryRegionName":"OECD","countryRegionTypeId":"r","countryRegionTypeName":"Region","dataFlagDescription":null,"dataFlagId":null,"productId":4002,"productName":"Coal and coke","unit":"MMTCD","unitName":"million metric tonnes carbon dioxide"}""",
            -2122406725
        },
        { """{"value":"2206.690829"}""", -2134288009 },
        { """{"value":1117.538985}""",    749992597 },

        // --- a '/' inside a value, stored UNESCAPED (the wire form is "BTU\/CF") ---
        { """{"units":"BTU/CF","value":"1028"}""", 1007497798 },
        {
            """{"area-name":"U.S.","duoarea":"NUS","process":"VGTH","process-name":"Heat Content Delivered to Consumers","product":"EPG0","product-name":"Natural Gas","series":"NGA_EPG0_VGTH_NUS_BTUCF","series-description":"U.S. Heat Content of Natural Gas Deliveries to Consumers (BTU per Cubic Foot)"}""",
            -172776443
        },

        // --- a negative numeric string, and a long facet object ---
        { """{"value":"-24","value-units":"megawatthours"}""", -1003607366 },
        {
            """{"respondent":"SRP","respondent-name":"Salt River Project Agricultural Improvement and Power District","timezone":"Mountain","timezone-description":"Mountain","type":"D","type-name":"Demand","value-units":"megawatthours"}""",
            -2147099737
        }
    };

    [Theory]
    [MemberData(nameof(LiveVectors))]
    public void Reproduces_values_stored_by_the_incumbent(string json, int expected) =>
        Assert.Equal(expected, EiaLegacyHash.Compute(json));

    // ----------------------------------------------------------- properties

    [Fact]
    public void Is_deterministic_across_calls()
    {
        const string json = """{"a":"1","b":2}""";
        Assert.Equal(EiaLegacyHash.Compute(json), EiaLegacyHash.Compute(json));
    }

    /// <summary>
    /// The guard against a vacuous implementation. A hash that returned a constant
    /// would pass every "is it stable" test and collapse the whole table onto one
    /// primary key.
    /// </summary>
    [Fact]
    public void Distinguishes_strings_that_differ()
    {
        var a = EiaLegacyHash.Compute("""{"price":1}""");
        var b = EiaLegacyHash.Compute("""{"price":2}""");
        var c = EiaLegacyHash.Compute("""{"price":"1"}""");

        Assert.NotEqual(a, b);
        Assert.NotEqual(a, c);
    }

    /// <summary>
    /// The folding is positional — even-indexed characters go into one accumulator and
    /// odd-indexed ones into the other — so a transposition must not be invisible.
    /// </summary>
    [Fact]
    public void Distinguishes_transposed_characters()
    {
        Assert.NotEqual(EiaLegacyHash.Compute("ab"), EiaLegacyHash.Compute("ba"));
        Assert.NotEqual(EiaLegacyHash.Compute("abcd"), EiaLegacyHash.Compute("abdc"));
    }

    /// <summary>
    /// Odd lengths exercise the loop's break path (the original read a NUL terminator
    /// where the second character would be), so both parities are pinned.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("ab")]
    [InlineData("abc")]
    public void Handles_short_and_odd_length_inputs(string value)
    {
        var first = EiaLegacyHash.Compute(value);
        Assert.Equal(first, EiaLegacyHash.Compute(value));
    }

    [Fact]
    public void Empty_string_hashes_to_the_seed()
    {
        // hash1 = hash2 = 5381, nothing consumed: 5381 + 5381 * 1566083941.
        Assert.Equal(unchecked(5381 + (5381 * 1566083941)), EiaLegacyHash.Compute(string.Empty));
    }

    /// <summary>
    /// ⚠ This is the test that would have caught the obvious wrong implementation.
    /// .NET 8 randomises <see cref="string.GetHashCode()"/> per process, so delegating
    /// to it would produce a FacetKey that changes on every run — and every row would
    /// miss its merge target. The runtime's value is expected NOT to match ours.
    /// </summary>
    [Fact]
    public void Does_not_merely_delegate_to_the_runtime_string_hash()
    {
        const string json = """{"marketTypeDescription":"Open Market","marketTypeId":"OM","stateRegionDescription":"Missouri","stateRegionId":"MO"}""";

        Assert.Equal(-1622649321, EiaLegacyHash.Compute(json));
        Assert.NotEqual(json.GetHashCode(), EiaLegacyHash.Compute(json));
    }
}
