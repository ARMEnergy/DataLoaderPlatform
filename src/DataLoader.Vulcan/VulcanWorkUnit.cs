using System.Globalization;
using DataLoader.Core.Abstractions;

namespace DataLoader.Vulcan;

/// <summary>
/// One "load this Vulcan table for this run date" unit. Shared by all five
/// per-table pipelines; the resolved SQL query it carries is what differs.
/// </summary>
public sealed class VulcanWorkUnit : WorkUnit
{
    public required string TableId { get; init; }
    public required DateOnly RunDate { get; init; }
    public required string Query { get; init; }

    public override string Key => $"vulcan:table={TableId};date={RunDate:yyyy-MM-dd}";
    public override string DisplayName => $"Vulcan {TableId} @ {RunDate:yyyy-MM-dd}";
}

/// <summary>
/// Builds the SQL query sent to query_datalinks for one table. Incremental filter
/// is inclusive (&gt;=) so day-granularity watermarks never skip same-day updates;
/// re-pulled boundary rows are harmless because the sink MERGEs by natural key.
/// </summary>
public static class VulcanQueryBuilder
{
    public static string Build(VulcanTableSpec spec, DateOnly? watermark)
    {
        var where = watermark is null
            ? string.Empty
            : $" WHERE {spec.WatermarkColumn} >= '{watermark.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}'";

        if (spec.DedupPartitionColumn is null)
            return $"SELECT * FROM {spec.SourceTable}{where}";

        // Keep only the latest row per id among the (optionally filtered) rows.
        return
            $"SELECT * FROM (SELECT *, ROW_NUMBER() OVER (PARTITION BY {spec.DedupPartitionColumn} " +
            $"ORDER BY {spec.WatermarkColumn} DESC) AS rn FROM {spec.SourceTable}{where}) t WHERE t.rn = 1";
    }
}
