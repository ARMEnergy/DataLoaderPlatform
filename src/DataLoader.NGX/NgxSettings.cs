using DataLoader.Core.Configuration;

namespace DataLoader.NGX;

/// <summary>
/// How a HOT work unit's resume key varies between runs. All tokens are stamped in
/// <b>UTC</b>: <c>yyyyMMddHH</c> is strictly monotonic only in UTC, because 01:00 local
/// occurs twice on a fall-back night, which would make the key go backwards and let an
/// already-recorded success suppress a legitimate re-pull for an hour.
/// </summary>
public enum NgxHotKeyStrategy
{
    /// <summary>Suffix with the UTC run date <c>yyyyMMdd</c> — one re-pull per UTC day. <b>The default.</b></summary>
    RunDate,

    /// <summary>Suffix with UTC <c>yyyyMMddHH</c> — one re-pull per clock hour.</summary>
    RunHour,

    /// <summary>Suffix with the run id — <b>every</b> invocation re-pulls (diagnostics / forced re-pull).</summary>
    RunId
}

/// <summary>
/// NGX loader settings, bound from <c>Loaders:NGX</c> via
/// <c>AddLoaderSettings&lt;NgxSettings&gt;</c> (which also wires the <c>SEE_DB</c>
/// indirection for the credentials). Inherits the common bits — destination connection
/// string, retry, concurrency, per-unit timeout — and adds the API connection and the
/// two feeds' windows.
/// </summary>
public sealed class NgxSettings : LoaderSettingsBase
{
    // ------------------------------------------------------------------- the API

    /// <summary>
    /// Base URL for the ICE NGX Clearing web services, without a trailing slash. Both
    /// feeds append their own document name (<c>indexPrice.xml</c>,
    /// <c>stripTradingSummaryXml.xml</c>).
    /// </summary>
    public string BaseUrl { get; set; } = "https://ngxclearing.ice.com/ngxcs";

    /// <summary>
    /// Web-service account user name. Defaults to the <c>SEE_DB</c> sentinel, resolved
    /// from <c>core.Param(LoaderName='NGX', ParamName='Username')</c> at run time.
    /// </summary>
    public string Username { get; set; } = "SEE_DB";

    /// <summary>
    /// Web-service account password, resolved the same way. NEVER logged — the loader
    /// logs the request URL and the user name only.
    /// </summary>
    public string Password { get; set; } = "SEE_DB";

    /// <summary>
    /// Per-request timeout. Generous by default: a month of strip trades is ~29 MB of
    /// XML, and the vendor streams it slowly.
    /// </summary>
    public int HttpTimeoutSeconds { get; set; } = 300;

    /// <summary>Which feeds run. Matched case-insensitively against the descriptor ids.</summary>
    public string[] EnabledFeeds { get; set; } = { "IndexPrice", "StripTradingSummary" };

    /// <summary>How a hot unit's resume key varies between runs.</summary>
    public NgxHotKeyStrategy HotKeyStrategy { get; set; } = NgxHotKeyStrategy.RunDate;

    // ------------------------------------------------------- IndexPrice feed

    /// <summary>
    /// How many whole months BACK from the current month the index window starts. The
    /// window always begins on the FIRST of that month: with the default 3, a run on
    /// 2026-09-18 starts at 2026-06-01.
    /// </summary>
    public int IndexMonthsBack { get; set; } = 3;

    /// <summary>
    /// How many whole months FORWARD the index window ends, again on the first of that
    /// month: with the default 6, a run on 2026-09-18 ends at 2027-03-01.
    /// </summary>
    public int IndexMonthsForward { get; set; } = 6;

    /// <summary>
    /// How many months each request covers. The whole 9-month window comfortably fits
    /// one request (10 indices over 9 months returned 1,237 rows against a 20,000-row
    /// page), so the default of 12 means ONE chunk — the setting exists so a backfill
    /// can be split without touching code.
    /// </summary>
    public int IndexWindowChunkMonths { get; set; } = 12;

    /// <summary>
    /// How many <c>indexId</c> parameters go in one request.
    ///
    /// <para><b>HARD VENDOR LIMIT: 10.</b> Eleven or more returns 403 "Access Denied"
    /// for the whole request. Measured, not documented: 10 ids succeed, 11 fail, and it
    /// is the COUNT rather than the URL length (a 5-id request padded to 400 extra
    /// characters still succeeds) or the id values. Raising this past 10 fails every
    /// work unit.</para>
    /// </summary>
    public int IndexIdsPerRequest { get; set; } = 10;

