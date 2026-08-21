using System.Globalization;
using System.Net;

namespace DataLoader.NGI.Tests;

/// <summary>
/// One place that wires the two NGI source readers over a fake <see cref="HttpMessageHandler"/> and
/// a fake <c>arm.FileLog</c>, so every reader test is offline and identical in setup. No network, no
/// database, no credential - the settings carry obvious dummies.
/// </summary>
internal static class ReaderHarness
{
    public const string BaseUrl = "https://api.ngidata.com";

    /// <summary>Obvious dummy credentials. The real ones must never appear in this repo.</summary>
    public const string DummyUser = "user@example.test";
    public const string DummyPassword = "dummy-password";

    public static NgiSettings Settings() => new()
    {
        BaseUrl = BaseUrl,
        ConnectionString = "unused-in-tests",
        Username = DummyUser,
        Password = DummyPassword
    };

    public static NgiBidWeekWorkUnit BidWeekUnit(DateOnly? issueDate = null)
    {
        var d = issueDate ?? Samples.FixtureIssueDate;
        return new NgiBidWeekWorkUnit
        {
            IssueDate = d,
            RequestPath = "/bidweekDatafeed.json?issue_date=" + d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            KeyValue = "ngi:bidweek:" + d.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ":run=test"
        };
    }

    public static NgiLocationsWorkUnit LocationsUnit() =>
        new() { KeyValue = "ngi:locations:run=test" };

    // ---- endpoint 1: BidWeekData ---------------------------------------------------------------

    public sealed record DatafeedResult(
        IReadOnlyList<BidWeekDataRow> Rows,
        FakeNgiFileLog FileLog,
        ListLogger Log,
        FakeHttpMessageHandler Handler);

    /// <summary>Runs the BidWeekData reader against a scripted HTTP response and captures everything.</summary>
    public static async Task<DatafeedResult> RunDatafeedAsync(
        HttpStatusCode status, string body, DateOnly? issueDate = null, INgiFileLog? fileLogOverride = null)
    {
        var handler = FakeHttpMessageHandler.Respond(status, body);
        var fileLog = new FakeNgiFileLog();
        var log = new ListLogger();
        var reader = new NgiBidWeekSourceReader(handler.NewClient(), Settings(), fileLogOverride ?? fileLog, log);

        var rows = await reader.ReadAsync(BidWeekUnit(issueDate), CancellationToken.None);
        return new DatafeedResult(rows, fileLog, log, handler);
    }

    /// <summary>Runs the BidWeekData reader over a caller-supplied client (e.g. one wearing the auth handler).</summary>
    public static async Task<DatafeedResult> RunDatafeedAsync(HttpClient client, DateOnly? issueDate = null)
    {
        var fileLog = new FakeNgiFileLog();
        var log = new ListLogger();
        var reader = new NgiBidWeekSourceReader(client, Settings(), fileLog, log);

        var rows = await reader.ReadAsync(BidWeekUnit(issueDate), CancellationToken.None);
        return new DatafeedResult(rows, fileLog, log, null!);
    }

    /// <summary>Builds a BidWeekData reader without running it (for the throw-path tests).</summary>
    public static (NgiBidWeekSourceReader Reader, FakeNgiFileLog FileLog, ListLogger Log) BidWeekReader(
        HttpStatusCode status, string body)
    {
        var handler = FakeHttpMessageHandler.Respond(status, body);
        var fileLog = new FakeNgiFileLog();
        var log = new ListLogger();
        return (new NgiBidWeekSourceReader(handler.NewClient(), Settings(), fileLog, log), fileLog, log);
    }

    public static (NgiBidWeekSourceReader Reader, ListLogger Log) BidWeekReaderWith(
        INgiFileLog fileLog, HttpStatusCode status, string body)
    {
        var handler = FakeHttpMessageHandler.Respond(status, body);
        var log = new ListLogger();
        return (new NgiBidWeekSourceReader(handler.NewClient(), Settings(), fileLog, log), log);
    }

    /// <summary>Convenience: the parsed rows for a 200 body.</summary>
    public static async Task<IReadOnlyList<BidWeekDataRow>> ReadDatafeedAsync(string body) =>
        (await RunDatafeedAsync(HttpStatusCode.OK, body)).Rows;

    // ---- endpoint 2: BidWeekLocations ---------------------------------------------------------

    public sealed record LocationsResult(
        IReadOnlyList<BidWeekLocationRow> Rows,
        FakeNgiFileLog FileLog,
        ListLogger Log,
        FakeHttpMessageHandler Handler);

    public static async Task<LocationsResult> RunLocationsAsync(
        HttpStatusCode status, string body, INgiFileLog? fileLogOverride = null)
    {
        var handler = FakeHttpMessageHandler.Respond(status, body);
        var fileLog = new FakeNgiFileLog();
        var log = new ListLogger();
        var reader = new NgiLocationsSourceReader(handler.NewClient(), Settings(), fileLogOverride ?? fileLog, log);

        var rows = await reader.ReadAsync(LocationsUnit(), CancellationToken.None);
        return new LocationsResult(rows, fileLog, log, handler);
    }

    public static (NgiLocationsSourceReader Reader, FakeNgiFileLog FileLog, ListLogger Log) LocationsReader(
        HttpStatusCode status, string body)
    {
        var handler = FakeHttpMessageHandler.Respond(status, body);
        var fileLog = new FakeNgiFileLog();
        var log = new ListLogger();
        return (new NgiLocationsSourceReader(handler.NewClient(), Settings(), fileLog, log), fileLog, log);
    }

    public static (NgiLocationsSourceReader Reader, ListLogger Log) LocationsReaderWith(
        INgiFileLog fileLog, HttpStatusCode status, string body)
    {
        var handler = FakeHttpMessageHandler.Respond(status, body);
        var log = new ListLogger();
        return (new NgiLocationsSourceReader(handler.NewClient(), Settings(), fileLog, log), log);
    }

    public static async Task<IReadOnlyList<BidWeekLocationRow>> ReadLocationsAsync(string body) =>
        (await RunLocationsAsync(HttpStatusCode.OK, body)).Rows;
}
