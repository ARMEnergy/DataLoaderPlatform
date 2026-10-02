using Xunit;

namespace DataLoader.ICE.Tests;

/// <summary>
/// The three <c>Settlement_Reports/Environmentals</c> XLSX feeds.
///
/// <para>
/// Every fixture below mirrors the real files, which were parsed cell-by-cell on
/// 2026-09-24:
/// <list type="bullet">
///   <item><c>icecleared_physenv_2026_09_23.xlsx</c> — A:K, 1,439 data rows, header on row 1.</item>
///   <item><c>ngxphysical_env_2026_09_23.xlsx</c> — A:K, 264 data rows, header on row 1.</item>
///   <item><c>icecleared_physenvoptions_2026_09_22.xlsx</c> — A:M, 12,579 data rows, 365 blank-strike.</item>
/// </list>
/// </para>
/// <para>
/// The sample values are taken verbatim from those sheets, including the
/// round-trip noise and exponent forms that only the XLSX publication has.
/// </para>
/// </summary>
public sealed class PhysEnvTests
{
    /// <summary>The 11 headers of both futures reports, in file order (A:K).</summary>
    private static readonly string[] FuturesHeaders =
    {
        "TRADE DATE", "HUB", "PRODUCT", "STRIP", "CONTRACT", "CONTRACT TYPE",
        "STRIKE", "SETTLEMENT PRICE", "NET CHANGE", "EXPIRATION DATE", "PRODUCT_ID"
    };

    /// <summary>The 13 headers of the options report, in file order (A:M).</summary>
    private static readonly string[] OptionsHeaders =
    {
        "TRADE DATE", "HUB", "PRODUCT", "STRIP", "CONTRACT", "CONTRACT TYPE",
        "STRIKE", "SETTLEMENT PRICE", "NET CHANGE", "EXPIRATION DATE", "PRODUCT_ID",
        "OPTION_VOLATILITY", "DELTA_FACTOR"
    };

    private static (IceRow Row, int Dropped) ParseOne(string feedId, string[] headers, string?[] row)
    {
        var feed = IceDescriptors.Find(feedId)!;
        var xlsx = XlsxBuilder.BuildLikeIce(headers, new[] { row });
        var (rows, dropped) = TestHelpers.Reader(feed).Parse(xlsx, "https://downloads.ice.com/x.xlsx");
        return (rows.Single(), dropped);
    }

    // ------------------------------------------------------------ registration

    [Fact]
    public void All_three_feeds_are_registered_under_the_xlsx_report_directory()
    {
        foreach (var id in new[] { "IcePhysEnv", "NgxPhysEnv", "IcePhysEnvOptions" })
        {
            var feed = IceDescriptors.Find(id);
            Assert.NotNull(feed);
            Assert.Equal(IceFileFormat.Xlsx, feed!.Format);

            var path = feed.PathFor(new DateOnly(2026, 9, 23));
            Assert.StartsWith("Settlement_Reports/Environmentals/", path);
            Assert.EndsWith(".xlsx", path);

            // NOT the _CSV directory the two .dat env feeds use — a different
            // publication that lands in different tables.
            Assert.DoesNotContain("Settlement_Reports_CSV", path);
        }
    }

    [Theory]
    [InlineData("IcePhysEnv", "Settlement_Reports/Environmentals/icecleared_physenv_2026_09_23.xlsx")]
    [InlineData("NgxPhysEnv", "Settlement_Reports/Environmentals/ngxphysical_env_2026_09_23.xlsx")]
    [InlineData("IcePhysEnvOptions", "Settlement_Reports/Environmentals/icecleared_physenvoptions_2026_09_23.xlsx")]
    public void Path_matches_the_real_file_name(string feedId, string expected)
    {
        Assert.Equal(expected, IceDescriptors.Find(feedId)!.PathFor(new DateOnly(2026, 9, 23)));
    }

    /// <summary>
    /// The two futures feeds share one table, one TVP and one merge proc — that
    /// sharing is the reason SourceSystem exists.
    /// </summary>
    [Fact]
    public void The_two_futures_feeds_share_one_table()
    {
        var ice = IceDescriptors.Find("IcePhysEnv")!;
        var ngx = IceDescriptors.Find("NgxPhysEnv")!;

        Assert.Same(ice.Table, ngx.Table);
        Assert.Equal("arm.PhysEnvFutures", ice.Table.TableName);
        Assert.Equal("ICECleared", ice.SourceSystem);
        Assert.Equal("NGXPhysical", ngx.SourceSystem);
    }

