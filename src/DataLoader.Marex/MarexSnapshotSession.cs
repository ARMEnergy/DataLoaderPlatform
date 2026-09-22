using System.Diagnostics;
using System.Reactive.Concurrency;
using Microsoft.Extensions.Logging;
using Neon.API.Implementation;
using Neon.API.Interfaces;
using SignalRClient.Contracts;

namespace DataLoader.Marex;

/// <summary>
/// One complete opening snapshot from the Neon gateway: the trading date plus the five
/// entity sets this loader persists.
/// </summary>
internal sealed class MarexSnapshot
{
    /// <summary>
    /// The gateway's own trading date, from <c>ExchangeDateSnapshot</c>. Leads the
    /// primary key of both <c>arm.ClosingPrice</c> and <c>arm.MarketStatistic</c>.
    /// </summary>
    public required DateTime ExchangeDate { get; init; }

    public required IReadOnlyList<PeriodGroupDto> PeriodGroups { get; init; }
    public required IReadOnlyList<PeriodDto> Periods { get; init; }
    public required IReadOnlyList<ProductDto> Products { get; init; }
    public required IReadOnlyList<ClosingPriceDto> ClosingPrices { get; init; }
    public required IReadOnlyList<MarketStatisticsDto> MarketStatistics { get; init; }
}

/// <summary>
/// Supplies the run's snapshot. Separate from the concrete session so the pipelines can
/// be tested without a gateway.
/// </summary>
internal interface IMarexSnapshotSource
{
    Task<MarexSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);
}

/// <summary>Creates the vendor client. Exists so tests can substitute a fake.</summary>
internal interface IMarexNeonClientFactory
{
    INeonApiClient Create();
}

/// <inheritdoc/>
internal sealed class MarexNeonClientFactory : IMarexNeonClientFactory
{
    // The SDK has no parameterless constructor; it needs an Rx IScheduler, from which it
    // builds its own inbound/outbound worker threads.
    public INeonApiClient Create() => new NeonApiClient(Scheduler.Default);
}

/// <summary>
/// Owns the single live gateway connection for one run, and hands every pipeline the
/// same captured snapshot.
///
/// <para>
/// This is the one part of the loader that does not look like the rest of the platform,
/// because the source does not look like the rest of the platform. Every other loader
/// PULLS: it asks for a window and gets an answer. Neon PUSHES: the client authenticates,
/// opens a SignalR websocket, and the gateway sends an unsolicited opening snapshot of
/// every entity, then streams deltas until the socket closes. There is no request that
/// means "give me the closing prices".
/// </para>
///
/// <para>
/// So the run shape is: connect ONCE, capture the opening snapshot, hand the same
/// captured object to all five pipelines, disconnect. Five pipelines share one
/// connection rather than opening five — both because the gateway sends all five
/// entities down one socket whether we want them or not, and because connecting five
/// times would mean five Auth0 grants and five snapshot transfers for the same bytes.
/// </para>
///
/// <para><b>Four things here are not optional, and each fails silently or confusingly
/// if changed:</b></para>
/// <list type="number">
///   <item>
///     <b>Subscribe before Connect.</b> The opening snapshot is pushed as soon as the
///     handshake completes. A handler attached after <c>Connect</c> races the arrival
///     and, when it loses, the run simply times out with no data and no error.
///   </item>
///   <item>
///     <b>Wait for the snapshots, not for <c>Connected</c>.</b> The SDK reaches
///     <c>ApiConnectionState.Connected</c> only after it has validated a heartbeat,
///     which lands AFTER the snapshot payload has already been delivered. Blocking on
///     the state first and reading the snapshot second works by luck on a fast link and
///     deadlocks on a slow one. Verified live: all six snapshot events fired while
///     <c>ConnectionStatus</c> still read <c>Connecting</c>.
///   </item>
///   <item>
///     <b><c>NeonApiConfig.Name</c> is the SignalR HUB name</b>, not a client label —
///     see <see cref="MarexSettings.HubName"/>.
///   </item>
///   <item>
///     <b>An <c>Unauthorized</c> or <c>IncompatibleVersion</c> state must abort the
///     wait.</b> The SDK's reconnect loop retries a doomed connection every 5 seconds
///     indefinitely; without these two short-circuits a bad credential costs the full
///     snapshot timeout on every run instead of failing in a second.
///   </item>
/// </list>
/// </summary>
internal sealed class MarexSnapshotSession : IMarexSnapshotSource, IAsyncDisposable
{
    private readonly MarexSettings _settings;
    private readonly IMarexNeonClientFactory _clientFactory;
    private readonly ILogger _logger;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private MarexSnapshot? _snapshot;
    private INeonApiClient? _client;
    private bool _disposed;

