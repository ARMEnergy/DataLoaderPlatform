using Xunit;

namespace DataLoader.OilX.Tests;

/// <summary>
/// <see cref="OilXRowId"/> and <see cref="OilXChecksum"/> — the two derivations the
/// merge depends on.
///
/// <para>
/// Both are CONTRACTS, not implementation details. <c>RowId</c> is half the primary key:
/// change how it is computed and the merge stops matching, so every table silently forks
/// into old-derivation and new-derivation copies that nothing reconciles. <c>Checksum</c>
/// decides whether a row is re-stamped, so a seed-dependent or un-normalised hash would
/// either re-stamp ~2M rows a day or hide real revisions.
/// </para>
/// <para>
/// The pinned values below are therefore deliberate tripwires: a change that alters them
/// is meant to fail loudly and be considered, not to pass quietly.
/// </para>
/// </summary>
public sealed class OilXIdentityTests
{
    private static Dictionary<string, object?> Values(params (string Name, object? Value)[] pairs)
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (name, value) in pairs) map[name] = value;
        return map;
    }

    private static readonly OilXFeedDescriptor GlobalBalance =
        TestHelpers.Feed(OilXDescriptors.GlobalBalance);

    private static Dictionary<string, object?> GlobalBalanceRow(
        string group = "OPEC", object? reference = null, string breakdown = "Crude Production",
        string unit = "KBD", decimal observed = 24534m) =>
        Values(
            ("RunDate", new DateTime(2026, 9, 30)),
            ("GroupName", group),
            ("ReferenceDate", reference ?? new DateTime(2010, 1, 1)),
            ("FlowBreakdown", breakdown),
            ("UnitMeasure", unit),
            ("ObservedValue", observed));

    // ------------------------------------------------------------------- RowId

    [Fact]
    public void RowId_is_deterministic_across_calls()
    {
        var a = OilXRowId.Compute(GlobalBalance, GlobalBalanceRow());
        var b = OilXRowId.Compute(GlobalBalance, GlobalBalanceRow());

        Assert.Equal(a, b);
        Assert.NotEqual(Guid.Empty, a);
    }

    /// <summary>
    /// ⚠ PINNED. These exact GUIDs are what is already stored in <c>arm.*</c>. If this
    /// test fails, the derivation changed and every previously-loaded row has been
    /// orphaned — that needs a migration, not a new expected value.
    /// </summary>
    [Fact]
    public void RowId_matches_its_pinned_value()
    {
        Assert.Equal(
            Guid.Parse("ea181cdc-2b6a-5d18-a84f-e2ffe476c6d8"),
            OilXRowId.FromName(OilXRowId.Namespace, "pinned-derivation-probe"));
    }

    /// <summary>The namespace is part of the contract; a typo in it re-keys everything.</summary>
    [Fact]
    public void Namespace_is_the_documented_constant()
    {
        Assert.Equal(Guid.Parse("6f696c78-0000-5000-8000-456e65726779"), OilXRowId.Namespace);
    }

    /// <summary>A v5 UUID must carry version 5 and the RFC 4122 variant.</summary>
    [Fact]
    public void RowId_is_a_well_formed_version_5_uuid()
    {
        var bytes = OilXRowId.Compute(GlobalBalance, GlobalBalanceRow()).ToByteArray();

        // .NET lays the first three fields out little-endian, so byte 7 holds the
        // version nibble and byte 8 the variant bits.
        Assert.Equal(0x50, bytes[7] & 0xF0);
        Assert.Equal(0x80, bytes[8] & 0xC0);
    }

    [Theory]
    [InlineData("GroupName")]
    [InlineData("FlowBreakdown")]
    [InlineData("UnitMeasure")]
    public void Changing_any_key_column_changes_the_RowId(string column)
    {
        var baseline = OilXRowId.Compute(GlobalBalance, GlobalBalanceRow());

        var changed = GlobalBalanceRow();
        changed[column] = "DIFFERENT";

        Assert.NotEqual(baseline, OilXRowId.Compute(GlobalBalance, changed));
    }

    /// <summary>
    /// A VALUE change must NOT change the identity — otherwise a revised number would
    /// insert a second row instead of updating the first, which is the whole point of
    /// keying on the business key rather than the whole row.
    /// </summary>
    [Fact]
    public void Changing_a_value_column_does_not_change_the_RowId()
    {
        Assert.Equal(
            OilXRowId.Compute(GlobalBalance, GlobalBalanceRow(observed: 24534m)),
            OilXRowId.Compute(GlobalBalance, GlobalBalanceRow(observed: 99999m)));
    }

    /// <summary>
    /// RunDate is a primary-key column in its own right, so the SAME series on a
    /// different day keeps the same RowId — which is what lets a query follow one series
    /// across snapshots.
    /// </summary>
    [Fact]
    public void RowId_is_independent_of_RunDate()
    {
        var day1 = GlobalBalanceRow();
        day1["RunDate"] = new DateTime(2026, 9, 30);

        var day2 = GlobalBalanceRow();
        day2["RunDate"] = new DateTime(2026, 10, 1);

        Assert.Equal(OilXRowId.Compute(GlobalBalance, day1), OilXRowId.Compute(GlobalBalance, day2));
    }

    /// <summary>
    /// The feed id prefixes the canonical form, so two feeds whose keys render alike
    /// cannot collide.
    /// </summary>
    [Fact]
    public void Different_feeds_with_identical_key_text_get_different_RowIds()
    {
        var regional = TestHelpers.Feed(OilXDescriptors.RegionalBalance);

        // GlobalBalance and RegionalBalance declare the SAME four key columns.
        Assert.Equal(GlobalBalance.KeyColumns, regional.KeyColumns);

        var values = GlobalBalanceRow();

        Assert.NotEqual(OilXRowId.Compute(GlobalBalance, values), OilXRowId.Compute(regional, values));
    }

    /// <summary>
    /// ⚠ An empty key component is a legitimate identity (Flow's subcountries usually
    /// are), but it must be distinguishable from a MISSING one.
    /// </summary>
    [Fact]
    public void Empty_and_null_key_components_hash_differently()
    {
        var flow = TestHelpers.Feed(OilXDescriptors.Flows);

        var empty = Values(
            ("OriginCountryName", "Albania"), ("DestinationCountryName", "Bahamas"),
            ("GroupByDateIndicator", "Exports"), ("OriginSubCountry", ""),
            ("DestinationSubCountry", ""), ("ReferenceDate", new DateTime(2015, 12, 1)),
            ("GradeName", "Albania Crude Oil"));

        var nulls = new Dictionary<string, object?>(empty, StringComparer.Ordinal)
        {
            ["OriginSubCountry"] = null,
            ["DestinationSubCountry"] = null
        };

        Assert.NotEqual(OilXRowId.Compute(flow, empty), OilXRowId.Compute(flow, nulls));
    }

    /// <summary>
    /// ⚠ The separator is what stops <c>("ab","c")</c> and <c>("a","bc")</c> hashing
    /// alike. Flow's seven mostly-text key columns make that a live risk, not a textbook
    /// one.
    /// </summary>
    [Fact]
    public void Field_boundaries_are_preserved_by_the_separator()
    {
        var flow = TestHelpers.Feed(OilXDescriptors.Flows);

        Dictionary<string, object?> Row(string origin, string destination) => Values(
            ("OriginCountryName", origin), ("DestinationCountryName", destination),
            ("GroupByDateIndicator", "Exports"), ("OriginSubCountry", ""),
            ("DestinationSubCountry", ""), ("ReferenceDate", new DateTime(2015, 12, 1)),
            ("GradeName", "Grade"));

        Assert.NotEqual(
            OilXRowId.Compute(flow, Row("ab", "c")),
            OilXRowId.Compute(flow, Row("a", "bc")));
    }

    [Fact]
    public void Canonical_form_lists_the_feed_then_the_key_columns_in_order()
    {
        var canonical = OilXRowId.Canonical(GlobalBalance, GlobalBalanceRow());
        var parts = canonical.Split(OilXCanonical.Separator);

        Assert.Equal(OilXDescriptors.GlobalBalance, parts[0]);
        Assert.Equal("OPEC", parts[1]);
        Assert.Equal("2010-01-01 00:00:00.0000000", parts[2]);
        Assert.Equal("Crude Production", parts[3]);
        Assert.Equal("KBD", parts[4]);

        // ObservedValue is a VALUE column and must not appear.
        Assert.DoesNotContain("24534", canonical);
    }

    // ---------------------------------------------------------------- Checksum

    [Fact]
    public void Checksum_is_deterministic_across_calls()
    {
        Assert.Equal(
            OilXChecksum.Compute(GlobalBalance, GlobalBalanceRow()),
            OilXChecksum.Compute(GlobalBalance, GlobalBalanceRow()));
    }

    [Fact]
    public void Checksum_changes_when_a_value_changes()
    {
        Assert.NotEqual(
            OilXChecksum.Compute(GlobalBalance, GlobalBalanceRow(observed: 24534m)),
            OilXChecksum.Compute(GlobalBalance, GlobalBalanceRow(observed: 24535m)));
    }

    /// <summary>
    /// ⚠ Key columns are excluded: they are identical on both sides of every MATCHED
    /// comparison, so hashing them adds nothing. (A key change produces a different
    /// RowId and therefore a different row entirely.)
    /// </summary>
    [Fact]
    public void Checksum_ignores_the_key_columns()
    {
        var changed = GlobalBalanceRow();
        changed["GroupName"] = "NON-OPEC";

        Assert.Equal(
            OilXChecksum.Compute(GlobalBalance, GlobalBalanceRow()),
            OilXChecksum.Compute(GlobalBalance, changed));
    }

    /// <summary>
    /// ⚠ FileName is PROVENANCE. A later snapshot that changed nothing must not count as
    /// a change, or the guard would fire four times a day on every row and ModifiedAtUtc
    /// would mean "time of last run" again.
    /// </summary>
    [Fact]
    public void Checksum_ignores_FileName_and_RunDate()
    {
        var feed = GlobalBalance;

        Assert.DoesNotContain(feed.Columns.Where(OilXChecksum.IsValueColumn),
            c => c.Name is "FileName" or "Checksum" or "RowId" or "RunDate");

        var other = GlobalBalanceRow();
        other["RunDate"] = new DateTime(2026, 10, 1);
        other["FileName"] = "GlobalBalance.2026-10-01T09-55.csv";

        Assert.Equal(
            OilXChecksum.Compute(feed, GlobalBalanceRow()),
            OilXChecksum.Compute(feed, other));
    }

    /// <summary>
    /// ⚠ decimal preserves trailing zeros in its scale, so 0.5m and 0.50m are equal
    /// numbers with different default renderings. Without the fixed F8 normalisation the
    /// loader would report a phantom change on a row whose value never moved — and the
    /// feeds really do publish varying scales ("195", "5.40", "11.0").
    /// </summary>
    [Fact]
    public void Equal_decimals_of_different_scale_hash_alike()
    {
        Assert.Equal(
            OilXChecksum.Compute(GlobalBalance, GlobalBalanceRow(observed: 0.5m)),
            OilXChecksum.Compute(GlobalBalance, GlobalBalanceRow(observed: 0.50m)));
    }

    [Fact]
    public void A_cleared_value_is_distinguishable_from_an_empty_one()
    {
        var supplyDemand = TestHelpers.Feed(OilXDescriptors.SupplyDemand);

        Dictionary<string, object?> Row(object? countryName) => Values(
            ("CountryISOCode", "AF"), ("ReferenceDate", new DateTime(2010, 1, 1)),
            ("FlowBreakdown", "CLOSTLV"), ("CountryName", countryName),
            ("UnitMeasure", "KBBL"), ("ObservedValue", 0m), ("GeneralizedSource", "OilX"));

        Assert.NotEqual(
            OilXChecksum.Compute(supplyDemand, Row(null)),
            OilXChecksum.Compute(supplyDemand, Row("")));
    }

    /// <summary>
    /// Every feed must have at least one value column, or its checksum would be constant
    /// and the guard would never let an update through.
    /// </summary>
    [Fact]
    public void Every_feed_has_at_least_one_value_column()
    {
        foreach (var feed in OilXDescriptors.All)
            Assert.True(
                feed.Columns.Any(OilXChecksum.IsValueColumn),
                $"{feed.FeedId} has no value columns — its Checksum would never change.");
    }
}
