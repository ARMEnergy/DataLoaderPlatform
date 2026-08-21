using System.Data;
using DataLoader.Core.Abstractions;
using DataLoader.Core.Concurrency;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.IIR;

// =============================================================================
// Per-endpoint SQL sinks (×3). Each bulk-merges its FileLogId-stamped rows into
// arm.<Table> via its arm.usp_Upsert<Table>(@Rows arm.<Table>Tvp) proc. Column
// ORDER in every BuildTable mirrors sql/IIR/002 EXACTLY — FileLogId FIRST (a
// load-bearing contract; the TVP binds BY POSITION).
//
// NOTE: this loader does NOT reuse Core's SqlSinkBase because the IIR procs
//   * name their TVP parameter @Rows (SqlSinkBase uses @Records), and
//   * OfflineEvent takes an additional scalar @RunDate, and
//   * the plants pull is ~112k rows, which is chunked into MergeChunkSize-row
//     MERGE calls (SqlSinkBase issues one single MERGE and does NOT chunk).
// The base below still de-dups the batch on the MERGE key (last wins) and
// acquires SqlWriteGate per target proc, exactly like SqlSinkBase.
// =============================================================================

/// <summary>
/// Custom IIR sink base: de-dups the batch on the MERGE key, chunks it into
/// <see cref="IirSettings.MergeChunkSize"/>-row TVP MERGEs, and calls the endpoint's
/// <c>arm.usp_Upsert*</c> proc with a <c>@Rows</c> TVP under the <see cref="SqlWriteGate"/>
/// (one key per target proc). Subclasses supply the proc/TVP names, the int MERGE key selector, the
/// TVP DataTable builder (in 002 column order) and, optionally, extra scalar params.
/// </summary>
public abstract class IirSqlSinkBase<TRow> : ISink<TRow>
    where TRow : class
{
    private readonly string _connectionString;
    private readonly int _chunkSize;
    private readonly ILogger _logger;

    protected IirSqlSinkBase(IirSettings settings, ILogger logger)
    {
        _connectionString = settings.ConnectionString;
        _chunkSize = Math.Max(1, settings.MergeChunkSize);
        _logger = logger;
    }

    protected abstract string StoredProcedureName { get; }
    protected abstract string TableValuedParameterType { get; }

    /// <summary>The integer MERGE key used to de-dup the batch (last wins) before building the TVP.</summary>
    protected abstract int MergeKey(TRow row);

    /// <summary>Builds the TVP DataTable for one (already de-duped, chunked) set of rows — columns in 002 order.</summary>
    protected abstract DataTable BuildTable(IReadOnlyList<TRow> rows);

    /// <summary>Hook to add any scalar proc params (e.g. OfflineEvent's <c>@RunDate</c>). No-op by default.</summary>
    protected virtual void AddScalarParameters(SqlCommand cmd, IReadOnlyList<TRow> chunk) { }

    public async Task<int> WriteAsync(IReadOnlyList<TRow> rows, CancellationToken cancellationToken)
    {
        if (rows.Count == 0) return 0;

        // Pure, unit-testable seam: last-wins dedup on the MERGE key + MergeChunkSize chunk-split.
        var chunks = DedupAndChunk(rows, MergeKey, _chunkSize);
        var rowCount = chunks.Sum(c => c.Count);

        var key = SqlWriteGate.KeyFor(_connectionString, StoredProcedureName);
        using var gate = await SqlWriteGate.AcquireAsync(key, cancellationToken).ConfigureAwait(false);

        var total = 0;
        try
        {
            // One connection for the whole batch, reused across every chunk MERGE (still inside the
            // write gate) — so the ~112k-row plants pull opens the connection once, not ~12 times.
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            foreach (var chunk in chunks)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var table = BuildTable(chunk);

                await using var cmd = new SqlCommand(StoredProcedureName, conn) { CommandType = CommandType.StoredProcedure };
                AddScalarParameters(cmd, chunk); // each chunk carries the right FileLogId (+ RunDate for OfflineEvent)
                var tvp = cmd.Parameters.AddWithValue("@Rows", table);
                tvp.SqlDbType = SqlDbType.Structured;
                tvp.TypeName = TableValuedParameterType;

                var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                total += result is int n ? n : chunk.Count;
            }
        }
        catch (SqlException ex)
        {
            _logger.LogError(ex, "IIR SQL sink failure: {Proc} with {Count} row(s)", StoredProcedureName, rowCount);
            throw;
        }

        return total;
    }

    /// <summary>
    /// Pure seam (no DB): collapses duplicate <paramref name="key"/> values keeping the LAST occurrence
    /// (matching the merge proc's <c>ROW_NUMBER</c> de-dup, and preventing "MERGE … same row more than
    /// once" across chunk boundaries) and splits the result into <paramref name="chunkSize"/>-row
    /// chunks (preserving order). Unit-testable via <c>DataLoader.IIR.Tests</c> (InternalsVisibleTo).
    /// </summary>
    internal static IReadOnlyList<IReadOnlyList<TRow>> DedupAndChunk(IEnumerable<TRow> rows, Func<TRow, int> key, int chunkSize)
    {
        if (chunkSize < 1) chunkSize = 1;

        var deduped = rows.GroupBy(key).Select(g => g.Last()).ToList();

        var chunks = new List<IReadOnlyList<TRow>>();
        for (var start = 0; start < deduped.Count; start += chunkSize)
            chunks.Add(deduped.GetRange(start, Math.Min(chunkSize, deduped.Count - start)));
        return chunks;
    }

    /// <summary>Boxes a nullable value type as its value or DBNull.</summary>
    protected static object Nz<T>(T? value) where T : struct => value.HasValue ? value.Value : DBNull.Value;

    /// <summary>Boxes a reference (string) as itself or DBNull.</summary>
    protected static object Nz(string? value) => (object?)value ?? DBNull.Value;
}

