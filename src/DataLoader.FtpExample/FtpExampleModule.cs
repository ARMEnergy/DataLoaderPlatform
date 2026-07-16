using System.Data;
using DataLoader.Core.Abstractions;
using DataLoader.Core.Pipeline;
using DataLoader.Core.Sinks;
using DataLoader.Core.Transforms;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.FtpExample;

/// <summary>
/// Lists files on the configured FTP server.
/// </summary>
public sealed class FtpWorkUnitProvider : IWorkUnitProvider<FtpFileWorkUnit>
{
    private readonly IFtpFileSystem _fs;
    private readonly FtpExampleSettings _settings;
    private readonly ILogger<FtpWorkUnitProvider> _logger;

    public FtpWorkUnitProvider(IFtpFileSystem fs, IOptions<FtpExampleSettings> settings, ILogger<FtpWorkUnitProvider> logger)
    {
        _fs = fs; _settings = settings.Value; _logger = logger;
    }

    public async Task<IReadOnlyList<FtpFileWorkUnit>> GetWorkUnitsAsync(LoaderRunContext context)
    {
        var files = await _fs.ListAsync(_settings.RemoteDirectory, _settings.FilePattern, context.CancellationToken)
                              .ConfigureAwait(false);
        _logger.LogInformation("FTP listed {Count} files in {Dir}", files.Count, _settings.RemoteDirectory);
        return files.Select(f => new FtpFileWorkUnit { File = f }).ToList();
    }
}

/// <summary>
/// Reads a remote file via FTP and parses each line.
/// </summary>
public sealed class FtpSourceReader : ISourceReader<FtpFileWorkUnit, FtpFeedRow>
{
    private readonly IFtpFileSystem _fs;
    private readonly FtpExampleSettings _settings;
    private readonly ILogger<FtpSourceReader> _logger;

    public FtpSourceReader(IFtpFileSystem fs, IOptions<FtpExampleSettings> settings, ILogger<FtpSourceReader> logger)
    {
        _fs = fs; _settings = settings.Value; _logger = logger;
    }

    public async Task<IReadOnlyList<FtpFeedRow>> ReadAsync(FtpFileWorkUnit unit, CancellationToken cancellationToken)
    {
        await using var stream = await _fs.OpenReadAsync(unit.File.FullPath, cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        var ingested = DateTime.UtcNow;
        var rows = new List<FtpFeedRow>();
        int n = 0;
        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
        {
            n++;
            if (n == 1 && _settings.HasHeaderRow) continue;
            if (string.IsNullOrWhiteSpace(line)) continue;
            rows.Add(new FtpFeedRow
            {
                SourceFile = unit.File.Name,
                IngestedAtUtc = ingested,
                RowNumber = n,
                Cells = line.Split(',')
            });
        }
        _logger.LogDebug("FTP file {Name} → {Rows} rows", unit.File.Name, rows.Count);
        return rows;
    }
}

/// <summary>
/// SQL sink that writes FTP feed rows to <c>ftp.RawRows</c>.
/// </summary>
public sealed class FtpSqlSink : SqlSinkBase<FtpFeedRow>
{
    private readonly FtpExampleSettings _settings;

    public FtpSqlSink(IOptions<FtpExampleSettings> settings, ILogger<FtpSqlSink> logger)
        : base(logger)
    {
        _settings = settings.Value;
    }

    protected override string GetConnectionString() => _settings.ConnectionString;
    protected override string StoredProcedureName => "ftp.usp_BulkInsertRawRows";
    protected override string TableValuedParameterType => "ftp.RawRowTvp";

    protected override DataTable BuildTable(IReadOnlyList<FtpFeedRow> rows)
    {
        var t = new DataTable();
        t.Columns.Add("SourceFile", typeof(string));
        t.Columns.Add("IngestedAtUtc", typeof(DateTime));
        t.Columns.Add("RowNumber", typeof(int));
        t.Columns.Add("RawLine", typeof(string));
        foreach (var r in rows)
            t.Rows.Add(NullIfEmpty(r.SourceFile), r.IngestedAtUtc, r.RowNumber, string.Join(",", r.Cells));
        return t;
    }
}

/// <summary>
/// Plugin entry point for the FTP loader.
/// </summary>
public sealed class FtpExampleModule : ILoaderModule
{
    public const string Id = "FtpExample";

    public string LoaderId => Id;
    public string DisplayName => "FTP Feed Loader";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<FtpExampleSettings>(configuration.GetSection($"Loaders:{Id}"));

        services.AddSingleton<IFtpFileSystem, FtpFileSystem>();
        services.AddSingleton<FtpWorkUnitProvider>();
        services.AddSingleton<FtpSourceReader>();
        services.AddSingleton<FtpSqlSink>();
        services.AddSingleton<IdentityTransformer<FtpFeedRow>>();

        services.AddSingleton<IWorkUnitProvider<FtpFileWorkUnit>>(sp => sp.GetRequiredService<FtpWorkUnitProvider>());
        services.AddSingleton<ISourceReader<FtpFileWorkUnit, FtpFeedRow>>(sp => sp.GetRequiredService<FtpSourceReader>());
        services.AddSingleton<ITransformer<FtpFeedRow, FtpFeedRow>>(sp => sp.GetRequiredService<IdentityTransformer<FtpFeedRow>>());
        services.AddSingleton<ISink<FtpFeedRow>>(sp => sp.GetRequiredService<FtpSqlSink>());

        services.AddSingleton<FtpExamplePipeline>();
    }

    public async Task<LoaderRunResult> RunAsync(IServiceProvider services, LoaderRunContext context)
    {
        var pipeline = services.GetRequiredService<FtpExamplePipeline>();
        return await pipeline.ExecuteAsync(context).ConfigureAwait(false);
    }
}

public sealed class FtpExamplePipeline : LoaderPipelineBase<FtpFileWorkUnit, FtpFeedRow, FtpFeedRow>
{
    public FtpExamplePipeline(
        IWorkUnitProvider<FtpFileWorkUnit> workUnits,
        ISourceReader<FtpFileWorkUnit, FtpFeedRow> source,
        ITransformer<FtpFeedRow, FtpFeedRow> transformer,
        ISink<FtpFeedRow> sink,
        ILoadLogRepository loadLog,
        IOptions<FtpExampleSettings> settings,
        ILogger<FtpExamplePipeline> logger)
        : base(FtpExampleModule.Id, workUnits, source, transformer, sink, loadLog, settings.Value, logger)
    {
    }
}
