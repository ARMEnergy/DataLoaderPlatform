using System.Globalization;

namespace DataLoader.EIA;

/// <summary>
/// The six period masks EIA uses, as carried in <c>arm.SeriesFrequency.Format</c> and
/// echoed back by the API as <c>response.dateFormat</c>.
/// </summary>
internal enum EiaPeriodMask
{
    /// <summary><c>YYYY</c> — annual. Step: 1 year.</summary>
    Year,

    /// <summary><c>YYYY-"Q"Q</c> — quarterly, e.g. <c>2026-Q2</c>. Step: 3 months.</summary>
    Quarter,

    /// <summary><c>YYYY-MM</c> — monthly. Step: 1 month.</summary>
    Month,

    /// <summary><c>YYYY-MM-DD</c> — daily, weekly and four-week-average. Step: 1 day.</summary>
    Day,

    /// <summary><c>YYYY-MM-DD"T"HH24</c> — hourly, e.g. <c>2026-10-15T06</c>. Step: 1 hour.</summary>
    Hour,

    /// <summary>
    /// <c>YYYY-MM-DD"T"HH24TZH</c> — local-hourly, e.g. <c>2026-10-15T02-04</c>.
    /// Step: 1 hour. See <see cref="EiaPeriod.FormatRequestBound"/> for the trap.
    /// </summary>
    HourZone
}

