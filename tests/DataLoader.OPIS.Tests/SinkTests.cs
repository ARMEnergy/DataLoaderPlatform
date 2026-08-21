using System.Data;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DataLoader.OPIS.Tests;

/// <summary>
/// The sink's <c>BuildTable</c> — the C# half of the load-bearing TVP contract.
/// Column NAME + ORDER + TYPE must match <c>arm.LPReportTvp</c> in
/// <c>sql/OPIS/002_CreateOpisTvpTypes.sql</c> EXACTLY: the TVP binds BY POSITION,
/// so a reorder corrupts every loaded row and raises nothing. The expected order
/// below is transcribed literally from that script.
///
/// <c>BuildTable</c> is protected on a sealed type, so it is invoked by reflection
/// (the CWG/AGSI/IHS/IIR posture) rather than widening production accessibility.
/// </summary>
public class SinkTests
{
    /// <summary>Column order transcribed from sql/OPIS/002_CreateOpisTvpTypes.sql.</summary>
    private static readonly string[] TvpColumns =
    {
        "FileLogId", "Mkt_Prod", "Date", "Timing", "Price",
        "Low", "High", "Avg", "Country", "Unit", "Freq", "SourceFileDate"
    };

    private static readonly Type[] TvpTypes =
    {
        typeof(int), typeof(string), typeof(DateTime), typeof(string), typeof(string),
        typeof(decimal), typeof(decimal), typeof(decimal), typeof(string), typeof(string), typeof(string),
        typeof(DateTime)
    };

    private static OpisLpReportSqlSink Sink() =>
        new(Options.Create(new OpisSettings { ConnectionString = "unused" }),
            NullLogger<OpisLpReportSqlSink>.Instance);

    private static DataTable BuildTable(object sink, IReadOnlyList<OpisLpReportRow> rows)
    {
        for (var t = sink.GetType(); t is not null; t = t.BaseType)
        {
            var m = t.GetMethod("BuildTable", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (m is not null) return (DataTable)m.Invoke(sink, new object[] { rows })!;
        }
        throw new InvalidOperationException("BuildTable not found");
    }

    private static string[] Names(DataTable t) => t.Columns.Cast<DataColumn>().Select(c => c.ColumnName).ToArray();
    private static Type[] Types(DataTable t) => t.Columns.Cast<DataColumn>().Select(c => c.DataType).ToArray();

    private static OpisLpReportRow Row(
        decimal? low = 85.8750m, decimal? high = 89.8750m, decimal? avg = 87.8750m,
        string? country = "US", string? unit = "GAL", string? freq = "D") => new()
        {
            FileLogId = 42,
            MktProd = "LOS ANGELES PRO",
            Date = new DateOnly(2026, 8, 20),
            Timing = "A",
            Price = "I",
            Low = low,
            High = high,
            Avg = avg,
            Country = country,
            Unit = unit,
            Freq = freq,
            SourceFileDate = new DateOnly(2026, 8, 20)
        };

    [Fact]
    public void BuildTable_MatchesTvpOrder_FileLogIdFirst()
    {
        var t = BuildTable(Sink(), new[] { Row() });

        Assert.Equal(TvpColumns, Names(t));
        Assert.Equal(TvpTypes, Types(t));
    }

    [Fact]
    public void BuildTable_OmitsDbStampedAndKeylessColumns()
    {
        var t = BuildTable(Sink(), new[] { Row() });

        // Stamped by the merge proc / table defaults — must never cross the TVP.
        Assert.False(t.Columns.Contains("DateCreated"));
        Assert.False(t.Columns.Contains("ModifiedAtUtc"));
        Assert.Equal(TvpColumns.Length, t.Columns.Count);
    }

    [Fact]
    public void BuildTable_MapsValues()
    {
        var t = BuildTable(Sink(), new[] { Row() });
        var r = t.Rows[0];

        Assert.Equal(42, r["FileLogId"]);
        Assert.Equal("LOS ANGELES PRO", r["Mkt_Prod"]);
        Assert.Equal(new DateTime(2026, 8, 20), r["Date"]);
        Assert.Equal("A", r["Timing"]);
        Assert.Equal("I", r["Price"]);
        Assert.Equal(85.8750m, r["Low"]);
        Assert.Equal(89.8750m, r["High"]);
        Assert.Equal(87.8750m, r["Avg"]);
        Assert.Equal("US", r["Country"]);
        Assert.Equal("GAL", r["Unit"]);
        Assert.Equal("D", r["Freq"]);
        Assert.Equal(new DateTime(2026, 8, 20), r["SourceFileDate"]);
    }

    [Fact]
    public void BuildTable_NullPricesAndCodes_BecomeDbNull()
    {
        // Basket rows carry only Avg; every nullable column must reach SQL as NULL,
        // not as a default 0 / empty string.
        var t = BuildTable(Sink(), new[] { Row(low: null, high: null, country: null, unit: null, freq: null) });
        var r = t.Rows[0];

        Assert.Equal(DBNull.Value, r["Low"]);
        Assert.Equal(DBNull.Value, r["High"]);
        Assert.Equal(DBNull.Value, r["Country"]);
        Assert.Equal(DBNull.Value, r["Unit"]);
        Assert.Equal(DBNull.Value, r["Freq"]);
        Assert.Equal(87.8750m, r["Avg"]);
    }

    [Fact]
    public void BuildTable_DateColumnsCarryNoTimeComponent()
    {
        // Both map to SQL DATE; a stray time would break equality in the MERGE ON clause.
        var t = BuildTable(Sink(), new[] { Row() });

        Assert.Equal(TimeSpan.Zero, ((DateTime)t.Rows[0]["Date"]).TimeOfDay);
        Assert.Equal(TimeSpan.Zero, ((DateTime)t.Rows[0]["SourceFileDate"]).TimeOfDay);
    }

    [Fact]
    public void SinkTargetsTheSingleFanOutProcAndTvp()
    {
        var sink = Sink();

        string Prop(string name)
        {
            for (var t = sink.GetType(); t is not null; t = t.BaseType)
            {
                var p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (p is not null) return (string)p.GetValue(sink)!;
            }
            throw new InvalidOperationException($"{name} not found");
        }

        // One proc fans the rows into arm.LPReportHistory AND arm.LPReport in one
        // transaction — see sql/OPIS/003.
        Assert.Equal("arm.usp_BulkMergeLPReport", Prop("StoredProcedureName"));
        Assert.Equal("arm.LPReportTvp", Prop("TableValuedParameterType"));
    }

    [Fact]
    public void BuildTable_KeepsBothRowsOfARevisionPair()
    {
        // BuildTable must not collapse the I/U pair — they are two distinct
        // arm.LPReportHistory rows. De-duplication belongs in the merge proc.
        var initial = Row();
        var updated = new OpisLpReportRow
        {
            FileLogId = 43,
            MktProd = initial.MktProd,
            Date = initial.Date,
            Timing = initial.Timing,
            Price = "U",
            Low = 81.25m,
            High = 82.00m,
            Avg = 81.625m,
            SourceFileDate = new DateOnly(2026, 8, 3)
        };

        var t = BuildTable(Sink(), new[] { initial, updated });

        Assert.Equal(2, t.Rows.Count);
        Assert.Equal("I", t.Rows[0]["Price"]);
        Assert.Equal("U", t.Rows[1]["Price"]);
    }
}
