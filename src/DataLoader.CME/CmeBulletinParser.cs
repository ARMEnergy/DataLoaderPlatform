using System.Globalization;
using System.Text;

namespace DataLoader.CME;

/// <summary>
/// Raised when a bulletin cannot be trusted as a whole — an empty file, a column
/// header whose geometry does not match <see cref="CmeDescriptors"/>, or (under
/// <see cref="CmeSettings.StrictLineParsing"/>) an unclassifiable line.
///
/// <para>
/// This is a HARD failure by design. Everything downstream is positional, so
/// continuing past broken geometry would mean guessing — and a wrong guess writes
/// plausible garbage rather than raising. Failing the file leaves the previous
/// data intact, records the reason in <c>arm.FileLog</c>, and lets the other
/// feeds finish.
/// </para>
/// </summary>
public sealed class CmeBulletinFormatException : Exception
{
    public CmeBulletinFormatException(string message) : base(message) { }
}

/// <summary>Counters describing what one bulletin parse did. Logged, and written to <c>arm.FileLog</c>.</summary>
public sealed class CmeParseStats
{
    public int LinesTotal { get; set; }
    public int ProductSections { get; set; }
    public int OptionRows { get; set; }
    public int FutureRows { get; set; }

    /// <summary>Section total lines (<c>TOTAL …</c>), deliberately skipped — they are aggregates, not facts.</summary>
    public int TotalLinesSkipped { get; set; }

    /// <summary>
    /// Futures rows skipped because their label is a day-of-month rather than a
    /// contract month — the BALMO / event-contract shape. Skipping these is the
    /// requester's explicit decision; see <see cref="CmeBulletinParser"/>.
    /// </summary>
    public int DayLabelRowsSkipped { get; set; }

    /// <summary>Values that read <c>CAB</c> (a cabinet price) and were stored NULL.</summary>
    public int CabinetValues { get; set; }

    /// <summary>Tick-notation values converted to decimal, e.g. <c>491'4</c> -&gt; 491.5.</summary>
    public int TickValues { get; set; }

    /// <summary>Lines that matched nothing the parser understands. Always logged.</summary>
    public int UnclassifiedLines { get; set; }

    /// <summary>Individual values that would not convert — that column NULL, the row still loaded.</summary>
    public int ValueParseFailures { get; set; }

    /// <summary>Product-header continuation lines consumed (the <c>&lt;br /&gt;</c> artifact).</summary>
    public int ContinuationLines { get; set; }

    /// <summary>Values whose tick width the feed's convention does not allow — stored NULL, counted.</summary>
    public int TickAnomalies { get; set; }

    /// <summary>Indicator letters found on a field the DDL has no indicator column for — dropped, counted.</summary>
    public int UnexpectedIndicators { get; set; }

    /// <summary>The report timestamp from line 1, e.g. <c>FINAL POST-CLEARING PRICES AS OF …</c>.</summary>
    public string? ReportHeader { get; set; }

    public int TotalRows => OptionRows + FutureRows;
}

