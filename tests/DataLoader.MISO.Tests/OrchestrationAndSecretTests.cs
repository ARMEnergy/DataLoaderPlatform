using System.Net;
using DataLoader.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DataLoader.MISO.Tests;

/// <summary>Module-level behaviour (run guard, feed toggle, discovery) and secret hygiene (design §0, §5, §7).</summary>
public sealed class OrchestrationAndSecretTests
{
    private static readonly DateTime Started = new(2026, 11, 24, 14, 0, 0, DateTimeKind.Utc);   // todayEst 2026-11-24

    [Fact]
    public async Task A_401_stops_the_run_with_one_aggregate_failure_and_no_load_log_row_per_remaining_unit()
    {
        var stub = StubHttpMessageHandler.Always(HttpStatusCode.Unauthorized, Samples.Fixture("err_badkey.json"));
        using var h = MisoHarness.Create(stub);
        var result = await h.RunAsync(Started);

        Assert.False(result.Success);
        Assert.Equal(1, stub.Count);   // one 401, no retry, nothing after it
        Assert.True(h.Guard.IsTripped);
        Assert.Contains("run guard tripped", result.ErrorMessage);
        Assert.Contains("HTTP 401", result.ErrorMessage);
        Assert.Contains("feeds not started: load_hourly, fuel_mix_hourly, load_forecast_mtlf", result.ErrorMessage);

        // Of the 20 units (DaysBack 2: 3 day feeds × 2 + MTLF 2 × 7) only the one that met the 401 was begun.
        Assert.Single(h.LoadLog.BegunKeys);
        Assert.Single(h.LoadLog.Failures);
        Assert.Contains("HTTP 401", h.LoadLog.Failures.Single().Error);
        Assert.Empty(h.LoadLog.Successes);
        Assert.Empty(h.ZonalSink.Writes);
    }

    [Fact]
    public async Task A_401_with_several_units_in_flight_begins_no_unit_after_the_trip()
    {
        // Concurrency 4: at most the units already holding a slot record a row; nothing later is begun.
        var stub = StubHttpMessageHandler.Always(HttpStatusCode.Unauthorized, Samples.Fixture("err_badkey.json"));
        using var h = MisoHarness.Create(stub, new Dictionary<string, string?> { ["Loaders:MISO:MaxConcurrentWorkUnits"] = "4", ["Loaders:MISO:DaysBack"] = "10" });
        var result = await h.RunAsync(Started);
        Assert.False(result.Success);
        Assert.InRange(h.LoadLog.BegunKeys.Count, 1, 4);
        Assert.InRange(stub.Count, 1, 4);
        Assert.Equal(h.LoadLog.BegunKeys.Count, h.LoadLog.Failures.Count);
    }

    [Fact]
    public async Task A_403_also_stops_the_run()
    {
        var stub = StubHttpMessageHandler.Always(HttpStatusCode.Forbidden, "{\"statusCode\":403,\"message\":\"Out of call volume quota.\"}");
        using var h = MisoHarness.Create(stub);
        var result = await h.RunAsync(Started);
        Assert.False(result.Success);
        Assert.Equal(1, stub.Count);
        Assert.Contains("HTTP 403", h.Guard.Reason);
    }

    [Fact]
    public async Task A_deterministic_400_fails_units_but_does_not_stop_the_run()
    {
        var stub = StubHttpMessageHandler.Always(HttpStatusCode.BadRequest, Samples.Fixture("err_badzone.json"));
        using var h = MisoHarness.Create(stub);
        var result = await h.RunAsync(Started);
        Assert.False(result.Success);
        Assert.False(h.Guard.IsTripped);
        Assert.Equal(20, stub.Count);   // one request per unit, never retried
        Assert.Equal(20, h.LoadLog.Failures.Count);
        Assert.All(h.LoadLog.Failures, f => Assert.Contains("must be one of the acceptableValues", f.Error));
    }

