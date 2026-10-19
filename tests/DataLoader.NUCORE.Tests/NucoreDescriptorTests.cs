using Xunit;

namespace DataLoader.NUCORE.Tests;

/// <summary>
/// The descriptor registry, and the vendor facts that were established empirically and must
/// not be quietly "tidied" later.
/// </summary>
public class NucoreDescriptorTests
{
    [Fact]
    public void RegistryValidates() => NucoreDescriptors.Validate();

    [Fact]
    public void AllFiveEndpointsArePresent()
    {
        Assert.Equal(5, NucoreDescriptors.All.Count);

        foreach (var name in new[]
                 {
                     NucoreDescriptors.VarResults, NucoreDescriptors.PositionSummary,
                     NucoreDescriptors.PnlExplain, NucoreDescriptors.PositionDetails,
                     NucoreDescriptors.Volatilities
                 })
            Assert.NotNull(NucoreDescriptors.Find(name));
    }

    [Fact]
    public void ColumnCountsMatchTheVendorSpec()
    {
        // Straight from the vendor's OpenAPI document. A change here means the vendor
        // changed shape and the generator needs re-running - it should not be edited to
        // match whatever the code now does.
        Assert.Equal(18,  NucoreDescriptors.Find("VarResults")!.Columns.Count);
        Assert.Equal(118, NucoreDescriptors.Find("PositionSummary")!.Columns.Count);
        Assert.Equal(120, NucoreDescriptors.Find("PnlExplain")!.Columns.Count);
        Assert.Equal(165, NucoreDescriptors.Find("PositionDetails")!.Columns.Count);
        Assert.Equal(15,  NucoreDescriptors.Find("Volatilities")!.Columns.Count);
    }

    /// <summary>
    /// ⚠ PnlExplain's date parameter is <c>CurDate</c>, NOT <c>SystemDate</c>.
    ///
    /// Asked with <c>systemDate</c> the endpoint returns HTTP 200 and the LATEST run for
    /// every date requested - verified live against 2026-10-14, 2025-10-15 and 2024-01-10,
    /// all of which came back as curDate 2026-10-15 with 11,376 rows. Nothing about the
    /// response says it ignored the parameter. If someone "fixes" this to SystemDate for
    /// consistency with its neighbours, every day of history silently loads as a copy of
    /// today.
    /// </summary>
    [Fact]
    public void PnlExplainUsesCurDateNotSystemDate()
    {
        var endpoint = NucoreDescriptors.Find("PnlExplain")!;
        Assert.Equal("CurDate", endpoint.DateParam);
        Assert.Equal("CurDate", endpoint.DateColumn);
    }

    /// <summary>
    /// ⚠ PositionDetails has no date parameter at all - the vendor's OpenAPI document lists
    /// only TradeMonthFrom, TradeMonthTo, TradeStatus, Page, PageSize and SortFields. Every
    /// date-ish name that was tried was accepted and ignored.
    /// </summary>
    [Fact]
    public void PositionDetailsHasNoDateParameterAndIsPartitionedByTradeMonth()
    {
        var endpoint = NucoreDescriptors.Find("PositionDetails")!;

        Assert.Null(endpoint.DateParam);
        Assert.Equal(NucorePartition.TradeMonth, endpoint.Partition);
        Assert.True(endpoint.TradeMonthColumnIndex >= 0);
    }

    [Fact]
    public void EveryOtherEndpointIsPartitionedByDateAndNamesItsParameter()
    {
        foreach (var endpoint in NucoreDescriptors.All)
        {
            if (endpoint.Name == "PositionDetails") continue;

            Assert.Equal(NucorePartition.Date, endpoint.Partition);
            Assert.False(string.IsNullOrWhiteSpace(endpoint.DateParam));
            Assert.True(endpoint.DateColumnIndex >= 0);
        }
    }

    /// <summary>
    /// The three endpoints with no trustworthy key are REPLACED, not merged.
    ///
    /// <para>
    /// ⚠ This is the regression guard for a production outage. PositionDetails originally
    /// merged on (SystemDate, TradeId, DetailId, TradeMonth), "verified unique" over 100,000
    /// rows that were the first page of a trade-ordered response and covered only 130
    /// distinct trades. Over five COMPLETE trade months (443,477 rows) that key has 45,151
    /// duplicates, and the load died on a primary key violation. PnlExplain's key failed the
    /// same way on 2025-10-15.
    /// </para>
    /// </summary>
    // The mode arrives as a string because NucoreWriteMode is internal and this test class
    // has to be public for xUnit to discover it.
    [Theory]
    [InlineData("PositionSummary", "ReplaceByDate",  new[] { "SystemDate" })]
    [InlineData("PnlExplain",      "ReplaceByDate",  new[] { "CurDate" })]
    [InlineData("PositionDetails", "ReplaceByScope", new[] { "TradeMonth", "SystemDate" })]
    public void KeylessEndpointsAreReplacedOverTheirScope(
        string endpointName, string expectedMode, string[] expectedScope)
    {
        var endpoint = NucoreDescriptors.Find(endpointName)!;

        Assert.Equal(expectedMode, endpoint.WriteMode.ToString());
        Assert.Empty(endpoint.KeyColumns);
        Assert.Equal(expectedScope, endpoint.ScopeColumns.ToArray());
        Assert.True(endpoint.IsReplaceMode);

        // Every scope column must resolve, or the generated DELETE would miss rows.
        Assert.All(endpoint.ScopeColumnIndexes, i => Assert.True(i >= 0));

        // The scope must contain the unit's own date, or one unit's write would delete
        // another unit's rows.
        Assert.Contains(endpoint.DateColumn, endpoint.ScopeColumns);
    }