/// <summary>
/// Parses one CME settlement bulletin (<c>STLAGS_20260904.txt</c>) into
/// <see cref="CmeFactRow"/> values.
///
/// <para><b>The file's shape</b>, verified live over 16 bulletins spanning all 8
/// feeds and 2 trade dates (757,360 data rows):</para>
/// <code>
///         FINAL POST-CLEARING PRICES AS OF 09/04/2026 10:35 PM (CDT)     &lt;- once, line 1
/// MTH/                       -------  DAILY  ------  ...                 &lt;- once, line 2
/// STRIKE            OPEN         HIGH          LOW   ...                 &lt;- once, line 3
/// 0CJ TEST PLATINUM FUTURE                                               &lt;- product header (future)
/// SEP26             ----         ----    ...    1821.0     ...           &lt;- data row, label = contract month
/// 0PO OCT26 TEST PLATINUM OPTION CALL                                    &lt;- product header (option)
/// 1540            294.00       294.00    ...                             &lt;- data row, label = strike
/// TOTAL                                    EST.VOL   VOLUME   OPEN INT   &lt;- section total (skipped)
/// TOTAL                                     175315   153289     612748   &lt;- section total (skipped)
/// </code>
///
/// <para><b>Five facts that drive every decision below.</b></para>
/// <list type="number">
///   <item>
///     <b>Fields are FIXED-WIDTH, never whitespace-delimited.</b> A missing value
///     is sometimes <c>----</c> and sometimes BLANK, so a whitespace split slides
///     later values into the wrong columns. See
///     <see cref="CmeDescriptors.MeasureFields"/>.
///   </item>
///   <item>
///     <b>The 3-line header appears exactly ONCE per file</b> — there is no
///     per-page repetition to skip, which was confirmed rather than assumed.
///   </item>
///   <item>
///     <b><c>TOTAL</c> lines are aggregates and must be skipped.</b> 6,394 of them
///     across the sample; 3,197 carry numbers that otherwise parse perfectly as a
///     data row, so they would silently load as facts with a bogus label.
///   </item>
///   <item>
///     <b><c>CAB</c> is a price.</b> It marks a cabinet (nominal-minimum) trade and
///     is the ONLY non-numeric price token in the whole sample. The DDL has no
///     column to record it, so it is stored NULL and counted.
///   </item>
///   <item>
///     <b>Options take their contract month from the HEADER, futures from the ROW.</b>
///     An option header is <c>&lt;sym&gt; &lt;MMMYY&gt; &lt;description&gt; CALL|PUT</c> and its
///     rows are strikes; a future header has no month and its rows are months.
///   </item>
/// </list>
///
/// <para>
/// <b>⚠ THE BALMO EXCEPTION — a deliberate, requester-approved data drop.</b>
/// A minority of futures sections invert fact 5: the contract month sits on the
/// HEADER and each row's label is a DAY OF MONTH (the balance-of-month start
/// day):
/// </para>
/// <code>
/// 1D AUG26 RBOB Gasoline BALMO Futures
/// 10                ----   ...   3.2375   ...      &lt;- 10th, 17th, 20th, 21st: different prices
/// 17                ----   ...   3.2784   ...
/// </code>
/// <para>
/// <c>arm.STLBASIC_Future</c>'s key is
/// (ExchangeCode, ProductCode, TradeDate, ProductSymbol, ContractYear,
/// ContractMonth) — it has NO day component, so all of a section's day rows
/// collapse onto one key and only one arbitrary day's prices would survive the
/// merge. Asked to choose between adding a day column and skipping these rows,
/// the requester chose to SKIP them. So they are dropped, counted in
/// <see cref="CmeParseStats.DayLabelRowsSkipped"/>, logged per file, and recorded
/// on the <c>arm.FileLog</c> row — never silently discarded. In the live sample
/// this is 8,827 of 56,019 futures rows per day, all in STLCPC and STLEQT.
/// </para>
/// <para>
/// The two futures shapes are MUTUALLY EXCLUSIVE — across the whole sample, zero
/// sections had both a header month and <c>MMMYY</c> rows — so a row's label
/// alone decides, and the header month is only consulted to confirm the skip is
/// the BALMO case rather than unexplained drift.
/// </para>
/// </summary>
internal sealed class CmeBulletinParser
{
    private static readonly string[] MonthAbbreviations =
        { "JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC" };

    /// <summary>The placeholder CME writes for "no value".</summary>
    private const string NoValue = "----";

    /// <summary>A cabinet price — a real settlement marker with no numeric form.</summary>
    private const string Cabinet = "CAB";

    private readonly CmeFeed _feed;
    private readonly DateOnly _tradeDate;
    private readonly bool _strict;

    public CmeBulletinParser(CmeFeed feed, DateOnly tradeDate, bool strictLineParsing)
    {
        _feed = feed;
        _tradeDate = tradeDate;
        _strict = strictLineParsing;
    }

    /// <summary>
    /// Parse a whole bulletin.
    ///
    /// <para>Failure policy, in order of severity:</para>
    /// <list type="bullet">
    ///   <item>Empty file, or a column header whose geometry disagrees with the
    ///         descriptor -&gt; throw. Contract drift must be loud.</item>
    ///   <item>A data row before any product header -&gt; counted as unclassified;
    ///         it has no product identity, so it cannot be loaded.</item>
    ///   <item>A futures row with a day label -&gt; skipped and counted (see the
    ///         BALMO note on this class).</item>
    ///   <item>An unparseable single VALUE -&gt; that column NULL, row still
    ///         loaded, counted.</item>
    /// </list>
    /// </summary>
    public (List<CmeFactRow> Rows, CmeParseStats Stats) Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new CmeBulletinFormatException(
                $"CME {_feed.FeedId} {CmeTime.Iso(_tradeDate)}: bulletin is empty.");

