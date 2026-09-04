using System.Text;

namespace DataLoader.ICE;

/// <summary>What a downloaded body actually is.</summary>
public enum IceResponseKind
{
    /// <summary>A real file. Parse it. May still legitimately contain zero data rows.</summary>
    Data,

    /// <summary>
    /// No file exists for this feed/date — a weekend, a holiday, a future date, or a
    /// feed that did not exist yet. NOT an error: zero rows, run continues.
    /// </summary>
    NotAvailable,

    /// <summary>The SSO login page came back instead of the file. Re-authenticate and retry.</summary>
    AuthExpired,

    /// <summary>Text that is neither a sentinel page nor a file with the expected header. Fail loudly.</summary>
    Malformed
}

/// <summary>
/// ⚠ <b>The correctness centrepiece of this loader.</b>
///
/// <para>
/// <c>downloads.ice.com</c> answers <b>HTTP 200 for everything</b> — real data, a
/// missing file, and expired authentication alike. It never returns 404 and never
/// returns a 4xx/5xx for any of them (verified live 2026-09-01,
/// <c>docs/apis/ICE.md</c> 2). The status code carries no information, so the
/// outcome has to be read out of the body.
/// </para>
/// <para>
/// Getting this wrong is silent data loss in both directions: trusting the status
/// code would parse a 33 KB HTML login page as a data file, or record a clean
/// "0 rows, success" for an authentication failure and then skip that date forever
/// once its resume key settles.
/// </para>
/// <para>
/// The four outcomes and how they are told apart:
/// <list type="table">
///   <item><term>NotAvailable</term><description>HTML containing <c>Index of</c> AND <c>No Files Available</c> (~985 bytes)</description></item>
///   <item><term>AuthExpired</term><description>HTML containing <c>ICE SSO Client</c> (~33 KB)</description></item>
///   <item><term>Data</term><description>first line matches the feed's expected header, or the XLSX zip magic</description></item>
///   <item><term>Malformed</term><description>anything else — a changed header fails the file rather than loading it shifted</description></item>
/// </list>
/// </para>
/// </summary>
public static class IceResponseClassifier
{
    /// <summary>Marker in the "no file for this date" directory-index page.</summary>
    internal const string NoFilesMarker = "No Files Available";

    /// <summary>Secondary marker in the same page — its title and h1 are both "Index of /path".</summary>
    internal const string IndexOfMarker = "Index of";

    /// <summary>Marker in the SSO login page returned when the token is missing or stale.</summary>
    internal const string SsoMarker = "ICE SSO Client";

    /// <summary>OOXML (and every zip) starts with these four bytes.</summary>
    private static readonly byte[] ZipMagic = { 0x50, 0x4B, 0x03, 0x04 };

    /// <summary>
    /// Classify raw downloaded bytes for a feed.
    ///
    /// <para>
    /// Sentinel detection runs BEFORE any format-specific handling, so an HTML page
    /// served in place of an XLSX is caught rather than handed to the zip reader.
    /// </para>
    /// </summary>
    /// <param name="content">The raw response body.</param>
    /// <param name="feed">The feed, whose table supplies the headers a data file must carry.</param>
    /// <param name="detail">Human-readable reason, for the log and <c>arm.FileLog.ErrorMessage</c>.</param>
    public static IceResponseKind Classify(byte[] content, IceFeedDescriptor feed, out string detail)
    {
        if (content.Length == 0)
        {
            // A zero-byte body is not a legitimate empty file: even an empty feed
            // ships its header row (the 178-byte crude-index trades file).
            detail = "Empty response body";
            return IceResponseKind.Malformed;
        }

        // Peek at the head as text for sentinel detection. Latin-1 never throws on
        // arbitrary bytes, so binary content cannot blow up the sniff.
        var peekLength = Math.Min(content.Length, 4096);
        var peek = Encoding.Latin1.GetString(content, 0, peekLength);

        if (LooksLikeHtml(peek))
        {
            if (peek.Contains(SsoMarker, StringComparison.OrdinalIgnoreCase))
            {
                detail = "SSO login page returned — token missing or expired";
                return IceResponseKind.AuthExpired;
            }

            if (peek.Contains(NoFilesMarker, StringComparison.OrdinalIgnoreCase) ||
                peek.Contains(IndexOfMarker, StringComparison.OrdinalIgnoreCase))
            {
                detail = "Directory-index page — no file published for this date";
                return IceResponseKind.NotAvailable;
            }

            // Some other HTML. Treated as auth rather than malformed on purpose: an
            // unrecognised interstitial is far more likely to be a login/consent
            // redirect than a corrupted data file, and one retry after re-auth is
            // cheap. If it recurs after re-auth the reader fails the unit anyway.
            detail = "Unrecognised HTML response";
            return IceResponseKind.AuthExpired;
        }

        if (feed.Format == IceFileFormat.Xlsx)
        {
            if (StartsWith(content, ZipMagic))
            {
                detail = "XLSX";
                return IceResponseKind.Data;
            }

            detail = "Expected an XLSX (PK zip magic) but the body is not a zip";
            return IceResponseKind.Malformed;
        }

        // Text feed: the header row must be the one we mapped against. This is what
        // turns a silent upstream column change into a loud failure instead of a
        // table full of NULLs.
        var firstLine = FirstLine(peek);
        var missing = MissingHeaders(firstLine, feed);
        if (missing.Count == 0)
        {
            detail = "Data";
            return IceResponseKind.Data;
        }

        detail = $"Header row does not carry required column(s): {string.Join(", ", missing)}. " +
                 $"First line was: {Truncate(firstLine, 200)}";
        return IceResponseKind.Malformed;
    }

    /// <summary>
    /// Required headers the first line does not contain. Matching is by NAME and is
    /// tolerant of the delimiter, because the point here is only to prove we are
    /// looking at the right file — the real per-column mapping happens in the parser.
    /// </summary>
    private static List<string> MissingHeaders(string firstLine, IceFeedDescriptor feed)
    {
        var delimiter = feed.Format == IceFileFormat.Csv ? ',' : '|';

        var present = firstLine
            .Split(delimiter)
            .Select(h => h.Trim().Trim('"').Trim())
            .Where(h => h.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = new List<string>();
        foreach (var column in feed.Table.Columns)
        {
            if (column.Derived != IceDerived.None || !column.Required) continue;
            if (!column.SourceHeaders.Any(present.Contains))
                missing.Add(column.SourceHeaders[0]);
        }
        return missing;
    }

    private static bool LooksLikeHtml(string peek)
    {
        var head = peek.TrimStart();
        return head.StartsWith("<!DOCTYPE html", StringComparison.OrdinalIgnoreCase)
            || head.StartsWith("<html", StringComparison.OrdinalIgnoreCase);
    }

    private static bool StartsWith(byte[] content, byte[] magic)
    {
        if (content.Length < magic.Length) return false;
        for (var i = 0; i < magic.Length; i++)
            if (content[i] != magic[i]) return false;
        return true;
    }

    private static string FirstLine(string text)
    {
        var end = text.IndexOfAny(new[] { '\r', '\n' });
        return (end < 0 ? text : text[..end]).Trim();
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
