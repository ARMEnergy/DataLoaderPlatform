using System.Globalization;
using System.Net;
using System.Text.Json;
using DataLoader.Core.Abstractions;
using DataLoader.Core.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DataLoader.PJM.Tests;

/// <summary>
/// Synthetic PJM payloads. Shapes follow docs/apis/PJM.md §4 (envelope <c>items</c> +
/// <c>totalRows</c>, snake_case fields, offset-free ISO timestamps). No real data.
/// </summary>
internal static class Samples
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Iso(DateTime dt) => dt.ToString("yyyy-MM-ddTHH:mm:ss", Inv);

    /// <summary>
    /// Eastern prevailing time computed from HARD-CODED 2025/2026 DST transition instants —
    /// deliberately independent of <see cref="PjmTime"/>, so the EPT echo check is tested
    /// against an oracle rather than against itself.
    /// </summary>
    public static DateTime Ept(DateTime utc)
    {
        var dst =
            (utc >= new DateTime(2025, 3, 9, 7, 0, 0) && utc < new DateTime(2025, 11, 2, 6, 0, 0)) ||
            (utc >= new DateTime(2026, 3, 8, 7, 0, 0) && utc < new DateTime(2026, 11, 1, 6, 0, 0));
        if (utc.Year is < 2025 or > 2026) throw new ArgumentOutOfRangeException(nameof(utc), "oracle covers 2025-2026 only");
        return utc.AddHours(dst ? -4 : -5);
    }

    public static JsonElement Element(object obj) =>
        JsonDocument.Parse(JsonSerializer.Serialize(obj)).RootElement.Clone();

    public static JsonElement ElementFromJson(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>A gen_by_fuel item. Pass <paramref name="fields"/> to override/remove (value <c>Remove</c>) any field.</summary>
    public static JsonElement Fuel(DateTime utc, string fuel = "Gas", double mw = 1234.5, Dictionary<string, object?>? fields = null)
    {
        var d = new Dictionary<string, object?>
        {
            ["datetime_beginning_utc"] = Iso(utc),
            ["datetime_beginning_ept"] = Iso(Ept(utc)),
            ["fuel_type"] = fuel,
            ["mw"] = mw,
            ["is_renewable"] = false,
        };
        return Element(Apply(d, fields));
    }

    /// <summary>An inst_load item.</summary>
    public static JsonElement Load(DateTime utc, string area = "PJM RTO", double load = 90000.25, Dictionary<string, object?>? fields = null)
    {
        var d = new Dictionary<string, object?>
        {
            ["datetime_beginning_utc"] = Iso(utc),
            ["datetime_beginning_ept"] = Iso(Ept(utc)),
            ["area"] = area,
            ["instantaneous_load"] = load,
        };
        return Element(Apply(d, fields));
    }

    public static readonly object Remove = new();

    private static Dictionary<string, object?> Apply(Dictionary<string, object?> d, Dictionary<string, object?>? fields)
    {
        if (fields is null) return d;
        foreach (var (k, v) in fields)
        {
            if (ReferenceEquals(v, Remove)) d.Remove(k);
            else d[k] = v;
        }
        return d;
    }

    /// <summary>24 hourly gen_by_fuel items for one UTC day.</summary>
    public static List<JsonElement> FuelDay(DateOnly day, string fuel = "Gas", int hours = 24) =>
        Enumerable.Range(0, hours).Select(h => Fuel(day.ToDateTime(new TimeOnly(h, 0)), fuel)).ToList();

    /// <summary>288 five-minute inst_load items for one UTC day and area (optionally fewer).</summary>
    public static List<JsonElement> LoadDay(DateOnly day, string area = "PJM RTO", int intervals = 288) =>
        Enumerable.Range(0, intervals).Select(i => Load(day.ToDateTime(TimeOnly.MinValue).AddMinutes(5 * i), area)).ToList();

    /// <summary>The documented envelope, including a <c>links</c> array the loader must ignore.</summary>
    public static string Envelope(IEnumerable<JsonElement> items, int? totalRows = null, string totalRowsName = "totalRows",
        string? nextLink = "https://api.pjm.com/api/v1/elsewhere?startRow=999")
    {
        var list = items.ToList();
        var parts = new List<string>
        {
            "\"items\":[" + string.Join(",", list.Select(i => i.GetRawText())) + "]",
            $"\"{totalRowsName}\":{(totalRows ?? list.Count).ToString(Inv)}",
        };
        if (nextLink is not null)
            parts.Add("\"links\":[{\"rel\":\"next\",\"href\":\"" + nextLink + "\"}]");
        return "{" + string.Join(",", parts) + "}";
    }

    public static string SettingsJson(string key, string? baseUrl = "https://api.pjm.com/api/v1") =>
        baseUrl is null
            ? $"{{\"subscriptionKey\":\"{key}\"}}"
            : $"{{\"subscriptionKey\":\"{key}\",\"baseUrl\":\"{baseUrl}\"}}";

    /// <summary>Feed name and UTC day a data request asked for (from its path and filter).</summary>
    public static (string Feed, DateOnly Day, int StartRow) Parse(Uri uri)
    {
        var feed = uri.AbsolutePath.TrimEnd('/').Split('/').Last();
        var query = uri.Query.TrimStart('?').Split('&')
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => p[0], p => p.Length > 1 ? Uri.UnescapeDataString(p[1]) : "");
        var day = DateOnly.ParseExact(query["datetime_beginning_utc"][..10], "MM/dd/yyyy", Inv);
        return (feed, day, int.Parse(query["startRow"], Inv));
    }

    /// <summary>A data responder that answers every request with one valid row for the requested feed/day.</summary>
    public static HttpResponseMessage OneRowFor(HttpRequestMessage request)
    {
        var (feed, day, _) = Parse(request.RequestUri!);
        var utc = day.ToDateTime(new TimeOnly(0, 0));
        var item = feed == PjmFeeds.InstLoadName ? Load(utc) : Fuel(utc);
        return StubHttpMessageHandler.Json(HttpStatusCode.OK, Envelope(new[] { item }, nextLink: null));
    }

    public static PjmSettings Settings(Action<PjmSettings>? configure = null)
    {
        var s = new PjmSettings { RetryDelayMs = 1, HttpTimeoutSeconds = 5, ConnectionString = "Server=unused.invalid;Database=Misc;" };
        configure?.Invoke(s);
        return s;
    }

    /// <summary>A unit for <paramref name="feed"/> at <paramref name="age"/> days before a fixed run day.</summary>
    public static PjmDayWorkUnit Unit(PjmFeed feed, DateOnly day, int age = 1, PjmZone zone = PjmZone.Hot) => new()
    {
        Feed = feed,
        Day = day,
        RunDay = day.AddDays(age),
        Zone = zone,
        KeyValue = $"pjm:{feed.Name}:{day:yyyyMMdd}",
    };

    public static DateTime Started { get; } = new(2026, 11, 23, 14, 20, 0, DateTimeKind.Utc);
}

