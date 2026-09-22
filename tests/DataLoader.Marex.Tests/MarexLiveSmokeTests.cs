using DataLoader.Marex;
using Xunit;
using Neon.API.Interfaces;

namespace DataLoader.Marex.Tests;

/// <summary>
/// Opt-in test that runs the loader's REAL read path against the live Neon gateway.
///
/// <para>Skipped unless <c>MAREX_LIVE=1</c> and the three credentials are in the
/// environment, so a normal <c>dotnet test</c> never touches the network:</para>
/// <code>
/// MAREX_LIVE=1 MAREX_USERNAME=... MAREX_PASSWORD=... MAREX_CLIENTID=... \
///   dotnet test tests/DataLoader.Marex.Tests -c Release --filter Live
/// </code>
///
/// <para><b>Why this is worth keeping.</b> Two risks in this loader cannot be settled by
/// any offline test, because both are about what happens at run time inside vendored
/// binaries:</para>
/// <list type="number">
///   <item>
///     The three Neon assemblies are .NET Framework 4.6.1 running on .NET 8. Nothing but
///     an actual connection proves the SignalR transport, the Auth0 client and log4net
///     all resolve.
///   </item>
///   <item>
///     <c>Neon.API.dll</c> was compiled against Microsoft.IdentityModel 5.2.4, but
///     <c>DataLoader.Core</c> -&gt; Microsoft.Data.SqlClient pulls 6.35.0 into the same
///     graph and the higher version wins. 5.x -&gt; 6.x had breaking API changes, so this
///     is the test that says the token path still works in THIS build rather than only
///     in isolation.
///   </item>
/// </list>
///
/// <para>Read-only: it takes the opening snapshot and disconnects. It never calls the
/// vendor's order or trade surface — <see cref="FakeNeonApiClient"/> documents that
/// boundary, and the real <see cref="INeonApiClient"/> members for it are never
/// referenced by loader code.</para>
/// </summary>
public class MarexLiveSmokeTests
{
    private const string Reason =
        "Live gateway test. Set MAREX_LIVE=1 plus MAREX_USERNAME / MAREX_PASSWORD / MAREX_CLIENTID to run.";

    private static bool Enabled =>
        Environment.GetEnvironmentVariable("MAREX_LIVE") == "1"
        && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MAREX_USERNAME"))
        && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MAREX_PASSWORD"))
        && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MAREX_CLIENTID"));

    private static MarexSettings LiveSettings() => new()
    {
        ApiEndpoint = Environment.GetEnvironmentVariable("MAREX_ENDPOINT")
                      ?? "https://app-ca.neon.markets/api/crude",
        AuthDomain = "login.neon.markets",
        ClientId = Environment.GetEnvironmentVariable("MAREX_CLIENTID")!,
        Username = Environment.GetEnvironmentVariable("MAREX_USERNAME")!,
        Password = Environment.GetEnvironmentVariable("MAREX_PASSWORD")!,
        SnapshotTimeoutSeconds = 120
    };

