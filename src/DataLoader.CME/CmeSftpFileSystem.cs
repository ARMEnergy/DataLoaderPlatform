using System.Net.Sockets;
using System.Text.RegularExpressions;
using DataLoader.Core.Sources;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace DataLoader.CME;

/// <summary>
/// CME-specific SFTP filesystem driver. Extends the platform's
/// <see cref="IFileSystemDriver"/> with the one capability the flat file-drop
/// interface lacks and this loader cannot work without: listing SUB-DIRECTORIES,
/// because the whole drop is a directory tree
/// (<c>product_exchange / EOD_exchange / yyyy / MM / dd / file.txt</c>) rather
/// than a flat folder. A loader-local marker interface keeps it from colliding
/// with another loader's file system in the shared DI container.
/// </summary>
public interface ICmeSftp : IFileSystemDriver
{
    /// <summary>List the sub-directory names (leaf names, not paths) under <paramref name="path"/>.</summary>
    Task<IReadOnlyList<string>> ListDirectoriesAsync(string path, CancellationToken ct);

    /// <summary>List files and sub-directories under <paramref name="path"/> in ONE round trip.</summary>
    Task<CmeListing> ListEntriesAsync(string path, CancellationToken ct);

    /// <summary>
    /// Depth-first walk of <paramref name="root"/> down to
    /// <paramref name="depth"/> directory levels, returning every file found, over
    /// a SINGLE SFTP session.
    ///
    /// <para>
    /// The drop nests five levels deep
    /// (<c>product / feed / yyyy / MM / dd / file.txt</c>), so a full discovery is
    /// ~100 directory listings. Doing those through
    /// <see cref="ListEntriesAsync"/> would open ~100 SSH connections — minutes of
    /// handshakes before the first byte of data. One session for the whole walk is
    /// what keeps discovery to a few seconds.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<RemoteFile>> WalkAsync(string root, int depth, CancellationToken ct);
}

/// <summary>One directory's contents: sub-directory leaf names, and files.</summary>
/// <param name="Directories">Leaf names, e.g. <c>2026</c> — NOT paths.</param>
/// <param name="Files">Files, whose <see cref="RemoteFile.FullPath"/> is the RELATIVE path this driver can re-open.</param>
public sealed record CmeListing(IReadOnlyList<string> Directories, IReadOnlyList<RemoteFile> Files);

/// <summary>
/// SFTP driver built on SSH.NET (Renci.SshNet). The drop speaks SSH SFTP — not
/// FTP/FTPS — so the FluentFTP driver the OPIS/Argus/EOX loaders use cannot serve
/// it.
///
/// <para>
/// <b>⚠ THE ABSOLUTE-PATH QUIRK — the single most load-bearing fact in this
/// class.</b> This server accepts only paths RELATIVE to the login directory.
/// Verified live against the account:
/// </para>
/// <code>
///   ListDirectory(".")            -> OK, 7 entries
///   ListDirectory("BAS_STLAGS")   -> OK
///   ListDirectory("./BAS_STLAGS") -> OK
///   ListDirectory("/BAS_STLAGS")  -> SftpPathNotFoundException("no such file")
///   ListDirectory("/BAS_STLAGS/") -> SftpPathNotFoundException("no such file")
/// </code>
/// <para>
/// The trap is that SSH.NET's own <c>SftpFile.FullName</c> reports
/// <c>/BAS_STLAGS</c> — the very form the server rejects. So this driver NEVER
/// propagates <c>FullName</c>: <see cref="ListEntriesAsync"/> builds each child
/// path by appending the leaf NAME to the relative parent path, and
/// <see cref="Normalize"/> strips any leading slash a caller supplies. Feeding
/// <c>FullName</c> back in is what breaks this loader, and it fails on the second
/// directory level rather than the first, so it would survive a shallow smoke
/// test.
/// </para>
/// <para>
/// <b>Thread-safety:</b> <see cref="SftpClient"/> is not safe for concurrent
/// operations, so each call opens a short-lived client (connect → work →
/// disconnect). This is simple and safe under <c>MaxConcurrentWorkUnits &gt; 1</c>.
/// </para>
/// <para>
/// SSH.NET's listing/download APIs are synchronous, so blocking calls are wrapped
/// in <see cref="Task.Run(Action, CancellationToken)"/> to honour the async
/// contract. The host key is accepted-and-logged (no pinned known-hosts), matching
/// the Platts driver.
/// </para>
/// </summary>
public sealed class CmeSftpFileSystem : ICmeSftp
{
    private readonly CmeSettings _settings;
    private readonly ILogger<CmeSftpFileSystem> _logger;

