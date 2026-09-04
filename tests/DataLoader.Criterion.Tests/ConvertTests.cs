using Xunit;

namespace DataLoader.Criterion.Tests;

/// <summary>
/// PostgreSQL-to-CLR narrowing.
///
/// <para>
/// Most of these are identities — Npgsql already returns the right type. The tests
/// that earn their place are the ones covering what happens when the source does
/// something unexpected: a value too long for its target, a boolean in a form the
/// driver did not normalise, residual blank padding.
/// </para>
/// </summary>
public sealed class ConvertTests
{
    private static readonly CriterionColumn Text = CriterionColumn.Str("Name", 10, "name");
    private static readonly CriterionColumn TextMax = CriterionColumn.StrMax("Body", "body");
    private static readonly CriterionColumn Uuid = CriterionColumn.Uid("Id", "id", required: true);
    private static readonly CriterionColumn Flag = CriterionColumn.Bit("Active", "active");
    private static readonly CriterionColumn Small = CriterionColumn.I16("CycleId", "cycle_id");
    private static readonly CriterionColumn Number = CriterionColumn.Flt("Qty", "qty");
    private static readonly CriterionColumn Coordinate = CriterionColumn.Dec13("Latitude", "latitude");
    private static readonly CriterionColumn Day = CriterionColumn.Dat("EffGasDay", "eff_gas_day");
    private static readonly CriterionColumn Stamp = CriterionColumn.Dtm7("LoadDate", "load_date");

    // ------------------------------------------------------------------ nulls

    [Fact]
    public void Null_and_DBNull_both_become_DBNull()
    {
        Assert.Equal(DBNull.Value, CriterionConvert.Value(null, Text, out _));
        Assert.Equal(DBNull.Value, CriterionConvert.Value(DBNull.Value, Text, out _));
        Assert.Equal(DBNull.Value, CriterionConvert.Value(null, Uuid, out _));
    }

    /// <summary>
    /// A blank string is NULL, not an empty string. This is what stops the 60 rows in
    /// a 30-day window whose <c>unit_id</c> is 24 spaces from becoming an empty UnitId
    /// that reads as "a unit we could not resolve" rather than "no unit".
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void Blank_strings_become_DBNull(string value)
    {
        Assert.Equal(DBNull.Value, CriterionConvert.Value(value, Text, out _));
    }

    // ----------------------------------------------------------------- strings

    /// <summary>Residual character(n) padding is trimmed even if the SELECT did not.</summary>
    [Fact]
    public void Blank_padding_is_trimmed()
    {
        Assert.Equal("IA", CriterionConvert.Value("IA   ", Text, out _));
    }

    /// <summary>
    /// A value longer than its target is TRUNCATED and reported, not silently dropped
    /// and not allowed to fail the work unit. No live value exceeds its target today —
    /// this exists because the source can widen a column without telling anyone.
    /// </summary>
    [Fact]
    public void An_overlong_string_is_truncated_and_reported()
    {
        var value = CriterionConvert.Value("0123456789ABCDEF", Text, out var truncated);

        Assert.True(truncated);
        Assert.Equal("0123456789", value);
    }

    [Fact]
    public void A_string_that_fits_is_not_reported_as_truncated()
    {
        CriterionConvert.Value("0123456789", Text, out var truncated);
        Assert.False(truncated);
    }

    /// <summary>VARCHAR(MAX) has no limit — UpdnLoc is declared that way.</summary>
    [Fact]
    public void Varchar_max_is_never_truncated()
    {
        var long_ = new string('x', 100_000);
        var value = CriterionConvert.Value(long_, TextMax, out var truncated);

        Assert.False(truncated);
        Assert.Equal(long_, value);
    }

    /// <summary>
    /// <c>-1</c> means "no length limit". Note <c>DECIMAL(13,10)</c> is deliberately -1
    /// rather than 13: a decimal's PRECISION is not a string length, and treating it as
    /// one would truncate a coordinate's text form. Only String and Char columns ever
    /// reach the truncation path, so a numeric type having no limit is the correct and
    /// unreachable answer.
    /// </summary>
    [Theory]
    [InlineData("VARCHAR(30)", 30)]
    [InlineData("CHAR(3)", 3)]
    [InlineData("VARCHAR(MAX)", -1)]
    [InlineData("varchar(max)", -1)]
    [InlineData("DECIMAL(13,10)", -1)]
    [InlineData("INT", -1)]
    [InlineData("UNIQUEIDENTIFIER", -1)]
    public void Max_length_is_read_off_the_sql_type(string sqlType, int expected)
    {
        Assert.Equal(expected, CriterionConvert.MaxLength(sqlType));
    }

