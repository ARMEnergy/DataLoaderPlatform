using System.Text;

namespace DataLoader.Core.Notifications;

/// <summary>
/// Trims framework noise out of an exception's <c>ToString()</c> so a failure
/// email shows the loader's own call path instead of pages of
/// <c>System.*</c>/<c>Microsoft.*</c>/<c>Polly.*</c> plumbing.
///
/// <para>This is a SEPARATE file from <see cref="ErrorTextRedactor"/> on
/// purpose. Trimming frames is formatting — it only changes how much of a
/// trusted-shape string (an exception's own <c>ToString()</c>) is shown.
/// Redaction is a security boundary — it has to catch secrets wherever a
/// vendor put them in free-form text. Conflating the two would make the
/// security boundary harder to review in isolation, so callers are expected
/// to run <see cref="Format"/> first and then <see cref="ErrorTextRedactor.Redact"/>
/// on its output, never the other way round and never merged into one
/// method.</para>
/// </summary>
public static class StackTraceFormatter
{
    private const int MaxChars = 2000;

    private static readonly string[] NoisyPrefixes = { "System.", "Microsoft.", "Polly." };

    /// <summary>
    /// Formats <paramref name="stackTrace"/> (typically an exception's
    /// <c>ToString()</c>, which includes the exception type, message, frames,
    /// and — for an <see cref="Exception.InnerException"/> chain — the
    /// <c>---&gt; </c> separators and their own type/message/frames) by
    /// dropping frames ("   at ...") whose declaring type starts with a noisy
    /// prefix, while always keeping every non-frame line (exception type and
    /// message lines, inner-exception separators and their messages).
    ///
    /// <para>If every frame would be dropped, the untrimmed text is returned
    /// instead — a stack trace with every frame removed looks like the
    /// formatter is broken, and the untrimmed version is still readable.</para>
    ///
    /// <para>Capped at 2000 characters either way.</para>
    /// </summary>
    public static string Format(string? stackTrace)
    {
        if (string.IsNullOrEmpty(stackTrace))
            return string.Empty;

        var lines = stackTrace.Replace("\r\n", "\n").Split('\n');

        var kept = new List<string>(lines.Length);
        var anyFrameSeen = false;
        var anyFrameKept = false;

        foreach (var line in lines)
        {
            var trimmedStart = line.TrimStart();
            var isFrame = trimmedStart.StartsWith("at ", StringComparison.Ordinal);

            if (!isFrame)
            {
                // Exception type/message lines and "---> " inner-exception
                // separators (and their own message lines) are never frames —
                // always keep them.
                kept.Add(line);
                continue;
            }

            anyFrameSeen = true;

            var frameType = trimmedStart[3..]; // text after "at "
            var isNoisy = NoisyPrefixes.Any(p => frameType.StartsWith(p, StringComparison.Ordinal));
            if (!isNoisy)
            {
                kept.Add(line);
                anyFrameKept = true;
            }
        }

        // Every frame dropped (and there was at least one to begin with):
        // fall back to the untrimmed text rather than emitting a trace with
        // no frames at all.
        var result = (anyFrameSeen && !anyFrameKept)
            ? stackTrace
            : string.Join(Environment.NewLine, kept);

        return result.Length <= MaxChars
            ? result
            : result[..MaxChars];
    }
}
