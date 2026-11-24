using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DataLoader.MISO.Tests;

/// <summary>
/// Minimal raw-TCP HTTP/1.1 server on a loopback address, for the few tests that must exercise the
/// module's REAL primary handler (<c>SocketsHttpHandler { AllowAutoRedirect = false }</c>) — a redirect
/// that is or is not followed, a body that is genuinely truncated against its Content-Length. Nothing
/// leaves the machine. Each connection gets one scripted raw response and is then closed.
/// </summary>
internal sealed class LoopbackHttpServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly Func<string, int, byte[]> _respond;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private int _count;

    /// <summary>Raw request heads (request line + headers) in arrival order.</summary>
    public ConcurrentQueue<string> Requests { get; } = new();
    public int Count => Volatile.Read(ref _count);
    public string Host { get; }
    public int Port { get; }
    public string BaseUrl => $"http://{Host}:{Port}";

    public LoopbackHttpServer(string address, Func<string, int, byte[]> respond)
    {
        _respond = respond;
        _listener = new TcpListener(IPAddress.Parse(address), 0);
        _listener.Start();
        Host = address;
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _loop = Task.Run(AcceptLoop);
    }

    public static byte[] Raw(string head, string body = "") => Encoding.ASCII.GetBytes(head.Replace("\n", "\r\n") + "\r\n\r\n" + body);

    public static byte[] Ok(string json) =>
        Raw($"HTTP/1.1 200 OK\nContent-Type: application/json\nContent-Length: {Encoding.UTF8.GetByteCount(json)}\nConnection: close", json);

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch { return; }

            _ = Task.Run(async () =>
            {
                using (client)
                {
                    try
                    {
                        var stream = client.GetStream();
                        var buf = new byte[16384];
                        var sb = new StringBuilder();
                        while (!sb.ToString().Contains("\r\n\r\n"))
                        {
                            var n = await stream.ReadAsync(buf, _cts.Token);
                            if (n == 0) return;
                            sb.Append(Encoding.ASCII.GetString(buf, 0, n));
                        }
                        var index = Interlocked.Increment(ref _count) - 1;
                        var head = sb.ToString();
                        Requests.Enqueue(head);
                        var response = _respond(head, index);
                        await stream.WriteAsync(response, _cts.Token);
                        await stream.FlushAsync(_cts.Token);
                        client.Client.Shutdown(SocketShutdown.Send);
                    }
                    catch { /* client went away */ }
                }
            });
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        try { await _loop; } catch { }
        _cts.Dispose();
    }
}