    [Fact]
    public async Task EnabledFeeds_toggles_pipelines()
    {
        var stub = StubHttpMessageHandler.Always(HttpStatusCode.NotFound, Samples.Fixture("err_page2.json"));
        using var h = MisoHarness.Create(stub, new Dictionary<string, string?> { ["Loaders:MISO:EnabledFeeds:0"] = "fuel_mix_hourly" });
        var result = await h.RunAsync(Started);
        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(2, stub.Count);
        Assert.All(stub.Requests, r => Assert.Contains("/generation/fuel-type", r.Uri.AbsolutePath));
    }

    [Fact]
    public void The_module_registers_four_pipelines_and_is_discoverable()
    {
        Assert.Equal("MISO", new MisoModule().LoaderId);
        Assert.True(typeof(ILoaderModule).IsAssignableFrom(typeof(MisoModule)));
        Assert.NotNull(Activator.CreateInstance(typeof(MisoModule)));   // the host instantiates it parameterlessly

        using var h = MisoHarness.Create(StubHttpMessageHandler.Always(HttpStatusCode.OK, "{}"));
        Assert.Equal(MisoFeeds.ExecutionOrder.OrderBy(x => x),
            h.Services.GetServices<IMisoPipeline>().Select(p => p.FeedId).OrderBy(x => x));
    }

    [Fact]
    public void Host_references_the_MISO_project_and_the_solution_contains_both_projects()
    {
        var host = File.ReadAllText(Path.Combine(RepoPaths.Root, "src", "DataLoader.Host", "DataLoader.Host.csproj"));
        Assert.Contains(@"..\DataLoader.MISO\DataLoader.MISO.csproj", host);
        var sln = File.ReadAllText(Path.Combine(RepoPaths.Root, "DataLoaderPlatform.sln"));
        Assert.Contains(@"src\DataLoader.MISO\DataLoader.MISO.csproj", sln);
        Assert.Contains(@"tests\DataLoader.MISO.Tests\DataLoader.MISO.Tests.csproj", sln);
        var cmd = File.ReadAllText(Path.Combine(RepoPaths.Root, "MISO.cmd"));
        Assert.Contains("DataLoader.Host.exe MISO", cmd);
    }

    [Fact]
    public void MISO_cmd_warns_before_running_against_the_appsettings_databases()
    {
        var lines = File.ReadAllLines(Path.Combine(RepoPaths.Root, "MISO.cmd"));
        var warn = Array.FindIndex(lines, l => l.Contains("WARNING"));
        var run = Array.FindIndex(lines, l => l.Contains("DataLoader.Host.exe MISO"));
        Assert.InRange(warn, 0, run - 1);
        var text = string.Join("\n", lines);
        Assert.Contains("ARMH-OPSDB01", text);
        Assert.Contains("Misc", text);
        Assert.Contains("DATALOADER_Loaders__MISO__ConnectionString", text);
        Assert.Contains("DATALOADER_Platform__LoadLogConnectionString", text);
        Assert.Contains("pause", lines.Skip(warn).Take(run - warn).Select(l => l.Trim()));   // a chance to abort first
    }

    // ---- secret hygiene -------------------------------------------------------------------------

