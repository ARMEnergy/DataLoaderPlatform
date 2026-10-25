using System.Text;
using DataLoader.Core.Notifications;

namespace DataLoader.Core.Watchdog;

/// <summary>
/// What one watchdog pass found. Implements <see cref="INotificationPayload"/>
/// so it travels the same notifier path as an in-process run failure.
/// </summary>
public sealed class WatchdogReport : INotificationPayload
{
    public IReadOnlyList<OverdueLoader> Overdue { get; init; } = Array.Empty<OverdueLoader>();
    public IReadOnlyList<StuckRun> Stuck { get; init; } = Array.Empty<StuckRun>();
    public IReadOnlyList<RecentFailure> RecentFailures { get; init; } = Array.Empty<RecentFailure>();

    public bool ShouldNotify => Overdue.Count > 0 || Stuck.Count > 0 || RecentFailures.Count > 0;

    public NotificationMessage BuildMessage() => new(BuildSubject(), BuildBody());

    private string BuildSubject()
    {
        var parts = new List<string>();
        if (Overdue.Count > 0) parts.Add($"{Overdue.Count} loader{(Overdue.Count == 1 ? "" : "s")} overdue");
        if (Stuck.Count > 0) parts.Add($"{Stuck.Count} run{(Stuck.Count == 1 ? "" : "s")} stuck");
        if (RecentFailures.Count > 0) parts.Add($"{RecentFailures.Count} recent failure{(RecentFailures.Count == 1 ? "" : "s")}");
        return $"WATCHDOG: {string.Join(", ", parts)}";
    }

    private string BuildBody()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Watchdog pass on {Environment.MachineName} at {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
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

        sb.AppendLine(@"Full output: C:\ETL_Tools\DataLoaderPlatform\Log\<Loader>.log");
        return sb.ToString();
    }
}
