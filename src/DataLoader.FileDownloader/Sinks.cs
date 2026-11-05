using System.Data;
using DataLoader.Core.Sinks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.FileDownloader;

/// <summary>
/// Merges the run's manifest into <c>download.FileDownloader_File</c> through
/// <c>download.usp_MergeFileDownloaderFile</c>.
/// </summary>
public sealed class FileDownloaderFileSink : SqlSinkBase<FileDownloadRow>
{
    private readonly IOptions<FileDownloaderSettings> _options;

    public FileDownloaderFileSink(
        IOptions<FileDownloaderSettings> options,
        ILogger<FileDownloaderFileSink> logger)
        : base(logger)
    {
        _options = options;
    }

    protected override string GetConnectionString() => _options.Value.ConnectionString;

    protected override string StoredProcedureName => "download.usp_MergeFileDownloaderFile";

    protected override string TableValuedParameterType => "download.FileDownloaderFileTvp";

    /// <summary>
    /// ⚠ TVP CONTRACT. Column NAME, ORDER and TYPE must match
    /// <c>sql/FileDownloader/002_CreateFileDownloaderTvpTypes.sql</c> exactly. SQL Server
    /// binds a TVP positionally, so a reordered column is not an error — it is a silent
    /// mis-write. The numbering below is the type's column ordinal; keep both in step.
    /// </summary>
    protected override DataTable BuildTable(IReadOnlyList<FileDownloadRow> rows)
    {
        var t = new DataTable();

        t.Columns.Add("SourceName", typeof(string));       //  1  VARCHAR(200)
        t.Columns.Add("Mode", typeof(string));             //  2  VARCHAR(20)
        t.Columns.Add("FileName", typeof(string));         //  3  NVARCHAR(260)
        t.Columns.Add("DestinationPath", typeof(string));  //  4  NVARCHAR(400)
        t.Columns.Add("FileDate", typeof(DateTime));       //  5  DATE          NULL
        t.Columns.Add("SizeBytes", typeof(long));          //  6  BIGINT
        t.Columns.Add("Status", typeof(string));           //  7  VARCHAR(20)
        t.Columns.Add("DownloadedAtUtc", typeof(DateTime));//  8  DATETIME2(3)
        t.Columns.Add("InfoId", typeof(int));              //  9  INT           NULL
        t.Columns.Add("SourceLocator", typeof(string));    // 10  NVARCHAR(1000)

        foreach (var r in rows)
        {
            t.Rows.Add(
                r.SourceName,
                r.Mode,
                r.FileName,
                r.DestinationPath,
                DbNullable(r.FileDate),
                r.SizeBytes,
                r.Status,
                r.DownloadedAtUtc,
                DbNullable(r.InfoId),
                r.SourceLocator);
        }

        return t;
    }
}