    [Fact]
    public async Task The_key_travels_only_in_the_header_never_in_a_url_log_loadlog_or_result()
    {
        // A hostile mix: one feed answers 200, one 400 echoing the key in its reason, one 404, one 5xx echoing it.
        var stub = new StubHttpMessageHandler((r, _) =>
        {
            var (path, q) = Req.Parse(r.RequestUri!);
            if (path.Contains("/generation/fuel-type"))
                return StubHttpMessageHandler.Json(HttpStatusCode.BadRequest, "{\"status\":400,\"reason\":\"bad key " + Samples.Key + " supplied\"}");
            if (path.Contains("/forecast/"))
                return StubHttpMessageHandler.Json(HttpStatusCode.ServiceUnavailable, "{\"status\":503,\"reason\":\"key=" + Samples.Key + "\"}");
            if (q.GetValueOrDefault("geoResolution") == "region")
                return StubHttpMessageHandler.Json(HttpStatusCode.NotFound, Samples.Fixture("err_page2.json"));
            var day = DateOnly.Parse(path.Split('/')[4]);
            return Req.Ok(Samples.Envelope(Samples.ZonalDay(day)));
        });

        using var h = MisoHarness.Create(stub);
        var result = await h.RunAsync(Started);

        Assert.False(result.Success);
        Assert.NotEmpty(stub.Requests);
        Assert.All(stub.Requests, r => Assert.Equal(Samples.Key, r.KeyHeader));
        Assert.All(stub.Requests, r => Assert.DoesNotContain(Samples.Key, r.Uri.ToString()));
        Assert.Contains(h.LoadLog.Failures, f => f.Error.Contains("bad key *** supplied"));
        Secrets.AssertAbsent(h.AllRunText(result), Samples.Key);
    }

    [Fact]
    public async Task A_401_whose_body_echoes_the_key_is_redacted_in_the_guard_reason()
    {
        var stub = StubHttpMessageHandler.Always(HttpStatusCode.Unauthorized,
            "{ \"statusCode\": 401, \"message\": \"Access denied due to invalid subscription key " + Samples.Key + "\" }");
        using var h = MisoHarness.Create(stub);
        var result = await h.RunAsync(Started);
        Assert.Contains("***", h.Guard.Reason);
        Secrets.AssertAbsent(h.AllRunText(result), Samples.Key);
    }

    [Fact]
    public async Task Exceptions_from_the_client_never_carry_the_key()
    {
        var stub = StubHttpMessageHandler.Always(HttpStatusCode.BadRequest, "{\"status\":400,\"reason\":\"" + Samples.Key + "\"}");
        using var h = MisoHarness.Create(stub);
        var ex = await Assert.ThrowsAsync<MisoRequestException>(() =>
            h.Reader(new MisoZonalLoadMapper()).ReadAsync(Samples.Unit(MisoFeeds.ZonalLoadHourly, Samples.D(2026, 11, 18)), default));
        Secrets.AssertAbsent(Secrets.ExceptionText(ex), Samples.Key);
    }

    [Fact]
    public void The_key_is_not_a_default_header_of_the_named_client()
    {
        using var h = MisoHarness.Create(StubHttpMessageHandler.Always(HttpStatusCode.OK, "{}"));
        var client = h.Services.GetRequiredService<IHttpClientFactory>().CreateClient(MisoModule.HttpClientName);
        Assert.False(client.DefaultRequestHeaders.Contains(MisoApiClient.KeyHeader));
        Assert.Equal(Timeout.InfiniteTimeSpan, client.Timeout);   // the budget lives in the per-attempt policy
    }

    [Fact]
    public void Redaction_removes_the_key_before_truncating_and_bounds_length()
    {
        var text = new string('x', 290) + Samples.Key + " tail";
        var cleaned = MisoRedaction.Clean(text, Samples.Key);
        Assert.DoesNotContain(Samples.Key[..8], cleaned);
        Assert.True(cleaned.Length <= MisoRedaction.MaxReasonLength + 3);
        Assert.Equal("api said ***", MisoRedaction.Clean("api said " + Samples.Key, Samples.Key));
    }

    [Fact]
    public async Task Long_vendor_reasons_are_bounded_to_300_characters()
    {
        var reason = "Invalid date: expected format " + new string('z', 1000);
        var stub = StubHttpMessageHandler.Always(HttpStatusCode.BadRequest, "{\"status\":400,\"reason\":\"" + reason + "\"}");
        using var h = MisoHarness.Create(stub);
        var ex = await Assert.ThrowsAsync<MisoRequestException>(() =>
            h.Reader(new MisoZonalLoadMapper()).ReadAsync(Samples.Unit(MisoFeeds.ZonalLoadHourly, Samples.D(2026, 11, 18)), default));
        Assert.True(ex.Message.Count(c => c == 'z') <= 300);
    }
}