        var stats = new CmeParseStats();
        var rows = new List<CmeFactRow>(capacity: 64 * 1024);

        // Current product section.
        var symbol = string.Empty;
        var description = string.Empty;
        var isOption = false;
        var putCall = string.Empty;
        short headerYear = 0;
        byte headerMonth = 0;
        var haveSection = false;

        // Set when a product header's description was pushed onto the NEXT line by
        // an embedded "<br />" (a literal HTML fragment in a CME product name).
        var awaitingDescription = false;

        var ordinal = 0;

        foreach (var rawLine in EnumerateLines(text))
        {
            stats.LinesTotal++;

            var line = rawLine.TrimEnd('\r');
            if (line.Trim().Length == 0) continue;

            // ---- file furniture: exactly once each, at the top ----
            if (line.Contains("PRICES AS OF", StringComparison.Ordinal))
            {
                stats.ReportHeader ??= line.Trim();
                continue;
            }

            if (line.StartsWith("MTH/", StringComparison.Ordinal)) continue;

            if (line.TrimStart().StartsWith("STRIKE", StringComparison.Ordinal)
                && line.Contains("OPEN", StringComparison.Ordinal))
            {
                VerifyColumnHeaderGeometry(line);
                continue;
            }

            var label = Slice(line, CmeDescriptors.LabelStart, CmeDescriptors.LabelEnd);

            // ---- section totals: aggregates, never facts ----
            if (label == "TOTAL")
            {
                stats.TotalLinesSkipped++;
                continue;
            }

            if (IsDataRow(line, label))
            {
                if (!haveSection)
                {
                    // A data row with no product identity. Cannot be attributed, so it
                    // cannot be loaded.
                    stats.UnclassifiedLines++;
                    if (_strict) throw Strict(line, "data row before any product header");
                    continue;
                }

                if (awaitingDescription)
                {
                    // The continuation line never arrived — the "<br />" header was
                    // followed straight by data. Load the section with what we have
                    // rather than dropping it; the description stays empty.
                    awaitingDescription = false;
                }

                var quote = ReadQuote(line, stats);

                if (isOption)
                {
                    if (!TryParseDecimal(label, out var strike))
                    {
                        stats.UnclassifiedLines++;
                        if (_strict) throw Strict(line, $"option strike '{label}' is not numeric");
                        continue;
                    }

                    rows.Add(BuildRow(CmeRowKind.Option, symbol, description, headerYear, headerMonth,
                        putCall, strike, quote, ++ordinal));
                    stats.OptionRows++;
                }
                else if (TryParseContractMonth(label, out var rowYear, out var rowMonth))
                {
                    rows.Add(BuildRow(CmeRowKind.Future, symbol, description, rowYear, rowMonth,
                        string.Empty, 0m, quote, ++ordinal));
                    stats.FutureRows++;
                }
                else if (headerMonth > 0)
                {
                    // The BALMO / event shape: the month is on the header and this
                    // label is a day of month. Dropped by requester decision because
                    // the Future key has no day component. Counted, never silent.
                    stats.DayLabelRowsSkipped++;
                }
                else
                {
                    stats.UnclassifiedLines++;
                    if (_strict) throw Strict(line, $"futures label '{label}' is neither a contract month nor a BALMO day");
                }

                continue;
            }

            // ---- product header ----
            if (awaitingDescription)
            {
                // Same normalisation as the single-line path — see CollapseSpaces.
                // A description must not depend on WHICH path produced it.
                description = CollapseSpaces(line);
                awaitingDescription = false;
                stats.ContinuationLines++;
                continue;
            }

            var header = ParseProductHeader(line);
            symbol = header.Symbol;
            description = header.Description;
            isOption = header.IsOption;
            putCall = header.PutCall;
            headerYear = header.ContractYear;
            headerMonth = header.ContractMonth;
            haveSection = true;
            awaitingDescription = header.AwaitingDescription;
            stats.ProductSections++;

            if (header.IsOption && header.ContractMonth == 0)
            {
                // Every one of the 701,341 sampled option rows had a header month, so
                // this is drift rather than a known shape.
                stats.UnclassifiedLines++;
                if (_strict) throw Strict(line, "option header carries no MMMYY contract month");
            }
        }