// -------------------------------------------------------------------- Plant
public sealed class IirPlantSqlSink : IirSqlSinkBase<IirPlantRow>
{
    public IirPlantSqlSink(IOptions<IirSettings> settings, ILogger<IirPlantSqlSink> logger) : base(settings.Value, logger) { }
    protected override string StoredProcedureName => "arm.usp_UpsertPlant";
    protected override string TableValuedParameterType => "arm.PlantTvp";
    protected override int MergeKey(IirPlantRow r) => r.PlantId;

    protected override DataTable BuildTable(IReadOnlyList<IirPlantRow> rows)
    {
        var t = new DataTable();
        t.Columns.Add("FileLogId", typeof(int));
        t.Columns.Add("PlantId", typeof(int));
        t.Columns.Add("PlantName", typeof(string));
        t.Columns.Add("PlantStatusDesc", typeof(string));
        t.Columns.Add("NoEmployees", typeof(int));
        t.Columns.Add("StartupDate", typeof(DateTime));
        t.Columns.Add("LiveDate", typeof(DateTime));
        t.Columns.Add("ReleaseDate", typeof(DateTime));
        t.Columns.Add("OperationsLaborPreference", typeof(int));
        t.Columns.Add("PrimaryFuel", typeof(string));
        t.Columns.Add("SecondaryFuel", typeof(string));
        t.Columns.Add("IndustryCode", typeof(string));
        t.Columns.Add("IndustryCodeDesc", typeof(string));
        t.Columns.Add("PrimarySicId", typeof(string));
        t.Columns.Add("PrimarySicDesc", typeof(string));
        t.Columns.Add("PecZone", typeof(string));
        t.Columns.Add("MarketRegionId", typeof(string));
        t.Columns.Add("MarketRegionName", typeof(string));
        t.Columns.Add("ConfirmationStatus", typeof(string));
        t.Columns.Add("NercRegion", typeof(string));
        t.Columns.Add("NercSubRegionName", typeof(string));
        t.Columns.Add("ElectricalConnectionName", typeof(string));
        t.Columns.Add("TradingRegionId", typeof(int));
        t.Columns.Add("TradingRegionName", typeof(string));
        t.Columns.Add("CogenChp", typeof(int));
        t.Columns.Add("Metallurgical", typeof(int));
        t.Columns.Add("Thermal", typeof(int));
        t.Columns.Add("Placer", typeof(int));
        t.Columns.Add("OpenPit", typeof(int));
        t.Columns.Add("Quarry", typeof(int));
        t.Columns.Add("Strip", typeof(int));
        t.Columns.Add("Auger", typeof(int));
        t.Columns.Add("Dredging", typeof(int));
        t.Columns.Add("Drift", typeof(int));
        t.Columns.Add("Shaft", typeof(int));
        t.Columns.Add("Slope", typeof(int));
        t.Columns.Add("Longwall", typeof(int));
        t.Columns.Add("RoomPillar", typeof(int));
        t.Columns.Add("CutFill", typeof(int));
        t.Columns.Add("Caving", typeof(int));
        t.Columns.Add("Stoping", typeof(int));
        t.Columns.Add("InSituSolution", typeof(int));
        t.Columns.Add("Longitude", typeof(double));
        t.Columns.Add("Latitude", typeof(double));
        t.Columns.Add("WorldRegionId", typeof(int));
        t.Columns.Add("WorldRegionName", typeof(string));
        t.Columns.Add("Offshore", typeof(int));
        t.Columns.Add("MailingAddressLine1", typeof(string));
        t.Columns.Add("MailingCity", typeof(string));
        t.Columns.Add("MailingStateName", typeof(string));
        t.Columns.Add("MailingPostalCode", typeof(string));
        t.Columns.Add("MailingCountryName", typeof(string));
        t.Columns.Add("PhysicalAddressLine1", typeof(string));
        t.Columns.Add("PhysicalCity", typeof(string));
        t.Columns.Add("PhysicalStateName", typeof(string));
        t.Columns.Add("PhysicalPostalCode", typeof(string));
        t.Columns.Add("PhysicalCountryName", typeof(string));
        t.Columns.Add("PhysicalCountyName", typeof(string));
        t.Columns.Add("PhoneCC", typeof(string));
        t.Columns.Add("PhoneNumber", typeof(string));
        t.Columns.Add("ParentCompanyId", typeof(string));
        t.Columns.Add("ParentCompanyName", typeof(string));
        t.Columns.Add("ParentCompanyWebsite", typeof(string));
        t.Columns.Add("OperatorCompanyId", typeof(string));
        t.Columns.Add("OperatorCompanyName", typeof(string));
        t.Columns.Add("OperatorCompanyWebsite", typeof(string));

        foreach (var r in rows)
            t.Rows.Add(
                r.FileLogId, r.PlantId, Nz(r.PlantName), Nz(r.PlantStatusDesc), Nz(r.NoEmployees),
                Nz(r.StartupDate), Nz(r.LiveDate), Nz(r.ReleaseDate), Nz(r.OperationsLaborPreference),
                Nz(r.PrimaryFuel), Nz(r.SecondaryFuel), Nz(r.IndustryCode), Nz(r.IndustryCodeDesc),
                Nz(r.PrimarySicId), Nz(r.PrimarySicDesc), Nz(r.PecZone), Nz(r.MarketRegionId),
                Nz(r.MarketRegionName), Nz(r.ConfirmationStatus), Nz(r.NercRegion), Nz(r.NercSubRegionName),
                Nz(r.ElectricalConnectionName), Nz(r.TradingRegionId), Nz(r.TradingRegionName),
                Nz(r.CogenChp), Nz(r.Metallurgical), Nz(r.Thermal), Nz(r.Placer), Nz(r.OpenPit),
                Nz(r.Quarry), Nz(r.Strip), Nz(r.Auger), Nz(r.Dredging), Nz(r.Drift), Nz(r.Shaft),
                Nz(r.Slope), Nz(r.Longwall), Nz(r.RoomPillar), Nz(r.CutFill), Nz(r.Caving), Nz(r.Stoping),
                Nz(r.InSituSolution), Nz(r.Longitude), Nz(r.Latitude), Nz(r.WorldRegionId),
                Nz(r.WorldRegionName), Nz(r.Offshore), Nz(r.MailingAddressLine1), Nz(r.MailingCity),
                Nz(r.MailingStateName), Nz(r.MailingPostalCode), Nz(r.MailingCountryName),
                Nz(r.PhysicalAddressLine1), Nz(r.PhysicalCity), Nz(r.PhysicalStateName),
                Nz(r.PhysicalPostalCode), Nz(r.PhysicalCountryName), Nz(r.PhysicalCountyName),
                Nz(r.PhoneCC), Nz(r.PhoneNumber), Nz(r.ParentCompanyId), Nz(r.ParentCompanyName),
                Nz(r.ParentCompanyWebsite), Nz(r.OperatorCompanyId), Nz(r.OperatorCompanyName),
                Nz(r.OperatorCompanyWebsite));
        return t;
    }
}

