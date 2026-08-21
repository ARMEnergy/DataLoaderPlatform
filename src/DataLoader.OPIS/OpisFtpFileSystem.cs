using DataLoader.Core.Sources;
using FluentFTP;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.OPIS;

/// <summary>
/// Loader-local marker over the platform's <see cref="IFileSystemDriver"/>. All
/// loaders share one DI container, so a distinct interface keeps this driver from
/// colliding with another loader's file system (the Platts <c>IPlattsSftp</c> posture).
/// </summary>
public interface IOpisFtp : IFileSystemDriver
{
}

/// <summary>
/// Plain-FTP filesystem driver for the OPIS drop, built on FluentFTP.
///
/// <para>
/// This is the platform's first FTP (as opposed to SFTP) driver. Platts' driver
/// speaks SSH SFTP via SSH.NET and cannot serve this feed, and
/// <c>System.Net.FtpWebRequest</c> is obsolete (SYSLIB0014) on net8.0 — hence
/// FluentFTP.
/// </para>
/// <para>
/// <b>Thread-safety:</b> <see cref="AsyncFtpClient"/> is not safe for concurrent
/// operations, so every call opens a short-lived client (connect → work →
/// dispose). That is simple and correct under <c>MaxConcurrentWorkUnits &gt; 1</c>,
/// and the per-file payload here is ~14 KB so the extra handshake is negligible.
/// </para>
/// <para>
/// <b>Secrets:</b> the password is passed to FluentFTP only. It is never logged,
/// and no method here ever builds a <c>ftp://user:pass@host</c> style URI — the
/// paths that reach the FileLog are host + path only.
/// </para>
/// </summary>
public sealed class OpisFtpFileSystem : IOpisFtp
{
    private readonly OpisSettings _settings;
    private readonly ILogger<OpisFtpFileSystem> _logger;

    public OpisFtpFileSystem(IOptions<OpisSettings> settings, ILogger<OpisFtpFileSystem> logger)
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

                // FluentFTP returns the server's LIST timestamp, which for this drop
                // is minute-granular. Where the server supports MDTM (OPIS does) it
                // is already the more precise value. Normalize to UTC either way so
                // the resume key is stable.
                var modified = item.Modified == default
                    ? DateTime.UnixEpoch
                    : DateTime.SpecifyKind(item.Modified.ToUniversalTime(), DateTimeKind.Utc);

                files.Add(new RemoteFile(CombinePath(dir, item.Name), item.Name, item.Size, modified));
            }

            _logger.LogInformation("OPIS FTP: {Count} file(s) matching {Pattern} in {Dir} on {Host}",
                files.Count, pattern, dir, _settings.FtpHost);

            return (IReadOnlyList<RemoteFile>)files;
        }, ct).ConfigureAwait(false);
    }

    public async Task<Stream> OpenReadAsync(string fullPath, CancellationToken ct)
    {
        // Download to memory rather than handing back a live network stream: the
        // caller would otherwise keep the FTP client alive for the whole parse, and
        // these files are ~14 KB. The returned stream is positioned at 0.
        return await WithClientAsync<Stream>(async client =>
        {
            var bytes = await client.DownloadBytes(fullPath, token: ct).ConfigureAwait(false)
                        ?? throw new IOException($"OPIS FTP: download returned no content for '{fullPath}'.");

            return new MemoryStream(bytes, writable: false);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Not supported. The OPIS drop is read-only for this account and the loader
    /// is idempotent by resume key, so it never needs to move a processed file aside.
    /// </summary>
    public Task MoveAsync(string fromPath, string toPath, CancellationToken ct) =>
        throw new NotSupportedException("OPIS FTP driver is read-only; MoveAsync is not supported.");

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
            catch (Exception ex) { _logger.LogDebug(ex, "OPIS FTP: error closing client to {Host}", _settings.FtpHost); }
        }
    }

    private static string NormalizeDirectory(string path) =>
        string.IsNullOrWhiteSpace(path) ? "/" : "/" + path.Trim().Trim('/');

    private static string CombinePath(string dir, string name) =>
        dir.EndsWith('/') ? dir + name : dir + "/" + name;

    /// <summary>
    /// Minimal glob for the <c>*.csv</c> / <c>*LP.csv</c> shapes this loader uses:
    /// <c>*</c> matches any run of characters, everything else is literal, matching
    /// is case-insensitive. Deliberately not a full glob engine.
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