    /// <summary>
    /// The <c>pageSize</c> sent on every index request.
    ///
    /// <para><b>This must be set explicitly or data is silently lost.</b> The vendor
    /// defaults to 50 and reports the truncation only in a <c>&lt;truncated&gt;</c>
    /// element most callers never read. The server caps the value at 20,000; asking for
    /// more is accepted and quietly clamped. The reader checks <c>truncated</c> and
    /// pages regardless, so this is a request-count optimisation, not the safety
    /// net.</para>
    /// </summary>
    public int IndexPageSize { get; set; } = 20000;

    /// <summary>Sent as <c>includeProjected</c>. Projected prices are ~27% of the window and settle later.</summary>
    public bool IncludeProjected { get; set; } = true;

    /// <summary>
    /// Which <c>dbo.[Index].IndexType</c> rows this feed loads.
    ///
    /// <para><b>Load-bearing.</b> <c>dbo.[Index]</c> holds 219 rows of which only the 47
    /// <c>IndexPrice</c> ones are entitled on this endpoint; the other 172 are
    /// <c>CrudeIndexPrice</c> and belong to a different endpoint. Because a request
    /// naming ONE unentitled id returns 403 for the WHOLE batch, widening this filter
    /// does not merely add empty rows — it fails the work units that happen to share a
    /// batch with a crude id.</para>
    /// </summary>
    public string IndexTypeFilter { get; set; } = "IndexPrice";

    /// <summary>
    /// Written into every index row's <c>CommodityType</c>.
    ///
    /// <para><c>indexPrice.xml</c> emits no commodity element, so this is a loader-supplied
    /// constant. <c>Natural Gas</c> is what the incumbent writes for all 2,946,561 of its
    /// rows, and what the retired <c>dbo.IndexPrice_2</c> used for every non-crude
    /// row.</para>
    /// </summary>
    public string IndexCommodityType { get; set; } = "Natural Gas";

    // ---------------------------------------------- StripTradingSummary feed

    /// <summary>
    /// How many whole months back the strip window starts, on the first of that month:
    /// with the default 1, a run on 2026-09-18 starts at 2026-08-01.
    /// </summary>
    public int StripMonthsBack { get; set; } = 1;

    /// <summary>
    /// How many whole months forward the strip window ends, on the first of that month:
    /// with the default 1, a run on 2026-09-18 ends at 2026-10-01.
    /// </summary>
    public int StripMonthsForward { get; set; } = 1;

    /// <summary>
    /// How many days each strip request covers.
    ///
    /// <para>Weekly by default. A calendar month in one request is ~36,000 rows and
    /// ~29 MB of XML held in memory while it parses; a week is ~8,000 rows and ~7 MB,
    /// and a failed chunk costs a week rather than a month. This endpoint has no
    /// pagination envelope at all — no <c>truncated</c> flag, no row count — so unlike
    /// the index feed there is nothing to detect a server-side cap with, which is the
    /// real reason to keep chunks small.</para>
    /// </summary>
    public int StripChunkDays { get; set; } = 7;

    /// <summary>
    /// Sent as <c>grouping</c>. Observed to make no difference to this document — the
    /// response is per-trade either way, byte-identical to the ungrouped v2 export — but
    /// it is what the vendor's own examples send.
    /// </summary>
    public string StripGrouping { get; set; } = "Hub";

    /// <summary>
    /// A strip chunk whose newest day is older than this is SETTLED: it gets a stable
    /// resume key, loads once, and is skipped cheaply forever after. Anything newer is
    /// hot and re-pulls every run.
    ///
    /// <para>
    /// The default of <b>75</b> keeps the ENTIRE default window hot, which is what the
    /// specification asks for: the loader is meant to re-pull
    /// [first-of-last-month .. first-of-next-month] on every run, because trades are
    /// amended and cleared after the fact.
    /// </para>
    /// <para>
    /// 75 rather than a rounder number because of the worst case. The window starts on
    /// the first of LAST month, and its oldest day is at its oldest when viewed on the
    /// LAST day of THIS month — <c>(31 - 1) + 31 = 61</c> days. A horizon below that
    /// turns the start of the window cold at the end of a long month but not at the
    /// start of it, so the feed would quietly behave differently depending on today's
    /// date, which is more confusing than either extreme. (The previous default of 45
    /// had exactly that flaw.) 75 leaves margin.
    /// </para>
    /// <para>
    /// The settled zone therefore exists for BACKFILLS: raise
    /// <see cref="StripMonthsBack"/> and everything past this horizon loads once and is
    /// then skipped cheaply forever.
    /// </para>
    /// </summary>
    public int StripSettledAfterDays { get; set; } = 75;
}
