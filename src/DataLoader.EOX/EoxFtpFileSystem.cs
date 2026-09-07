using System.Collections.Concurrent;
using DataLoader.Core.Sources;
using FluentFTP;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.EOX;

/// <summary>
/// Loader-local marker over the platform's <see cref="IFileSystemDriver"/>. All
/// loaders share one DI container, so a distinct interface keeps this driver from
/// colliding with OPIS's <c>IOpisFtp</c>, Argus's <c>IArgusFtp</c> or Platts'
/// <c>IPlattsSftp</c>.
/// </summary>
public interface IEoxFtp : IFileSystemDriver
{
}

/// <summary>
/// FTP filesystem driver for the EOX drop, built on FluentFTP. Explicit TLS is on
/// by default (<see cref="EoxSettings.UseFtps"/>) because the server advertises
/// <c>AUTH TLS</c> and the account password would otherwise cross the wire in
/// cleartext.
///
/// <para>
/// <b>Thread-safety:</b> <see cref="AsyncFtpClient"/> is not safe for concurrent
/// operations, so every call opens a short-lived client (connect → work →
/// dispose). That is correct under <c>MaxConcurrentWorkUnits &gt; 1</c>, and the
/// handshake is negligible next to a 4 MB download.
/// </para>
/// <para>
/// <b>Secrets:</b> the password reaches FluentFTP only. It is never logged, and no
/// method here builds a <c>ftp://user:pass@host</c> style URI — the paths that
/// reach <c>arm.FileLog.RequestPath</c> are host + path.
/// </para>
/// </summary>
public sealed class EoxFtpFileSystem : IEoxFtp
{
    private readonly EoxSettings _settings;
    private readonly ILogger<EoxFtpFileSystem> _logger;

