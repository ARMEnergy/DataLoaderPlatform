using DataLoader.Vulcan;
using Xunit;

namespace DataLoader.Vulcan.Tests;

public class VulcanQueryBuilderTests
{
    [Fact]
    public void FirstRun_NoWatermark_NoDedup_SelectsWholeTable()
    {
        var sql = VulcanQueryBuilder.Build(VulcanTableSpec.UnderConstruction, watermark: null);
        Assert.Equal("SELECT * FROM vdl.under_construction", sql);
    }

    [Fact]
    public void Incremental_NoDedup_AddsInclusiveWatermarkFilter()
    {
        var sql = VulcanQueryBuilder.Build(VulcanTableSpec.LngProjects, new DateOnly(2026, 1, 15));
        Assert.Equal("SELECT * FROM vdl.lng_projects WHERE modified_at >= '2026-01-15'", sql);
    }

    [Fact]
    public void Incremental_Dedup_WrapsRowNumberAndFilters()
    {
        var sql = VulcanQueryBuilder.Build(VulcanTableSpec.DataCenters, new DateOnly(2026, 1, 15));
        Assert.Equal(
            "SELECT * FROM (SELECT *, ROW_NUMBER() OVER (PARTITION BY synmax_id ORDER BY modified_at DESC) AS rn " +
            "FROM vdl.datacenters WHERE modified_at >= '2026-01-15') t WHERE t.rn = 1",
            sql);
    }

    [Fact]
    public void FirstRun_Dedup_WrapsRowNumberWithoutWhere()
    {
        var sql = VulcanQueryBuilder.Build(VulcanTableSpec.ProjectRankings, watermark: null);
        Assert.Equal(
            "SELECT * FROM (SELECT *, ROW_NUMBER() OVER (PARTITION BY synmax_id ORDER BY date_updated DESC) AS rn " +
            "FROM vdl.project_rankings) t WHERE t.rn = 1",
            sql);
    }

    // --- BuildPage: paging clause appended to the OUTERMOST query ---

    [Fact]
    public void BuildPage_NonDedup_WithWatermark_AppendsClauseAfterWhere()
    {
        var sql = VulcanQueryBuilder.BuildPage(
            VulcanTableSpec.LngProjects, new DateOnly(2026, 1, 15), offset: 100000, pageSize: 50000);
        Assert.Equal(
            "SELECT * FROM vdl.lng_projects WHERE modified_at >= '2026-01-15' " +
            "ORDER BY plant_name, phase_number OFFSET 100000 ROWS FETCH NEXT 50000 ROWS ONLY",
            sql);
    }

    [Fact]
    public void BuildPage_NonDedup_NoWatermark_AppendsClauseNoWhere()
    {
        var sql = VulcanQueryBuilder.BuildPage(
            VulcanTableSpec.LngProjects, watermark: null, offset: 0, pageSize: 50000);
        Assert.Equal(
            "SELECT * FROM vdl.lng_projects " +
            "ORDER BY plant_name, phase_number OFFSET 0 ROWS FETCH NEXT 50000 ROWS ONLY",
            sql);
    }

    [Fact]
    public void BuildPage_Dedup_WithWatermark_AppendsClauseAfterOuterRnFilter()
    {
        var sql = VulcanQueryBuilder.BuildPage(
            VulcanTableSpec.MetadataHistory, new DateOnly(2026, 1, 15), offset: 0, pageSize: 50000);
        Assert.Equal(
            "SELECT * FROM (SELECT *, ROW_NUMBER() OVER (PARTITION BY synmax_id ORDER BY date_eia_updated DESC) AS rn " +
            "FROM vdl.metadata_history WHERE date_eia_updated >= '2026-01-15') t WHERE t.rn = 1 " +
            "ORDER BY synmax_id OFFSET 0 ROWS FETCH NEXT 50000 ROWS ONLY",
            sql);
        // The ROW_NUMBER subquery itself carries no OFFSET — paging is on the outermost query only.
        var innerEnd = sql.IndexOf(") t WHERE t.rn = 1", StringComparison.Ordinal);
        Assert.DoesNotContain("OFFSET", sql.Substring(0, innerEnd));
    }

    [Fact]
    public void BuildPage_Dedup_NoWatermark_AppendsClauseAfterOuterRnFilter()
    {
        var sql = VulcanQueryBuilder.BuildPage(
            VulcanTableSpec.ProjectRankings, watermark: null, offset: 0, pageSize: 50000);
        Assert.Equal(
            "SELECT * FROM (SELECT *, ROW_NUMBER() OVER (PARTITION BY synmax_id ORDER BY date_updated DESC) AS rn " +
            "FROM vdl.project_rankings) t WHERE t.rn = 1 " +
            "ORDER BY synmax_id OFFSET 0 ROWS FETCH NEXT 50000 ROWS ONLY",
            sql);
    }

    [Fact]
    public void BuildPage_Offset_RendersAsInvariantIntegerLiteral()
    {
        // Guards against culture-specific thousands separators (e.g. "100,000" or "100.000").
        var sql = VulcanQueryBuilder.BuildPage(
            VulcanTableSpec.UnderConstruction, watermark: null, offset: 100000, pageSize: 50000);
        Assert.Contains("OFFSET 100000 ROWS FETCH NEXT 50000 ROWS ONLY", sql);
        Assert.DoesNotContain("100,000", sql);
        Assert.DoesNotContain("100.000", sql);
    }
}
