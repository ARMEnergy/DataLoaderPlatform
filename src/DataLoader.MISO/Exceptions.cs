using DataLoader.Core.Notifications;

namespace DataLoader.MISO;

// =============================================================================
// MISO exception taxonomy. 🔒 Every message is built from fixed loader text plus,
// at most, the vendor's `reason`/`message` string passed through MisoRedaction.Clean
// (the key removed, then ErrorTextRedactor, then ≤ 300 chars). Never a header, never
// a full response body, never the key (design §5).
// =============================================================================

/// <summary>A request MISO refused or never answered successfully (design §5). Not retried beyond Polly.</summary>
public class MisoRequestException : Exception
{
    public MisoRequestException(int? httpStatus, string message, Exception? inner = null) : base(message, inner) => HttpStatus = httpStatus;

    public int? HttpStatus { get; }
}

/// <summary>The response broke the documented contract (not JSON, no <c>data</c>/<c>page</c>, a redirect).</summary>
public sealed class MisoContractException : MisoRequestException
{
    public MisoContractException(string message, int? httpStatus = 200) : base(httpStatus, message) { }
}

/// <summary>A response whose data failed a reader rule (design §4): count, echo, member, duplicate, completeness, shape.</summary>
public sealed class MisoDataException : Exception
{
    public MisoDataException(string message) : base(message) { }
}

/// <summary>Thrown before sending once <see cref="MisoRunGuard"/> has tripped: the unit fails without a request.</summary>
public sealed class MisoRunHaltedException : Exception
{
    public MisoRunHaltedException(string reason) : base($"MISO run halted before this request: {reason}") { }
}

/// <summary>Redaction for any vendor text quoted into an exception or log.</summary>
public static class MisoRedaction
{
    public const int MaxReasonLength = 300;

    /// <summary>Removes the key value FIRST (truncation can never leave half a key), then the platform redactor, then truncates.</summary>
    public static string Clean(string? text, string? key, int maxLength = MaxReasonLength)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var result = text;
        if (!string.IsNullOrEmpty(key) && key.Length >= 4)
            result = result.Replace(key, "***", StringComparison.Ordinal);
        return ErrorTextRedactor.Clean(result, maxLength);
    }
}
