using Xunit;

namespace DataLoader.OilX.Tests;

/// <summary>
/// The descriptor registry's own invariants — the things the rest of the loader assumes
/// without re-checking, each of which would otherwise fail far from its cause.
/// </summary>
public sealed class OilXDescriptorTests
{
    [Fact]
    public void Registry_self_check_passes()
    {
        OilXDescriptors.Validate();   // throws on any inconsistency
    }

    [Fact]
    public void All_eight_requested_feeds_are_registered()
    {
        Assert.Equal(8, OilXDescriptors.All.Count);

        foreach (var id in new[]
                 {
                     "CargoTracking", "FloatingStorageVessels", "Flows", "GlobalBalance",
                     "OilFieldsProduction", "Regional_Balance", "SupplyDemand", "Terminals"
                 })
            Assert.NotNull(OilXDescriptors.Find(id));
    }

    [Fact]
    public void Find_is_case_insensitive_and_null_for_unknown()
    {
        Assert.NotNull(OilXDescriptors.Find("cargotracking"));
        Assert.NotNull(OilXDescriptors.Find("REGIONAL_BALANCE"));
        Assert.Null(OilXDescriptors.Find("TerminalsWeekly"));
    }

    /// <summary>
    /// The feed id IS the <c>files=</c> query value, so it must be the vendor's own file
    /// name — underscore and all. Deriving it from the table name would send
    /// <c>RegionalBalance</c> and earn a 422.
    /// </summary>
    [Fact]
    public void Regional_Balance_keeps_the_vendor_spelling_while_its_table_does_not()
    {
        var feed = TestHelpers.Feed(OilXDescriptors.RegionalBalance);

        Assert.Equal("Regional_Balance", feed.FeedId);
        Assert.Equal("arm.RegionalBalance", feed.TargetTable);
        Assert.Equal("Regional_Balance.", feed.FileNamePrefix);
    }

    /// <summary>
    /// ⚠ Feed attribution is by file-name prefix and the trailing dot is what keeps
    /// <c>Terminals</c> from swallowing <c>TerminalsWeekly</c> — a real vendor feed this
    /// loader deliberately does not load.
    /// </summary>
    [Fact]
    public void Terminals_does_not_claim_TerminalsWeekly_files()
    {
        var terminals = TestHelpers.Feed(OilXDescriptors.Terminals);

        Assert.True(terminals.Owns("Terminals.2026-09-30T09-55.csv"));
        Assert.False(terminals.Owns("TerminalsWeekly.2026-09-30T09-55.csv"));
    }

    [Fact]
    public void Cargo_tracking_feed_does_not_claim_the_crude_revisions_feed()
    {
        var cargo = TestHelpers.Feed(OilXDescriptors.CargoTracking);

        Assert.True(cargo.Owns("CargoTracking.2026-09-30T03-39.csv"));
        Assert.False(cargo.Owns("CargoTrackingCrudeRevisions.2026-09-30T03-39.csv"));
    }

    [Fact]
    public void Every_feed_leads_with_the_primary_key_and_ends_with_provenance()
    {
        foreach (var feed in OilXDescriptors.All)
        {
            Assert.Equal("RunDate", feed.Columns[0].Name);
            Assert.Equal("RowId", feed.Columns[1].Name);
            Assert.Equal("FileName", feed.Columns[^2].Name);
            Assert.Equal("Checksum", feed.Columns[^1].Name);
        }
    }

    /// <summary>
    /// Exactly four columns are NOT NULL in every TVP. Making a key column required would
    /// drop the rows whose key is legitimately empty.
    /// </summary>
    [Fact]
    public void Only_the_four_structural_columns_are_required()
    {
        foreach (var feed in OilXDescriptors.All)
        {
            var required = feed.Columns.Where(c => c.Required).Select(c => c.Name)
                               .OrderBy(n => n, StringComparer.Ordinal).ToList();

            Assert.Equal(new[] { "Checksum", "FileName", "RowId", "RunDate" }, required);
        }
    }

    [Fact]
    public void No_key_column_is_required()
    {
        foreach (var feed in OilXDescriptors.All)
            foreach (var column in feed.Columns.Where(c => c.IsKey))
                Assert.False(column.Required,
                    $"{feed.FeedId}.{column.Name} is a key column and must not be Required.");
    }

