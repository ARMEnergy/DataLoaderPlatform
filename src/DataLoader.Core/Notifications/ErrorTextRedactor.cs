using System.Text.RegularExpressions;

namespace DataLoader.Core.Notifications;

/// <summary>
/// Removes secrets from vendor-supplied text before it can be emailed.
///
/// <para>⚠ This is NOT belt-and-braces. Several vendors and loaders on this
/// platform put credentials into text that reaches
/// <c>LoaderRunResult.ErrorMessage</c>: AGSI echoes the caller's own
/// <c>x-key</c> back inside the error body's <c>message</c> field, OilX
/// carries its <c>api_key</c> in the query string so any URL inside an
/// exception is sensitive, and IIR/NUCORE authenticate with JWT bearer
/// tokens that can land in an HTTP exception's message or headers dump.</para>
///
/// <para>Logs stay on the machine; email does not. Every piece of error text
/// must pass through here before it enters a <see cref="NotificationMessage"/>.</para>
///
/// <para>Deliberately conservative in the other direction too: over-redacting
/// produces alerts nobody can act on, which is its own failure. Only known
/// key-bearing tokens, URL query strings, and bearer-token shapes are
/// touched.</para>
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
    /// Masks the value after an UNAMBIGUOUS credential name — one that is
    /// never ordinary English prose, so any shape it appears in is worth
    /// redacting. Covers both bare <c>name=value</c>/<c>name: value</c> (the
    /// AGSI "API key: ..." prose shape) AND the quoted JSON field shape
    /// (<c>"name":"value"</c>), via an optional quote on either side of the
    /// separator — vendor error bodies are JSON, and the quote between the
    /// field name and the colon is exactly what an earlier version of this
    /// pattern could not cross: <c>{"api_key":"..."}</c> passed through
    /// completely untouched until this was added.
    ///
    /// <para>Spelling variants fold into one alternative
    /// (<c>api[\s_-]?key</c> matches apikey/api_key/api-key/"API key")
    /// instead of relying on alternation order to pick the right one — the
    /// mechanism that actually resolves a conflict is leftmost-match at the
    /// leftmost starting position plus the <c>\b</c> boundaries, not which
    /// alternative happens to be listed first.</para>
    /// </summary>
    private static readonly Regex SpecificKeyValue = new(
        @"\b(client_secret|api[\s_-]?key|x-key|access[_-]?token|auth[_-]?token|secret|password|passwd|pwd)\b""?\s*[:=]\s*""?(?:[^\s""'&,;)\]}]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Masks bare <c>key</c>/<c>token</c> — but ONLY in the two shapes that
    /// cannot be ordinary prose: <c>name=value</c> (query/connection-string
    /// syntax) and <c>"name":"value"</c> (a quoted JSON field name). Both
    /// words are everyday English, and this platform's own diagnostics use
    /// them constantly without meaning "credential": .NET's own
    /// <c>Dictionary.Add</c> duplicate-key exception reads "...has already
    /// been added. Key: 5", and this platform has had real duplicate-key
    /// incidents (NUCORE PositionDetails/PnlExplain — see CLAUDE.md) where
    /// that trailing value is the one thing the alert needs to carry. A
    /// free-form <c>key: value</c> or <c>token: value</c> prose colon is
    /// therefore left alone; only the two unambiguous shapes above redact.
    ///
    /// <para>.NET allows the same named group across different alternation
    /// branches, so <c>k</c> resolves to whichever branch actually matched.</para>
    /// </summary>
    private static readonly Regex GenericKeyValue = new(
        @"(?:\b(?<k>key|token)\b\s*=\s*""?|""(?<k>key|token)""\s*:\s*""?)(?:[^\s""'&,;)\]}]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Masks a JWT/opaque credential after the word "bearer". IIR and NUCORE
    /// both authenticate with bearer tokens, and that shape has no
    /// <c>name=value</c>/<c>name: value</c> form for the other two patterns
    /// to catch — it is just the literal word followed by the token.
    ///
    /// <para>"Bearer" alone is not enough of a signal: ordinary prose and
    /// error text use it too — "Bearer auth-header is missing" and "Bearer
    /// realm.example.com rejected the request" are both real text this must
    /// NOT touch. A hyphen or dot is not sufficient evidence either, since
    /// hyphenated words and hostnames both have them. The discriminator is a
    /// run of at least 12 base64url-ish characters that contains at least
    /// one DIGIT — real tokens are long and carry digits; English words and
    /// hostnames generally do not.</para>
    /// </summary>
    private static readonly Regex BearerToken = new(
        @"\b(bearer)\s+(?=[A-Za-z0-9._~+/=\-]*[0-9])[A-Za-z0-9._~+/=\-]{12,}",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Returns <paramref name="text"/> with secrets masked. Null becomes empty.</summary>
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var result = UrlQuery.Replace(text, "$1");
        result = SpecificKeyValue.Replace(result, m => $"{m.Groups[1].Value}={Mask}");
        result = GenericKeyValue.Replace(result, m => $"{m.Groups["k"].Value}={Mask}");
        result = BearerToken.Replace(result, m => $"{m.Groups[1].Value} {Mask}");
        return result;
    }

    /// <summary>
    /// Caps length so one pathological exception cannot produce a
    /// multi-megabyte email. Null or a non-positive <paramref name="maxLength"/>
    /// becomes empty — there is no room for content, so there is nothing
    /// useful left to say either.
    ///
    /// <para>⚠ This does NOT redact. It only shortens, so calling it alone on
    /// vendor-supplied text still ships whatever secrets were in it — this is
    /// why <see cref="Redact"/> always runs first inside <see cref="Clean"/>,
    /// which is what almost every caller actually wants.</para>
    /// </summary>
    public static string Truncate(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        if (maxLength <= 0) return string.Empty;
        if (text.Length <= maxLength) return text;

        // Back off one char if the cut would split a surrogate pair in two,
        // which would leave a lone (invalid) high surrogate at the end.
        var cut = maxLength;
        if (char.IsHighSurrogate(text[cut - 1])) cut--;

        return text[..cut] + $"… [truncated, {text.Length} chars total]";
    }

    /// <summary>Redact then truncate — the combination every caller actually wants.</summary>
    public static string Clean(string? text, int maxLength = 2000) =>
        Truncate(Redact(text), maxLength);
}
