using DataLoader.Marex;
using Neon.API.Interfaces;
using Xunit;

namespace DataLoader.Marex.Tests;

/// <summary>
/// The session is where this loader differs from every other one in the repo, so these
/// tests pin the four behaviours that would otherwise fail silently or hang.
/// </summary>
public class MarexSnapshotSessionTests
{
    private static MarexSettings Settings(int timeoutSeconds = 5) => new()
    {
        ApiEndpoint = "https://app-ca.neon.markets/api/crude",
        AuthDomain = "login.neon.markets",
        ClientId = "client",
        Username = "user",
        Password = "secret",
        SnapshotTimeoutSeconds = timeoutSeconds
    };

    private static (MarexSnapshotSession Session, FakeNeonApiClient Client, RecordingLogger Log) Build(
        MarexSettings? settings = null)
    {
        var client = new FakeNeonApiClient();
        var log = new RecordingLogger();
        var session = new MarexSnapshotSession(settings ?? Settings(), new FakeNeonClientFactory(client), log);
        return (session, client, log);
    }

    [Fact]
    public async Task Handlers_AreAttachedBeforeConnect()
    {
        // ⚠ The whole loader depends on this. The gateway pushes the opening snapshot as
        // soon as the handshake completes; a handler attached after Connect races the
        // arrival and, when it loses, the run just times out with no data and no error.
        var (session, client, _) = Build();
        client.OnConnect += () => client.RaiseFullSnapshot();

        await session.GetSnapshotAsync(CancellationToken.None);

        Assert.Equal(6, client.HandlersAttachedAtConnect);
    }

    [Fact]
    public async Task Snapshot_CompletesWithoutEverReachingConnectedState()
    {
        // Verified live: all six snapshot events fire while ConnectionStatus still reads
        // Connecting. Waiting for Connected first and reading the snapshot second would
        // deadlock.
        var (session, client, _) = Build();
        client.OnConnect += () => client.RaiseFullSnapshot();

        var snapshot = await session.GetSnapshotAsync(CancellationToken.None);

        Assert.Equal(ApiConnectionState.Connecting, client.ConnectionStatus);
        Assert.Equal(new DateTime(2026, 9, 21), snapshot.ExchangeDate);
        Assert.Single(snapshot.Products);
        Assert.Single(snapshot.ClosingPrices);
        Assert.Single(snapshot.MarketStatistics);
        Assert.Single(snapshot.Periods);
        Assert.Single(snapshot.PeriodGroups);
    }

    [Fact]
    public async Task ExchangeDate_IsTruncatedToItsDatePart()
    {
        var (session, client, _) = Build();
        client.OnConnect += () =>
            client.RaiseFullSnapshot(new DateTime(2026, 9, 21, 17, 45, 0, DateTimeKind.Utc));

        var snapshot = await session.GetSnapshotAsync(CancellationToken.None);

        Assert.Equal(new DateTime(2026, 9, 21), snapshot.ExchangeDate);
        Assert.Equal(TimeSpan.Zero, snapshot.ExchangeDate.TimeOfDay);
    }

    [Fact]
    public async Task HubName_DefaultsToNull_SoTheSdkUsesGatewayCrude()
    {
        // NeonApiConfig.Name IS the SignalR hub name. Empty means "let the SDK default to
        // Gateway.Crude"; anything else would have to be a real hub or the gateway
        // answers the negotiate with HTTP 500.
        var (session, client, _) = Build();
        client.OnConnect += () => client.RaiseFullSnapshot();

        await session.GetSnapshotAsync(CancellationToken.None);

        Assert.Null(client.ConnectConfig!.Name);
    }

    [Fact]
    public async Task HubName_WhenSet_IsPassedThrough()
    {
        var settings = Settings();
        settings.HubName = "Gateway.Crude";
        var (session, client, _) = Build(settings);
        client.OnConnect += () => client.RaiseFullSnapshot();

        await session.GetSnapshotAsync(CancellationToken.None);

        Assert.Equal("Gateway.Crude", client.ConnectConfig!.Name);
    }

