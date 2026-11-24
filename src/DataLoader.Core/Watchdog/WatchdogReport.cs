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

    /// <summary>
    /// One line — <c>Findings were NOT persisted: &lt;redacted reason&gt;</c> — when the
    /// findings sink failed this pass; null otherwise.
    ///
    /// <para>⚠ Deliberately NOT part of <see cref="ShouldNotify"/>: persistence is an
    /// additional sink, and the email's "when to send" rules stay exactly as they were.
    /// A persistence failure on an all-clear pass is logged at Error and shows up as a
    /// missing <c>core.WatchdogPass</c> row, which the off-box heartbeat check watches.</para>
    /// </summary>
    public string? PersistenceWarning { get; init; }

    /// <summary>
    /// <c>DATA_VOLUME</c> anomalies to EMAIL. Empty unless
    /// <see cref="WatchdogDataVolumeSettings.Notify"/> is on — with it off the anomalies are
    /// persisted (the mapper takes them separately) and this report, its subject, body and
    /// <see cref="ShouldNotify"/> are exactly what they were before the signal existed.
    /// </summary>
    public IReadOnlyList<VolumeAnomaly> VolumeAnomalies { get; init; } = Array.Empty<VolumeAnomaly>();

    public bool ShouldNotify =>
        Overdue.Count > 0 || Stuck.Count > 0 || RecentFailures.Count > 0 ||
        Misconfigured.Count > 0 || Degraded.Count > 0 || VolumeAnomalies.Count > 0;

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
        if (VolumeAnomalies.Count > 0) parts.Add($"{VolumeAnomalies.Count} row-volume anomal{(VolumeAnomalies.Count == 1 ? "y" : "ies")}");
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
                sb.AppendLine($"  {OverdueLine(o)}");
            sb.AppendLine();
        }

        if (Stuck.Count > 0)
        {
            sb.AppendLine("STUCK — a run row that never closed, i.e. the process was killed");
            sb.AppendLine(new string('-', 60));
            foreach (var s in Stuck.OrderBy(s => s.StartedAtUtc))
                sb.AppendLine($"  {StuckLine(s)}");
            sb.AppendLine();
        }

        if (RecentFailures.Count > 0)
        {
            sb.AppendLine("RECENT FAILURES");
            sb.AppendLine(new string('-', 60));
            foreach (var f in RecentFailures.OrderByDescending(f => f.StartTimeCentral))
                sb.AppendLine($"  {FailureLine(f)}");
            sb.AppendLine();
        }

        if (VolumeAnomalies.Count > 0)
        {
            sb.AppendLine("ROW VOLUME — ran successfully but wrote an unusual number of rows (rows WRITTEN, not available)");
            sb.AppendLine(new string('-', 60));
            foreach (var a in VolumeAnomalies
                         .OrderBy(a => a.LoaderName, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(a => a.DayCentral))
                sb.AppendLine($"  {VolumeLine(a)}");
            sb.AppendLine();
        }

        if (Misconfigured.Count > 0)
        {
            sb.AppendLine("MISCONFIGURED — threshold missing or invalid, neither healthy nor overdue");
            sb.AppendLine(new string('-', 60));
            foreach (var name in Misconfigured.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
                sb.AppendLine($"  {MisconfiguredLine(name)}");
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
        if (!string.IsNullOrEmpty(PersistenceWarning))
        {
            sb.AppendLine(PersistenceWarning);
            sb.AppendLine();
        }

        sb.AppendLine(@"Full output: C:\ETL_Tools\DataLoaderPlatform\Log\Watchdog.log");
        return sb.ToString();
    }

    // ---------------------------------------------------------------------------------
    // One line per finding, shared with WatchdogFindingMapper so the persisted Summary is
    // the SAME text as the email line and the two cannot drift. The email indents each
    // line by two spaces; these return it unindented.
    // ---------------------------------------------------------------------------------

    internal static string OverdueLine(OverdueLoader o) =>
        o.LastStartCentral is null
            ? $"{o.LoaderName}: has NEVER run (threshold {o.MaxGapHours}h)"
            : $"{o.LoaderName}: last started {o.LastStartCentral:yyyy-MM-dd HH:mm} Central, " +
              $"{o.HoursSinceLastRun:F1}h ago (threshold {o.MaxGapHours}h)";

    internal static string StuckLine(StuckRun s) =>
        $"{s.LoaderName ?? "Unknown loader"} run {s.RunId} started {s.StartedAtUtc:yyyy-MM-dd HH:mm} UTC " +
        $"on {s.HostName ?? "unknown host"}";

    internal static string FailureLine(RecentFailure f)
    {
        // Shown only when it adds information: if UnresolvedUnits == UnitsFailed,
        // either nothing resolved yet or resolution was never evaluated
        // (SkipAlertIfResolved = false) — either way the plain count already says
        // everything there is to say, and this keeps that path's text identical to
        // before this feature existed.
        var unresolvedSuffix = f.UnresolvedUnits < f.UnitsFailed
            ? $" ({f.UnresolvedUnits} unresolved)"
            : string.Empty;
        return $"{f.LoaderName}: {f.StartTimeCentral:yyyy-MM-dd HH:mm} Central, " +
               $"{f.UnitsFailed}/{f.UnitsTotal} units failed{unresolvedSuffix}";
    }

    internal static string MisconfiguredLine(string name) =>
        $"{name}: MaxGapHours is missing or <= 0 (Platform:Watchdog:Loaders:{name}:MaxGapHours)";

    /// <summary>
    /// <c>Marex loaded 2,500 rows on Thu 2026-11-19 (Central), 29% of its usual Thursday (8,700).</c>
    /// / <c>… 3.6x its usual Monday (190,000).</c> — the persisted Summary and the email line.
    /// InvariantCulture throughout, so the thousands separator does not follow the box's locale.
    /// </summary>
    internal static string VolumeLine(VolumeAnomaly a)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var day = a.DayCentral.ToDateTime(TimeOnly.MinValue);
        var head = string.Format(inv, "{0} loaded {1:N0} rows on {2:ddd yyyy-MM-dd} (Central), ",
            a.LoaderName, a.Rows, day);
        var weekday = a.DayCentral.DayOfWeek.ToString();
        var median = Math.Round(a.MedianRows, MidpointRounding.AwayFromZero).ToString("N0", inv);

        return a.Direction == VolumeDirection.High
            ? head + string.Format(inv, "{0:0.0}x its usual {1} ({2}).", a.Ratio, weekday, median)
            : head + string.Format(inv, "{0} of its usual {1} ({2}).", LowPercentText(a), weekday, median);
    }

    /// <summary>
    /// The percentage in a Low/Zero line. Whole percent normally (<c>29%</c>), but never text
    /// that contradicts the verdict: a Low at 49.6% against a 50% threshold would round to
    /// "50%", and a non-zero day would round to "0%" — so those show one decimal place,
    /// rounded DOWN (<c>49.6%</c>), or <c>&lt;1%</c> below one percent. A Zero day is <c>0%</c>.
    /// </summary>
    internal static string LowPercentText(VolumeAnomaly a)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var pct = a.Ratio * 100;
        if (a.Rows > 0 && pct < 1) return "<1%";

        var whole = Math.Round(pct, MidpointRounding.AwayFromZero);
        // (A non-zero day under 1% was handled above, so "0%" can only mean a Zero day here.)
        if (whole >= a.LowPercent && a.Rows > 0)
            return (Math.Floor(pct * 10) / 10).ToString("0.0", inv) + "%";

        return whole.ToString("0", inv) + "%";
    }

    /// <summary>
    /// A copy of this report carrying <paramref name="warning"/> as
    /// <see cref="PersistenceWarning"/>. The report is otherwise immutable, and the
    /// warning is only known after the evaluator built it.
    /// </summary>
    public WatchdogReport WithPersistenceWarning(string? warning) => Copy(warning, VolumeAnomalies);

    /// <summary>
    /// A copy carrying <paramref name="anomalies"/> as <see cref="VolumeAnomalies"/>. Only
    /// called with <see cref="WatchdogDataVolumeSettings.Notify"/> on; the volume check runs
    /// beside <see cref="WatchdogEvaluator.Evaluate"/>, not inside it, so its signature and
    /// every existing report stay as they were.
    /// </summary>
    public WatchdogReport WithVolumeAnomalies(IReadOnlyList<VolumeAnomaly> anomalies) =>
        Copy(PersistenceWarning, anomalies);

    // ⚠ Every init-only member must be listed here, or one With* call silently drops it.
    private WatchdogReport Copy(string? persistenceWarning, IReadOnlyList<VolumeAnomaly> volumeAnomalies) => new()
    {
        HostName = HostName,
        GeneratedAtCentral = GeneratedAtCentral,
        Overdue = Overdue,
        Stuck = Stuck,
        RecentFailures = RecentFailures,
        Misconfigured = Misconfigured,
        Degraded = Degraded,
        PersistenceWarning = persistenceWarning,
        VolumeAnomalies = volumeAnomalies
    };
}
