using System.Data;
using DataLoader.Core.Sinks;
using Microsoft.Extensions.Logging;

namespace DataLoader.PJM;

// =============================================================================
// The original two SQL sinks (design §8; the six §14.9 sinks are in SinksLoad.cs). Each MERGEs one unit — a single batch, never split —
// into dbo.<table> via dbo.usp_MergePjm<X>(@Records dbo.Pjm<X>Type). BuildTable's
// columns match design §7.4 by NAME + ORDER + TYPE (tvp-contract-check). Source
// columns only; CreatedAtUtc/ModifiedAtUtc are never TVP columns.
//
// ⚠ NO DE-DUPLICATION, deliberately (design §4.6): the reader fails a unit with a
// duplicate key before it gets here, and the proc THROWs on one as defence in depth.
// A silent de-dup would destroy a real row or hide that the key is wrong.
// =============================================================================

/// <summary>Common wiring for a PJM sink.</summary>
public abstract class PjmSqlSinkBase<TRow> : SqlSinkBase<TRow>
{
    private readonly PjmSettings _settings;

    protected PjmSqlSinkBase(PjmSettings settings, ILogger logger) : base(logger)
    {
        _settings = settings;
    }

    protected override string GetConnectionString() => _settings.ConnectionString;

    /// <summary>The procs <c>SELECT @@ROWCOUNT</c> of CHANGED rows; a no-op re-read records 0, correctly.</summary>
    protected override bool ProcedureReturnsRowCount => true;

    internal DataTable BuildTableForTests(IReadOnlyList<TRow> rows) => BuildTable(rows);

    protected static DataColumn Col(DataTable t, string name, Type type, bool allowNull)
    {
        var c = t.Columns.Add(name, type);
        c.AllowDBNull = allowNull;
        return c;
    }
}

/// <summary>
/// <c>dbo.pjm_fuel_mix</c> via <c>dbo.usp_MergePjmFuelMix</c> / <c>dbo.PjmFuelMixType</c>. Column order:
/// <c>(datetime_beginning_utc, datetime_beginning_ept, fuel_type, mw, fuel_percentage_of_total, is_renewable)</c>.
/// </summary>
public sealed class PjmFuelMixSqlSink : PjmSqlSinkBase<PjmFuelMixRow>
{
    public PjmFuelMixSqlSink(PjmSettings settings, ILogger<PjmFuelMixSqlSink> logger) : base(settings, logger) { }

    protected override string StoredProcedureName => "dbo.usp_MergePjmFuelMix";
    protected override string TableValuedParameterType => "dbo.PjmFuelMixType";

    protected override DataTable BuildTable(IReadOnlyList<PjmFuelMixRow> rows)
    {
        var t = new DataTable();
        Col(t, "datetime_beginning_utc", typeof(DateTime), allowNull: false);   // DATETIME2(0)  NOT NULL
        Col(t, "datetime_beginning_ept", typeof(DateTime), allowNull: false);   // DATETIME2(0)  NOT NULL
        Col(t, "fuel_type", typeof(string), allowNull: false);                  // VARCHAR(50)   NOT NULL
        Col(t, "mw", typeof(decimal), allowNull: true);                         // DECIMAL(18,4) NULL
        Col(t, "fuel_percentage_of_total", typeof(decimal), allowNull: true);   // DECIMAL(9,6)  NULL
        Col(t, "is_renewable", typeof(bool), allowNull: true);                  // BIT           NULL

        foreach (var r in rows)
            t.Rows.Add(
                r.DatetimeBeginningUtc,
                r.DatetimeBeginningEpt,
                r.FuelType,
                DbNullable(r.Mw),
                DbNullable(r.FuelPercentageOfTotal),
                DbNullable(r.IsRenewable));
        return t;
    }
}

/// <summary>
/// <c>dbo.pjm_load</c> via <c>dbo.usp_MergePjmLoad</c> / <c>dbo.PjmLoadType</c>. Column order:
/// <c>(datetime_beginning_utc, datetime_beginning_ept, area, instantaneous_load)</c>.
/// </summary>
public sealed class PjmLoadSqlSink : PjmSqlSinkBase<PjmLoadRow>
{
    public PjmLoadSqlSink(PjmSettings settings, ILogger<PjmLoadSqlSink> logger) : base(settings, logger) { }

    protected override string StoredProcedureName => "dbo.usp_MergePjmLoad";
    protected override string TableValuedParameterType => "dbo.PjmLoadType";

    protected override DataTable BuildTable(IReadOnlyList<PjmLoadRow> rows)
    {
        var t = new DataTable();
        Col(t, "datetime_beginning_utc", typeof(DateTime), allowNull: false);   // DATETIME2(0)  NOT NULL
        Col(t, "datetime_beginning_ept", typeof(DateTime), allowNull: false);   // DATETIME2(0)  NOT NULL
        Col(t, "area", typeof(string), allowNull: false);                       // VARCHAR(50)   NOT NULL
        Col(t, "instantaneous_load", typeof(decimal), allowNull: true);         // DECIMAL(18,4) NULL

        foreach (var r in rows)
            t.Rows.Add(
                r.DatetimeBeginningUtc,
                r.DatetimeBeginningEpt,
                r.Area,
                DbNullable(r.InstantaneousLoad));
        return t;
    }
}
