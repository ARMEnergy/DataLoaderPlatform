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
    ///
    /// <para><paramref name="volumeAnomalies"/> are passed SEPARATELY from the report: they
    /// are persisted whether or not they are emailed, and the report only carries them when
    /// <see cref="WatchdogDataVolumeSettings.Notify"/> is on. Reading them off the report
    /// would make persistence depend on the email setting.</para>
    /// </summary>
    public static IReadOnlyList<WatchdogFindingRow> BuildFindings(
        WatchdogReport report,
        IReadOnlyList<RecentFailure> suppressed,
        IReadOnlyList<DegradedCheck> degradedChecks,
        WatchdogSettings settings,
        IReadOnlyList<VolumeAnomaly>? volumeAnomalies = null)
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
                // From core.LoaderRun_Summary / core.LoadLog (see StuckRun); null only for
                // a run killed before its first unit. Capped to the column's NVARCHAR(128) —
                // only a multi-loader run's joined list could reach it.
                StuckLoaderName(s.LoaderName, settings),
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

        foreach (var a in volumeAnomalies ?? Array.Empty<VolumeAnomaly>())
            rows.Add(VolumeRow(a, settings));

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
    /// <see cref="ComputeSignals(IEnumerable{string})"/> plus, when
    /// <paramref name="includeDataVolume"/>, <c>DATA_VOLUME</c> appended to whichever list
    /// applies — degraded if <c>VOLUME_QUERY</c>, <c>NO_LOADERS_CONFIGURED</c> or
    /// <c>HISTORY_QUERY</c> blinds it, else checked.
    ///
    /// <para><paramref name="includeDataVolume"/> means "the volume check was in scope this
    /// pass": <see cref="WatchdogDataVolumeSettings.Enabled"/> and a valid section. With it
    /// false the result is identical to the one-argument overload, which is what keeps a
    /// disabled pass byte-for-byte unchanged. The A4 master-switch-off case is added later
    /// by the sink, which is the component that knows whether anything is open.</para>
    /// </summary>
    public static (IReadOnlyList<string> Checked, IReadOnlyList<string> Degraded) ComputeSignals(
        IEnumerable<string> degradedCodes, bool includeDataVolume)
    {
        var codes = degradedCodes.ToList();
        var (chk, deg) = ComputeSignals(codes);
        if (!includeDataVolume) return (chk, deg);

        var blinded = codes.SelectMany(WatchdogDegradedCodes.SignalsBlindedBy).Contains(WatchdogSignals.DataVolume);
        return blinded
            ? (chk, deg.Append(WatchdogSignals.DataVolume).ToList())
            : (chk.Append(WatchdogSignals.DataVolume).ToList(), deg);
    }

    /// <summary>
    /// Closing hints for open <c>DATA_VOLUME</c> findings NOT re-detected this pass (design
    /// §A3/§A4). First match wins:
    /// <list type="number">
    /// <item><paramref name="masterSwitchOff"/> → <c>LoaderDisabled</c> (§A4).</item>
    /// <item>Key that does not parse (no day) → <c>AgedOutUnresolved</c>: it can never be judged.</item>
    /// <item>Loader gone from <c>Platform:Watchdog:Loaders</c> → <c>ConfigRemoved</c>.</item>
    /// <item>Loader <c>Enabled = false</c> or <c>Volume.Enabled = false</c> → <c>LoaderDisabled</c>.</item>
    /// <item>Day older than <see cref="WatchdogVolumeResult.WindowStart"/> → <c>AgedOutUnresolved</c>.</item>
    /// <item>Day judged in range this pass → no hint (the proc closes it <c>NotDetected</c> — the rerun case).</item>
    /// <item>Loader has <c>BaselineFromCentral</c> ≤ the day → no hint (<c>NotDetected</c>: the owner
    /// declared that level normal).</item>
    /// <item>Anything else → <c>KeepOpen</c>: in the window but not judgeable, so neither fixed
    /// nor aged out.</item>
    /// </list>
    ///
    /// <para>⚠ The caller must only call this when <c>DATA_VOLUME</c> is in
    /// <c>SignalsChecked</c> (or for the A4 case). With <paramref name="volume"/> null and the
    /// master switch on — which a correct runner never produces — every finding gets
    /// <c>KeepOpen</c>: an outcome nobody computed must never close anything.</para>
    /// </summary>
    public static IReadOnlyList<WatchdogResolutionHint> BuildVolumeHints(
        IReadOnlyList<OpenWatchdogFinding> open,
        IReadOnlyList<WatchdogFindingRow> current,
        WatchdogSettings settings,
        WatchdogVolumeResult? volume,
        bool masterSwitchOff)
    {
        var currentKeys = new HashSet<string>(current.Select(c => c.FindingKey), StringComparer.OrdinalIgnoreCase);
        var loaders = settings.Loaders ?? new Dictionary<string, WatchdogLoaderSettings>(StringComparer.OrdinalIgnoreCase);
        var judged = new HashSet<(string, DateOnly)>(
            (volume?.JudgedInRange ?? Array.Empty<VolumeLoaderDay>())
            .Select(j => (j.LoaderName.ToUpperInvariant(), j.DayCentral)));

        var hints = new List<WatchdogResolutionHint>();
        foreach (var o in open)
        {
            if (o.Signal != WatchdogSignals.DataVolume || currentKeys.Contains(o.FindingKey)) continue;

            if (masterSwitchOff)
            {
                hints.Add(new(o.FindingKey, WatchdogResolutionReasons.LoaderDisabled));
                continue;
            }

            // A key that does not parse has no day, so it can never be judged or re-detected:
            // KeepOpen would leave it open forever. Close it as aged out — explicitly NOT fixed.
            if (SqlWatchdogRepository.ParseVolumeFindingKey(o.FindingKey) is not { } p)
            {
                hints.Add(new(o.FindingKey, WatchdogResolutionReasons.AgedOutUnresolved));
                continue;
            }

            var entry = loaders.FirstOrDefault(
                kv => string.Equals(kv.Key, p.LoaderName, StringComparison.OrdinalIgnoreCase));
            if (entry.Key is null)
            {
                hints.Add(new(o.FindingKey, WatchdogResolutionReasons.ConfigRemoved));
                continue;
            }
            if (entry.Value is { Enabled: false } || entry.Value?.Volume is { Enabled: false })
            {
                hints.Add(new(o.FindingKey, WatchdogResolutionReasons.LoaderDisabled));
                continue;
            }

            // From here on the decision needs this pass's outcome.
            if (volume is null)
            {
                hints.Add(new(o.FindingKey, WatchdogResolutionHints.KeepOpen));
                continue;
            }

            // Only OLDER than the window ages out. A day NEWER than the window end — yesterday
            // on a pass before SettleHours — is simply not judgeable yet and falls through to
            // KeepOpen below.
            if (p.DayCentral < volume.WindowStart)
            {
                hints.Add(new(o.FindingKey, WatchdogResolutionReasons.AgedOutUnresolved));
                continue;
            }

            if (p.DayCentral <= volume.WindowEnd && judged.Contains((p.LoaderName.ToUpperInvariant(), p.DayCentral)))
                continue; // NotDetected — judged and in range.

            if (p.DayCentral <= volume.WindowEnd &&
                volume.BaselineFromByLoader.TryGetValue(p.LoaderName, out var baselineFrom) &&
                p.DayCentral >= baselineFrom)
                continue; // NotDetected — the owner declared this level normal.

            hints.Add(new(o.FindingKey, WatchdogResolutionHints.KeepOpen));
        }

        return hints;
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

    /// <summary>At most this many <c>loaderRunIds</c> go into a volume finding's DetailJson.</summary>
    public const int MaxVolumeLoaderRunIds = 20;

    /// <summary>
    /// <c>DATA_VOLUME|&lt;Loader&gt;|&lt;yyyy-MM-dd&gt;</c> — one finding per loader per Central day,
    /// whatever its direction, so a day that flips from Low to High on a rerun is the same
    /// finding refreshed rather than a second one. <c>RunId</c> is NULL: a day is many runs.
    /// </summary>
    public static string VolumeFindingKey(string loaderName, DateOnly dayCentral) =>
        $"{WatchdogSignals.DataVolume}|{loaderName}|{DayText(dayCentral)}";

    /// <summary>
    /// One <c>DATA_VOLUME</c> finding. Zero/Low → Warning, High → Info (usually a rerun or a
    /// backfill). Every date is a CENTRAL calendar day and every field name says so.
    /// </summary>
    public static WatchdogFindingRow VolumeRow(VolumeAnomaly a, WatchdogSettings settings)
    {
        var name = CanonicalLoaderName(a.LoaderName, settings);
        var anomaly = name == a.LoaderName ? a : a with { LoaderName = name };

        return new WatchdogFindingRow(
            VolumeFindingKey(name, a.DayCentral),
            WatchdogSignals.DataVolume,
            name,
            null,
            a.Direction == VolumeDirection.High ? SeverityInfo : SeverityWarning,
            Summary(WatchdogReport.VolumeLine(anomaly)),
            Detail(new Dictionary<string, object?>
            {
                ["dayCentral"] = DayText(a.DayCentral),
                ["weekday"] = a.DayCentral.DayOfWeek.ToString(),
                ["direction"] = a.Direction.ToString(),
                ["rows"] = a.Rows,
                ["successfulRuns"] = a.SuccessfulRuns,
                ["totalRuns"] = a.TotalRuns,
                ["medianRows"] = FiniteOrNull(a.MedianRows),
                ["ratio"] = FiniteOrNull(a.Ratio),
                ["lowPercent"] = a.LowPercent,
                // Not rounded: it is a configured threshold, not a measurement, and must read
                // back exactly as set (2.125 must not become 2.13).
                ["highMultiple"] = double.IsFinite(a.HighMultiple) ? a.HighMultiple : null,
                ["samples"] = a.Samples
                    .Select(s => new Dictionary<string, object?> { ["dayCentral"] = DayText(s.DayCentral), ["rows"] = s.Rows })
                    .ToList(),
                ["excludedSampleDays"] = a.ExcludedSampleDays.Select(DayText).ToList(),
                ["baselineFromCentral"] = a.BaselineFromCentral is { } bf ? DayText(bf) : null,
                ["loaderRunIds"] = a.LoaderRunIds.Take(MaxVolumeLoaderRunIds).ToList()
            }),
            false);
    }

    private static string DayText(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

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

    /// <summary><c>core.WatchdogFinding.LoaderName</c> is NVARCHAR(128).</summary>
    public const int MaxLoaderNameChars = 128;

    /// <summary>
    /// A STUCK finding's loader name: a single name takes the config's spelling, like every
    /// other signal's, so consumers can match it; a multi-loader run's joined list is kept as
    /// is and cut to <see cref="MaxLoaderNameChars"/>. Null stays null.
    /// </summary>
    internal static string? StuckLoaderName(string? name, WatchdogSettings settings)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var value = name.Contains(',') ? name : CanonicalLoaderName(name, settings);
        return value.Length <= MaxLoaderNameChars ? value : value[..MaxLoaderNameChars];
    }

    /// <summary>
    /// Redacted, and never longer than <see cref="MaxSummaryChars"/> — the column and the TVP
    /// are NVARCHAR(400).
    ///
    /// <para>⚠ <see cref="ErrorTextRedactor.Clean"/> cuts at the limit and THEN appends
    /// <c>… [truncated, N chars total]</c>, so on its own it overshoots. Its behaviour is left
    /// alone for its other callers; an over-long line is re-cut here so the suffix fits.
    /// Anything already within the limit is returned exactly as <c>Clean</c> produced it.</para>
    /// </summary>
    internal static string Summary(string line)
    {
        var cleaned = ErrorTextRedactor.Clean(line, MaxSummaryChars);
        if (cleaned.Length <= MaxSummaryChars) return cleaned;

        var redacted = ErrorTextRedactor.Redact(line);
        var suffix = $"… [truncated, {redacted.Length} chars total]";
        var cut = Math.Max(0, MaxSummaryChars - suffix.Length);
        if (cut > 0 && char.IsHighSurrogate(redacted[cut - 1])) cut--;
        var capped = redacted[..cut] + suffix;
        return capped.Length <= MaxSummaryChars ? capped : capped[..MaxSummaryChars];
    }

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