    /// <summary>
    /// The truncation path is reachable only from a string-typed column, which is what
    /// makes the DECIMAL case above harmless rather than a latent bug.
    /// </summary>
    [Fact]
    public void Only_string_columns_can_be_truncated()
    {
        CriterionConvert.Value(29.7601234567m, Coordinate, out var decimalTruncated);
        CriterionConvert.Value(12345, Small, out var intTruncated);

        Assert.False(decimalTruncated);
        Assert.False(intTruncated);
    }

    // ------------------------------------------------------------------ scalars

    [Fact]
    public void Guids_pass_through_and_parse_from_text()
    {
        var id = Guid.Parse("59c1c374-c1c8-422c-aaf5-c7e51ad45f3c");

        Assert.Equal(id, CriterionConvert.Value(id, Uuid, out _));
        Assert.Equal(id, CriterionConvert.Value("59c1c374-c1c8-422c-aaf5-c7e51ad45f3c", Uuid, out _));
    }

    [Fact]
    public void An_unparseable_guid_becomes_DBNull_rather_than_throwing()
    {
        Assert.Equal(DBNull.Value, CriterionConvert.Value("not-a-guid", Uuid, out _));
    }

    /// <summary>
    /// Npgsql returns a real bool for <c>boolean</c>, but the single-character and
    /// textual forms are accepted so a driver or view change cannot null out every
    /// Status and Active value at once.
    /// </summary>
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("t", true)]
    [InlineData("f", false)]
    [InlineData("1", true)]
    [InlineData("0", false)]
    public void Booleans_are_read_in_every_form_the_source_uses(object raw, bool expected)
    {
        Assert.Equal(expected, CriterionConvert.Value(raw, Flag, out _));
    }

    /// <summary>
    /// An unrecognised boolean degrades to DBNull rather than throwing — one unreadable
    /// flag must not cost a whole work unit. Matches the Guid case.
    /// </summary>
    [Fact]
    public void An_unrecognised_boolean_becomes_DBNull_rather_than_throwing()
    {
        Assert.Equal(DBNull.Value, CriterionConvert.Value("maybe", Flag, out _));
    }

    /// <summary>
    /// ⚠ The OTHER posture, pinned deliberately. A numeric conversion that overflows
    /// means the source changed the column's type underneath us; quietly nulling a
    /// quantity would corrupt analytics far more expensively than a failed work unit,
    /// so this is allowed to throw.
    /// </summary>
    [Fact]
    public void A_numeric_overflow_throws_rather_than_silently_nulling_a_quantity()
    {
        Assert.ThrowsAny<Exception>(() => CriterionConvert.Value(int.MaxValue, Small, out _));
    }

    [Fact]
    public void Numeric_types_narrow_to_their_target()
    {
        Assert.Equal((short)6, CriterionConvert.Value((short)6, Small, out _));
        Assert.Equal(1234.5, CriterionConvert.Value(1234.5, Number, out _));
        Assert.Equal(29.7601234567m, CriterionConvert.Value(29.7601234567m, Coordinate, out _));
    }

    /// <summary>A DATE target keeps only the calendar date; a DATETIME2 keeps the time.</summary>
    [Fact]
    public void Date_targets_discard_the_time_and_datetime_targets_keep_it()
    {
        var instant = new DateTime(2026, 9, 3, 23, 41, 26, DateTimeKind.Unspecified);

        Assert.Equal(new DateTime(2026, 9, 3), CriterionConvert.Value(instant, Day, out _));
        Assert.Equal(instant, CriterionConvert.Value(instant, Stamp, out _));
    }

    [Fact]
    public void DateOnly_is_accepted_for_a_date_column()
    {
        Assert.Equal(
            new DateTime(2026, 9, 3),
            CriterionConvert.Value(new DateOnly(2026, 9, 3), Day, out _));
    }

    /// <summary>A non-date in a date column is DBNull rather than an exception that fails the unit.</summary>
    [Fact]
    public void A_non_date_in_a_date_column_becomes_DBNull()
    {
        Assert.Equal(DBNull.Value, CriterionConvert.Value(42, Day, out _));
    }
}
