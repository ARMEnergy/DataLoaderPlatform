using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using DataLoader.Core.Abstractions;
using DataLoader.Core.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.MISO.Tests;

/// <summary>Synthetic MISO payloads shaped like docs/apis/MISO.md §2/§5 (no real data), plus fixture access.</summary>
internal static class Samples
{
    /// <summary>🔒 Secret sentinel: must reach the key header and nowhere else.</summary>
    public const string Key = "k3y-SENTINEL-9f8e7d6c5b4a";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Iso(DateTime dt) => dt.ToString("yyyy-MM-ddTHH:mm:ss", Inv);
    public static string Day(DateOnly d) => d.ToString("yyyy-MM-dd", Inv);

    public static JsonObject Interval(DateTime start, string resolution = "hourly", DateTime? end = null) => new()
    {
        ["resolution"] = resolution,
        ["start"] = Iso(start),
        ["end"] = Iso(end ?? start.AddHours(1)),
        ["value"] = (start.Hour + 1).ToString(Inv),
    };

    public static JsonObject LrzRow(DateTime start, string zone, decimal? load = 1000.1m) => new()
    {
        ["timeInterval"] = Interval(start),
        ["localResourceZone"] = zone,
        ["load"] = load is { } v ? JsonValue.Create(v) : null,
    };

    public static JsonObject RegionRow(DateTime start, string region, decimal? load = 2000.2m) => new()
    {
        ["timeInterval"] = Interval(start),
        ["region"] = region,
        ["load"] = load is { } v ? JsonValue.Create(v) : null,
    };

    public static JsonObject FuelRow(DateTime start, string region, JsonObject? fuel = null, decimal? total = 300m) => new()
    {
        ["timeInterval"] = Interval(start),
        ["region"] = region,
        ["fuelTypes"] = fuel ?? new JsonObject
        {
            ["coal"] = 1.5m, ["gas"] = 2.5m, ["nuclear"] = 3m, ["water"] = 4m,
            ["wind"] = 5m, ["solar"] = 6m, ["other"] = 7m, ["storage"] = 0.1m,
        },
        ["totalMw"] = total is { } t ? JsonValue.Create(t) : null,
        ["interval"] = (start.Hour + 1).ToString(Inv),
    };

    /// <summary>Observed in the live MTLF captures: NORTH Z1,Z3; CENTRAL Z2,Z4..Z7; SOUTH Z8..Z10 (independent oracle).</summary>
    public static string MtlfRegion(string zone) => zone.ToUpperInvariant() switch
    {
        "Z1" or "Z3" => "NORTH",
        "Z2" or "Z4" or "Z5" or "Z6" or "Z7" => "CENTRAL",
        _ => "SOUTH",
    };

    public static JsonObject MtlfRow(DateTime start, string zone, DateOnly init, decimal? forecast = 6092m, string? region = null) => new()
    {
        ["timeInterval"] = Interval(start),
        ["region"] = region ?? MtlfRegion(zone),
        ["localResourceZone"] = zone,
        ["loadForecast"] = forecast is { } f ? JsonValue.Create(f) : null,
        ["init"] = Day(init),
    };

    private static IEnumerable<DateTime> Hours(DateOnly d) => Enumerable.Range(0, 24).Select(h => d.ToDateTime(new TimeOnly(h, 0)));

    public static List<JsonObject> ZonalDay(DateOnly d) => Hours(d).SelectMany(h => MisoFeeds.Lrz.Select(z => LrzRow(h, z))).ToList();

    public static List<JsonObject> RegionDay(DateOnly d, Func<string, string>? casing = null) =>
        Hours(d).SelectMany(h => MisoFeeds.Regions.Select(r => RegionRow(h, (casing ?? (s => s[..1] + s[1..].ToLowerInvariant()))(r)))).ToList();

    public static List<JsonObject> FuelDay(DateOnly d) => Hours(d).SelectMany(h => MisoFeeds.Regions.Select(r => FuelRow(h, r))).ToList();

    public static List<JsonObject> MtlfDay(DateOnly d, DateOnly init) => Hours(d).SelectMany(h => MisoFeeds.MtlfZones.Select(z => MtlfRow(h, z, init))).ToList();

