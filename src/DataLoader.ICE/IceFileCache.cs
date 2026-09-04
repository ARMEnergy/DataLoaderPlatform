using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.ICE;

/// <summary>
/// The on-disk copy of every downloaded file, laid out
/// <c>&lt;DownloadDirectory&gt;/&lt;FeedId&gt;/&lt;filename&gt;</c>.
///
/// <para>
/// This is a real cache, not a scratch directory. With the shipped all-hot 30-day
/// window (<see cref="IceSettings.SettledAfterDays"/>) every run revisits ~31 trade
/// dates across 18 feeds; a day of ICE files is roughly 100 MB, so re-downloading
/// the whole window every run would be ~3 GB. Reusing a file that is already on
/// disk reduces that to genuinely new dates plus whatever retention has expired.
/// </para>
/// <para>
/// <b>Sentinel bodies are never cached.</b> Writing an "Index of / No Files
/// Available" page or an SSO login page to disk under the data file's name would
/// poison that date for every later run — the cache would keep handing back HTML
/// that the classifier keeps rejecting, and no re-download would ever be attempted.
/// Only bodies classified <see cref="IceResponseKind.Data"/> are written, and that
/// decision is the caller's (<see cref="IceSourceReader"/>).
/// </para>
/// </summary>
public sealed class IceFileCache
{
    private readonly IceSettings _settings;
    private readonly ILogger _logger;

    public IceFileCache(IOptions<IceSettings> options, ILogger<IceFileCache> logger)
    {
        _settings = options.Value;
        _logger = logger;
        Root = ResolveRoot(_settings.DownloadDirectory);
    }

    /// <summary>Absolute path of the cache root.</summary>
    public string Root { get; }

    /// <summary>
    /// A relative <c>DownloadDirectory</c> resolves against the host's base
    /// directory so files land with the deployment rather than in whatever the
    /// process's current directory happens to be when a scheduler starts it.
    /// </summary>
    internal static string ResolveRoot(string configured)
    {
        var value = string.IsNullOrWhiteSpace(configured) ? "files/ICE" : configured.Trim();
        return Path.IsPathRooted(value)
            ? Path.GetFullPath(value)
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, value));
    }

    /// <summary>Local path this feed/date would occupy.</summary>
    public string PathFor(IceFeedDescriptor feed, DateOnly tradeDate) =>
        Path.Combine(Root, feed.FeedId, feed.FileNameFor(tradeDate));

    /// <summary>
    /// Cached bytes, or null when there is no usable copy.
    ///
    /// <para>
    /// A zero-length file counts as no copy: it is what a crashed or interrupted
    /// write leaves behind, and treating it as data would classify as
    /// <c>Malformed</c> forever.
    /// </para>
    /// </summary>
    public async Task<byte[]?> TryReadAsync(IceFeedDescriptor feed, DateOnly tradeDate, CancellationToken cancellationToken)
    {
        if (_settings.ForceDownload) return null;

        var path = PathFor(feed, tradeDate);

        try
        {
            if (!File.Exists(path)) return null;
            if (new FileInfo(path).Length == 0) return null;

            return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // An unreadable cache entry must degrade to a re-download, never to a
            // failed work unit.
            _logger.LogWarning(ex, "ICE: could not read cached file {Path} — will re-download", path);
            return null;
        }
    }

    /// <summary>
    /// Write a downloaded body to the cache. Best-effort: a disk failure is logged
    /// and swallowed, because the bytes are already in hand and the work unit can
    /// complete without them ever reaching disk.
    /// </summary>
    public async Task SaveAsync(IceFeedDescriptor feed, DateOnly tradeDate, byte[] content, CancellationToken cancellationToken)
    {
        var path = PathFor(feed, tradeDate);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            // Write to a temp name and move into place so a crash mid-write cannot
            // leave a truncated file that later looks like a valid cache hit.
            var temp = path + ".tmp";
            await File.WriteAllBytesAsync(temp, content, cancellationToken).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ICE: could not cache {Path}", path);
        }
    }

    /// <summary>
    /// Delete cached files whose last-write time is older than
    /// <see cref="IceSettings.FileRetentionDays"/>. Run once per run, before
    /// enumeration.
    ///
    /// <para>
    /// Entirely best-effort: a locked or unreadable file is logged at debug and
    /// skipped. Housekeeping must never fail a load.
    /// </para>
    /// </summary>
    public int SweepExpired()
    {
        var retentionDays = _settings.FileRetentionDays;
        if (retentionDays <= 0)
        {
            _logger.LogDebug("ICE: FileRetentionDays={Days} — retention sweep disabled", retentionDays);
            return 0;
        }

        if (!Directory.Exists(Root)) return 0;

        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
        var deleted = 0;

        try
        {
            foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) >= cutoff) continue;
                    File.Delete(file);
                    deleted++;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "ICE: could not delete expired cache file {Path}", file);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ICE: retention sweep of {Root} failed", Root);
            return deleted;
        }

        if (deleted > 0)
            _logger.LogInformation("ICE: retention sweep removed {Count} file(s) older than {Days} day(s) from {Root}",
                deleted, retentionDays, Root);

        return deleted;
    }
}
