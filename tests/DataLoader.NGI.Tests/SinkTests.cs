using System.Data;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DataLoader.NGI.Tests;

/// <summary>
/// *** THE MOST VALUABLE TEST IN THIS PROJECT. ***
///
/// <para>The two sinks' <c>BuildTable</c> DataTables are the C# side of the load-bearing TVP
/// contract. A TVP binds <b>BY POSITION</b>, so a column NAME / ORDER / TYPE drift between
/// <c>BuildTable</c> and <c>sql/NGI/002_CreateNgiTvpTypes.sql</c> produces <b>no compile error, no
/// SQL error and no warning</b> - it just writes every value into the wrong column of every loaded
/// row. This test is the only thing that catches it.</para>
///
/// <para>The expected arrays below are transcribed <b>literally</b> from
/// <c>sql/NGI/002_CreateNgiTvpTypes.sql</c>:</para>
/// <code>
/// arm.BidWeekLocationTvp - 3 columns:
///     1. FileLogId    INT           NULL
///     2. PointCode    VARCHAR(20)   NOT NULL   &lt;- merge key (the JSON object VALUE)
///     3. LocationName VARCHAR(100)  NULL       &lt;- the JSON object KEY
///
/// arm.BidWeekDataTvp - 12 columns:
///     1. FileLogId    INT           NULL
///     2. IssueDate    DATE          NOT NULL   &lt;- merge key part 1
///     3. PointCode    VARCHAR(20)   NOT NULL   &lt;- merge key part 2
///     4. SurveyStart  DATE          NULL
///     5. SurveyEnd    DATE          NULL
///     6. Region       VARCHAR(64)   NULL
///     7. PricingPoint VARCHAR(100)  NULL
///     8. [Low]        DECIMAL(13,6) NULL
///     9. [High]       DECIMAL(13,6) NULL
///    10. [Average]    DECIMAL(13,6) NULL
///    11. [Volume]     INT           NULL
///    12. Deals        INT           NULL
/// </code>
///
/// <para><c>BuildTable</c> is pure (no DB) but <c>protected</c> on a <c>sealed</c> type, so it is
/// invoked by reflection - the established CWG/AGSI/IHS/IIR posture. Production accessibility is
/// NOT widened to suit a test.</para>
/// </summary>
public class SinkTests
{
    // ---- reflection helpers (copied from the AGSI/IIR test projects) ---------------------------

    private static DataTable BuildTable(object sink, object rows)
    {
        var method = sink.GetType().GetMethod("BuildTable", BindingFlags.Instance | BindingFlags.NonPublic)
                     ?? throw new InvalidOperationException("BuildTable not found");
        return (DataTable)method.Invoke(sink, new[] { rows })!;
    }