    internal MarexSnapshotSession(
        MarexSettings settings, IMarexNeonClientFactory clientFactory, ILogger logger)
    {
        _settings = settings;
        _clientFactory = clientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Returns the run's snapshot, connecting and capturing it on the first call. Safe to
    /// call concurrently from several pipelines — the first caller does the work and the
    /// rest await the same result.
    /// </summary>
    public async Task<MarexSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        if (_snapshot is not null) return _snapshot;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _snapshot ??= await ConnectAndCaptureAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<MarexSnapshot> ConnectAndCaptureAsync(CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var client = _clientFactory.Create();
        _client = client;

        // ------------------------------------------------------------------ token
        // Auth0 resource-owner password grant. The SDK sources the tenant and client id
        // from PasswordConnectionData, so there is no app.config dependency and no need
        // to hand-roll the /oauth/token call.
        _logger.LogInformation(
            "Marex: requesting a Neon token for {User} from {Domain}",
            _settings.Username, _settings.AuthDomain);

        var token = await client.GetToken(new PasswordConnectionData
        {
            UserName = _settings.Username,
            Password = _settings.Password,
            Domain = _settings.AuthDomain,
            ClientId = _settings.ClientId
        }).WaitAsync(cancellationToken).ConfigureAwait(false);

        if (!token.IsSuccess || string.IsNullOrWhiteSpace(token.Data))
            throw new InvalidOperationException(
                $"Marex: Auth0 token request failed: {Describe(token.Message)}. " +
                "Check Username/Password/ClientId (all three resolve from core.Param when set to " +
                "'SEE_DB'), and note that an account with MFA enabled cannot use the password grant.");

        // -------------------------------------------------- subscribe, THEN connect
        var capture = new SnapshotCapture(_logger);
        capture.Attach(client);

        var failure = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnState(object? sender, GatewayClientStateArgs args)
        {
            _logger.LogDebug("Marex: gateway state {State} {Message}", args.State, args.ErrorMessage);

            // These two never recover on their own. The SDK would keep retrying every
            // 5 seconds until the snapshot timeout expired.
            if (args.State == ApiConnectionState.Unauthorized)
                failure.TrySetResult(
                    "the gateway rejected the token (Unauthorized). The token was issued but is not " +
                    "accepted for this endpoint — check the account's entitlement to " +
                    $"{_settings.ApiEndpoint}");
            else if (args.State == ApiConnectionState.IncompatibleVersion)
                failure.TrySetResult(
                    "the gateway reported IncompatibleVersion — the vendored SDK in lib/neon is too " +
                    "old for the server. Ask Marex for a newer NeonMarkets.NeonAPI package");
        }

        client.ConnectionStateChanged += OnState;

        try
        {
            var hub = string.IsNullOrWhiteSpace(_settings.HubName) ? null : _settings.HubName;

            _logger.LogInformation(
                "Marex: connecting to {Endpoint} (hub {Hub})",
                _settings.ApiEndpoint, hub ?? "Gateway.Crude (SDK default)");

            // Synchronous: this only kicks the connection off. IsSuccess=false means the
            // SDK refused to start (e.g. already connected), NOT that the gateway
            // accepted us — that is what the snapshot wait below actually proves.
            var connect = client.Connect(new NeonApiConfig
            {
                URL = _settings.ApiEndpoint,
                Token = token.Data,
                Name = hub,
                EnableTracing = _settings.EnableSignalRTracing
            });

            if (!connect.IsSuccess)
                throw new InvalidOperationException(
                    $"Marex: Connect was refused by the SDK: {Describe(connect.Message)}");

            var timeout = TimeSpan.FromSeconds(Math.Max(1, _settings.SnapshotTimeoutSeconds));
            var snapshot = await capture
                .WaitAsync(timeout, failure.Task, cancellationToken)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "Marex: snapshot complete in {Elapsed:N1}s — ExchangeDate {Date:yyyy-MM-dd}; " +
                "{Groups} period group(s), {Periods} period(s), {Products} product(s), " +
                "{Closing} closing price(s), {Stats} market statistic(s)",
                sw.Elapsed.TotalSeconds, snapshot.ExchangeDate,
                snapshot.PeriodGroups.Count, snapshot.Periods.Count, snapshot.Products.Count,
                snapshot.ClosingPrices.Count, snapshot.MarketStatistics.Count);

            return snapshot;
        }
        finally
        {
            client.ConnectionStateChanged -= OnState;
            capture.Detach(client);
        }
    }

