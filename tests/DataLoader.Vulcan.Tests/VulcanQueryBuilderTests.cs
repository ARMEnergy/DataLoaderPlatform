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
}