    [Theory]
    [InlineData("VarResults",   new[] { "VarDate", "VarBucket", "LegalEntity", "ConfidenceLevelId", "HoldingPeriod", "TradeBook", "TradeMonth", "ProductSpec" })]
    [InlineData("Volatilities", new[] { "SystemDate", "ProductSpec", "InstrumentType", "StartTradeMonth", "EndTradeMonth", "PutCallFlag", "StrikeType", "StrikePrice" })]
    public void OnlyKeysVerifiedOverCompletePopulationsSurvive(string endpointName, string[] expected)
    {
        // Re-verified over COMPLETE days, not samples:
        //   VarResults    7 days: 9,378 / 9,684 / 9,666 / 10,350 / 10,170 / 6,714 / 6,948
        //   Volatilities  5 days: 147,908 / 147,770 / 144,878 / 139,860 / 132,588
        var endpoint = NucoreDescriptors.Find(endpointName)!;

        Assert.Equal(NucoreWriteMode.Merge, endpoint.WriteMode);
        Assert.Equal(expected, endpoint.KeyColumns.Select(c => c.Name).ToArray());
        Assert.Empty(endpoint.ScopeColumns);
    }

    [Fact]
    public void ExactlyTwoEndpointsStillMerge()
    {
        // A bare list, so quietly turning another endpoint back into a merge has to be a
        // deliberate edit to this test rather than a silent change.
        var merging = NucoreDescriptors.All
            .Where(e => e.WriteMode == NucoreWriteMode.Merge)
            .Select(e => e.Name)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[] { "VarResults", "Volatilities" }, merging);
    }

    /// <summary>
    /// id and guid are 0 and all-zeros on every row of every endpoint, so they must never be
    /// mistaken for identifiers.
    /// </summary>
    [Fact]
    public void VendorSurrogateColumnsAreNeverPartOfAKey()
    {
        foreach (var endpoint in NucoreDescriptors.All)
            foreach (var name in new[] { "Id", "Guid", "RecordState", "EntityName" })
                Assert.DoesNotContain(endpoint.KeyColumns, c => c.Name == name);
    }

    [Fact]
    public void KeyColumnsAreNeverNullable()
    {
        // A nullable key column breaks both the PRIMARY KEY and the MERGE join: "t.c = s.c"
        // is UNKNOWN when both are NULL, so the row never matches and is re-inserted on
        // every run.
        foreach (var endpoint in NucoreDescriptors.All)
            foreach (var key in endpoint.KeyColumns)
                Assert.False(key.Nullable, $"{endpoint.Name}.{key.Name} is a nullable key column.");
    }

    [Fact]
    public void NaturalKeysFitSqlServersIndexKeyLimit()
    {
        // SQL Server refuses an index key wider than 900 bytes. The generator checks this
        // too; this is the second half of the two-sided guard, so widening a VARCHAR in one
        // place cannot silently produce a table that fails to create.
        foreach (var endpoint in NucoreDescriptors.All)
        {
            if (endpoint.WriteMode != NucoreWriteMode.Merge) continue;

            var bytes = endpoint.KeyColumns.Sum(KeyByteCost);
            Assert.True(bytes <= 900,
                $"{endpoint.Name}: primary key is {bytes} bytes, over the 900-byte limit.");
        }
    }

    [Fact]
    public void OnlyTheThreeOffsetBearingStampsUseDateTimeOffset()
    {
        // The vendor sends an offset on createdAt / modifiedAt / originalModifiedAt only.
        // Every other date is a bare business date and must stay DATETIME2, or it shifts
        // across a day boundary for anyone not on UTC.
        var offsetColumns = NucoreDescriptors.All
            .SelectMany(e => e.Columns)
            .Where(c => c.Kind == NucoreColumnKind.DateTimeOffset)
            .Select(c => c.JsonName)
            .Distinct()
            .OrderBy(x => x)
            .ToArray();

        Assert.Equal(new[] { "createdAt", "modifiedAt", "originalModifiedAt" }, offsetColumns);
    }

    [Fact]
    public void EveryDecimalColumnUsesTheSameDeclaredShape()
    {
        var expected = $"DECIMAL({NucoreColumns.DecimalPrecision},{NucoreColumns.DecimalScale})";

        foreach (var column in NucoreDescriptors.All.SelectMany(e => e.Columns))
            if (column.Kind == NucoreColumnKind.Decimal)
                Assert.Equal(expected, column.SqlType);
    }

    [Fact]
    public void DecimalScaleCoversTheWidestObservedValue()
    {
        // The widest scale seen in live data is 10 (the option greeks: priorDelta
        // -0.9094652105, gamma 0.4483348049, ...). Dropping the declared scale below that
        // would round real risk numbers away without any error.
        Assert.True(NucoreColumns.DecimalScale >= 10,
            $"DecimalScale is {NucoreColumns.DecimalScale}; live data carries 10 decimal places.");
    }

    private static int KeyByteCost(NucoreColumn column) => column.Kind switch
    {
        NucoreColumnKind.String => column.MaxLength ?? 8000,
        NucoreColumnKind.Int32 => 4,
        NucoreColumnKind.Int64 => 8,
        NucoreColumnKind.Boolean => 1,
        NucoreColumnKind.DateTime => 8,
        NucoreColumnKind.DateTimeOffset => 10,
        NucoreColumnKind.Guid => 16,
        NucoreColumnKind.Decimal => 13,
        _ => 8000
    };
}
