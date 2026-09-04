using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DataLoader.ICE.Tests;

/// <summary>
/// Shared fixtures. <see cref="IceSourceReader.Parse"/> touches none of the
/// reader's collaborators, so parsing is exercised with no I/O at all.
/// </summary>
internal static class TestHelpers
{
    public static IceSettings Settings(Action<IceSettings>? configure = null)
    {
        var settings = new IceSettings
        {
            ConnectionString = "Server=(local);Database=ICE;Integrated Security=SSPI;",
            UserId = "test-user",
            Password = "test-password",
            DownloadDirectory = Path.Combine(Path.GetTempPath(), "ice-tests-" + Guid.NewGuid().ToString("N"))
        };

        configure?.Invoke(settings);
        return settings;
    }

    public static IceFileCache Cache(IceSettings settings) =>
        new(Options.Create(settings), NullLogger<IceFileCache>.Instance);

    /// <summary>A reader wired with non-network fakes; safe for Parse/BuildUrl tests.</summary>
    public static IceSourceReader Reader(IceFeedDescriptor feed, IceSettings? settings = null)
    {
        var resolved = settings ?? Settings();
        return new IceSourceReader(
            feed,
            new HttpClient(new UnusedHandler()),
            new FakeAuthenticator(),
            Cache(resolved),
            new FakeFileLog(),
            Options.Create(resolved),
            NullLogger.Instance);
    }

    public static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    /// <summary>Index of a descriptor column by name — tests read row values positionally.</summary>
    public static int Ordinal(this IceTableDescriptor table, string columnName)
    {
        for (var i = 0; i < table.Columns.Count; i++)
            if (table.Columns[i].Name.Equals(columnName, StringComparison.OrdinalIgnoreCase))
                return i;

        throw new ArgumentOutOfRangeException(nameof(columnName), columnName, $"Not a column of {table.TableName}.");
    }

    public static object Value(this IceRow row, IceTableDescriptor table, string columnName) =>
        row.Values[table.Ordinal(columnName)];

    private sealed class UnusedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new NotSupportedException("Parse tests must not touch the network.");
    }
}

/// <summary>Hands out a fixed token and counts refreshes.</summary>
internal sealed class FakeAuthenticator : IIceAuthenticator
{
    private int _generation;

    public int RefreshCount { get; private set; }

    public Task<string> GetTokenAsync(CancellationToken ct) => Task.FromResult($"token-{_generation}");

    public Task<string> RefreshAsync(string staleToken, CancellationToken ct)
    {
        RefreshCount++;
        _generation++;
        return Task.FromResult($"token-{_generation}");
    }
}

/// <summary>Captures FileLog writes instead of hitting SQL.</summary>
internal sealed class FakeFileLog : IIceFileLog
{
    public List<(IceFileContext File, string Status, int RowCount, int RowsDropped, string? Error)> Calls { get; } = new();

    public Task<int> UpsertAsync(
        IceFileContext file, string status, int rowCount, int rowsDropped, string? errorMessage, CancellationToken ct)
    {
        Calls.Add((file, status, rowCount, rowsDropped, errorMessage));
        return Task.FromResult(Calls.Count);
    }
}

/// <summary>
/// Real bytes captured from the live service on 2026-09-01. Using the actual
/// headers and rows — rather than invented ones — is what makes these tests
/// evidence that the mapping matches production, not just that it is
/// self-consistent.
/// </summary>
internal static class Samples
{
    // ---------------------------------------------------------------- .dat feeds

    /// <summary>icecleared_physenv_2026_08_28.dat — futures shape, VARCHAR strips.</summary>
    public const string PhysEnv =
        "TRADE DATE|HUB|PRODUCT|STRIP|CONTRACT|CONTRACT TYPE|STRIKE|SETTLEMENT PRICE|NET CHANGE|EXPIRATION DATE|PRODUCT_ID\n" +
        "8/28/2026|CCA|CCA ACP Advance Futures|Feb27|ACA|F||0.30000|0.00000|2/26/2027|22323\n" +
        "8/28/2026|CCA|CCA ACP Advance Futures|May27|ACA|F||0.30000|0.00000|5/28/2027|22323\n" +
        "8/28/2026|Carbon Offset|CCO Futures|01 Sep 26|CCO|F||15.54000|0.00000|8/27/2019|23422\n";

