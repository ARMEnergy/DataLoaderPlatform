using System.Globalization;
using System.Text.Json;
using DataLoader.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace DataLoader.MISO;

/// <summary>
/// One validated source row: its EST interval, its member (as sent, trimmed) and the canonical
/// upper-case member, plus the raw element the feed mapper reads measures from.
/// </summary>
public sealed record MisoRecord(DateTime Start, DateTime End, string Member, string MemberKey, JsonElement Source);

/// <summary>Per-feed mapping from validated records to target rows (pivot or one row each).</summary>
public interface IMisoRowMapper<TRow>
{
    IReadOnlyList<TRow> Map(IReadOnlyList<MisoRecord> records, MisoWorkUnit unit);
}

/// <summary>
/// The one reader shared by all feeds (design §4). Pages 1..N until <c>lastPage</c>, applies the
/// status-matrix outcomes the client classified (design §5), then validates the whole unit and
/// FAILS it — never silently drops — when any rule is false:
/// <list type="number">
///   <item>rows read == page 1's <c>totalElements</c>; every <c>page.pageNumber</c> echoes the request;</item>
///   <item>every row is <c>hourly</c>, exactly one hour long, and starts on the requested date (echo);</item>
///   <item>MTLF: every row's <c>init</c> echoes the requested init (the vendor silently defaults it);</item>
///   <item>the member is in the feed's known set (case-insensitive);</item>
///   <item>fuel keys are known (in the mapper);</item>
///   <item>no duplicate <c>(interval, member)</c>;</item>
///   <item>complete: 24 distinct hourly intervals, each with every member.</item>
/// </list>
/// </summary>
public sealed class MisoDayReader<TRow> : ISourceReader<MisoWorkUnit, TRow>
{
    /// <summary>Paging backstop: every feed fits on one page of 1,000 (API doc §2).</summary>
    internal const int MaxPages = 20;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly IMisoPageSource _pages;
    private readonly IMisoRowMapper<TRow> _mapper;
    private readonly MisoSettings _settings;
    private readonly ILogger _logger;

    public MisoDayReader(IMisoPageSource pages, IMisoRowMapper<TRow> mapper, MisoSettings settings, ILogger logger)
    {
        _pages = pages;
        _mapper = mapper;
        _settings = settings;
        _logger = logger;
    }

