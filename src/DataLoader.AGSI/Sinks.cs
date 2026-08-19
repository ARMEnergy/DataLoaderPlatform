using System.Data;
using DataLoader.Core.Sinks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.AGSI;

// =============================================================================
// The two SQL sinks. Each bulk-merges its FileLogId-stamped rows into arm.<Table>
// via arm.usp_BulkMerge<Table>(@Records arm.<Table>Tvp). Column ORDER in each
// BuildTable mirrors sql/AGSI/002 EXACTLY — CRITICAL: the two TVPs differ in
// FileLogId position (LAST for the entity table, FIRST for the storage table).
// Each de-dups the batch on its merge key before building the TVP so an in-batch
// duplicate cannot break the MERGE. Concurrent-MERGE serialization is applied
// automatically by SqlSinkBase (SqlWriteGate); the two procs are distinct keys, so
// the entity and storage merges never serialize against each other (design §10).
// =============================================================================

/// <summary>Common base wiring for an AGSI sink.</summary>
public abstract class AgsiSqlSinkBase<TRow> : SqlSinkBase<TRow>
{
    private readonly AgsiSettings _settings;

    protected AgsiSqlSinkBase(IOptions<AgsiSettings> settings, ILogger logger) : base(logger)
    {
        _settings = settings.Value;
    }

    protected override string GetConnectionString() => _settings.ConnectionString;
    protected override bool ProcedureReturnsRowCount => true;

    protected static DateTime D(DateOnly d) => d.ToDateTime(TimeOnly.MinValue);
}

// -------------------------------------------------------------------- endpoint 1: GasStorageEntity
/// <summary>
/// arm.GasStorageEntity — key <c>Code</c>. TVP column order:
/// <c>(Code, Name, ParentCode, ParentName, FileLogId)</c> — <b>FileLogId LAST</b>
/// (matches arm.GasStorageEntityTvp in sql/AGSI/002).
/// </summary>
public sealed class GasStorageEntitySqlSink : AgsiSqlSinkBase<GasStorageEntityRow>
{
    public GasStorageEntitySqlSink(IOptions<AgsiSettings> settings, ILogger<GasStorageEntitySqlSink> logger) : base(settings, logger) { }
    protected override string StoredProcedureName => "arm.usp_BulkMergeGasStorageEntity";
    protected override string TableValuedParameterType => "arm.GasStorageEntityTvp";

    protected override DataTable BuildTable(IReadOnlyList<GasStorageEntityRow> rows)
    {
        var deduped = rows
            .GroupBy(r => r.Code, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last());

        // Order: Code, Name, ParentCode, ParentName, FileLogId — FileLogId LAST.
        var t = new DataTable();
        t.Columns.Add("Code", typeof(string));
        t.Columns.Add("Name", typeof(string));
        t.Columns.Add("ParentCode", typeof(string));
        t.Columns.Add("ParentName", typeof(string));
        t.Columns.Add("FileLogId", typeof(int));

        foreach (var r in deduped)
            t.Rows.Add(r.Code, r.Name, r.ParentCode, r.ParentName, r.FileLogId);
        return t;
    }
}

// -------------------------------------------------------------------- endpoint 2: GasStorage
/// <summary>
/// arm.GasStorage — key <c>(EntityId, GasDayStart)</c>. TVP column order (22 cols):
/// <c>(FileLogId, EntityId, Date, Gas_Day, UpdatedAt, GasDayStart, GasDayEnd,
/// GasInStorage, Consumption, ConsumptionFull, Injection, Withdrawal, NetWithdrawal,
/// WorkingGasVolume, InjectionCapacity, WithdrawalCapacity, ContractedCapacity,
/// AvailableCapacity, CoveredCapacity, Status, Trend, Full)</c> — <b>FileLogId
/// FIRST, EntityId SECOND</b> (matches arm.GasStorageTvp in sql/AGSI/002; Name/Code/Url
/// were normalized out to EntityId → the entity dimension).
/// </summary>
public sealed class GasStorageSqlSink : AgsiSqlSinkBase<GasStorageRow>
{
    public GasStorageSqlSink(IOptions<AgsiSettings> settings, ILogger<GasStorageSqlSink> logger) : base(settings, logger) { }
    protected override string StoredProcedureName => "arm.usp_BulkMergeGasStorage";
    protected override string TableValuedParameterType => "arm.GasStorageTvp";

    protected override DataTable BuildTable(IReadOnlyList<GasStorageRow> rows)
    {
        // De-dup on the natural/MERGE key (EntityId, GasDayStart), matching the proc.
        var deduped = rows
            .GroupBy(r => (r.EntityId, r.GasDayStart))
            .Select(g => g.Last());

        // Order: FileLogId FIRST, EntityId SECOND, then the remaining 20 business columns
        // in arm.GasStorageTvp order — 22 columns total. Bound to the TVP BY POSITION.
        var t = new DataTable();
        t.Columns.Add("FileLogId", typeof(int));
        t.Columns.Add("EntityId", typeof(int));
        t.Columns.Add("Date", typeof(DateTime));
        t.Columns.Add("Gas_Day", typeof(DateTime));
        t.Columns.Add("UpdatedAt", typeof(DateTime));
        t.Columns.Add("GasDayStart", typeof(DateTime));
        t.Columns.Add("GasDayEnd", typeof(DateTime));
        t.Columns.Add("GasInStorage", typeof(decimal));
        t.Columns.Add("Consumption", typeof(decimal));
        t.Columns.Add("ConsumptionFull", typeof(decimal));
        t.Columns.Add("Injection", typeof(decimal));
        t.Columns.Add("Withdrawal", typeof(decimal));
        t.Columns.Add("NetWithdrawal", typeof(decimal));
        t.Columns.Add("WorkingGasVolume", typeof(decimal));
        t.Columns.Add("InjectionCapacity", typeof(decimal));
        t.Columns.Add("WithdrawalCapacity", typeof(decimal));
        t.Columns.Add("ContractedCapacity", typeof(decimal));
        t.Columns.Add("AvailableCapacity", typeof(decimal));
        t.Columns.Add("CoveredCapacity", typeof(decimal));
        t.Columns.Add("Status", typeof(string));
        t.Columns.Add("Trend", typeof(decimal));
        t.Columns.Add("Full", typeof(decimal));

        foreach (var r in deduped)
            t.Rows.Add(
                r.FileLogId, r.EntityId, D(r.Date), D(r.GasDay),
                DbNullable(r.UpdatedAt), D(r.GasDayStart), D(r.GasDayEnd),
                DbNullable(r.GasInStorage), DbNullable(r.Consumption), DbNullable(r.ConsumptionFull),
                DbNullable(r.Injection), DbNullable(r.Withdrawal), DbNullable(r.NetWithdrawal),
                DbNullable(r.WorkingGasVolume), DbNullable(r.InjectionCapacity), DbNullable(r.WithdrawalCapacity),
                DbNullable(r.ContractedCapacity), DbNullable(r.AvailableCapacity), DbNullable(r.CoveredCapacity),
                r.Status, DbNullable(r.Trend), DbNullable(r.Full));
        return t;
    }
}
