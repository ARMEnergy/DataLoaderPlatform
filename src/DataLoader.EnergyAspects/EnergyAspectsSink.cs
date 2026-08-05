using System.Data;
using DataLoader.Core.Abstractions;
using DataLoader.Core.Concurrency;
using DataLoader.Core.Sinks;
using DataLoader.EnergyAspects.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.EnergyAspects;

/// <summary>
/// SQL sink for one work unit's data.
///
/// One unit produces a <see cref="TimeseriesBatch"/> containing both
/// timeseries values and timeseries metadata; both have to be written under
/// the same call. We bulk-merge the value rows via the framework helper,
/// and separately bulk-upsert the metadata rows. The number we return is
/// the count of value rows persisted — what the load log records.
/// </summary>
public sealed class EnergyAspectsSink : ISink<TimeseriesBatch>
{
    private readonly EnergyAspectsSettings _settings;
    private readonly ILogger<EnergyAspectsSink> _logger;

    public EnergyAspectsSink(IOptions<EnergyAspectsSettings> settings, ILogger<EnergyAspectsSink> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<int> WriteAsync(IReadOnlyList<TimeseriesBatch> batches, CancellationToken cancellationToken)
    {
        // The pipeline passes a list, but the EA transformer always emits a
        // single batch per work unit (one mapping × one date window). Flatten.
        var data = batches.SelectMany(b => b.Data).ToList();
        var meta = batches.SelectMany(b => b.Metadata).ToList();

        int rowsWritten = 0;
        if (data.Count > 0)
            rowsWritten = await BulkMergeAsync(
                "dbo.usp_BulkMergeTimeseriesData", "dbo.TimeseriesDataTvp",
                BuildDataTable(data), cancellationToken).ConfigureAwait(false);

        if (meta.Count > 0)
            await BulkUpsertAsync(
                "dbo.usp_BulkUpsertDatasetMetadata", "dbo.DatasetMetadataTvp",
                BuildMetadataTable(meta), cancellationToken).ConfigureAwait(false);

        return rowsWritten;
    }

    // -------- Public helper for the mapping-refresh step --------

    public async Task BulkUpsertMappingsAsync(IReadOnlyList<DatasetMapping> mappings, CancellationToken ct)
    {
        if (mappings.Count == 0) return;

        await BulkUpsertAsync(
            "dbo.usp_BulkUpsertDatasetMappings", "dbo.MappingDatasetTvp",
            BuildMappingTable(mappings), ct).ConfigureAwait(false);

        await BulkUpsertAsync(
            "dbo.usp_BulkUpsertDatasetMappingIds", "dbo.MappingDatasetIdTvp",
            BuildMappingIdTable(mappings), ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DatasetMapping>> GetActiveMappingsAsync(CancellationToken ct)
    {
        var mappings = new List<DatasetMapping>();
        await using var conn = new SqlConnection(_settings.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new SqlCommand("dbo.usp_GetActiveMappings", conn) { CommandType = CommandType.StoredProcedure };
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            mappings.Add(new DatasetMapping
            {
                MappingId = reader.GetInt32(reader.GetOrdinal("MappingId")),
                Name = reader.GetString(reader.GetOrdinal("MappingName")),
                Category = reader.GetString(reader.GetOrdinal("Category")),
                RequestString = ReadNullableString(reader, "RequestString"),
                Licensed = ReadNullableString(reader, "Licensed"),
                DatasetIds = ParseDatasetIds(reader["DatasetIds"] as string)
            });
        }
        return mappings;
    }

    public async Task LogApiResponseAsync(int mappingId, int responseCode, CancellationToken ct)
    {
        // Serialize concurrent writes to this append-only log per target.
        using var gate = await SqlWriteGate.AcquireAsync(
            SqlWriteGate.KeyFor(_settings.ConnectionString, "dbo.usp_LogApiResponse"), ct).ConfigureAwait(false);

        await using var conn = new SqlConnection(_settings.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new SqlCommand("dbo.usp_LogApiResponse", conn) { CommandType = CommandType.StoredProcedure };
        cmd.Parameters.AddWithValue("@MappingId", mappingId);
        cmd.Parameters.AddWithValue("@ResponseCode", responseCode);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    // -------- Internals --------

    private async Task<int> BulkMergeAsync(string procName, string tvpType, DataTable table, CancellationToken ct)
    {
        try
        {
            // Serialize concurrent MERGE calls against the same target proc.
            using var gate = await SqlWriteGate.AcquireAsync(
                SqlWriteGate.KeyFor(_settings.ConnectionString, procName), ct).ConfigureAwait(false);

            await using var conn = new SqlConnection(_settings.ConnectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = new SqlCommand(procName, conn) { CommandType = CommandType.StoredProcedure };
            var p = cmd.Parameters.AddWithValue("@Records", table);
            p.SqlDbType = SqlDbType.Structured;
            p.TypeName = tvpType;
            var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return result is int n ? n : table.Rows.Count;
        }
        catch (SqlException ex)
        {
            _logger.LogError(ex, "SQL bulk merge failed: {Proc}", procName);
            throw;
        }
    }

    private async Task BulkUpsertAsync(string procName, string tvpType, DataTable table, CancellationToken ct)
    {
        try
        {
            // Serialize concurrent upsert calls against the same target proc.
            using var gate = await SqlWriteGate.AcquireAsync(
                SqlWriteGate.KeyFor(_settings.ConnectionString, procName), ct).ConfigureAwait(false);

            await using var conn = new SqlConnection(_settings.ConnectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = new SqlCommand(procName, conn) { CommandType = CommandType.StoredProcedure };
            var p = cmd.Parameters.AddWithValue("@Records", table);
            p.SqlDbType = SqlDbType.Structured;
            p.TypeName = tvpType;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (SqlException ex)
        {
            _logger.LogError(ex, "SQL bulk upsert failed: {Proc}", procName);
            throw;
        }
    }

    private static DataTable BuildDataTable(IReadOnlyList<TimeseriesRecord> rows)
    {
        var t = new DataTable();
        t.Columns.Add("DatasetId", typeof(int));
        t.Columns.Add("DataDate", typeof(DateTime));
        t.Columns.Add("Value", typeof(decimal));
        foreach (var r in rows)
            t.Rows.Add(r.DatasetId, r.DataDate, r.Value.HasValue ? (object)(decimal)r.Value.Value : DBNull.Value);
        return t;
    }

    private static DataTable BuildMetadataTable(IReadOnlyList<TimeseriesMetadataRecord> rows)
    {
        var t = new DataTable();
        t.Columns.Add("DatasetId", typeof(int));
        t.Columns.Add("Country", typeof(string));
        t.Columns.Add("CountryIso", typeof(string));
        t.Columns.Add("Region", typeof(string));
        t.Columns.Add("SubRegion", typeof(string));
        t.Columns.Add("Description", typeof(string));
        t.Columns.Add("Aspect", typeof(string));
        t.Columns.Add("AspectSubtype", typeof(string));
        t.Columns.Add("Category", typeof(string));
        t.Columns.Add("CategorySubtype", typeof(string));
        t.Columns.Add("ForecastStartDate", typeof(DateTime));
        t.Columns.Add("Frequency", typeof(string));
        t.Columns.Add("LifecycleStage", typeof(string));
        t.Columns.Add("Source", typeof(string));
        t.Columns.Add("Unit", typeof(string));
        t.Columns.Add("ReleaseDate", typeof(DateTime));
        t.Columns.Add("Basin", typeof(string));
        t.Columns.Add("PartnerSubRegion", typeof(string));
        t.Columns.Add("Pipeline", typeof(string));
        t.Columns.Add("EaIsoRto", typeof(string));
        foreach (var r in rows)
            t.Rows.Add(
                r.DatasetId, NullIfEmpty(r.Country), NullIfEmpty(r.CountryIso), NullIfEmpty(r.Region),
                NullIfEmpty(r.SubRegion), NullIfEmpty(r.Description), NullIfEmpty(r.Aspect),
                NullIfEmpty(r.AspectSubtype), NullIfEmpty(r.Category), NullIfEmpty(r.CategorySubtype),
                r.ForecastStartDate.HasValue ? (object)r.ForecastStartDate.Value : DBNull.Value,
                NullIfEmpty(r.Frequency), NullIfEmpty(r.LifecycleStage), NullIfEmpty(r.Source),
                NullIfEmpty(r.Unit),
                r.ReleaseDate.HasValue ? (object)r.ReleaseDate.Value : DBNull.Value,
                (object?)r.Basin ?? DBNull.Value,
                (object?)r.PartnerSubRegion ?? DBNull.Value,
                (object?)r.Pipeline ?? DBNull.Value,
                (object?)r.EaIsoRto ?? DBNull.Value);
        return t;
    }

    private static DataTable BuildMappingTable(IReadOnlyList<DatasetMapping> mappings)
    {
        var t = new DataTable();
        t.Columns.Add("MappingId", typeof(int));
        t.Columns.Add("MappingName", typeof(string));
        t.Columns.Add("Category", typeof(string));
        t.Columns.Add("RequestString", typeof(string));
        t.Columns.Add("Licensed", typeof(string));
        t.Columns.Add("IsActive", typeof(bool));
        foreach (var m in mappings)
            t.Rows.Add(m.MappingId, m.Name, m.Category, NullIfEmpty(m.RequestString), NullIfEmpty(m.Licensed), true);
        return t;
    }

    private static DataTable BuildMappingIdTable(IReadOnlyList<DatasetMapping> mappings)
    {
        var t = new DataTable();
        t.Columns.Add("MappingId", typeof(int));
        t.Columns.Add("DatasetId", typeof(int));
        foreach (var m in mappings)
            foreach (var id in m.DatasetIds)
                t.Rows.Add(m.MappingId, id);
        return t;
    }

    private static List<int> ParseDatasetIds(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new List<int>();
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries)
                  .Select(s => int.TryParse(s.Trim(), out var id) ? id : (int?)null)
                  .Where(x => x.HasValue).Select(x => x!.Value).ToList();
    }

    private static string ReadNullableString(SqlDataReader r, string col)
    {
        var ord = r.GetOrdinal(col);
        return r.IsDBNull(ord) ? string.Empty : r.GetString(ord);
    }

    private static object NullIfEmpty(string? v) =>
        string.IsNullOrWhiteSpace(v) ? DBNull.Value : v;
}
