namespace DataLoader.Vulcan;

/// <summary>
/// Static description of one Vulcan source table: where to read it, which column
/// drives incremental loading, and whether latest-row-per-id deduplication is
/// needed. One instance per table; consumed by the work-unit provider to build
/// the SQL query sent to the API.
/// </summary>
public sealed class VulcanTableSpec
{
    /// <summary>Loader-facing table id (matches EnabledTables and dbo.LoadWatermark.TableName), e.g. "datacenters".</summary>
    public required string TableId { get; init; }

    /// <summary>Fully-qualified source datalink table, e.g. "vdl.datacenters".</summary>
    public required string SourceTable { get; init; }

    /// <summary>Column used as the incremental watermark, e.g. "modified_at".</summary>
    public required string WatermarkColumn { get; init; }

    /// <summary>When set, wrap with ROW_NUMBER() PARTITION BY this column to keep the latest row per id.</summary>
    public string? DedupPartitionColumn { get; init; }

    public static readonly VulcanTableSpec UnderConstruction = new()
    {
        TableId = "under_construction", SourceTable = "vdl.under_construction", WatermarkColumn = "date_image"
    };
    public static readonly VulcanTableSpec DataCenters = new()
    {
        TableId = "datacenters", SourceTable = "vdl.datacenters", WatermarkColumn = "modified_at",
        DedupPartitionColumn = "synmax_id"
    };
    public static readonly VulcanTableSpec LngProjects = new()
    {
        TableId = "lng_projects", SourceTable = "vdl.lng_projects", WatermarkColumn = "modified_at"
    };
    public static readonly VulcanTableSpec ProjectRankings = new()
    {
        TableId = "project_rankings", SourceTable = "vdl.project_rankings", WatermarkColumn = "date_updated",
        DedupPartitionColumn = "synmax_id"
    };
    public static readonly VulcanTableSpec MetadataHistory = new()
    {
        TableId = "metadata_history", SourceTable = "vdl.metadata_history", WatermarkColumn = "date_eia_updated",
        DedupPartitionColumn = "synmax_id"
    };
}