// -------------------------------------------------------------------- Unit
public sealed class IirUnitSqlSink : IirSqlSinkBase<IirUnitRow>
{
    public IirUnitSqlSink(IOptions<IirSettings> settings, ILogger<IirUnitSqlSink> logger) : base(settings.Value, logger) { }
    protected override string StoredProcedureName => "arm.usp_UpsertUnit";
    protected override string TableValuedParameterType => "arm.UnitTvp";
    protected override int MergeKey(IirUnitRow r) => r.UnitId;

    protected override DataTable BuildTable(IReadOnlyList<IirUnitRow> rows)
    {
        var t = new DataTable();
        t.Columns.Add("FileLogId", typeof(int));
        t.Columns.Add("UnitId", typeof(int));
        t.Columns.Add("UnitName", typeof(string));
        t.Columns.Add("PlantId", typeof(int));
        t.Columns.Add("PlantName", typeof(string));
        t.Columns.Add("PlantStatusDesc", typeof(string));
        t.Columns.Add("PlantAddressLine1", typeof(string));
        t.Columns.Add("PlantCity", typeof(string));
        t.Columns.Add("PlantStateName", typeof(string));
        t.Columns.Add("PlantPostalCode", typeof(string));
        t.Columns.Add("PlantCountryName", typeof(string));
        t.Columns.Add("PlantCountyName", typeof(string));
        t.Columns.Add("MarketRegionId", typeof(string));
        t.Columns.Add("MarketRegionName", typeof(string));
        t.Columns.Add("WorldRegionId", typeof(int));
        t.Columns.Add("WorldRegionName", typeof(string));
        t.Columns.Add("TradingRegionId", typeof(int));
        t.Columns.Add("TradingRegionName", typeof(string));
        t.Columns.Add("UnitStatusDesc", typeof(string));
        t.Columns.Add("UnitStatusGroup", typeof(string));
        t.Columns.Add("HeaterCount", typeof(int));
        t.Columns.Add("UnitTypeId", typeof(string));
        t.Columns.Add("UnitTypeDesc", typeof(string));
        t.Columns.Add("UnitTypeGroup", typeof(string));
        t.Columns.Add("CapacityProductId", typeof(string));
        t.Columns.Add("Capacity", typeof(double));
        t.Columns.Add("CapacityUom", typeof(string));
        t.Columns.Add("PrimarySicId", typeof(string));
        t.Columns.Add("PrimarySicDesc", typeof(string));
        t.Columns.Add("AreaId", typeof(int));
        t.Columns.Add("AreaName", typeof(string));
        t.Columns.Add("PlantLatitude", typeof(double));
        t.Columns.Add("PlantLongitude", typeof(double));
        t.Columns.Add("Offshore", typeof(int));
        t.Columns.Add("IndustryCode", typeof(string));
        t.Columns.Add("IndustryCodeDesc", typeof(string));
        t.Columns.Add("Technology", typeof(string));
        t.Columns.Add("Renewable", typeof(int));
        t.Columns.Add("CogenChp", typeof(int));
        t.Columns.Add("PlantOperatorName", typeof(string));
        t.Columns.Add("PlantOwnerName", typeof(string));
        t.Columns.Add("PlantParentName", typeof(string));
        t.Columns.Add("PlantPhone", typeof(string));
        t.Columns.Add("ReleaseDate", typeof(DateTime));
        t.Columns.Add("LiveDate", typeof(DateTime));

        foreach (var r in rows)
            t.Rows.Add(
                r.FileLogId, r.UnitId, Nz(r.UnitName), Nz(r.PlantId), Nz(r.PlantName), Nz(r.PlantStatusDesc),
                Nz(r.PlantAddressLine1), Nz(r.PlantCity), Nz(r.PlantStateName), Nz(r.PlantPostalCode),
                Nz(r.PlantCountryName), Nz(r.PlantCountyName), Nz(r.MarketRegionId), Nz(r.MarketRegionName),
                Nz(r.WorldRegionId), Nz(r.WorldRegionName), Nz(r.TradingRegionId), Nz(r.TradingRegionName),
                Nz(r.UnitStatusDesc), Nz(r.UnitStatusGroup), Nz(r.HeaterCount), Nz(r.UnitTypeId),
                Nz(r.UnitTypeDesc), Nz(r.UnitTypeGroup), Nz(r.CapacityProductId), Nz(r.Capacity),
                Nz(r.CapacityUom), Nz(r.PrimarySicId), Nz(r.PrimarySicDesc), Nz(r.AreaId), Nz(r.AreaName),
                Nz(r.PlantLatitude), Nz(r.PlantLongitude), Nz(r.Offshore), Nz(r.IndustryCode),
                Nz(r.IndustryCodeDesc), Nz(r.Technology), Nz(r.Renewable), Nz(r.CogenChp),
                Nz(r.PlantOperatorName), Nz(r.PlantOwnerName), Nz(r.PlantParentName), Nz(r.PlantPhone),
                Nz(r.ReleaseDate), Nz(r.LiveDate));
        return t;
    }
}

