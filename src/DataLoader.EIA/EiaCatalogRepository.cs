using System.Data;
using DataLoader.Core.Concurrency;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.EIA;

/// <summary>
/// Every read of, and write to, the EIA catalog tables.
///
/// <para>
/// <b>No SQL text lives in this class.</b> The loader request asked for stored
/// procedures "so the actual queries are not hardcoded in the software", and that is
/// taken literally: this type only names procs and binds parameters. Changing which
/// series load, or adding an <c>Enabled</c> predicate to the frequency/data-type reads,
/// is an edit to <c>sql/EIA/003_CreateEiaProcedures.sql</c> and a re-run of that
/// script — never a rebuild and redeploy of the binary.
/// </para>
/// </summary>
internal sealed class EiaCatalogRepository
{
    private readonly EiaSettings _settings;
    private readonly ILogger<EiaCatalogRepository> _logger;

    public EiaCatalogRepository(IOptions<EiaSettings> settings, ILogger<EiaCatalogRepository> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    /// <summary><c>arm.usp_GetActiveSeries</c>.</summary>
    public async Task<IReadOnlyList<EiaSeries>> GetActiveSeriesAsync(CancellationToken ct)
    {
        var list = new List<EiaSeries>();

        await using var conn = new SqlConnection(_settings.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new SqlCommand("arm.usp_GetActiveSeries", conn)
        {
            CommandType = CommandType.StoredProcedure
        };

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new EiaSeries
            {
                SeriesId          = reader.GetString(reader.GetOrdinal("SeriesId")),
                Name              = ReadNullableString(reader, "Name"),
                ParentSeriesId    = ReadNullableString(reader, "ParentSeriesId"),
                StartPeriod       = ReadNullableString(reader, "StartPeriod"),
                EndPeriod         = ReadNullableString(reader, "EndPeriod"),
                DefaultDateFormat = ReadNullableString(reader, "DefaultDateFormat"),
                DefaultFrequency  = ReadNullableString(reader, "DefaultFrequency"),
                TotalRecordCount  = ReadNullableInt64(reader, "TotalRecordCount"),
                AllowIncremental  = ReadNullableBool(reader, "AllowIncremental")
            });
        }

        return list;
    }

    /// <summary><c>arm.usp_GetSeriesFrequencies</c>.</summary>
    public async Task<IReadOnlyList<EiaFrequency>> GetFrequenciesAsync(string seriesId, CancellationToken ct)
    {
        var list = new List<EiaFrequency>();

        await using var conn = new SqlConnection(_settings.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new SqlCommand("arm.usp_GetSeriesFrequencies", conn)
        {
            CommandType = CommandType.StoredProcedure
        };
        cmd.Parameters.Add("@SeriesId", SqlDbType.VarChar, 100).Value = seriesId;

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new EiaFrequency
            {
                SeriesId    = reader.GetString(reader.GetOrdinal("SeriesId")),
                FrequencyId = reader.GetString(reader.GetOrdinal("FrequencyId")),
                Query       = ReadNullableString(reader, "Query"),
                Format      = ReadNullableString(reader, "Format"),
                Enabled     = ReadNullableBool(reader, "Enabled")
            });
        }

        return list;
    }

    /// <summary><c>arm.usp_GetSeriesDataTypes</c>.</summary>
    public async Task<IReadOnlyList<EiaDataType>> GetDataTypesAsync(string seriesId, CancellationToken ct)
    {
        var list = new List<EiaDataType>();

        await using var conn = new SqlConnection(_settings.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new SqlCommand("arm.usp_GetSeriesDataTypes", conn)
        {
            CommandType = CommandType.StoredProcedure
        };
        cmd.Parameters.Add("@SeriesId", SqlDbType.VarChar, 100).Value = seriesId;

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new EiaDataType
            {
                SeriesId   = reader.GetString(reader.GetOrdinal("SeriesId")),
                DataTypeId = reader.GetString(reader.GetOrdinal("DataTypeId")),
                Units      = ReadNullableString(reader, "Units"),
                Alias      = ReadNullableString(reader, "Alias"),
                Enabled    = ReadNullableBool(reader, "Enabled")
            });
        }

        return list;
    }

    /// <summary>
    /// <c>arm.usp_UpdateSeriesEndPeriod</c>. Advance-only inside the proc — this method
    /// cannot move EndPeriod backwards no matter what it is passed.
    ///
    /// <para>
    /// Takes the shared <see cref="SqlWriteGate"/> for this proc so the metadata pass,
    /// which runs across series concurrently, does not pile concurrent UPDATEs onto the
    /// small <c>arm.Series</c> table while merges are in flight.
    /// </para>
    /// </summary>
    public async Task<int> UpdateEndPeriodAsync(string seriesId, string endPeriod, CancellationToken ct)
    {
        using var gate = await SqlWriteGate
            .AcquireAsync(SqlWriteGate.KeyFor(_settings.ConnectionString, "arm.usp_UpdateSeriesEndPeriod"), ct)
            .ConfigureAwait(false);

        await using var conn = new SqlConnection(_settings.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new SqlCommand("arm.usp_UpdateSeriesEndPeriod", conn)
        {
            CommandType = CommandType.StoredProcedure
        };
        cmd.Parameters.Add("@SeriesId",  SqlDbType.VarChar, 100).Value = seriesId;
        cmd.Parameters.Add("@EndPeriod", SqlDbType.VarChar,  50).Value = endPeriod;

        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        var updated = result is int n ? n : 0;

        if (updated > 0)
            _logger.LogInformation("EIA {Series}: EndPeriod advanced to {EndPeriod}", seriesId, endPeriod);

        return updated;
    }

    private static string? ReadNullableString(SqlDataReader r, string column)
    {
        var ord = r.GetOrdinal(column);
        return r.IsDBNull(ord) ? null : r.GetString(ord);
    }

    private static bool? ReadNullableBool(SqlDataReader r, string column)
    {
        var ord = r.GetOrdinal(column);
        return r.IsDBNull(ord) ? null : r.GetBoolean(ord);
    }

    private static long? ReadNullableInt64(SqlDataReader r, string column)
    {
        var ord = r.GetOrdinal(column);
        return r.IsDBNull(ord) ? null : r.GetInt64(ord);
    }
}
