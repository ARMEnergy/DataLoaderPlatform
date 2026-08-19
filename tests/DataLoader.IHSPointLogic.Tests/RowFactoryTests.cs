using System.Text.Json;
using Xunit;

namespace DataLoader.IHSPointLogic.Tests;

/// <summary>
/// The per-endpoint row factories (<c>Rows.cs</c>) — the ONLY typed mapping code (design §7). Each
/// <c>From(elem, unit)</c> maps a real JSON element (Samples.*) to a target row, injects the
/// path/param/stamped fields carried on the work unit, drops an element (returns null) when a required
/// key/NOT-NULL cell is missing, and leaves <c>FileLogId</c> at its default 0 (the source reader stamps
/// it later). This class covers every §7 special case plus the null-drop guards.
/// </summary>
public class RowFactoryTests
{
    private static JsonElement E(string json) => Json.Element(json);

    /// <summary>A minimal work unit; the optional injected fields are set per test.</summary>
    private static PlWorkUnit Unit(
        string endpointId = "X",
        int? paramId = null,
        int? secondaryId = null,
        DateOnly? reportDate = null) => new()
    {
        EndpointId = endpointId,
        RequestPath = "cs/v1/x",
        KeyValue = "k",
        ParamId = paramId,
        SecondaryId = secondaryId,
        ReportDate = reportDate
    };

    // ================================================================ Tier 0 dimensions (A)

    [Fact]
    public void Region_MapsIdAndName_FileLogIdDefaultsToZero()
    {
        var r = RegionRow.From(E(Samples.RegionElem), Unit())!;
        Assert.Equal(26105, r.RegionId);
        Assert.Equal("Midcon", r.Name);
        Assert.Equal(0, r.FileLogId); // stamped by the reader, not the factory
    }

    [Theory]
    [InlineData("""{ "name": "x" }""")]          // missing id
    [InlineData("""{ "id": 1, "name": " " }""")] // blank name → null
    [InlineData("""{ "id": 1 }""")]              // missing name
    public void Region_DropsWhenIdOrNameMissing(string json) =>
        Assert.Null(RegionRow.From(E(json), Unit()));

    [Fact]
    public void Pipeline_MapsLegacyName()
    {
        var r = PipelineRow.From(E(Samples.PipelineElem), Unit())!;
        Assert.Equal(2, r.PipelineId);
        Assert.Equal("Algonquin Gas Transmission", r.Name);
        Assert.Equal("Algonquin", r.LegacyName);
    }

    [Fact]
    public void Point_ReadsPipelineIdFromThe_0_SuffixField()
    {
        // The verified field name is pipelineid_0 (NOT pipelineid) on lookup_point.
        var r = PointRow.From(E(Samples.PointElem), Unit())!;
        Assert.Equal(691, r.PointId);
        Assert.Equal("Amoco", r.Name);
        Assert.Equal(59, r.PipelineId);   // from pipelineid_0
        Assert.Equal(3, r.PointTypeId);
        Assert.Equal(2, r.PointStatusId);
    }

    [Fact]
    public void Point_Ignores_pipelineid_WithoutSuffix()
    {
        // A plain "pipelineid" (no _0) must NOT populate PipelineId — only pipelineid_0 is read.
        var r = PointRow.From(E("""{ "id": 5, "name": "N", "pipelineid": 77 }"""), Unit())!;
        Assert.Null(r.PipelineId);
    }

    [Fact]
    public void PointMetadata_DesignCapacityStringNumber_AndBlankToNull_AndBool()
    {
        var r = PointMetadataRow.From(E(Samples.PointMetadataElem), Unit())!;
        Assert.Equal(33071, r.PointId);
        Assert.Equal("LAReg112", r.PointName);
        Assert.Equal("ACADA0001", r.PointLciId);
        Assert.Equal(0m, r.DesignCapacity);      // "0.00000000" string-number → 0
        Assert.False(r.PointIsActive);           // JSON false → bit
        Assert.Null(r.Drn);                      // " " blank → null
        Assert.Null(r.County);                   // " " blank → null
        Assert.Null(r.Region);                   // " " blank → null
        Assert.Null(r.LocProp);                  // "" empty → null
        Assert.Equal(0m, r.PointLatitude);
        Assert.Equal(0m, r.PointLongitude);
        Assert.Equal(2, r.FlowDirectionId);
        Assert.Equal("Delivery", r.FlowDirection);
        Assert.Null(r.CountyId);                 // absent → null
        Assert.Null(r.RegionId);                 // absent → null
    }

