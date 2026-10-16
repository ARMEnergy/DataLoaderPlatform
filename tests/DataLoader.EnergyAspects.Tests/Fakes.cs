using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;

namespace DataLoader.EnergyAspects.Tests;

// =============================================================================
// Test doubles. No network, no database. Mirrors the AGSI/CWG test posture.
// =============================================================================

/// <summary>
/// Hand-written <see cref="HttpMessageHandler"/> stub (no mocking library is
/// referenced). Records every request URI and returns a scripted status + body.
/// </summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

    public List<Uri> Requests { get; } = new();

    private StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        _responder = responder;

    public static StubHttpMessageHandler Respond(HttpStatusCode status, string body = "") =>
        new(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        });

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_responder(request));
    }

    public HttpClient NewClient() =>
        new(this) { BaseAddress = new Uri("https://api.energyaspects.test") };
}

/// <summary>Records every API response code the source reports, as the sink would.</summary>
internal sealed class FakeApiLogger : IEnergyAspectsApiLogger
{
    public List<(int MappingId, int ResponseCode)> Calls { get; } = new();

    public Task LogApiResponseAsync(int mappingId, int responseCode, CancellationToken ct)
    {
        Calls.Add((mappingId, responseCode));
        return Task.CompletedTask;
    }
}

/// <summary>Captures log entries so tests can assert on level and message.</summary>
internal sealed class ListLogger<T> : ILogger<T>
{
    public sealed record Entry(LogLevel Level, string Message);

    public ConcurrentQueue<Entry> Entries { get; } = new();

    public IEnumerable<Entry> OfLevel(LogLevel level) => Entries.Where(e => e.Level == level);

    IDisposable? ILogger.BeginScope<TState>(TState state) => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        Entries.Enqueue(new Entry(logLevel, formatter(state, exception)));
}
