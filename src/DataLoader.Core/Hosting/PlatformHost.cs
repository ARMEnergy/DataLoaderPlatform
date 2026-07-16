using System.Diagnostics;
using DataLoader.Core.Abstractions;
using DataLoader.Core.Concurrency;
using DataLoader.Core.Configuration;
using DataLoader.Core.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.Core.Hosting;

/// <summary>
/// Runs the discovered loader modules. The host's <c>Program.cs</c> resolves
/// this and calls <see cref="RunAsync"/>.
///
/// This class owns:
///   - filtering loaders by the <c>EnabledLoaders</c> config
///   - opening/closing the <c>core.LoaderRun</c> row
///   - running enabled loaders with bounded parallelism
///   - aggregating results
/// </summary>
public sealed class PlatformHost
{
    private readonly IServiceProvider _services;
    private readonly IReadOnlyList<ILoaderModule> _modules;
    private readonly ILoadLogRepository _runLog;
    private readonly ILoaderOverlapGuard _overlapGuard;
    private readonly PlatformSettings _settings;
    private readonly ILogger<PlatformHost> _logger;

    public PlatformHost(
        IServiceProvider services,
        IReadOnlyList<ILoaderModule> modules,
        ILoadLogRepository runLog,
        ILoaderOverlapGuard overlapGuard,
        IOptions<PlatformSettings> settings,
        ILogger<PlatformHost> logger)
    {
        _services = services;
        _modules = modules;
        _runLog = runLog;
        _overlapGuard = overlapGuard;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var runId = Guid.NewGuid();
        var startedAt = DateTime.UtcNow;
        var sw = Stopwatch.StartNew();

        var enabled = SelectEnabledModules();
        if (enabled.Count == 0)
        {
            _logger.LogWarning("No loaders enabled — nothing to do");
            return 0;
        }

        _logger.LogInformation("Run {RunId} starting with {Count} loaders: {Names}",
            runId, enabled.Count, string.Join(", ", enabled.Select(m => m.LoaderId)));

        await _runLog.BeginRunAsync(runId, cancellationToken).ConfigureAwait(false);

        var context = BuildContext(runId, startedAt, cancellationToken);
        var results = new List<LoaderRunResult>();
        var resultsLock = new object();

        await ParallelRunner.RunAsync(
            enabled,
            _settings.MaxConcurrentLoaders,
            async (module, ct) =>
            {
                var ctx = new LoaderRunContext
                {
                    RunId = runId,
                    StartedAtUtc = startedAt,
                    DateFrom = context.DateFrom,
                    DateTo = context.DateTo,
                    CancellationToken = ct
                };

                // Overlap guard — if another process is already running this
                // loader, skip rather than racing. The next scheduled run
                // will pick up whatever the current run leaves behind
                // (idempotent thanks to the load log).
                await using var lockHandle = await _overlapGuard
                    .TryAcquireAsync(module.LoaderId, ct).ConfigureAwait(false);

                LoaderRunResult result;
                if (lockHandle is null)
                {
                    result = new LoaderRunResult
                    {
                        LoaderId = module.LoaderId,
                        Success = true,
                        ErrorMessage = "Skipped — another instance already running",
                        Duration = TimeSpan.Zero
                    };
                }
                else
                {
                    try
                    {
                        result = await module.RunAsync(_services, ctx).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Loader {Loader} threw out of RunAsync", module.LoaderId);
                        result = LoaderRunResult.Failed(module.LoaderId, ex.Message, TimeSpan.Zero);
                    }
                }
                lock (resultsLock) results.Add(result);
            },
            cancellationToken
        ).ConfigureAwait(false);

        var allSucceeded = results.All(r => r.Success);
        await _runLog.CompleteRunAsync(runId, allSucceeded, CancellationToken.None).ConfigureAwait(false);

        LogSummary(results, sw.Elapsed);
        return allSucceeded ? 0 : 1;
    }

    private IReadOnlyList<ILoaderModule> SelectEnabledModules()
    {
        if (_settings.EnabledLoaders.Count == 0)
            return _modules;
        var set = new HashSet<string>(_settings.EnabledLoaders, StringComparer.OrdinalIgnoreCase);
        return _modules.Where(m => set.Contains(m.LoaderId)).ToList();
    }

    private LoaderRunContext BuildContext(Guid runId, DateTime startedAt, CancellationToken ct)
    {
        DateTime? from = null, to = null;
        if (_settings.DefaultDaysBackStart.HasValue)
            from = startedAt.Date.AddDays(-_settings.DefaultDaysBackStart.Value);
        if (_settings.DefaultDaysBackEnd.HasValue)
            to = startedAt.Date.AddDays(-_settings.DefaultDaysBackEnd.Value);

        return new LoaderRunContext
        {
            RunId = runId, StartedAtUtc = startedAt, DateFrom = from, DateTo = to,
            CancellationToken = ct
        };
    }

    private void LogSummary(IReadOnlyList<LoaderRunResult> results, TimeSpan elapsed)
    {
        _logger.LogInformation("=== Run summary ({Elapsed}) ===", elapsed);
        foreach (var r in results)
        {
            _logger.LogInformation(
                "  {Loader}: {Status} — {OK}/{Total} ok, {Skip} skipped, {Fail} failed, {Recs} records",
                r.LoaderId, r.Success ? "OK" : "FAIL",
                r.WorkUnitsSucceeded, r.WorkUnitsTotal, r.WorkUnitsSkipped, r.WorkUnitsFailed, r.RecordsProcessed);
            if (!r.Success && !string.IsNullOrEmpty(r.ErrorMessage))
                _logger.LogWarning("    error: {Error}", r.ErrorMessage);
        }
    }
}