/// <summary>
/// A point on a frequency's period axis, plus the arithmetic the work-unit provider
/// needs to tile a series' history into windows.
///
/// <para>
/// <b>Why this exists rather than <see cref="DateTime"/> alone.</b> Three separate
/// things have to line up and none of them is a date:
/// </para>
/// <list type="number">
///   <item><c>arm.Series.StartPeriod</c> / <c>EndPeriod</c> are written in the
///         SERIES' <c>DefaultDateFormat</c>, but a series has several frequencies. For
///         <c>crude-oil-imports</c>, StartPeriod is <c>2009-01</c> (its default is
///         monthly) and the ANNUAL frequency needs <c>2009</c>. Re-rendering across
///         masks is the whole job of <see cref="ToMask"/>.</item>
///   <item>Window boundaries must be STABLE run to run, because they appear in the
///         resume key. They are therefore derived only from (StartPeriod, mask, step
///         count) — never from today's date or from a row count.</item>
///   <item>The step is per-mask, not per-day: stepping a quarterly series by 90 days
///         would drift.</item>
/// </list>
/// </summary>
internal readonly record struct EiaPeriod
{
    private EiaPeriod(EiaPeriodMask mask, DateTime anchor)
    {
        Mask = mask;
        Anchor = anchor;
    }

    /// <summary>Which mask this period is expressed in.</summary>
    public EiaPeriodMask Mask { get; }

    /// <summary>
    /// The first instant of the period, truncated to the mask's granularity
    /// (a year is its 1 Jan 00:00, a quarter its first month, an hour its :00).
    /// </summary>
    public DateTime Anchor { get; }

    // ---------------------------------------------------------------- masks ----

    /// <summary>
    /// Map an <c>arm.SeriesFrequency.Format</c> / <c>DefaultDateFormat</c> string to a
    /// mask.
    ///
    /// <para>
    /// An unrecognised mask is REFUSED rather than guessed. Guessing would pick a step
    /// size, and a wrong step silently mis-tiles the series: windows would overlap (a
    /// waste) or leave holes (silent data loss) — and because the boundaries are baked
    /// into resume keys, the holes would be recorded as successfully loaded.
    /// </para>
    /// </summary>
    public static bool TryParseMask(string? format, out EiaPeriodMask mask)
    {
        switch ((format ?? string.Empty).Trim())
        {
            case "YYYY":                    mask = EiaPeriodMask.Year;     return true;
            case "YYYY-\"Q\"Q":             mask = EiaPeriodMask.Quarter;  return true;
            case "YYYY-MM":                 mask = EiaPeriodMask.Month;    return true;
            case "YYYY-MM-DD":              mask = EiaPeriodMask.Day;      return true;
            case "YYYY-MM-DD\"T\"HH24":     mask = EiaPeriodMask.Hour;     return true;
            case "YYYY-MM-DD\"T\"HH24TZH":  mask = EiaPeriodMask.HourZone; return true;
            default:                        mask = default;                return false;
        }
    }

    /// <summary>The canonical mask string, as it appears in the catalog.</summary>
    public static string MaskToFormat(EiaPeriodMask mask) => mask switch
    {
        EiaPeriodMask.Year     => "YYYY",
        EiaPeriodMask.Quarter  => "YYYY-\"Q\"Q",
        EiaPeriodMask.Month    => "YYYY-MM",
        EiaPeriodMask.Day      => "YYYY-MM-DD",
        EiaPeriodMask.Hour     => "YYYY-MM-DD\"T\"HH24",
        EiaPeriodMask.HourZone => "YYYY-MM-DD\"T\"HH24TZH",
        _ => throw new NotSupportedException($"Unmapped EiaPeriodMask '{mask}'.")
    };

    // --------------------------------------------------------------- parsing ----

    /// <summary>
    /// Parse a period written in <paramref name="mask"/>.
    ///
    /// <para>
    /// Tolerant in exactly one direction: a value written in a FINER mask than asked
    /// for is accepted and truncated, because the catalog does that routinely —
    /// <c>crude-oil-imports.StartPeriod</c> is <c>2009-01</c> and the annual frequency
    /// asks for a year. A value written in a COARSER mask is also accepted and
    /// extended to the start of the period (<c>2009</c> read as monthly is
    /// <c>2009-01</c>). Both are the same operation: parse whatever precision is
    /// present, then truncate to the requested mask.
    /// </para>
    /// <para>
    /// A trailing <c>±HH</c> zone, as local-hourly periods carry, is parsed and the
    /// value is normalised to UTC so that two labels for the same instant compare
    /// equal.
    /// </para>
    /// </summary>
    public static bool TryParse(string? text, EiaPeriodMask mask, out EiaPeriod period)
    {
        period = default;
        var s = (text ?? string.Empty).Trim();
        if (s.Length == 0) return false;

        // Optional trailing zone: "...T02-04" / "...T02+05".
        var offsetHours = 0;
        var tIndex = s.IndexOf('T');
        if (tIndex >= 0 && s.Length >= tIndex + 4)
        {
            var signIndex = s.IndexOfAny(new[] { '+', '-' }, tIndex);
            if (signIndex > tIndex)
            {
                var zone = s[signIndex..];
                if (zone.Length != 3 ||
                    !int.TryParse(zone.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var zh))
                    return false;
                offsetHours = zone[0] == '-' ? -zh : zh;
                s = s[..signIndex];
            }
        }

        // Quarter is the only non-numeric shape.
        if (s.Length == 7 && s[4] == '-' && (s[5] == 'Q' || s[5] == 'q'))
        {
            if (!int.TryParse(s.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var qy) ||
                !int.TryParse(s.AsSpan(6, 1), NumberStyles.None, CultureInfo.InvariantCulture, out var qq) ||
                qq is < 1 or > 4)
                return false;

            period = Truncate(new DateTime(qy, ((qq - 1) * 3) + 1, 1, 0, 0, 0, DateTimeKind.Utc), mask);
            return true;
        }

        int year, month = 1, day = 1, hour = 0;

        if (!int.TryParse(s.AsSpan(0, Math.Min(4, s.Length)), NumberStyles.None, CultureInfo.InvariantCulture, out year))
            return false;
        if (s.Length > 4)
        {
            if (s.Length < 7 || s[4] != '-' ||
                !int.TryParse(s.AsSpan(5, 2), NumberStyles.None, CultureInfo.InvariantCulture, out month) ||
                month is < 1 or > 12)
                return false;
        }
        if (s.Length > 7)
        {
            if (s.Length < 10 || s[7] != '-' ||
                !int.TryParse(s.AsSpan(8, 2), NumberStyles.None, CultureInfo.InvariantCulture, out day) ||
                day < 1 || day > DateTime.DaysInMonth(year, month))
                return false;
        }
        if (s.Length > 10)
        {
            if (s.Length != 13 || s[10] != 'T' ||
                !int.TryParse(s.AsSpan(11, 2), NumberStyles.None, CultureInfo.InvariantCulture, out hour) ||
                hour is < 0 or > 23)
                return false;
        }

        DateTime value;
        try
        {
            value = new DateTime(year, month, day, hour, 0, 0, DateTimeKind.Utc).AddHours(-offsetHours);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }

        period = Truncate(value, mask);
        return true;
    }

    private static EiaPeriod Truncate(DateTime value, EiaPeriodMask mask) => mask switch
    {
        EiaPeriodMask.Year =>
            new EiaPeriod(mask, new DateTime(value.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
        EiaPeriodMask.Quarter =>
            new EiaPeriod(mask, new DateTime(value.Year, (((value.Month - 1) / 3) * 3) + 1, 1, 0, 0, 0, DateTimeKind.Utc)),
        EiaPeriodMask.Month =>
            new EiaPeriod(mask, new DateTime(value.Year, value.Month, 1, 0, 0, 0, DateTimeKind.Utc)),
        EiaPeriodMask.Day =>
            new EiaPeriod(mask, value.Date),
        EiaPeriodMask.Hour or EiaPeriodMask.HourZone =>
            new EiaPeriod(mask, new DateTime(value.Year, value.Month, value.Day, value.Hour, 0, 0, DateTimeKind.Utc)),
        _ => throw new NotSupportedException($"Unmapped EiaPeriodMask '{mask}'.")
    };

    /// <summary>Re-express this period under a different mask (truncating or extending).</summary>
    public EiaPeriod ToMask(EiaPeriodMask mask) => Truncate(Anchor, mask);

    // -------------------------------------------------------------- rendering ----

    /// <summary>The period as the API labels it — what goes in <c>arm.SeriesData.Period</c>.</summary>
    public string Format() => Mask switch
    {
        EiaPeriodMask.Year     => Anchor.ToString("yyyy", CultureInfo.InvariantCulture),
        EiaPeriodMask.Quarter  => $"{Anchor.Year:D4}-Q{((Anchor.Month - 1) / 3) + 1}",
        EiaPeriodMask.Month    => Anchor.ToString("yyyy-MM", CultureInfo.InvariantCulture),
        EiaPeriodMask.Day      => Anchor.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        EiaPeriodMask.Hour     => Anchor.ToString("yyyy-MM-dd'T'HH", CultureInfo.InvariantCulture),
        EiaPeriodMask.HourZone => Anchor.ToString("yyyy-MM-dd'T'HH", CultureInfo.InvariantCulture) + "-00",
        _ => throw new NotSupportedException($"Unmapped EiaPeriodMask '{Mask}'.")
    };

    /// <summary>
    /// The period as a <c>start=</c> / <c>end=</c> request parameter.
    ///
    /// <para>
    /// Identical to <see cref="Format"/> for five of the six masks. For
    /// <see cref="EiaPeriodMask.HourZone"/> the <c>-00</c> suffix is <b>load-bearing</b>:
    /// </para>
    /// <list type="bullet">
    ///   <item>a local-hourly bound WITHOUT a zone (<c>2026-10-01T00</c>) answers
    ///         <b>HTTP 500 "Something unexpected happened"</b> — which looks transient,
    ///         so a retry policy would hammer it forever and the window would never
    ///         load;</item>
    ///   <item><c>Z</c> and the four-digit <c>-0400</c> form also answer 500. Only an
    ///         exact <c>±HH</c> is accepted;</item>
    ///   <item>a literal <c>+</c> in a query string decodes as a space, so <c>+00</c>
    ///         answers 500 too unless percent-encoded. <c>-00</c> means the same
    ///         instant and sidesteps the encoding hazard entirely.</item>
    /// </list>
    /// <para>
    /// The bound is interpreted as an absolute instant, so UTC bounds tile the axis
    /// contiguously even though the rows come back labelled in each respondent's own
    /// local time. All four behaviours were measured against the live API on
    /// 2026-10-16 — see <c>docs/apis/EIA.md</c> §5.
    /// </para>
    /// </summary>
    public string FormatRequestBound() => Format();

    public override string ToString() => Format();

    // ------------------------------------------------------------- arithmetic ----

    /// <summary>
    /// Move <paramref name="steps"/> periods forward (or back, if negative). One step
    /// is one unit of this period's own mask.
    /// </summary>
    public EiaPeriod AddSteps(int steps) => Mask switch
    {
        EiaPeriodMask.Year                       => new EiaPeriod(Mask, Anchor.AddYears(steps)),
        EiaPeriodMask.Quarter                    => new EiaPeriod(Mask, Anchor.AddMonths(steps * 3)),
        EiaPeriodMask.Month                      => new EiaPeriod(Mask, Anchor.AddMonths(steps)),
        EiaPeriodMask.Day                        => new EiaPeriod(Mask, Anchor.AddDays(steps)),
        EiaPeriodMask.Hour or EiaPeriodMask.HourZone => new EiaPeriod(Mask, Anchor.AddHours(steps)),
        _ => throw new NotSupportedException($"Unmapped EiaPeriodMask '{Mask}'.")
    };

    /// <summary>
    /// How many steps separate this period from <paramref name="other"/>, in this
    /// period's own mask. Zero means they are the same period; the range
    /// <c>[this, other]</c> therefore holds <c>StepsBetween(other) + 1</c> periods.
    ///
    /// <para>
    /// Computed in CALENDAR units, not elapsed time — a year is one step whether or not
    /// it is a leap year, and a month is one step whether it has 28 days or 31. Dividing
    /// a timespan would drift and eventually mis-split a range.
    /// </para>
    /// </summary>
    public int StepsBetween(EiaPeriod other) => Mask switch
    {
        EiaPeriodMask.Year    => other.Anchor.Year - Anchor.Year,
        EiaPeriodMask.Quarter => ((other.Anchor.Year * 4) + ((other.Anchor.Month - 1) / 3))
                               - ((Anchor.Year * 4) + ((Anchor.Month - 1) / 3)),
        EiaPeriodMask.Month   => ((other.Anchor.Year - Anchor.Year) * 12) + (other.Anchor.Month - Anchor.Month),
        EiaPeriodMask.Day     => (int)(other.Anchor.Date - Anchor.Date).TotalDays,
        EiaPeriodMask.Hour or EiaPeriodMask.HourZone => (int)(other.Anchor - Anchor).TotalHours,
        _ => throw new NotSupportedException($"Unmapped EiaPeriodMask '{Mask}'.")
    };

    /// <summary>
    /// Split <c>[this, <paramref name="to"/>]</c> into two contiguous halves.
    ///
    /// <para>
    /// Returns <c>null</c> when the range is a SINGLE period and so cannot be divided —
    /// that is the floor of <see cref="EiaSourceReader"/>'s subdivision, below which it
    /// must fall back to offset paging.
    /// </para>
    /// <para>
    /// The halves are guaranteed contiguous and non-empty: <c>leftEnd</c> is at least
    /// <c>this</c>, <c>rightStart</c> is exactly one step after it, and <c>rightStart</c>
    /// is never past <paramref name="to"/>. No period can fall in both halves or in
    /// neither — which matters because a gap here would be silent data loss recorded as
    /// a success.
    /// </para>
    /// </summary>
    public (EiaPeriod LeftEnd, EiaPeriod RightStart)? Split(EiaPeriod to)
    {
        var steps = StepsBetween(to);
        if (steps < 1) return null;                 // one period: indivisible

        var leftEnd = AddSteps(steps / 2);
        return (leftEnd, leftEnd.AddSteps(1));
    }

    /// <summary>True when this period starts before <paramref name="other"/>.</summary>
    public bool IsBefore(EiaPeriod other) => Anchor < other.Anchor;

    /// <summary>True when this period starts at or before <paramref name="other"/>.</summary>
    public bool IsAtOrBefore(EiaPeriod other) => Anchor <= other.Anchor;

    /// <summary>
    /// Whole days between this period's start and <paramref name="asOfUtc"/>. Used only
    /// to decide settled vs hot, never for window boundaries.
    /// </summary>
    public double DaysBefore(DateTime asOfUtc) => (asOfUtc - Anchor).TotalDays;
}