    /// <summary>icecleared_gas_2026_08_28.dat — futures shape, DATE strips.</summary>
    public const string IceGas =
        "TRADE DATE|HUB|PRODUCT|STRIP|CONTRACT|CONTRACT TYPE|STRIKE|SETTLEMENT PRICE|NET CHANGE|EXPIRATION DATE|PRODUCT_ID\n" +
        "8/28/2026|AB-NIT|NG Basis LD1 for NGX 7a Futures|9/1/2026|AEC|F||-1.95750|-0.01750|9/1/2026|451\n" +
        "8/28/2026|AB-NIT|NG Basis LD1 for NGX 7a Futures|10/1/2026|AEC|F||-1.96500|-0.01500|10/1/2026|451\n";

    /// <summary>
    /// The contract-code collision that forced ProductId into the arm.Futures key:
    /// 'OLD' from ngxcleared_power and 'OLD' from icecleared_oil, same trade date,
    /// same strip, entirely different products.
    /// </summary>
    public const string NgxPowerOld =
        "TRADE DATE|HUB|PRODUCT|STRIP|CONTRACT|CONTRACT TYPE|STRIKE|SETTLEMENT PRICE|NET CHANGE|EXPIRATION DATE|PRODUCT_ID\n" +
        "8/28/2026|Ontario - Essa DA|NGX Fin Extended Peak Futures|1/1/2027|OLD|F||156.60000|0.00000|12/31/2026|30948\n";

    public const string IceOilOld =
        "TRADE DATE|HUB|PRODUCT|STRIP|CONTRACT|CONTRACT TYPE|STRIKE|SETTLEMENT PRICE|NET CHANGE|EXPIRATION DATE|PRODUCT_ID\n" +
        "8/28/2026|NYH LD|Heating Oil Futures|1/1/2027|OLD|F||3.83160|0.00000|12/31/2026|26755\n";

    /// <summary>
    /// icecleared_physenvoptions — row 1 is an 'F' underlying-future row with a
    /// BLANK STRIKE (must be dropped), row 2 is a real option. Row 3 carries the
    /// multi-leg spread strip that proves Strip must stay VARCHAR here.
    /// </summary>
    public const string PhysEnvOptions =
        "TRADE DATE|HUB|PRODUCT|STRIP|CONTRACT|CONTRACT TYPE|STRIKE|SETTLEMENT PRICE|NET CHANGE|EXPIRATION DATE|PRODUCT_ID|OPTION_VOLATILITY|DELTA_FACTOR\n" +
        "8/28/2026|CCA V26|CCA Futures|Apr27|CB6|F||33.94000|0.07000|4/27/2027|27756||\n" +
        "8/28/2026|CCA V26|CCA Futures|Apr27|CB6|C|30.0000|4.10000|0.05000|4/27/2027|27756|28.50000|0.75000\n" +
        "8/28/2026|CCA V26|CCA Futures|BH26 Apr27 FH26|CB6|P|30.0000|1.10000|-0.02000|4/27/2027|27756|29.10000|-0.25000\n";

    /// <summary>icecleared_gasoptions — options shape with DATE strips (arm.Options).</summary>
    public const string GasOptions =
        "TRADE DATE|HUB|PRODUCT|STRIP|CONTRACT|CONTRACT TYPE|STRIKE|SETTLEMENT PRICE|NET CHANGE|EXPIRATION DATE|PRODUCT_ID|OPTION_VOLATILITY|DELTA_FACTOR\n" +
        "8/28/2026|AB-NIT|NG NGX 7a Futures (US/MM)|9/1/2026|FQN|C|1.7500|0.00010||9/2/2026|21725|39.33510|0.00000\n" +
        "8/28/2026|AB-NIT|NG NGX 7a Futures (US/MM)|9/1/2026|FQN|F||0.00010||9/2/2026|21725||\n";

    /// <summary>ICEFUS_FinOptions — row 2 is the blank-strike 'F' row this feed is full of.</summary>
    public const string Greeks =
        "TRADE DATE|CONTRACT|STRIP|EXPIRATION_DATE|STRIKE|PUT_CALL|SETTLEMENT_PRICE|VOLATILITY|DELTA|GAMMA|THETA|VEGA\n" +
        "8/28/2026|RS|10/1/2026|9/25/2026|635.0000|C|188.8000|34.7327|0.9970|0.0001|-4.6617|2.0592\n" +
        "8/28/2026|30C|9/1/2026|9/14/2026||F|93.0000|||||\n";

