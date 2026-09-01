namespace DataLoader.Argus.Tests;

/// <summary>
/// Verbatim excerpts captured from the LIVE Argus drop on 2026-08-27.
///
/// <para>
/// Every header line here is byte-for-byte what the server published — including
/// the inconsistent capitalisation (<c>TimeStampID</c> vs <c>TimestampID</c> vs
/// <c>TS Type</c>), the SCREAMING_SNAKE headers in <c>latestUnits</c> /
/// <c>latestNewsCategory</c>, and the <c>latestCategory</c> column order that does
/// NOT match its target table. Tests assert against these rather than against
/// hand-written approximations, so a descriptor that drifts from the real feed
/// fails here.
/// </para>
/// <para>
/// Lines use <c>\r\n</c> to match the real files, which are CRLF.
/// </para>
/// </summary>
internal static class Samples
{
    private static string Csv(params string[] lines) => string.Join("\r\n", lines) + "\r\n";

    // ---------------------------------------------------------------- DOCUMENTATION

    public static readonly string Category = Csv(
        "Code,DisplayName,Category",
        "PA0016168,Coffee Uberaba-Uberlandia 60kg bag BRL,->Agriculture->Coffee",
        "PA0016169,Coffee Uberaba-Uberlandia 60kg bag USD,->Agriculture->Coffee",
        // Real quoted row — the Category value is space-padded, which is why the
        // source quotes it. Trimming is load-bearing: Category is a PK component.
        "PA0019355,Ferrous scrap shredded fob Rotterdam,\"->Ferrous scrap->Shredded->Rotterdam \"");

    public static readonly string Codes = Csv(
        "Code,DisplayName,DeliveryMode,Unit,Frequency,Specification",
        "PA0000001,Alberta Par Edmonton month,fip,USD/bl,Daily,",
        "PA0000005,Amna formula,fob,USD/bl,Daily,",
        // Specification quoted as a single space -> must land as NULL, not " ".
        "PA0022338,Gasoline >=92r CRE retail same-day Ecatepec average,_,MXN/litre,Daily,\" \"");

    public static readonly string ModuleDetails = Csv(
        "Module,Code,TimeStampID,PriceTypeID,ContinuousForwardPeriod,StartDateInModule,EndDateInModule",
        "DABC,PA0045363,0,8,1,13-Apr-2022,01-Jan-2038",
        "DABC,PA0045364,0,8,1,08-Nov-2023,01-Jan-2038");

    public static readonly string Modules = Csv(
        "Module,Path,FileName,Description,Folder,Time,LocalTime,LocalTimeZone",
        @"DABC,DATA\DABC,dabc,Argus Biochemicals,\DABC,15:00:00 EST,20:00:00,GMT Standard Time",
        // The two DCRDEUS modules. UPPER(suffix) matches Module exactly for both.
        @"DHC,DATA\DCRDEUS,dhc,Argus US crude,\DCRDEUS,19:30:00 EST,18:30:00,Central Standard Time",
        @"DHCA,DATA\DCRDEUS,dhca,Argus US crude - 17:00 section (Houston time),\DCRDEUS,18:00:00 EST,17:00:00,Central Standard Time",
        // A module whose FileName is NOT lower(Module) — the recorded limitation of
        // deriving Module by uppercasing the suffix (docs/apis/Argus.md §5.2).
        @"DAMCOAL,DATA\DAMCOAL,dcm,Argus Coal Daily International,\DAMCOAL,15:00:00 EST,20:00:00,GMT Standard Time");

    public static readonly string PriceType = Csv(
        "PriceTypeID,Description",
        "1,value low",
        "2,value high");

    public static readonly string Quotes = Csv(
        "Code,ContinuousForwardPeriod,Timing,ForwardPeriodDescription,TimestampID,PriceTypeID,DifferentialBasis,DifferentialBasisTiming,StartDate,EndDate,OldCode,DecimalPlaces",
        "PA0000001,1,month,month value,2,1,WTI,month,20-Sep-1999,18-Aug-2010,GCDHC201L,2",
        // Open-ended row: EndDate blank. Same 4-column key as the row above but a
        // different StartDate — this is the duplication that rules out the short key.
        "PA0000005,0,month,month value,0,3,North Sea Dated,,01-Jun-2007,,NOCODE,2",
        // Quoted, space-padded DifferentialBasis.
        "PA0000905,0,prompt,na,2,1,\"Nymex Gasoline RFG \",month,01-Apr-1996,06-Sep-2006,GPGUNYDCL,2");

    public static readonly string Timestamp = Csv(
        "TimestampID,Description",
        "0,No time stamp",
        "1,London midday");