    /// <summary>The options report has no NGX counterpart, so it carries no discriminator.</summary>
    [Fact]
    public void Options_table_has_no_source_system_column()
    {
        Assert.DoesNotContain(IceDescriptors.PhysEnvOptionsTable.Columns,
            c => c.Derived == IceDerived.SourceSystem);

        Assert.Null(IceDescriptors.Find("IcePhysEnvOptions")!.SourceSystem);
    }

    // ------------------------------------------------------------ SourceSystem

    [Theory]
    [InlineData("IcePhysEnv", "ICECleared")]
    [InlineData("NgxPhysEnv", "NGXPhysical")]
    public void Each_feed_stamps_its_own_source_system(string feedId, string expected)
    {
        // A real row: note the omitted STRIKE (index 6) — every futures row has one.
        var (row, _) = ParseOne(feedId, FuturesHeaders, new string?[]
        {
            "9/23/2026", "CCA", "CCA ACP Advance Futures", "Feb27", "ACA", "F",
            null, "0.3", "0", "2/26/2027", "22323"
        });

        Assert.Equal(expected, row.Value(IceDescriptors.PhysEnvFuturesTable, "SourceSystem"));
    }

    /// <summary>
    /// SourceSystem is loader-derived, so it must never be read out of the sheet —
    /// a column of that name in the file must be ignored rather than override the
    /// feed's identity, which is a primary-key value.
    /// </summary>
    [Fact]
    public void A_source_system_column_in_the_file_cannot_override_the_feed()
    {
        var headers = FuturesHeaders.Append("SourceSystem").ToArray();

        var feed = IceDescriptors.Find("NgxPhysEnv")!;
        var xlsx = XlsxBuilder.BuildLikeIce(headers, new[]
        {
            new string?[]
            {
                "9/23/2026", "AEO V25", "NGX AEO Futures", "9/1/2026", "AEO", "F",
                null, "27.66", "0", "10/30/2026", "29899", "ICECleared"
            }
        });

        var (rows, _) = TestHelpers.Reader(feed).Parse(xlsx, "https://downloads.ice.com/x.xlsx");

        Assert.Equal("NGXPhysical", rows.Single().Value(IceDescriptors.PhysEnvFuturesTable, "SourceSystem"));
    }

    /// <summary>
    /// A descriptor that pairs the shared table with a feed carrying no
    /// SourceSystem would write a blank into a PK column and silently merge the two
    /// processes together. It must fail on the first file instead.
    /// </summary>
    [Fact]
    public void Reader_throws_when_a_shared_table_feed_omits_its_source_system()
    {
        var misWired = new IceFeedDescriptor(
            "Broken", "mis-wired feed",
            "Settlement_Reports/Environmentals/icecleared_physenv_{date}.xlsx",
            "yyyy_MM_dd", IceFileFormat.Xlsx, IceDescriptors.PhysEnvFuturesTable);

        var xlsx = XlsxBuilder.BuildLikeIce(FuturesHeaders, new[]
        {
            new string?[] { "9/23/2026", "CCA", "P", "Feb27", "ACA", "F", null, "0.3", "0", "2/26/2027", "22323" }
        });

        var ex = Assert.Throws<InvalidOperationException>(
            () => TestHelpers.Reader(misWired).Parse(xlsx, "https://downloads.ice.com/x.xlsx"));

        Assert.Contains("SourceSystem", ex.Message);
    }

    [Fact]
    public void Source_system_is_a_required_non_header_column()
    {
        var column = IceDescriptors.PhysEnvFuturesTable.Columns
            .Single(c => c.Name == "SourceSystem");

        Assert.True(column.Required, "SourceSystem is a primary-key column.");
        Assert.Equal(IceDerived.SourceSystem, column.Derived);
        Assert.Empty(column.SourceHeaders);
        Assert.Equal("VARCHAR(20)", column.SqlType);
    }

    // ------------------------------------------------------------ column mapping