    [Fact]
    public void PipelineNoticeSearch_IdIsLong_OffsetsParsed_MissingDatesNull()
    {
        var r = PipelineNoticeSearchRow.From(E(Samples.NoticeElem), Unit())!;
        Assert.Equal(635182L, r.Id);             // BIGINT
        Assert.Equal(14, r.CategoryId);
        Assert.False(r.IsCritical);
        Assert.Equal(521, r.PipelineId);
        Assert.Equal(new DateTimeOffset(2026, 8, 18, 15, 27, 29, 544, TimeSpan.FromHours(-5)), r.PostedDate);
        Assert.Equal(new DateTimeOffset(2026, 8, 18, 15, 27, 27, 638, TimeSpan.FromHours(-5)), r.EffectiveDate);
        Assert.Null(r.EndDate);                  // absent in payload

        var noDates = PipelineNoticeSearchRow.From(E(Samples.NoticeElemNoDates), Unit())!;
        Assert.Equal(635183L, noDates.Id);
        Assert.Null(noDates.EffectiveDate);      // absent → null
        Assert.Null(noDates.EndDate);
    }

    [Fact]
    public void PipelineNoticeSearch_DropsWhenIdMissing() =>
        Assert.Null(PipelineNoticeSearchRow.From(E("""{ "subject": "x" }"""), Unit()));

    // ================================================================ Tier 0 fact snapshots (B)

    [Fact]
    public void DemandForecastRegion_StampsForecastDateFromUnit_MapsRegionSubregion()
    {
        var forecast = new DateOnly(2026, 8, 18); // the stamped UTC run date
        var r = DemandForecastRegionRow.From(E(Samples.DemandForecastRegionElem), Unit(reportDate: forecast))!;
        Assert.Equal(forecast, r.ForecastDate);              // stamped from unit.ReportDate
        Assert.Equal(new DateOnly(2026, 8, 19), r.Date);     // payload "date"
        Assert.Equal("Canada", r.Region);
        Assert.Equal("Eastern Canada", r.Subregion);
        Assert.Equal(-0.015251m, r.DepartureFromNormalF);
        Assert.Equal(68.352891m, r.NormalTemperatureF);
        Assert.Null(r.TotalConsumption);                     // absent → null
    }

    [Fact]
    public void DemandForecastRegion_DropsWhenUnstamped()
    {
        // No ReportDate on the unit → the row cannot be keyed → dropped.
        Assert.Null(DemandForecastRegionRow.From(E(Samples.DemandForecastRegionElem), Unit(reportDate: null)));
    }

    [Fact]
    public void DemandForecastUsLower48_StampsForecast_MapsRegionNameAndEndingDate()
    {
        var forecast = new DateOnly(2026, 8, 18);
        var r = DemandForecastUsLower48Row.From(E(Samples.DemandForecastUsLower48Elem), Unit(reportDate: forecast))!;
        Assert.Equal(forecast, r.ForecastDate);
        Assert.Equal(new DateOnly(2026, 8, 18), r.Date);         // from pointreadingaggregate_endingdate
        Assert.Equal("United States of America", r.Region);       // from regionname
        Assert.Equal(48.05198169m, r.Power);
        Assert.Equal(77.71465771m, r.Subtotal);
    }

    [Fact]
    public void DemandForecastUsLower48_DropsWhenUnstamped() =>
        Assert.Null(DemandForecastUsLower48Row.From(E(Samples.DemandForecastUsLower48Elem), Unit(reportDate: null)));

    [Fact]
    public void GasProduction_ReportedDateIsDateTime2_MinutePrecision()
    {
        var r = GasProductionProducingAreaRow.From(E(Samples.GasProductionElem), Unit())!;
        Assert.Equal(new DateTime(2026, 8, 18, 13, 33, 0), r.ReportedDate); // yyyy-MM-dd HH:mm
        Assert.Equal(new DateOnly(2026, 7, 19), r.ReferenceDate);
        Assert.Equal("Gulf of Mexico", r.Region);
        Assert.Equal("Gulf of Mexico", r.ProducingArea);
        Assert.Equal("Gulf of Mexico", r.State);
        Assert.Equal(1825.3466644m, r.DryFactoredValue);
        Assert.Equal(2194.11088m, r.WellheadValue);
    }

