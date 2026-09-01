using System.Collections.Concurrent;
using DataLoader.Core.Sources;
using FluentFTP;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.Argus;

/// <summary>
/// Loader-local marker over the platform's <see cref="IFileSystemDriver"/>. All
/// loaders share one DI container, so a distinct interface keeps this driver from
/// colliding with OPIS's <c>IOpisFtp</c> or Platts' <c>IPlattsSftp</c>.
/// </summary>
public interface IArgusFtp : IFileSystemDriver
{
}

/// <summary>
/// Plain-FTP filesystem driver for the Argus drop, built on FluentFTP.
///
/// <para>
/// <b>Thread-safety:</b> <see cref="AsyncFtpClient"/> is not safe for concurrent
/// operations, so every call opens a short-lived client (connect, work, dispose).
/// That is correct under <c>MaxConcurrentWorkUnits &gt; 1</c>; the extra handshake
/// is negligible next to a 10 MB download.
/// </para>
/// <para>
/// <b>Secrets:</b> the password reaches FluentFTP only. It is never logged, and no
/// method here builds a <c>ftp://user:pass@host</c> style URI — the paths that
/// reach the FileLog are host + path.
/// </para>
/// </summary>
public sealed class ArgusFtpFileSystem : IArgusFtp
{
    private readonly ArgusSettings _settings;
    private readonly ILogger<ArgusFtpFileSystem> _logger;

    public ArgusFtpFileSystem(IOptions<ArgusSettings> settings, ILogger<ArgusFtpFileSystem> logger)
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

                // Normalize to UTC so the resume key is stable regardless of what
                // the server's LIST reports in local terms.
                var modified = item.Modified == default
                    ? DateTime.UnixEpoch
                    : DateTime.SpecifyKind(item.Modified.ToUniversalTime(), DateTimeKind.Utc);

                files.Add(new RemoteFile(CombinePath(dir, item.Name), item.Name, item.Size, modified));
            }

            _logger.LogInformation("Argus FTP: {Count} file(s) matching {Pattern} in {Dir} on {Host}",
                files.Count, pattern, dir, _settings.FtpHost);

            return (IReadOnlyList<RemoteFile>)files;
        }, ct).ConfigureAwait(false);
    }

    public async Task<Stream> OpenReadAsync(string fullPath, CancellationToken ct)
    {
        // Download to memory rather than handing back a live network stream: the
        // caller would otherwise hold the FTP client open for the whole parse. The
        // largest in-scope file is 10.5 MB. The returned stream is positioned at 0.
        return await WithClientAsync<Stream>(async client =>
        {
            var bytes = await client.DownloadBytes(fullPath, token: ct).ConfigureAwait(false)
                        ?? throw new IOException($"Argus FTP: download returned no content for '{fullPath}'.");

            return new MemoryStream(bytes, writable: false);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Not supported. The Argus drop is read-only for this account and the loader
    /// is idempotent by resume key, so it never moves a processed file aside.
    /// </summary>
    public Task MoveAsync(string fromPath, string toPath, CancellationToken ct) =>
        throw new NotSupportedException("Argus FTP driver is read-only; MoveAsync is not supported.");

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
            client.Config.ValidateAnyCertificate = _settings.UseFtps;

            await client.Connect(ct).ConfigureAwait(false);
            return await work(client).ConfigureAwait(false);
        }
        finally
        {
            // Dispose covers the disconnect; swallow only teardown noise so a
            // successful download is not lost to a rude server close.
            try { await client.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogDebug(ex, "Argus FTP: error closing client to {Host}", _settings.FtpHost); }
        }
    }

    internal static string NormalizeDirectory(string path) =>
        string.IsNullOrWhiteSpace(path) ? "/" : "/" + path.Trim().Trim('/');

    internal static string CombinePath(string dir, string name) =>
        dir.EndsWith('/') ? dir + name : dir + "/" + name;

    /// <summary>
    /// Minimal glob for the <c>*.csv</c> shapes this loader uses: <c>*</c> matches
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
/// Memoizes one directory listing per run.
///
/// <para>
/// 15 reference pipelines all enumerate the same DOCUMENTATION folder. Without
/// this they would issue 15 identical <c>LIST</c> calls; with it the whole run
/// makes two — one per directory. Entries are keyed by normalized directory, and
/// a FAILED listing is evicted, so a transient failure does not poison the rest of
/// the run.
/// </para>
/// <para>
/// The value is a <see cref="Lazy{T}"/> rather than a bare <c>Task</c>:
/// <c>ConcurrentDictionary.GetOrAdd</c> does not hold a lock while it runs the
/// factory, so two concurrent callers could otherwise each start a listing and one
/// result would be thrown away. Lazy with
/// <see cref="LazyThreadSafetyMode.ExecutionAndPublication"/> makes exactly one of
/// them do the work.
/// </para>
/// <para>
/// <b>Cancellation:</b> the first caller's token is the one the listing observes.
/// Every caller here passes the same run-scoped token, so that is not a live
/// hazard — but it is the reason not to reuse this cache across runs.
/// </para>
/// </summary>
public sealed class ArgusListingCache
{
    private readonly IArgusFtp _ftp;
    private readonly ConcurrentDictionary<string, Lazy<Task<IReadOnlyList<RemoteFile>>>> _cache = new(StringComparer.OrdinalIgnoreCase);

    public ArgusListingCache(IArgusFtp ftp) => _ftp = ftp;

    public Task<IReadOnlyList<RemoteFile>> ListAsync(string directory, CancellationToken ct)
    {
        var key = ArgusFtpFileSystem.NormalizeDirectory(directory);

        var lazy = _cache.GetOrAdd(key, k => new Lazy<Task<IReadOnlyList<RemoteFile>>>(
            () => ListAndEvictOnFailureAsync(k, ct),
            LazyThreadSafetyMode.ExecutionAndPublication));

        return lazy.Value;
    }

    private async Task<IReadOnlyList<RemoteFile>> ListAndEvictOnFailureAsync(string dir, CancellationToken ct)
    {
        try
        {
            return await _ftp.ListAsync(dir, "*.csv", ct).ConfigureAwait(false);
        }
        catch
        {
            _cache.TryRemove(dir, out _);
            throw;
        }
    }
}
