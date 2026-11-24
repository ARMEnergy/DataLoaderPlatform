using System.Globalization;

namespace DataLoader.PJM;

/// <summary>What a mapper needs besides the item (design §14.2): the feed, a unit label for messages, and the jitter tolerance.</summary>
public sealed record PjmMapContext(PjmFeed Feed, string UnitLabel, int TimestampJitterToleranceSeconds);

/// <summary>A row's normalised timestamp pairs, by UTC field name.</summary>
public sealed class PjmStampSet
{
    private readonly Dictionary<string, (DateTime Utc, DateTime Ept)> _pairs;

    internal PjmStampSet(Dictionary<string, (DateTime Utc, DateTime Ept)> pairs) => _pairs = pairs;

    /// <summary>The normalised <c>(utc, ept)</c> of the rule whose UTC field is <paramref name="utcField"/>.</summary>
    public (DateTime Utc, DateTime Ept) this[string utcField] =>
        _pairs.TryGetValue(utcField, out var p)
            ? p
            : throw new InvalidOperationException($"PJM descriptor has no stamp rule for '{utcField}'");
}

/// <summary>
/// Shared timestamp normalisation for every mapper (design §4.4, §14.2, §14.6, §14.14). Runs the
/// stamp mode, the EPT == Eastern(UTC) check on EVERY pair, and the UTC end-offset checks. Every
/// violation is a <see cref="PjmDataException"/> (day-specific; resets the run-guard streak); a
/// malformed value is a <see cref="PjmContractException"/> from the parser. 🔒 Messages carry
/// timestamps and field names only.
/// </summary>
public static class PjmStamps
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Reads every <see cref="PjmFeed.Stamps"/> pair, then checks <see cref="PjmFeed.EndOffsets"/>.</summary>
    public static PjmStampSet ReadAll(PjmItem item, PjmMapContext ctx)
    {
        var pairs = new Dictionary<string, (DateTime Utc, DateTime Ept)>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in ctx.Feed.Stamps)
            pairs[rule.UtcField] = Read(item, rule, ctx);

        // §14.14 V-2: ending − beginning is exact in UTC only. EPT is naive wall clock (0 or −55 min on
        // a fall-back hour), so no offset check runs on EPT — it is already pinned to its UTC twin.
        foreach (var offset in ctx.Feed.EndOffsets)
        {
            var begin = pairs[offset.BeginUtcField].Utc;
            var end = pairs[offset.EndUtcField].Utc;
            var minutes = (end - begin).TotalMinutes;
            if (minutes != offset.Minutes)
                throw new PjmDataException(
                    $"{Prefix(ctx)}{offset.EndUtcField} {T(end)} − {offset.BeginUtcField} {T(begin)} is {minutes.ToString("0.##", Inv)} min, " +
                    $"expected {offset.Minutes}; a timestamp column has shifted or been relabelled");
        }

        return new PjmStampSet(pairs);
    }

    /// <summary>
    /// One pair under its mode, then the EPT check (design §14.6).
    ///
    /// <para><b>Seconds jitter (LIVE-VERIFIED 2026-11-23).</b> <c>inst_load</c> stamps carry 0–2 s
    /// of posting jitter while the minute is always on the 5-minute mark. Under
    /// <see cref="PjmStampMode.Slot"/>/<see cref="PjmStampMode.Minute"/>, BOTH timestamps are
    /// truncated to the minute — the slot start is what is keyed and stored, so a re-post with
    /// different seconds merges onto the same row. It fails only when the dropped seconds exceed
    /// the tolerance, or (Slot) the minute is off the interval boundary. The duplicate check runs
    /// on the truncated value, so two rows in one slot still fail.
    /// <see cref="PjmStampMode.Exact"/> keeps the seconds (7_day's real <c>22:47:12</c>).</para>
    /// </summary>
    public static (DateTime Utc, DateTime Ept) Read(PjmItem item, PjmStampRule rule, PjmMapContext ctx)
    {
        var rawUtc = PjmValueParser.Timestamp(item, rule.UtcField, allowZ: true);
        var rawEpt = PjmValueParser.Timestamp(item, rule.EptField, allowZ: false);

        DateTime utc, ept;
        if (rule.Mode == PjmStampMode.Exact)
        {
            utc = rawUtc;
            ept = rawEpt;
        }
        else
        {
            var tolerance = Math.Max(0, ctx.TimestampJitterToleranceSeconds);
            foreach (var (field, value) in new[] { (rule.UtcField, rawUtc), (rule.EptField, rawEpt) })
            {
                if (value.Second > tolerance)
                    throw new PjmDataException(
                        $"{Prefix(ctx)}{field} {T(value)} is {value.Second}s past its minute, " +
                        $"beyond the {tolerance}s jitter tolerance (TimestampJitterToleranceSeconds); the feed's timestamp convention has changed");
            }

            utc = TruncateToMinute(rawUtc);
            if (rule.Mode == PjmStampMode.Slot && rule.IntervalMinutes > 0 &&
                (utc.Hour * 60 + utc.Minute) % rule.IntervalMinutes != 0)
                throw new PjmDataException(
                    $"{Prefix(ctx)}{rule.UtcField} {T(rawUtc)} " +
                    $"is not on a {rule.IntervalMinutes}-minute boundary; the feed's granularity has changed");

            ept = TruncateToMinute(rawEpt);
        }

        // UTC/EPT consistency: proves the "utc" column really is UTC (design §4.4 item 2), on every pair.
        var expectedEpt = PjmTime.ToEastern(utc);
        if (ept != expectedEpt)
            throw new PjmDataException(
                $"{Prefix(ctx)}{rule.EptField} {T(ept)} " +
                $"is not Eastern prevailing time of {rule.UtcField} {T(utc)} " +
                $"(expected {T(expectedEpt)}); the 'utc' key column may not really be UTC");

        return (utc, ept);
    }

    private static string Prefix(PjmMapContext ctx) => $"PJM {ctx.Feed.Name} {ctx.UnitLabel}: ";

    private static string T(DateTime t) => t.ToString("yyyy-MM-ddTHH:mm:ss", Inv);

    private static DateTime TruncateToMinute(DateTime t) =>
        new(t.Ticks - t.Ticks % TimeSpan.TicksPerMinute, t.Kind);
}
