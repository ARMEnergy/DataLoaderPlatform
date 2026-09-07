namespace DataLoader.EOX.Tests;

/// <summary>
/// Verbatim excerpts captured from the LIVE EOX drop on 2026-09-04.
///
/// <para>
/// Every header line here is byte-for-byte what the server published, including
/// the drift that makes name-based column mapping mandatory:
/// </para>
/// <list type="bullet">
///   <item><c>Number</c> instead of <c>Line</c> in the 2014 CrudeOil and NGL files;</item>
///   <item><c>Code</c> instead of <c>Data_Code</c> in the 2014 NGL file;</item>
///   <item>no <c>FP</c> column at all in the 2014 NaturalGas file.</item>
/// </list>
/// <para>
/// Tests assert against these rather than against hand-written approximations, so
/// a descriptor that drifts from the real feed fails here. Lines use <c>\r\n</c>
/// to match the real files, which are CRLF and end with a trailing newline.
/// </para>
/// </summary>
internal static class Samples
{
    private static string Csv(params string[] lines) => string.Join("\r\n", lines) + "\r\n";

    // ---------------------------------------------------------------- CrudeOil

    /// <summary>EOD_CSV_C_20260904_1430.csv — modern header, plus a NEGATIVE-priced spread row.</summary>
    public static readonly string CrudeOil2026 = Csv(
        "Line,Code,Curve_Date,Locat._Code,Contract_Term,Contract_Name,Contract_Begin,Contract_End,Time_Key,Location,Mid,Bid,Ask",
        "1,20260904_A_MB01,2026-09-04,A,Month,Sep_2026,2026-09-01,2026-09-30,MB01,Financial_WTI,93.639,93.489,93.789",
        "2,20260904_A_M01,2026-09-04,A,Month,Oct_2026,2026-10-01,2026-10-31,M01,Financial_WTI,90.530,90.276,90.784",
        // Canada WCS trades as a discount to WTI — every price is negative.
        "605,20260904_S_MB01,2026-09-04,S,Month,Sep_2026,2026-09-01,2026-09-30,MB01,Canada_WCS,-15.804,-15.954,-15.654",
        // The widest Location in the 2026 file (63 chars) — proves VARCHAR(128) is not tight.
        "11930,20260904_EX_MB01,2026-09-04,EX,Month,Sep_2026,2026-09-01,2026-09-30,MB01,Gulf Coast_Midland WTI American Gulf Coast Diff to CMA ICE Trade,3.581,3.431,3.731");

    /// <summary>EOD_CSV_C_20140519_1430.csv — column 1 is headed <c>Number</c>, not <c>Line</c>.</summary>
    public static readonly string CrudeOil2014 = Csv(
        "Number,Code,Curve_Date,Locat._Code,Contract_Term,Contract_Name,Contract_Begin,Contract_End,Time_Key,Location,Mid,Bid,Ask",
        "1,20140519_A_M01,2014-05-19,A,Month,Jun_2014,2014-06-01,2014-06-30,M01,Midwest_WTI,102.763,102.763,102.763",
        "2,20140519_A_M02,2014-05-19,A,Month,Jul_2014,2014-07-01,2014-07-31,M02,Midwest_WTI,102.33,102.33,102.33");

    // ---------------------------------------------------------------- NaturalGas

    /// <summary>
    /// EOD_CSV_NG_20260904_1430.csv — modern header WITH <c>FP</c>, and the
    /// two-digit-year date format unique to this feed.
    /// </summary>
    public static readonly string NaturalGas2026 = Csv(
        "Line,Data_Code,Curve_Date,Region,Market,Market_Code,Contract_Name,Contract_Term,Contract_Begin,Contract_End,Time_Key,Mid,Bid,Ask,FP",
        "1,20260904_AAMB01,09/04/26,NYMEX Settlement,NYMEX,AA,Sep_2026,Month,09/01/26,09/30/26,MB01,2.6691,2.6691,2.6691,2.6691",
        "2,20260904_AAM01,09/04/26,NYMEX Settlement,NYMEX,AA,Oct_2026,Month,10/01/26,10/31/26,M01,2.9560,2.9560,2.9560,2.9560",
        "123,20260904_AAC01,09/04/26,NYMEX Settlement,NYMEX,AA,Calendar_2026,Calendar,10/01/26,12/31/26,C01,3.1800,3.1800,3.1800,3.1800");

    /// <summary>EOD_CSV_NG_20140519_1430.csv — 14 fields, NO <c>FP</c> column.</summary>
    public static readonly string NaturalGas2014 = Csv(
        "Line,Data_Code,Curve_Date,Region,Market,Market_Code,Contract_Name,Contract_Term,Contract_Begin,Contract_End,Time_Key,Mid,Bid,Ask",
        "1,20140519_AAM01,05/19/14,NYMEX Settlement,NYMEX,AA,Jun_2014,Month,06/01/14,06/30/14,M01,4.4700,4.4700,4.4700",
        "2,20140519_AAM02,05/19/14,NYMEX Settlement,NYMEX,AA,Jul_2014,Month,07/01/14,07/31/14,M02,4.4730,4.4730,4.4730");

    // ---------------------------------------------------------------- NGL

    /// <summary>EOD_CSV_NGL_20260904_1430.csv — modern header (<c>Data_Code</c>).</summary>
    public static readonly string Ngl2026 = Csv(
        "Line,Data_Code,Curve_Date,Locat._Code,Contract_Term,Contract_Name,Contract_Begin,Contract_End,Time_Key,Location,Mid,Bid,Ask",
        "1,20260904_E_MB01,2026-09-04,E,Month,Sep_2026,2026-09-01,2026-09-30,MB01,Mont Belvieu_Propane_LST,81.625,81.498,81.752",
        "2,20260904_E_M01,2026-09-04,E,Month,Oct_2026,2026-10-01,2026-10-31,M01,Mont Belvieu_Propane_LST,80.500,80.285,80.715");

    /// <summary>
    /// EOD_CSV_NGL_20140519_1430.csv — BOTH renames at once: <c>Number</c> for
    /// <c>Line</c> and <c>Code</c> for <c>Data_Code</c>.
    /// </summary>
    public static readonly string Ngl2014 = Csv(
        "Number,Code,Curve_Date,Locat._Code,Contract_Term,Contract_Name,Contract_Begin,Contract_End,Time_Key,Location,Mid,Bid,Ask",
        "1,20140519_E_M01,2014-05-19,E,Month,Jun_2014,2014-06-01,2014-06-30,M01,Mont Belvieu_Propane_LST,102.875,102.875,102.875",
        "2,20140519_E_M02,2014-05-19,E,Month,Jul_2014,2014-07-01,2014-07-31,M02,Mont Belvieu_Propane_LST,103.375,103.375,103.375");
}
