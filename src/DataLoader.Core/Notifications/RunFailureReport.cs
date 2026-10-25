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

    private IReadOnlyList<LoaderRunResult> Failures =>
        Results.Where(r => !r.Success).ToList();

    /// <summary>
    /// Names the actual file when exactly one loader failed — the common shape,
    /// since each scheduled task passes ONE loader as argv. With several
    /// failures, or a failure outside any loader, no single log applies and the
    /// placeholder is the honest answer.
    /// </summary>
    private string LogPathHint
    {
        get
        {
            var failures = Failures;
            return Exception is null && failures.Count == 1
                ? $@"C:\ETL_Tools\DataLoaderPlatform\Log\{failures[0].LoaderId}.log"
                : @"C:\ETL_Tools\DataLoaderPlatform\Log\<Loader>.log";
        }
    }

    public bool ShouldNotify =>
        Exception is not null || StaleRowsClosed > 0 || Results.Any(r => !r.Success);

    /// <summary>
    /// No subject prefix here — <see cref="FailureNotifier"/> prepends it on the
    /// single shared send path, so no payload can forget it and ship an alert
    /// whose DEV and PROD copies read identically.
    ///
    /// <para>Throws if nothing is worth reporting. <c>FailureNotifier</c> always
    /// checks <see cref="ShouldNotify"/> first, so reaching here with it false
    /// can only be a caller bug — failing loudly beats silently mailing a
    /// nonsense "WARNING: 0 orphaned load-log row(s)" alert.</para>
    /// </summary>
    public NotificationMessage BuildMessage()
    {
        if (!ShouldNotify)
            throw new InvalidOperationException(
                "BuildMessage() was called on a report with nothing to report. " +
                "Check ShouldNotify first, as FailureNotifier does.");

        return new(BuildSubject(), BuildBody());
    }

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
        // Omitted when there is no real run to name — the watchdog's own
        // DB-read-failure alert passes Guid.Empty because it is reporting a
        // failed CHECK, not a run, and the all-zero guid would only confuse
        // whoever reads it.
        if (RunId != Guid.Empty)
            sb.AppendLine($"Run id:    {RunId}");
        sb.AppendLine($"Started:   {StartedAtUtc:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine($"Completed: {CompletedAtUtc:yyyy-MM-dd HH:mm:ss} UTC");
        // Omitted when the two are the same instant — that same watchdog alert
        // stamps both fields from one clock read, since it is reporting the
        // single moment it gave up, not a span with a duration.
        if (CompletedAtUtc != StartedAtUtc)
            sb.AppendLine($"Duration:  {FormatDuration(CompletedAtUtc - StartedAtUtc)}");
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

    /// <summary>
    /// Renders TOTAL hours rather than a <c>hh</c> component, because <c>hh</c>
    /// silently drops whole days: a 34-hour run formats as "10:05:00", which
    /// reads as ten minutes past ten. Multi-hour runs are routine here, so that
    /// is a live trap, and a wrong number with nothing marking it wrong is worse
    /// than no number at all. A negative span (clock skew) is signed rather than
    /// rendered as a normal short run.
    /// </summary>
    private static string FormatDuration(TimeSpan d)
    {
        var sign = d < TimeSpan.Zero ? "-" : string.Empty;
        d = d.Duration();
        return $"{sign}{(int)d.TotalHours:D2}:{d.Minutes:D2}:{d.Seconds:D2}";
    }
}
