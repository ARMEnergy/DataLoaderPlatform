using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.PJM;

/// <summary>
/// Run-wide circuit breaker (design §6.3, skill check 4a): stops burning PJM's SHARED public key
/// once a refusal is about the key rather than the request. Process singleton (one process = one
/// run). <see cref="PjmApiClient"/> checks it before EVERY request; once tripped, every remaining
/// unit fails at once with <see cref="PjmRunHaltedException"/> and sends nothing, and the next run
/// re-attempts them.
///
/// <para>Trips on: a second 401 / any 401 under an override; 403; 429 still returned after all
/// retries; and <c>ConsecutiveFailureTripCount</c> consecutive unit failures of the same
/// class (400, 404, <c>errors</c>, transient-exhausted, contract, and empty-past-day — a bad or
/// ignored filter answering 200/empty for every past day — with empty-snapshot counted in the same
/// streak as empty-past-day, design §14.7). Other data failures (echo, boundary, end offset,
/// duplicate, snapshot) do not count — and they RESET the streak, because a request that produced
/// day-specific data was by definition accepted.</para>
/// </summary>
public sealed class PjmRunGuard
{
    private readonly object _gate = new();
    private readonly int _tripCount;
    private readonly ILogger _logger;

    private string? _reason;
    private string? _streakClass;
    private int _streak;
    private readonly HashSet<string> _countedScopes = new(StringComparer.Ordinal);

    public PjmRunGuard(IOptions<PjmSettings> settings, ILogger<PjmRunGuard> logger)
        : this(settings.Value.ConsecutiveFailureTripCount, logger) { }

    internal PjmRunGuard(int consecutiveFailureTripCount, ILogger logger)
    {
        _tripCount = consecutiveFailureTripCount;
        _logger = logger;
    }

    public bool IsTripped
    {
        get { lock (_gate) return _reason is not null; }
    }

    /// <summary>The first trip reason, or <c>null</c>. Never contains a key.</summary>
    public string? Reason
    {
        get { lock (_gate) return _reason; }
    }

    /// <summary>Trips the guard. Only the first reason is kept and logged.</summary>
    public void Trip(string reason)
    {
        bool first;
        lock (_gate)
        {
            first = _reason is null;
            if (first) _reason = reason;
        }

        if (first)
            _logger.LogError("PJM run guard tripped — remaining units will fail without a request: {Reason}", reason);
    }

    /// <summary>Throws <see cref="PjmRunHaltedException"/> when tripped.</summary>
    public void ThrowIfTripped()
    {
        var reason = Reason;
        if (reason is not null) throw new PjmRunHaltedException(reason);
    }

    /// <summary>A unit's requests were accepted (success, or a day-specific data failure): resets the streak.</summary>
    public void RecordUnitAccepted()
    {
        lock (_gate)
        {
            _streak = 0;
            _streakClass = null;
            _countedScopes.Clear();
        }
    }

    /// <summary>
    /// A unit failed with a request-level failure class; trips on N consecutive of the same class.
    /// <paramref name="countOnceScope"/>, when set, makes every failure sharing that scope count ONCE
    /// toward the current streak (design §14.7, review fix): very_short's four 6-hour blocks of one
    /// empty UTC day are one finding, not four, so an 18-hour PJM gap no longer trips the guard on
    /// every run for the next ~25 days and starves the feeds after it. A repeat inside the scope
    /// neither increments nor resets the streak.
    /// </summary>
    public void RecordUnitFailure(string failureClass, string? countOnceScope = null)
    {
        if (_tripCount <= 0) return;

        // Empty-snapshot and empty-past-day share one streak (PjmFailureClass.StreakFamily).
        var family = PjmFailureClass.StreakFamily(failureClass);
        var label = string.Equals(family, failureClass, StringComparison.Ordinal) ? failureClass : $"{family}/{failureClass}";

        string? tripReason = null;
        lock (_gate)
        {
            if (string.Equals(_streakClass, family, StringComparison.Ordinal))
            {
                if (countOnceScope is not null && !_countedScopes.Add(countOnceScope))
                    return; // already counted in this streak
                _streak++;
            }
            else
            {
                _streakClass = family;
                _streak = 1;
                _countedScopes.Clear();
                if (countOnceScope is not null) _countedScopes.Add(countOnceScope);
            }

            if (_streak >= _tripCount)
                tripReason =
                    $"{_streak} consecutive PJM work units failed with the same failure class ({label}); " +
                    "this looks deterministic rather than transient, so the run stops instead of repeating it";
        }

        if (tripReason is not null) Trip(tripReason);
    }
}
