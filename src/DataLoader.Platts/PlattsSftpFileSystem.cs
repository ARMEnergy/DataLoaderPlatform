using System.Net.Sockets;
using System.Text.RegularExpressions;
using DataLoader.Core.Sources;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace DataLoader.Platts;

/// <summary>
/// Platts-specific SFTP filesystem driver. Extends the platform's
/// <see cref="IFileSystemDriver"/> with one extra capability the file-drop
/// interface lacks: listing the <c>yyyymmdd</c> date sub-directories under a root.
/// A loader-local marker interface keeps it from colliding with other loaders'
/// file systems in DI.
/// </summary>
public interface IPlattsSftp : IFileSystemDriver
{
    /// <summary>List the date sub-directories under <paramref name="path"/>.</summary>
    Task<IReadOnlyList<string>> ListDirectoriesAsync(string path, CancellationToken ct);
}

/// <summary>
/// SFTP driver built on SSH.NET (Renci.SshNet). SSH SFTP — not FTP/FTPS — so the
/// <see cref="DataLoader.Ftp.FtpFileSystem"/> (which uses FtpWebRequest)
/// cannot serve this feed.
///
/// <para>
/// <b>Thread-safety:</b> <see cref="SftpClient"/> is not safe for concurrent
/// operations, so each call opens a short-lived client (connect → work →
/// disconnect). This is simple and safe under <c>MaxConcurrentWorkUnits &gt; 1</c>.
/// </para>
/// <para>
/// SSH.NET's listing/download APIs are synchronous, so blocking calls are wrapped
/// in <see cref="Task.Run(Action, CancellationToken)"/> to honour the async
/// contract. The host key is accepted-and-logged (no pinned known-hosts).
/// </para>
/// </summary>
public sealed class PlattsSftpFileSystem : IPlattsSftp
{
    private readonly PlattsSettings _settings;
    private readonly ILogger<PlattsSftpFileSystem> _logger;

    public PlattsSftpFileSystem(IOptions<PlattsSettings> settings, ILogger<PlattsSftpFileSystem> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public Task<IReadOnlyList<string>> ListDirectoriesAsync(string path, CancellationToken ct) =>
        WithClientAsync(async (client, token) =>
        {
            var entries = await Task.Run(() => client.ListDirectory(path).ToList(), token).ConfigureAwait(false);
            var folderPattern = new Regex(_settings.DateFolderPattern);
            var basePath = path.TrimEnd('/');

            var result = new List<string>();
            foreach (var entry in entries)
            {
                if (!entry.IsDirectory) continue;
                if (entry.Name is "." or "..") continue;
                if (!folderPattern.IsMatch(entry.Name)) continue;
                result.Add(basePath + "/" + entry.Name);
            }

            _logger.LogDebug("SFTP listed {Count} date folders under {Path}", result.Count, path);
            return (IReadOnlyList<string>)result;
        }, ct);

    public Task<IReadOnlyList<RemoteFile>> ListAsync(string path, string pattern, CancellationToken ct) =>
        WithClientAsync(async (client, token) =>
        {
            var entries = await Task.Run(() => client.ListDirectory(path).ToList(), token).ConfigureAwait(false);
            var glob = WildcardToRegex(pattern);

            var files = new List<RemoteFile>();
            foreach (var file in entries)
            {
                if (file.IsDirectory) continue;
                if (!glob.IsMatch(file.Name)) continue;
                files.Add(new RemoteFile(file.FullName, file.Name, file.Length, file.LastWriteTimeUtc));
            }

            _logger.LogDebug("SFTP listed {Count} files matching {Pattern} in {Path}", files.Count, pattern, path);
            return (IReadOnlyList<RemoteFile>)files;
        }, ct);

    public Task<Stream> OpenReadAsync(string fullPath, CancellationToken ct) =>
        WithClientAsync(async (client, token) =>
        {
            // Download fully into memory so the SFTP connection can close before the
            // caller reads. Return the stream positioned at 0.
            var buffer = new MemoryStream();
            // NOTE: the token only cancels scheduling of this Task.Run, not the
            // in-flight synchronous SSH.NET download; that is bounded by
            // OperationTimeout (same limitation as the reference FtpFileSystem).
            await Task.Run(() => client.DownloadFile(fullPath, buffer), token).ConfigureAwait(false);
            buffer.Position = 0;
            _logger.LogDebug("SFTP downloaded {Path} ({Bytes} bytes)", fullPath, buffer.Length);
            return (Stream)buffer;
        }, ct);

    public Task MoveAsync(string fromPath, string toPath, CancellationToken ct) =>
        throw new NotSupportedException("PlattsSftpFileSystem does not support MoveAsync; files remain on the server.");

    // -------------------------------------------------------------------------

    /// <summary>
    /// Opens a short-lived connected client, runs <paramref name="work"/>, and
    /// always disconnects afterwards.
    /// </summary>
    private async Task<T> WithClientAsync<T>(Func<SftpClient, CancellationToken, Task<T>> work, CancellationToken ct)
    {
        using var client = CreateClient();
        await ConnectWithRetryAsync(client, ct).ConfigureAwait(false);
        try
        {
            return await work(client, ct).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                if (client.IsConnected) client.Disconnect();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "SFTP disconnect failed (ignored)");
            }
        }
    }

    private SftpClient CreateClient()
    {
        var port = _settings.SftpPort <= 0 ? 22 : _settings.SftpPort;
        var client = new SftpClient(_settings.SftpHost, port, _settings.SftpUsername, _settings.SftpPassword);

        client.OperationTimeout = _settings.WorkUnitTimeoutSeconds > 0
            ? TimeSpan.FromSeconds(_settings.WorkUnitTimeoutSeconds)
            : Timeout.InfiniteTimeSpan;

        // Accept-and-log the host key (no pinned known-hosts). Never logs credentials.
        client.HostKeyReceived += (_, e) =>
        {
            e.CanTrust = true;
            _logger.LogDebug("SFTP host key fingerprint {Fingerprint}", BitConverter.ToString(e.FingerPrint));
        };

        return client;
    }

    private async Task ConnectWithRetryAsync(SftpClient client, CancellationToken ct)
    {
        var maxAttempts = Math.Max(1, _settings.RetryCount);
        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // NOTE: ct only cancels scheduling of this Task.Run, not the
                // in-flight synchronous SSH.NET Connect; that is bounded by the
                // client's connection/operation timeouts (same limitation as the
                // reference FtpFileSystem).
                await Task.Run(() => client.Connect(), ct).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (attempt < maxAttempts
                                       && !ct.IsCancellationRequested
                                       && ex is SshException or SocketException)
            {
                _logger.LogWarning(ex,
                    "SFTP connect attempt {Attempt}/{Max} to {Host}:{Port} failed; retrying in {Delay}ms",
                    attempt, maxAttempts, _settings.SftpHost, _settings.SftpPort, _settings.RetryDelayMs);
                await Task.Delay(_settings.RetryDelayMs, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Compile a glob (<c>*</c>/<c>?</c>) into a case-insensitive regex. Copied from the FTP driver.</summary>
    private static Regex WildcardToRegex(string pattern)
    {
        var escaped = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return new Regex(escaped, RegexOptions.IgnoreCase);
    }
}