    /// <summary>
    /// ⚠ The sparse-cell hazard, live in these files: the real row 2 of both
    /// futures reports is <c>A,B,C,D,E,F,H,I,J,K</c> with <b>no G</b>, because
    /// STRIKE is blank on every futures row. A positional read would slide
    /// SETTLEMENT PRICE into STRIKE and shift the rest.
    /// </summary>
    [Fact]
    public void Omitted_strike_cell_does_not_shift_the_later_columns()
    {
        var table = IceDescriptors.PhysEnvFuturesTable;

        var (row, dropped) = ParseOne("IcePhysEnv", FuturesHeaders, new string?[]
        {
            "9/23/2026", "CCA", "CCA ACP Advance Futures", "Feb27", "ACA", "F",
            null, "0.3", "0", "2/26/2027", "22323"
        });

        Assert.Equal(0, dropped);
        Assert.Equal(new DateTime(2026, 9, 23), row.Value(table, "TradeDate"));
        Assert.Equal("ACA", row.Value(table, "Contract"));
        Assert.Equal("F", row.Value(table, "ContractType"));
        Assert.Equal("Feb27", row.Value(table, "Strip"));
        Assert.Equal(22323, row.Value(table, "ProductId"));
        Assert.Equal("CCA", row.Value(table, "Hub"));
        Assert.Equal("CCA ACP Advance Futures", row.Value(table, "Product"));
        Assert.Equal(DBNull.Value, row.Value(table, "Strike"));
        Assert.Equal(0.3m, row.Value(table, "SettlementPrice"));
        Assert.Equal(0m, row.Value(table, "NetChange"));
        Assert.Equal(new DateTime(2027, 2, 26), row.Value(table, "ExpirationDate"));
    }

    /// <summary>
    /// The two feeds disagree about what a strip looks like: ICE publishes month
    /// codes, dailies and spreads, NGX publishes dates. Both must survive, which is
    /// why Strip is VARCHAR and not DATE.
    /// </summary>
    [Theory]
    [InlineData("IcePhysEnv", "Apr27")]
    [InlineData("IcePhysEnv", "23 Sep 26")]
    [InlineData("IcePhysEnv", "Apr27 BH25")]
    [InlineData("NgxPhysEnv", "9/1/2026")]
    [InlineData("NgxPhysEnv", "10/1/2027")]
    public void Strip_is_kept_as_text_for_both_feeds(string feedId, string strip)
    {
        var (row, dropped) = ParseOne(feedId, FuturesHeaders, new string?[]
        {
            "9/23/2026", "CCA", "Some Product", strip, "ACA", "F",
            null, "1", "0", "2/26/2027", "22323"
        });

        Assert.Equal(0, dropped);
        Assert.Equal(strip, row.Value(IceDescriptors.PhysEnvFuturesTable, "Strip"));
    }

    /// <summary>Strip must stay a string column — a DATE would drop every ICE row.</summary>
    [Fact]
    public void Shared_table_strip_column_is_textual()
    {
        var strip = IceDescriptors.PhysEnvFuturesTable.Columns.Single(c => c.Name == "Strip");

        Assert.Equal(IceColumnType.String, strip.Type);
        Assert.Equal("VARCHAR(50)", strip.SqlType);
    }

    // ------------------------------------------------------------ options

    /// <summary>
    /// The options report's 'F' rows describe the underlying future and carry no
    /// STRIKE. Strike is a PK column, so those rows are dropped rather than merged
    /// under a blank key — 365 of 12,579 (2.9 %) on 2026-09-22.
    /// </summary>
    [Fact]
    public void Options_rows_without_a_strike_are_dropped()
    {
        var feed = IceDescriptors.Find("IcePhysEnvOptions")!;

        var xlsx = XlsxBuilder.BuildLikeIce(OptionsHeaders, new[]
        {
            // 'F' underlying-future row: no STRIKE, and no volatility/delta either.
            new string?[] { "9/22/2026", "CCA V26", "CCA Futures", "Apr27", "CB6", "F",
                            null, "32.14", "-0.28", "4/27/2027", "27756", null, null },
            // a real option row
            new string?[] { "9/22/2026", "CCA V26", "CCA Futures", "Apr27", "CB6", "C",
                            "30", "5.5", "-0.1", "4/27/2027", "27756", "64.0083", "0.62" }
        });

        var (rows, dropped) = TestHelpers.Reader(feed).Parse(xlsx, "https://downloads.ice.com/x.xlsx");

        Assert.Equal(1, dropped);
        var row = Assert.Single(rows);
        Assert.Equal(30m, row.Value(IceDescriptors.PhysEnvOptionsTable, "Strike"));
        Assert.Equal("C", row.Value(IceDescriptors.PhysEnvOptionsTable, "ContractType"));
        Assert.Equal(64.0083m, row.Value(IceDescriptors.PhysEnvOptionsTable, "OptionVolatility"));
        Assert.Equal(0.62m, row.Value(IceDescriptors.PhysEnvOptionsTable, "DeltaFactor"));
    }

