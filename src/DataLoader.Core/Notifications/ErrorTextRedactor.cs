using System.Text.RegularExpressions;

namespace DataLoader.Core.Notifications;

/// <summary>
/// Removes secrets from vendor-supplied text before it can be emailed.
///
/// <para>⚠ This is NOT belt-and-braces. Two vendors on this platform put
/// credentials into text that reaches <c>LoaderRunResult.ErrorMessage</c>:
/// AGSI echoes the caller's own <c>x-key</c> back inside the error body's
/// <c>message</c> field, and OilX carries its <c>api_key</c> in the query
/// string, so any URL inside an exception is sensitive.</para>
///
/// <para>Logs stay on the machine; email does not. Every piece of error text
/// must pass through here before it enters a <see cref="NotificationMessage"/>.</para>
///
/// <para>Deliberately conservative in the other direction too: over-redacting
/// produces alerts nobody can act on, which is its own failure. Only known
/// key-bearing tokens and URL query strings are touched.</para>
/// </summary>
public static class ErrorTextRedactor
{
    private const string Mask = "***REDACTED***";

    /// <summary>
    /// Strips the query string off any URL. OilX puts its api_key there, and a
    /// query string in an error message is never worth the risk of keeping.
    /// The character class stops at whitespace (and quote/paren delimiters) so
    /// the URL itself survives and trailing prose after the query string is
    /// never swallowed into the match.
    /// </summary>
    private static readonly Regex UrlQuery = new(
        @"(https?://[^\s?""']+)\?[^\s""']*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Masks the value after a known key-bearing token. The separator covers
    /// both <c>name=value</c> (query/connection-string style) and
    /// <c>name: value</c> (the AGSI "API key: ..." prose shape). Longer
    /// alternatives come first so <c>api_key</c> is not matched as bare
    /// <c>key</c> with an <c>api_</c> prefix left dangling.
    /// </summary>
    private static readonly Regex KeyValue = new(
        @"\b(client_secret|api[\s_-]?key|x-key|apikey|secret|password|passwd|pwd|token|key)\b\s*[:=]\s*""?([^\s""'&,;)\]]+)""?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Returns <paramref name="text"/> with secrets masked. Null becomes empty.</summary>
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var result = UrlQuery.Replace(text, "$1");
        result = KeyValue.Replace(result, m => $"{m.Groups[1].Value}={Mask}");
        return result;
    }

    /// <summary>
    /// Caps length so one pathological exception cannot produce a
    /// multi-megabyte email. Null becomes empty.
    /// </summary>
    public static string Truncate(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        if (text.Length <= maxLength) return text;
        return text[..maxLength] + $"… [truncated, {text.Length} chars total]";
    }

    /// <summary>Redact then truncate — the combination every caller actually wants.</summary>
    public static string Clean(string? text, int maxLength = 2000) =>
        Truncate(Redact(text), maxLength);
}
