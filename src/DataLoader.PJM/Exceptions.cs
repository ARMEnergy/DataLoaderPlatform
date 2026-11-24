using DataLoader.Core.Notifications;

namespace DataLoader.PJM;

// =============================================================================
// PJM exception taxonomy. 🔒 Every message here is built by the loader from fixed
// text plus already-redacted vendor text (PjmRedaction.Clean); none may ever carry
// the subscription key, the settings.json body, or an unredacted response body.
// The type decides how the run guard counts the failure (design §6.3):
//   PjmRequestException  — request-level; counts toward the consecutive trip by FailureClass
//   PjmContractException — request-level, class "contract"
//   PjmDataException     — data-level (echo, duplicate, empty read, snapshot); never counts
//   PjmRunHaltedException — the guard has tripped; never counts
// =============================================================================

/// <summary>Stable failure-class names counted by <see cref="PjmRunGuard"/>.</summary>
public static class PjmFailureClass
{
    public const string Http400 = "HTTP 400";
    public const string Http404 = "HTTP 404";
    public const string Errors = "errors";
    public const string TransientExhausted = "transient-exhausted";
    public const string Contract = "contract";
    public const string EmptyPastDay = "empty-past-day";

    /// <summary>
    /// An empty <c>load_frcstd_7_day</c> snapshot (design §14.8): never legitimate. Its own class, but
    /// it shares <see cref="EmptyPastDay"/>'s STREAK (see <see cref="StreakFamily"/>), because one
    /// snapshot unit per run could otherwise never reach the trip count on its own (design §14.12 #66:
    /// consecutive empty-snapshot or empty-past-day failures across feeds trip the guard).
    /// </summary>
    public const string EmptySnapshot = "empty-snapshot";

    public static string ForStatus(int status) => $"HTTP {status}";

    /// <summary>The run guard's streak bucket: every empty-read class counts together; others count alone.</summary>
    public static string StreakFamily(string failureClass) =>
        string.Equals(failureClass, EmptySnapshot, StringComparison.Ordinal) ? EmptyPastDay : failureClass;
}

/// <summary>A request PJM rejected or never answered successfully (status matrix, design §6.1).</summary>
public class PjmRequestException : Exception
{
    public PjmRequestException(string failureClass, int? httpStatus, string message, Exception? inner = null)
        : base(message, inner)
    {
        FailureClass = failureClass;
        HttpStatus = httpStatus;
    }

    public string FailureClass { get; }
    public int? HttpStatus { get; }
}

/// <summary>The response broke the documented contract (no <c>items</c>/<c>totalRows</c>, not JSON, a bad field shape).</summary>
public sealed class PjmContractException : PjmRequestException
{
    public PjmContractException(string message) : base(PjmFailureClass.Contract, 200, message) { }

    /// <summary>A contract failure on a non-200 status (a 3xx redirect).</summary>
    public PjmContractException(string message, int httpStatus) : base(PjmFailureClass.Contract, httpStatus, message) { }
}

/// <summary>
/// A day-specific data failure: echo, boundary, duplicate key, snapshot mismatch/shortfall, empty past day.
/// <see cref="FailureClass"/> is <c>null</c> for failures that RESET the run guard's streak (PJM
/// accepted the request), and set for those that COUNT toward it — the empty past day
/// (<see cref="PjmFailureClass.EmptyPastDay"/>), because a bad or ignored filter answering 200/empty
/// for every past day would otherwise never trip and hammer the shared pool every hour.
/// </summary>
public sealed class PjmDataException : Exception
{
    public PjmDataException(string message) : base(message) { }

    public PjmDataException(string message, string failureClass) : base(message) => FailureClass = failureClass;

    public string? FailureClass { get; }
}

/// <summary>Thrown before sending once <see cref="PjmRunGuard"/> has tripped: the unit fails without an HTTP request.</summary>
public sealed class PjmRunHaltedException : Exception
{
    public PjmRunHaltedException(string reason) : base($"PJM run halted before this request: {reason}") { }
}

/// <summary>The key could not be acquired at run start (design §5); the run fails before any unit.</summary>
public sealed class PjmKeyAcquisitionException : Exception
{
    public PjmKeyAcquisitionException(string message) : base(message) { }
}

/// <summary>Redaction helpers for any vendor text quoted into an exception or log (design §5.4).</summary>
public static class PjmRedaction
{
    /// <summary>
    /// Replaces every known key value with <c>***</c> FIRST (so truncation can never leave half a
    /// key behind), then applies the platform <see cref="ErrorTextRedactor"/> and truncates.
    /// </summary>
    public static string Clean(string? text, IEnumerable<string> knownKeys, int maxLength = 300)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var result = text;
        foreach (var key in knownKeys)
        {
            if (!string.IsNullOrEmpty(key))
                result = result.Replace(key, "***", StringComparison.Ordinal);
        }
        return ErrorTextRedactor.Clean(result, maxLength);
    }
}
