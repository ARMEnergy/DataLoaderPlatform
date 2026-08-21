using DataLoader.Core.Abstractions;
using DataLoader.Core.Hosting;
using DataLoader.Core.Pipeline;
using DataLoader.Core.Transforms;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.OPIS;

/// <summary>Marker so the module can resolve its one pipeline without colliding with other loaders' registrations.</summary>
public interface IOpisPipeline : ILoaderPipeline
{
}

/// <summary>One closed pipeline over the platform's standard ETL loop.</summary>
public sealed class OpisPipeline : LoaderPipelineBase<OpisWorkUnit, OpisLpReportRow, OpisLpReportRow>, IOpisPipeline
{
    public OpisPipeline(
        IWorkUnitProvider<OpisWorkUnit> provider,
        ISourceReader<OpisWorkUnit, OpisLpReportRow> source,
        ISink<OpisLpReportRow> sink,
        ILoadLogRepository loadLog,
        OpisSettings settings,
        ILogger logger)
        : base(OpisModule.Id, provider, source, new IdentityTransformer<OpisLpReportRow>(), sink, loadLog, settings, logger)
    {
    }
}

/// <summary>
/// Plugin entry point for the OPIS FTP loader.
///
/// <para>
/// One feed, one pipeline: every <c>&lt;yyyyMMdd&gt;LP.csv</c> on
/// <c>ftp.opisnet.com</c> is a work unit; the reader downloads and parses it; a
/// single sink merges the rows into <c>arm.LPReportHistory</c> and
/// <c>arm.LPReport</c> in one transaction. The reader already emits the final row
/// type, so the pipeline's transformer is
/// <see cref="IdentityTransformer{T}"/>.
/// </para>
/// <para>
/// The pipeline is built explicitly (the Platts/Vulcan posture) rather than via
/// open-generic DI, so this loader's <c>IWorkUnitProvider&lt;T&gt;</c> and
/// <c>ISink&lt;T&gt;</c> registrations cannot collide with another loader's in the
/// shared container.
/// </para>
/// </summary>
public sealed class OpisModule : ILoaderModule
{
    public const string Id = "OPIS";

    public string LoaderId => Id;
    public string DisplayName => "OPIS FTP (LP daily price report)";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        // AddLoaderSettings (not a bare Configure<>) — this is what wires the
        // SEE_DB resolver that turns the "SEE_DB" Username/Password sentinels into
        // the real values from core.Param at run time.
        services.AddLoaderSettings<OpisSettings>(configuration, Id);

        services.AddSingleton<IOpisFtp, OpisFtpFileSystem>();
        services.AddSingleton<IOpisFileLog, SqlOpisFileLog>();
        services.AddSingleton<OpisLoadValidator>();

        // Concrete type, not the open-generic IWorkUnitProvider<T> — that would
        // collide with other loaders in the shared container. Registered (rather
        // than newed in the factory) so RunAsync can read back the dates this run
        // actually enumerated instead of re-listing the FTP drop.
        services.AddSingleton<OpisWorkUnitProvider>();

        services.AddSingleton<IOpisPipeline>(BuildPipeline);
    }

    private static IOpisPipeline BuildPipeline(IServiceProvider sp)
    {
        var options = sp.GetRequiredService<IOptions<OpisSettings>>();
        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();

        var ftp = sp.GetRequiredService<IOpisFtp>();
        var fileLog = sp.GetRequiredService<IOpisFileLog>();

        var provider = sp.GetRequiredService<OpisWorkUnitProvider>();
        var source = new OpisSourceReader(ftp, fileLog, options, loggerFactory.CreateLogger<OpisSourceReader>());
        var sink = new OpisLpReportSqlSink(options, loggerFactory.CreateLogger<OpisLpReportSqlSink>());

        return new OpisPipeline(
            provider, source, sink,
            sp.GetRequiredService<ILoadLogRepository>(),
            options.Value,
            loggerFactory.CreateLogger("OPIS.Pipeline"));
    }

    public async Task<LoaderRunResult> RunAsync(IServiceProvider services, LoaderRunContext context)
    {
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("OPIS.Module");
        var pipeline = services.GetRequiredService<IOpisPipeline>();

        var result = await pipeline.ExecuteAsync(context).ConfigureAwait(false);

        // Post-load validation is observational and must not change the run's
        // outcome — OpisLoadValidator swallows its own failures. Scope it with the
        // newest date the pipeline's own enumeration saw (null when nothing ran,
        // which validates the whole table) — no second FTP listing.
        var latestDate = services.GetRequiredService<OpisWorkUnitProvider>().LastEnumeratedMaxDate;
        var validator = services.GetRequiredService<OpisLoadValidator>();
        await validator.ValidateAsync(latestDate, context.CancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "OPIS run complete: {Succeeded} succeeded, {Skipped} skipped, {Failed} failed, {Records} record(s)",
            result.WorkUnitsSucceeded, result.WorkUnitsSkipped, result.WorkUnitsFailed, result.RecordsProcessed);

        return result;
    }
}