    [SkippableFact]
    [Trait("Category", "Live")]
    public async Task Live_SnapshotAndMapping_WorksEndToEnd()
    {
        Skip.IfNot(Enabled, Reason);

        var log = new RecordingLogger();
        await using var session = new MarexSnapshotSession(
            LiveSettings(), new MarexNeonClientFactory(), log);

        var snapshot = await session.GetSnapshotAsync(
            new CancellationTokenSource(TimeSpan.FromMinutes(3)).Token);

        // --- the gateway answered with a real trading day -----------------------
        Assert.NotEqual(default, snapshot.ExchangeDate);
        Assert.Equal(TimeSpan.Zero, snapshot.ExchangeDate.TimeOfDay);
        Assert.InRange(snapshot.ExchangeDate, new DateTime(2020, 1, 1), DateTime.UtcNow.AddDays(7));

        // --- every entity arrived ----------------------------------------------
        // Lower bounds, not exact counts: this is a live market. Measured on
        // 2026-09-21: 6 groups, 724 periods, 1,666 products, 4,227 closing prices,
        // 266 market statistics.
        Assert.NotEmpty(snapshot.PeriodGroups);
        Assert.NotEmpty(snapshot.Periods);
        Assert.NotEmpty(snapshot.Products);
        Assert.NotEmpty(snapshot.ClosingPrices);
        Assert.NotEmpty(snapshot.MarketStatistics);

        // --- and the whole live set maps without throwing -----------------------
        // Every row, not a sample: a single unmappable record would otherwise only
        // surface on a production run.
        var rows = new[]
        {
            (MarexDescriptors.ClosingPrice, MarexSnapshotReader.ClosingPrices(snapshot)),
            (MarexDescriptors.MarketStatistic, MarexSnapshotReader.MarketStatistics(snapshot)),
            (MarexDescriptors.Period, MarexSnapshotReader.Periods(snapshot)),
            (MarexDescriptors.PeriodGroup, MarexSnapshotReader.PeriodGroups(snapshot)),
            (MarexDescriptors.Product, MarexSnapshotReader.Products(snapshot))
        };

        foreach (var (table, mapped) in rows)
        {
            Assert.NotEmpty(mapped);

            foreach (var row in mapped)
            {
                Assert.Equal(table.Columns.Count, row.Values.Length);

                for (var i = 0; i < table.Columns.Count; i++)
                {
                    var column = table.Columns[i];
                    var value = row.Values[i];

                    if (column.IsKey)
                        Assert.False(value is DBNull,
                            $"{table.TableName}.{column.Name} is a key and came back NULL from the live gateway");

                    if (value is DBNull) continue;

                    Assert.True(column.ClrType.IsInstanceOfType(value),
                        $"{table.TableName}.{column.Name} is declared {column.ClrType.Name} " +
                        $"but the live data produced {value.GetType().Name}");

                    // Would be truncated (non-key) or dropped (key) by the sink.
                    if (column.MaxLength is { } max && value is string s)
                        Assert.True(s.Length <= max,
                            $"{table.TableName}.{column.Name} live value is {s.Length} chars, " +
                            $"wider than its declared {max}");
                }
            }
        }

        // --- no credential ever reached the log --------------------------------
        Assert.DoesNotContain(log.Messages, m => m.Contains(LiveSettings().Password));
        Assert.DoesNotContain(log.Messages, m => m.Contains("eyJ"));
    }

    [SkippableFact]
    [Trait("Category", "Live")]
    public async Task Live_EveryLiveEnumValue_IsKnownToTheVendoredSdk()
    {
        Skip.IfNot(Enabled, Reason);

        // The gateway reports minVersion 3.37.0.1522 against a 3.15.1.22 SDK, so a value
        // the SDK's enums do not define is a live possibility. The mapper stringifies
        // such a value to its number rather than throwing — this reports whether that is
        // happening today, because a numeric enum column is data nobody would notice.
        var log = new RecordingLogger();
        await using var session = new MarexSnapshotSession(
            LiveSettings(), new MarexNeonClientFactory(), log);

        var snapshot = await session.GetSnapshotAsync(
            new CancellationTokenSource(TimeSpan.FromMinutes(3)).Token);

        var numeric = new List<string>();

        foreach (var (table, mapped) in new[]
                 {
                     (MarexDescriptors.MarketStatistic, MarexSnapshotReader.MarketStatistics(snapshot)),
                     (MarexDescriptors.Product, MarexSnapshotReader.Products(snapshot))
                 })
        {
            var enumColumns = table.FeedId == MarexDescriptors.MarketStatisticFeedId
                ? new[] { "State", "LastTradeEventSource", "LastTradeAggressorSide", "UpdateReason" }
                : new[] { "ProductType", "TradeChartDataSource" };

            foreach (var name in enumColumns)
            {
                var index = -1;
                for (var i = 0; i < table.Columns.Count; i++)
                    if (table.Columns[i].Name == name) { index = i; break; }

                foreach (var row in mapped)
                    if (row.Values[index] is string s && s.Length > 0 && !s.Any(char.IsLetter))
                        numeric.Add($"{table.TableName}.{name} = {s}");
            }
        }

        Assert.True(numeric.Count == 0,
            "The live gateway sent enum values the vendored SDK does not define, so they were written as " +
            "numbers. lib/neon needs refreshing. Offending values: " +
            string.Join(", ", numeric.Distinct().Take(20)));
    }
}