    public async Task<IReadOnlyList<TRow>> ReadAsync(MisoWorkUnit unit, CancellationToken cancellationToken)
    {
        var where = $"MISO {unit.Feed.Id} {unit.Label}";
        var items = new List<JsonElement>();
        var totalElements = 0;
        var totalPages = 0;

        for (var page = 1; ; page++)
        {
            if (page > MaxPages)
                throw new MisoDataException($"{where}: still not on the last page after {MaxPages} pages; refusing to page further");

            var result = await _pages.GetPageAsync(unit, page, cancellationToken).ConfigureAwait(false);
            switch (result.Outcome)
            {
                case MisoPageOutcome.NotYet:
                    if (page > 1) throw new MisoDataException($"{where}: page {page} answered not-yet after page 1 had data ({result.Reason})");
                    EnsureHot(unit, where, "not published yet");
                    _logger.LogInformation("[MISO {Feed}] {Unit}: not published yet ({Reason}); 0 rows, stays hot", unit.Feed.Id, unit.Label, result.Reason);
                    return Array.Empty<TRow>();

                case MisoPageOutcome.OutOfRetention:
                    if (page > 1) throw new MisoDataException($"{where}: page {page} answered out-of-retention after page 1 had data ({result.Reason})");
                    // Only plausible near the edge of the rolling lookback. The vendor checks the
                    // requested date D (the forecast date for MTLF), so age is measured from D. A
                    // younger date answered this way means MISO shrank its lookback or mis-answered,
                    // and that must not settle an empty key.
                    var dateAge = unit.TodayEst.DayNumber - unit.MarketDate.DayNumber;
                    var floor = OutOfRetentionMinAge(_settings);
                    if (dateAge < floor)
                        throw new MisoDataException(
                            $"{where}: MISO says the date is beyond its lookback ({result.Reason}) but it is only {dateAge} days old; " +
                            $"accepted only from {floor} days (MaxLookbackDays {_settings.MaxLookbackDays} − {OutOfRetentionMarginDays}). " +
                            "MISO may have shrunk its lookback; lower MaxLookbackDays or raise StartDate if that is confirmed");
                    _logger.LogWarning("[MISO {Feed}] {Unit}: outside MISO's rolling lookback ({Reason}); 0 rows. Raise StartDate or lower MaxLookbackDays",
                        unit.Feed.Id, unit.Label, result.Reason);
                    return Array.Empty<TRow>();

                case MisoPageOutcome.Empty:
                    if (page > 1)
                        throw new MisoDataException($"{where}: HTTP 404 on page {page} although page {page - 1} said it was not the last ({result.Reason})");
                    if (unit.Age > _settings.ExpectRowsWithinDays)
                        throw new MisoDataException(
                            $"{where}: HTTP 404 '{result.Reason}' for a date {unit.Age} days old; MISO publishes within {_settings.ExpectRowsWithinDays} days, so an empty day this old is a failure");
                    EnsureHot(unit, where, "empty");
                    _logger.LogInformation("[MISO {Feed}] {Unit}: empty ({Reason}) at age {Age}; 0 rows, stays hot", unit.Feed.Id, unit.Label, result.Reason, unit.Age);
                    return Array.Empty<TRow>();

                case MisoPageOutcome.Data:
                    var info = result.Page ?? throw new MisoContractException($"{where}: page {page} has no page object");
                    if (info.PageNumber != page)
                        throw new MisoDataException($"{where}: asked for page {page} but the envelope says page {info.PageNumber}");
                    if (page == 1)
                    {
                        totalElements = info.TotalElements;
                        totalPages = info.TotalPages;
                    }
                    else if (info.TotalElements != totalElements || info.TotalPages != totalPages)
                    {
                        // The result set changed under the paging (or the vendor mis-answered): rows could be
                        // skipped or doubled, so the unit fails rather than reconciling against a moving total.
                        throw new MisoDataException(
                            $"{where}: page {page} reports totalElements {info.TotalElements} / totalPages {info.TotalPages}, " +
                            $"but page 1 reported {totalElements} / {totalPages}");
                    }
                    items.AddRange(result.Rows);
                    if (info.LastPage) return Finish(unit, where, items, totalElements);
                    break;

                default:
                    throw new InvalidOperationException($"{where}: unknown page outcome {result.Outcome}");
            }
        }
    }

    /// <summary>How far inside <c>MaxLookbackDays</c> an out-of-retention answer is still believed (design §5).</summary>
    internal const int OutOfRetentionMarginDays = 30;

    /// <summary>The youngest date age (from D) at which a "beyond the allowed lookback period" 400 is a 0-row success.</summary>
    internal static int OutOfRetentionMinAge(MisoSettings settings) => settings.MaxLookbackDays - OutOfRetentionMarginDays;

    /// <summary>The never-settle-a-not-yet invariant (design §6): a zero-row outcome on a settled key fails instead.</summary>
    private static void EnsureHot(MisoWorkUnit unit, string where, string what)
    {
        if (unit.Zone == MisoZone.Settled)
            throw new MisoDataException(
                $"{where}: {what} on a SETTLED unit (age {unit.Age}); refusing to record a settled key with zero rows");
    }

    private IReadOnlyList<TRow> Finish(MisoWorkUnit unit, string where, List<JsonElement> items, int totalElements)
    {
        // Rule 1: reconcile against the envelope's own count.
        if (items.Count != totalElements)
            throw new MisoDataException($"{where}: read {items.Count} row(s) but page 1 reported totalElements {totalElements}");

        var records = Validate(unit, where, items);
        return _mapper.Map(records, unit);
    }

