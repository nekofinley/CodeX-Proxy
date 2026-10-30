using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using CodeXProxy.Configuration;
using CodeXProxy.Logging;

namespace CodeXProxy.Proxy;

public sealed class LocalProxyServer : IAsyncDisposable
{
    private const int MaxHeaderBytes = 64 * 1024;
    private readonly DomainRules _rules;
    private readonly UpstreamProxyConfig _upstream;
    private readonly UpstreamConnector _upstreamConnector;
    private readonly ProxyLogStore _log;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<TcpClient, byte> _clients = new();
    private readonly ConcurrentDictionary<int, Task> _connections = new();
    private TcpListener? _listener;
    private Task? _acceptLoop;
    private int _connectionSequence;
    private int _activeConnections;

    public LocalProxyServer(
        DomainRules rules,
        UpstreamProxyConfig upstream,
        ProxyLogStore log)
    {
        _rules = rules;
        _upstream = upstream;
        _upstreamConnector = new UpstreamConnector(upstream);
        _log = log;
    }

    public int Port { get; private set; }

    public int ActiveConnectionCount => Volatile.Read(ref _activeConnections);

    public bool IsRunning => _listener is not null;

    public Task StartAsync()
    {
        if (_listener is not null)
        {
            throw new InvalidOperationException("本地代理已启动。");
        }

        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start(backlog: 128);
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = AcceptLoopAsync(_shutdown.Token);
        _log.Success($"本地 HTTP 代理已监听 127.0.0.1:{Port}", route: "LISTEN");
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
        foreach (var client in _clients.Keys)
        {
            client.Dispose();
        }

        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop;
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
        }

        var connections = _connections.Values.ToArray();
        if (connections.Length > 0)
        {
            try
            {
                await Task.WhenAll(connections);
            }
            catch
            {
            }
        }

        _listener = null;
        _acceptLoop = null;
        _log.Info("本地 HTTP 代理已停止", route: "STOP");
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