    [Fact]
    public void MarketBalances_MapsAll16Measures_NegativeBalancingItem()
    {
        var r = MarketBalancesUsLower48Row.From(E(Samples.MarketBalancesElem), Unit())!;
        Assert.Equal(new DateOnly(2026, 8, 18), r.TimePeriod);
        Assert.Equal(123.850395m, r.Wellhead);
        Assert.Equal(110.167711m, r.DryGas);
        Assert.Equal(115.55359m, r.TotalSupply);
        Assert.Equal(112.236958m, r.TotalDemand);
        Assert.Equal(-0.029986m, r.BalancingItem); // signed preserved
    }

    [Fact]
    public void SupplyAndDemand_KeyedByDate_MapsMeasures()
    {
        var r = SupplyAndDemandRow.From(E(Samples.SupplyAndDemandElem), Unit())!;
        Assert.Equal(new DateOnly(2026, 6, 19), r.Date);
        Assert.Equal(124.063469m, r.Wellhead);
        Assert.Equal(0.352836m, r.BalancingItem);
    }

    [Fact]
    public void PipelineFlow_ThroughputIsAKey_ReportedDateExcluded_VolumeRead()
    {
        var r = PipelineFlowThroughputRow.From(E(Samples.PipelineFlowElem), Unit())!;
        Assert.Equal(new DateOnly(2026, 7, 19), r.FlowDate);   // MM/dd/yyyy
        Assert.Equal("Southeast", r.Region);
        Assert.Equal("Southern Natural Gas", r.Pipeline);
        Assert.Equal("Scheduled", r.Throughput);               // a LABEL (key), not a measure
        Assert.Equal("Receipt", r.FlowType);
        Assert.Equal(1234.567m, r.Volume);
    }

    [Fact]
    public void PipelineFlow_RowType_HasNoReportedDateColumn()
    {
        // reporteddate is present in the payload but deliberately NOT persisted — no such property exists.
        Assert.Null(typeof(PipelineFlowThroughputRow).GetProperty("ReportedDate"));
    }

    [Fact]
    public void UsSampleStorage_MapsReportDateAndFieldType_NegativeVolume()
    {
        var r = UsSampleStorageFacilityRow.From(E(Samples.UsSampleStorageElem), Unit())!;
        Assert.Equal(new DateOnly(2026, 8, 18), r.ReportDate);
        Assert.Equal(new DateOnly(2026, 8, 18), r.FlowDate);
        Assert.Equal("Arcadia", r.Name);
        Assert.Equal("South Central", r.EiaRegion);
        Assert.Equal("LA", r.State);
        Assert.Equal("Salt Dome", r.FieldType);   // from field_type
        Assert.Equal(-48.25m, r.Volume);
    }

    [Fact]
    public void UsImportsExports_MapsAllColumns_NegativeVolume()
    {
        var r = UsImportsExportsByPointsAggregateRow.From(E(Samples.UsImportsExportsElem), Unit())!;
        Assert.Equal(new DateOnly(2026, 8, 18), r.RunDate);
        Assert.Equal(new DateOnly(2026, 8, 18), r.FlowDate);
        Assert.Equal("Freeport modeled feedgas activity", r.PointName);
        Assert.Equal("Delivery", r.LedgerSide);
        Assert.Equal(-1113.0385m, r.Volume);
        Assert.Equal("TX", r.State);
        Assert.Equal("LNG", r.Type);
        Assert.Equal("LNG - Texas", r.DistrictName);
    }

    [Fact]
    public void ModeledDemand_MapsProductAndRegion()
    {
        var r = ModeledDemandRegionTypeRow.From(E(Samples.ModeledDemandElem), Unit())!;
        Assert.Equal(new DateOnly(2026, 7, 19), r.ReferenceDate);
        Assert.Equal("Electric Power Plants", r.PLEProductName);
        Assert.Equal("Northeast", r.RegionName);
        Assert.Equal(10968.2019m, r.Volume);
    }

    [Fact]
    public void StateFlows_MapsFromToStates()
    {
        var r = StateFlowsThroughputAggregateRow.From(E(Samples.StateFlowsElem), Unit())!;
        Assert.Equal(new DateOnly(2026, 7, 19), r.FlowDate);
        Assert.Equal("Alabama", r.FromState);
        Assert.Equal("Florida", r.ToState);
        Assert.Equal("Inflow", r.FlowType);
        Assert.Equal(2092.671m, r.Volume);
    }

    // ================================================================ Tier 1 discovery-fed lookups (C)

