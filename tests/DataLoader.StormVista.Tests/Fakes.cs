using System.Net;
using System.Text;
using DataLoader.Core.Abstractions;

namespace DataLoader.StormVista.Tests;

// =============================================================================
// Test doubles. No network, no database. Everything the loader touches at run
// time (HTTP, the FileLog upsert, the reference provider, the load log) is
// substituted with an in-memory fake so tests are deterministic and offline.
// =============================================================================

/// <summary>
/// Hand-written <see cref="HttpMessageHandler"/> stub (no mocking library is
/// referenced). It records every request URI and returns a scripted response —
/// or throws a scripted exception (used for the cancellation path). The absolute
/// URI it captures still carries the <c>?apikey=</c>, so tests can prove the key
/// reaches the wire but never the sanitized log/FileLog path.
/// </summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

    public List<Uri> Requests { get; } = new();
    public int CallCount => Requests.Count;

    private StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        _responder = responder;

    /// <summary>Always returns the given status + CSV body.</summary>
    public static StubHttpMessageHandler Respond(HttpStatusCode status, string body = "") =>
        new(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/csv")
        });

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!);
        // Observe cancellation exactly as the real HttpClient would.
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_responder(request));
    }

    public HttpClient NewClient() => new(this);
}

/// <summary>Records every <see cref="IStormVistaFileLog.UpsertAsync"/> call for assertion; returns a fixed id.</summary>
internal sealed class FakeStormVistaFileLog : IStormVistaFileLog
{
    public int FileLogIdToReturn { get; set; } = 4242;

    public sealed record Call(
        StormVistaFileContext File, string Status, int? HttpStatus, string RequestPath, int RowCount);

    public List<Call> Calls { get; } = new();

    public Task<int> UpsertAsync(
        StormVistaFileContext file, string status, int? httpStatus, string requestPath, int rowCount,
        CancellationToken cancellationToken)
    {
        Calls.Add(new Call(file, status, httpStatus, requestPath, rowCount));
        return Task.FromResult(FileLogIdToReturn);
    }
}

/// <summary>Serves a preassembled <see cref="StormVistaReference"/> with no DB access.</summary>
internal sealed class FakeReferenceProvider : IStormVistaReferenceProvider
{
    private readonly StormVistaReference _reference;
    public FakeReferenceProvider(StormVistaReference reference) => _reference = reference;
    public Task<StormVistaReference> GetAsync(CancellationToken cancellationToken) => Task.FromResult(_reference);
}

/// <summary>Records the (start,end) window pairs the feed pipeline asks it to enumerate; yields no units.</summary>
internal sealed class RecordingEnumerator<TUnit> : IStormVistaUnitEnumerator<TUnit> where TUnit : WorkUnit
{
    public List<(DateOnly Start, DateOnly End)> Windows { get; } = new();

    public Task<IReadOnlyList<TUnit>> EnumerateAsync(
        DateOnly windowStart, DateOnly windowEnd, LoaderRunContext context, CancellationToken cancellationToken)
    {
        Windows.Add((windowStart, windowEnd));
        return Task.FromResult<IReadOnlyList<TUnit>>(Array.Empty<TUnit>());
    }
}

