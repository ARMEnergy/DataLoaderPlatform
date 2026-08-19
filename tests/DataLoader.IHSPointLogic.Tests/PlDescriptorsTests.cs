using Xunit;

namespace DataLoader.IHSPointLogic.Tests;

/// <summary>
/// The 25-descriptor registry and its §2.2 invariants. The real invariant checks live in the
/// <c>PlDescriptors</c> static constructor (which runs, and must NOT throw, on first access to
/// <see cref="PlDescriptors.All"/>). These tests re-assert every §2.2 rule over the shipped registry so
/// a mis-wired descriptor (archetype↔tier↔pathparam↔refprovider↔reportdate drift) is caught here, and
/// prove the archetype→invariant predicate independently by replaying it against deliberately malformed
/// descriptors.
/// </summary>
public class PlDescriptorsTests
{
    [Fact]
    public void Registry_HasExactly25Descriptors_AndStaticCtorDidNotThrow()
    {
        // Touching All triggers the static ctor; if its own invariant checks threw this line would fail.
        Assert.Equal(25, PlDescriptors.All.Length);
        Assert.Equal(25, PlDescriptors.AllIds.Length);
    }

    [Fact]
    public void Registry_EndpointIds_AreUnique_CaseInsensitively()
    {
        var distinct = PlDescriptors.All.Select(d => d.EndpointId).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        Assert.Equal(25, distinct);
    }

    [Fact]
    public void Registry_TierComposition_Is19_3_3()
    {
        Assert.Equal(19, PlDescriptors.All.Count(d => d.Tier == 0));
        Assert.Equal(3, PlDescriptors.All.Count(d => d.Tier == 1));
        Assert.Equal(3, PlDescriptors.All.Count(d => d.Tier == 2));
    }

    // ---------------------------------------------------------------- §2.2 rule: {id} token ⟺ substituted id PathParam

    [Fact]
    public void Every_IdToken_MatchesASubstitutedIdPathParam_AndViceVersa()
    {
        foreach (var d in PlDescriptors.All)
        {
            var hasIdToken = d.PathTemplate.Contains("{id}", StringComparison.Ordinal);
            var wantsIdParam = d.PathParam is PlPathParam.StateId or PlPathParam.PointTypeId
                or PlPathParam.RegionId or PlPathParam.SubRegionId;
            Assert.True(hasIdToken == wantsIdParam,
                $"{d.EndpointId}: '{{id}}' present={hasIdToken} but substituted-id PathParam={wantsIdParam}");
        }
    }

    // ---------------------------------------------------------------- §2.2 rule: archetype ↔ tier / pathparam / refprovider / reportdate

    [Fact]
    public void ArchetypeA_And_B_AreTier0_NoParam_NoRefProvider_NoReportDate()
    {
        foreach (var d in PlDescriptors.All.Where(x =>
                     x.Archetype is PlArchetype.LatestLookup or PlArchetype.GoForwardSnapshot))
        {
            Assert.Equal(0, d.Tier);
            Assert.Equal(PlPathParam.None, d.PathParam);
            Assert.Null(d.RefProvider);
            Assert.False(d.UsesReportDate);
        }
    }

    [Fact]
    public void ArchetypeC_IsTier1_HasIdToken_StateOrPointTypeOrRegionParam_RefProviderSet_NoReportDate()
    {
        foreach (var d in PlDescriptors.All.Where(x => x.Archetype == PlArchetype.DiscoveryLookup))
        {
            Assert.Equal(1, d.Tier);
            Assert.Contains("{id}", d.PathTemplate);
            Assert.Contains(d.PathParam, new[] { PlPathParam.StateId, PlPathParam.PointTypeId, PlPathParam.RegionId });
            Assert.NotNull(d.RefProvider);
            Assert.False(d.UsesReportDate);
        }
    }

    [Fact]
    public void ArchetypeD_IsTier2_HasIdToken_RegionOrSubRegionParam_UsesReportDate_RefProviderSet()
    {
        foreach (var d in PlDescriptors.All.Where(x => x.Archetype == PlArchetype.DiscoveryDatedFact))
        {
            Assert.Equal(2, d.Tier);
            Assert.Contains("{id}", d.PathTemplate);
            Assert.Contains(d.PathParam, new[] { PlPathParam.RegionId, PlPathParam.SubRegionId });
            Assert.True(d.UsesReportDate);
            Assert.NotNull(d.RefProvider);
            Assert.Equal(PlReportDateBasis.ParamInjected, d.ReportDateBasis);
        }
    }