    public static string Envelope(IEnumerable<JsonNode> rows, int? totalElements = null, int pageNumber = 1, bool lastPage = true, int? totalPages = null)
    {
        var list = rows.Select(r => r.DeepClone()).ToList();
        var o = new JsonObject
        {
            ["data"] = new JsonArray(list.ToArray()),
            ["page"] = new JsonObject
            {
                ["pageNumber"] = pageNumber,
                ["pageSize"] = 1000,
                ["totalElements"] = totalElements ?? list.Count,
                ["totalPages"] = totalPages ?? 1,
                ["lastPage"] = lastPage,
            },
        };
        return o.ToJsonString();
    }

    /// <summary>A unit whose run day is <paramref name="age"/> days after <c>I</c> (MTLF) or <c>D</c>.</summary>
    public static MisoWorkUnit Unit(MisoFeed feed, DateOnly d, int age = 1, MisoZone zone = MisoZone.Hot, DateOnly? init = null) => new()
    {
        Feed = feed,
        MarketDate = d,
        InitDate = init,
        TodayEst = (init ?? d).AddDays(age),
        Zone = zone,
        KeyValue = $"MISO|{feed.Id}|test|{Day(d)}",
    };

    public static MisoSettings Settings(Action<MisoSettings>? configure = null)
    {
        var s = new MisoSettings { RetryDelayMs = 1, HttpTimeoutSeconds = 5, ApiKey = Key, ConnectionString = "Server=unused.invalid;Database=Misc;" };
        configure?.Invoke(s);
        return s;
    }

    public static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "live", name);
    public static string Fixture(string name) => File.ReadAllText(FixturePath(name));

    public static DateOnly D(int y, int m, int d) => new(y, m, d);
}

/// <summary>Locates repo files by walking up from the test binaries.</summary>
internal static class RepoPaths
{
    public static string Root { get; } = FindRoot();

    public static string SqlDir => Path.Combine(Root, "sql", "MISO");
    public static string Sql(string file) => Path.Combine(SqlDir, file);
    public static string AppSettings => Path.Combine(Root, "src", "DataLoader.Host", "appsettings.json");

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DataLoaderPlatform.sln")) &&
                File.Exists(Path.Combine(dir.FullName, "src", "DataLoader.Host", "appsettings.json")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException($"Could not locate the repo root from '{AppContext.BaseDirectory}'.");
    }
}

/// <summary>
/// Module-level harness: the REAL <see cref="MisoModule.RegisterServices"/> wiring (named client with
/// the real retry → limiter → per-attempt timeout → buffering chain, RemoveAllLoggers, the SEE_DB
/// resolver, the run guard), with only the primary HTTP handler, the core.Param store, core.LoadLog
/// and the four SQL sinks replaced. Pipelines are rebuilt through <see cref="MisoModule.BuildPipeline{TRow}"/>
/// with in-memory sinks, so nothing ever opens a SQL connection or leaves the machine.
/// </summary>
internal sealed class MisoHarness : IDisposable
{
    public CapturingLoggerFactory Logs { get; } = new();
    public FakeLoadLog LoadLog { get; } = new();
    public FakeSink<MisoZonalLoadHourlyRow> ZonalSink { get; } = new();
    public FakeSink<MisoLoadHourlyRow> LoadSink { get; } = new();
    public FakeSink<MisoFuelMixHourlyRow> FuelSink { get; } = new();
    public FakeSink<MisoLoadForecastMtlfRow> MtlfSink { get; } = new();
    public required StubHttpMessageHandler Data { get; init; }
    public required FakeParamStore Store { get; init; }
    public ServiceProvider Services { get; private set; } = null!;
    public MisoModule Module { get; } = new();

