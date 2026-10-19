using System.Data;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DataLoader.NUCORE.Tests;

/// <summary>
/// The DataTable half of the TVP contract, the retry predicate and the log redaction.
/// </summary>
public class NucoreSinkAndPolicyTests
{
    /// <summary>Exposes the protected BuildTable so the contract can be asserted with no connection.</summary>
    private sealed class TestableSink : NucoreTableSink
    {
        public TestableSink(NucoreEndpoint endpoint)
            : base(endpoint, "Server=.;Database=NUCORE;Integrated Security=true", NullLogger.Instance) { }

        public DataTable Build(IReadOnlyList<NucoreRow> rows) => BuildTable(rows);
    }

    public static TheoryData<string> EndpointNames()
    {
        var data = new TheoryData<string>();
        foreach (var e in NucoreDescriptors.All) data.Add(e.Name);
        return data;
    }

    [Theory]
    [MemberData(nameof(EndpointNames))]
    public void DataTableColumnsMatchTheDescriptorInNameOrderAndClrType(string endpointName)
    {
        var endpoint = NucoreDescriptors.Find(endpointName)!;
        var table = new TestableSink(endpoint).Build(Array.Empty<NucoreRow>());

        Assert.Equal(endpoint.Columns.Count, table.Columns.Count);

        for (var i = 0; i < endpoint.Columns.Count; i++)
        {
            // Order is the contract: a TVP binds by position.
            Assert.Equal(endpoint.Columns[i].Name, table.Columns[i].ColumnName);
            Assert.Equal(endpoint.Columns[i].ClrType, table.Columns[i].DataType);
        }
    }

    [Fact]
    public void ARowWithTheWrongValueCountIsRejectedBeforeItReachesTheServer()
    {
        // A length mismatch means the row builder and the descriptor disagree. Letting it
        // through would shift every later column and write a plausible, wrong row.
        var endpoint = NucoreDescriptors.Find("Volatilities")!;
        var sink = new TestableSink(endpoint);

        var ex = Assert.Throws<InvalidOperationException>(
            () => sink.Build(new[] { new NucoreRow { Values = new object[] { 1, 2 } } }));

        Assert.Contains("descriptor declares", ex.Message);
    }

    [Fact]
    public void SinkTargetsTheEndpointsOwnProcAndType()
    {
        foreach (var endpoint in NucoreDescriptors.All)
        {
            Assert.Equal($"dbo.usp_Merge{endpoint.Name}", endpoint.MergeProc);
            Assert.Equal($"dbo.{endpoint.Name}Type", endpoint.TvpType);
            Assert.Equal($"dbo.{endpoint.Name}", endpoint.TableName);
        }
    }

    // ------------------------------------------------------------- the retry predicate

    /// <summary>
    /// ⚠ HTTP 500 must NOT be retried. On this API a 500 is what a MALFORMED request gets -
    /// a garbage date string returns 500, not 400 - so it is deterministic. Polly's stock
    /// HandleTransientHttpError treats every 5xx as transient, which would burn the whole
    /// retry budget before failing anyway and bury the real cause.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, false)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.RequestTimeout, true)]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.BadGateway, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.GatewayTimeout, true)]
    public void OnlyGenuinelyTransientStatusesAreRetried(HttpStatusCode status, bool expected)
    {
        using var response = new HttpResponseMessage(status);
        Assert.Equal(expected, NucoreHttp.IsTransientStatus(response));
    }

    // -------------------------------------------------------------------- redaction

    [Theory]
    [InlineData("GET /x?api_key=abc123 failed", "abc123")]
    // Synthetic values only - never a fragment of a real key, not even a prefix.
    [InlineData("header X-Api-Key: EXAMPLEKEYVALUE", "EXAMPLEKEYVALUE")]
    [InlineData("X-API-KEY=secretvalue", "secretvalue")]
    [InlineData("?apikey=zzz&page=1", "zzz")]
    public void SecretsAreScrubbedFromAnyMessageThatLeavesTheLoader(string input, string secret)
    {
        var redacted = NucoreHttp.Redact(input);
        Assert.DoesNotContain(secret, redacted, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RedactionLeavesTheUsefulPartOfAMessage()
    {
        var redacted = NucoreHttp.Redact("NUCORE VarResults 2026-10-14: HTTP 504");
        Assert.Contains("VarResults", redacted);
        Assert.Contains("504", redacted);
    }

    [Fact]
    public void RedactionHandlesNullAndEmpty()
    {
        Assert.Equal(string.Empty, NucoreHttp.Redact(null));
        Assert.Equal(string.Empty, NucoreHttp.Redact(string.Empty));
    }

    // ---------------------------------------------------- configuration self-checks

    [Fact]
    public void ShippedDefaultsKeepTheRetryBudgetInsideTheWorkUnitBudget()
    {
        // ⚠ The coupling that bit EIA: retries are serial, so the worst case is
        // (RetryCount + 1) x HttpTimeoutSeconds plus backoff. If that exceeds the per-unit
        // budget, a unit is cancelled mid-retry and the log blames the wrong setting.
        var settings = new NucoreSettings();
        var worstCase = (settings.RetryCount + 1) * settings.HttpTimeoutSeconds;

        Assert.True(
            worstCase <= settings.WorkUnitTimeoutSeconds,
            $"(RetryCount {settings.RetryCount} + 1) x HttpTimeoutSeconds " +
            $"{settings.HttpTimeoutSeconds} = {worstCase}s exceeds WorkUnitTimeoutSeconds " +
            $"{settings.WorkUnitTimeoutSeconds}s.");
    }

    [Fact]
    public void TheWarningPassToleratesEveryDegenerateSetting()
    {
        // The startup warnings must never be the thing that throws - they exist to explain a
        // misconfiguration, not to become one.
        var settings = new NucoreSettings
        {
            PageSize = 0,
            BatchSize = 0,
            HttpTimeoutSeconds = 1,
            WorkUnitTimeoutSeconds = 1,
            SettledAfterDays = -5,
            StartDate = "not-a-date",
            TradeMonthFrom = "nonsense",
            ApiKey = "SEE_DB",
            ConnectionString = string.Empty,
            EnabledEndpoints = new List<string> { "NoSuchEndpoint" }
        };

        NucoreModule.WarnAboutConfiguration(settings, NullLogger.Instance);
    }

    [Fact]
    public void ShippedDefaultsAreInternallyConsistent()
    {
        var settings = new NucoreSettings();

        Assert.True(settings.PageSize > 0);
        Assert.True(settings.BatchSize > 0);
        Assert.True(settings.SettledAfterDays >= 0);
        Assert.True(settings.MaxPagesPerUnit > 0);
        Assert.True(NucoreTime.TryParseTradeMonth(settings.TradeMonthFrom, out _));
        Assert.True(DateTime.TryParse(settings.StartDate, out _));
        Assert.Equal(5, settings.EnabledEndpoints.Count);
    }
}
