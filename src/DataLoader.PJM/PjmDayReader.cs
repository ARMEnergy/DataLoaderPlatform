using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace DataLoader.PJM;

/// <summary>
/// Shared per-unit read logic for every feed (design §4.2–§4.7, §6.2, generalised in §14.2):
/// explicit 1-based <c>startRow</c> paging, <c>totalRows</c> stability + rows==totalRows
/// reconciliation with ONE whole-unit re-read, required/extra-field policy, mapping (which
/// normalises and checks every timestamp pair through <see cref="PjmStamps"/>), the window echo on
/// <see cref="PjmFeed.FilterField"/> or the snapshot checks (§14.8), duplicate-key FAIL (compared
/// as SQL will), the empty-read rule (§14.7) and completeness warnings.
/// </summary>
public sealed class PjmDayReader<TRow> where TRow : IPjmRow
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>A snapshot evaluation stamped later than this past the run start fails (design §14.8 check 3).</summary>
    internal static readonly TimeSpan SnapshotFutureTolerance = TimeSpan.FromMinutes(5);

    /// <summary>Once-per-feed-per-process flag for the unexpected-field warning (design §4.3).</summary>
    private static readonly ConcurrentDictionary<string, bool> ExtraFieldWarned = new(StringComparer.OrdinalIgnoreCase);

    private readonly IPjmPageSource _pages;
    private readonly IPjmRowMapper<TRow> _mapper;
    private readonly PjmSettings _settings;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;

    /// <param name="time">Clock for the snapshot "read at" instant (design §14.8 check 3); defaults to the system clock.</param>
    public PjmDayReader(IPjmPageSource pages, IPjmRowMapper<TRow> mapper, PjmSettings settings, ILogger logger, TimeProvider? time = null)
    {
        _pages = pages;
        _mapper = mapper;
        _settings = settings;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Test seam: resets the once-per-process extra-field warning flags.</summary>
    internal static void ResetExtraFieldWarningsForTests() => ExtraFieldWarned.Clear();

    public async Task<IReadOnlyList<TRow>> ReadDayAsync(PjmDayWorkUnit unit, CancellationToken cancellationToken)
    {
        var first = await ReadAttemptAsync(unit, cancellationToken).ConfigureAwait(false);
        var attempt = first;

        if (first.Problem is not null)
        {
            // Data posts every five minutes, so a multi-page read can legitimately race a post.
            // One whole-unit re-read absorbs that; a second one would be masking something.
            _logger.LogWarning("[PJM {Feed}] {Day}: {Problem}; re-reading the whole unit once",
                unit.Feed.Name, unit.Label, first.Problem);

            var second = await ReadAttemptAsync(unit, cancellationToken).ConfigureAwait(false);
            if (second.Problem is not null)
                throw new PjmDataException(
                    $"PJM {unit.Feed.Name} {unit.Label}: paging did not reconcile twice — " +
                    $"first read: totalRows {Fmt(first.FirstTotal)}, rows read {first.RowsRead} ({first.Problem}); " +
                    $"re-read: totalRows {Fmt(second.FirstTotal)}, rows read {second.RowsRead} ({second.Problem})");
            attempt = second;
        }

        var rows = attempt.Rows;
        if (unit.IsSnapshot)
            CheckSnapshot(unit, rows, attempt.CompletedAtUtc);
        else
            ApplyEmptyReadRule(unit, rows.Count);
        WarnIfIncomplete(unit, rows);
        return rows;
    }

    private sealed record Attempt(List<TRow> Rows, int? FirstTotal, int RowsRead, string? Problem)
    {
        /// <summary>When the last response of a reconciled read arrived (UTC).</summary>
        public DateTime CompletedAtUtc { get; init; }
    }

    private async Task<Attempt> ReadAttemptAsync(PjmDayWorkUnit unit, CancellationToken ct)
    {
        var ctx = new PjmMapContext(unit.Feed, unit.Label, _settings.TimestampJitterToleranceSeconds);
        var windowStart = unit.WindowStartUtc;
        var windowEnd = unit.WindowEndUtc.AddSeconds(59); // the inclusive minute, to its last second
        var seen = new Dictionary<PjmRowKey, int>(KeyComparer.Instance);
        var rows = new List<TRow>();
        var startRow = 1;
        var rowsRead = 0;
        int? firstTotal = null;
        var index = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var page = await _pages.GetPageAsync(unit, startRow, ct).ConfigureAwait(false);

            if (firstTotal is null)
            {
                firstTotal = page.TotalRows;

                // Single-page contract (design §14.3, review fix): very_short's 6-hour block is ONE page
                // by design (43,200 rows). More than MaxPagesPerUnit pages means PJM added areas or
                // steps, and a multi-page read of a live block races the 5-minute posts (and its sort
                // is a single, non-unique column), so fail BEFORE reading any further page.
                if (unit.Feed.MaxPagesPerUnit is { } maxPages &&
                    (long)page.TotalRows > (long)_settings.PageSize * maxPages)
                    throw new PjmContractException(
                        $"PJM {unit.Feed.Name} {unit.Label}: totalRows {page.TotalRows} exceeds {maxPages} page(s) of {_settings.PageSize}; " +
                        "the unit is designed to be read in one page, so the feed's shape has changed and the unit size and timeout need review");
            }
            else if (page.TotalRows != firstTotal)
                return new Attempt(rows, firstTotal, rowsRead,
                    $"snapshot mismatch: totalRows changed from {firstTotal} to {page.TotalRows} at startRow {startRow}");

            foreach (var element in page.Items)
            {
                var item = ToItem(unit.Feed, index++, element);
                var row = _mapper.Map(item, ctx);
                if (!unit.IsSnapshot)
                    CheckWindowEcho(unit, row, windowStart, windowEnd);
                var key = row.Key;
                seen[key] = seen.TryGetValue(key, out var c) ? c + 1 : 1;
                rows.Add(row);
            }

            ThrowIfDuplicates(unit, seen);

            rowsRead += page.Items.Count;

            if (page.Items.Count == 0)
            {
                if (rowsRead < firstTotal)
                    return new Attempt(rows, firstTotal, rowsRead,
                        $"shortfall: an empty page at startRow {startRow} before totalRows {firstTotal} was reached (read {rowsRead})");
                break;
            }

            if (rowsRead >= firstTotal.Value)
                break;

            // Advance by what was RECEIVED, not by PageSize: a server that caps a page below
            // rowCount would otherwise make the loader skip the rows between (design §4.2).
            startRow += page.Items.Count;
        }

        if (rowsRead != firstTotal)
            return new Attempt(rows, firstTotal, rowsRead, $"shortfall: read {rowsRead} rows but totalRows is {firstTotal}");

        return new Attempt(rows, firstTotal, rowsRead, null) { CompletedAtUtc = _time.GetUtcNow().UtcDateTime };
    }

    /// <summary>Required fields present (fail), unknown fields named once per feed per process (warn).</summary>
    private PjmItem ToItem(PjmFeed feed, int index, JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new PjmContractException($"PJM {feed.Name}: item {index} is not a JSON object");

        var fields = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        List<string>? extras = null;
        foreach (var p in element.EnumerateObject())
        {
            fields.TryAdd(p.Name, p.Value);
            if (!feed.IsKnownField(p.Name))
                (extras ??= new List<string>()).Add(p.Name);
        }

        foreach (var required in feed.RequiredFields)
        {
            if (!fields.ContainsKey(required))
                throw new PjmContractException(
                    $"PJM {feed.Name}: item {index} is missing required field '{required}' — the feed's contract has changed");
        }

        if (extras is not null && ExtraFieldWarned.TryAdd(feed.Name, true))
        {
            var names = string.Join(", ", extras.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.Ordinal));
            _logger.LogWarning(
                "[PJM {Feed}] unexpected field(s) {Fields}: rows still load, but the API doc §11.1 metadata and the DDL may need a new column",
                feed.Name, names);
        }

        return new PjmItem(feed.Name, index, fields);
    }

    /// <summary>
    /// Window echo (design §4.4, §14.6): every row's <see cref="PjmFeed.FilterField"/> value must lie
    /// inside <c>[WindowStartUtc, WindowEndUtc:59]</c>. Forecast TARGET times legitimately fall
    /// outside and are not echo-checked.
    /// </summary>
    private static void CheckWindowEcho(PjmDayWorkUnit unit, TRow row, DateTime windowStart, DateTime windowEnd)
    {
        var echo = row.EchoUtc;
        if (echo >= windowStart && echo <= windowEnd) return;

        var what = unit.Feed.UnitHours >= 24 ? "day" : "block";
        throw new PjmDataException(
            $"PJM {unit.Feed.Name} {unit.Label}: a row's {unit.Feed.FilterField} {T(echo)} lies outside the requested UTC {what}; " +
            $"the filter was ignored or misread, and storing it would write another unit's {what}");
    }

    /// <summary>
    /// Snapshot checks (design §14.8): empty → FAIL (<see cref="PjmFailureClass.EmptySnapshot"/>,
    /// counts toward the trip); more than one evaluation → contract FAIL; an evaluation in the
    /// future → FAIL; a stale evaluation → WARNING only (the rows are real and re-storing them is a no-op).
    /// </summary>
    private void CheckSnapshot(PjmDayWorkUnit unit, List<TRow> rows, DateTime readAtUtc)
    {
        if (rows.Count == 0)
            throw new PjmDataException($"{unit.Feed.Name} {unit.Label}: {unit.Feed.EmptyPastDayReason}", PjmFailureClass.EmptySnapshot);

        var evaluations = rows.Select(r => r.EchoUtc).Distinct().OrderBy(t => t).ToList();
        if (evaluations.Count > 1)
            throw new PjmContractException(
                $"PJM {unit.Feed.Name} {unit.Label}: the snapshot holds {evaluations.Count} distinct {unit.Feed.KeyFields[0]} values " +
                $"({T(evaluations[0])} … {T(evaluations[^1])}); the feed is no longer a single latest-evaluation snapshot, so the key and unit model must be revisited");

        var evaluatedAt = evaluations[0];

        // Compared with the moment the response ARRIVED, never the run's StartedAtUtc: a run reaches
        // this feed minutes after it starts (~29 min on a first run), so an issue PJM posted after
        // the run began is legitimate and must not fail (review fix 2026-11-23, design §14.8 check 3).
        if (evaluatedAt > readAtUtc + SnapshotFutureTolerance)
            throw new PjmDataException(
                $"PJM {unit.Feed.Name} {unit.Label}: {unit.Feed.KeyFields[0]} {T(evaluatedAt)} is more than " +
                $"{SnapshotFutureTolerance.TotalMinutes:0} min after it was read ({T(readAtUtc)}); the column is not UTC or a clock is wrong");

        if (unit.Feed.SnapshotStaleAfterHours is { } staleHours && evaluatedAt < readAtUtc.AddHours(-staleHours))
            _logger.LogWarning(
                "[PJM {Feed}] {Label}: evaluation {EvaluatedAt} is {Hours:0.0} h old, beyond {Limit} h — PJM may have stopped refreshing it; stored anyway",
                unit.Feed.Name, unit.Label, T(evaluatedAt), (readAtUtc - evaluatedAt).TotalHours, staleHours);
    }

    /// <summary>Duplicate key → FAIL, never de-duplicate (design §4.6). Not retried.</summary>
    private static void ThrowIfDuplicates(PjmDayWorkUnit unit, Dictionary<PjmRowKey, int> seen)
    {
        var dups = seen.Where(kv => kv.Value > 1).ToList();
        if (dups.Count == 0) return;

        var extra = dups.Sum(kv => kv.Value - 1);
        var firstDup = dups
            .OrderBy(kv => kv.Key.K1)
            .ThenBy(kv => kv.Key.K2 ?? DateTime.MinValue)
            .ThenBy(kv => kv.Key.Member, StringComparer.Ordinal)
            .First();
        throw new PjmDataException(
            $"PJM {unit.Feed.Name} {unit.Label}: duplicate key ({DescribeKey(unit.Feed, firstDup.Key)}) " +
            $"appears {firstDup.Value} times; {dups.Count} key(s) duplicated, {extra} extra row(s). " +
            "Compared as SQL will (case-insensitive, trailing spaces ignored). Nothing was written; the key is wrong or PJM published a duplicate");
    }

    /// <summary><c>field value, …, member 'x'</c> in <see cref="PjmFeed.KeyFields"/> order.</summary>
    private static string DescribeKey(PjmFeed feed, PjmRowKey key)
    {
        var f = feed.KeyFields;
        var parts = new List<string> { $"{f[0]} {T(key.K1)}" };
        if (key.K2 is { } k2)
            parts.Add($"{(f.Count > 2 ? f[1] : "key2")} {T(k2)}");
        parts.Add($"{f[^1]} '{key.Member}'");
        return string.Join(", ", parts);
    }

    /// <summary>
    /// Empty-read rule (design §6.2, §14.7). A day unit is exempt through
    /// <see cref="PjmFeed.EmptyOkThroughAge"/> (0 = today only). ⚠ A unit of under 24 hours is
    /// exempt only while it is RECENT: a block of today that has left its Recent tail is Hot, and 0
    /// rows there must fail.
    /// </summary>
    private void ApplyEmptyReadRule(PjmDayWorkUnit unit, int rowCount)
    {
        if (rowCount > 0) return;

        var age = unit.Age;
        var exempt = unit.Feed.UnitHours >= 24
            ? age <= unit.Feed.EmptyOkThroughAge
            : unit.Zone == PjmZone.Recent || age < 0;
        if (exempt) return; // not posted yet

        var limit = unit.Feed.ExpectRowsWithinDays;
        if (limit is null || age <= limit.Value)
        {
            var reason = unit.Feed.EmptyPastDayReason
                .Replace("{age}", age.ToString(Inv), StringComparison.Ordinal)
                .Replace("{limit}", (limit ?? 0).ToString(Inv), StringComparison.Ordinal);
            throw new PjmDataException($"{unit.Feed.Name} {unit.Label}: {reason}", PjmFailureClass.EmptyPastDay);
        }

        _logger.LogWarning(
            "[PJM {Feed}] {Day}: 0 rows at age {Age}, past the expectation band ({Limit} days) — likely purged by vendor retention; recorded as done",
            unit.Feed.Name, unit.Label, age, limit);
    }

    /// <summary>Completeness warnings, never failures (design §4.5, §14.7). Units with rows only.</summary>
    private void WarnIfIncomplete(PjmDayWorkUnit unit, List<TRow> rows)
    {
        var rule = unit.Feed.Completeness;
        if (rule.Mode == PjmCompletenessMode.None || rows.Count == 0) return;
        if (unit.Age < rule.FromAge) return;
        if (!unit.IsSnapshot && unit.Feed.UnitHours < 24 && unit.Zone == PjmZone.Recent) return;

        switch (rule.Mode)
        {
            case PjmCompletenessMode.DistinctSlotsTotal:
            {
                // LIVE-VERIFIED 2026-11-23: on the fall-back day gen_by_fuel has only 23 hours — PJM
                // drops the repeated 01:00 EPT hour (UTC 06:00 absent on 2026-11-01). Expect 23 then.
                var expected = rule.ExpectedPerUnit -
                    (unit.Feed.DropsRepeatedFallBackHour ? PjmTime.RepeatedEasternHours(unit.Day) : 0);
                var hours = rows.Select(r => r.SlotUtc).Distinct().Count();
                if (hours < expected)
                    _logger.LogWarning("{Feed} {Day}: {Hours}/{Expected} hours", unit.Feed.Name, unit.Label, hours, expected);
                return;
            }

            case PjmCompletenessMode.DistinctSlotsPerMember:
            {
                var shortMembers = rows
                    .GroupBy(r => r.Key.Member.TrimEnd(' '), StringComparer.OrdinalIgnoreCase)
                    .Select(g => (Member: g.Key, Count: g.Select(r => r.SlotUtc).Distinct().Count()))
                    .Where(a => a.Count < rule.ExpectedPerUnit)
                    .OrderBy(a => a.Member, StringComparer.Ordinal)
                    .ToList();

                if (shortMembers.Count > 0)
                    _logger.LogWarning(
                        "{Feed} {Day}: {ShortCount} {Member}(s) below {Expected} intervals (minimum {Min}); first: {First}",
                        unit.Feed.Name, unit.Label, shortMembers.Count, unit.Feed.MemberField, rule.ExpectedPerUnit,
                        shortMembers.Min(a => a.Count), string.Join(", ", shortMembers.Take(3).Select(a => a.Member)));
                return;
            }

            case PjmCompletenessMode.RowsPerMemberExact:
            {
                var offMembers = rows
                    .GroupBy(r => r.Key.Member.TrimEnd(' '), StringComparer.OrdinalIgnoreCase)
                    .Select(g => (Member: g.Key, Count: g.Count()))
                    .Where(a => a.Count != rule.ExpectedPerUnit)
                    .OrderBy(a => a.Member, StringComparer.Ordinal)
                    .ToList();

                if (offMembers.Count > 0)
                    _logger.LogWarning(
                        "{Feed} {Day}: {OffCount} {Member}(s) with a row count other than {Expected} (range {Min}-{Max}); first: {First}",
                        unit.Feed.Name, unit.Label, offMembers.Count, unit.Feed.MemberField, rule.ExpectedPerUnit,
                        offMembers.Min(a => a.Count), offMembers.Max(a => a.Count),
                        string.Join(", ", offMembers.Take(3).Select(a => a.Member)));
                return;
            }
        }
    }

    private static string T(DateTime t) => t.ToString("yyyy-MM-ddTHH:mm:ss", Inv);
    private static string Fmt(int? n) => n?.ToString(Inv) ?? "n/a";

    /// <summary>
    /// The key compared the way SQL will (design §4.6): timestamps exact (already whole seconds),
    /// member under OrdinalIgnoreCase after trimming TRAILING SPACES ONLY (CI collation + ANSI padding).
    /// </summary>
    private sealed class KeyComparer : IEqualityComparer<PjmRowKey>
    {
        public static readonly KeyComparer Instance = new();

        public bool Equals(PjmRowKey x, PjmRowKey y) =>
            x.K1 == y.K1 && x.K2 == y.K2 &&
            StringComparer.OrdinalIgnoreCase.Equals(x.Member.TrimEnd(' '), y.Member.TrimEnd(' '));

        public int GetHashCode(PjmRowKey obj) =>
            HashCode.Combine(obj.K1, obj.K2, StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Member.TrimEnd(' ')));
    }
}
