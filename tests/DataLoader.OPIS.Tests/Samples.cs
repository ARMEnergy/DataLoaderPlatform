namespace DataLoader.OPIS.Tests;

/// <summary>
/// Verbatim excerpts captured from the live OPIS drop on 2026-08-21 — byte-for-byte
/// as the feed publishes them, including the header row, the CRLF line endings and
/// the fixed-width space padding. Tests assert against the real shape, not a
/// tidied-up idealization of it.
/// </summary>
internal static class Samples
{
    /// <summary>Head of <c>20260820LP.csv</c>.</summary>
    public const string File20260820 =
        "Price,Mkt_Prod,Date,Low,High,Avg,Country,Unit,Timing,Freq\r\n" +
        "I,LOS ANGELES PRO     ,08/20/26, 85.8750, 89.8750, 87.8750,US,GAL,A,D\r\n" +
        "I,LOS ANGELES NBT     ,08/20/26,132.0000,132.2500,132.1250,US,GAL,A,D\r\n" +
        "I,LOS ANGELES BT MIX  ,08/20/26,132.0000,132.2500,132.1250,US,GAL,A,D\r\n" +
        "I,LOS ANGELES ISO     ,08/20/26,191.8750,192.1250,192.0000,US,GAL,A,D\r\n" +
        "I,S.F. BAY AREA PRO   ,08/20/26, 89.7500, 90.0000, 89.8750,US,GAL,A,D\r\n";

    /// <summary>
    /// A basket row: OPIS publishes only Avg and leaves Low/High blank (all spaces).
    /// From <c>20260720LP.csv</c>.
    /// </summary>
    public const string BasketRowLine =
        "I,MT BEL NT BSKT      ,07/20/26,        ,        , 73.0588,US,GAL,A,D";

    /// <summary>
    /// The revision pair that makes arm.LPReportHistory a history: the same
    /// (Mkt_Prod, Date, Timing) published first as Price='I' in 20260730LP.csv and
    /// then as Price='U' — with DIFFERENT High/Avg — in 20260803LP.csv onward.
    /// </summary>
    public const string InitialSarniaLine =
        "I,SARNIA PRO          ,07/30/26, 81.2500, 81.5000, 81.3750,US,GAL,O,D";

    public const string UpdatedSarniaLine =
        "U,SARNIA PRO          ,07/30/26, 81.2500, 82.0000, 81.6250,US,GAL,O,D";
}