/// <summary>Locates repo files by walking up from the test binaries.</summary>
internal static class RepoPaths
{
    public static string Root { get; } = FindRoot();

    public static string SqlDir => Path.Combine(Root, "sql", "PJM");
    public static string Sql(string file) => Path.Combine(SqlDir, file);
    public static string AppSettings => Path.Combine(Root, "src", "DataLoader.Host", "appsettings.json");

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "sql", "PJM")) &&
                File.Exists(Path.Combine(dir.FullName, "src", "DataLoader.Host", "appsettings.json")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException($"Could not locate the repo root from '{AppContext.BaseDirectory}'.");
    }
}

/// <summary>
/// Module-level harness: the REAL <see cref="PjmModule.RegisterServices"/> wiring (named clients
/// with the real Polly policy, the real limiter handler, RemoveAllLoggers, the real key provider and
/// run guard), with only the primary HTTP handlers, the core.Param store, core.LoadLog and the two
/// SQL sinks replaced. The pipelines are rebuilt exactly as the module's private factory does, but
/// with in-memory sinks, so nothing ever opens a SQL connection.
/// </summary>
internal sealed class PjmHarness : IDisposable
{
    public CapturingLoggerFactory Logs { get; } = new();
    public FakeLoadLog LoadLog { get; } = new();
    public FakeSink<PjmLoadRow> LoadSink { get; } = new();
    public FakeSink<PjmFuelMixRow> FuelSink { get; } = new();
    public required StubHttpMessageHandler Data { get; init; }
    public required StubHttpMessageHandler SettingsHttp { get; init; }
    public required FakeParamStore Store { get; init; }
    public ServiceProvider Services { get; private set; } = null!;
    public PjmModule Module { get; } = new();

