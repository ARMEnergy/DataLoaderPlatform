using Microsoft.Extensions.Logging;

namespace DataLoader.MISO;

/// <summary>
/// Run-wide stop (design §5): a 401 or 403 is about the KEY, not the request, so every remaining
/// unit would be refused the same way. Once tripped, <see cref="MisoApiClient"/> sends nothing
/// more AND <see cref="Token"/> is cancelled: the module runs every pipeline under a token linked to
/// it, so units still waiting for a slot are never begun (no <c>core.LoadLog</c> row each), later
/// pipelines are not started, and the run ends with ONE aggregate failure. The next run re-attempts
/// everything. Process singleton (one process = one run).
/// </summary>
public sealed class MisoRunGuard : IDisposable
{
    private readonly object _gate = new();
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private string? _reason;

    public MisoRunGuard(ILogger<MisoRunGuard> logger) : this((ILogger)logger) { }

    internal MisoRunGuard(ILogger logger) => _logger = logger;

    public bool IsTripped { get { lock (_gate) return _reason is not null; } }

    /// <summary>The first trip reason, or <c>null</c>. Never contains the key.</summary>
    public string? Reason { get { lock (_gate) return _reason; } }

    /// <summary>Cancelled when the guard trips.</summary>
    public CancellationToken Token => _cts.Token;

    public void Trip(string reason)
    {
        bool first;
        lock (_gate)
        {
            first = _reason is null;
            if (first) _reason = reason;
        }

        if (!first) return;
        _logger.LogError("MISO run guard tripped; no further units will be started: {Reason}", reason);
        try { _cts.Cancel(); }
        catch (AggregateException) { /* a cancellation callback threw; the trip itself stands */ }
    }

    public void ThrowIfTripped()
    {
        if (Reason is { } reason) throw new MisoRunHaltedException(reason);
    }

    public void Dispose() => _cts.Dispose();
}
