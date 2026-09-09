namespace DataLoader.CME.Tests;

/// <summary>
/// Bulletin fixtures, taken VERBATIM from live 2026-09-04 files on
/// sftp.cmeprod.datahex.rozettatech.com.
///
/// <para>
/// ⚠ These are fixed-width records: every run of spaces is load-bearing, and
/// re-indenting or trimming a line silently changes which column a value lands
/// in. They are stored as one string per line, joined with "\n", so a stray edit
/// is at least visible as a changed literal rather than as invisible whitespace
/// inside a block literal. Each was extracted with `sed` from the real files
/// rather than retyped, precisely so the columns are exact.
/// </para>
/// <para>
/// Between them these four fixtures cover every shape the parser has to handle:
/// both header dialects (POST-CLEARING/ACTUAL VOL and PRE-CLEARING/EST.VOL),
/// futures and options, all three A/B indicator columns, blank versus "----"
/// missing values, negative strikes, eighths tick notation, CAB cabinet prices,
/// TOTAL aggregate lines, BALMO day-label rows, and the two-line product header.
/// </para>
/// </summary>
internal static class Samples
{
    /// <summary>STLNYMEX: 3-line POST-CLEARING header, the 0CJ futures section (carrying B on High and A on Low/Last), two single-strike option sections, a multi-strike CALL and PUT pair with negative strikes, and a TOTAL pair.</summary>
    public static readonly string Nymex = string.Join("\n", new[]
    {
        "        FINAL POST-CLEARING PRICES AS OF 09/04/2026 10:35 PM (CDT)",
        "MTH/                       -------  DAILY  ------                                  PT                         -------  PRIOR  DAY  -------",
        "STRIKE            OPEN         HIGH          LOW         LAST         SETT         CHGE  ACTUAL VOL           SETT         VOL         INT",
        "0CJ TEST PLATINUM FUTURE",
        "SEP26             ----         ----         ----         ----       1821.0         -7.4                     1828.4                        ",
        "OCT26             ----         ----         ----         ----       1826.0         -8.0                     1834.0                        ",
        "NOV26             ----         ----         ----         ----       1833.0         -7.7                     1840.7                        ",
        "JAN27             ----         ----         ----         ----       1846.9         -6.9                     1853.8                        ",
        "APR27             ----         ----         ----         ----       1863.4         -6.6                     1870.0                        ",
        "JUL27             ----         ----         ----         ----       1877.0         -6.8                     1883.8                        ",
        "OCT27             ----         ----         ----         ----       1888.0         -6.7                     1894.7                        ",
        "JAN28             ----         ----         ----         ----       1897.9         -6.7                     1904.6                        ",
        "APR28             ----         ----         ----         ----       1907.8         -6.7                     1914.5                        ",
        "JUL28             ----         ----         ----         ----       1917.5         -6.6                     1924.1                        ",
        "OCT28             ----         ----         ----         ----       1927.0         -6.5                     1933.5                        ",
        "JAN29           1942.5       2136.9B      1942.5       1942.5       1936.0         -6.5                     1942.5                        ",
        "APR29             ----         ----       1881.7A      1881.7A      1945.6         -6.5                     1952.1                        ",
        "JUL29             ----         ----         ----         ----       1955.1         -6.5                     1961.6                        ",
        "0PO OCT26 TEST PLATINUM OPTION CALL",
        "1540            294.00       294.00       294.00       294.00         ----         ----                       ----                        ",
        "0PO NOV26 TEST PLATINUM OPTION CALL",
        "1880             41.30        41.30        40.50        40.50         ----         ----                       ----                        ",
        "7A OCT26 Crude Oil Financial Calendar Spread Option (One Month) CALL",
        "0.00              ----         ----         ----         ----         2.92         -.35                       3.27                    7875",
        "0.25              ----         ----         ----         ----         2.67         -.35                       3.02                    4350",
        "0.30              ----         ----         ----         ----         2.62         -.35                       2.97                    1050",
        "0.50              ----         ----         ----         ----         2.42         -.36                       2.78                    6525",
        "7A OCT26 Crude Oil Financial Calendar Spread Option (One Month) PUT",
        "-1.00             ----         ----         ----         ----          .01          .00                        .01                     200",
        "-0.75             ----         ----         ----         ----          .01          .00                        .01                    6320",
        "-0.50             ----         ----         ----         ----          .01          .00                        .01                    9000",
        "TOTAL                                                                                    ACTUAL VOL                     VOLUME    OPEN INT",
        "TOTAL                                                                                                                                  201",
    });

