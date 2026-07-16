namespace DataLoader.EnergyAspects.Models;

public sealed class DatasetMapping
{
    public int MappingId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string RequestString { get; set; } = string.Empty;
    public string Licensed { get; set; } = string.Empty;
    public List<int> DatasetIds { get; set; } = new();
    public bool IsLicensed => string.Equals(Licensed, "yes", StringComparison.OrdinalIgnoreCase);
}

public sealed class TimeseriesMetadata
{
    public string Country { get; set; } = string.Empty;
    public string CountryIso { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public string SubRegion { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Aspect { get; set; } = string.Empty;
    public string AspectSubtype { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string CategorySubtype { get; set; } = string.Empty;
    public string ForecastStartDate { get; set; } = string.Empty;
    public string Frequency { get; set; } = string.Empty;
    public string LifecycleStage { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string Unit { get; set; } = string.Empty;
    public string ReleaseDate { get; set; } = string.Empty;
}

public sealed class TimeseriesApiItem
{
    public int DatasetId { get; set; }
    public TimeseriesMetadata Metadata { get; set; } = new();
    public Dictionary<string, double?> Data { get; set; } = new();
    public Dictionary<string, string?> AdditionalFields { get; set; } = new();
}

public sealed class TimeseriesRecord
{
    public int DatasetId { get; set; }
    public DateTime DataDate { get; set; }
    public double? Value { get; set; }
}

public sealed class TimeseriesMetadataRecord
{
    public int MappingId { get; set; }
    public int DatasetId { get; set; }
    public string Country { get; set; } = string.Empty;
    public string CountryIso { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public string SubRegion { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Aspect { get; set; } = string.Empty;
    public string AspectSubtype { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string CategorySubtype { get; set; } = string.Empty;
    public DateTime? ForecastStartDate { get; set; }
    public string Frequency { get; set; } = string.Empty;
    public string LifecycleStage { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string Unit { get; set; } = string.Empty;
    public DateTime? ReleaseDate { get; set; }
    public string? Basin { get; set; }
    public string? PartnerSubRegion { get; set; }
    public string? Pipeline { get; set; }
    public string? EaIsoRto { get; set; }
}

/// <summary>
/// Bundle of rows produced by the transformer for one work unit — both the
/// value rows and the metadata rows. The sink handles both in one call.
/// </summary>
public sealed class TimeseriesBatch
{
    public IReadOnlyList<TimeseriesRecord> Data { get; init; } = Array.Empty<TimeseriesRecord>();
    public IReadOnlyList<TimeseriesMetadataRecord> Metadata { get; init; } = Array.Empty<TimeseriesMetadataRecord>();
}