// -------------------------------------------------------------------- OfflineEvent
public sealed class IirOfflineEventSqlSink : IirSqlSinkBase<IirOfflineEventRow>
{
    public IirOfflineEventSqlSink(IOptions<IirSettings> settings, ILogger<IirOfflineEventSqlSink> logger) : base(settings.Value, logger) { }
    protected override string StoredProcedureName => "arm.usp_UpsertOfflineEvent";
    protected override string TableValuedParameterType => "arm.OfflineEventTvp";
    protected override int MergeKey(IirOfflineEventRow r) => r.EventId;

    /// <summary>OfflineEvent takes the Central run date as a scalar (§5.4); every row in a chunk shares it.</summary>
    protected override void AddScalarParameters(SqlCommand cmd, IReadOnlyList<IirOfflineEventRow> chunk)
    {
        cmd.Parameters.Add("@RunDate", SqlDbType.Date).Value = chunk[0].RunDate.ToDateTime(TimeOnly.MinValue);
    }

    protected override DataTable BuildTable(IReadOnlyList<IirOfflineEventRow> rows)
    {
        // RunDate is NOT a TVP column (it is the scalar @RunDate). Order matches arm.OfflineEventTvp.
        var t = new DataTable();
        t.Columns.Add("FileLogId", typeof(int));
        t.Columns.Add("EventId", typeof(int));
        t.Columns.Add("EventKind", typeof(string));
        t.Columns.Add("EventType", typeof(string));
        t.Columns.Add("EventCause", typeof(string));
        t.Columns.Add("EventStatusDesc", typeof(string));
        t.Columns.Add("UnitId", typeof(int));
        t.Columns.Add("UnitName", typeof(string));
        t.Columns.Add("UnitStatusDesc", typeof(string));
        t.Columns.Add("IndustryCode", typeof(string));
        t.Columns.Add("IndustryCodeDesc", typeof(string));
        t.Columns.Add("PlantId", typeof(int));
        t.Columns.Add("PlantName", typeof(string));
        t.Columns.Add("PlantParentName", typeof(string));
        t.Columns.Add("PlantOwnerName", typeof(string));
        t.Columns.Add("PlantOperatorName", typeof(string));
        t.Columns.Add("PlantAddressLine1", typeof(string));
        t.Columns.Add("PlantCity", typeof(string));
        t.Columns.Add("PlantState", typeof(string));
        t.Columns.Add("PlantPostalCode", typeof(string));
        t.Columns.Add("PlantCountry", typeof(string));
        t.Columns.Add("PlantCounty", typeof(string));
        t.Columns.Add("PlantLatitude", typeof(double));
        t.Columns.Add("PlantLongitude", typeof(double));
        t.Columns.Add("AreaId", typeof(int));
        t.Columns.Add("AreaName", typeof(string));
        t.Columns.Add("Offshore", typeof(int));
        t.Columns.Add("GasRegionId", typeof(string));
        t.Columns.Add("GasRegionName", typeof(string));
        t.Columns.Add("MarketRegionId", typeof(string));
        t.Columns.Add("MarketRegionName", typeof(string));
        t.Columns.Add("TradingRegionId", typeof(int));
        t.Columns.Add("TradingRegionName", typeof(string));
        t.Columns.Add("PowerTradeRegion", typeof(string));
        t.Columns.Add("WorldRegionId", typeof(int));
        t.Columns.Add("WorldRegionName", typeof(string));
        t.Columns.Add("PecZone", typeof(string));
        t.Columns.Add("PrimarySicId", typeof(string));
        t.Columns.Add("UnitClassification", typeof(string));
        t.Columns.Add("Derate", typeof(double));
        t.Columns.Add("IsDerated", typeof(int));
        t.Columns.Add("ProductId", typeof(int));
        t.Columns.Add("ProductDescription", typeof(string));
        t.Columns.Add("UnitCapacity", typeof(double));
        t.Columns.Add("OfflineCapacity", typeof(double));
        t.Columns.Add("OfflineCapacityUOM", typeof(string));
        t.Columns.Add("EventStartDate", typeof(DateTime));
        t.Columns.Add("EventEndDate", typeof(DateTime));
        t.Columns.Add("EventDuration", typeof(int));
        t.Columns.Add("PrevStartDate", typeof(DateTime));
        t.Columns.Add("PrevEndDate", typeof(DateTime));
        t.Columns.Add("UnitTypeId", typeof(string));
        t.Columns.Add("UnitTypeDesc", typeof(string));
        t.Columns.Add("EventConfirmationStatus", typeof(string));
        t.Columns.Add("CogenChp", typeof(int));
        t.Columns.Add("EventDatePrecision", typeof(string));
        t.Columns.Add("KickoffSlippage", typeof(int));
        t.Columns.Add("EventComments", typeof(string));
        t.Columns.Add("LiveDate", typeof(DateTime));
        t.Columns.Add("ReleaseDate", typeof(DateTime));

        foreach (var r in rows)
            t.Rows.Add(
                r.FileLogId, r.EventId, Nz(r.EventKind), Nz(r.EventType), Nz(r.EventCause),
                Nz(r.EventStatusDesc), Nz(r.UnitId), Nz(r.UnitName), Nz(r.UnitStatusDesc), Nz(r.IndustryCode),
                Nz(r.IndustryCodeDesc), Nz(r.PlantId), Nz(r.PlantName), Nz(r.PlantParentName),
                Nz(r.PlantOwnerName), Nz(r.PlantOperatorName), Nz(r.PlantAddressLine1), Nz(r.PlantCity),
                Nz(r.PlantState), Nz(r.PlantPostalCode), Nz(r.PlantCountry), Nz(r.PlantCounty),
                Nz(r.PlantLatitude), Nz(r.PlantLongitude), Nz(r.AreaId), Nz(r.AreaName), Nz(r.Offshore),
                Nz(r.GasRegionId), Nz(r.GasRegionName), Nz(r.MarketRegionId), Nz(r.MarketRegionName),
                Nz(r.TradingRegionId), Nz(r.TradingRegionName), Nz(r.PowerTradeRegion), Nz(r.WorldRegionId),
                Nz(r.WorldRegionName), Nz(r.PecZone), Nz(r.PrimarySicId), Nz(r.UnitClassification),
                Nz(r.Derate), Nz(r.IsDerated), Nz(r.ProductId), Nz(r.ProductDescription), Nz(r.UnitCapacity),
                Nz(r.OfflineCapacity), Nz(r.OfflineCapacityUOM), Nz(r.EventStartDate), Nz(r.EventEndDate),
                Nz(r.EventDuration), Nz(r.PrevStartDate), Nz(r.PrevEndDate), Nz(r.UnitTypeId),
                Nz(r.UnitTypeDesc), Nz(r.EventConfirmationStatus), Nz(r.CogenChp), Nz(r.EventDatePrecision),
                Nz(r.KickoffSlippage), Nz(r.EventComments), Nz(r.LiveDate), Nz(r.ReleaseDate));
        return t;
    }
}

