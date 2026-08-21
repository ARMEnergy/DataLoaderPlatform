namespace DataLoader.NGI;

/// <summary>
/// The candidate JSON property-name sets both readers bind against (design §5.1).
///
/// <para><b>Why this class exists at all.</b> Five of the eleven record field names and the single
/// locations envelope key <b>contain SPACES</b>. Default <c>System.Text.Json</c> POCO binding — with
/// or without a <c>JsonNamingPolicy</c> — will NOT match them, so there is deliberately <b>no DTO
/// class</b> for either payload: everything is navigated as <see cref="System.Text.Json.JsonElement"/>
/// through <see cref="NgiParse.Prop"/> with the candidate lists below. The live spelling is always
/// FIRST; the camel/snake variants are the tolerant fallbacks the platform convention asks for
/// (the vendor spec declares "No response body" for both data endpoints, so any NGI-side rename is
/// by definition silent).</para>
///
/// <para><b>Bind by NAME, never by position.</b> Field order was stable in the live fixture but JSON
/// object order is not a contract.</para>
/// </summary>
internal static class NgiFields
{
    // ---- endpoint 1 envelope ---------------------------------------------------------------
    public static readonly string[] Meta = { "meta", "Meta" };
    public static readonly string[] Data = { "data", "Data" };

    // meta block (3 fields; NOT persisted — used as a fallback source and a validation assertion)
    public static readonly string[] MetaIssueDate = { "issue_date", "issueDate", "IssueDate", "Issue Date" };
    public static readonly string[] MetaStartDate = { "start_date", "startDate", "StartDate", "Survey Start" };
    public static readonly string[] MetaEndDate = { "end_date", "endDate", "EndDate", "Survey End" };

    // ---- endpoint 1 record (11 fields). NOTE THE SPACES in the live names. -----------------
    public static readonly string[] PointCode = { "Point Code", "PointCode", "point_code" };
    public static readonly string[] IssueDate = { "Issue Date", "IssueDate", "issue_date" };
    public static readonly string[] SurveyStart = { "Survey Start", "SurveyStart", "survey_start" };
    public static readonly string[] SurveyEnd = { "Survey End", "SurveyEnd", "survey_end" };
    public static readonly string[] Region = { "Region", "region" };
    public static readonly string[] PricingPoint = { "Pricing Point", "PricingPoint", "pricing_point" };
    public static readonly string[] Low = { "Low", "low" };
    public static readonly string[] High = { "High", "high" };
    public static readonly string[] Average = { "Average", "average" };
    public static readonly string[] Volume = { "Volume", "volume" };
    public static readonly string[] Deals = { "Deals", "deals" };

    // ---- endpoint 2 envelope ---------------------------------------------------------------
    /// <summary>
    /// The single top-level node of <c>GET /bidweekLocations?format=json</c>. Note the <b>space</b>
    /// and the <b>capital L</b> in the live spelling.
    /// </summary>
    public static readonly string[] BidweekLocations = { "Bidweek Locations", "BidweekLocations", "bidweek_locations" };

    // ---- POST /auth response ---------------------------------------------------------------
    /// <summary>Candidate names for the bearer credential in the mint response body (design §4.2).</summary>
    public static readonly string[] AccessToken = { "access_token", "accessToken", "token", "jwt" };

    /// <summary>
    /// The <c>refresh</c> field. It is read ONLY so a shape-strict binder can never trip on it and is
    /// then <b>discarded immediately</b>: there is NO refresh endpoint anywhere in the 59-path spec,
    /// so it is unusable — and it is a credential, so it must never be stored, cached, persisted,
    /// logged or written to a fixture (design §4.2).
    /// </summary>
    public static readonly string[] RefreshToken = { "refresh", "refresh_token", "refreshToken" };
}

/// <summary>
/// The endpoint-1 <c>meta</c> block, read tolerantly (design §5.3 step 3). <b>Not persisted</b> —
/// all three values were verified identical to the corresponding record fields on all 163 records.
/// It earns its keep as (a) a fallback source when a record field is missing, and (b) a validation
/// assertion. Any member may be <c>null</c> when <c>meta</c> is absent or malformed.
/// </summary>
internal readonly record struct NgiMeta(DateOnly? IssueDate, DateOnly? StartDate, DateOnly? EndDate);

/// <summary>
/// Endpoint ids — the <c>EnabledEndpoints</c> values, the <see cref="INgiPipeline.EndpointId"/>
/// values and the <c>arm.Endpoint</c> seed names, all one set of literals so they cannot drift
/// (design §1.4, §6). <c>arm.usp_UpsertFileLog</c> rejects any other name.
/// </summary>
internal static class NgiEndpoints
{
    public const string BidWeekLocations = "BidWeekLocations";
    public const string BidWeekData = "BidWeekData";
}

/// <summary>
/// The three <c>arm.Status</c> labels <c>arm.usp_UpsertFileLog</c> accepts (design §6). Anything
/// else raises server-side, so they are constants rather than inline strings.
/// </summary>
internal static class NgiFileStatus
{
    public const string Success = "Success";

    /// <summary>
    /// <b>The expected MAJORITY outcome.</b> Bidweek is a monthly feed, so over the default 60-day
    /// window roughly 58 of 60 units legitimately return HTTP 404 → <c>NotAvailable</c>,
    /// <c>RowCount = 0</c>, and the work unit <b>SUCCEEDS</b> (design §5.4).
    /// </summary>
    public const string NotAvailable = "NotAvailable";

    public const string Failed = "Failed";
}
