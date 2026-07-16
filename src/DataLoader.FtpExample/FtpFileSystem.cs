using System.Net;
using DataLoader.Core.Sources;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.FtpExample;

/// <summary>
/// Loader-local FTP filesystem abstraction. Same pattern as
/// <c>DataLoader.CsvExample.ICsvFileSystem</c> — a marker interface that
/// extends the platform's <see cref="IFileSystemDriver"/> so two loaders
/// can have different file systems without colliding in DI.
/// </summary>
public interface IFtpFileSystem : IFileSystemDriver
{
}

/// <summary>
/// Minimal FTP driver built on <c>FtpWebRequest</c>. Demonstrates the shape
/// without taking on an FTP-library dependency. In production you'd use
/// FluentFTP for proper SFTP / FTPS support and connection reuse.
/// </summary>
public sealed class FtpFileSystem : IFtpFileSystem
{
    private readonly FtpExampleSettings _settings;
    private readonly ILogger<FtpFileSystem> _logger;

    public FtpFileSystem(IOptions<FtpExampleSettings> settings, ILogger<FtpFileSystem> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    private string BuildUri(string remotePath)
    {
        var scheme = _settings.UseSsl ? "ftps" : "ftp";
        var host = _settings.FtpHost.TrimEnd('/');
        var path = remotePath.StartsWith("/") ? remotePath : "/" + remotePath;
        var portPart = _settings.FtpPort == 21 ? "" : $":{_settings.FtpPort}";
        return $"{scheme}://{host}{portPart}{path}";
    }

    private FtpWebRequest CreateRequest(string remotePath, string method)
    {
#pragma warning disable SYSLIB0014 // FtpWebRequest is obsolete in .NET 6+; demo only
        var req = (FtpWebRequest)WebRequest.Create(BuildUri(remotePath));
#pragma warning restore SYSLIB0014
        req.Credentials = new NetworkCredential(_settings.FtpUsername, _settings.FtpPassword);
        req.EnableSsl = _settings.UseSsl;
        req.Method = method;
        req.UseBinary = true;
        req.UsePassive = true;
        req.KeepAlive = false;
        return req;
    }

    public async Task<IReadOnlyList<RemoteFile>> ListAsync(string path, string pattern, CancellationToken ct)
    {
        _logger.LogDebug("FTP LIST {Path} ({Pattern})", path, pattern);
        var req = CreateRequest(path, WebRequestMethods.Ftp.ListDirectoryDetails);
        using var resp = (FtpWebResponse)await req.GetResponseAsync().ConfigureAwait(false);
        await using var stream = resp.GetResponseStream();
        using var reader = new StreamReader(stream);

        var files = new List<RemoteFile>();
        var glob = WildcardToRegex(pattern);
        string? line;
        while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
        {
            // FTP LIST output formats vary by server; this is a coarse
            // unix-style parser. A real impl uses MLSD or FluentFTP.
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 9) continue;
            if (parts[0].StartsWith('d')) continue;       // skip directories
            var name = string.Join(' ', parts.Skip(8));
            if (!glob.IsMatch(name)) continue;
            long.TryParse(parts[4], out var size);
            var full = path.TrimEnd('/') + "/" + name;
            files.Add(new RemoteFile(full, name, size, DateTime.UtcNow));
        }
        return files;
    }

    public async Task<Stream> OpenReadAsync(string fullPath, CancellationToken ct)
    {
        _logger.LogDebug("FTP RETR {Path}", fullPath);
        var req = CreateRequest(fullPath, WebRequestMethods.Ftp.DownloadFile);
        var resp = (FtpWebResponse)await req.GetResponseAsync().ConfigureAwait(false);
        // Caller disposes — wrap so the response goes with it.
        return new FtpResponseStream(resp);
    }

    public Task MoveAsync(string fromPath, string toPath, CancellationToken ct) =>
        throw new NotSupportedException("MoveAsync not implemented in the demo FTP driver");

    private static System.Text.RegularExpressions.Regex WildcardToRegex(string pattern)
    {
        var escaped = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
            .Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return new System.Text.RegularExpressions.Regex(escaped,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private sealed class FtpResponseStream : Stream
    {
        private readonly FtpWebResponse _resp;
        private readonly Stream _inner;
        public FtpResponseStream(FtpWebResponse resp) { _resp = resp; _inner = resp.GetResponseStream(); }
        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) { _inner.Dispose(); _resp.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
