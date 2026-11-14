using System.Data;
using DataLoader.Core.Configuration;
using DataLoader.Core.Resilience;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace DataLoader.Core.Watchdog;

/// <summary>
/// <c>core.vw_WatchdogFinding_Open</c> and <c>core.usp_RecordWatchdogPass</c>
/// (<c>sql/Core/007_CreateWatchdogFindings.sql</c>), on the platform database.
///
/// <para>⚠ TVP contract: <see cref="BuildFindingsTable"/> and <see cref="BuildHintsTable"/>
/// must match <c>core.WatchdogFindingType</c> / <c>core.WatchdogResolutionHintType</c> by
/// column NAME + ORDER + TYPE. SQL Server binds a TVP by ORDINAL, so a reordered column
/// lands in the wrong field silently; a test pins both.</para>
/// </summary>
public sealed class SqlWatchdogFindingStore : IWatchdogFindingStore
{
    public const string ProcName = "core.usp_RecordWatchdogPass";
    public const string FindingTypeName = "core.WatchdogFindingType";
    public const string HintTypeName = "core.WatchdogResolutionHintType";

    private readonly PlatformSettings _platform;
    private readonly WatchdogSettings _settings;

    public SqlWatchdogFindingStore(IOptions<PlatformSettings> platform, IOptions<WatchdogSettings> settings)
    {
        _platform = platform.Value;
        _settings = settings.Value;
    }

    private int CommandTimeoutSeconds => Math.Max(5, _settings.PersistTimeoutSeconds);

    public async Task<IReadOnlyList<OpenWatchdogFinding>> ReadOpenFindingsAsync(CancellationToken cancellationToken)
    {
        const string Sql = "SELECT FindingKey, Signal, LoaderName, RunId FROM core.vw_WatchdogFinding_Open;";

        await using var conn = new SqlConnection(_platform.LoadLogConnectionString);
        await conn.OpenWithRetryAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = new SqlCommand(Sql, conn) { CommandTimeout = CommandTimeoutSeconds };
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var rows = new List<OpenWatchdogFinding>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new OpenWatchdogFinding(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetGuid(3)));
        }
        return rows;
    }

    public async Task<WatchdogPersistResult> RecordPassAsync(
        WatchdogPassRecord pass, IReadOnlyList<WatchdogResolutionHint> hints, CancellationToken cancellationToken)
    {
        await using var conn = new SqlConnection(_platform.LoadLogConnectionString);
        await conn.OpenWithRetryAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = new SqlCommand(ProcName, conn)
        {
            CommandType = CommandType.StoredProcedure,
            CommandTimeout = CommandTimeoutSeconds
        };

        cmd.Parameters.Add(new SqlParameter("@StartedAtUtc", SqlDbType.DateTime2) { Scale = 3, Value = pass.StartedAtUtc });
        cmd.Parameters.Add(new SqlParameter("@HostName", SqlDbType.NVarChar, 128) { Value = Cap(pass.HostName, 128) });
        cmd.Parameters.Add(new SqlParameter("@Status", SqlDbType.VarChar, 12) { Value = pass.Status.ToString() });
        cmd.Parameters.Add(new SqlParameter("@SignalsChecked", SqlDbType.VarChar, 200)
            { Value = string.Join(",", pass.SignalsChecked) });
        cmd.Parameters.Add(new SqlParameter("@SignalsDegraded", SqlDbType.VarChar, 200)
            { Value = pass.SignalsDegraded.Count == 0 ? DBNull.Value : string.Join(",", pass.SignalsDegraded) });
        cmd.Parameters.Add(new SqlParameter("@AppVersion", SqlDbType.VarChar, 50)
            { Value = (object?)pass.AppVersion ?? DBNull.Value });
        cmd.Parameters.Add(new SqlParameter("@Findings", SqlDbType.Structured)
            { TypeName = FindingTypeName, Value = BuildFindingsTable(pass.Findings) });
        cmd.Parameters.Add(new SqlParameter("@ResolvedHints", SqlDbType.Structured)
            { TypeName = HintTypeName, Value = BuildHintsTable(hints) });

        var passId = new SqlParameter("@PassId", SqlDbType.BigInt) { Direction = ParameterDirection.Output };
        var retentionError = new SqlParameter("@RetentionError", SqlDbType.NVarChar, 400) { Direction = ParameterDirection.Output };
        cmd.Parameters.Add(passId);
        cmd.Parameters.Add(retentionError);

        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return new WatchdogPersistResult(
            passId.Value is long id ? id : 0,
            retentionError.Value as string);
    }

    /// <summary>Column NAME + ORDER + TYPE = <c>core.WatchdogFindingType</c>.</summary>
    public static DataTable BuildFindingsTable(IEnumerable<WatchdogFindingRow> findings)
    {
        var table = new DataTable();
        table.Columns.Add("FindingKey", typeof(string));
        table.Columns.Add("Signal", typeof(string));
        table.Columns.Add("LoaderName", typeof(string));
        table.Columns.Add("RunId", typeof(Guid));
        table.Columns.Add("Severity", typeof(string));
        table.Columns.Add("Summary", typeof(string));
        table.Columns.Add("DetailJson", typeof(string));
        table.Columns.Add("IsSuppressed", typeof(bool));

        foreach (var f in findings)
        {
            table.Rows.Add(
                Cap(f.FindingKey, 300),
                f.Signal,
                f.LoaderName is null ? DBNull.Value : Cap(f.LoaderName, 128),
                f.RunId is { } runId ? runId : DBNull.Value,
                f.Severity,
                Cap(f.Summary, WatchdogFindingMapper.MaxSummaryChars),
                f.DetailJson,
                f.IsSuppressed);
        }
        return table;
    }

    /// <summary>Column NAME + ORDER + TYPE = <c>core.WatchdogResolutionHintType</c>.</summary>
    public static DataTable BuildHintsTable(IEnumerable<WatchdogResolutionHint> hints)
    {
        var table = new DataTable();
        table.Columns.Add("FindingKey", typeof(string));
        table.Columns.Add("ResolutionReason", typeof(string));

        // The TVP's primary key is case-insensitive; never send the same key twice.
        foreach (var h in hints.GroupBy(h => h.FindingKey, StringComparer.OrdinalIgnoreCase).Select(g => g.First()))
            table.Rows.Add(Cap(h.FindingKey, 300), h.ResolutionReason);
        return table;
    }

    /// <summary>Hard cap so an over-long value fails no write. Never splits a surrogate pair.</summary>
    private static string Cap(string value, int max)
    {
        if (value.Length <= max) return value;
        var cut = max;
        if (char.IsHighSurrogate(value[cut - 1])) cut--;
        return value[..cut];
    }
}
