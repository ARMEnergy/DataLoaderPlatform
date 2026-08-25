using System.Reflection;
using DataLoader.Core.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DataLoader.ModernCommodities.Tests;

/// <summary>
/// The module surface, the endpoint registry, the settings defaults, the credential guard and the
/// validator window - all DB-free and offline.
///
/// <para>Nothing here resolves the loader out of a real DI container: materialising
/// <c>IOptions&lt;ModComSettings&gt;</c> in production runs the <c>SEE_DB</c> resolver, which is a
/// database round-trip by design. The parts that matter without a database are asserted directly.</para>
/// </summary>
public class ModuleTests
{
    // ============================================================ module identity

    [Fact]
    public void TheModuleIdentifiesItselfConsistently()
    {
        var module = new ModernCommoditiesModule();

        Assert.Equal("ModernCommodities", ModernCommoditiesModule.Id);
        Assert.Equal(ModernCommoditiesModule.Id, module.LoaderId);
        Assert.Equal("ModernCommodities", ModernCommoditiesModule.HttpClientName);
        Assert.False(string.IsNullOrWhiteSpace(module.DisplayName));
        Assert.IsAssignableFrom<ILoaderModule>(module);
    }

    // ============================================================ the endpoint registry

    [Fact]
    public void ThereAreExactlyThreeEndpoints_InTheFixedCosmeticOrder()
    {
        Assert.Equal(3, ModComDescriptors.All.Count);
        Assert.Equal(
            new[] { ModComEndpoints.AllTrades, ModComEndpoints.MyTrades, ModComEndpoints.Settlements },
            ModComDescriptors.All.Select(d => d.EndpointId).ToArray());
    }

    [Fact]
    public void EachDescriptorNamesItsPathParseShapeAndTargetObjects()
    {
        Assert.Equal("allTrades/v1", ModComDescriptors.AllTrades.Path);
        Assert.Equal("myTrades/v1", ModComDescriptors.MyTrades.Path);
        Assert.Equal("settlements/v1", ModComDescriptors.Settlements.Path);

        Assert.Equal(ModComParseShape.Trades, ModComDescriptors.AllTrades.ParseShape);
        Assert.Equal(ModComParseShape.Trades, ModComDescriptors.MyTrades.ParseShape);
        Assert.Equal(ModComParseShape.Settlements, ModComDescriptors.Settlements.ParseShape);

        Assert.Equal("arm.AllTrades", ModComDescriptors.AllTrades.TargetTable);
        Assert.Equal("arm.MyTrades", ModComDescriptors.MyTrades.TargetTable);
        Assert.Equal("arm.Settlements", ModComDescriptors.Settlements.TargetTable);

        // The two trades endpoints deliberately SHARE one TVP type and differ only in the proc.
        Assert.Equal("arm.TradesTvp", ModComDescriptors.AllTrades.TargetTvp);
        Assert.Equal("arm.TradesTvp", ModComDescriptors.MyTrades.TargetTvp);
        Assert.Equal("arm.SettlementsTvp", ModComDescriptors.Settlements.TargetTvp);
        Assert.NotEqual(ModComDescriptors.AllTrades.TargetProc, ModComDescriptors.MyTrades.TargetProc);
    }