        // A bulletin with no fact rows is a layout change — UNLESS the rows were
        // deliberately skipped. Those are different outcomes and must not share a
        // status: "we understood this file and chose to drop every row" is a
        // legitimate empty read, while "we recognised nothing in it" means the
        // format moved and the file must not be recorded as loaded.
        if (stats.TotalRows == 0 && stats.DayLabelRowsSkipped == 0)
            throw new CmeBulletinFormatException(
                $"CME {_feed.FeedId} {CmeTime.Iso(_tradeDate)}: bulletin yielded no fact rows " +
                $"from {stats.LinesTotal} line(s) — the layout has probably changed.");

        return (rows, stats);
    }

    private CmeBulletinFormatException Strict(string line, string why) =>
        new($"CME {_feed.FeedId} {CmeTime.Iso(_tradeDate)}: {why} (StrictLineParsing is on): [{line}]");

    // -------------------------------------------------------------------------
    // Line classification
    // -------------------------------------------------------------------------

    /// <summary>
    /// A line is a DATA ROW when its label is a single token and every measure
    /// field holds nothing but blank, <c>----</c>, <c>CAB</c>, or a number
    /// (optionally tick-notated, optionally A/B-suffixed) — with at least one
    /// non-blank.
    ///
    /// <para>
    /// This test is what separates data from product headers, and it is
    /// content-based rather than a guess about description length. A header such as
    /// <c>06 Soybean Meal Futures</c> puts letters inside the measure spans, so it
    /// fails; <c>1CE CHICAGO ETHANOL 1 MTH SYN CAL SPD</c> likewise. The label must
    /// also be a single token, which is what rejects a header whose symbol and
    /// first word both fall inside the label span.
    /// </para>
    /// </summary>
    private static bool IsDataRow(string line, string label)
    {
        if (label.Length == 0 || label.AsSpan().IndexOf(' ') >= 0) return false;

        var any = false;

        for (var i = 0; i < CmeDescriptors.MeasureFields.Length; i++)
        {
            var value = SliceField(line, i);
            if (value.Length == 0) continue;

            any = true;
            if (!LooksLikeValue(value)) return false;
        }

        return any;
    }

    /// <summary>
    /// Slice one measure field, INCLUDING its A/B indicator column.
    ///
    /// <para>
    /// ⚠ The indicator sits one character PAST the numeric right edge — <c>High</c>
    /// ends at column 35 and its <c>B</c> lives at 36 — so a slice that stopped at
    /// the numeric edge would parse the number correctly and silently drop every
    /// indicator, leaving <c>HighABIndicator</c>/<c>LowABIndicator</c>/
    /// <c>LastABIndicator</c> NULL on all 757,360 rows. That is exactly the bug
    /// this helper exists to prevent, which is why the span is derived here rather
    /// than at each call site.
    /// </para>
    /// </summary>
    private static string SliceField(string line, int index)
    {
        var (_, start, end, indicator) = CmeDescriptors.MeasureFields[index];
        return Slice(line, start, Math.Max(end, indicator));
    }

    private static bool LooksLikeValue(string value)
    {
        if (value == NoValue || value == Cabinet) return true;

        var span = value.AsSpan();

        // An A/B indicator is one trailing letter.
        if (char.IsLetter(span[^1])) span = span[..^1];
        if (span.Length == 0) return false;

        if (span[0] is '+' or '-') span = span[1..];
        if (span.Length == 0) return false;

        var digits = 0;
        var separators = 0;

        foreach (var c in span)
        {
            if (char.IsAsciiDigit(c)) { digits++; continue; }

            // '.' decimal point, '\'' tick separator. At most one, and never both.
            if (c is '.' or '\'') { separators++; continue; }

            return false;
        }

        return digits > 0 && separators <= 1;
    }

    /// <summary>
    /// Assert the bulletin's own column-header line still lands where
    /// <see cref="CmeDescriptors"/> says the fields are.
    ///
    /// <para>
    /// This is the cheap early warning for the failure mode that would otherwise
    /// be silent: CME widening a column would shift every value, and positional
    /// slicing would keep "working" while writing each number one field out of
    /// place. The header's own labels are right-aligned to the same edges as the
    /// data, so checking them costs one line per file.
    /// </para>
    /// <para>
    /// The label text itself is NOT pinned — <c>STLAGS</c> heads its volume column
    /// <c>EST.VOL</c> while <c>STLNYMEX</c> heads it <c>ACTUAL VOL</c>, and both
    /// are correct. Only the geometry is pinned.
    /// </para>
    /// </summary>
    private void VerifyColumnHeaderGeometry(string line)
    {
        // Every token after the first must end on a known value right edge. The
        // header writes multi-word labels ("ACTUAL VOL", "OPEN INT"), so only the
        // LAST word of each label lands on an edge — hence "ends on an edge" is
        // checked per token that could be a final word, and a mismatch on ALL of
        // them is what indicates real drift.
        var matched = 0;
        var total = 0;

        foreach (var (start, end) in Tokens(line))
        {
            if (start == 1) continue; // the "STRIKE" label
            total++;
            if (CmeDescriptors.ValueRightEdges.Contains(end)) matched++;
        }

        // Ten measure columns; allow the multi-word labels not to match, but demand
        // that a clear majority still line up.
        if (total == 0 || matched < 8)
            throw new CmeBulletinFormatException(
                $"CME {_feed.FeedId} {CmeTime.Iso(_tradeDate)}: the bulletin's column header no longer matches the " +
                $"expected fixed-width geometry ({matched}/{total} labels landed on a known right edge). " +
                $"CME has probably changed the layout — the parser must be re-measured before this file is trusted. " +
                $"Header: [{line}]");
    }

    private static IEnumerable<(int Start, int End)> Tokens(string line)
    {
        var i = 0;

        while (i < line.Length)
        {
            if (line[i] == ' ') { i++; continue; }

            var start = i;
            while (i < line.Length && line[i] != ' ') i++;
            yield return (start + 1, i); // 1-based inclusive
        }
    }

    // -------------------------------------------------------------------------
    // Product header
    // -------------------------------------------------------------------------

    internal readonly record struct ProductHeader(
        string Symbol,
        string Description,
        bool IsOption,
        string PutCall,
        short ContractYear,
        byte ContractMonth,
        bool AwaitingDescription);

    /// <summary>
    /// Split a product header into symbol, contract month, description and put/call.
    ///
    /// <para>Examples, all live:</para>
    /// <code>
    ///   0CJ TEST PLATINUM FUTURE
    ///     -> sym 0CJ,  desc "TEST PLATINUM FUTURE",              future
    ///   1D AUG26 RBOB Gasoline BALMO Futures
    ///     -> sym 1D,   desc "RBOB Gasoline BALMO Futures",       future, hdr 2026-08
    ///   0PO OCT26 TEST PLATINUM OPTION CALL
    ///     -> sym 0PO,  desc "TEST PLATINUM OPTION",              option, hdr 2026-10, C
    ///   7A OCT26 Crude Oil Financial Calendar Spread Option (One Month) PUT
    ///     -> sym 7A,   desc "Crude Oil ... (One Month)",         option, hdr 2026-10, P
    ///   KYP11 &lt;br /&gt;
    ///     -> sym KYP11, description arrives on the NEXT line
    /// </code>
    ///
    /// <para>
    /// <c>CALL</c>/<c>PUT</c> is stripped from the description and becomes
    /// <c>C</c>/<c>P</c>, per the requester's rule. Matching is on a trailing
    /// WHOLE WORD, not a substring: every one of the 651 sampled occurrences sits
    /// at the very end of the line and is upper-case, and a substring match would
    /// misread a description containing e.g. "OUTPUT".
    /// </para>
    /// </summary>
    internal static ProductHeader ParseProductHeader(string line)
    {
        var text = line.Trim();

        var space = text.IndexOf(' ');
        var symbol = space < 0 ? text : text[..space];
        var rest = space < 0 ? string.Empty : text[(space + 1)..].Trim();

        var isOption = false;
        var putCall = string.Empty;

        if (EndsWithWord(rest, "CALL")) { isOption = true; putCall = "C"; rest = TrimWord(rest, 4); }
        else if (EndsWithWord(rest, "PUT")) { isOption = true; putCall = "P"; rest = TrimWord(rest, 3); }

        short year = 0;
        byte month = 0;

        // A leading MMMYY token is the contract month: for an option it IS the
        // contract; for a future it marks the BALMO/event shape. Either way it is
        // removed from the description, per the requester's rule.
        if (TryParseLeadingContractMonth(rest, out var y, out var m, out var remainder))
        {
            year = y;
            month = m;
            rest = remainder;
        }

        // Strip the literal HTML fragment CME embeds in a handful of product names.
        if (rest.Contains("<br", StringComparison.OrdinalIgnoreCase))
            rest = rest.Replace("<br />", " ", StringComparison.OrdinalIgnoreCase)
                       .Replace("<br/>", " ", StringComparison.OrdinalIgnoreCase)
                       .Replace("<br>", " ", StringComparison.OrdinalIgnoreCase);

        rest = CollapseSpaces(rest);

        // Nothing left => the real description was pushed onto the next line.
        return new ProductHeader(symbol, rest, isOption, putCall, year, month, rest.Length == 0);
    }

    private static bool EndsWithWord(string text, string word)
    {
        if (!text.EndsWith(word, StringComparison.Ordinal)) return false;
        if (text.Length == word.Length) return true;
        return text[text.Length - word.Length - 1] == ' ';
    }

    private static string TrimWord(string text, int wordLength) =>
        text.Length <= wordLength ? string.Empty : text[..^wordLength].TrimEnd();

    /// <summary>Parse a leading <c>MMMYY</c> token, e.g. <c>OCT26</c> -&gt; 2026-10.</summary>
    private static bool TryParseLeadingContractMonth(
        string text, out short year, out byte month, out string remainder)
    {
        year = 0;
        month = 0;
        remainder = text;

        if (text.Length < 5) return false;
        if (text.Length > 5 && text[5] != ' ') return false;

        if (!TryParseContractMonth(text[..5], out year, out month)) return false;

        remainder = text.Length > 5 ? text[6..].TrimStart() : string.Empty;
        return true;
    }

    /// <summary>
    /// Parse a <c>MMMYY</c> contract token, e.g. <c>SEP26</c> -&gt; 2026-09.
    ///
    /// <para>
    /// The two-digit year maps to <c>2000 + yy</c> across the whole range. The live
    /// sample spans <c>JUN14</c> to <c>DEC99</c> and BOTH ends are legitimate: the
    /// 2014 contracts belong to <c>YIE 30-Year Eris SOFR Swap Futures</c>, whose
    /// listed effective dates reach back years, and <c>DEC99</c> is a far-dated
    /// test contract. A 1900s reading is impossible in a 2026 bulletin, so no
    /// pivot-year heuristic is needed or wanted.
    /// </para>
    /// </summary>
    internal static bool TryParseContractMonth(string token, out short year, out byte month)
    {
        year = 0;
        month = 0;

        if (token.Length != 5) return false;

        var index = Array.IndexOf(MonthAbbreviations, token[..3].ToUpperInvariant());
        if (index < 0) return false;

        if (!char.IsAsciiDigit(token[3]) || !char.IsAsciiDigit(token[4])) return false;

        month = (byte)(index + 1);
        year = (short)(2000 + ((token[3] - '0') * 10) + (token[4] - '0'));
        return true;
    }

    /// <summary>
    /// Trim, and squeeze every run of repeated spaces down to one.
    ///
    /// <para>
    /// CME pads product names inside a fixed-width field, so a description
    /// genuinely arrives with doubled spaces — e.g.
    /// <c>HC HEATING OIL CRK SPD  SYN FUT NYMEX</c> is stored as
    /// <c>HEATING OIL CRK SPD SYN FUT NYMEX</c>. 962 of the sampled header lines
    /// carry an internal double space.
    /// </para>
    /// <para>
    /// This is not cosmetic. <c>ProductDescription</c> is part of the OPTION
    /// primary key, so the exact spacing decides row identity: if CME ever
    /// re-pads a name, an un-normalised description would key a SECOND row for
    /// the same contract rather than updating the first. Collapsing makes the key
    /// insensitive to padding.
    /// </para>
    /// <para>
    /// EVERY path that sets a description must go through here — both the
    /// single-line header and the <c>&lt;br /&gt;</c> continuation line — or the
    /// same product would key differently depending on how CME happened to split
    /// its name across lines.
    /// </para>
    /// </summary>
    private static string CollapseSpaces(string text)
    {
        if (!text.Contains("  ", StringComparison.Ordinal)) return text.Trim();

        var builder = new StringBuilder(text.Length);
        var lastWasSpace = false;

        foreach (var c in text)
        {
            var isSpace = c == ' ';
            if (isSpace && lastWasSpace) continue;

            builder.Append(c);
            lastWasSpace = isSpace;
        }

        return builder.ToString().Trim();
    }

    // -------------------------------------------------------------------------
    // Values
    // -------------------------------------------------------------------------

    /// <summary>One row's ten measures plus the three A/B indicators.</summary>
    private readonly record struct Quote(
        decimal? Open,
        decimal? High, string? HighInd,
        decimal? Low, string? LowInd,
        decimal? Last, string? LastInd,
        decimal? Settle,
        decimal? PctChange,
        decimal? EstVol,
        decimal? PriorSettle,
        decimal? PriorVol,
        decimal? PriorInt);

    private Quote ReadQuote(string line, CmeParseStats stats)
    {
        var values = new decimal?[CmeDescriptors.MeasureFields.Length];
        var indicators = new string?[CmeDescriptors.MeasureFields.Length];

        for (var i = 0; i < CmeDescriptors.MeasureFields.Length; i++)
        {
            var (name, _, _, indicatorColumn) = CmeDescriptors.MeasureFields[i];

            var text = SliceField(line, i);
            values[i] = ParseValue(text, name, indicatorColumn != 0, stats, out var indicator);
            indicators[i] = indicator;
        }

        return new Quote(
            values[0],
            values[1], indicators[1],
            values[2], indicators[2],
            values[3], indicators[3],
            values[4],
            values[5],
            values[6],
            values[7],
            values[8],
            values[9]);
    }

    /// <summary>
    /// Convert one field's raw text to a decimal, splitting off an A/B indicator.
    ///
    /// <para>Handled forms, all live:</para>
    /// <list type="bullet">
    ///   <item>blank or <c>----</c> -&gt; NULL.</item>
    ///   <item><c>CAB</c> -&gt; NULL, counted. A cabinet price has no numeric form
    ///         and the DDL has nowhere to record the marker.</item>
    ///   <item><c>9.92B</c> -&gt; 9.92 with indicator <c>B</c>.</item>
    ///   <item><c>491'4</c> -&gt; 491.5 (eighths) or <c>'635</c> -&gt; 0.9921875
    ///         (64ths), per the feed's <see cref="CmeTickConvention"/>.</item>
    ///   <item><c>+.1600</c>, <c>-.0289</c>, <c>.03000</c> -&gt; signed decimals with
    ///         no integer part.</item>
    /// </list>
    /// </summary>
    private decimal? ParseValue(
        string text, string fieldName, bool indicatorAllowed, CmeParseStats stats, out string? indicator)
    {
        indicator = null;

        if (text.Length == 0 || text == NoValue) return null;

        if (text == Cabinet)
        {
            stats.CabinetValues++;
            return null;
        }

        var span = text.AsSpan();

        if (char.IsLetter(span[^1]))
        {
            var letter = char.ToUpperInvariant(span[^1]).ToString();
            span = span[..^1];

            if (indicatorAllowed)
            {
                indicator = letter;
            }
            else
            {
                // The DDL has no indicator column for this field. Never seen live —
                // count it rather than inventing a home for it.
                stats.UnexpectedIndicators++;
            }
        }

        if (span.Length == 0) return null;

        var tick = span.IndexOf('\'');
        return tick >= 0
            ? ParseTick(span, tick, fieldName, stats)
            : (TryParseDecimal(span, out var value) ? value : Anomaly(stats));
    }

    private static decimal? Anomaly(CmeParseStats stats)
    {
        // A single unreadable VALUE, not an unreadable line: the row still loads with
        // that column NULL. Counted separately so a column-level oddity never reads
        // as a structural problem with the file.
        stats.ValueParseFailures++;
        return null;
    }

    /// <summary>
    /// Convert tick notation to a decimal using the FEED's convention.
    ///
    /// <para>
    /// <c>491'4</c> in a grain bulletin is 491 + 4/8; <c>'635</c> in a rates
    /// bulletin is 63.5/64. The denominator is a property of the feed, never of the
    /// value, which is why <see cref="CmeTickConvention"/> is keyed on the exchange
    /// code. A width the convention does not allow is NOT converted under a guessed
    /// denominator — it becomes NULL and is counted, because a wrongly scaled price
    /// is worse than a missing one.
    /// </para>
    /// </summary>
    private decimal? ParseTick(ReadOnlySpan<char> span, int tickIndex, string fieldName, CmeParseStats stats)
    {
        var convention = _feed.Ticks;

        if (convention is null)
        {
            stats.TickAnomalies++;
            return null;
        }

        var negative = span[0] == '-';
        if (span[0] is '+' or '-') { span = span[1..]; tickIndex--; }

        var wholeText = span[..tickIndex];
        var fractionText = span[(tickIndex + 1)..];

        if (fractionText.Length == 0 || fractionText.Length > convention.MaxFractionDigits)
        {
            stats.TickAnomalies++;
            return null;
        }

        decimal whole = 0m;
        if (wholeText.Length > 0 && !TryParseDecimal(wholeText, out whole))
        {
            stats.TickAnomalies++;
            return null;
        }

        if (!TryParseDecimal(fractionText, out var numerator))
        {
            stats.TickAnomalies++;
            return null;
        }

        // A 3-digit fraction carries a TENTH of a denominator-th in its last digit:
        // '635 is 63.5 sixty-fourths, not 635 of anything. Verified by the maximum
        // numerator observed live (635 == 63.5, just under the 64 denominator).
        if (fractionText.Length == 3) numerator /= 10m;

        var magnitude = whole + (numerator / convention.Denominator);
        stats.TickValues++;

        return negative ? -magnitude : magnitude;
    }

    private static bool TryParseDecimal(ReadOnlySpan<char> span, out decimal value) =>
        decimal.TryParse(span, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CmeTime.Inv, out value);

    private static bool TryParseDecimal(string text, out decimal value) =>
        TryParseDecimal(text.AsSpan(), out value);

    // -------------------------------------------------------------------------
    // Row assembly
    // -------------------------------------------------------------------------

    private CmeFactRow BuildRow(
        CmeRowKind kind, string symbol, string description,
        short year, byte month, string putCall, decimal strike, Quote quote, int ordinal) =>
        new()
        {
            Kind = kind,
            ExchangeCode = _feed.ExchangeCode,
            ProductCode = _feed.ProductCode,
            TradeDate = _tradeDate,
            ProductSymbol = symbol,

            // ProductDescription is part of the OPTION primary key, and SQL Server
            // promotes a PK column to NOT NULL whatever the DDL said — so an empty
            // description must reach the server as '' and not as NULL.
            ProductDescription = description,

            ContractYear = year,
            ContractMonth = month,
            PutCall = putCall,
            Strike = strike,

            Open = quote.Open,
            High = quote.High,
            HighABIndicator = quote.HighInd,
            Low = quote.Low,
            LowABIndicator = quote.LowInd,
            Last = quote.Last,
            LastABIndicator = quote.LastInd,
            Settle = quote.Settle,
            PctChange = quote.PctChange,
            EstVol = quote.EstVol,
            PriorSettle = quote.PriorSettle,
            PriorVol = quote.PriorVol,
            PriorInt = quote.PriorInt,

            RowOrdinal = ordinal
        };

    // -------------------------------------------------------------------------
    // Text helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Take a 1-based inclusive character span and trim it. A line shorter than the
    /// span yields whatever exists, which is normal: CME right-pads only as far as
    /// the last populated column, so trailing fields are simply absent.
    /// </summary>
    internal static string Slice(string line, int start, int end)
    {
        if (start > line.Length) return string.Empty;

        var from = start - 1;
        var length = Math.Min(end, line.Length) - from;
        return length <= 0 ? string.Empty : line.Substring(from, length).Trim();
    }

    /// <summary>
    /// Split on LF, tolerating CRLF. The live bulletins are LF-only, but a CR is
    /// stripped by the caller so either works.
    /// </summary>
    private static IEnumerable<string> EnumerateLines(string text)
    {
        var start = 0;

        while (start <= text.Length)
        {
            var next = text.IndexOf('\n', start);

            if (next < 0)
            {
                if (start < text.Length) yield return text[start..];
                break;
            }

            yield return text[start..next];
            start = next + 1;
        }
    }
}