    internal static IReadOnlyList<MisoRecord> Validate(MisoWorkUnit unit, string where, IReadOnlyList<JsonElement> items)
    {
        var feed = unit.Feed;
        var records = new List<MisoRecord>(items.Count);
        var seen = new HashSet<(DateTime, string)>();
        var expectedInit = unit.InitDate is { } i ? MisoTime.Format(i) : null;

        for (var n = 0; n < items.Count; n++)
        {
            var item = items[n];
            var at = $"{where}: row {n}";
            if (item.ValueKind != JsonValueKind.Object) throw new MisoDataException($"{at} is not a JSON object");

            // Rule 2: hourly, one hour long, starts on the requested date.
            if (!MisoJson.TryGet(item, "timeInterval", out var ti) || ti.ValueKind != JsonValueKind.Object)
                throw new MisoDataException($"{at} has no timeInterval object");
            var resolution = Str(ti, "resolution");
            if (!string.Equals(resolution, "hourly", StringComparison.OrdinalIgnoreCase))
                throw new MisoDataException($"{at}: timeInterval.resolution is '{Short(resolution)}', expected 'hourly'");
            if (!MisoTime.TryParseInterval(Str(ti, "start"), out var start) || !MisoTime.TryParseInterval(Str(ti, "end"), out var end))
                throw new MisoDataException($"{at}: timeInterval.start/end is not yyyy-MM-ddTHH:mm:ss");
            if (end - start != TimeSpan.FromHours(1))
                throw new MisoDataException($"{at}: interval {start:yyyy-MM-ddTHH:mm:ss}..{end:yyyy-MM-ddTHH:mm:ss} is not exactly one hour");
            if (DateOnly.FromDateTime(start) != unit.MarketDate)
                throw new MisoDataException($"{at}: interval starts on {start:yyyy-MM-dd} but {MisoTime.Format(unit.MarketDate)} was requested (date echo)");
            if (start.Minute != 0 || start.Second != 0)
                throw new MisoDataException($"{at}: interval start {start:HH:mm:ss} is not on the hour");

            // Rule 3: MTLF init echo.
            if (expectedInit is not null)
            {
                var init = Str(item, "init");
                if (!string.Equals(init, expectedInit, StringComparison.Ordinal))
                    throw new MisoDataException($"{at}: init is '{Short(init)}' but init={expectedInit} was requested (init echo)");
            }

            // Rule 4: known member, case-insensitive.
            var member = Str(item, feed.MemberField)?.Trim();
            if (string.IsNullOrEmpty(member))
                throw new MisoDataException($"{at}: {feed.MemberField} is missing");
            var key = member.ToUpperInvariant();
            if (!feed.MemberSet.Contains(key))
                throw new MisoDataException($"{at}: unknown {feed.MemberField} '{Short(member)}'; known: {string.Join(", ", feed.Members)}");

            // Rule 6: no duplicate (interval, member).
            if (!seen.Add((start, key)))
                throw new MisoDataException($"{at}: duplicate ({start:yyyy-MM-ddTHH:mm:ss}, {member})");

            records.Add(new MisoRecord(start, end, member, key, item));
        }

        // Rule 7: complete — 24 distinct intervals, each with every member.
        var byInterval = records.GroupBy(r => r.Start).ToList();
        if (byInterval.Count != 24)
            throw new MisoDataException($"{where}: {byInterval.Count} distinct hourly interval(s), expected 24 (incomplete day)");
        foreach (var g in byInterval)
        {
            if (g.Count() != feed.Members.Count)
            {
                var missing = feed.MemberSet.Except(g.Select(r => r.MemberKey)).OrderBy(x => x, StringComparer.Ordinal);
                throw new MisoDataException(
                    $"{where}: interval {g.Key:yyyy-MM-ddTHH:mm:ss} is missing {feed.MemberField} {string.Join(", ", missing)} (incomplete day)");
            }
        }

        return records.OrderBy(r => r.Start).ThenBy(r => r.MemberKey, StringComparer.Ordinal).ToList();
    }

    internal static string? Str(JsonElement obj, string name) =>
        MisoJson.TryGet(obj, name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>Vendor-supplied value quoted into a message: bounded.</summary>
    internal static string Short(string? s) => s is null ? "(missing)" : s.Length <= 40 ? s : s[..40] + "...";
}

/// <summary>Measure parsing shared by the mappers: JSON number → exact <see cref="decimal"/>, never via <see cref="double"/>.</summary>
internal static class MisoMeasure
{
    /// <summary>
    /// <paramref name="required"/>: an ABSENT property fails; a JSON <c>null</c> is always NULL.
    /// Not required: absent → NULL (design §4 rule 5, a missing known fuel key).
    /// </summary>
    public static decimal? Read(JsonElement obj, string name, string where, bool required)
    {
        if (!MisoJson.TryGet(obj, name, out var v))
            return required ? throw new MisoDataException($"{where}: '{name}' is missing") : null;
        return v.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.Number when v.TryGetDecimal(out var d) => d,
            _ => throw new MisoDataException($"{where}: '{name}' is not a number"),
        };
    }

    /// <summary>Decimal sum; NULL if any member is NULL (design §4).</summary>
    public static decimal? SumOrNull(IEnumerable<decimal?> values)
    {
        decimal sum = 0m;
        foreach (var v in values)
        {
            if (v is null) return null;
            sum += v.Value;
        }
        return sum;
    }
}
