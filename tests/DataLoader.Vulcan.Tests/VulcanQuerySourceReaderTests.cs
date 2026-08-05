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
    public void ParseRows_LoneObject_IsSingleRow()
    {
        // New contract: a lone top-level JSON object is one NDJSON data row (never an "error"
        // envelope) — non-2xx bodies are already rejected upstream by EnsureSuccessStatusCode.
        var rows = VulcanQuerySourceReader<ProjectRankingRow>.ParseRows("""{ "synmax_id": "Z9" }""");
        Assert.Equal("Z9", Assert.Single(rows).SynmaxId);
    }

    [Fact]
    public void ParseRows_Ndjson_OneObjectPerLine_MapsAllRows()
    {
        // query_datalinks streams NDJSON: one JSON object per line, no array wrapper.
        var json =
            "{ \"synmax_id\": \"A\", \"plant_name\": \"Alpha\" }\n" +
            "{ \"synmax_id\": \"B\", \"plant_name\": \"Bravo\" }\n" +
            "{ \"synmax_id\": \"C\", \"plant_name\": \"Charlie\" }\n";

        var rows = VulcanQuerySourceReader<UnderConstructionRow>.ParseRows(json);

        Assert.Equal(3, rows.Count);
        Assert.Equal(new[] { "A", "B", "C" }, rows.Select(r => r.SynmaxId).ToArray());
        Assert.Equal("Bravo", rows[1].PlantName);
    }

    [Fact]
    public void ParseRows_NumericStringField_IsCoerced()
    {
        // datacenters sends plant_id as a bare JSON number; the model has string? PlantId.
        var json = """{ "data": [ { "synmax_id": "P1-U1", "plant_id": 68347 } ] }""";

        var row = Assert.Single(VulcanQuerySourceReader<DataCenterRow>.ParseRows(json));

        Assert.Equal("68347", row.PlantId);
    }

    [Fact]
    public void ParseRows_NumbersAsStrings_AreTolerated()
    {
        var json = """{ "data": [ { "synmax_id": "R1", "plant_id": "55123", "final_rank": "6.5" } ] }""";
        var row = Assert.Single(VulcanQuerySourceReader<DataLoader.Vulcan.Models.ProjectRankingRow>.ParseRows(json));
        Assert.Equal(55123L, row.PlantId);
        Assert.Equal(6.5, row.FinalRank);
    }

    [Fact]
    public void ParseRows_DataCenter_FullPayload_MapsAllFields()
    {
        // The complete shape the datacenters endpoint returns for one record.
        var json = """
        [{
            "plant_id": 3623575, "plant_name": "Newnan Data Center Campus (Project Sail)",
            "unit_id": 2585128, "unit_name": "Newnan DC09", "owner_name": "Prologis Incorporated",
            "state_code": "GA", "balancing_authority": null, "unit_capacity": 48.0,
            "unit_status": "Planned", "plant_status": "Planned", "date_planned_operation": "2032-01-31",
            "data_center_type": "Hyperscale Data Center", "date_vulcan_status_change": null,
            "date_image": "2026-01-16", "vulcan_status": "none", "observation": null,
            "date_construction_start": null, "date_land_cleared": null, "date_first_structures": null,
            "date_construction_50_percent_complete": "TBD", "date_construction_completed": null,
            "date_vulcan_earliest_plus_7": "10/21/2026", "date_vulcan_earliest_online": null,
            "date_vulcan_latest_online": null, "date_vulcan_median_online": null,
            "days_iir_minus_vulcan_earliest_online": null, "days_iir_minus_vulcan_latest_online": null,
            "synmax_id": "3623575-2585128", "source": "IIR Energy", "created_at": "TBD",
            "modified_at": "2026-04-24", "btm_generation": false, "date_image_reviewed": "2026-04-21",
            "btm_classification": "G", "weekly_progress_indicator": null,
            "date_projected_earliest_land_clear": "2031-08-11", "date_projected_median_land_clear": null,
            "country": null, "market_region": null, "date_projected_earliest_first_structures": null,
            "date_projected_median_first_structures": null, "total_wpi": 0.0, "wpi_online_date": null
        }]
        """;

        var row = Assert.Single(VulcanQuerySourceReader<DataCenterRow>.ParseRows(json));

        Assert.Equal("3623575-2585128", row.SynmaxId);
        Assert.Equal("3623575", row.PlantId);
        Assert.Equal("2585128", row.UnitId);
        Assert.Equal("Prologis Incorporated", row.OwnerName);
        Assert.Equal(48.0, row.UnitCapacity);
        Assert.Equal("Planned", row.UnitStatus);
        Assert.Equal("Planned", row.PlantStatus);
        Assert.Equal("IIR Energy", row.Source);
        Assert.Equal("G", row.BtmClassification);
        Assert.False(row.BtmGeneration);
        Assert.Equal(0.0, row.TotalWpi);
        Assert.Equal(new DateTime(2032, 1, 31), row.DatePlannedOperation);
        Assert.Equal(new DateTime(2026, 1, 16), row.DateImage);
        Assert.Equal(new DateTime(2026, 4, 21), row.DateImageReviewed);
        Assert.Equal(new DateTime(2031, 8, 11), row.DateProjectedEarliestLandClear);
        Assert.Equal(new DateTime(2026, 4, 24), row.ModifiedAt);
        // US M/d/yyyy date is parsed.
        Assert.Equal(new DateTime(2026, 10, 21), row.DateVulcanEarliestPlus7);
        // "TBD" sentinels in date fields become null instead of failing the batch.
        Assert.Null(row.DateConstruction50PercentComplete);
        Assert.Null(row.CreatedAt);
        Assert.Null(row.DateVulcanEarliestOnline);
        Assert.Null(row.WeeklyProgressIndicator);
        Assert.Null(row.BalancingAuthority);
    }

    [Theory]
    [InlineData("2026-01-16", 2026, 1, 16)]
    [InlineData("10/21/2026", 2026, 10, 21)]
    [InlineData("2026-04-24T00:00:00Z", 2026, 4, 24)]
    public void FlexibleDate_ParsesKnownFormats(string input, int y, int m, int d) =>
        Assert.Equal(new DateTime(y, m, d), FlexibleDateConverter.ParseDate(input));

    [Theory]
    [InlineData("TBD")]
    [InlineData("none")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-date")]
    public void FlexibleDate_UnparseableOrSentinel_IsNull(string input) =>
        Assert.Null(FlexibleDateConverter.ParseDate(input));
}
