using Xunit;

namespace DataLoader.EOX.Tests;

/// <summary>
/// The descriptor registry's own invariants. <see cref="EoxDescriptors.Validate"/>
/// runs at module startup too, so a bad edit fails before the loader touches the
/// network — but a failing build is better than a failing run.
/// </summary>
public sealed class DescriptorTests
{
    [Fact]
    public void The_registry_is_self_consistent()
    {
        Assert.Empty(EoxDescriptors.Validate());
    }

    [Fact]
    public void All_three_feeds_are_registered_and_findable_case_insensitively()
    {
        Assert.Equal(3, EoxDescriptors.All.Count);

        Assert.Same(EoxDescriptors.CrudeOil, EoxDescriptors.Find("crudeoil"));
        Assert.Same(EoxDescriptors.NaturalGas, EoxDescriptors.Find("NATURALGAS"));
        Assert.Same(EoxDescriptors.Ngl, EoxDescriptors.Find("ngl"));
        Assert.Null(EoxDescriptors.Find("Coal"));
    }

    /// <summary>
    /// The shipped <c>EnabledFeeds</c> default must name every feed — a typo here
    /// would silently skip a whole series while the run reported success.
    /// </summary>
    [Fact]
    public void The_default_EnabledFeeds_names_every_registered_feed()
    {
        var settings = new EoxSettings();

        Assert.Equal(
            EoxDescriptors.All.Select(f => f.FeedId).OrderBy(x => x),
            settings.EnabledFeeds.OrderBy(x => x));
    }

    /// <summary>
    /// Both header spellings EOX has published for the renamed columns must be
    /// listed, or a backfill silently rejects every file older than the rename.
    /// </summary>
    [Fact]
    public void The_renamed_columns_list_both_published_spellings()
    {
        foreach (var feed in EoxDescriptors.All)
        {
            var line = feed.Columns[feed.Ordinal("Line")];
            Assert.Contains("Line", line.SourceHeaders);
            Assert.Contains("Number", line.SourceHeaders);

            var code = feed.Columns[feed.Ordinal("Code")];
            Assert.Contains("Code", code.SourceHeaders);
            Assert.Contains("Data_Code", code.SourceHeaders);
        }
    }

    /// <summary>
    /// FP is the ONLY column allowed to be missing from a file's header. Marking
    /// anything else optional would turn a genuine contract break into silent NULLs.
    /// </summary>
    [Fact]
    public void Only_FP_is_header_optional()
    {
        var optional = EoxDescriptors.All
            .SelectMany(f => f.Columns.Where(c => c.HeaderOptional).Select(c => $"{f.FeedId}.{c.Name}"))
            .ToList();

        Assert.Equal(new[] { "NaturalGas.FP" }, optional);
    }

    /// <summary>
    /// The settings' file-name time token and the descriptors' prefixes together
    /// reproduce the real names on the drop, verified 2026-09-04.
    /// </summary>
    [Theory]
    [InlineData("CrudeOil", "EOD_CSV_C_20260904_1430.csv")]
    [InlineData("NaturalGas", "EOD_CSV_NG_20260904_1430.csv")]
    [InlineData("NGL", "EOD_CSV_NGL_20260904_1430.csv")]
    public void The_shipped_defaults_reproduce_real_file_names(string feedId, string expected)
    {
        var settings = new EoxSettings();

        Assert.Equal(expected, EoxDescriptors.Find(feedId)!.FileNameFor(new DateOnly(2026, 9, 4), settings.FileNameTimeToken));
    }

    /// <summary>
    /// The prefixes must be mutually unambiguous. <c>EOD_CSV_NG_</c> and
    /// <c>EOD_CSV_NGL_</c> nearly collide, which is exactly why file selection uses
    /// the full constructed name rather than a prefix match.
    /// </summary>
    [Fact]
    public void No_feeds_file_name_can_be_mistaken_for_anothers()
    {
        var date = new DateOnly(2026, 9, 4);
        var names = EoxDescriptors.All.Select(f => f.FileNameFor(date, "1430")).ToList();

        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        // The near-collision is real: NG's prefix IS a prefix of NGL's.
        Assert.StartsWith(EoxDescriptors.NaturalGas.FilePrefix.TrimEnd('_'), EoxDescriptors.Ngl.FilePrefix);
    }

    // ---- Each Validate() rule must actually fire. A self-check nobody has seen
    // ---- fail is a self-check nobody knows works.

    private static EoxFeedDescriptor Broken(Func<IReadOnlyList<EoxColumn>, IReadOnlyList<EoxColumn>> mutate) =>
        EoxDescriptors.CrudeOil with { Columns = mutate(EoxDescriptors.CrudeOil.Columns) };

    private static IReadOnlyList<EoxColumn> Replace(
        IReadOnlyList<EoxColumn> columns, string name, Func<EoxColumn, EoxColumn> change) =>
        columns.Select(c => c.Name == name ? change(c) : c).ToList();

    [Fact]
    public void Validate_rejects_a_primary_key_column_that_is_not_required()
    {
        // A non-required key column would let a blank be merged under a partial key.
        var broken = Broken(cols => Replace(cols, "TimeKey", c => c with { Required = false }));

        Assert.Contains(
            EoxDescriptors.Validate(new[] { broken }),
            p => p.Contains("TimeKey") && p.Contains("must be Required"));
    }

    [Fact]
    public void Validate_rejects_Required_combined_with_HeaderOptional()
    {
        // A missing header cannot supply a value a row is dropped without.
        var broken = Broken(cols => Replace(cols, "Mid", c => c with { Required = true, HeaderOptional = true }));

        Assert.Contains(EoxDescriptors.Validate(new[] { broken }), p => p.Contains("mutually exclusive"));
    }

    [Fact]
    public void Validate_rejects_a_column_with_no_source_at_all()
    {
        var broken = Broken(cols => Replace(cols, "Location", c => c with { SourceHeaders = Array.Empty<string>() }));

        Assert.Contains(EoxDescriptors.Validate(new[] { broken }), p => p.Contains("no source header"));
    }

    [Fact]
    public void Validate_rejects_a_db_stamped_column_in_a_tvp()
    {
        var broken = Broken(cols => cols.Append(
            EoxColumn.Dat("ModifiedAtUtc", false, "Whenever")).ToList());

        Assert.Contains(EoxDescriptors.Validate(new[] { broken }), p => p.Contains("DB-stamped"));
    }

    [Fact]
    public void Validate_rejects_a_feed_whose_last_column_is_not_the_FileName_guard()
    {
        var broken = Broken(cols => cols.Take(cols.Count - 1).ToList());   // drop FileName

        Assert.Contains(EoxDescriptors.Validate(new[] { broken }), p => p.Contains("ordering guard"));
    }

    [Fact]
    public void Validate_rejects_a_duplicated_column_name()
    {
        var broken = Broken(cols => cols.Append(EoxColumn.Str("TimeKey", 16, false, "Time_Key")).ToList());

        Assert.Contains(EoxDescriptors.Validate(new[] { broken }), p => p.Contains("duplicate column"));
    }
}
