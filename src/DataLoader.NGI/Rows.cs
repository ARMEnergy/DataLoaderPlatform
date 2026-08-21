namespace DataLoader.NGI;

// =============================================================================
// Target rows — the source readers build these directly (the identity transform
// passes them straight to the sink). Each row carries its FileLogId, stamped by the
// reader AFTER the arm.FileLog upsert returns.
//
// Property order below mirrors (and documents) the TVP column contract in
// sql/NGI/002. FileLogId is FIRST in BOTH TVPs — this deliberately does NOT copy
// AGSI, whose entity TVP puts FileLogId LAST while its storage TVP puts it first;
// the cross-loader convention is "FileLogId first where present" and NGI follows it,
// so there is one rule to remember instead of two.
//
// DateCreated (table DEFAULT) and ModifiedAtUtc (stamped by the MERGE proc) NEVER
// cross a TVP. Neither merge proc takes a scalar alongside its TVP.
// =============================================================================

/// <summary>
/// <c>arm.BidWeekLocation</c> (endpoint 2 <c>GET /bidweekLocations?format=json</c>) — the name↔code
/// crosswalk. PK / merge key <c>PointCode</c> (a stated deviation from the <c>Id IDENTITY</c>
/// dimension shape: nothing references this table, so nothing needs a surrogate id — design §8.1).
///
/// <para>
/// 🛑 <b>DIRECTION IS CRITICAL. The source map runs NAME → CODE:</b>
/// <c>"Agua Dulce" : "STXAGUAD"</c> — the JSON <b>KEY</b> is <see cref="LocationName"/>, the JSON
/// <b>VALUE</b> is <see cref="PointCode"/>. Inverting the two produces no exception, no parse error
/// and no warning — just 163 rows with the name in <c>PointCode</c> and the code in
/// <c>LocationName</c>, after which every join and every reconciliation check silently misses.
/// <b>Mnemonic: codes are UPPERCASE and unspaced; the uppercase side is always the VALUE.</b>
/// </para>
///
/// <para>This endpoint returns <b>only these 2 fields</b>: no region (that lives only on the fact),
/// no state/country, <b>no latitude/longitude or any coordinate</b> (so no FLOAT coordinate columns
/// and no <c>GEOGRAPHY</c> — contrast IIR's <c>PlantPoint</c>), no pipeline/operator metadata, no
/// active flag, no first/last-published date, no sort order and no parent-aggregate linkage. Do not
/// model columns the API cannot fill.</para>
/// </summary>
public sealed class BidWeekLocationRow
{
    /// <summary>TVP column 1.</summary>
    public int FileLogId { get; set; }

    /// <summary>TVP column 2 — <b>the JSON object VALUE</b>. Merge key. Observed max len 12, ASCII.</summary>
    public required string PointCode { get; init; }

    /// <summary>TVP column 3 — <b>the JSON object KEY</b>. Observed max len 33, ASCII.</summary>
    public string? LocationName { get; init; }
}

/// <summary>
/// <c>arm.BidWeekData</c> (endpoint 1 <c>GET /bidweekDatafeed.json?issue_date=…</c>) — the Bidweek
/// price/volume survey fact, one row per pricing point per published issue (~163 rows/month).
/// PK / merge key <c>(IssueDate, PointCode)</c>: the response <c>data</c> node is a JSON object
/// <b>keyed by point code</b>, so it structurally cannot express more than one record per point per
/// issue — the key is exact and collision-free.
///
/// <para><b>All five measures are NULLable</b> — the literal string <c>"None"</c> is the vendor's null
/// sentinel and appeared in 45/163 (prices) and 47/163 (activity) records. That is <b>normal</b>, not
/// a bad load. <see cref="Average"/> is <b>deal-weighted, NOT the Low/High midpoint</b> — never
/// compute it; <see cref="SurveyStart"/>/<see cref="SurveyEnd"/> are <b>read, never computed</b> (both
/// the offset from the issue date and the window length vary month to month); <see cref="Region"/> is
/// taken from the field and is <b>not derivable from</b> <see cref="PointCode"/>.</para>
///
/// <para><see cref="SurveyStart"/>, <see cref="SurveyEnd"/>, <see cref="Region"/> and
/// <see cref="PricingPoint"/> are nullable even though they were never null in 163/163, so the
/// tolerant-parse contract is expressible end to end: <b>only an unusable KEY drops a record; every
/// other field degrades to NULL</b>. <c>arm.usp_ValidateLoad</c> asserts 0 NULLs in all four
/// (<c>UnexpectedNulls</c>) — design §8.2 †.</para>
///
/// <para><b>In-band aggregate rows are persisted as ordinary rows.</b> The feed mixes granular points
/// with regional averages (<c>*RAVG</c>), sub-aggregates (<c>SEREGAVG</c>, <c>APPREGAVG</c>,
/// <c>CALSAVG</c>) and the national average (<c>USAVG</c>), and there is <b>no flag field</b> marking
/// them — any downstream aggregation over this table will double-count unless it excludes those
/// codes.</para>
/// </summary>
public sealed class BidWeekDataRow
{
    /// <summary>TVP column 1. Provenance only — UPDATEd on match, NEVER part of the merge key.</summary>
    public int FileLogId { get; set; }

    /// <summary>TVP column 2 — merge key part 1. <c>"Issue Date"</c> = <c>meta.issue_date</c> = the requested date.</summary>
    public required DateOnly IssueDate { get; init; }

    /// <summary>TVP column 3 — merge key part 2. <c>"Point Code"</c>; equals the <c>data</c> map key in all 163 observed records.</summary>
    public required string PointCode { get; init; }

    /// <summary>TVP column 4 — <c>"Survey Start"</c>. READ IT, NEVER COMPUTE IT (offset varies 5/7/10 days).</summary>
    public DateOnly? SurveyStart { get; init; }

    /// <summary>TVP column 5 — <c>"Survey End"</c>. Window length also varies (3/3/6 days observed).</summary>
    public DateOnly? SurveyEnd { get; init; }

    /// <summary>TVP column 6 — <c>"Region"</c>. Free text, NOT an enum: persist as published, never gate the load on it.</summary>
    public string? Region { get; init; }

    /// <summary>TVP column 7 — <c>"Pricing Point"</c>. Equals the locations name for all 163 observed codes.</summary>
    public string? PricingPoint { get; init; }

    /// <summary>TVP column 8 — <c>"Low"</c>, USD/MMBtu. <c>DECIMAL</c>, signed; <c>"None"</c> → NULL.</summary>
    public decimal? Low { get; init; }

    /// <summary>TVP column 9 — <c>"High"</c>. May EQUAL <see cref="Low"/> on thin points (a zero-width range is valid).</summary>
    public decimal? High { get; init; }

    /// <summary>TVP column 10 — <c>"Average"</c>. DEAL-WEIGHTED, not the midpoint. Never compute it.</summary>
    public decimal? Average { get; init; }

    /// <summary>TVP column 11 — <c>"Volume"</c>. Unit undocumented by the vendor; persisted raw.</summary>
    public int? Volume { get; init; }

    /// <summary>TVP column 12 — <c>"Deals"</c>, the count of deals behind the price.</summary>
    public int? Deals { get; init; }
}
