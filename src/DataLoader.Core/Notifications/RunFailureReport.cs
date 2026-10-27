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

    /// <summary>
    /// How many failed units get the FULL block (key, display, error text, and
    /// a trimmed stack). Beyond this and up to <c>LoaderPipelineBase</c>'s own
    /// 25-unit retention cap, units still get key/display/error text — just no
    /// stack, since a stack per unit is what actually makes the email large.
    /// </summary>
    private const int FullDetailUnitCount = 5;

    /// <summary>
    /// Hard ceiling on how many failed units this renders, enforced here
    /// independently of <c>LoaderPipelineBase.Counters</c>' (and
    /// <see cref="LoaderRunResult.Aggregate"/>'s) own retention cap — defense in
    /// depth, so a future caller that hands this a longer
    /// <see cref="LoaderRunResult.FailedUnits"/> list still gets a bounded email
    /// rather than one line per unit. Sourced from the same constant as those
    /// two so "defense in depth" does not quietly become "two different caps".
    /// </summary>
    private const int MaxRenderedFailedUnits = LoaderRunResult.MaxFailedUnitsRetained;

    /// <summary>Column the value starts at — every label below is padded to this width.</summary>
    private const int FailedUnitLabelWidth = 26;

    /// <summary>Same indentation as the loader's own error-message line.</summary>
    private const string FailedUnitIndent = "       ";

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

                AppendFailedUnits(sb, r);
            }
            sb.AppendLine();
        }

        sb.AppendLine($"Full output: {LogPathHint}");
        return sb.ToString();
    }

    /// <summary>
    /// Renders the per-work-unit failure detail immediately after a loader's
    /// own error line — key/display/error text for every captured unit, plus a
    /// trimmed stack for the first <see cref="FullDetailUnitCount"/> of them.
    ///
    /// <para><see cref="LoaderRunResult.FailedUnits"/> is already capped (at
    /// <see cref="LoaderRunResult.MaxFailedUnitsRetained"/>, in
    /// <c>LoaderPipelineBase.Counters</c> or <see cref="LoaderRunResult.Aggregate"/>)
    /// by the time it gets here, so this never iterates more than that. When the
    /// real failure count exceeds what was captured, one summary line reports
    /// how many more there were.</para>
    ///
    /// <para>⚠ A loader can report <see cref="LoaderRunResult.WorkUnitsFailed"/>
    /// greater than zero while <see cref="LoaderRunResult.FailedUnits"/> is
    /// empty — either a hand-rolled merge that forgot to carry the list (the bug
    /// that put zero units under "...and 4 more" in a real AGSI alert, before
    /// every such merge went through <see cref="LoaderRunResult.Aggregate"/>), or
    /// simply a pipeline that never captures per-unit detail to begin with. That
    /// is NOT the same situation as some-rendered-some-not, and must not reuse
    /// its wording: "...and 4 more" implies 4 were just listed above, which is a
    /// lie when the count rendered is zero. The two cases get different sentences.</para>
    ///
    /// <para>🔒 Every string below is vendor-supplied (a work unit's key/display
    /// name, and the error text and stack captured at the point of failure) and
    /// goes through <see cref="ErrorTextRedactor"/> before it enters the
    /// message — the same rule <see cref="ErrorMessage"/> above follows, and for
    /// the same reason: AGSI echoes <c>x-key</c> back in error bodies, OilX puts
    /// <c>api_key</c> in query strings, and either can show up in a unit's own
    /// error text or stack just as easily as in the loader-level message.</para>
    /// </summary>
    private static void AppendFailedUnits(StringBuilder sb, LoaderRunResult r)
    {
        var units = r.FailedUnits;
        var renderCount = Math.Min(units.Count, MaxRenderedFailedUnits);
        for (var i = 0; i < renderCount; i++)
            AppendFailedUnit(sb, units[i], includeStack: i < FullDetailUnitCount);

        if (renderCount == 0)
        {
            // Nothing was listed above, so "...and N more" would read as though
            // N units had just been shown. Say plainly that there is no detail,
            // rather than implying an overflow that never happened.
            if (r.WorkUnitsFailed > 0)
                sb.AppendLine($"{FailedUnitIndent}(no per-unit detail captured for this loader)");
            return;
        }

        var uncaptured = r.WorkUnitsFailed - renderCount;
        if (uncaptured > 0)
            sb.AppendLine($"{FailedUnitIndent}… and {uncaptured} more failed unit(s) — see the log");
    }

    private static void AppendFailedUnit(StringBuilder sb, FailedWorkUnit unit, bool includeStack)
    {
        sb.AppendLine(FailedUnitIndent + "Failed Work Unit Key:".PadRight(FailedUnitLabelWidth) +
                      ErrorTextRedactor.Clean(unit.Key, MaxErrorChars));
        sb.AppendLine(FailedUnitIndent + "Failed Work Unit Display:".PadRight(FailedUnitLabelWidth) +
                      ErrorTextRedactor.Clean(unit.DisplayName, MaxErrorChars));
        sb.AppendLine(FailedUnitIndent + "Error Text:".PadRight(FailedUnitLabelWidth) +
                      ErrorTextRedactor.Clean(unit.ErrorText, MaxErrorChars));

        if (!includeStack)
            return;

        // Formatting (frame trimming) happens first, then redaction — same
        // ordering StackTraceFormatter documents: trimming is not a security
        // boundary and must never substitute for Redact.
        var stack = ErrorTextRedactor.Redact(StackTraceFormatter.Format(unit.StackTrace));
        var lines = stack.Replace("\r\n", "\n").Split('\n');

        sb.AppendLine(FailedUnitIndent + "Error Stack:".PadRight(FailedUnitLabelWidth) + lines[0]);

        var continuationIndent = new string(' ', FailedUnitIndent.Length + FailedUnitLabelWidth);
        for (var i = 1; i < lines.Length; i++)
            sb.AppendLine(continuationIndent + lines[i]);
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
