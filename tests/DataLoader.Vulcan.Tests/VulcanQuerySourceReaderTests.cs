using DataLoader.Vulcan;
using DataLoader.Vulcan.Models;
using Xunit;

namespace DataLoader.Vulcan.Tests;

public class VulcanQuerySourceReaderTests
{
    [Fact]
    public void ParseRows_DataEnvelope_MapsSnakeCaseToProperties()
    {
        var json = """
        { "data": [
            { "synmax_id": "P1-U1", "plant_name": "Alpha", "unit_capacity": 123.5,
              "modified_at": "2026-01-10", "date_vulcan_earliest_online": "2027-06-01" }
        ] }
        """;

        var rows = VulcanQuerySourceReader<DataCenterRow>.ParseRows(json);

        var row = Assert.Single(rows);
        Assert.Equal("P1-U1", row.SynmaxId);
        Assert.Equal("Alpha", row.PlantName);
        Assert.Equal(123.5, row.UnitCapacity);
        Assert.Equal(new DateTime(2026, 1, 10), row.ModifiedAt);
        Assert.Equal(new DateTime(2027, 6, 1), row.DateVulcanEarliestOnline);
    }

    [Fact]
    public void ParseRows_BareArray_IsTolerated()
    {
        var json = """[ { "synmax_id": "X" } ]""";
        var rows = VulcanQuerySourceReader<ProjectRankingRow>.ParseRows(json);
        Assert.Equal("X", Assert.Single(rows).SynmaxId);
    }

    [Fact]
    public void ParseRows_UnknownShape_ReturnsEmpty()
    {
        var rows = VulcanQuerySourceReader<ProjectRankingRow>.ParseRows("""{ "error": "nope" }""");
        Assert.Empty(rows);
    }

    [Fact]
    public void ParseRows_NumbersAsStrings_AreTolerated()
    {
        var json = """{ "data": [ { "synmax_id": "R1", "plant_id": "55123", "final_rank": "6.5" } ] }""";
        var row = Assert.Single(VulcanQuerySourceReader<DataLoader.Vulcan.Models.ProjectRankingRow>.ParseRows(json));
        Assert.Equal(55123L, row.PlantId);
        Assert.Equal(6.5, row.FinalRank);
    }
}