    public CmeSftpFileSystem(IOptions<CmeSettings> settings, ILogger<CmeSftpFileSystem> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    /// <summary>
    /// Turn any caller-supplied path into the RELATIVE form this server accepts:
    /// strip leading slashes, strip a trailing slash, and map empty to <c>.</c>.
    ///
    /// <para>
    /// A trailing slash is stripped because the server rejects that too — see the
    /// class remarks.
    /// </para>
    /// </summary>
    internal static string Normalize(string? path)
    {
        var value = (path ?? string.Empty).Trim().Replace('\\', '/');
        value = value.TrimStart('/');
        value = value.TrimEnd('/');
        return value.Length == 0 ? "." : value;
    }

    /// <summary>Append a child leaf name to a normalised parent path.</summary>
    internal static string Combine(string parent, string child)
    {
        var p = Normalize(parent);
        var c = (child ?? string.Empty).Trim('/');
        return p == "." ? c : p + "/" + c;
    }

    public Task<IReadOnlyList<string>> ListDirectoriesAsync(string path, CancellationToken ct) =>
        ListEntriesAsync(path, ct).ContinueWith(t => t.Result.Directories, ct,
            TaskContinuationOptions.OnlyOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    public Task<CmeListing> ListEntriesAsync(string path, CancellationToken ct) =>
        WithClientAsync(async (client, token) =>
        {
            var relative = Normalize(path);
            var entries = await Task.Run(() => client.ListDirectory(relative).ToList(), token).ConfigureAwait(false);

            var dirs = new List<string>();
            var files = new List<RemoteFile>();

            foreach (var entry in entries)
            {
                if (entry.Name is "." or "..") continue;

                if (entry.IsDirectory)
                {
                    dirs.Add(entry.Name);
                }
                else
                {
                    // FullPath is the RELATIVE path, deliberately NOT entry.FullName —
                    // see the class remarks on the absolute-path quirk.
                    files.Add(new RemoteFile(
                        Combine(relative, entry.Name), entry.Name, entry.Length, entry.LastWriteTimeUtc));
                }
            }

            _logger.LogDebug("CME SFTP listed {Dirs} dir(s) and {Files} file(s) under {Path}",
                dirs.Count, files.Count, relative);

            return new CmeListing(dirs, files);
        }, ct);

    public Task<IReadOnlyList<RemoteFile>> ListAsync(string path, string pattern, CancellationToken ct) =>
        ListEntriesAsync(path, ct).ContinueWith(t =>
        {
            var glob = WildcardToRegex(pattern);
            return (IReadOnlyList<RemoteFile>)t.Result.Files.Where(f => glob.IsMatch(f.Name)).ToList();
        }, ct, TaskContinuationOptions.OnlyOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    /// <summary>
    /// Hard ceiling on directories visited by one <see cref="WalkAsync"/> call.
    /// The live drop needs about 100; this exists so a symlink loop or an
    /// unexpectedly deep tree fails loudly instead of walking forever inside a work
    /// unit's timeout.
    /// </summary>
    private const int MaxWalkDirectories = 20000;

    public Task<IReadOnlyList<RemoteFile>> WalkAsync(string root, int depth, CancellationToken ct) =>
        WithClientAsync(async (client, token) =>
        {
            var files = new List<RemoteFile>();
            var visited = 0;

            // Iterative DFS over (path, remaining depth) so the whole walk shares one
            // session and one stack frame budget.
            var pending = new Stack<(string Path, int Remaining)>();
            pending.Push((Normalize(root), depth));

            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested();

                var (path, remaining) = pending.Pop();

                if (++visited > MaxWalkDirectories)
                    throw new InvalidOperationException(
                        $"CME SFTP walk visited more than {MaxWalkDirectories} directories under '{root}' — " +
                        "the drop layout has probably changed, or a directory loop exists.");

                List<Renci.SshNet.Sftp.ISftpFile> entries;
                try
                {
                    entries = await Task.Run(() => client.ListDirectory(path).ToList(), token).ConfigureAwait(false);
                }
                catch (SftpPathNotFoundException ex)
                {
                    // A directory that vanished between listings (CME rotates the
                    // window) must not fail the whole discovery.
                    _logger.LogWarning(ex, "CME SFTP walk could not open {Path} — skipped", path);
                    continue;
                }

                foreach (var entry in entries)
                {
                    if (entry.Name is "." or "..") continue;

                    var childPath = Combine(path, entry.Name);

                    if (entry.IsDirectory)
                    {
                        if (remaining > 0) pending.Push((childPath, remaining - 1));
                    }
                    else
                    {
                        // RELATIVE path, deliberately NOT entry.FullName — see the class
                        // remarks on the absolute-path quirk.
                        files.Add(new RemoteFile(childPath, entry.Name, entry.Length, entry.LastWriteTimeUtc));
                    }
                }
            }

            _logger.LogInformation(
                "CME SFTP walk of {Root} (depth {Depth}) visited {Dirs} directory(ies) and found {Files} file(s)",
                Normalize(root), depth, visited, files.Count);

            return (IReadOnlyList<RemoteFile>)files;
        }, ct);

    public Task<Stream> OpenReadAsync(string fullPath, CancellationToken ct) =>
        WithClientAsync(async (client, token) =>
        {
            // Download fully into memory so the SFTP connection can close before the
            // caller reads. Bulletins run 3-13 MB, which is well inside a single
            // buffer. Returned positioned at 0.
            var buffer = new MemoryStream();

            // NOTE: the token only cancels scheduling of this Task.Run, not the
            // in-flight synchronous SSH.NET download; that is bounded by
            // OperationTimeout (the same limitation as the Platts driver).
            await Task.Run(() => client.DownloadFile(Normalize(fullPath), buffer), token).ConfigureAwait(false);

            buffer.Position = 0;
            _logger.LogDebug("CME SFTP downloaded {Path} ({Bytes} bytes)", fullPath, buffer.Length);
            return (Stream)buffer;
        }, ct);

    public Task MoveAsync(string fromPath, string toPath, CancellationToken ct) =>
        throw new NotSupportedException(
            "CmeSftpFileSystem does not support MoveAsync; the drop is read-only and files stay on the server.");

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
                _logger.LogDebug(ex, "CME SFTP disconnect failed (ignored)");
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
            _logger.LogDebug("CME SFTP host key fingerprint {Fingerprint}", BitConverter.ToString(e.FingerPrint));
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
                // NOTE: ct only cancels scheduling of this Task.Run, not the in-flight
                // synchronous SSH.NET Connect; that is bounded by the client's
                // connection/operation timeouts (the same limitation as Platts).
                await Task.Run(() => client.Connect(), ct).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (attempt < maxAttempts
                                       && !ct.IsCancellationRequested
                                       && ex is SshException or SocketException)
            {
                _logger.LogWarning(ex,
                    "CME SFTP connect attempt {Attempt}/{Max} to {Host}:{Port} failed; retrying in {Delay}ms",
                    attempt, maxAttempts, _settings.SftpHost, _settings.SftpPort, _settings.RetryDelayMs);

                await Task.Delay(_settings.RetryDelayMs, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Compile a glob (<c>*</c>/<c>?</c>) into a case-insensitive regex.</summary>
    private static Regex WildcardToRegex(string pattern)
    {
        var escaped = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return new Regex(escaped, RegexOptions.IgnoreCase);
    }
}