    public static readonly string Timing = Csv(
        "TimingId,Description,MinForwardPeriod,MaxForwardPeriod,ForwardPeriodDescription",
        "0,na,0,0,timing is not applicable",
        "8,Dec,1901,2099,year value");

    public static readonly string Units = Csv(
        "UNIT_ID,DESCRIPTION,UNIT_DETAILS",
        "1,USC,US cent",
        "122,'000 lb,\" thousand pounds\"");

    public static readonly string UnitCodeConv = Csv(
        "UnitID,BaseUnitID,ValidFrom,ValidTo,CodeID,Ratio",
        "44,9,01-Apr-1996,,5000827,6.8232",
        // The 17-decimal-place ratio that forces DECIMAL(28,17).
        "44,32,23-Jan-2008,28-Apr-2010,4985,0.00334112930170398");

    public static readonly string HolidayRegion = Csv(
        "HolidayRegionID,HolidayRegionDescription",
        "0,No Holidays",
        "1,Japan");

    public static readonly string Holiday = Csv(
        "HolidayRegionID,HolidayDate",
        "1,01-Jan-1990",
        "2,01-Jan-1990",
        // The source really does ship exact duplicates (11 of them live).
        "2,01-Jan-1990");

    public static readonly string QuoteHolidayRegion = Csv(
        "Code,ContinuousForwardPeriod,TimeStampID,PriceTypeID,HolidayRegionID1,HolidayRegionID2,HolidayRegionID3",
        "PA0000005,0,0,3,0,0,0",
        "PA0000008,1,2,1,10,,0");

    public static readonly string NewsCategory = Csv(
        "CATEGORY_TYPE,CATEGORY_ID,PARENT_ID,DESCRIPTION,ACTIVE",
        "Content stream,95003,0,Argus Americas Crude,Y",
        // The id that overflows INT — this row is why CategoryID is BIGINT.
        "News Category,10000004936,10000004756,Biopropane,N");

    public static readonly string RvpCodeReference = Csv(
        "CODE_ID,RVP_CODE_ID",
        "PA0000905,PA0014537",
        "PA0000906,PA0014651");

    // ---------------------------------------------------------------- DCRDEUS

    /// <summary>Real content of 20260827dhc.csv (first rows of each of its two date blocks).</summary>
    public static readonly string TimeSeries = Csv(
        "Code,TS Type,PT Code,Date,Value,Fwd Period,Diff Base Roll,Year,Cont Fwd,Record Status",
        "PA0045347,2,6,26-Aug-2026,6.02,0,10,2026,0,N",
        "PA0045351,2,6,26-Aug-2026,-5.14,10,10,2026,1,N",
        "PA0048286,2,8,27-Aug-2026,96.040,10,10,2026,1,N");

    /// <summary>
    /// From 7667.csv — the only place a 'C' (corrected) record status has been
    /// observed. The dated files carry only 'N'.
    /// </summary>
    public static readonly string TimeSeriesCorrected = Csv(
        "Code,TS Type,PT Code,Date,Value,Fwd Period,Diff Base Roll,Year,Cont Fwd,Record Status",
        "PA0005611,2,1,13-May-2026,102.02,6,6,2026,1,C",
        "PA0005611,2,2,13-May-2026,102.27,6,6,2026,1,C");

    /// <summary>The DCRDEUS listing as it really is — 4 of these names must be excluded.</summary>
    public static readonly string[] TimeSeriesDirectoryNames =
    {
        "20260826dhc.csv", "20260826dhca.csv", "20260827dhc.csv", "20260827dhca.csv",
        "latestdhc.csv", "latestdhca.csv", "previousdhc.csv", "previousdhca.csv", "7667.csv"
    };

    /// <summary>Maps a feed to its sample document, for the table-driven parser tests.</summary>
    public static string For(string feedId) => feedId switch
    {
        "Category" => Category,
        "Codes" => Codes,
        "ModuleDetails" => ModuleDetails,
        "Modules" => Modules,
        "PriceType" => PriceType,
        "Quotes" => Quotes,
        "Timestamp" => Timestamp,
        "Timing" => Timing,
        "Units" => Units,
        "UnitCodeConv" => UnitCodeConv,
        "HolidayRegion" => HolidayRegion,
        "Holiday" => Holiday,
        "QuoteHolidayRegion" => QuoteHolidayRegion,
        "NewsCategory" => NewsCategory,
        "RvpCodeReference" => RvpCodeReference,
        "TimeSeries" => TimeSeries,
        _ => throw new ArgumentOutOfRangeException(nameof(feedId), feedId, "No sample for this feed.")
    };
}
