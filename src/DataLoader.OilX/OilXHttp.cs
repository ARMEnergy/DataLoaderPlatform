using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Extensions.Http;

namespace DataLoader.OilX;

/// <summary>
/// HTTP concerns shared by the manifest client and the file reader: secret redaction
/// and the retry policy.
/// </summary>
internal static partial class OilXHttp
{
    /// <summary>
    /// 🔒 Strip every secret this loader can touch out of a string bound for a log or an
    /// exception message.
    ///
    /// <para>
    /// Three different secrets travel in URLs here, which is unusual — most loaders in
    /// this repo authenticate with a header and have nothing to redact:
    /// </para>
    /// <list type="bullet">
    ///   <item><c>api_key</c> — the vendor offers no header form, so it is in the query
    ///         string of every manifest request;</item>
    ///   <item><c>Signature</c> and <c>AWSAccessKeyId</c> — in every presigned S3 download
    ///         URL;</item>
    ///   <item><c>x-amz-security-token</c> — likewise, and long enough that a truncated
    ///         log line can still leak most of it.</item>
    /// </list>
    ///
    /// <para>
    /// This is belt-and-braces: the loader is written to log feed/day/file names rather
    /// than URLs, and the named <c>HttpClient</c> has its logging handlers removed. But
    /// an <see cref="HttpRequestException"/> can carry a URL in its message, so anything
    /// derived from an exception goes through here first.
    /// </para>
    /// </summary>
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return SecretParam().Replace(text, "$1=***");
    }

    /// <summary>
    /// Matches a sensitive query parameter and its value. The value run stops at
    /// <c>&amp;</c>, whitespace, a quote or a closing bracket, so surrounding log text
    /// survives intact.
    /// </summary>
    [GeneratedRegex(
        @"\b(api_key|apikey|Signature|AWSAccessKeyId|x-amz-security-token)=([^&\s""'<>\]\)]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretParam();

    /// <summary>
    /// Retry policy for the manifest and the downloads.
    ///
    /// <para>
    /// Retries transient HTTP faults and <c>429</c>, with exponential backoff. It
    /// deliberately does NOT retry <c>401</c> (a bad key will not fix itself) or
    /// <c>422</c> — which is the vendor's EMPTY-DAY answer as well as its
    /// bad-feed-name answer (<c>docs/apis/OilX.md</c> §2 Behaviour 4), and retrying
    /// either is pure waste.
    /// </para>
    /// <para>
    /// <see cref="HttpPolicyExtensions.HandleTransientHttpError"/> covers 5xx and
    /// <see cref="HttpRequestException"/>; <c>408</c> and <c>429</c> are added
    /// explicitly.
    /// </para>
    /// </summary>
    public static IAsyncPolicy<HttpResponseMessage> Build(int retryCount, int retryDelayMs, ILogger logger)
    {
        var count = Math.Max(0, retryCount);
        var delay = Math.Max(1, retryDelayMs);

        return HttpPolicyExtensions
            .HandleTransientHttpError()
            .OrResult(r => r.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests)
            .WaitAndRetryAsync(
                count,
                attempt => TimeSpan.FromMilliseconds(delay * Math.Pow(2, attempt - 1)),
                (outcome, timespan, attempt, _) =>
                {
                    // The request URI is NEVER logged here -- it carries api_key.
                    var reason = outcome.Result is not null
                        ? $"HTTP {(int)outcome.Result.StatusCode}"
                        : Redact(outcome.Exception?.Message);

                    logger.LogWarning(
                        "OilX HTTP retry {Attempt}/{Count} in {Delay}ms: {Reason}",
                        attempt, count, timespan.TotalMilliseconds, reason);
                });
    }
}
