using System.Text;
using DataLoader.Core.Notifications;

namespace DataLoader.Core.Watchdog;

/// <summary>
/// What one watchdog pass found. Implements <see cref="INotificationPayload"/>
/// so it travels the same notifier path as an in-process run failure.
/// </summary>
public sealed class WatchdogReport : INotificationPayload
{
    /// <summary>Supplied by the caller, not read from the environment — see
    /// <see cref="INotificationPayload"/>, whose members must stay pure so the
    /// rendered alert is testable without a clock or a machine name.</summary>
    public string HostName { get; init; } = string.Empty;

    /// <summary>US Central, matching every other timestamp in this report.</summary>
    public DateTime GeneratedAtCentral { get; init; }

    public IReadOnlyList<OverdueLoader> Overdue { get; init; } = Array.Empty<OverdueLoader>();
    public IReadOnlyList<StuckRun> Stuck { get; init; } = Array.Empty<StuckRun>();
    public IReadOnlyList<RecentFailure> RecentFailures { get; init; } = Array.Empty<RecentFailure>();

    /// <summary>
    /// Loaders configured but unusable — <c>MaxGapHours</c> missing or ≤ 0.
    ///
    /// <para>Reported rather than skipped, because silently ignoring a
    /// misconfigured loader is exactly the blindness this component exists to
    /// prevent: the loader could stop running for a month and nothing would
    /// say so. <c>MaxGapHours</c> defaults to 0 while <c>Enabled</c> defaults
    /// to true, so an entry like <c>"Platts": {}</c> lands here.</para>
    /// </summary>
    public IReadOnlyList<string> Misconfigured { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Signals this pass could not check, with the reason.
    ///
    /// <para>A watchdog that can only half-see must say so. Silently returning
    /// "no stuck runs" when the stuck-run query threw would be a false
    /// all-clear — the single most damaging thing this component can emit.</para>
    /// </summary>
    public IReadOnlyList<string> Degraded { get; init; } = Array.Empty<string>();

    public bool ShouldNotify =>
        Overdue.Count > 0 || Stuck.Count > 0 || RecentFailures.Count > 0 ||
        Misconfigured.Count > 0 || Degraded.Count > 0;

    /// <summary>
    /// Throws if nothing is worth reporting, matching
    /// <see cref="RunFailureReport.BuildMessage"/> — two types implementing one
    /// interface should not disagree on this. <c>FailureNotifier</c> always
    /// checks <see cref="ShouldNotify"/> first, so reaching here with it false
    /// can only be a caller bug — failing loudly beats silently mailing a
    /// nonsense empty "WATCHDOG: " alert.
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
        var parts = new List<string>();
        if (Overdue.Count > 0) parts.Add($"{Overdue.Count} loader{(Overdue.Count == 1 ? "" : "s")} overdue");
        if (Stuck.Count > 0) parts.Add($"{Stuck.Count} run{(Stuck.Count == 1 ? "" : "s")} stuck");
        if (RecentFailures.Count > 0) parts.Add($"{RecentFailures.Count} recent failure{(RecentFailures.Count == 1 ? "" : "s")}");
        if (Misconfigured.Count > 0) parts.Add($"{Misconfigured.Count} loader{(Misconfigured.Count == 1 ? "" : "s")} misconfigured");
        if (Degraded.Count > 0) parts.Add($"{Degraded.Count} check{(Degraded.Count == 1 ? "" : "s")} degraded");
        return $"WATCHDOG: {string.Join(", ", parts)}";
    }

    private string BuildBody()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Watchdog pass on {HostName} at {GeneratedAtCentral:yyyy-MM-dd HH:mm} Central");
        sb.AppendLine();

        if (Overdue.Count > 0)
        {
            sb.AppendLine("OVERDUE — expected to have run by now");
            sb.AppendLine(new string('-', 60));
            foreach (var o in Overdue.OrderByDescending(o => o.HoursSinceLastRun))
            {
                sb.AppendLine(o.LastStartCentral is null
                    ? $"  {o.LoaderName}: has NEVER run (threshold {o.MaxGapHours}h)"
                    : $"  {o.LoaderName}: last started {o.LastStartCentral:yyyy-MM-dd HH:mm} Central, " +
                      $"{o.HoursSinceLastRun:F1}h ago (threshold {o.MaxGapHours}h)");
            }
            sb.AppendLine();
        }

        if (Stuck.Count > 0)
        {
            sb.AppendLine("STUCK — a run row that never closed, i.e. the process was killed");
            sb.AppendLine(new string('-', 60));
            foreach (var s in Stuck.OrderBy(s => s.StartedAtUtc))
                sb.AppendLine($"  {s.RunId} started {s.StartedAtUtc:yyyy-MM-dd HH:mm} UTC on {s.HostName ?? "unknown host"}");
            sb.AppendLine();
        }

        if (RecentFailures.Count > 0)
        {
            sb.AppendLine("RECENT FAILURES");
            sb.AppendLine(new string('-', 60));
            foreach (var f in RecentFailures.OrderByDescending(f => f.StartTimeCentral))
                sb.AppendLine($"  {f.LoaderName}: {f.StartTimeCentral:yyyy-MM-dd HH:mm} Central, " +
                              $"{f.UnitsFailed}/{f.UnitsTotal} units failed");
            sb.AppendLine();
        }

        if (Misconfigured.Count > 0)
        {
            sb.AppendLine("MISCONFIGURED — threshold missing or invalid, neither healthy nor overdue");
            sb.AppendLine(new string('-', 60));
            foreach (var name in Misconfigured.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
                sb.AppendLine($"  {name}: MaxGapHours is missing or <= 0 " +
                              $"(Platform:Watchdog:Loaders:{name}:MaxGapHours)");
            sb.AppendLine();
        }

        if (Degraded.Count > 0)
        {
            sb.AppendLine("COULD NOT CHECK — this pass is incomplete; absence of a finding above is NOT evidence of health");
            sb.AppendLine(new string('-', 60));
            foreach (var d in Degraded.OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                sb.AppendLine($"  {d}");
            sb.AppendLine();
        }

        // Not `Log\<Loader>.log` — this pass is not about any one loader, and
        // pointing there (as RunFailureReport does) would misdirect whoever
        // reads the alert. It is its own scheduled task with its own log, at
        // Log\Watchdog.log, once IT wires the \DLP\Watchdog task to redirect
        // its console output there the same way every other `.cmd` launcher does.
        sb.AppendLine(@"Full output: C:\ETL_Tools\DataLoaderPlatform\Log\Watchdog.log");
        return sb.ToString();
    }
}
