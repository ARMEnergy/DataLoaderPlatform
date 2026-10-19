using System.Text.Json;
using Xunit;

namespace DataLoader.NUCORE.Tests;

/// <summary>
/// JSON-to-CLR conversion: the per-column rules that decide what actually lands in the
/// table.
/// </summary>
public class NucoreRowBuilderTests
{
    private static JsonElement Value(string json) =>
        JsonDocument.Parse($"{{\"v\":{json}}}").RootElement.GetProperty("v");

    private static NucoreColumn Column(
        NucoreColumnKind kind, string sqlType, bool nullable = true, bool isKey = false) =>
        new("Col", "col", sqlType, kind, nullable, isKey);

    // ----------------------------------------------------------------------- strings

    [Fact]
    public void TrailingPaddingIsTrimmed()
    {
        // VarResults.productGroup arrives space-padded to 100 characters, because the source
        // column is fixed-width CHAR. Untrimmed it never joins or compares to anything.
        var column = Column(NucoreColumnKind.String, "VARCHAR(400)");
        Assert.True(NucoreRowBuilder.TryConvert(Value("\"Electricity          \""), column, out var v, out _));
        Assert.Equal("Electricity", v);
    }

    [Fact]
    public void OverlongStringIsRejectedByNameRatherThanTruncated()
    {
        // SqlClient's own message is "String or binary data would be truncated" and names no
        // column, which turns a one-line fix into an afternoon.
        var column = Column(NucoreColumnKind.String, "VARCHAR(10)");
        Assert.False(NucoreRowBuilder.TryConvert(Value("\"0123456789abc\""), column, out _, out var reason));
        Assert.Contains("exceeds VARCHAR(10)", reason);
    }

    [Fact]
    public void BlankNullableStringBecomesNull()
    {
        var column = Column(NucoreColumnKind.String, "VARCHAR(50)");
        Assert.True(NucoreRowBuilder.TryConvert(Value("\"   \""), column, out var v, out _));
        Assert.Equal(DBNull.Value, v);
    }

    /// <summary>
    /// ⚠ A NULL in a KEY column collapses to '' rather than failing or staying NULL.
    ///
    /// Most of this vendor's dimension columns are declared nullable and several are
    /// genuinely empty - VarResults alone had 540 rows with a null productSpec in one day.
    /// A NULL key column would make the MERGE join UNKNOWN and re-insert the row on every
    /// single run; '' is a stable, intentional identity. This is also exactly how key
    /// uniqueness was verified against the live data.
    /// </summary>
    [Fact]
    public void NullInAKeyStringCollapsesToEmptyString()
    {
        var column = Column(NucoreColumnKind.String, "VARCHAR(100)", nullable: false, isKey: true);
        Assert.True(NucoreRowBuilder.TryConvert(Value("null"), column, out var v, out _));
        Assert.Equal(string.Empty, v);
    }

    [Fact]
    public void NullInANonNullableNonKeyColumnIsRejected()
    {
        var column = Column(NucoreColumnKind.Decimal, "DECIMAL(28,12)", nullable: false);
        Assert.False(NucoreRowBuilder.TryConvert(Value("null"), column, out _, out var reason));
        Assert.Contains("NOT NULL", reason);
    }

    // ------------------------------------------------------------------- date / time

    /// <summary>
    /// ⚠ A bare business date must be stored as written, with no zone adjustment. The
    /// vendor sends "2026-10-14T00:00:00"; parsing that as local-or-UTC and converting
    /// would move it to the 13th or 15th for anyone not on UTC - and these dates are
    /// primary key columns.
    /// </summary>
    [Fact]
    public void BareDateTimeKeepsItsWallClockValueAndIsUnspecified()
    {
        var column = Column(NucoreColumnKind.DateTime, "DATETIME2(7)");
        Assert.True(NucoreRowBuilder.TryConvert(Value("\"2026-10-14T00:00:00\""), column, out var v, out _));

        var dt = Assert.IsType<DateTime>(v);
        Assert.Equal(new DateTime(2026, 10, 14, 0, 0, 0), dt);
        Assert.Equal(DateTimeKind.Unspecified, dt.Kind);
    }

    [Fact]
    public void OffsetBearingStampKeepsItsOffset()
    {
        var column = Column(NucoreColumnKind.DateTimeOffset, "DATETIMEOFFSET(7)");
        Assert.True(NucoreRowBuilder.TryConvert(
            Value("\"2026-10-14T13:29:54.623437+00:00\""), column, out var v, out _));

        var dto = Assert.IsType<DateTimeOffset>(v);
        Assert.Equal(TimeSpan.Zero, dto.Offset);
        Assert.Equal(2026, dto.Year);
    }

    [Fact]
    public void TheVendorsZeroDateSentinelRoundTrips()
    {
        // originalModifiedAt is "0001-01-01T00:00:00+00:00" on most rows. It is a sentinel,
        // not a fault, and DATETIMEOFFSET stores it fine.
        var column = Column(NucoreColumnKind.DateTimeOffset, "DATETIMEOFFSET(7)");
        Assert.True(NucoreRowBuilder.TryConvert(
            Value("\"0001-01-01T00:00:00+00:00\""), column, out var v, out _));
        Assert.Equal(1, Assert.IsType<DateTimeOffset>(v).Year);
    }

