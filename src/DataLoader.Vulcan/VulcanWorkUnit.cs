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

    /// <summary>
    /// The logical un-paged query, stamped by the provider and retained for
    /// logging/diagnostics. The reader does not read this: it paginates via
    /// <see cref="VulcanQueryBuilder.BuildPage"/> using <see cref="Spec"/>/<see cref="Watermark"/>.
    /// </summary>
    public required string Query { get; init; }
    public required VulcanTableSpec Spec { get; init; }
    public required DateOnly? Watermark { get; init; }

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
    // SECURITY: every interpolated identifier below (SourceTable, WatermarkColumn,
    // DedupPartitionColumn, OrderByColumns) is a compile-time constant from VulcanTableSpec, and the
    // watermark is an invariant-formatted DateOnly. The offset/pageSize appended by BuildPage are
    // validated non-negative integers (caller guarantees offset >= 0 and pageSize >= 1) rendered as
    // literals via CultureInfo.InvariantCulture. Never interpolate user/config input here.
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

    /// <summary>
    /// Builds the core query for <paramref name="spec"/> and appends a deterministic
    /// ORDER BY … OFFSET … FETCH NEXT … clause to the OUTERMOST query for page-by-page
    /// reads. Caller guarantees <paramref name="offset"/> &gt;= 0 and <paramref name="pageSize"/> &gt;= 1.
    /// </summary>
    public static string BuildPage(VulcanTableSpec spec, DateOnly? watermark, int offset, int pageSize)
    {
        var core = Build(spec, watermark);
        var orderBy = string.Join(", ", spec.OrderByColumns);
        return
            $"{core} ORDER BY {orderBy} " +
            $"OFFSET {offset.ToString(CultureInfo.InvariantCulture)} ROWS " +
            $"FETCH NEXT {pageSize.ToString(CultureInfo.InvariantCulture)} ROWS ONLY";
    }
}
