using System.Data;
using DataLoader.Core.Configuration;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.Core.Persistence;

/// <summary>
/// Calls <c>core.usp_RunAdditionalProcesses</c> once per loader, after that
/// loader finishes and before the run row is closed. The proc is a dispatch
/// shell owned by the DBA side — it decides what post-load work (if any) a
/// given loader needs, across whatever databases it needs to touch.
///
/// <para>
/// Used by <see cref="Hosting.PlatformHost"/>. It is deliberately NOT called
/// when the overlap guard skipped the loader (another process holds the lock
/// and is already doing this work) or when the run was cancelled.
/// </para>
/// </summary>
public interface IAdditionalProcessRunner
{
    /// <summary>
    /// Runs the post-load hook for <paramref name="loaderId"/> under
    /// <paramref name="runId"/>. Never throws: failures are logged and
    /// swallowed, because post-processing that cannot run must not turn a
    /// successful load into a failed one.
    /// </summary>
    Task RunAsync(string loaderId, Guid runId, CancellationToken cancellationToken);
}

public sealed class SqlAdditionalProcessRunner : IAdditionalProcessRunner
{
    private readonly PlatformSettings _settings;
    private readonly ILogger<SqlAdditionalProcessRunner> _logger;

    public SqlAdditionalProcessRunner(IOptions<PlatformSettings> settings, ILogger<SqlAdditionalProcessRunner> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task RunAsync(string loaderId, Guid runId, CancellationToken cancellationToken)
    {
        if (!_settings.RunAdditionalProcesses)
        {
            _logger.LogDebug(
                "[{Loader}] Platform:RunAdditionalProcesses is false — skipping core.usp_RunAdditionalProcesses",
                loaderId);
            return;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await using var conn = new SqlConnection(_settings.LoadLogConnectionString);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var cmd = new SqlCommand("core.usp_RunAdditionalProcesses", conn)
            {
                CommandType = CommandType.StoredProcedure,

                // NOT the 30-second ADO.NET default. This proc fans out to post-load
                // work across several databases, which is exactly the shape that
                // outgrows 30s — and this repo has already been bitten twice by that
                // default (Criterion's validator, and a GasProduction merge batch).
                // A client-side expiry here aborts whatever the proc was midway
                // through, so the ceiling is deliberately generous.
                CommandTimeout = Math.Max(1, _settings.AdditionalProcessesTimeoutSeconds)
            };
            cmd.Parameters.Add("@LoaderName", SqlDbType.VarChar, 150).Value = loaderId;
            cmd.Parameters.Add("@RunId", SqlDbType.UniqueIdentifier).Value = runId;

            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "[{Loader}] core.usp_RunAdditionalProcesses completed in {Elapsed}", loaderId, sw.Elapsed);
        }
        catch (Exception ex)
        {
            // Swallowed BY DESIGN — see the interface doc. The loader's result and
            // the host's exit code are not touched. Logged at Error so it is still
            // visible; this is the only signal that post-processing did not run.
            _logger.LogError(ex,
                "[{Loader}] core.usp_RunAdditionalProcesses failed after {Elapsed} — the loader's outcome is " +
                "unchanged, but its post-run processing did NOT run for RunId {RunId}",
                loaderId, sw.Elapsed, runId);
        }
    }
}