// -------------------------------------------------------------------- Summary (id-catalog census)
/// <summary>
/// The STEP-1 id-catalog census sink (design §4.4/§7.5). One parameterized class serves all three
/// endpoints — the census TVPs are positionally identical, ID-ONLY <c>(&lt;Id&gt;)</c> single-column
/// tables (sql/IIR/002), and the proc/TVP names are supplied per endpoint at construction. It reuses the
/// custom <see cref="IirSqlSinkBase{TRow}"/> shape (<c>@Rows</c> TVP + a scalar <c>@RunDate</c>),
/// de-dups on the entity id, and — via the base's <see cref="SqlWriteGate"/> keyed on this census
/// proc name — writes under a proc key DISTINCT from the fact merge and <c>arm.usp_UpsertFileLog</c>,
/// so the reader's census side-write never deadlocks against the fact merge (design §4.4). NO
/// FileLogId column (unlike the fact TVPs) and NO lat/long — the census records only WHICH ids
/// STEP 1 discovered; the coordinates live on the fact tables (the row's in-memory
/// <see cref="IirSummaryRow.Latitude"/>/<see cref="IirSummaryRow.Longitude"/> still feed the §6.3
/// detail carry-forward, they are simply not persisted here).
/// </summary>
public sealed class IirSummarySqlSink : IirSqlSinkBase<IirSummaryRow>
{
    private readonly string _proc;
    private readonly string _tvp;