    /// <summary>STLAGS: PRE-CLEARING header with the EST.VOL column label, the 00C corn futures section in EIGHTHS tick notation (including the tick-plus-indicator 534'2A), and a Live Cattle option section whose PriorSettle reads CAB.</summary>
    public static readonly string Ags = string.Join("\n", new[]
    {
        "         FINAL PRE-CLEARING PRICES AS OF 09/04/2026 06:02 PM (CDT)",
        "MTH/                       -------  DAILY  ------                                  PT                         -------  PRIOR  DAY  -------",
        "STRIKE            OPEN         HIGH          LOW         LAST         SETT         CHGE     EST.VOL           SETT         VOL         INT",
        "00C TEST CORN FUTURE",
        "SEP26            515'2        515'2        515'2        515'2        512'0         -3'2           5          515'2                        ",
        "OCT26            510'2        510'2        510'2        510'2         ----         ----          24           ----                        ",
        "DEC26             ----         ----         ----         ----        536'6         -4'0                      540'6                        ",
        "JAN27             ----         ----         ----        534'2A        ----         ----                       ----                        ",
        "MAR27             ----         ----         ----         ----        552'2         -3'6                      556'0                        ",
        "48 SEP26 Live Cattle Options CALL",
        "218.000           .100         .100         .025A        .100A        .000        -.100          21           .100         403         762",
        "219.000           .013         .013         .013         .013         .000        -.050          14           .050          75        1112",
        "220.000           .013         .013         .013         .013         .000        -.025           5           .025         244        1641",
        "221.000           ----         ----         ----         ----         .000         .000                        CAB          16         335",
        "222.000           ----         ----         ----         ----         .000         .000                        CAB          46         503",
    });

    /// <summary>STLCPC: a BALMO futures section whose contract month sits on the header and whose row labels are days of the month — the rows the requester chose not to load.</summary>
    public static readonly string Cpc = string.Join("\n", new[]
    {
        "        FINAL POST-CLEARING PRICES AS OF 09/04/2026 10:35 PM (CDT)",
        "MTH/                       -------  DAILY  ------                                  PT                         -------  PRIOR  DAY  -------",
        "STRIKE            OPEN         HIGH          LOW         LAST         SETT         CHGE  ACTUAL VOL           SETT         VOL         INT",
        "1D SEP26 RBOB Gasoline BALMO Futures",
        "02                ----         ----         ----         ----       3.2051       +.0718                     3.1333                      25",
        "03                ----         ----         ----         ----       3.2104       +.0755                     3.1349          65          65",
        "TOTAL                                                                                    ACTUAL VOL                     VOLUME    OPEN INT",
        "TOTAL                                                                                                                       65          90",
        "1G SEP26 NY Harbor ULSD BALMO Futures",
    });

    /// <summary>STLEQT: the KYP11 product header split across two lines by CME's embedded &lt;br /&gt; fragment, followed by its data rows.</summary>
    public static readonly string Eqt = string.Join("\n", new[]
    {
        "         FINAL PRE-CLEARING PRICES AS OF 09/04/2026 06:03 PM (CDT)",
        "MTH/                       -------  DAILY  ------                                  PT                         -------  PRIOR  DAY  -------",
        "STRIKE            OPEN         HIGH          LOW         LAST         SETT         CHGE     EST.VOL           SETT         VOL         INT",
        "KYP11 <br />",
        "E-mini S&P 500 Synthetic Future (11:00 A.M. ET )",
        "SEP26             ----         ----         ----         ----      7715.75        -1.75                    7717.50                        ",
        "DEC26             ----         ----         ----         ----      7783.00        -1.50                    7784.50                        ",
        "MAR27             ----         ----         ----         ----      7860.50         -.25                    7860.75                        ",
        "JUN27             ----         ----         ----         ----      7931.00        -1.75                    7932.75                        ",
    });

}
