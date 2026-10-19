using System.Text;
using System.Text.Json;
using Xunit;

namespace DataLoader.EIA.Tests;

/// <summary>
/// The canonical-form gate.
///
/// <para>
/// The text <see cref="EiaCanonicalJson"/> produces is both what gets STORED and what
/// gets HASHED into the primary key. A change to spacing, key order, escaping or number
/// rendering re-keys the whole table. These tests pin the rules against the same live
/// incumbent rows the hash tests use, so a well-meaning "tidy-up" fails the build.
/// </para>
/// </summary>
public sealed class EiaCanonicalJsonTests
{
    private static EiaField Str(string name, string? value) => new(name, JsonValueKind.String, value);
    private static EiaField Num(string name, string raw) => new(name, JsonValueKind.Number, raw);
    private static EiaField Null(string name) => new(name, JsonValueKind.Null, null);

    private static string Render(params EiaField[] fields) =>
        EiaCanonicalJson.Render(fields, coerceNumericStrings: true);

    // ------------------------------------------------------------- shape

    [Fact]
    public void Renders_no_whitespace()
    {
        Assert.Equal("""{"a":"x","b":"y"}""", Render(Str("a", "x"), Str("b", "y")));
    }

    [Fact]
    public void Empty_object_is_the_two_character_form()
    {
        var rendered = EiaCanonicalJson.Render(Array.Empty<EiaField>(), coerceNumericStrings: true);

        Assert.Equal("{}", rendered);
        // The value 7,301 facet-less rows in the incumbent carry.
        Assert.Equal(1947253782, EiaLegacyHash.Compute(rendered));
    }

    // ------------------------------------------------------------- ordering

    [Fact]
    public void Sorts_keys_ordinal_regardless_of_input_order()
    {
        var forwards = Render(
            Str("marketTypeDescription", "Open Market"),
            Str("marketTypeId", "OM"),
            Str("stateRegionDescription", "Missouri"),
            Str("stateRegionId", "MO"));

        var shuffled = Render(
            Str("stateRegionId", "MO"),
            Str("marketTypeId", "OM"),
            Str("stateRegionDescription", "Missouri"),
            Str("marketTypeDescription", "Open Market"));

        Assert.Equal(forwards, shuffled);
        Assert.Equal(-1622649321, EiaLegacyHash.Compute(forwards));
    }

    /// <summary>
    /// Ordinal order puts '-' (0x2D) before every letter and digit, so a key and that
    /// same key with a suffix sort in a specific way a culture-aware comparer could get
    /// wrong. This is the real EIA key family that exercises it.
    /// </summary>
    [Fact]
    public void Orders_hyphenated_sibling_keys_the_way_the_incumbent_does()
    {
        var rendered = Render(
            Str("production-units", "short tons"),
            Num("productivity", "9.05"),
            Num("production", "341096"),
            Str("productivity-units", "short tons per labor hour"));

        Assert.Equal(
            """{"production":341096,"production-units":"short tons","productivity":9.05,"productivity-units":"short tons per labor hour"}""",
            rendered);
    }

    // ------------------------------------------------------------- escaping

