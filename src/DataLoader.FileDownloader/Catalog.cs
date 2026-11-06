using System.Data;
using DataLoader.Core.Resilience;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace DataLoader.FileDownloader;

/// <summary>One row of <c>download.FileDownloader_Info</c> — a configured Link source.</summary>
public sealed record FileDownloaderInfo(
    int Id,
    string Name,
    string BaseUrl,
    string DestinationPath,
    string? DocumentName);

/// <summary>
/// One pending download from a source's queue view.
///
/// <para><see cref="Value"/> is the <c>;</c>-delimited argument list for the source's
/// <see cref="FileDownloaderInfo.BaseUrl"/> <c>string.Format</c> template.
/// <see cref="FileName"/> is composed by the view itself from
/// <c>FileDownloader_Info.DocumentName</c>, so the naming convention stays in SQL where
/// an operator can change it without a deployment.</para>
/// </summary>
public sealed record FileDownloaderQueueRow(int Id, string Value, string FileName);

/// <summary>Reads the Link-mode catalogue and drains its queue.</summary>
public interface IFileDownloaderCatalog
{
    Task<IReadOnlyList<FileDownloaderInfo>> GetActiveSourcesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<FileDownloaderQueueRow>> GetQueueAsync(int infoId, CancellationToken cancellationToken);

    /// <summary>Marks one queue row handled so the view stops offering it.</summary>
    Task StampAsync(int infoId, int rowId, bool isNotFound, CancellationToken cancellationToken);
}

/// <summary>
/// SQL implementation over the <c>download</c> schema in Mako.
///
/// <para>⚠ Every statement here is a fixed string or a stored-procedure call. The
/// configured <c>ListTable</c>/<c>UpdateTable</c> names are never seen by this code —
/// they are resolved, schema-checked and quoted inside
/// <c>download.usp_GetFileDownloaderQueue</c> / <c>usp_StampFileDownloaderRow</c>. That
/// is deliberate: it keeps the original app's data-driven design (an operator adds a
/// source by inserting a row) without this platform ever compiling SQL from
/// configuration.</para>
/// </summary>
public sealed class SqlFileDownloaderCatalog : IFileDownloaderCatalog
{
    private readonly IOptions<FileDownloaderSettings> _options;

    public SqlFileDownloaderCatalog(IOptions<FileDownloaderSettings> options) => _options = options;

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(_options.Value.ConnectionString);
        // OpenWithRetryAsync, not OpenAsync: this is the first database call a
        // FileDownloader run makes, and the first connection of a process is exactly
        // where the Windows Update stall killed loaders (see SqlConnectionResilience).
        await connection.OpenWithRetryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    public async Task<IReadOnlyList<FileDownloaderInfo>> GetActiveSourcesAsync(
        CancellationToken cancellationToken)
    {
        const string sql = @"
SELECT Id, Name, BaseURL, DestinationPath, DocumentName
FROM download.FileDownloader_Info
WHERE IsActive = 1
ORDER BY Id;";

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(sql, connection)
        {
            CommandTimeout = _options.Value.TransferTimeoutSeconds
        };

        var results = new List<FileDownloaderInfo>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new FileDownloaderInfo(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return results;
    }

    public async Task<IReadOnlyList<FileDownloaderQueueRow>> GetQueueAsync(
        int infoId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand("download.usp_GetFileDownloaderQueue", connection)
        {
            CommandType = CommandType.StoredProcedure,
            CommandTimeout = _options.Value.TransferTimeoutSeconds
        };
        command.Parameters.Add(new SqlParameter("@InfoId", SqlDbType.Int) { Value = infoId });

        var results = new List<FileDownloaderQueueRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            // Value is '' on most views rather than NULL, but IsDBNull costs nothing and
            // a NULL here would otherwise be an unexplained cast exception at run time.
            results.Add(new FileDownloaderQueueRow(
                reader.GetInt32(0),
                reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                reader.GetString(2)));
        }

        return results;
    }

    public async Task StampAsync(
        int infoId, int rowId, bool isNotFound, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand("download.usp_StampFileDownloaderRow", connection)
        {
            CommandType = CommandType.StoredProcedure,
            CommandTimeout = _options.Value.TransferTimeoutSeconds
        };
        command.Parameters.Add(new SqlParameter("@InfoId", SqlDbType.Int) { Value = infoId });
        command.Parameters.Add(new SqlParameter("@RowId", SqlDbType.Int) { Value = rowId });
        command.Parameters.Add(new SqlParameter("@IsNotFound", SqlDbType.Bit) { Value = isNotFound });

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