    [Fact]
    public void ArchetypeE_IsTier2_PointBatchParam_PointRefProvider()
    {
        var e = Assert.Single(PlDescriptors.All, x => x.Archetype == PlArchetype.BatchedFact);
        Assert.Equal(2, e.Tier);
        Assert.Equal(PlPathParam.PointBatch, e.PathParam);
        Assert.Equal("Point", e.RefProvider);
        Assert.Equal("PointVolume", e.EndpointId);
    }

    // ---------------------------------------------------------------- §2.2 rule: StampUtcRunDate only on the two demandforecast ids

    [Fact]
    public void StampUtcRunDate_IsExactlyTheTwoDemandForecastEndpoints()
    {
        var stamped = PlDescriptors.All
            .Where(d => d.ReportDateBasis == PlReportDateBasis.StampUtcRunDate)
            .Select(d => d.EndpointId)
            .OrderBy(x => x)
            .ToArray();
        Assert.Equal(new[] { "DemandForecastRegion", "DemandForecastUsLower48" }, stamped);
    }

    [Fact]
    public void SubRegion_DatedFact_ReusesTheRegionPath_WithASubRegionParam()
    {
        // The distinct /subregion/ path 404s, so SD-by-subregion deliberately reuses /region/{id}.
        var region = PlDescriptors.SupplyAndDemandByRegion;
        var sub = PlDescriptors.SupplyAndDemandBySubRegion;
        Assert.Equal(region.PathTemplate, sub.PathTemplate);
        Assert.Equal(PlPathParam.RegionId, region.PathParam);
        Assert.Equal(PlPathParam.SubRegionId, sub.PathParam);
        Assert.Equal("Subregion", sub.RefProvider);
    }

    // ---------------------------------------------------------------- the invariant predicate itself (replayed on malformed descriptors)

    // Mirrors the two structural §2.2 gates the static ctor enforces, so we can exercise them against a
    // deliberately-malformed descriptor (the real PlDescriptors.All array is fixed and cannot be mutated).
    private static bool IdTokenMatchesParam(PlEndpointDescriptor d)
    {
        var hasIdToken = d.PathTemplate.Contains("{id}", StringComparison.Ordinal);
        var wantsIdParam = d.PathParam is PlPathParam.StateId or PlPathParam.PointTypeId
            or PlPathParam.RegionId or PlPathParam.SubRegionId;
        return hasIdToken == wantsIdParam;
    }

    private static bool StampBasisOnlyOnDemandForecast(PlEndpointDescriptor d)
    {
        var isStamp = d.ReportDateBasis == PlReportDateBasis.StampUtcRunDate;
        var isDf = d.EndpointId is "DemandForecastRegion" or "DemandForecastUsLower48";
        return isStamp == isDf;
    }

    [Fact]
    public void InvariantPredicate_RejectsIdTokenWithoutParam_AndParamWithoutIdToken()
    {
        // {id} in the template but PathParam None → invalid.
        var tokenNoParam = PlDescriptors.Region with { PathTemplate = "cs/v1/x/{id}", PathParam = PlPathParam.None };
        Assert.False(IdTokenMatchesParam(tokenNoParam));

        // Substituted id PathParam but no {id} in the template → invalid.
        var paramNoToken = PlDescriptors.Region with { PathTemplate = "cs/v1/x", PathParam = PlPathParam.StateId };
        Assert.False(IdTokenMatchesParam(paramNoToken));

        // A well-formed pairing passes.
        Assert.True(IdTokenMatchesParam(PlDescriptors.County));
        Assert.True(IdTokenMatchesParam(PlDescriptors.Region));
    }

    [Fact]
    public void InvariantPredicate_RejectsStampBasisOnANonDemandForecastEndpoint()
    {
        var badStamp = PlDescriptors.SupplyAndDemand with { ReportDateBasis = PlReportDateBasis.StampUtcRunDate };
        Assert.False(StampBasisOnlyOnDemandForecast(badStamp));

        // Removing the stamp from a real demand-forecast endpoint is also a violation.
        var missingStamp = PlDescriptors.DemandForecastRegion with { ReportDateBasis = PlReportDateBasis.PayloadCarried };
        Assert.False(StampBasisOnlyOnDemandForecast(missingStamp));

        Assert.True(StampBasisOnlyOnDemandForecast(PlDescriptors.DemandForecastRegion));
        Assert.True(StampBasisOnlyOnDemandForecast(PlDescriptors.SupplyAndDemand));
    }
}
