using System.Text.Json;
using Xunit;

namespace DataLoader.NGI.Tests;

/// <summary>
/// Guards the FIXTURES themselves, at the raw-file level, before any parsing is involved. If someone
/// re-captures or trims <c>Samples\*.json</c>, these assertions say so immediately instead of letting
/// every count-based mapping test drift quietly.
/// </summary>
public class FixtureFactsTests
{
    [Fact]
    public void BothFixturesArePresentNextToTheTestAssembly()
    {
        Assert.False(string.IsNullOrWhiteSpace(Samples.Datafeed20260801));
        Assert.False(string.IsNullOrWhiteSpace(Samples.Locations));
    }

    [Fact]
    public void Datafeed_Has163Records_EachWithAllElevenDocumentedFields()
    {
        using var doc = JsonDocument.Parse(Samples.Datafeed20260801);
        var root = doc.RootElement;

        // The live envelope: {meta, data} with data an OBJECT keyed by point code (never an array).
        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        var meta = root.GetProperty("meta");
        Assert.Equal("2026-08-01", meta.GetProperty("issue_date").GetString());
        Assert.Equal("2026-07-27", meta.GetProperty("start_date").GetString());
        Assert.Equal("2026-07-29", meta.GetProperty("end_date").GetString());

        var data = root.GetProperty("data");
        Assert.Equal(JsonValueKind.Object, data.ValueKind);

        var records = data.EnumerateObject().ToArray();
        Assert.Equal(Samples.ExpectedRecordCount, records.Length);   // 163

        var expectedFields = new[]
        {
            "Point Code", "Issue Date", "Survey Start", "Survey End", "Region", "Pricing Point",
            "Low", "High", "Average", "Volume", "Deals"
        };

        foreach (var record in records)
        {
            Assert.Equal(JsonValueKind.Object, record.Value.ValueKind);
            Assert.Equal(expectedFields.Length, record.Value.EnumerateObject().Count());
            foreach (var field in expectedFields)
                Assert.True(record.Value.TryGetProperty(field, out _), $"{record.Name} is missing '{field}'");

            // The map key equals the record's own "Point Code" in every live record.
            Assert.Equal(record.Name, record.Value.GetProperty("Point Code").GetString());
        }
    }

    [Fact]
    public void Datafeed_EveryValueIsAString_AndTheNullSentinelIsTheLiteralNone()
    {
        using var doc = JsonDocument.Parse(Samples.Datafeed20260801);

        var lowNones = 0;
        var volumeNones = 0;
        foreach (var record in doc.RootElement.GetProperty("data").EnumerateObject())
        {
            foreach (var field in record.Value.EnumerateObject())
                Assert.Equal(JsonValueKind.String, field.Value.ValueKind); // never a JSON number/null

            if (record.Value.GetProperty("Low").GetString() == "None") lowNones++;
            if (record.Value.GetProperty("Volume").GetString() == "None") volumeNones++;
        }

        // The counts the mapping tests assert against, read straight off the raw file.
        Assert.Equal(Samples.ExpectedPriceNullCount, lowNones);       // 45
        Assert.Equal(Samples.ExpectedActivityNullCount, volumeNones); // 47
    }

    [Fact]
    public void Locations_Has163Entries_UnderTheSpaceBearingNode_MappingNameToCode()
    {
        using var doc = JsonDocument.Parse(Samples.Locations);
        var root = doc.RootElement;

        var node = root.GetProperty("Bidweek Locations");   // note the SPACE and the capital L
        Assert.Equal(JsonValueKind.Object, node.ValueKind);
        Assert.Single(root.EnumerateObject());              // a single top-level node

        var entries = node.EnumerateObject().ToArray();
        Assert.Equal(Samples.ExpectedLocationCount, entries.Length);   // 163

        // Direction, at the raw-file level: KEY = name, VALUE = code.
        Assert.Equal("STXAGUAD", node.GetProperty("Agua Dulce").GetString());
        Assert.All(entries, e =>
        {
            Assert.Equal(JsonValueKind.String, e.Value.ValueKind);
            var code = e.Value.GetString()!;
            Assert.Equal(code.ToUpperInvariant(), code);      // codes are UPPERCASE ...
            Assert.DoesNotContain(" ", code);                 // ... and unspaced
        });
    }

    [Fact]
    public void BothFixtures_AreAsciiOnly_WhichIsWhyTheColumnsAreVarcharNotNvarchar()
    {
        foreach (var text in new[] { Samples.Datafeed20260801, Samples.Locations })
            Assert.All(text, c => Assert.True(c <= 127, $"non-ASCII character U+{(int)c:X4}"));
    }

    [Fact]
    public void ObservedWidths_FitTheDeclaredColumnWidths()
    {
        var map = Samples.RawLocationMap();
        Assert.True(map.Values.Max(c => c.Length) <= 20, "PointCode exceeds VARCHAR(20)");
        Assert.True(map.Keys.Max(n => n.Length) <= 100, "LocationName exceeds VARCHAR(100)");

        var records = Samples.RawDatafeedRecords();
        Assert.True(records.Values.Max(r => r.GetProperty("Region").GetString()!.Length) <= 64,
            "Region exceeds VARCHAR(64)");
        Assert.True(records.Values.Max(r => r.GetProperty("Pricing Point").GetString()!.Length) <= 100,
            "PricingPoint exceeds VARCHAR(100)");
    }
}