    [Fact]
    public async Task Token_IsRequestedWithDomainAndClientId()
    {
        // The vendor's integration note claimed PasswordConnectionData carries no Auth0
        // domain/client id and recommended hand-rolling the /oauth/token call. It does
        // carry them, and this is the flow that was verified live.
        var (session, client, _) = Build();
        client.OnConnect += () => client.RaiseFullSnapshot();

        await session.GetSnapshotAsync(CancellationToken.None);

        Assert.Equal("login.neon.markets", client.TokenRequest!.Domain);
        Assert.Equal("client", client.TokenRequest.ClientId);
        Assert.Equal("user", client.TokenRequest.UserName);
        Assert.Equal("secret", client.TokenRequest.Password);
        Assert.Equal("a.fake.token", client.ConnectConfig!.Token);
    }

    [Fact]
    public async Task FailedToken_ThrowsWithTheVendorMessage_AndNeverConnects()
    {
        var (session, client, _) = Build();
        client.TokenSucceeds = false;
        client.TokenMessage = "mfa_required";

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.GetSnapshotAsync(CancellationToken.None));

        Assert.Contains("mfa_required", ex.Message);
        Assert.Null(client.ConnectConfig);
    }

    [Fact]
    public async Task FailedToken_WithNoVendorMessage_StillSaysSomethingUseful()
    {
        var (session, client, _) = Build();
        client.TokenSucceeds = false;
        client.TokenMessage = "";

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.GetSnapshotAsync(CancellationToken.None));

        Assert.Contains("no message from the SDK", ex.Message);
    }

    [Fact]
    public async Task Unauthorized_FailsImmediately_RatherThanWaitingOutTheTimeout()
    {
        // The SDK's reconnect loop retries a doomed connection every 5 seconds forever.
        // Without this short-circuit a bad credential costs the full snapshot timeout on
        // every single run.
        var (session, client, _) = Build(Settings(timeoutSeconds: 120));
        client.OnConnect += () => client.RaiseState(ApiConnectionState.Unauthorized);

        var started = DateTime.UtcNow;
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.GetSnapshotAsync(CancellationToken.None));

        Assert.Contains("Unauthorized", ex.Message);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10),
            "Unauthorized must abort the wait, not run out the 120s snapshot timeout");
    }

    [Fact]
    public async Task IncompatibleVersion_FailsImmediately_AndNamesTheVendoredSdk()
    {
        var (session, client, _) = Build(Settings(timeoutSeconds: 120));
        client.OnConnect += () => client.RaiseState(ApiConnectionState.IncompatibleVersion);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.GetSnapshotAsync(CancellationToken.None));

        Assert.Contains("IncompatibleVersion", ex.Message);
        Assert.Contains("lib/neon", ex.Message);
    }

    [Fact]
    public async Task RefusedConnect_Throws()
    {
        var (session, client, _) = Build();
        client.ConnectSucceeds = false;
        client.ConnectMessage = "already connected";

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.GetSnapshotAsync(CancellationToken.None));

        Assert.Contains("already connected", ex.Message);
    }

    [Fact]
    public async Task PartialSnapshot_TimesOut_AndTheMessageNamesWhatIsMissing()
    {
        var (session, client, _) = Build(Settings(timeoutSeconds: 1));
        client.OnConnect += () =>
        {
            client.RaiseExchangeDate(new DateTime(2026, 9, 21, 0, 0, 0, DateTimeKind.Utc));
            client.RaiseProducts(Dto.Product());
            // ...and nothing else.
        };

        var ex = await Assert.ThrowsAsync<TimeoutException>(
            () => session.GetSnapshotAsync(CancellationToken.None));

        Assert.Contains("ExchangeDate", ex.Message);
        Assert.Contains("Product", ex.Message);
        Assert.Contains("still waiting on", ex.Message);
        Assert.Contains("ClosingPrice", ex.Message);
        // The most likely cause is named, because it is invisible otherwise.
        Assert.Contains("hub name", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EmptyVendorCollection_IsAnEmptySet_NotAMissingPart()
    {
        // A legitimately empty feed must still complete the snapshot, so the pipeline can
        // record a zero-record load rather than the run timing out.
        var (session, client, _) = Build();
        client.OnConnect += () =>
        {
            client.RaiseExchangeDate(new DateTime(2026, 9, 21, 0, 0, 0, DateTimeKind.Utc));
            client.RaisePeriodGroups();
            client.RaisePeriods();
            client.RaiseProducts();
            client.RaiseClosingPrices();
            client.RaiseMarketStatistics();
        };

        var snapshot = await session.GetSnapshotAsync(CancellationToken.None);

        Assert.Empty(snapshot.ClosingPrices);
        Assert.Empty(snapshot.Products);
    }

    [Fact]
    public async Task RepeatedSnapshot_AfterAReconnect_IsIgnored()
    {
        // The gateway re-sends the whole set on reconnect. The first complete set must
        // win, rather than a later one half-overwriting a snapshot a pipeline is reading.
        var (session, client, log) = Build();
        client.OnConnect += () =>
        {
            client.RaiseFullSnapshot();
            client.RaiseProducts(Dto.Product(id: 999), Dto.Product(id: 1000));
        };

        var snapshot = await session.GetSnapshotAsync(CancellationToken.None);

        Assert.Single(snapshot.Products);
        Assert.Equal(4601L, snapshot.Products[0].Id);
        Assert.Contains(log.Messages, m => m.Contains("ignoring a repeated Product"));
    }

    [Fact]
    public async Task ConnectsOnlyOnce_NoMatterHowManyCallersAsk()
    {
        // Five pipelines share one session. Connecting per feed would mean five Auth0
        // grants and five snapshot transfers, and — worse — five chances for the feeds to
        // disagree about the exchange date that keys two of the tables.
        var (session, client, _) = Build();
        client.OnConnect += () => client.RaiseFullSnapshot();

        var results = await Task.WhenAll(
            Enumerable.Range(0, 5).Select(_ => session.GetSnapshotAsync(CancellationToken.None)));

        Assert.All(results, r => Assert.Same(results[0], r));
    }

    [Fact]
    public async Task Dispose_DisconnectsThenDisposesTheClient()
    {
        var (session, client, _) = Build();
        client.OnConnect += () => client.RaiseFullSnapshot();
        await session.GetSnapshotAsync(CancellationToken.None);

        await session.DisposeAsync();

        Assert.Equal(1, client.DisconnectCount);
        Assert.Equal(1, client.DisposeCount);
    }

    [Fact]
    public async Task Dispose_WithoutEverConnecting_IsHarmless()
    {
        var (session, client, _) = Build();

        await session.DisposeAsync();
        await session.DisposeAsync();

        Assert.Equal(0, client.DisconnectCount);
    }

    [Fact]
    public async Task Cancellation_WhileWaiting_Propagates()
    {
        var (session, client, _) = Build(Settings(timeoutSeconds: 120));
        using var cts = new CancellationTokenSource();
        client.OnConnect += () => cts.CancelAfter(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => session.GetSnapshotAsync(cts.Token));
    }

    [Fact]
    public async Task NoLogLine_EverCarriesThePasswordOrTheToken()
    {
        var (session, client, log) = Build();
        client.TokenValue = "eyJhbGciOiJSUzI1NiJ9.super.secret";
        client.OnConnect += () => client.RaiseFullSnapshot();

        await session.GetSnapshotAsync(CancellationToken.None);

        Assert.DoesNotContain(log.Messages, m => m.Contains("secret"));
        Assert.DoesNotContain(log.Messages, m => m.Contains("eyJ"));
        // The user name and endpoint ARE logged — that is the point of the line.
        Assert.Contains(log.Messages, m => m.Contains("user"));
    }
}