    // Takes a plain ILogger so the module can supply a PER-ENDPOINT category (e.g. "IIR.Plant.Census")
    // — a census SQL error then names its endpoint, matching the fact sinks/readers.
    public IirSummarySqlSink(string proc, string tvp, IOptions<IirSettings> settings, ILogger logger)
        : base(settings.Value, logger)
    {
        _proc = proc;
        _tvp = tvp;
    }

    protected override string StoredProcedureName => _proc;
    protected override string TableValuedParameterType => _tvp;
    protected override int MergeKey(IirSummaryRow r) => r.EntityId;

    /// <summary>The census proc takes the Central run date as a scalar (§7.5); every row in a chunk shares it.</summary>
    protected override void AddScalarParameters(SqlCommand cmd, IReadOnlyList<IirSummaryRow> chunk)
    {
        cmd.Parameters.Add("@RunDate", SqlDbType.Date).Value = chunk[0].RunDate.ToDateTime(TimeOnly.MinValue);
    }

    protected override DataTable BuildTable(IReadOnlyList<IirSummaryRow> rows)
    {
        // Order = sql/IIR/002 census TVP: (<Id>) — ID-ONLY. Bound BY POSITION, so the id column name
        // is generic; RunDate is the scalar @RunDate, NOT a TVP column, and there is no FileLogId or
        // lat/long column (the fact TVPs carry the coordinates).
        var t = new DataTable();
        t.Columns.Add("EntityId", typeof(int));

        foreach (var r in rows)
            t.Rows.Add(r.EntityId);
        return t;
    }
}