/// <summary>No-op source — only used so the pipeline can be constructed; never invoked in the empty-window tests.</summary>
internal sealed class NullSource<TUnit, TRow> : ISourceReader<TUnit, TRow> where TUnit : WorkUnit
{
    public Task<IReadOnlyList<TRow>> ReadAsync(TUnit unit, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<TRow>>(Array.Empty<TRow>());
}

/// <summary>No-op sink — only used so the pipeline can be constructed; never invoked in the empty-window tests.</summary>
internal sealed class NullSink<TRow> : ISink<TRow>
{
    public Task<int> WriteAsync(IReadOnlyList<TRow> rows, CancellationToken cancellationToken) => Task.FromResult(0);
}

/// <summary>Trivial load-log double; the empty-window pipeline tests never reach it.</summary>
internal sealed class FakeLoadLog : ILoadLogRepository
{
    public Task<LoadLogHandle?> BeginAsync(string loaderId, Guid runId, string workUnitKey, string workUnitDisplay, CancellationToken cancellationToken) =>
        Task.FromResult<LoadLogHandle?>(null);
    public Task CompleteSuccessAsync(LoadLogHandle handle, int recordsProcessed, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task CompleteFailureAsync(LoadLogHandle handle, string errorMessage, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task BeginRunAsync(Guid runId, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task CompleteRunAsync(Guid runId, bool success, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Builders for the seeded reference the loader reads at run start.</summary>
internal static class ReferenceBuilder
{
    /// <summary>
    /// A representative matrix: 3 daily models (one experimental), 2 regional models
    /// (one experimental), the four cycles with per-feed support flags, all four WDD
    /// types, and the EIA-3 + ISO region sets with their type bridges and region names.
    /// </summary>
    public static StormVistaReference Full() => new()
    {
        Models = new List<ModelRef>
        {
            new("gfs", SupportsDaily: true, SupportsRegional: false, IsExperimental: false),
            new("ecmwf", SupportsDaily: true, SupportsRegional: false, IsExperimental: false),
            new("mlr15", SupportsDaily: true, SupportsRegional: false, IsExperimental: true),
            new("ecmwf-weekly", SupportsDaily: false, SupportsRegional: true, IsExperimental: false),
            new("ai-fourcastnetv2-gfs-ens-weekly", SupportsDaily: false, SupportsRegional: true, IsExperimental: true)
        },
        Cycles = new List<CycleRef>
        {
            new("00", SupportsDaily: true, SupportsRegional: true),
            new("06", SupportsDaily: true, SupportsRegional: false),
            new("12", SupportsDaily: true, SupportsRegional: true),
            new("18", SupportsDaily: true, SupportsRegional: false)
        },
        WddTypes = new List<string> { "ew_cdd", "gw_hdd", "pw_cdd", "pw_hdd" },
        RegionSets = new List<RegionSetRef> { new("3", "EIA"), new("iso", "ISO") },
        RegionSetTypes = new Dictionary<string, IReadOnlyList<string>>
        {
            ["3"] = new[] { "ew_cdd", "gw_hdd", "pw_cdd" },
            ["iso"] = new[] { "pw_cdd", "pw_hdd" }
        },
        Regions = new Dictionary<string, IReadOnlyList<string>>
        {
            ["3"] = new[] { "West", "East", "Producing" },
            ["iso"] = new[] { "bpa", "miso", "nyiso" }
        }
    };

    /// <summary>A single daily model/cycle/type reference, for isolating the two-zone key.</summary>
    public static StormVistaReference SingleDaily() => new()
    {
        Models = new List<ModelRef> { new("gfs", true, false, false) },
        Cycles = new List<CycleRef> { new("00", true, true) },
        WddTypes = new List<string> { "ew_cdd", "gw_hdd", "pw_cdd" },
        RegionSets = new List<RegionSetRef> { new("3", "EIA"), new("iso", "ISO") },
        RegionSetTypes = new Dictionary<string, IReadOnlyList<string>>
        {
            ["3"] = new[] { "ew_cdd" },
            ["iso"] = new[] { "pw_cdd" }
        },
        Regions = new Dictionary<string, IReadOnlyList<string>>
        {
            ["3"] = new[] { "West", "East", "Producing" },
            ["iso"] = new[] { "bpa", "miso", "nyiso" }
        }
    };

    public static StormVistaReference SingleRegional() => new()
    {
        Models = new List<ModelRef> { new("ecmwf-weekly", false, true, false) },
        Cycles = new List<CycleRef> { new("00", true, true) },
        WddTypes = new List<string> { "ew_cdd", "gw_hdd", "pw_cdd", "pw_hdd" },
        RegionSets = new List<RegionSetRef> { new("iso", "ISO") },
        RegionSetTypes = new Dictionary<string, IReadOnlyList<string>> { ["iso"] = new[] { "pw_cdd" } },
        Regions = new Dictionary<string, IReadOnlyList<string>> { ["iso"] = new[] { "bpa", "miso" } }
    };
}