    public static PjmHarness Create(
        StubHttpMessageHandler data,
        StubHttpMessageHandler settings,
        FakeParamStore? store = null,
        Dictionary<string, string?>? config = null,
        Action<PjmSettings>? configure = null)
    {
        var h = new PjmHarness { Data = data, SettingsHttp = settings, Store = store ?? FakeParamStore.Returning(null) };

        var values = new Dictionary<string, string?>
        {
            ["Loaders:PJM:ConnectionString"] = "Server=unused.invalid;Database=Misc;",
            ["Loaders:PJM:RetryDelayMs"] = "1",
            ["Loaders:PJM:HttpTimeoutSeconds"] = "5",
            ["Loaders:PJM:SettingsFetchTimeoutSeconds"] = "5",
            ["Loaders:PJM:RequestsPerMinute"] = "100000",
            ["Loaders:PJM:MaxConcurrentWorkUnits"] = "1",
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

        services.AddHttpClient(PjmModule.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => data);
        services.AddHttpClient(PjmModule.SettingsHttpClientName).ConfigurePrimaryHttpMessageHandler(() => settings);
        if (configure is not null) services.Configure(configure);

        services.RemoveAll<IPjmPipeline>();
        services.AddSingleton<IPjmPipeline>(sp => Build(sp, PjmFeeds.InstLoad, new PjmLoadRowMapper(),
            (r, g) => new PjmLoadSourceReader(r, g), h.LoadSink));
        services.AddSingleton<IPjmPipeline>(sp => Build(sp, PjmFeeds.GenByFuel, new PjmFuelMixRowMapper(),
            (r, g) => new PjmFuelMixSourceReader(r, g), h.FuelSink));

        h.Services = services.BuildServiceProvider();
        return h;
    }

    private static IPjmPipeline Build<TRow>(
        IServiceProvider sp, Func<PjmSettings, PjmFeed> feedFactory, IPjmRowMapper<TRow> mapper,
        Func<PjmDayReader<TRow>, PjmRunGuard, ISourceReader<PjmDayWorkUnit, TRow>> sourceFactory, ISink<TRow> sink)
        where TRow : IPjmRow
    {
        var settings = sp.GetRequiredService<IOptions<PjmSettings>>().Value;
        var lf = sp.GetRequiredService<ILoggerFactory>();
        var guard = sp.GetRequiredService<PjmRunGuard>();
        var feed = feedFactory(settings);
        var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient(PjmModule.HttpClientName);
        var client = new PjmApiClient(http, settings, sp.GetRequiredService<IPjmKeyProvider>(), guard, lf.CreateLogger($"PJM.{feed.Name}.Http"));
        var reader = new PjmDayReader<TRow>(client, mapper, settings, lf.CreateLogger($"PJM.{feed.Name}.Source"));
        var provider = new PjmWorkUnitProvider(feed, settings, lf.CreateLogger($"PJM.{feed.Name}.Provider"));
        return new PjmPipeline<TRow>(feed.Name, provider, sourceFactory(reader, guard), sink, sp.GetRequiredService<ILoadLogRepository>(),
            settings, lf.CreateLogger($"PJM.{feed.Name}.Pipeline"));
    }

    public Task<LoaderRunResult> RunAsync(DateTime? startedAtUtc = null) =>
        Module.RunAsync(Services, new LoaderRunContext
        {
            RunId = Guid.NewGuid(),
            StartedAtUtc = startedAtUtc ?? Samples.Started,
            CancellationToken = CancellationToken.None,
        });

    public PjmRunGuard Guard => Services.GetRequiredService<PjmRunGuard>();

    /// <summary>Every string the run produced that could reach a log, an email or core.LoadLog.</summary>
    public IEnumerable<string?> AllRunText(LoaderRunResult result) =>
        Logs.AllText()
            .Concat(LoadLog.BegunKeys)
            .Concat(LoadLog.Failures.Select(f => f.Error))
            .Concat(LoadLog.Successes.Select(s => s.Key))
            .Append(result.ErrorMessage)
            .Concat(result.FailedUnits.SelectMany(f => new[] { f.Key, f.DisplayName, f.ErrorText, f.StackTrace }))
            .Append(Guard.Reason)
            .Concat(Data.Requests.Select(r => r.Uri.ToString()))
            .Concat(SettingsHttp.Requests.Select(r => r.Uri.ToString()));

    public void Dispose() => Services?.Dispose();
}

/// <summary>
/// Client-level builder over the REAL <see cref="PjmModule.RegisterServices"/> HTTP wiring
/// (retry → limiter → per-attempt timeout → buffering, SingleAttempt re-send, no redirects). Only the
/// settings instance, the limiter (rate + clock), the run guard and — unless <paramref name="stub"/>
/// is null — the primary handler are replaced. With a null stub the module's own primary
/// (<c>SocketsHttpHandler { AllowAutoRedirect = false }</c>) is used, against a loopback test server.
/// </summary>
internal static class ClientFactory
{
    public static (PjmApiClient Client, PjmRunGuard Guard, PjmRateLimiter Limiter, CapturingLoggerFactory Logs) Build(
        StubHttpMessageHandler? stub, IPjmKeyProvider keys, PjmSettings? settings = null, int tripCount = 3,
        TimeProvider? time = null, int requestsPerMinute = 100000)
    {
        var sp = Services(stub, settingsStub: null, settings, tripCount, time, requestsPerMinute, out var logs);
        settings = sp.GetRequiredService<IOptions<PjmSettings>>().Value;
        var guard = sp.GetRequiredService<PjmRunGuard>();
        var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient(PjmModule.HttpClientName);
        return (new PjmApiClient(http, settings, keys, guard, logs.CreateLogger("PJM.Client")), guard,
            sp.GetRequiredService<PjmRateLimiter>(), logs);
    }