    public static MisoHarness Create(StubHttpMessageHandler data, Dictionary<string, string?>? config = null, FakeParamStore? store = null)
    {
        var h = new MisoHarness
        {
            Data = data,
            Store = store ?? new FakeParamStore((l, p) => l == "MISO" && p == "ApiKey" ? Samples.Key : null),
        };

        var values = new Dictionary<string, string?>
        {
            ["Loaders:MISO:ConnectionString"] = "Server=unused.invalid;Database=Misc;",
            ["Loaders:MISO:RetryDelayMs"] = "1",
            ["Loaders:MISO:HttpTimeoutSeconds"] = "5",
            ["Loaders:MISO:RequestsPerMinute"] = "100000",
            ["Loaders:MISO:MaxConcurrentWorkUnits"] = "1",
            ["Loaders:MISO:DaysBack"] = "2",
            // A future StartDate makes DaysBack alone decide the window (the last 2 days).
            ["Loaders:MISO:StartDate"] = "2099-01-01",
        };
        if (config is not null)
            foreach (var (k, v) in config) values[k] = v;

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(h.Logs);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddSingleton<ISeeDbParamStore>(h.Store);   // registered first: AddLoaderSettings uses TryAdd
        services.AddSingleton<ILoadLogRepository>(h.LoadLog);

        h.Module.RegisterServices(services, configuration);
        services.AddHttpClient(MisoModule.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => data);

        services.RemoveAll<IMisoPipeline>();
        services.AddSingleton<IMisoPipeline>(sp => MisoModule.BuildPipeline(sp, MisoFeeds.ZonalLoadHourly, new MisoZonalLoadMapper(), h.ZonalSink));
        services.AddSingleton<IMisoPipeline>(sp => MisoModule.BuildPipeline(sp, MisoFeeds.LoadHourly, new MisoLoadMapper(), h.LoadSink));
        services.AddSingleton<IMisoPipeline>(sp => MisoModule.BuildPipeline(sp, MisoFeeds.FuelMixHourly, new MisoFuelMixMapper(), h.FuelSink));
        services.AddSingleton<IMisoPipeline>(sp => MisoModule.BuildPipeline(sp, MisoFeeds.LoadForecastMtlf, new MisoLoadForecastMtlfMapper(), h.MtlfSink));

        h.Services = services.BuildServiceProvider();
        return h;
    }

    public MisoSettings Settings => Services.GetRequiredService<IOptions<MisoSettings>>().Value;
    public MisoRunGuard Guard => Services.GetRequiredService<MisoRunGuard>();

    /// <summary>A client over the real named HttpClient (real policies; stub primary).</summary>
    public MisoApiClient Client() =>
        new(Services.GetRequiredService<IHttpClientFactory>().CreateClient(MisoModule.HttpClientName), Settings, Guard, Logs.CreateLogger("MISO.Test.Http"));

    public MisoDayReader<TRow> Reader<TRow>(IMisoRowMapper<TRow> mapper) => new(Client(), mapper, Settings, Logs.CreateLogger("MISO.Test.Source"));

    public Task<LoaderRunResult> RunAsync(DateTime startedAtUtc) =>
        Module.RunAsync(Services, new LoaderRunContext
        {
            RunId = Guid.NewGuid(),
            StartedAtUtc = startedAtUtc,
            CancellationToken = CancellationToken.None,
        });

    /// <summary>Every string the run produced that could reach a log, an email or core.LoadLog — and every URL sent.</summary>
    public IEnumerable<string?> AllRunText(LoaderRunResult result) =>
        Logs.AllText()
            .Concat(LoadLog.BegunKeys)
            .Concat(LoadLog.Failures.Select(f => f.Error))
            .Concat(LoadLog.Successes.Select(s => s.Key))
            .Append(result.ErrorMessage)
            .Concat(result.FailedUnits.SelectMany(f => new[] { f.Key, f.DisplayName, f.ErrorText, f.StackTrace }))
            .Append(Guard.Reason)
            .Concat(Data.Requests.Select(r => r.Uri.ToString()));

    public void Dispose() => Services?.Dispose();
}

/// <summary>Request parsing for fixture/stub responders.</summary>
internal static class Req
{
    /// <summary>(path after /lgi/v1, query dictionary).</summary>
    public static (string Path, Dictionary<string, string> Query) Parse(Uri uri)
    {
        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => p[0], p => p.Length > 1 ? Uri.UnescapeDataString(p[1]) : "");
        return (uri.AbsolutePath, query);
    }

    public static HttpResponseMessage Ok(string json) => StubHttpMessageHandler.Json(HttpStatusCode.OK, json);
}
