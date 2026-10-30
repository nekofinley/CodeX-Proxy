using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using CodeXProxy.Security;

namespace CodeXProxy.Proxy;

public sealed class TunnelConnection : IAsyncDisposable
{
    public TunnelConnection(TcpClient client, Stream stream, byte[] initialData)
    {
        Client = client;
        Stream = stream;
        InitialData = initialData;
    }

    public TcpClient Client { get; }

    public Stream Stream { get; }

    public byte[] InitialData { get; }

    public async ValueTask DisposeAsync()
    {
        await Stream.DisposeAsync();
        Client.Dispose();
    }
}

public static class TcpTransport
{
    public static async Task<TcpClient> ConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        var client = new TcpClient
        {
            NoDelay = true
        };

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            await client.ConnectAsync(host, port, timeout.Token);
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            return client;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            client.Dispose();
            throw new TimeoutException($"连接 {host}:{port} 超时。");
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}

public sealed class UpstreamConnector
{
    private static readonly byte[] CrlfCrlf = "\r\n\r\n"u8.ToArray();
    private readonly UpstreamProxyConfig _proxy;

    public UpstreamConnector(UpstreamProxyConfig proxy)
    {
        _proxy = proxy;
    }

    public Task<TunnelConnection> OpenTunnelAsync(
        string targetHost,
        int targetPort,
        CancellationToken cancellationToken)
    {
        return _proxy.Kind switch
        {
            UpstreamProxyKind.Http => OpenHttpTunnelAsync(targetHost, targetPort, cancellationToken),
            UpstreamProxyKind.Socks5 => OpenSocks5TunnelAsync(targetHost, targetPort, cancellationToken),
            _ => throw new NotSupportedException("未知的上游代理类型。")
        };
    }

