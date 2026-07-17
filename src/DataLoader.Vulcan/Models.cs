namespace DataLoader.Vulcan.Models;

public sealed class UnderConstructionRow
{
    public string SynmaxId { get; set; } = string.Empty;
    public string? PlantId { get; set; }
    public string? PlantName { get; set; }
    public string? Technology { get; set; }
    public double? NameplateCapacity { get; set; }
    public string? VulcanStatus { get; set; }
    public double? ProjectRank { get; set; }
    public DateTime? DateVulcanEarliestOnline { get; set; }
    public DateTime? DateVulcanLatestOnline { get; set; }
    public string? StateCode { get; set; }
    public string? BalancingAuthority { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public DateTime? DateVulcanStatusChange { get; set; }
    public DateTime? DateImage { get; set; }
}

public sealed class DataCenterRow
{
    public string SynmaxId { get; set; } = string.Empty;
    public string? PlantId { get; set; }
    public string? PlantName { get; set; }
    public string? UnitId { get; set; }
    public string? UnitName { get; set; }
    public string? OwnerName { get; set; }
    public string? DataCenterType { get; set; }
    public double? UnitCapacity { get; set; }
    public string? VulcanStatus { get; set; }
    public string? StateCode { get; set; }
    public string? BalancingAuthority { get; set; }
    public DateTime? DateVulcanEarliestOnline { get; set; }
    public DateTime? DateVulcanLatestOnline { get; set; }
    public DateTime? ModifiedAt { get; set; }
}

public sealed class LngProjectRow
{
    public string PlantName { get; set; } = string.Empty;
    public int PhaseNumber { get; set; }
    public string? EntityName { get; set; }
    public string? Technology { get; set; }
    public double? NameplateCapacity { get; set; }
    public string? CapacityUnit { get; set; }
    public int? Trains { get; set; }
    public string? VulcanStatus { get; set; }
    public DateTime? DateVulcanEarliestOnline { get; set; }
    public DateTime? DateVulcanLatestOnline { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public string? Observation { get; set; }
    public DateTime? DateVulcanStatusChange { get; set; }
    public DateTime? DateImage { get; set; }
    public DateTime? ModifiedAt { get; set; }
}

public sealed class ProjectRankingRow
{
    public string SynmaxId { get; set; } = string.Empty;
    public long? PlantId { get; set; }
    public string? GeneratorId { get; set; }
    public double? FinalRank { get; set; }
    public DateTime? DateVulcanProposedV2Online { get; set; }
    public DateTime? DateVulcanProposedOnline { get; set; }
    public DateTime? DateUpdated { get; set; }
}

public sealed class MetadataHistoryRow
{
    public string SynmaxId { get; set; } = string.Empty;
    public string? PlantName { get; set; }
    public string? Technology { get; set; }
    public double? NameplateCapacity { get; set; }
    public string? StateCode { get; set; }
    public string? County { get; set; }
    public DateTime? DatePlannedOperations { get; set; }
    public string? PlantStatus { get; set; }
    public DateTime? DateEiaUpdated { get; set; }
    public double? DaysPlannedOperationMinusFirstSeenPlannedOperation { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public string? BalancingAuthorityCode { get; set; }
    public string? SectorName { get; set; }
}
