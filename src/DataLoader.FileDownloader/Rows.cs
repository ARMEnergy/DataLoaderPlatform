namespace DataLoader.FileDownloader;

/// <summary>
/// One manifest row: a record that a named file was acquired (or found already present,
/// or legitimately absent) at a destination.
///
/// <para>This is the loader's merged output. The FILES are the product — this table is
/// what makes the fetching observable, which the standalone app had only for its Link
/// mode. Modes Sftp/Ftp/Browser wrote nothing to SQL at all, which is why a two-month
/// outage of the Click task went unnoticed until the share was inspected by hand.</para>
/// </summary>
public sealed class FileDownloadRow
{
    public required string SourceName { get; init; }

    /// <summary>Stored as the mode NAME (<c>Link</c>/<c>Browser</c>/<c>Sftp</c>/<c>Ftp</c>),
    /// not its number — the numbers are the original app's argv values and mean nothing
    /// to a reader of the table.</summary>
    public required string Mode { get; init; }

    public required string FileName { get; init; }

    public required string DestinationPath { get; init; }

    /// <summary>Publication date where the source has one; null for listing-driven feeds.</summary>
    public DateTime? FileDate { get; init; }

    /// <summary>Bytes written. 0 for <see cref="FileDownloadStatus.NotFound"/>.</summary>
    public long SizeBytes { get; init; }

    public required string Status { get; init; }

    public DateTime DownloadedAtUtc { get; init; }

    /// <summary><c>download.FileDownloader_Info.Id</c> for Link rows; null otherwise.</summary>
    public int? InfoId { get; init; }

    /// <summary>
    /// Where the file came from, for diagnostics.
    ///
    /// <para>🔒 Always set through <see cref="SafeLocator"/>, never from the raw locator.
    /// Link-mode URLs are built from a <c>string.Format</c> template in
    /// <c>download.FileDownloader_Info.BaseURL</c> whose parameters are operator-supplied,
    /// so a vendor that authenticates by query string (the shape OilX uses) would put a
    /// live key straight into this column — and unlike a log file, a table is read by
    /// reports, copied into extracts and backed up.</para>
    /// </summary>
    public required string SourceLocator { get; init; }

    /// <summary>
    /// Strips the query string and any userinfo from an absolute URL, leaving scheme,
    /// host and path. Non-URL locators (FTP/SFTP remote paths) are returned unchanged —
    /// those carry no credential, because this loader's credentials travel in the
    /// connection, not the path.
    /// </summary>
    public static string SafeLocator(string? locator)
    {
        if (string.IsNullOrWhiteSpace(locator)) return string.Empty;

        if (!Uri.TryCreate(locator, UriKind.Absolute, out var uri)) return locator;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return locator;

        return $"{uri.Scheme}://{uri.Host}{uri.AbsolutePath}";
    }
}
