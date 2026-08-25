using System.Net;

namespace DataLoader.ModernCommodities.Tests;

/// <summary>
/// One place that wires the two ModCom CSV source readers over a fake
/// <see cref="System.Net.Http.HttpMessageHandler"/> and a fake <c>arm.FileLog</c>, so every reader
/// test is offline and identical in setup. No network, no database, no credential - the settings
/// carry obvious dummies, and the ModCom credential is a request HEADER that never rides a URL.
/// </summary>
internal static class ReaderHarness
{
    public const string BaseUrl = "https://app.modcom.inc/api/integration/";

    /// <summary>Obvious dummies. The real credential must never appear in this repo.</summary>
    public const string DummyUser = "modcom-test-user";
    public const string DummyPassword = "dummy-password";

    public static ModComSettings Settings(
        int daysBack = 30,
        int chunkDays = 0,
        ModComHotKeyStrategy hotKey = ModComHotKeyStrategy.RunHour,
        string? legalEntityName = null,
        Dictionary<string, ModComEndpointOverride>? endpoints = null) => new()
        {
            BaseUrl = BaseUrl,
            ConnectionString = "unused-in-tests",
            Username = DummyUser,
            Password = DummyPassword,
            DaysBack = daysBack,
            ChunkDays = chunkDays,
            HotKeyStrategy = hotKey,
            LegalEntityName = legalEntityName,
            Endpoints = endpoints ?? new Dictionary<string, ModComEndpointOverride>(StringComparer.OrdinalIgnoreCase)
        };

    public static readonly DateOnly WindowStart = new(2026, 7, 25);
    public static readonly DateOnly WindowEnd = new(2026, 8, 24);

    /// <summary>A work unit shaped exactly as <see cref="ModComWorkUnitProvider"/> builds one.</summary>
    public static ModComWorkUnit Unit(
        ModComEndpointDescriptor? descriptor = null,
        DateOnly? start = null,
        DateOnly? end = null,
        string? scope = null,
        string hot = "2026082413")
    {
        var d = descriptor ?? ModComDescriptors.AllTrades;
        var s = start ?? WindowStart;
        var e = end ?? WindowEnd;
        return new ModComWorkUnit
        {
            EndpointId = d.EndpointId,
            WindowStart = s,
            WindowEnd = e,
            LegalEntityName = scope,
            RequestPath = ModComWorkUnitProvider.BuildRequestPath(d, s, e, scope),
            RunToken = hot,
            KeyValue = ModComWorkUnitProvider.BuildKey(d.EndpointId, s, e, scope, hot)
        };
    }

    // ---- trades (allTrades + myTrades - ONE reader class, one row type) ------------------------

    public sealed record TradesResult(
        IReadOnlyList<TradeRow> Rows,
        FakeModComFileLog FileLog,
        ListLogger Log,
        FakeHttpMessageHandler Handler);

    /// <summary>Runs the trades reader against a scripted HTTP response and captures everything.</summary>
    public static async Task<TradesResult> RunTradesAsync(
        HttpStatusCode status,
        string body,
        ModComEndpointDescriptor? descriptor = null,
        ModComWorkUnit? unit = null,
        IModComFileLog? fileLogOverride = null,
        ModComSettings? settings = null)
    {
        var d = descriptor ?? ModComDescriptors.AllTrades;
        var handler = FakeHttpMessageHandler.Respond(status, body);
        var fileLog = new FakeModComFileLog();
        var log = new ListLogger();
        var reader = new ModComTradesSourceReader(
            handler.NewClient(), settings ?? Settings(), fileLogOverride ?? fileLog, d, log);

        var rows = await reader.ReadAsync(unit ?? Unit(d), CancellationToken.None);
        return new TradesResult(rows, fileLog, log, handler);
    }

    /// <summary>Builds a trades reader without running it (for the throw-path tests).</summary>
    public static (ModComTradesSourceReader Reader, FakeModComFileLog FileLog, ListLogger Log) TradesReader(
        HttpStatusCode status, string body, ModComEndpointDescriptor? descriptor = null, ModComSettings? settings = null)
    {
        var d = descriptor ?? ModComDescriptors.AllTrades;
        var handler = FakeHttpMessageHandler.Respond(status, body);
        var fileLog = new FakeModComFileLog();
        var log = new ListLogger();
        return (new ModComTradesSourceReader(handler.NewClient(), settings ?? Settings(), fileLog, d, log), fileLog, log);
    }

    public static (ModComTradesSourceReader Reader, ListLogger Log) TradesReaderWith(
        IModComFileLog fileLog, HttpStatusCode status, string body, ModComEndpointDescriptor? descriptor = null)
    {
        var d = descriptor ?? ModComDescriptors.AllTrades;
        var handler = FakeHttpMessageHandler.Respond(status, body);
        var log = new ListLogger();
        return (new ModComTradesSourceReader(handler.NewClient(), Settings(), fileLog, d, log), log);
    }

    /// <summary>Convenience: the parsed trade rows of a 200 body.</summary>
    public static async Task<IReadOnlyList<TradeRow>> ReadTradesAsync(string body, ModComEndpointDescriptor? descriptor = null) =>
        (await RunTradesAsync(HttpStatusCode.OK, body, descriptor)).Rows;

    /// <summary>Convenience: the single parsed trade row of a 200 body carrying exactly one record.</summary>
    public static async Task<TradeRow> ReadOneTradeAsync(string record)
    {
        var rows = await ReadTradesAsync(Samples.TradesCsv(record));
        return rows.Single();
    }

    // ---- settlements ---------------------------------------------------------------------------

    public sealed record SettlementsResult(
        IReadOnlyList<SettlementRow> Rows,
        FakeModComFileLog FileLog,
        ListLogger Log,
        FakeHttpMessageHandler Handler);

    public static async Task<SettlementsResult> RunSettlementsAsync(
        HttpStatusCode status,
        string body,
        ModComWorkUnit? unit = null,
        IModComFileLog? fileLogOverride = null,
        ModComSettings? settings = null)
    {
        var d = ModComDescriptors.Settlements;
        var handler = FakeHttpMessageHandler.Respond(status, body);
        var fileLog = new FakeModComFileLog();
        var log = new ListLogger();
        var reader = new ModComSettlementsSourceReader(
            handler.NewClient(), settings ?? Settings(), fileLogOverride ?? fileLog, d, log);

        var rows = await reader.ReadAsync(unit ?? Unit(d), CancellationToken.None);
        return new SettlementsResult(rows, fileLog, log, handler);
    }

    public static (ModComSettlementsSourceReader Reader, FakeModComFileLog FileLog, ListLogger Log) SettlementsReader(
        HttpStatusCode status, string body)
    {
        var d = ModComDescriptors.Settlements;
        var handler = FakeHttpMessageHandler.Respond(status, body);
        var fileLog = new FakeModComFileLog();
        var log = new ListLogger();
        return (new ModComSettlementsSourceReader(handler.NewClient(), Settings(), fileLog, d, log), fileLog, log);
    }

    public static async Task<IReadOnlyList<SettlementRow>> ReadSettlementsAsync(string body) =>
        (await RunSettlementsAsync(HttpStatusCode.OK, body)).Rows;

    public static async Task<SettlementRow> ReadOneSettlementAsync(string record)
    {
        var rows = await ReadSettlementsAsync(Samples.SettlementsCsv(record));
        return rows.Single();
    }
}