    /// <summary>A vendor message is often empty; say so rather than printing nothing.</summary>
    private static string Describe(string? message) =>
        string.IsNullOrWhiteSpace(message) ? "(no message from the SDK)" : message;

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        var client = Interlocked.Exchange(ref _client, null);
        if (client is null) return;

        // Disconnect before Dispose, and never let teardown fail a run that has already
        // written its rows.
        await Task.Run(() =>
        {
            try { client.Disconnect(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Marex: Disconnect threw during teardown"); }

            try { client.Dispose(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Marex: Dispose threw during teardown"); }
        }).ConfigureAwait(false);

        _gate.Dispose();
    }

    /// <summary>
    /// Collects the six opening snapshot events and completes once all of them have
    /// arrived.
    ///
    /// <para>The gateway re-sends the whole set after a reconnect, so every slot is
    /// write-once: the FIRST complete set wins and a later one is ignored rather than
    /// half-overwriting a snapshot some pipeline is already reading.</para>
    /// </summary>
    private sealed class SnapshotCapture
    {
        private const int ExpectedParts = 6;

        private readonly ILogger _logger;
        private readonly object _lock = new();
        private readonly TaskCompletionSource<MarexSnapshot> _complete =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private DateTime? _exchangeDate;
        private IReadOnlyList<PeriodGroupDto>? _periodGroups;
        private IReadOnlyList<PeriodDto>? _periods;
        private IReadOnlyList<ProductDto>? _products;
        private IReadOnlyList<ClosingPriceDto>? _closingPrices;
        private IReadOnlyList<MarketStatisticsDto>? _marketStatistics;

        internal SnapshotCapture(ILogger logger) => _logger = logger;

        private EventHandler<DateTime>? _onExchangeDate;
        private EventHandler<PeriodGroupSnapshotDto>? _onPeriodGroups;
        private EventHandler<PeriodSnapshotDto>? _onPeriods;
        private EventHandler<ProductSnapshotDto>? _onProducts;
        private EventHandler<ClosingPriceSnapshotDto>? _onClosingPrices;
        private EventHandler<MarketStatisticsSnapshotDto>? _onMarketStatistics;

        internal void Attach(INeonApiClient client)
        {
            // MUST happen before Connect — see the class remarks.
            client.ExchangeDateSnapshot += _onExchangeDate =
                (_, d) => Set("ExchangeDate", () => _exchangeDate ??= d, () => _exchangeDate is not null, 1);
            client.PeriodGroupSnapshot += _onPeriodGroups =
                (_, d) => Set("PeriodGroup", () => _periodGroups ??= Safe(d?.PeriodGroups), () => _periodGroups is not null, d?.PeriodGroups?.Count);
            client.PeriodSnapshot += _onPeriods =
                (_, d) => Set("Period", () => _periods ??= Safe(d?.Periods), () => _periods is not null, d?.Periods?.Count);
            client.ProductSnapshot += _onProducts =
                (_, d) => Set("Product", () => _products ??= Safe(d?.Products), () => _products is not null, d?.Products?.Count);
            client.ClosingPriceSnapshot += _onClosingPrices =
                (_, d) => Set("ClosingPrice", () => _closingPrices ??= Safe(d?.ClosingPrices), () => _closingPrices is not null, d?.ClosingPrices?.Count);
            client.MarketStatisticSnapshot += _onMarketStatistics =
                (_, d) => Set("MarketStatistic", () => _marketStatistics ??= Safe(d?.MarketStatistics), () => _marketStatistics is not null, d?.MarketStatistics?.Count);
        }

        internal void Detach(INeonApiClient client)
        {
            if (_onExchangeDate is not null) client.ExchangeDateSnapshot -= _onExchangeDate;
            if (_onPeriodGroups is not null) client.PeriodGroupSnapshot -= _onPeriodGroups;
            if (_onPeriods is not null) client.PeriodSnapshot -= _onPeriods;
            if (_onProducts is not null) client.ProductSnapshot -= _onProducts;
            if (_onClosingPrices is not null) client.ClosingPriceSnapshot -= _onClosingPrices;
            if (_onMarketStatistics is not null) client.MarketStatisticSnapshot -= _onMarketStatistics;
        }

        /// <summary>A null list from the vendor is an EMPTY set, not a missing part.</summary>
        private static IReadOnlyList<T> Safe<T>(IEnumerable<T>? items) => items?.ToArray() ?? Array.Empty<T>();

        private void Set(string part, Action assign, Func<bool> alreadyHad, int? count)
        {
            lock (_lock)
            {
                if (alreadyHad())
                {
                    // A reconnect re-sent the set. Keep the first.
                    _logger.LogDebug("Marex: ignoring a repeated {Part} snapshot", part);
                    return;
                }

                assign();
                _logger.LogDebug("Marex: received {Part} snapshot ({Count} item(s))", part, count);
                TryComplete();
            }
        }

        /// <summary>Caller must hold <see cref="_lock"/>.</summary>
        private void TryComplete()
        {
            var have = 0;
            if (_exchangeDate is not null) have++;
            if (_periodGroups is not null) have++;
            if (_periods is not null) have++;
            if (_products is not null) have++;
            if (_closingPrices is not null) have++;
            if (_marketStatistics is not null) have++;
            if (have < ExpectedParts) return;

            _complete.TrySetResult(new MarexSnapshot
            {
                // The gateway sends the trading day at midnight; keep only the date part,
                // which is what both primary keys store.
                ExchangeDate = _exchangeDate!.Value.Date,
                PeriodGroups = _periodGroups!,
                Periods = _periods!,
                Products = _products!,
                ClosingPrices = _closingPrices!,
                MarketStatistics = _marketStatistics!
            });
        }

        /// <summary>
        /// Waits for the full set, a fatal connection state, cancellation, or the timeout
        /// — whichever comes first.
        /// </summary>
        internal async Task<MarexSnapshot> WaitAsync(
            TimeSpan timeout, Task<string> failure, CancellationToken cancellationToken)
        {
            var timeoutTask = Task.Delay(timeout, cancellationToken);
            var winner = await Task.WhenAny(_complete.Task, failure, timeoutTask).ConfigureAwait(false);

            if (winner == _complete.Task) return await _complete.Task.ConfigureAwait(false);

            if (winner == failure)
                throw new InvalidOperationException($"Marex: {await failure.ConfigureAwait(false)}");

            // Task.Delay completed: either the run was cancelled or we genuinely timed out.
            cancellationToken.ThrowIfCancellationRequested();

            throw new TimeoutException(
                $"Marex: the gateway did not deliver a complete opening snapshot within {timeout.TotalSeconds:N0}s. " +
                $"Received {Missing()}. A connection that stays on 'Connecting' with no snapshot is usually a " +
                "wrong hub name (MarexSettings.HubName) — the gateway answers the SignalR negotiate with " +
                "HTTP 500 and the SDK then retries silently every 5 seconds. Set EnableSdkLogging to see it.");
        }

        private string Missing()
        {
            lock (_lock)
            {
                var got = new List<string>();
                var missing = new List<string>();
                void Note(string name, bool have) => (have ? got : missing).Add(name);

                Note("ExchangeDate", _exchangeDate is not null);
                Note("PeriodGroup", _periodGroups is not null);
                Note("Period", _periods is not null);
                Note("Product", _products is not null);
                Note("ClosingPrice", _closingPrices is not null);
                Note("MarketStatistic", _marketStatistics is not null);

                return $"[{string.Join(", ", got)}]; still waiting on [{string.Join(", ", missing)}]";
            }
        }
    }
}