    // ---------------------------------------------------------------- .csv feeds

    /// <summary>
    /// ICE_Crude_Oil_Index — quoted strings, bare numbers, and the 12-hour AM/PM
    /// timestamps. Note INDEX_DATE_RANGE is FIRST in the file and SIXTH in the table.
    /// </summary>
    public const string CrudeIndex =
        "\"INDEX_DATE_RANGE\",\"MKTID\",\"PCC\",\"INDEX_ID\",\"INDEX_DATE\",\"INDEX_PRICE\",\"VOLUME\",\"MKT_DESC\",\"CREATION_TIME\",\"LAST_UPDATE_TIME\",\"BBL\",\"DAILY_PRICE\",\"DAILY_VOLUME\",\"DAILY_BBL\",\"NUM_OF_TRADES\"\n" +
        "\"08/26/2026-09/25/2026\",273,\"BGS\",1416,\"2026-08-28\",-3.6,0,\"ICE WCS CUS 1a - INDEX - Oct26\",\"2026-08-28 03:00:00 PM\",\"2026-08-28 03:00:00 PM\",0,0,0,0,0\n" +
        "\"08/26/2026-09/25/2026\",273,\"BGT\",1417,\"2026-08-28\",-0.8,0,\"ICE LT SW GRN 1a - INDEX - Oct26\",\"2026-08-28 03:00:00 PM\",\"2026-08-28 03:00:00 PM\",0,0,0,0,0\n";

    /// <summary>ICE_Crude_Oil_Index_Trades — DEAL_ID here overflows INT.</summary>
    public const string CrudeIndexTrades =
        "\"TRADE_DATE\",\"PCC_TRADE\",\"PCC_INDEX\",\"PRODUCT_NAME\",\"HUB_NAME\",\"STRIP\",\"INDEX_ID\",\"DEAL_ID\",\"DEAL_EXECUTION_TIME\",\"DEAL_PRICE\",\"DEAL_QUANTITY\",\"DEAL_QTY_IN_BBL\",\"MARKET_TYPE_ID\"\n" +
        "\"2026-08-04\",\"ASW\",\"BGW\",\"C5\",\"Edmonton - Fort Saskatchewan\",\"Sep26\",1383,82817876,\"2026-08-04 12:12:39 PM\",0.25,5000,1048.8117,273\n" +
        "\"2026-08-04\",\"AVN\",\"BFF\",\"SW\",\"Edmonton - Peace\",\"Sep26\",1372,297636150009,\"2026-08-04 12:24:40 PM\",2.25,5000,1048.8117,273\n";

    /// <summary>A real file that legitimately contains only its header row — Success with 0 rows.</summary>
    public const string CrudeIndexTradesEmpty =
        "\"TRADE_DATE\",\"PCC_TRADE\",\"PCC_INDEX\",\"PRODUCT_NAME\",\"HUB_NAME\",\"STRIP\",\"INDEX_ID\",\"DEAL_ID\",\"DEAL_EXECUTION_TIME\",\"DEAL_PRICE\",\"DEAL_QUANTITY\",\"DEAL_QTY_IN_BBL\",\"MARKET_TYPE_ID\"\n";

    // ---------------------------------------------------------------- sentinels

    /// <summary>
    /// The HTML ICE serves — with HTTP 200 — when no file exists for that date.
    /// Trimmed from the real 986-byte body; both markers are preserved verbatim.
    /// </summary>
    public const string NoFilesHtml =
        "<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n" +
        "    <title >Index of /Settlement_Reports_CSV/Gas/icecleared_gas_2026_08_30.dat</title>\n" +
        "</head>\n<body>\n" +
        "    <h1 >Index of /Settlement_Reports_CSV/Gas/icecleared_gas_2026_08_30.dat</h1>\n" +
        "    <table><tbody><tr><td colspan=\"2\"> No Files Available</td></tr></tbody></table>\n" +
        "</body>\n</html>\n";

    /// <summary>The SSO login page ICE serves — with HTTP 200 — when the token is stale.</summary>
    public const string SsoLoginHtml =
        "<!DOCTYPE html>\n<html lang=\"en\" class=\"no-js\">\n<head>\n" +
        "  <meta charset=\"utf-8\">\n" +
        "  <title>ICE SSO Client</title>\n" +
        "</head>\n<body>\n  <form id=\"loginForm\" action=\"/api/authenticate\">\n  </form>\n</body>\n</html>\n";
}