    private static string ProtectedString(object sink, string propertyName)
    {
        var type = sink.GetType();
        while (type is not null)
        {
            var prop = type.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.NonPublic);
            if (prop is not null) return (string)prop.GetValue(sink)!;
            type = type.BaseType;
        }
        throw new InvalidOperationException(propertyName + " not found");
    }

    private static IOptions<NgiSettings> Opt() =>
        Options.Create(new NgiSettings { ConnectionString = "unused-in-tests" });

    private static string[] Names(DataTable t) => t.Columns.Cast<DataColumn>().Select(c => c.ColumnName).ToArray();
    private static Type[] Types(DataTable t) => t.Columns.Cast<DataColumn>().Select(c => c.DataType).ToArray();

    private static BidWeekLocationSqlSink LocationSink() =>
        new(Opt(), NullLogger<BidWeekLocationSqlSink>.Instance);

    private static BidWeekDataSqlSink DataSink() =>
        new(Opt(), NullLogger<BidWeekDataSqlSink>.Instance);

    private static BidWeekDataRow FullDataRow(string pointCode = "STXAGUAD", DateOnly? issueDate = null, decimal? low = 2.360m) => new()
    {
        FileLogId = 4242,
        IssueDate = issueDate ?? new DateOnly(2026, 8, 1),
        PointCode = pointCode,
        SurveyStart = new DateOnly(2026, 7, 27),
        SurveyEnd = new DateOnly(2026, 7, 29),
        Region = "South Texas",
        PricingPoint = "Agua Dulce",
        Low = low,
        High = 2.390m,
        Average = 2.375m,
        Volume = 240,
        Deals = 15
    };

    // =========================================================== arm.BidWeekLocationTvp (3 columns)

    [Fact]
    public void LocationSink_BuildTable_MatchesTvp_NameOrderType_ThreeColumns_FileLogIdFirst()
    {
        var t = BuildTable(LocationSink(), new List<BidWeekLocationRow>
        {
            new() { FileLogId = 7, PointCode = "STXAGUAD", LocationName = "Agua Dulce" }
        });

        // Transcribed literally from sql/NGI/002: (FileLogId, PointCode, LocationName).
        Assert.Equal(new[] { "FileLogId", "PointCode", "LocationName" }, Names(t));
        Assert.Equal(new[] { typeof(int), typeof(string), typeof(string) }, Types(t));
        Assert.Equal(3, t.Columns.Count);
        Assert.Equal("FileLogId", Names(t)[0]); // FileLogId is FIRST in BOTH NGI TVPs

        var r = t.Rows[0];
        Assert.Equal(7, r["FileLogId"]);
        Assert.Equal("STXAGUAD", r["PointCode"]);   // the JSON object VALUE
        Assert.Equal("Agua Dulce", r["LocationName"]); // the JSON object KEY
    }

    [Fact]
    public void LocationSink_BuildTable_HasNoDateCreated_NoModifiedAtUtc_NoScalarColumn()
    {
        var t = BuildTable(LocationSink(), new List<BidWeekLocationRow>
        {
            new() { FileLogId = 7, PointCode = "STXAGUAD", LocationName = "Agua Dulce" }
        });

        // Never cross a TVP: DateCreated is the table DEFAULT, ModifiedAtUtc is stamped by the MERGE.
        Assert.DoesNotContain("DateCreated", Names(t));
        Assert.DoesNotContain("ModifiedAtUtc", Names(t));
        // No computed column exists for this loader (the locations feed carries no coordinates), and
        // arm.usp_BulkMergeBidWeekLocation takes NO scalar alongside its TVP (no RunDate/IssueDate).
        Assert.DoesNotContain("RunDate", Names(t));
        Assert.DoesNotContain("Id", Names(t));
    }

    [Fact]
    public void LocationSink_BlankLocationName_IsDbNull_KeyStillPresent()
    {
        var t = BuildTable(LocationSink(), new List<BidWeekLocationRow>
        {
            new() { FileLogId = 7, PointCode = "STXAGUAD", LocationName = null }
        });

        var r = t.Rows[0];
        Assert.Equal(DBNull.Value, r["LocationName"]); // NULLable payload column
        Assert.Equal("STXAGUAD", r["PointCode"]);      // NOT NULL merge key survives
    }

    [Fact]
    public void LocationSink_BuildTable_DedupsOnPointCode_CaseInsensitive_LastWins()
    {
        var t = BuildTable(LocationSink(), new List<BidWeekLocationRow>
        {
            new() { FileLogId = 1, PointCode = "STXAGUAD", LocationName = "Old Name" },
            new() { FileLogId = 2, PointCode = "stxaguad", LocationName = "Agua Dulce" }
        });

        var kept = Assert.Single(t.Rows.Cast<DataRow>()); // one merge key -> one TVP row
        Assert.Equal("Agua Dulce", kept["LocationName"]); // last wins, matching the proc's ROW_NUMBER dedup
    }

    [Fact]
    public void LocationSink_TargetsExpectedProcAndTvpType()
    {
        var sink = LocationSink();
        Assert.Equal("arm.usp_BulkMergeBidWeekLocation", ProtectedString(sink, "StoredProcedureName"));
        Assert.Equal("arm.BidWeekLocationTvp", ProtectedString(sink, "TableValuedParameterType"));
    }

    // =========================================================== arm.BidWeekDataTvp (12 columns)

    [Fact]
    public void DataSink_BuildTable_MatchesTvp_NameOrderType_TwelveColumns_FileLogIdFirst()
    {
        var t = BuildTable(DataSink(), new List<BidWeekDataRow> { FullDataRow() });

        // Transcribed literally from sql/NGI/002 - order is the contract.
        Assert.Equal(new[]
        {
            "FileLogId", "IssueDate", "PointCode", "SurveyStart", "SurveyEnd", "Region",
            "PricingPoint", "Low", "High", "Average", "Volume", "Deals"
        }, Names(t));

        Assert.Equal(new[]
        {
            typeof(int),      // FileLogId    INT
            typeof(DateTime), // IssueDate    DATE
            typeof(string),   // PointCode    VARCHAR(20)
            typeof(DateTime), // SurveyStart  DATE
            typeof(DateTime), // SurveyEnd    DATE
            typeof(string),   // Region       VARCHAR(64)
            typeof(string),   // PricingPoint VARCHAR(100)
            typeof(decimal),  // Low          DECIMAL(13,6)
            typeof(decimal),  // High         DECIMAL(13,6)
            typeof(decimal),  // Average      DECIMAL(13,6)
            typeof(int),      // Volume       INT
            typeof(int)       // Deals        INT
        }, Types(t));

        Assert.Equal(12, t.Columns.Count);
        Assert.Equal("FileLogId", Names(t)[0]);  // FileLogId FIRST (NGI does not copy AGSI's split rule)
        Assert.Equal("IssueDate", Names(t)[1]);  // merge key part 1
        Assert.Equal("PointCode", Names(t)[2]);  // merge key part 2

        var r = t.Rows[0];
        Assert.Equal(4242, r["FileLogId"]);
        Assert.Equal(new DateTime(2026, 8, 1), r["IssueDate"]);   // DateOnly -> midnight DateTime
        Assert.Equal("STXAGUAD", r["PointCode"]);
        Assert.Equal(new DateTime(2026, 7, 27), r["SurveyStart"]);
        Assert.Equal(new DateTime(2026, 7, 29), r["SurveyEnd"]);
        Assert.Equal("South Texas", r["Region"]);
        Assert.Equal("Agua Dulce", r["PricingPoint"]);
        Assert.Equal(2.360m, r["Low"]);
        Assert.Equal(2.390m, r["High"]);
        Assert.Equal(2.375m, r["Average"]);
        Assert.Equal(240, r["Volume"]);
        Assert.Equal(15, r["Deals"]);
    }

    [Fact]
    public void DataSink_BuildTable_HasNoDateCreated_NoModifiedAtUtc_NoComputedOrScalarColumn()
    {
        var t = BuildTable(DataSink(), new List<BidWeekDataRow> { FullDataRow() });

        Assert.DoesNotContain("DateCreated", Names(t));    // table DEFAULT GETDATE()
        Assert.DoesNotContain("ModifiedAtUtc", Names(t));  // stamped by the MERGE proc
        // NGI has no computed column at all (no GEOGRAPHY - contrast IIR's PlantPoint) and neither
        // merge proc takes a scalar next to its TVP: IssueDate is per-row because it is a key, and
        // the run date is not persisted by this loader.
        Assert.DoesNotContain("RunDate", Names(t));
        Assert.DoesNotContain("PlantPoint", Names(t));
        Assert.DoesNotContain("Id", Names(t));
    }

    [Fact]
    public void DataSink_NullMeasuresAndStrings_AreDbNull_KeysStillPresent()
    {
        var row = new BidWeekDataRow
        {
            FileLogId = 4242,
            IssueDate = new DateOnly(2026, 8, 1),
            PointCode = "ETXCARTH",
            SurveyStart = null,
            SurveyEnd = null,
            Region = null,
            PricingPoint = null,
            Low = null,
            High = null,
            Average = null,
            Volume = null,
            Deals = null
        };

        var t = BuildTable(DataSink(), new List<BidWeekDataRow> { row });

        var r = t.Rows[0];
        Assert.Equal(DBNull.Value, r["SurveyStart"]);
        Assert.Equal(DBNull.Value, r["SurveyEnd"]);
        Assert.Equal(DBNull.Value, r["Region"]);
        Assert.Equal(DBNull.Value, r["PricingPoint"]);
        Assert.Equal(DBNull.Value, r["Low"]);
        Assert.Equal(DBNull.Value, r["High"]);
        Assert.Equal(DBNull.Value, r["Average"]);
        Assert.Equal(DBNull.Value, r["Volume"]);
        Assert.Equal(DBNull.Value, r["Deals"]);
        // The NOT NULL merge key columns still carry values.
        Assert.Equal(new DateTime(2026, 8, 1), r["IssueDate"]);
        Assert.Equal("ETXCARTH", r["PointCode"]);
    }

    [Fact]
    public void DataSink_NegativePrice_SurvivesTheTvp_Unrounded()
    {
        // Negative gas prices are real (Waha has printed them) - no non-negative CHECK anywhere.
        var t = BuildTable(DataSink(), new List<BidWeekDataRow> { FullDataRow(low: -1.125m) });
        Assert.Equal(-1.125m, t.Rows[0]["Low"]);
    }

    [Fact]
    public void DataSink_BuildTable_DedupsOnIssueDateAndPointCode_LastWins()
    {
        var t = BuildTable(DataSink(), new List<BidWeekDataRow>
        {
            FullDataRow(low: 1.111m),
            FullDataRow(low: 2.222m)
        });

        var kept = Assert.Single(t.Rows.Cast<DataRow>());
        Assert.Equal(2.222m, kept["Low"]);
    }

    [Fact]
    public void DataSink_BuildTable_SameIssueDate_DifferentPointCode_KeptAsTwo()
    {
        var t = BuildTable(DataSink(), new List<BidWeekDataRow>
        {
            FullDataRow("STXAGUAD"),
            FullDataRow("STXNGPL")
        });

        Assert.Equal(2, t.Rows.Count);
        Assert.Equal(new[] { "STXAGUAD", "STXNGPL" },
            t.Rows.Cast<DataRow>().Select(r => (string)r["PointCode"]).OrderBy(x => x).ToArray());
    }

    [Fact]
    public void DataSink_BuildTable_SamePointCode_DifferentIssueDate_KeptAsTwo()
    {
        var t = BuildTable(DataSink(), new List<BidWeekDataRow>
        {
            FullDataRow(issueDate: new DateOnly(2026, 7, 1)),
            FullDataRow(issueDate: new DateOnly(2026, 8, 1))
        });

        Assert.Equal(2, t.Rows.Count); // IssueDate is part of the merge key
    }

    [Fact]
    public void DataSink_TargetsExpectedProcAndTvpType()
    {
        var sink = DataSink();
        Assert.Equal("arm.usp_BulkMergeBidWeekData", ProtectedString(sink, "StoredProcedureName"));
        Assert.Equal("arm.BidWeekDataTvp", ProtectedString(sink, "TableValuedParameterType"));
    }

    // =========================================================== the whole live batch binds cleanly

    [Fact]
    public async Task DataSink_BuildTable_OverTheWholeLiveFixture_Produces163Rows_InTvpShape()
    {
        // End-to-end within the process: real fixture -> reader -> sink DataTable. Proves the 163
        // live records survive the TVP shape (163 distinct merge keys, no dedup collapse).
        var rows = await ReaderHarness.ReadDatafeedAsync(Samples.Datafeed20260801);

        var t = BuildTable(DataSink(), rows.ToList());

        Assert.Equal(Samples.ExpectedRecordCount, t.Rows.Count);
        Assert.Equal(12, t.Columns.Count);
        Assert.Equal("FileLogId", Names(t)[0]);
        Assert.All(t.Rows.Cast<DataRow>(), r => Assert.Equal(new DateTime(2026, 8, 1), r["IssueDate"]));
    }

    [Fact]
    public async Task LocationSink_BuildTable_OverTheWholeLiveFixture_Produces163Rows_CodeInPointCode()
    {
        var rows = await ReaderHarness.ReadLocationsAsync(Samples.Locations);

        var t = BuildTable(LocationSink(), rows.ToList());

        Assert.Equal(Samples.ExpectedLocationCount, t.Rows.Count);

        // DIRECTION, all the way through the sink: the code is in PointCode, the name in LocationName.
        var agua = t.Rows.Cast<DataRow>().Single(r => (string)r["PointCode"] == "STXAGUAD");
        Assert.Equal("Agua Dulce", agua["LocationName"]);
        Assert.DoesNotContain("Agua Dulce", t.Rows.Cast<DataRow>().Select(r => (string)r["PointCode"]));
    }
}