    /// <summary>
    /// ⚠ The measured key. Dropping either subcountry column collapses 28,887 of Flow's
    /// 232,311 rows onto shared keys — they are usually empty, which is exactly what
    /// makes them look droppable.
    /// </summary>
    [Fact]
    public void Flow_keys_on_both_subcountry_columns()
    {
        var flow = TestHelpers.Feed(OilXDescriptors.Flows);

        Assert.Equal(
            new[]
            {
                "OriginCountryName", "DestinationCountryName", "GroupByDateIndicator",
                "OriginSubCountry", "DestinationSubCountry", "ReferenceDate", "GradeName"
            },
            flow.KeyColumns);
    }

    /// <summary>The vendor spells it with a lower-case b; the column does not.</summary>
    [Fact]
    public void Flow_maps_the_vendors_lowercase_b_spelling()
    {
        var column = TestHelpers.Feed(OilXDescriptors.Flows).Columns
            .Single(c => c.Name == "GroupByDateIndicator");

        Assert.Contains("GroupbyDateIndicator", column.SourceHeaders);
    }

    /// <summary>
    /// ⚠ The CSV publishes KT BEFORE KBBL and spells both with parentheses, while the
    /// table lists KBBL first. Mapping is by header NAME, so the orders may differ safely
    /// — but only because of these explicit header strings.
    /// </summary>
    [Fact]
    public void CargoTracking_maps_the_parenthesised_quantity_headers()
    {
        var feed = TestHelpers.Feed(OilXDescriptors.CargoTracking);

        Assert.Contains("LoadQuantity(KBBL)",
            feed.Columns.Single(c => c.Name == "LoadQuantity_KBBL").SourceHeaders);
        Assert.Contains("LoadQuantity(KT)",
            feed.Columns.Single(c => c.Name == "LoadQuantity_KT").SourceHeaders);

        // The table's order, which is the reverse of the CSV's.
        Assert.True(feed.Ordinal("LoadQuantity_KBBL") < feed.Ordinal("LoadQuantity_KT"));
    }

    /// <summary>CargoTracking keys on the vendor's own sha256 row identity.</summary>
    [Fact]
    public void CargoTracking_keys_on_FlowID_alone()
    {
        var feed = TestHelpers.Feed(OilXDescriptors.CargoTracking);

        Assert.Equal(new[] { "FlowID" }, feed.KeyColumns);
        Assert.Equal("VARCHAR(64)", feed.Columns.Single(c => c.Name == "FlowID").SqlType);
    }

    [Fact]
    public void Every_tvp_proc_and_table_name_is_distinct()
    {
        Assert.Equal(8, OilXDescriptors.All.Select(f => f.TvpType).Distinct().Count());
        Assert.Equal(8, OilXDescriptors.All.Select(f => f.MergeProc).Distinct().Count());
        Assert.Equal(8, OilXDescriptors.All.Select(f => f.TargetTable).Distinct().Count());
    }

    /// <summary>Everything lands in [arm], never the DATABASE_DEVELOPER default [dbo].</summary>
    [Fact]
    public void Every_object_is_in_the_arm_schema()
    {
        foreach (var feed in OilXDescriptors.All)
        {
            Assert.StartsWith("arm.", feed.TargetTable, StringComparison.Ordinal);
            Assert.StartsWith("arm.", feed.TvpType, StringComparison.Ordinal);
            Assert.StartsWith("arm.", feed.MergeProc, StringComparison.Ordinal);
        }
    }

    /// <summary>The shipped defaults are the ones the loader was specified with.</summary>
    [Fact]
    public void Shipped_settings_match_the_specification()
    {
        var settings = new OilXSettings();

        Assert.Equal(30, settings.DaysBack);
        Assert.Equal(1, settings.SettledAfterDays);
        Assert.Equal(OilXHotKeyStrategy.RunDate, settings.HotKeyStrategy);
        Assert.Equal("SEE_DB", settings.ApiKey);
        Assert.Equal(8, settings.EnabledFeeds.Count);

        foreach (var id in settings.EnabledFeeds)
            Assert.NotNull(OilXDescriptors.Find(id));
    }
}