    [Fact]
    public void UnparseableDateIsRejected()
    {
        var column = Column(NucoreColumnKind.DateTime, "DATETIME2(7)");
        Assert.False(NucoreRowBuilder.TryConvert(Value("\"not-a-date\""), column, out _, out var reason));
        Assert.Contains("unparseable", reason);
    }

    // ----------------------------------------------------------------------- numbers

    [Fact]
    public void DecimalIsRoundedToTheColumnScale()
    {
        var column = Column(NucoreColumnKind.Decimal, "DECIMAL(28,12)");
        Assert.True(NucoreRowBuilder.TryConvert(Value("0.1234567890123456789"), column, out var v, out _));
        Assert.Equal(0.123456789012m, (decimal)v!);
    }

    [Fact]
    public void TenDecimalPlaceGreeksSurviveUnchanged()
    {
        // Live values: priorDelta -0.9094652105, gamma 0.4483348049.
        var column = Column(NucoreColumnKind.Decimal, "DECIMAL(28,12)");
        Assert.True(NucoreRowBuilder.TryConvert(Value("-0.9094652105"), column, out var v, out _));
        Assert.Equal(-0.9094652105m, (decimal)v!);
    }

    [Fact]
    public void DecimalOverflowIsRejectedRatherThanClamped()
    {
        // A silently clamped P&L number is far worse than a failed row.
        var column = Column(NucoreColumnKind.Decimal, "DECIMAL(28,12)");
        Assert.False(NucoreRowBuilder.TryConvert(Value("1e30"), column, out _, out var reason));
        Assert.NotNull(reason);
    }

    [Fact]
    public void IntegersAcceptBothNumberAndStringForms()
    {
        var column = Column(NucoreColumnKind.Int32, "INT");
        Assert.True(NucoreRowBuilder.TryConvert(Value("42"), column, out var a, out _));
        Assert.Equal(42, a);

        // The vendor changed JSON value types from numbers to strings on another ARM feed
        // (EIA) between releases; accepting both costs nothing and survives that.
        Assert.True(NucoreRowBuilder.TryConvert(Value("\"42\""), column, out var b, out _));
        Assert.Equal(42, b);
    }

    [Fact]
    public void NonNumericValueInANumericColumnIsRejected()
    {
        var column = Column(NucoreColumnKind.Int32, "INT");
        Assert.False(NucoreRowBuilder.TryConvert(Value("\"abc\""), column, out _, out var reason));
        Assert.Contains("not an INT", reason);
    }

    [Fact]
    public void BooleansAcceptTrueFalseAndZeroOne()
    {
        var column = Column(NucoreColumnKind.Boolean, "BIT");
        Assert.True(NucoreRowBuilder.TryConvert(Value("true"), column, out var a, out _));
        Assert.Equal(true, a);
        Assert.True(NucoreRowBuilder.TryConvert(Value("0"), column, out var b, out _));
        Assert.Equal(false, b);
    }

    [Fact]
    public void GuidParsesTheAllZeroSentinel()
    {
        var column = Column(NucoreColumnKind.Guid, "UNIQUEIDENTIFIER");
        Assert.True(NucoreRowBuilder.TryConvert(
            Value("\"00000000-0000-0000-0000-000000000000\""), column, out var v, out _));
        Assert.Equal(Guid.Empty, v);
    }

    // ---------------------------------------------------------------- whole-row shape

    [Fact]
    public void BuiltRowHasExactlyOneValuePerColumnInDescriptorOrder()
    {
        var endpoint = NucoreDescriptors.Find("Volatilities")!;
        var builder = new NucoreRowBuilder(endpoint);

        var row = builder.TryBuild(SampleVolatilitiesRow(), out var error);

        Assert.Null(error);
        Assert.NotNull(row);
        Assert.Equal(endpoint.Columns.Count, row!.Values.Length);

        // Position of the date column is what the echo check and the watermark both rely on.
        Assert.Equal(new DateTime(2026, 10, 14), (DateTime)row.Values[endpoint.DateColumnIndex]);
    }

    [Fact]
    public void MissingRequiredPropertyRejectsTheRow()
    {
        var endpoint = NucoreDescriptors.Find("Volatilities")!;
        var builder = new NucoreRowBuilder(endpoint);

        var json = JsonDocument.Parse("""{"productSpec":"NG"}""").RootElement;
        var row = builder.TryBuild(json, out var error);

        Assert.Null(row);
        Assert.NotNull(error);
    }

    internal static JsonElement SampleVolatilitiesRow() => JsonDocument.Parse(
        """
        {
          "createdAt": "2026-10-14T13:29:54.623437+00:00",
          "modifiedAt": "2026-10-14T13:29:54.623437+00:00",
          "createdByUser": "SYSTEM",
          "modifiedByUser": "SYSTEM",
          "systemDate": "2026-10-14T00:00:00",
          "productSpec": "NG",
          "instrumentType": "Gas Option",
          "startTradeMonth": "202701",
          "endTradeMonth": "202701",
          "putCallFlag": "C",
          "strikeType": "A",
          "strikePrice": 3.25,
          "volatility": 0.4483348049,
          "atmFlag": true,
          "updatedFlag": false
        }
        """).RootElement;
}
