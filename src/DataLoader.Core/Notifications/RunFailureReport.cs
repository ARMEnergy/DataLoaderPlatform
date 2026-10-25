using System.Text;
using DataLoader.Core.Abstractions;

namespace DataLoader.Core.Notifications;

/// <summary>
/// What one host invocation has to report. Built by <c>PlatformHost</c> at the
/// end of a run, and by <c>Program.cs</c> when the process fails before a host
/// even exists.
///
/// <para>Both members of <see cref="INotificationPayload"/> are pure here, so
/// every rule about what counts as newsworthy and every word of the resulting
/// email is unit-testable with no mail server and no database.</para>
/// </summary>
public sealed class RunFailureReport : INotificationPayload
{
    /// <summary>Where this ran. The first thing anyone reading the alert wants.</summary>
    public required string HostName { get; init; }

    public Guid RunId { get; init; }
    public DateTime StartedAtUtc { get; init; }
    public DateTime CompletedAtUtc { get; init; }

    public IReadOnlyList<LoaderRunResult> Results { get; init; } = Array.Empty<LoaderRunResult>();

    /// <summary>
    /// How many <c>core.LoadLog</c> rows this run closed on behalf of EARLIER
    /// runs. Non-zero means a previous invocation was killed mid-unit — news
    /// worth sending even when the current run succeeded, because nothing else
    /// inside the process reports it.
    /// </summary>
    public int StaleRowsClosed { get; init; }

    /// <summary>Set when the process failed outright rather than a loader failing.</summary>
    public Exception? Exception { get; init; }

    /// <summary>
    /// Names the phase for an <see cref="Exception"/> that happened outside a
    /// loader — "Bootstrap" when the DI container or configuration failed.
    /// </summary>
    public string? FailedStage { get; init; }

    private const int MaxErrorChars = 2000;
    private const string LogPathHint = @"C:\ETL_Tools\DataLoaderPlatform\Log\<Loader>.log";

    private IReadOnlyList<LoaderRunResult> Failures =>
        Results.Where(r => !r.Success).ToList();

    public bool ShouldNotify =>
        Exception is not null || StaleRowsClosed > 0 || Results.Any(r => !r.Success);

    /// <summary>
    /// No subject prefix here — <see cref="FailureNotifier"/> prepends it on the
    /// single shared send path, so no payload can forget it and ship an alert
    /// whose DEV and PROD copies read identically.
    /// </summary>
    public NotificationMessage BuildMessage() => new(BuildSubject(), BuildBody());

    private string BuildSubject()
    {
        if (Exception is not null)
        {
            var stage = string.IsNullOrWhiteSpace(FailedStage) ? "run" : FailedStage;
            return $"FAILED: {stage} on {HostName} — {Exception.GetType().Name}";
        }

        var failures = Failures;
        if (failures.Count == 1)
        {
            var f = failures[0];
            return $"FAILED: {f.LoaderId} on {HostName} " +
                   $"({f.WorkUnitsFailed}/{f.WorkUnitsTotal} units failed)";
        }
        if (failures.Count > 1)
        {
            return $"FAILED: {failures.Count} loaders on {HostName}";
        }

        // Nothing failed now, so the only reason we are here is orphaned rows.
        return $"WARNING: {StaleRowsClosed} orphaned load-log row(s) on {HostName}";
    }

    private string BuildBody()
    {
        var sb = new StringBuilder();

        sb.AppendLine($"Host:      {HostName}");
        sb.AppendLine($"Run id:    {RunId}");
        sb.AppendLine($"Started:   {StartedAtUtc:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine($"Completed: {CompletedAtUtc:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine($"Duration:  {CompletedAtUtc - StartedAtUtc:hh\\:mm\\:ss}");
        sb.AppendLine();

        if (Exception is not null)
        {
            sb.AppendLine(string.IsNullOrWhiteSpace(FailedStage)
                ? "UNHANDLED EXCEPTION"
                : $"UNHANDLED EXCEPTION during {FailedStage}");
            sb.AppendLine(new string('-', 60));
            sb.AppendLine(ErrorTextRedactor.Clean(Exception.ToString(), MaxErrorChars));
            sb.AppendLine();
        }

        if (StaleRowsClosed > 0)
        {
            sb.AppendLine($"{StaleRowsClosed} load-log row(s) from an earlier run were left open — " +
                          "that run was killed before it could close them.");
            sb.AppendLine();
        }

        if (Results.Count > 0)
        {
            sb.AppendLine("Loaders");
            sb.AppendLine(new string('-', 60));
            foreach (var r in Results.OrderBy(r => r.Success).ThenBy(r => r.LoaderId))
            {
                sb.AppendLine($"  {(r.Success ? "OK  " : "FAIL")} {r.LoaderId}: " +
                              $"{r.WorkUnitsSucceeded}/{r.WorkUnitsTotal} ok, " +
                              $"{r.WorkUnitsSkipped} skipped, {r.WorkUnitsFailed} failed, " +
                              $"{r.RecordsProcessed} records");
                if (!string.IsNullOrEmpty(r.ErrorMessage))
                    sb.AppendLine($"       {ErrorTextRedactor.Clean(r.ErrorMessage, MaxErrorChars)}");
            }
            sb.AppendLine();
        }

        sb.AppendLine($"Full output: {LogPathHint}");
        return sb.ToString();
    }
}