    public async Task<TimeSpan> TestAsync(string targetHost, int targetPort, CancellationToken cancellationToken)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await using var tunnel = await OpenTunnelAsync(targetHost, targetPort, cancellationToken);
        stopwatch.Stop();
        return stopwatch.Elapsed;
    }

    private async Task<TunnelConnection> OpenHttpTunnelAsync(
        string targetHost,
        int targetPort,
        CancellationToken cancellationToken)
    {
        var client = await TcpTransport.ConnectAsync(_proxy.Host, _proxy.Port, cancellationToken);
        try
        {
            var stream = client.GetStream();
            var authority = FormatAuthority(targetHost, targetPort);
            var authorization = BuildProxyAuthorization();
            var request = new StringBuilder()
                .Append("CONNECT ").Append(authority).Append(" HTTP/1.1\r\n")
                .Append("Host: ").Append(authority).Append("\r\n")
                .Append("Proxy-Connection: Keep-Alive\r\n")
                .Append("User-Agent: CodeXProxy/0.1\r\n");
            if (authorization is not null)
            {
                request.Append("Proxy-Authorization: ").Append(authorization).Append("\r\n");
            }

            request.Append("\r\n");
            var requestBytes = Encoding.ASCII.GetBytes(request.ToString());
            await stream.WriteAsync(requestBytes, cancellationToken);
            await stream.FlushAsync(cancellationToken);

            HttpHeaderBlock response;
            using (var responseTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                responseTimeout.CancelAfter(TimeSpan.FromSeconds(20));
                response = await HttpHeaderReader.ReadAsync(stream, 32 * 1024, responseTimeout.Token);
            }

            var statusLine = response.Text.Split("\r\n", 2, StringSplitOptions.None)[0];
            var statusParts = statusLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (statusParts.Length < 2 || !int.TryParse(statusParts[1], out var statusCode))
            {
                throw new UpstreamProxyException("上游 HTTP 代理返回了无效的 CONNECT 响应。");
            }

            if (statusCode != 200)
            {
                throw new UpstreamProxyException($"上游 HTTP 代理拒绝 CONNECT，状态码 {statusCode}。");
            }

            return new TunnelConnection(client, stream, response.TrailingBytes);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private async Task<TunnelConnection> OpenSocks5TunnelAsync(
        string targetHost,
        int targetPort,
        CancellationToken cancellationToken)
    {
        var client = await TcpTransport.ConnectAsync(_proxy.Host, _proxy.Port, cancellationToken);
        try
        {
            var stream = client.GetStream();
            var credential = _proxy.Credential;
            var methods = credential is null ? new byte[] { 0x00 } : new byte[] { 0x00, 0x02 };
            var greeting = new byte[2 + methods.Length];
            greeting[0] = 0x05;
            greeting[1] = (byte)methods.Length;
            methods.CopyTo(greeting, 2);
            await stream.WriteAsync(greeting, cancellationToken);

            var choice = new byte[2];
            await stream.ReadExactlyAsync(choice, cancellationToken);
            if (choice[0] != 0x05)
            {
                throw new UpstreamProxyException("上游 SOCKS5 代理协议版本无效。");
            }

            if (choice[1] == 0xFF)
            {
                throw new UpstreamProxyException("上游 SOCKS5 代理不接受当前认证方式。");
            }

            if (choice[1] == 0x02)
            {
                if (credential is null)
                {
                    throw new UpstreamProxyException("上游 SOCKS5 代理要求用户名密码认证。");
                }

                var username = Encoding.UTF8.GetBytes(credential.Username);
                var password = Encoding.UTF8.GetBytes(credential.Password);
                if (username.Length > 255 || password.Length > 255)
                {
                    throw new UpstreamProxyException("SOCKS5 用户名或密码超过 255 字节。");
                }

                var auth = new byte[3 + username.Length + password.Length];
                auth[0] = 0x01;
                auth[1] = (byte)username.Length;
                username.CopyTo(auth, 2);
                auth[2 + username.Length] = (byte)password.Length;
                password.CopyTo(auth, 3 + username.Length);
                await stream.WriteAsync(auth, cancellationToken);

                var authResponse = new byte[2];
                await stream.ReadExactlyAsync(authResponse, cancellationToken);
                if (authResponse[0] != 0x01 || authResponse[1] != 0x00)
                {
                    throw new UpstreamProxyException("上游 SOCKS5 代理用户名或密码错误。");
                }
            }
            else if (choice[1] != 0x00)
            {
                throw new UpstreamProxyException($"上游 SOCKS5 代理选择了不支持的认证方式 0x{choice[1]:X2}。");
            }

            var (addressType, addressBytes) = BuildSocksAddress(targetHost);
            var connect = new byte[6 + addressBytes.Length];
            connect[0] = 0x05;
            connect[1] = 0x01;
            connect[2] = 0x00;
            connect[3] = addressType;
            addressBytes.CopyTo(connect, 4);
            BinaryPrimitives.WriteUInt16BigEndian(connect.AsSpan(4 + addressBytes.Length), (ushort)targetPort);
            await stream.WriteAsync(connect, cancellationToken);

            var replyHead = new byte[4];
            await stream.ReadExactlyAsync(replyHead, cancellationToken);
            if (replyHead[0] != 0x05 || replyHead[1] != 0x00)
            {
                var reason = replyHead[1] switch
                {
                    0x01 => "一般故障",
                    0x02 => "规则禁止",
                    0x03 => "网络不可达",
                    0x04 => "主机不可达",
                    0x05 => "连接被拒绝",
                    0x06 => "TTL 过期",
                    0x07 => "命令不支持",
                    0x08 => "地址类型不支持",
                    _ => $"错误 0x{replyHead[1]:X2}"
                };
                throw new UpstreamProxyException($"上游 SOCKS5 代理连接失败：{reason}。");
            }

            var addressLength = replyHead[3] switch
            {
                0x01 => 4,
                0x03 => await ReadSingleByteAsync(stream, cancellationToken),
                0x04 => 16,
                _ => throw new UpstreamProxyException("上游 SOCKS5 代理返回了未知地址类型。")
            };
            var discard = new byte[addressLength + 2];
            await stream.ReadExactlyAsync(discard, cancellationToken);

            return new TunnelConnection(client, stream, []);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private string? BuildProxyAuthorization()
    {
        if (_proxy.Credential is null)
        {
            return null;
        }

        var value = $"{_proxy.Credential.Username}:{_proxy.Credential.Password}";
        return "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    }

    public string? ProxyAuthorization => BuildProxyAuthorization();

    private static async Task<int> ReadSingleByteAsync(Stream stream, CancellationToken cancellationToken)
    {
        var value = new byte[1];
        await stream.ReadExactlyAsync(value, cancellationToken);
        return value[0];
    }

    private static string FormatAuthority(string host, int port)
    {
        var formattedHost = host.Contains(':', StringComparison.Ordinal) ? $"[{host}]" : host;
        return $"{formattedHost}:{port}";
    }

    private static (byte AddressType, byte[] AddressBytes) BuildSocksAddress(string host)
    {
        if (IPAddress.TryParse(host, out var ipAddress))
        {
            var bytes = ipAddress.GetAddressBytes();
            if (ipAddress.AddressFamily == AddressFamily.InterNetworkV6
                && ipAddress.IsIPv4MappedToIPv6)
            {
                ipAddress = ipAddress.MapToIPv4();
                bytes = ipAddress.GetAddressBytes();
            }

            return ipAddress.AddressFamily switch
            {
                AddressFamily.InterNetwork => (0x01, bytes),
                AddressFamily.InterNetworkV6 => (0x04, bytes),
                _ => throw new UpstreamProxyException("目标 IP 地址类型不受 SOCKS5 支持。")
            };
        }

        var asciiHost = new IdnMapping().GetAscii(host);
        var hostBytes = Encoding.ASCII.GetBytes(asciiHost);
        if (hostBytes.Length > 255)
        {
            throw new UpstreamProxyException("目标域名超过 SOCKS5 协议限制。");
        }

        return (0x03, [.. new[] { (byte)hostBytes.Length }, .. hostBytes]);
    }
}