    [Fact]
    public void TheColumnListsAreTheThirtyFourAndNineVendorNames_WithNoDuplicates()
    {
        Assert.Equal(34, ModComColumns.Trades.Length);
        Assert.Equal(9, ModComColumns.Settlements.Length);
        Assert.Equal(34, ModComColumns.Trades.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(9, ModComColumns.Settlements.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        // The declared order IS the CSV order, which is the TVP order.
        Assert.Equal("Trade Number", ModComColumns.Trades[0]);
        Assert.Equal("Product Type", ModComColumns.Trades[33]);
        Assert.Equal("Settlement Date", ModComColumns.Settlements[0]);
        Assert.Equal("Price", ModComColumns.Settlements[8]);

        // ... and they match the literal live headers byte for byte.
        Assert.Equal(ModComColumns.Trades, ModComCsv.Parse(Samples.TradesHeader).Single());
        Assert.Equal(ModComColumns.Settlements, ModComCsv.Parse(Samples.SettlementsHeader).Single());
    }

    [Fact]
    public void TheCommittedFixtureHeadersMatchTheExpectedColumnListsExactly()
    {
        Assert.Equal(ModComColumns.Trades, ModComCsv.Parse(Samples.AllTradesLive)[0]);
        Assert.Equal(ModComColumns.Trades, ModComCsv.Parse(Samples.MyTradesAnonymised)[0]);
        Assert.Equal(ModComColumns.Trades, ModComCsv.Parse(Samples.MyTradesHeaderOnly)[0]);
        Assert.Equal(ModComColumns.Settlements, ModComCsv.Parse(Samples.SettlementsLive)[0]);
    }

    [Fact]
    public void TheTwoTradesEndpointsPublishAByteIdenticalHeader()
    {
        // The reason there is ONE row type, ONE reader, ONE BuildTable and ONE TVP.
        var allTrades = ModComCsv.Parse(Samples.AllTradesLive)[0];
        var myTrades = ModComCsv.Parse(Samples.MyTradesHeaderOnly)[0];

        Assert.Equal(allTrades, myTrades);
        Assert.Equal(497, Samples.MyTradesHeaderOnly.TrimEnd('\r', '\n').Length);   // the verified 497-byte header
    }

    // ============================================================ settings defaults

    [Fact]
    public void TheShippedDefaultsAreTheDecidedOnes()
    {
        var settings = new ModComSettings();

        Assert.Equal(30, settings.DaysBack);                                   // U4/D13 -> a 31-day window
        Assert.Equal(0, settings.ChunkDays);                                   // U6 -> 3 requests per run
        Assert.Equal(ModComHotKeyStrategy.RunHour, settings.HotKeyStrategy);    // U5
        Assert.Null(settings.LegalEntityName);                                 // widest myTrades scope
        Assert.Equal(120, settings.HttpTimeoutSeconds);
        Assert.Equal(1d, settings.RequestsPerSecond);
        Assert.Equal(5, settings.ValidationClockSkewMinutes);
        Assert.Equal(new[] { "AllTrades", "MyTrades", "Settlements" }, settings.EnabledEndpoints);
        Assert.Equal("https://app.modcom.inc/api/integration/", ModComSettings.DefaultBaseUrl);
    }

    [Fact]
    public void TheCredentialSettingsDefaultToTheSeeDbSentinel_NeverToALiteralSecret()
    {
        var settings = new ModComSettings();

        Assert.Equal("SEE_DB", settings.Username);
        Assert.Equal("SEE_DB", settings.Password);
    }

    [Theory]
    [InlineData("https://app.modcom.inc/api/integration/", "https://app.modcom.inc/api/integration")]
    [InlineData("https://app.modcom.inc/api/integration", "https://app.modcom.inc/api/integration")]
    [InlineData("https://app.modcom.inc/api/integration///", "https://app.modcom.inc/api/integration")]
    [InlineData(null, "https://app.modcom.inc/api/integration")]
    [InlineData("", "https://app.modcom.inc/api/integration")]
    [InlineData("   ", "https://app.modcom.inc/api/integration")]
    public void BaseUrlRoot_TrimsTrailingSlashesAndCoalescesABlankBinding(string? configured, string expected)
    {
        var settings = new ModComSettings { BaseUrl = configured! };
        Assert.Equal(expected, settings.BaseUrlRoot());
    }

    [Fact]
    public void EffectiveDaysBackAndChunkDays_FallThroughToTheGlobalsAndClampNegatives()
    {
        var settings = new ModComSettings { DaysBack = 30, ChunkDays = 7 };

        Assert.Equal(30, settings.EffectiveDaysBack(ModComEndpoints.AllTrades));
        Assert.Equal(7, settings.EffectiveChunkDays(ModComEndpoints.Settlements));

        settings.Endpoints["MyTrades"] = new ModComEndpointOverride { DaysBack = 3650 };
        settings.Endpoints["settlements"] = new ModComEndpointOverride { ChunkDays = 60 };

        Assert.Equal(3650, settings.EffectiveDaysBack(ModComEndpoints.MyTrades));
        Assert.Equal(7, settings.EffectiveChunkDays(ModComEndpoints.MyTrades));      // not overridden
        Assert.Equal(60, settings.EffectiveChunkDays(ModComEndpoints.Settlements));  // case-insensitive
        Assert.Equal(30, settings.EffectiveDaysBack(ModComEndpoints.AllTrades));

        settings.Endpoints["AllTrades"] = new ModComEndpointOverride { DaysBack = -5 };
        Assert.Equal(0, settings.EffectiveDaysBack(ModComEndpoints.AllTrades));      // clamped to >= 0
    }

    // ============================================================ the credential guard

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("SEE_DB", true)]
    [InlineData("see_db", true)]           // a hand-edited core.Param row is just as unconfigured
    [InlineData(" SEE_DB ", true)]
    [InlineData("modcom-test-user", false)]
    public void TheCredentialGuard_TreatsBlankAndTheSentinelAsUnresolved(string? value, bool expected)
    {
        var method = typeof(ModernCommoditiesModule)
            .GetMethod("IsUnresolved", BindingFlags.Static | BindingFlags.NonPublic)!;

        Assert.Equal(expected, (bool)method.Invoke(null, new object?[] { value })!);
    }

    // ============================================================ the validator window

    [Fact]
    public void TheValidatorUsesTheSAMEWindowArithmeticAsTheProviders_IncludingThe31Days()
    {
        // One shared helper, so the load window and the validation window can never drift apart.
        var validator = new ModernCommoditiesLoadValidator(
            Options.Create(ReaderHarness.Settings()), NullLogger<ModernCommoditiesLoadValidator>.Instance);

        var context = new LoaderRunContext
        {
            RunId = Guid.NewGuid(),
            StartedAtUtc = new DateTime(2026, 8, 24, 13, 0, 0, DateTimeKind.Utc),
            CancellationToken = CancellationToken.None
        };

        var (from, to) = validator.ResolveUnionWindow(context, ModComDescriptors.All);

        Assert.Equal(new DateOnly(2026, 7, 25), from);
        Assert.Equal(new DateOnly(2026, 8, 24), to);
        Assert.Equal(31, to.DayNumber - from.DayNumber + 1);   // the D13 figure, in the validator too
    }

    [Fact]
    public void TheValidatorWindowIsTheUNIONOfTheEnabledEndpoints()
    {
        var settings = ReaderHarness.Settings(daysBack: 30, endpoints: new()
        {
            ["MyTrades"] = new ModComEndpointOverride { DaysBack = 3650 }   // all-time, unclamped
        });
        var validator = new ModernCommoditiesLoadValidator(
            Options.Create(settings), NullLogger<ModernCommoditiesLoadValidator>.Instance);

        var context = new LoaderRunContext
        {
            RunId = Guid.NewGuid(),
            StartedAtUtc = new DateTime(2026, 8, 24, 13, 0, 0, DateTimeKind.Utc),
            CancellationToken = CancellationToken.None
        };

        // All three: the union reaches back to myTrades' unclamped start.
        var union = validator.ResolveUnionWindow(context, ModComDescriptors.All);
        Assert.Equal(new DateOnly(2016, 8, 26), union.From);
        Assert.Equal(new DateOnly(2026, 8, 24), union.To);

        // Settlements only: back to its own 31-day window, nothing wider.
        var settlementsOnly = validator.ResolveUnionWindow(context, new[] { ModComDescriptors.Settlements });
        Assert.Equal(new DateOnly(2026, 7, 25), settlementsOnly.From);

        // An empty list falls back to all three descriptors.
        var fallback = validator.ResolveUnionWindow(context, Array.Empty<ModComEndpointDescriptor>());
        Assert.Equal(union.From, fallback.From);
    }

    // ============================================================ the arm.FileLog writer

    [Fact]
    public void TheHubWriterClampsItsTextToTheColumnWidths_AndBlankToNull()
    {
        var clamp = typeof(SqlModComFileLog).GetMethod("Clamp", BindingFlags.Static | BindingFlags.NonPublic)!;

        // arm.FileLog.ErrorMessage is NVARCHAR(400) and carries the VERBATIM non-2xx body.
        var long401 = new string('x', 900);
        Assert.Equal(400, ((string?)clamp.Invoke(null, new object?[] { long401, 400 }))!.Length);
        Assert.Equal("body", clamp.Invoke(null, new object?[] { "  body  ", 400 }));
        Assert.Null(clamp.Invoke(null, new object?[] { "   ", 400 }));
        Assert.Null(clamp.Invoke(null, new object?[] { null, 400 }));
    }

    [Fact]
    public void TheHubContextCarriesTheFourNaturalKeyPartsAndTheSanitisedPath()
    {
        var file = new ModComFileContext(
            ModComEndpoints.AllTrades, new DateOnly(2026, 7, 25), new DateOnly(2026, 8, 24),
            "2026082413", null, "allTrades/v1?startDate=2026-07-25&endDate=2026-08-24");

        Assert.Equal(ModComEndpoints.AllTrades, file.Endpoint);
        Assert.Equal(new DateOnly(2026, 7, 25), file.WindowStart);
        Assert.Equal(new DateOnly(2026, 8, 24), file.WindowEnd);
        Assert.Equal("2026082413", file.RunToken);       // the resume key hot token
        Assert.Null(file.ScopeLabel);
        Assert.DoesNotContain("http", file.RequestPath); // relative, no host, no credential
    }

    [Fact]
    public async Task TheHubRowRecordsTheWindowAndTheRunTokenOfItsOwnWorkUnit()
    {
        var unit = ReaderHarness.Unit(
            ModComDescriptors.MyTrades,
            start: new DateOnly(2026, 8, 1), end: new DateOnly(2026, 8, 10),
            scope: "Acme Energy Management, LLC", hot: "2026082414");

        var result = await ReaderHarness.RunTradesAsync(
            System.Net.HttpStatusCode.OK, Samples.MyTradesAnonymised, ModComDescriptors.MyTrades, unit);

        var call = Assert.Single(result.FileLog.Calls);
        Assert.Equal(ModComEndpoints.MyTrades, call.File.Endpoint);
        Assert.Equal(new DateOnly(2026, 8, 1), call.File.WindowStart);
        Assert.Equal(new DateOnly(2026, 8, 10), call.File.WindowEnd);
        Assert.Equal("2026082414", call.File.RunToken);
        Assert.Equal("Acme Energy Management, LLC", call.File.ScopeLabel);
        Assert.Equal(unit.RequestPath, call.File.RequestPath);
        Assert.Equal(Samples.MyTradesRowCount, call.RowCount);
    }
}