    public EoxFtpFileSystem(IOptions<EoxSettings> settings, ILogger<EoxFtpFileSystem> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<RemoteFile>> ListAsync(string path, string pattern, CancellationToken ct)
    {
        var dir = NormalizeDirectory(path);

        return await WithClientAsync(async client =>
        {
            var listing = await client.GetListing(dir, ct).ConfigureAwait(false);

            var files = new List<RemoteFile>();
            foreach (var item in listing)
            {
                if (item.Type != FtpObjectType.File) continue;
                if (!GlobMatch(item.Name, pattern)) continue;

                // The server supports MLSD and MDTM, so FluentFTP gets a real UTC
                // modify stamp rather than the minute-granular local time in LIST.
                // Normalize to UTC either way — the resume key embeds this value, so
                // an unstable rendering would defeat the whole skip.
                var modified = item.Modified == default
                    ? DateTime.UnixEpoch
                    : DateTime.SpecifyKind(item.Modified.ToUniversalTime(), DateTimeKind.Utc);

                files.Add(new RemoteFile(CombinePath(dir, item.Name), item.Name, item.Size, modified));
            }

            _logger.LogInformation("EOX FTP: {Count} file(s) matching {Pattern} in {Dir} on {Host} (ftps={Ftps})",
                files.Count, pattern, dir, _settings.FtpHost, _settings.UseFtps);

            return (IReadOnlyList<RemoteFile>)files;
        }, ct).ConfigureAwait(false);
    }

    public async Task<Stream> OpenReadAsync(string fullPath, CancellationToken ct)
    {
        // Download to memory rather than handing back a live network stream: the
        // caller would otherwise hold the FTP client open for the whole parse. The
        // largest in-scope file is ~4.2 MB. The returned stream is positioned at 0.
        return await WithClientAsync<Stream>(async client =>
        {
            var bytes = await client.DownloadBytes(fullPath, token: ct).ConfigureAwait(false)
                        ?? throw new IOException($"EOX FTP: download returned no content for '{fullPath}'.");

            return new MemoryStream(bytes, writable: false);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Not supported. The EOX drop is read-only for this account and the loader is
    /// idempotent by resume key, so it never needs to move a processed file aside.
    /// Moving one would also destroy history the account is expected to keep — the
    /// drop holds every file back to 2011.
    /// </summary>
    public Task MoveAsync(string fromPath, string toPath, CancellationToken ct) =>
        throw new NotSupportedException("EOX FTP driver is read-only; MoveAsync is not supported.");

    // ------------------------------------------------------------------ internals

    private async Task<T> WithClientAsync<T>(Func<AsyncFtpClient, Task<T>> work, CancellationToken ct)
    {
        var client = new AsyncFtpClient(_settings.FtpHost, _settings.Username, _settings.Password, _settings.FtpPort);
        try
        {
            client.Config.ConnectTimeout = _settings.FtpTimeoutMs;
            client.Config.ReadTimeout = _settings.FtpTimeoutMs;
            client.Config.DataConnectionType = _settings.UsePassiveMode
                ? FtpDataConnectionType.AutoPassive
                : FtpDataConnectionType.AutoActive;
            client.Config.EncryptionMode = _settings.UseFtps ? FtpEncryptionMode.Explicit : FtpEncryptionMode.None;
            client.Config.ValidateAnyCertificate = _settings.UseFtps && _settings.ValidateAnyCertificate;

            await client.Connect(ct).ConfigureAwait(false);
            return await work(client).ConfigureAwait(false);
        }
        finally
        {
            // Dispose covers the disconnect; swallow only teardown noise so a
            // successful download is not lost to a rude server close.
            try { await client.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogDebug(ex, "EOX FTP: error closing client to {Host}", _settings.FtpHost); }
        }
    }

    internal static string NormalizeDirectory(string path) =>
        string.IsNullOrWhiteSpace(path) ? "/" : "/" + path.Trim().Trim('/');

    internal static string CombinePath(string dir, string name) =>
        dir.EndsWith('/') ? dir + name : dir + "/" + name;

    /// <summary>
    /// Minimal glob for the <c>*.csv</c> shape this loader uses: <c>*</c> matches
    /// any run of characters, everything else is literal, matching is
    /// case-insensitive. Deliberately not a full glob engine.
    /// </summary>
    internal static bool GlobMatch(string name, string pattern)
    {
        if (string.IsNullOrEmpty(pattern) || pattern == "*") return true;

        var regex = "^" + string.Join(
            ".*",
            pattern.Split('*').Select(System.Text.RegularExpressions.Regex.Escape)) + "$";

        return System.Text.RegularExpressions.Regex.IsMatch(
            name, regex, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }
}

/// <summary>
/// One directory listing per run, shared by all three feed pipelines.
///
/// <para>
/// This matters more here than in the Argus loader: the EOX drop is a single flat
/// root holding 18,486 entries, so listing it costs ~1.7 s and ~1.6 MB. Without
/// the cache each of the three pipelines would pay that, and the listing must
/// stay CONSISTENT between them anyway — a file appearing mid-run should not make
/// one feed see a different world than another.
/// </para>
/// <para>
/// A failed listing is evicted so a later pipeline retries rather than inheriting
/// a cached exception.
/// </para>
/// </summary>
public sealed class EoxListingCache
{
    private readonly IEoxFtp _ftp;
    private readonly ConcurrentDictionary<string, Lazy<Task<IReadOnlyDictionary<string, RemoteFile>>>> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public EoxListingCache(IEoxFtp ftp) => _ftp = ftp;

    /// <summary>
    /// The directory's <c>.csv</c> files, indexed by file NAME (case-insensitive).
    ///
    /// <para>
    /// A dictionary rather than a list because every consumer does an exact-name
    /// lookup: the work-unit provider builds the name it wants from the curve date
    /// and asks whether the server has it. Scanning 18k entries once per date per
    /// feed would otherwise be 93 linear scans per run.
    /// </para>
    /// </summary>
    public Task<IReadOnlyDictionary<string, RemoteFile>> ListAsync(string directory, CancellationToken ct)
    {
        var key = EoxFtpFileSystem.NormalizeDirectory(directory);

        var lazy = _cache.GetOrAdd(key, k => new Lazy<Task<IReadOnlyDictionary<string, RemoteFile>>>(
            () => ListAndEvictOnFailureAsync(k, ct),
            LazyThreadSafetyMode.ExecutionAndPublication));

        return lazy.Value;
    }

    private async Task<IReadOnlyDictionary<string, RemoteFile>> ListAndEvictOnFailureAsync(
        string dir, CancellationToken ct)
    {
        try
        {
            var files = await _ftp.ListAsync(dir, "*.csv", ct).ConfigureAwait(false);

            var index = new Dictionary<string, RemoteFile>(files.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var file in files) index[file.Name] = file;

            return index;
        }
        catch
        {
            _cache.TryRemove(dir, out _);
            throw;
        }
    }
}
