using System.Reflection;
using DataLoader.Marex;
using log4net.Core;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DataLoader.Marex.Tests;

/// <summary>
/// The SDK log bridge exists to make a connection problem diagnosable. These tests are
/// about the one way it could do harm: the SDK logs its whole connection config —
/// <b>bearer token included</b> — at INFO on every <c>Connect</c>.
/// </summary>
public class MarexSdkLogBridgeTests
{
    /// <summary>Drives the appender directly; attaching to the real repositories is global state.</summary>
    private static RecordingLogger Append(string message, Level? level = null)
    {
        var log = new RecordingLogger();

        var bridge = (MarexSdkLogBridge)Activator.CreateInstance(
            typeof(MarexSdkLogBridge),
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args: new object[] { log },
            culture: null)!;

        var data = new LoggingEventData
        {
            Level = level ?? Level.Info,
            LoggerName = "SignalRConnector",
            Message = message,
            TimeStampUtc = DateTime.UtcNow
        };

        var append = typeof(MarexSdkLogBridge).GetMethod(
            "Append", BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, types: new[] { typeof(LoggingEvent) }, modifiers: null)!;

        append.Invoke(bridge, new object[] { new LoggingEvent(data) });
        return log;
    }

    [Fact]
    public void Redacts_TheConfigurationLine()
    {
        // ⚠ This is the SDK's own wording. The line it introduces contains the full JWT.
        // If a future SDK rewords it, THIS TEST fails — which is the point: better a red
        // build than a live credential quietly appearing in the platform log.
        var log = Append(
            "starting with configuration  DATA=\"{\\\"URL\\\":\\\"https://app-ca.neon.markets/api/crude\\\"," +
            "\\\"Token\\\":\\\"eyJhbGciOiJSUzI1NiJ9.payload.signature\\\"}\"");

        Assert.Empty(log.Records);
    }

    [Fact]
    public void Redacts_AnythingElseCarryingAJwt()
    {
        var log = Append("some new line the SDK invented eyJhbGciOiJSUzI1NiJ9.x.y");

        Assert.Empty(log.Records);
    }

    [Fact]
    public void Demotes_Heartbeats_ToTrace()
    {
        // Every 2 seconds for the life of the connection.
        var log = Append("Heartbeat received  DATA=\"{\\\"sequenceNumber\\\":59527}\"", Level.Debug);

        Assert.Single(log.Records);
        Assert.Equal(LogLevel.Trace, log.Records[0].Level);
    }

    [Fact]
    public void Passes_ErrorsThrough_AsErrors()
    {
        var log = Append("OnServerError : StatusCode: 500, ReasonPhrase: 'Internal Server Error'", Level.Error);

        Assert.Single(log.Records);
        Assert.Equal(LogLevel.Error, log.Records[0].Level);
        Assert.Contains("500", log.Records[0].Message);
    }

    [Fact]
    public void Demotes_SdkInfo_ToDebug()
    {
        // The SDK is chatty at INFO; the platform's own INFO lines should stay readable.
        var log = Append("Connected with connection type  DATA={\"Transport\":\"webSockets\"}", Level.Info);

        Assert.Single(log.Records);
        Assert.Equal(LogLevel.Debug, log.Records[0].Level);
    }

    [Fact]
    public void Passes_WarningsThrough_AsWarnings()
    {
        var log = Append("Account has no permissions or is expired", Level.Warn);

        Assert.Single(log.Records);
        Assert.Equal(LogLevel.Warning, log.Records[0].Level);
    }
}
