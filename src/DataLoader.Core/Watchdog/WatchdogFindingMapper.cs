using System.Globalization;
using System.Text.Json;
using DataLoader.Core.Notifications;

namespace DataLoader.Core.Watchdog;

/// <summary>
/// Turns one pass's <see cref="WatchdogReport"/> into the rows, signal sets and closing
/// hints <c>core.usp_RecordWatchdogPass</c> takes. Pure — no clock, no database — so
/// every identity, severity and closure rule is unit-testable.
///
/// <para>The rules here are the persisted contract the DBA Agent reads; see
/// <c>sql/Core/007_CreateWatchdogFindings.sql</c> for the matching half in T-SQL.</para>
/// </summary>
public static class WatchdogFindingMapper
{
    /// <summary><c>core.WatchdogFinding.Summary</c> is NVARCHAR(400).</summary>
    public const int MaxSummaryChars = 400;

    /// <summary><c>DetailJson.reason</c> for a degraded check.</summary>
    public const int MaxReasonChars = 200;

    public const string SeverityCritical = "Critical";
    public const string SeverityWarning = "Warning";
    public const string SeverityInfo = "Info";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    /// <summary>
    /// Every finding detected this pass, plus suppressed (already self-healed) failures
    /// flagged <see cref="WatchdogFindingRow.IsSuppressed"/> so the proc records them
    /// already resolved. De-duplicated by key, case-insensitively, because the TVP's
    /// primary key is compared in a case-insensitive collation.
    /// </summary>
    public static IReadOnlyList<WatchdogFindingRow> BuildFindings(
        WatchdogReport report,
        IReadOnlyList<RecentFailure> suppressed,
        IReadOnlyList<DegradedCheck> degradedChecks,
        WatchdogSettings settings)
    {
        var rows = new List<WatchdogFindingRow>();

        foreach (var o in report.Overdue)
        {
            var name = CanonicalLoaderName(o.LoaderName, settings);
            var neverRun = o.LastStartCentral is null;
            var skipWeekends = settings.Loaders is not null &&
                               settings.Loaders.TryGetValue(name, out var ls) && ls.SkipWeekends;

            rows.Add(new WatchdogFindingRow(
                $"{WatchdogSignals.Overdue}|{name}",
                WatchdogSignals.Overdue,
                name,
                null,
                SeverityCritical,
                Summary(WatchdogReport.OverdueLine(o)),
                Detail(new Dictionary<string, object?>
                {
                    ["lastStartCentral"] = CentralText(o.LastStartCentral),
                    // Infinity for a loader that has never run — System.Text.Json throws on
                    // it, and "infinite hours" is not a number anyone should chart anyway.
                    ["hoursSinceLastRun"] = FiniteOrNull(o.HoursSinceLastRun),
                    ["maxGapHours"] = o.MaxGapHours,
                    ["skipWeekends"] = skipWeekends,
                    ["neverRun"] = neverRun
                }),
                false));
        }

        foreach (var m in report.Misconfigured)
        {
            var name = CanonicalLoaderName(m, settings);
            rows.Add(new WatchdogFindingRow(
                $"{WatchdogSignals.Misconfigured}|{name}",
                WatchdogSignals.Misconfigured,
                name,
                null,
                SeverityWarning,
                Summary(WatchdogReport.MisconfiguredLine(m)),
                Detail(new Dictionary<string, object?>
                {
                    ["configPath"] = Redact($"Platform:Watchdog:Loaders:{name}:MaxGapHours")
                }),
                false));
        }

        foreach (var s in report.Stuck)
        {
            rows.Add(new WatchdogFindingRow(
                $"{WatchdogSignals.Stuck}|{RunIdText(s.RunId)}",
                WatchdogSignals.Stuck,
                // core.LoaderRun carries no loader name, and a stuck run never reached
                // usp_CompleteLoaderRun, so core.LoaderRun_Summary has no row for it either.
                null,
                s.RunId,
                SeverityWarning,
                Summary(WatchdogReport.StuckLine(s)),
                Detail(new Dictionary<string, object?>
                {
                    ["startedAtUtc"] = UtcText(s.StartedAtUtc),
                    ["hostName"] = s.HostName is null ? null : Redact(s.HostName),
                    ["stuckAfterHours"] = settings.StuckAfterHours
                }),
                false));
        }

        foreach (var f in report.RecentFailures)
            rows.Add(FailureRow(f, settings, isSuppressed: false));

        foreach (var f in suppressed)
            rows.Add(FailureRow(f, settings, isSuppressed: true));

        foreach (var d in degradedChecks)
            rows.Add(DegradedRow(d));

        return rows
            .GroupBy(r => r.FindingKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    /// <summary>A DEGRADED finding for one check that could not run.</summary>
    public static WatchdogFindingRow DegradedRow(DegradedCheck d) => new(
        $"{WatchdogSignals.Degraded}|{d.Code}",
        WatchdogSignals.Degraded,
        null,
        null,
        SeverityCritical,
        Summary(d.EmailLine),
        Detail(new Dictionary<string, object?>
        {
            ["checkCode"] = d.Code,
            ["reason"] = ErrorTextRedactor.Clean(d.Reason, MaxReasonChars)
        }),
        false);

    /// <summary>
    /// Splits <see cref="WatchdogSignals.All"/> into checked and degraded for this pass,
    /// from the codes of the checks that could not run. A signal any code blinds is
    /// degraded; everything else was checked. With <c>HISTORY_QUERY</c> nothing is
    /// checked — not even DEGRADED, so no earlier DEGRADED finding closes on a pass
    /// that never got far enough to re-run its check.
    /// </summary>
    public static (IReadOnlyList<string> Checked, IReadOnlyList<string> Degraded) ComputeSignals(
        IEnumerable<string> degradedCodes)
    {
        var blinded = new HashSet<string>(
            degradedCodes.SelectMany(WatchdogDegradedCodes.SignalsBlindedBy), StringComparer.Ordinal);

        return (WatchdogSignals.All.Where(s => !blinded.Contains(s)).ToList(),
                WatchdogSignals.All.Where(blinded.Contains).ToList());
    }

    /// <summary>
    /// RunIds of open FAILED findings that were NOT detected this pass — the ones about to
    /// close, whose reason (SelfHealed vs AgedOutUnresolved) needs the resolver.
    /// </summary>
    public static IReadOnlyList<Guid> FailedRunIdsToResolve(
        IReadOnlyList<OpenWatchdogFinding> open, IReadOnlyList<WatchdogFindingRow> current)
    {
        var currentKeys = new HashSet<string>(current.Select(c => c.FindingKey), StringComparer.OrdinalIgnoreCase);
        return open
            .Where(o => o.Signal == WatchdogSignals.Failed && o.RunId is not null && !currentKeys.Contains(o.FindingKey))
            .Select(o => o.RunId!.Value)
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// Closing reasons for open findings that are about to close. Only sends what the
    /// proc cannot work out itself:
    /// <list type="bullet">
    /// <item>OVERDUE / MISCONFIGURED → <c>ConfigRemoved</c> when the loader is gone from
    /// <c>Platform:Watchdog:Loaders</c>, <c>LoaderDisabled</c> when <c>Enabled = false</c>.
    /// No hint means NotDetected (it was fixed).</item>
    /// <item>FAILED → <c>SelfHealed</c> only when the resolver confirmed every unit later
    /// succeeded; otherwise <c>AgedOutUnresolved</c>. A run the resolver could not find
    /// at all gets no hint, which the proc also treats as AgedOutUnresolved.</item>
    /// </list>
    /// STUCK needs no hint: the proc checks <c>core.LoaderRun</c> itself.
    /// </summary>
    public static IReadOnlyList<WatchdogResolutionHint> BuildHints(
        IReadOnlyList<OpenWatchdogFinding> open,
        IReadOnlyList<WatchdogFindingRow> current,
        WatchdogSettings settings,
        RecentFailuresResult? failedResolution)
    {
        var currentKeys = new HashSet<string>(current.Select(c => c.FindingKey), StringComparer.OrdinalIgnoreCase);
        var loaders = settings.Loaders ?? new Dictionary<string, WatchdogLoaderSettings>(StringComparer.OrdinalIgnoreCase);

        var healed = new HashSet<Guid>(failedResolution?.Suppressed.Select(s => s.RunId) ?? Enumerable.Empty<Guid>());
        var unresolved = new HashSet<Guid>(failedResolution?.Failures.Select(s => s.RunId) ?? Enumerable.Empty<Guid>());

        var hints = new List<WatchdogResolutionHint>();
        foreach (var o in open)
        {
            if (currentKeys.Contains(o.FindingKey)) continue;

            switch (o.Signal)
            {
                case WatchdogSignals.Overdue:
                case WatchdogSignals.Misconfigured:
                    if (o.LoaderName is null) break;
                    var entry = loaders.FirstOrDefault(
                        kv => string.Equals(kv.Key, o.LoaderName, StringComparison.OrdinalIgnoreCase));
                    if (entry.Key is null)
                        hints.Add(new(o.FindingKey, WatchdogResolutionReasons.ConfigRemoved));
                    else if (entry.Value is { Enabled: false })
                        hints.Add(new(o.FindingKey, WatchdogResolutionReasons.LoaderDisabled));
                    break;

                case WatchdogSignals.Failed:
                    if (o.RunId is not { } runId) break;
                    if (healed.Contains(runId))
                        hints.Add(new(o.FindingKey, WatchdogResolutionReasons.SelfHealed));
                    else if (unresolved.Contains(runId))
                        hints.Add(new(o.FindingKey, WatchdogResolutionReasons.AgedOutUnresolved));
                    break;
            }
        }

        return hints;
    }

    /// <summary>
    /// The release folder name when running from <c>Releases\&lt;folder&gt;\</c> — what
    /// <c>current.txt</c> named when this process started. Read from the exe's own
    /// location rather than <c>current.txt</c>, which a deploy can flip mid-pass.
    /// Null anywhere else (a dev box's <c>bin\Release\net8.0</c> is not a version).
    /// </summary>
    public static string? AppVersionFromBaseDirectory(string? baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory)) return null;
        var dir = new DirectoryInfo(baseDirectory.TrimEnd('\\', '/'));
        if (dir.Parent is null ||
            !string.Equals(dir.Parent.Name, "Releases", StringComparison.OrdinalIgnoreCase))
            return null;
        return dir.Name.Length <= 50 ? dir.Name : dir.Name[..50];
    }

    private static WatchdogFindingRow FailureRow(RecentFailure f, WatchdogSettings settings, bool isSuppressed)
    {
        var name = CanonicalLoaderName(f.LoaderName, settings);
        return new WatchdogFindingRow(
            $"{WatchdogSignals.Failed}|{RunIdText(f.RunId)}",
            WatchdogSignals.Failed,
            name,
            f.RunId,
            isSuppressed ? SeverityInfo : SeverityWarning,
            Summary(WatchdogReport.FailureLine(f)),
            Detail(new Dictionary<string, object?>
            {
                ["startTimeCentral"] = CentralText(f.StartTimeCentral),
                ["unitsFailed"] = f.UnitsFailed,
                ["unitsTotal"] = f.UnitsTotal,
                ["unresolvedUnits"] = f.UnresolvedUnits
            }),
            isSuppressed);
    }

    /// <summary>
    /// The config's own spelling, because config lookup is case-insensitive while the
    /// persisted key is compared as text by consumers. A name not in config (a FAILED
    /// run of an unmonitored loader) keeps the spelling it arrived with.
    /// </summary>
    internal static string CanonicalLoaderName(string name, WatchdogSettings settings)
    {
        if (settings.Loaders is null) return name;
        foreach (var key in settings.Loaders.Keys)
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                return key;
        return name;
    }

    private static string Summary(string line) => ErrorTextRedactor.Clean(line, MaxSummaryChars);

    private static string Redact(string text) => ErrorTextRedactor.Redact(text);

    private static string Detail(Dictionary<string, object?> fields) => JsonSerializer.Serialize(fields, Json);

    private static string RunIdText(Guid runId) => runId.ToString("D", CultureInfo.InvariantCulture);

    private static double? FiniteOrNull(double value) =>
        double.IsFinite(value) ? Math.Round(value, 2) : null;

    /// <summary>Central wall-clock time, no offset — the name (<c>*Central</c>) carries the zone.</summary>
    private static string? CentralText(DateTime? value) =>
        value?.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);

    private static string UtcText(DateTime value) =>
        value.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture) + "Z";
}