    /// <summary>The module's settings.json named client (optionally over a stub primary).</summary>
    public static HttpClient SettingsClient(StubHttpMessageHandler? stub, PjmSettings settings) =>
        Services(null, stub, settings, 3, null, 100000, out _)
            .GetRequiredService<IHttpClientFactory>().CreateClient(PjmModule.SettingsHttpClientName);

    private static readonly List<ServiceProvider> Keep = new();   // providers live for the test process

    private static ServiceProvider Services(StubHttpMessageHandler? stub, StubHttpMessageHandler? settingsStub, PjmSettings? settings,
        int tripCount, TimeProvider? time, int rpm, out CapturingLoggerFactory logs)
    {
        settings ??= Samples.Settings();
        logs = new CapturingLoggerFactory();
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(logs);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddSingleton<ISeeDbParamStore>(FakeParamStore.Returning(null));
        services.AddSingleton<ILoadLogRepository>(new FakeLoadLog());
        new PjmModule().RegisterServices(services, new ConfigurationBuilder().Build());
        services.AddSingleton<IOptions<PjmSettings>>(Options.Create(settings));
        var guardLogger = logs.CreateLogger("PJM.Guard");
        services.AddSingleton(new PjmRunGuard(tripCount, guardLogger));
        services.AddSingleton(new PjmRateLimiter(rpm, time ?? TimeProvider.System));
        if (stub is not null)
            services.AddHttpClient(PjmModule.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => stub);
        if (settingsStub is not null)
            services.AddHttpClient(PjmModule.SettingsHttpClientName).ConfigurePrimaryHttpMessageHandler(() => settingsStub);
        var sp = services.BuildServiceProvider();
        lock (Keep) Keep.Add(sp);
        return sp;
    }

    public static PjmDayWorkUnit GenUnit(DateOnly day, int age = 1) => Samples.Unit(PjmFeeds.GenByFuel(Samples.Settings()), day, age);
    public static PjmDayWorkUnit LoadUnit(DateOnly day, int age = 1) => Samples.Unit(PjmFeeds.InstLoad(Samples.Settings()), day, age);

    public static NullLogger Null => NullLogger.Instance;
}
