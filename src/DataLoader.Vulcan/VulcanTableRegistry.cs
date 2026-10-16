using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace DataLoader.Vulcan;

/// <summary>One row of <c>dbo.LoaderTable</c>: may this source table load, and when did it last succeed.</summary>
/// <param name="TableName">Loader-facing table id, matching <see cref="VulcanTableSpec.TableId"/>.</param>
/// <param name="Enabled">False → the loader skips it entirely this run.</param>
/// <param name="LastRun">When it last loaded with NO failed work units; null if it never has.</param>
public sealed record VulcanTableRegistration(string TableName, bool Enabled, DateTime? LastRun);

/// <summary>
/// The set of Vulcan source tables the loader may load, and their last successful load.
///
/// <para>This replaced the <c>Loaders:Vulcan:EnabledTables</c> list in appsettings.json so the set
/// is operable without a redeploy — flip <c>dbo.LoaderTable.Enabled</c> to 0 and the next run skips
/// that table.</para>
/// </summary>
public interface IVulcanTableRegistry
{
    /// <summary>Every registered table, enabled or not, so the caller can log what it is skipping.</summary>
    Task<IReadOnlyList<VulcanTableRegistration>> GetAllAsync(CancellationToken ct);

    /// <summary>
    /// Stamps <c>LastRun</c> for a table that has just loaded successfully. Call ONLY after a
    /// pipeline completes with no failed work units — the column means "last successful load".
    /// </summary>
    Task SetLastRunAsync(string tableName, CancellationToken ct);
}

/// <summary>SQL implementation over <c>dbo.LoaderTable</c> in the Vulcan database.</summary>
public sealed class VulcanTableRegistry : IVulcanTableRegistry
{
    private readonly VulcanSettings _settings;

    public VulcanTableRegistry(IOptions<VulcanSettings> settings) => _settings = settings.Value;

    public async Task<IReadOnlyList<VulcanTableRegistration>> GetAllAsync(CancellationToken ct)
    {
        await using var conn = new SqlConnection(_settings.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new SqlCommand("dbo.usp_GetVulcanLoaderTables", conn)
            { CommandType = CommandType.StoredProcedure };

        var rows = new List<VulcanTableRegistration>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(new VulcanTableRegistration(
                reader.GetString(0),
                reader.GetBoolean(1),
                await reader.IsDBNullAsync(2, ct).ConfigureAwait(false) ? null : reader.GetDateTime(2)));
        }
        return rows;
    }

    public async Task SetLastRunAsync(string tableName, CancellationToken ct)
    {
        await using var conn = new SqlConnection(_settings.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new SqlCommand("dbo.usp_SetVulcanTableLastRun", conn)
            { CommandType = CommandType.StoredProcedure };
        cmd.Parameters.AddWithValue("@TableName", tableName);
        cmd.Parameters.AddWithValue("@LastRun", DBNull.Value);   // proc defaults to SYSUTCDATETIME()
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