    /// <summary>
    /// ⚠ XLSX-only hazard. These sheets store numbers as IEEE-754 doubles, so they
    /// carry round-trip noise the .dat twin never shows ('78.01000000000001' for
    /// 78.01) and write small values in exponent form ('-1E-05'). Both must parse:
    /// a failed conversion on STRIKE would DROP the row, since Strike is required.
    ///
    /// <para>
    /// The noise itself is harmless — DECIMAL(18,6) rounds it away, and rounding was
    /// verified to introduce no key collisions across the 12,214 strike-bearing
    /// rows of 2026-09-22.
    /// </para>
    /// </summary>
    [Fact]
    public void Float_noise_and_exponent_values_from_the_sheet_parse()
    {
        var table = IceDescriptors.PhysEnvOptionsTable;

        var (row, dropped) = ParseOne("IcePhysEnvOptions", OptionsHeaders, new string?[]
        {
            "9/22/2026", "EFO", "EUA Futures", "Dec26", "EFO", "C",
            "78.01000000000001",        // really 78.01
            "0.008999999999999999",     // really 0.009
            "-0.07000000000000001",     // really -0.07
            "1/15/2027", "22304",
            "64.00830000000001",        // really 64.0083
            "-1E-05"                    // exponent form
        });

        Assert.Equal(0, dropped);
        Assert.Equal(78.01000000000001m, row.Value(table, "Strike"));
        Assert.Equal(0.008999999999999999m, row.Value(table, "SettlementPrice"));
        Assert.Equal(-0.07000000000000001m, row.Value(table, "NetChange"));
        Assert.Equal(64.00830000000001m, row.Value(table, "OptionVolatility"));
        Assert.Equal(-0.00001m, row.Value(table, "DeltaFactor"));
    }

    /// <summary>
    /// Rounding to the TVP's scale is what SQL Server does on bind; this pins that
    /// it lands on the intended value rather than on a neighbour.
    /// </summary>
    [Theory]
    [InlineData("78.01000000000001", "78.010000")]
    [InlineData("0.008999999999999999", "0.009000")]
    [InlineData("-1E-05", "-0.000010")]
    [InlineData("64.00830000000001", "64.008300")]
    public void Sheet_noise_rounds_to_the_intended_value_at_scale_six(string raw, string expected)
    {
        Assert.True(IceConvert.TryConvert(IceColumnType.Decimal, raw, out var value));
        Assert.Equal(expected, Math.Round((decimal)value, 6).ToString("F6",
            System.Globalization.CultureInfo.InvariantCulture));
    }

    // ------------------------------------------------------------ SQL contract

    /// <summary>
    /// SourceSystem must be in the table's PRIMARY KEY. Without it the two feeds
    /// share a key space and whichever merges last wins — the exact silent
    /// overwrite the column was added to prevent.
    /// </summary>
    [Fact]
    public void Source_system_is_part_of_the_primary_key_in_the_schema_script()
    {
        var schema = File.ReadAllText(RepoPaths.SchemaScript);

        Assert.Contains("CONSTRAINT PK_ARM_PhysEnvFutures PRIMARY KEY CLUSTERED", schema);
        Assert.Contains("TradeDate, SourceSystem, Contract, ContractType, Strip", schema);
    }

    /// <summary>The merge must partition and match on SourceSystem too, or the de-dup collapses the two feeds.</summary>
    [Fact]
    public void Merge_procedure_keys_on_source_system()
    {
        var procs = File.ReadAllText(RepoPaths.ProceduresScript);
        var body = TvpParser.ProcedureBody(procs, "arm.usp_BulkMergePhysEnvFutures");

        Assert.Contains("PARTITION BY TradeDate, SourceSystem, Contract, ContractType, Strip", body);
        Assert.Contains("tgt.SourceSystem = s.SourceSystem", body);
    }
}