    [Fact]
    public void County_InjectsStateIdFromPath()
    {
        var r = CountyRow.From(E(Samples.CountyElem), Unit(paramId: 4520))!;
        Assert.Equal(14117, r.CountyId);
        Assert.Equal(4520, r.StateId);   // injected from unit.ParamId (the {id} in the URL)
        Assert.Equal("Autauga", r.Name);
    }

    [Fact]
    public void County_DropsWhenParamIdMissing() =>
        Assert.Null(CountyRow.From(E(Samples.CountyElem), Unit(paramId: null)));

    [Fact]
    public void Facility_InjectsPointTypeId_DropsRedundantFacilityTypeIdEcho()
    {
        var r = FacilityRow.From(E(Samples.FacilityElem), Unit(paramId: 1))!;
        Assert.Equal(24854, r.FacilityId);
        Assert.Equal(1, r.PointTypeId);  // injected from the path, NOT the body echo
        Assert.Equal("PWR - Oregon Clean Energy", r.Name);
        // The redundant facilitytypeid body echo is not persisted — the row type carries no such column.
        Assert.Null(typeof(FacilityRow).GetProperty("FacilityTypeId"));
    }

    [Fact]
    public void Subregion_InjectsRegionIdFromPath()
    {
        var r = SubregionRow.From(E(Samples.SubregionElem), Unit(paramId: 26105))!;
        Assert.Equal(26252, r.SubRegionId);
        Assert.Equal(26105, r.RegionId); // injected from unit.ParamId
        Assert.Equal("Lower Midcon", r.Name);
    }

    // ================================================================ Tier 2 parametrized facts (D, E)

    [Fact]
    public void SdByRegion_InjectsRegionIdFromPath_AndDateFromParam()
    {
        var reportDate = new DateOnly(2026, 8, 18);
        var r = SupplyAndDemandByRegionRow.From(E(Samples.SdRegionElem), Unit(paramId: 26105, reportDate: reportDate))!;
        Assert.Equal(26105, r.RegionId);       // injected (path)
        Assert.Equal(reportDate, r.Date);      // injected (reportDate param)
        Assert.Equal("Plains Production", r.Product);
        Assert.Equal(2947m, r.VolumeMmcfd);
    }

    [Theory]
    [InlineData(null, "2026-08-18")]  // missing RegionId
    [InlineData(26105, null)]         // missing Date
    public void SdByRegion_DropsWhenInjectedKeyMissing(int? paramId, string? date)
    {
        var d = date is null ? (DateOnly?)null : DateOnly.Parse(date);
        Assert.Null(SupplyAndDemandByRegionRow.From(E(Samples.SdRegionElem), Unit(paramId: paramId, reportDate: d)));
    }

    [Fact]
    public void SdBySubRegion_InjectsSubRegionFromPath_RegionFromMap_DateFromParam()
    {
        var reportDate = new DateOnly(2026, 8, 18);
        var r = SupplyAndDemandBySubRegionRow.From(
            E(Samples.SdRegionElem), Unit(paramId: 26252, secondaryId: 26105, reportDate: reportDate))!;
        Assert.Equal(26252, r.SubRegionId);   // injected (path)
        Assert.Equal(26105, r.RegionId);      // injected (from the SubRegionId→RegionId map, unit.SecondaryId)
        Assert.Equal(reportDate, r.Date);
        Assert.Equal("Plains Production", r.Product);
        Assert.Equal(2947m, r.VolumeMmcfd);
    }

    [Fact]
    public void SdBySubRegion_DropsWhenRegionMapMissing() =>
        Assert.Null(SupplyAndDemandBySubRegionRow.From(
            E(Samples.SdRegionElem), Unit(paramId: 26252, secondaryId: null, reportDate: new DateOnly(2026, 8, 18))));

    [Fact]
    public void PointVolume_IdIsPointId_NegativeVolume()
    {
        var r = PointVolumeRow.From(E(Samples.PointVolumeElem), Unit())!;
        Assert.Equal(691, r.PointId);          // id IS the PointId (per-row attribution across a batch)
        Assert.Equal(new DateOnly(2026, 8, 17), r.Date);
        Assert.Equal(-8.699m, r.Volume);
    }

    [Fact]
    public void PointVolume_DropsWhenIdOrDateMissing()
    {
        Assert.Null(PointVolumeRow.From(E("""{ "volume": 1, "date": "2026-08-17" }"""), Unit())); // no id
        Assert.Null(PointVolumeRow.From(E("""{ "id": 691, "volume": 1 }"""), Unit()));            // no date
    }
}
