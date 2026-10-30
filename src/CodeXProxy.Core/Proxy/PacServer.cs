using System.Net;
using System.Net.Sockets;
using System.Text;
using CodeXProxy.Logging;

namespace CodeXProxy.Proxy;

public sealed class PacServer : IAsyncDisposable
{
    private readonly string _pacContent;
    private readonly ProxyLogStore _log;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<Task> _requests = [];
    private readonly object _requestsGate = new();
    private TcpListener? _listener;
    private Task? _acceptLoop;

    public PacServer(string pacContent, ProxyLogStore log)
    {
        _pacContent = pacContent;
        _log = log;
    }

    public int Port { get; private set; }

    public string Url => $"http://127.0.0.1:{Port}/proxy.pac";

    public Task StartAsync()
    {
        if (_listener is not null)
        {
            throw new InvalidOperationException("PAC 服务已启动。");
        }

        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start(backlog: 32);
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = AcceptLoopAsync(_shutdown.Token);
        _log.Success($"PAC 服务已监听 {Url}", route: "PAC");
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_listener is null)
        {
            return;
        }

        _shutdown.Cancel();
        _listener.Stop();
        try
        {
            if (_acceptLoop is not null)
            {
                await _acceptLoop;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SocketException)
        {
        }

        Task[] pending;
        lock (_requestsGate)
        {
            pending = _requests.ToArray();
        }

        try
        {
            await Task.WhenAll(pending);
        }
        catch
        {
        }

        _listener = null;
        _acceptLoop = null;
        _log.Info("PAC 服务已停止", route: "PAC");
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _shutdown.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        var listener = _listener!;
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var task = HandleRequestAsync(client, cancellationToken);
            lock (_requestsGate)
            {
                _requests.Add(task);
            }

            _ = task.ContinueWith(
                completed =>
                {
                    lock (_requestsGate)
                    {
                        _requests.Remove(completed);
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private async Task HandleRequestAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                var header = await HttpHeaderReader.ReadAsync(stream, 16 * 1024, timeout.Token);
                var requestLine = header.Text.Split("\r\n", 2, StringSplitOptions.None)[0];
                var parts = requestLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                var path = parts.Length >= 2 ? parts[1] : "/";

                if (path is "/proxy.pac" or "/")
                {
                    await WriteResponseAsync(stream, 200, "OK", "application/x-ns-proxy-autoconfig", _pacContent, timeout.Token);
                }
                else
                {
                    await WriteResponseAsync(stream, 404, "Not Found", "text/plain; charset=utf-8", "Not found.", timeout.Token);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _log.Warning($"PAC 请求失败：{ex.Message}", route: "PAC");
            }
        }
    }

    private static async Task WriteResponseAsync(
        Stream stream,
        int statusCode,
        string reason,
        string contentType,
        string body,
        CancellationToken cancellationToken)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {statusCode} {reason}\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {bodyBytes.Length}\r\n" +
            "Cache-Control: no-cache, no-store, must-revalidate\r\n" +
            "Pragma: no-cache\r\n" +
            "Connection: close\r\n\r\n");
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(bodyBytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
}

