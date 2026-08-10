using System.Globalization;
using DataLoader.Core.Abstractions;

namespace DataLoader.StormVista;

// =============================================================================
// Target rows — the source readers emit these directly (identity transformer).
// SCHEMA v2 (dbo, normalized): the facts hang off dbo.FileLog, so each row carries
// only its FileLogId (provenance) + its own leaf attributes. Model/Cycle/WddType/
// InitDate/Endpoint/RegionSet live on FileLog, reached via FileLogId — never
// duplicated on the fact rows. Field order below maps 1:1 to the TVP column
// contract in 002_CreateStormVistaTvpTypes.sql.
// =============================================================================

/// <summary>
/// A fact row whose parent <c>dbo.FileLog</c> id is stamped by the source reader
/// after the per-request FileLog upsert (design load-flow step 4).
/// </summary>
public interface IStormVistaFactRow
{
    int FileLogId { get; set; }
}

/// <summary>
/// One national daily WDD row, targeting <c>dbo.DailyWdd</c> via <c>dbo.DailyWddTvp</c>
/// <c>(FileLogId, ValidDate, FlagCode, Value)</c>. The proc resolves FlagCode → FlagId.
/// </summary>
public sealed class DailyWddRow : IStormVistaFactRow
{
    /// <summary>Parent FileLog id; stamped by the source reader after the FileLog upsert.</summary>
    public int FileLogId { get; set; }
    public required DateOnly ValidDate { get; init; }
    public required int FlagCode { get; init; }
    public decimal? Value { get; init; }
}

/// <summary>
/// One regional WDD row (wide → long unpivot), targeting <c>dbo.RegionalWdd</c> via
/// <c>dbo.RegionalWddTvp</c> <c>(FileLogId, RegionName, ValidDate, Value)</c>.
/// Forecast-only — no Flag. The proc resolves RegionName → RegionId scoped by the
/// parent FileLog's RegionSet.
/// </summary>
public sealed class RegionalWddRow : IStormVistaFactRow
{
    /// <summary>Parent FileLog id; stamped by the source reader after the FileLog upsert.</summary>
    public int FileLogId { get; set; }
    public required string RegionName { get; init; }
    public required DateOnly ValidDate { get; init; }
    public decimal? Value { get; init; }
}

// =============================================================================
// Work units — one CSV = one unit. The two-zone Key (§4) is precomputed by the
// provider and stored, because it depends on the run context (today/age/hot
// suffix), not just the unit's own identity. Each unit still carries every enum
// value: they build the URL, the two-zone key, and the FileLog natural key.
// =============================================================================

/// <summary>Natural-key fields the FileLog upsert needs, common to both feeds.</summary>
public interface IStormVistaAuditContext
{
    string Model { get; }
    DateOnly InitDate { get; }
    string Cycle { get; }
    string WddType { get; }
    string? RegionSetCode { get; }
}

/// <summary>One daily national CSV: <c>/model-data/{model}/{date}/{cycle}z/wdd/{type}-daily.csv</c>.</summary>
public sealed class DailyWorkUnit : WorkUnit, IStormVistaAuditContext
{
    public required string Model { get; init; }
    public required DateOnly InitDate { get; init; }
    public required string Cycle { get; init; }
    public required string WddType { get; init; }

    /// <summary>Precomputed two-zone key (design §4).</summary>
    public required string KeyValue { get; init; }

    public override string Key => KeyValue;

    public override string DisplayName =>
        $"Daily {Model} {InitDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture)} {Cycle}z {WddType}";

    string? IStormVistaAuditContext.RegionSetCode => null;
}

/// <summary>One regional/weekly CSV: <c>/model-data/{wkmodel}/{date}/{cycle}z/wdd/{type}_reg{code}.csv</c>.</summary>
public sealed class RegionalWorkUnit : WorkUnit, IStormVistaAuditContext
{
    public required string WkModel { get; init; }
    public required DateOnly InitDate { get; init; }
    public required string Cycle { get; init; }
    public required string WddType { get; init; }

    /// <summary>Region-set code — <c>"3" | "5" | "9" | "iso"</c> (string, because 'iso' is non-numeric).</summary>
    public required string RegionSetCode { get; init; }

    /// <summary>Precomputed two-zone key (design §4).</summary>
    public required string KeyValue { get; init; }

    public override string Key => KeyValue;

    public override string DisplayName =>
        $"Regional {WkModel} {InitDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture)} {Cycle}z {WddType} reg{RegionSetCode}";

    string IStormVistaAuditContext.Model => WkModel;
    string? IStormVistaAuditContext.RegionSetCode => RegionSetCode;
}

// =============================================================================
// Reference records — the seeded dbo.* reference rows the loader reads at run
// start to drive enumeration and validate regional headers (design §5).
// =============================================================================

public sealed record ModelRef(string ModelSlug, bool SupportsDaily, bool SupportsRegional, bool IsExperimental);

public sealed record CycleRef(string CycleCode, bool SupportsDaily, bool SupportsRegional);

public sealed record RegionSetRef(string RegionSetCode, string Kind);