    [Fact]
    public void Does_not_escape_the_solidus()
    {
        // The wire form is "BTU\/CF"; the incumbent stores "BTU/CF".
        var rendered = Render(Str("units", "BTU/CF"), Str("value", "1028"));

        Assert.Equal("""{"units":"BTU/CF","value":1028}""", rendered);
        Assert.Contains("BTU/CF", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(@"BTU\/CF", rendered, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same fields with coercion OFF reproduce the incumbent row byte for byte,
    /// including its stored hash — which is what proves the solidus rule is right and
    /// that the only difference from Conduit is the numeric normalisation.
    /// </summary>
    [Fact]
    public void Without_coercion_the_solidus_row_matches_the_incumbent_exactly()
    {
        var verbatim = EiaCanonicalJson.Render(
            new[] { Str("units", "BTU/CF"), Str("value", "1028") },
            coerceNumericStrings: false);

        Assert.Equal("""{"units":"BTU/CF","value":"1028"}""", verbatim);
        Assert.Equal(1007497798, EiaLegacyHash.Compute(verbatim));
    }

    /// <summary>
    /// A live incumbent row stores a carriage return as the two-character short escape.
    /// That row also shows why numeric coercion must reject trailing junk: the value
    /// stays a string.
    /// </summary>
    [Fact]
    public void Uses_short_escapes_for_control_characters()
    {
        Assert.Equal(@"{""v"":""148.49\r""}", Render(Str("v", "148.49\r")));
        Assert.Equal(@"{""v"":""a\nb""}", Render(Str("v", "a\nb")));
        Assert.Equal(@"{""v"":""a\tb""}", Render(Str("v", "a\tb")));
        Assert.Equal(@"{""v"":""a\bb""}", Render(Str("v", "a\bb")));
        Assert.Equal(@"{""v"":""a\fb""}", Render(Str("v", "a\fb")));
    }

    [Fact]
    public void Escapes_quotes_and_backslashes()
    {
        Assert.Equal(@"{""v"":""say \""hi\""""}", Render(Str("v", "say \"hi\"")));
        Assert.Equal(@"{""v"":""a\\b""}", Render(Str("v", @"a\b")));
    }

    [Fact]
    public void Leaves_non_ascii_literal()
    {
        Assert.Equal("{\"v\":\"café\"}", Render(Str("v", "café")));
    }

    [Fact]
    public void Escapes_other_control_characters_as_four_digit_unicode()
    {
        // U+0001 has no short escape, so it must come out as the six-character
        // sequence backslash-u-0-0-0-1. Both the input control character and the
        // expected escape text are built here rather than written as literals, so an
        // editor or an encoding round trip cannot quietly turn one into the other.
        var control = ((char)1).ToString();
        var expected = "{\"v\":\"a" + "\\" + "u0001b\"}";

        var rendered = Render(Str("v", "a" + control + "b"));

        Assert.Equal(expected, rendered);
        Assert.DoesNotContain(control, rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Escapes_the_key_as_well_as_the_value()
    {
        var sb = new StringBuilder();
        EiaCanonicalJson.AppendString(sb, "a\"b");
        Assert.Equal(@"""a\""b""", sb.ToString());
    }

    // ------------------------------------------------- numbers and coercion

    [Fact]
    public void Keeps_a_json_number_token_verbatim()
    {
        // Parsing to double and re-rendering would change this value, which is real:
        // it appears in the incumbent's `international` series.
        Assert.Equal(
            """{"value":6.0754044100385393E-05}""",
            Render(Num("value", "6.0754044100385393E-05")));

        Assert.Equal("""{"value":1117.538985}""", Render(Num("value", "1117.538985")));
    }

    /// <summary>
    /// The behaviour the loader request specifies: the API sends
    /// <c>"sales":"1023740"</c> and the stored form is unquoted.
    /// </summary>
    [Fact]
    public void Coerces_a_numeric_string_to_a_number()
    {
        var rendered = Render(
            Str("price", "w"),
            Str("price-units", "dollars per short ton"),
            Str("sales", "44163"),
            Str("sales-units", "short tons"));

        Assert.Equal(
            """{"price":"w","price-units":"dollars per short ton","sales":44163,"sales-units":"short tons"}""",
            rendered);
        Assert.Equal(-764207628, EiaLegacyHash.Compute(rendered));
    }

    /// <summary>
    /// The whole point of coercion: EIA flipped these fields from JSON numbers to JSON
    /// strings between 2022 and 2024, and because the key is a hash of the text, the
    /// incumbent stored the SAME business row twice under two FacetKeys
    /// (-2122406725 and -2135369135 are both live). Normalising makes the two wire
    /// forms produce one key.
    /// </summary>
    [Fact]
    public void Collapses_the_vendors_string_vs_number_type_flip_onto_one_key()
    {
        var asNumbers = Render(Num("activityId", "8"), Num("productId", "4002"), Str("name", "Coal"));
        var asStrings = Render(Str("activityId", "8"), Str("productId", "4002"), Str("name", "Coal"));

        Assert.Equal(asNumbers, asStrings);
        Assert.Equal(EiaLegacyHash.Compute(asNumbers), EiaLegacyHash.Compute(asStrings));
    }

    [Fact]
    public void Coercion_can_be_turned_off_to_reproduce_the_incumbent_exactly()
    {
        var fields = new[] { Str("value", "2206.690829") };

        var verbatim = EiaCanonicalJson.Render(fields, coerceNumericStrings: false);
        Assert.Equal("""{"value":"2206.690829"}""", verbatim);
        // The live DataHash of that exact stored text.
        Assert.Equal(-2134288009, EiaLegacyHash.Compute(verbatim));

        var coerced = EiaCanonicalJson.Render(fields, coerceNumericStrings: true);
        Assert.Equal("""{"value":2206.690829}""", coerced);
        Assert.NotEqual(verbatim, coerced);
    }

    [Fact]
    public void Renders_nulls_and_booleans()
    {
        Assert.Equal("""{"a":null}""", Render(Null("a")));
        Assert.Equal("""{"a":true,"b":false}""", Render(
            new EiaField("a", JsonValueKind.True, null),
            new EiaField("b", JsonValueKind.False, null)));
    }

    [Fact]
    public void Refuses_a_nested_object_rather_than_inventing_an_ordering()
    {
        var ex = Assert.Throws<NotSupportedException>(() =>
            Render(new EiaField("nested", JsonValueKind.Object, """{"x":1}""")));

        Assert.Contains("nested", ex.Message);
    }

    // ----------------------------------------- the numeric grammar itself

    [Theory]
    [InlineData("0")]
    [InlineData("-0")]
    [InlineData("44163")]
    [InlineData("-24")]
    [InlineData("53")]
    [InlineData("60.32")]
    [InlineData("2206.690829")]
    [InlineData("1e5")]
    [InlineData("6.0754044100385393E-05")]
    [InlineData("-1.5e+10")]
    public void Recognises_valid_json_numbers(string value) =>
        Assert.True(EiaCanonicalJson.LooksLikeJsonNumber(value));

    [Theory]
    [InlineData("")]
    [InlineData("w")]
    [InlineData("NA")]
    [InlineData("(s)")]
    [InlineData("--")]
    [InlineData("007")]          // leading zeros are not JSON, and are meaningful in an id
    [InlineData("+5")]           // JSON has no leading plus
    [InlineData(".5")]
    [InlineData("5.")]
    [InlineData("1,000")]
    [InlineData("1 000")]
    [InlineData("148.49\r")]     // real incumbent value: trailing junk keeps it a string
    [InlineData(" 42")]
    [InlineData("42 ")]
    [InlineData("1e")]
    [InlineData("1e+")]
    [InlineData("0x1F")]
    [InlineData("Infinity")]
    [InlineData("NaN")]
    [InlineData("-")]
    public void Rejects_everything_that_is_not_a_json_number(string value) =>
        Assert.False(EiaCanonicalJson.LooksLikeJsonNumber(value));

    /// <summary>
    /// Leading zeros are the case where coercion would silently destroy information:
    /// a facet id of "007" is not the number 7.
    /// </summary>
    [Fact]
    public void Leaves_an_identifier_with_leading_zeros_as_a_string()
    {
        Assert.Equal("""{"id":"007"}""", Render(Str("id", "007")));
    }

    // ------------------------------------ the two shapes JSON does not allow

    /// <summary>
    /// ⚠ Live-verified 2026-10-16: EIA sends <c>"productivity":".43"</c> for 5 of the
    /// 400 rows in <c>coal/aggregate-production</c> for 2001, and the incumbent stores
    /// <c>0.43</c>. A leading dot is a legal decimal nearly everywhere EXCEPT in JSON,
    /// so emitting it unquoted would put invalid JSON in the column. Inserting the
    /// zero is text surgery, not arithmetic, so no digit changes.
    /// </summary>
    [Theory]
    [InlineData(".43", "0.43")]
    [InlineData(".04", "0.04")]
    [InlineData(".81", "0.81")]
    [InlineData("-.5", "-0.5")]
    [InlineData("5.", "5")]
    [InlineData("-5.", "-5")]
    public void Repairs_a_decimal_json_itself_would_reject(string wire, string expected)
    {
        Assert.True(EiaCanonicalJson.TryNormaliseNumber(wire, out var normalised));
        Assert.Equal(expected, normalised);
        Assert.True(EiaCanonicalJson.LooksLikeJsonNumber(normalised));

        Assert.Equal($"{{\"v\":{expected}}}", Render(Str("v", wire)));
    }

    /// <summary>
    /// The exact live row. With the repair in place this reproduces the incumbent's
    /// stored text for all ten fields.
    /// </summary>
    [Fact]
    public void Reproduces_the_incumbent_row_that_carries_a_leading_dot_value()
    {
        var rendered = Render(
            Str("average-employees", "272"),
            Str("average-employees-units", "number of employees"),
            Str("labor-hours", "479901"),
            Str("labor-hours-units", "hours"),
            Str("number-of-mines", "25"),
            Str("number-of-mines-units", "number of mines"),
            Str("production", "341096"),
            Str("production-units", "short tons"),
            Str("productivity", ".71"),
            Str("productivity-units", "short tons per labor hour"));

        Assert.Equal(
            """{"average-employees":272,"average-employees-units":"number of employees","labor-hours":479901,"labor-hours-units":"hours","number-of-mines":25,"number-of-mines-units":"number of mines","production":341096,"production-units":"short tons","productivity":0.71,"productivity-units":"short tons per labor hour"}""",
            rendered);
    }

    /// <summary>
    /// The repair is deliberately narrow. A leading plus and leading zeros are NOT
    /// repaired — "007" is an identifier, and "7" is a different thing.
    /// </summary>
    [Theory]
    [InlineData("007")]
    [InlineData("+5")]
    [InlineData("+.5")]
    [InlineData(".")]
    [InlineData("-.")]
    [InlineData("..5")]
    [InlineData("1.2.3")]
    [InlineData("w")]
    [InlineData("")]
    public void Does_not_repair_anything_else(string wire)
    {
        Assert.False(EiaCanonicalJson.TryNormaliseNumber(wire, out _));

        // None of these cases needs escaping, so the quoted wire text IS the expected
        // output. (Built by hand rather than with JsonSerializer, whose default encoder
        // escapes a plus sign into a unicode escape -- this writer deliberately does not.)
        Assert.Equal($"{{\"v\":\"{wire}\"}}", Render(Str("v", wire)));
    }

    /// <summary>
    /// Whatever the repair produces must be parseable as JSON, or the column stops
    /// being queryable with OPENJSON / JSON_VALUE.
    /// </summary>
    [Theory]
    [InlineData(".43")]
    [InlineData("-.5")]
    [InlineData("5.")]
    [InlineData("w")]
    [InlineData("148.49\r")]
    [InlineData("007")]
    public void Output_is_always_valid_json(string wire)
    {
        using var parsed = System.Text.Json.JsonDocument.Parse(Render(Str("v", wire)));
        Assert.Equal(JsonValueKind.Object, parsed.RootElement.ValueKind);
    }
}
