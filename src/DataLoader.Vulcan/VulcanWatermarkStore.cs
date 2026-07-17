using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace DataLoader.Vulcan;

public interface IVulcanWatermarkStore
{
    /// <summary>Returns the stored watermark for a table, or null if none (=> full backfill).</summary>
    Task<DateOnly?> GetAsync(string tableId, CancellationToken ct);
}

public sealed class VulcanWatermarkStore : IVulcanWatermarkStore
{
    private readonly VulcanSettings _settings;
    public VulcanWatermarkStore(IOptions<VulcanSettings> settings) => _settings = settings.Value;

    public async Task<DateOnly?> GetAsync(string tableId, CancellationToken ct)
    {
        await using var conn = new SqlConnection(_settings.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new SqlCommand("dbo.usp_GetVulcanWatermark", conn) { CommandType = CommandType.StoredProcedure };
        cmd.Parameters.AddWithValue("@TableName", tableId);
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (result is null || result is DBNull) return null;
        return DateOnly.FromDateTime((DateTime)result);
    }
}