            client.NoDelay = true;
            _clients.TryAdd(client, 0);
            var sequence = Interlocked.Increment(ref _connectionSequence);
            var task = HandleClientAsync(client, cancellationToken);
            _connections[sequence] = task;
            _ = task.ContinueWith(
                completedTask => _connections.TryRemove(sequence, out var _),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _activeConnections);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var clientStream = client.GetStream();
            HttpHeaderBlock header;
            using (var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                headerTimeout.CancelAfter(TimeSpan.FromSeconds(30));
                try
                {
                    header = await HttpHeaderReader.ReadAsync(clientStream, MaxHeaderBytes, headerTimeout.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    await WriteErrorResponseAsync(clientStream, 408, "Request Timeout", "Request header timeout.", cancellationToken);
                    return;
                }
            }

            ProxyHttpRequestHead request;
            try
            {
                request = ProxyHttpRequestHead.Parse(header.Text);
            }
            catch (FormatException ex)
            {
                await WriteErrorResponseAsync(clientStream, 400, "Bad Request", ex.Message, cancellationToken);
                return;
            }

            if (request.Method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
            {
                await HandleConnectAsync(client, clientStream, header, request, stopwatch, cancellationToken);
            }
            else
            {
                await HandleHttpAsync(clientStream, header, request, stopwatch, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _log.Error($"代理连接失败：{ex.Message}", route: "ERROR");
        }
        finally
        {
            Interlocked.Decrement(ref _activeConnections);
            _clients.TryRemove(client, out _);
            client.Dispose();
        }
    }

    private async Task HandleConnectAsync(
        TcpClient client,
        NetworkStream clientStream,
        HttpHeaderBlock header,
        ProxyHttpRequestHead request,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        TargetEndpoint target;
        try
        {
            target = TargetEndpoint.Parse(request.Target, 443);
        }
        catch (FormatException ex)
        {
            await WriteErrorResponseAsync(clientStream, 400, "Bad Request", ex.Message, cancellationToken);
            return;
        }

        var matchedRule = _rules.FindMatch(target.Host);
        var useUpstream = matchedRule is not null;
        TunnelConnection? tunnel = null;
        var established = false;
        try
        {
            if (useUpstream)
            {
                tunnel = await _upstreamConnector.OpenTunnelAsync(target.Host, target.Port, cancellationToken);
            }
            else
            {
                var directClient = await TcpTransport.ConnectAsync(target.Host, target.Port, cancellationToken);
                tunnel = new TunnelConnection(directClient, directClient.GetStream(), []);
            }

            var route = useUpstream ? _upstream.RouteLabel : "DIRECT";
            await clientStream.WriteAsync(
                Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\nProxy-Agent: CodeXProxy/0.1\r\n\r\n"),
                cancellationToken);
            established = true;

            if (header.TrailingBytes.Length > 0)
            {
                await tunnel.Stream.WriteAsync(header.TrailingBytes, cancellationToken);
            }

            if (tunnel.InitialData.Length > 0)
            {
                await clientStream.WriteAsync(tunnel.InitialData, cancellationToken);
            }

            _log.Success(
                "CONNECT 隧道已建立",
                target.Host,
                matchedRule,
                route,
                (int)stopwatch.ElapsedMilliseconds);

            await RelayAsync(clientStream, tunnel.Stream, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _log.Error(
                $"CONNECT {target.Authority} 失败：{ex.Message}",
                target.Host,
                matchedRule,
                useUpstream ? _upstream.RouteLabel : "DIRECT");
            try
            {
                if (!established)
                {
                    await WriteErrorResponseAsync(clientStream, 502, "Bad Gateway", "Unable to establish tunnel.", cancellationToken);
                }
            }
            catch
            {
            }
        }
        finally
        {
            if (tunnel is not null)
            {
                await tunnel.DisposeAsync();
            }
        }
    }

    private async Task HandleHttpAsync(
        NetworkStream clientStream,
        HttpHeaderBlock header,
        ProxyHttpRequestHead request,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        Uri? absoluteUri = null;
        if (request.Target.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(request.Target, UriKind.Absolute, out var parsedUri)
                || !parsedUri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase))
            {
                await WriteErrorResponseAsync(
                    clientStream,
                    400,
                    "Bad Request",
                    "Only HTTP proxy requests and HTTPS CONNECT tunnels are supported.",
                    cancellationToken);
                return;
            }

            absoluteUri = parsedUri;
        }

        var host = absoluteUri?.Host ?? request.GetHeader("Host");
        if (string.IsNullOrWhiteSpace(host))
        {
            await WriteErrorResponseAsync(clientStream, 400, "Bad Request", "Missing Host header.", cancellationToken);
            return;
        }

        TargetEndpoint target;
        try
        {
            if (absoluteUri is not null)
            {
                target = new TargetEndpoint(absoluteUri.Host, absoluteUri.Port > 0 ? absoluteUri.Port : 80);
            }
            else
            {
                target = TargetEndpoint.Parse(host, 80);
            }
        }
        catch (FormatException ex)
        {
            await WriteErrorResponseAsync(clientStream, 400, "Bad Request", ex.Message, cancellationToken);
            return;
        }

        var matchedRule = _rules.FindMatch(target.Host);
        var useUpstream = matchedRule is not null;
        TunnelConnection? tunnel = null;
        var requestForwarded = false;
        try
        {
            var authority = target.Authority;
            var originTarget = absoluteUri?.PathAndQuery ?? request.Target;
            if (string.IsNullOrEmpty(originTarget))
            {
                originTarget = "/";
            }

            string route;
            string forwardTarget;
            string? proxyAuthorization = null;

            if (useUpstream && _upstream.Kind == UpstreamProxyKind.Http)
            {
                var upstreamClient = await TcpTransport.ConnectAsync(
                    _upstream.Host,
                    _upstream.Port,
                    cancellationToken);
                tunnel = new TunnelConnection(upstreamClient, upstreamClient.GetStream(), []);
                route = _upstream.RouteLabel;
                proxyAuthorization = _upstreamConnector.ProxyAuthorization;
                var hostForUri = target.Host.Contains(':', StringComparison.Ordinal)
                    ? $"[{target.Host}]"
                    : target.Host;
                var portPart = target.Port == 80 ? string.Empty : $":{target.Port}";
                forwardTarget = absoluteUri is not null
                    ? request.Target
                    : $"http://{hostForUri}{portPart}{originTarget}";
            }
            else if (useUpstream)
            {
                tunnel = await _upstreamConnector.OpenTunnelAsync(target.Host, target.Port, cancellationToken);
                route = _upstream.RouteLabel;
                forwardTarget = originTarget;
            }
            else
            {
                var directClient = await TcpTransport.ConnectAsync(target.Host, target.Port, cancellationToken);
                tunnel = new TunnelConnection(directClient, directClient.GetStream(), []);
                route = "DIRECT";
                forwardTarget = originTarget;
            }

            var forwardHeader = request.BuildForwardHeader(forwardTarget, proxyAuthorization, authority);
            await tunnel.Stream.WriteAsync(Encoding.Latin1.GetBytes(forwardHeader), cancellationToken);
            requestForwarded = true;
            if (header.TrailingBytes.Length > 0)
            {
                await tunnel.Stream.WriteAsync(header.TrailingBytes, cancellationToken);
            }

            if (tunnel.InitialData.Length > 0)
            {
                await clientStream.WriteAsync(tunnel.InitialData, cancellationToken);
            }

            _log.Success(
                $"{request.Method} 请求已转发",
                target.Host,
                matchedRule,
                route,
                (int)stopwatch.ElapsedMilliseconds);

            await RelayAsync(clientStream, tunnel.Stream, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _log.Error(
                $"{request.Method} {target.Authority} 失败：{ex.Message}",
                target.Host,
                matchedRule,
                useUpstream ? _upstream.RouteLabel : "DIRECT");
            try
            {
                if (!requestForwarded)
                {
                    await WriteErrorResponseAsync(clientStream, 502, "Bad Gateway", "Unable to forward request.", cancellationToken);
                }
            }
            catch
            {
            }
        }
        finally
        {
            if (tunnel is not null)
            {
                await tunnel.DisposeAsync();
            }
        }
    }

    private static async Task RelayAsync(
        Stream clientStream,
        Stream upstreamStream,
        CancellationToken cancellationToken)
    {
        using var relayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var clientToUpstream = CopyAsync(clientStream, upstreamStream, relayCancellation.Token);
        var upstreamToClient = CopyAsync(upstreamStream, clientStream, relayCancellation.Token);
        await Task.WhenAny(clientToUpstream, upstreamToClient);
        relayCancellation.Cancel();
        await SuppressRelayErrorsAsync(clientToUpstream);
        await SuppressRelayErrorsAsync(upstreamToClient);
    }

    private static async Task CopyAsync(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        var buffer = new byte[32 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            await destination.FlushAsync(cancellationToken);
        }
    }

    private static async Task SuppressRelayErrorsAsync(Task task)
    {
        try
        {
            await task;
        }
        catch
        {
        }
    }

    private static async Task WriteErrorResponseAsync(
        Stream stream,
        int statusCode,
        string reason,
        string message,
        CancellationToken cancellationToken)
    {
        var body = Encoding.UTF8.GetBytes(message);
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {statusCode} {reason}\r\n" +
            "Content-Type: text/plain; charset=utf-8\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Connection: close\r\n" +
            "Proxy-Agent: CodeXProxy/0.1\r\n\r\n");
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(body, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
}
