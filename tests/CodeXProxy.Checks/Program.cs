using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using CodeXProxy.Apps;
using CodeXProxy.Configuration;
using CodeXProxy.Logging;
using CodeXProxy.Proxy;
using CodeXProxy.Security;

namespace CodeXProxy.Checks;

internal static class Program
{
    private static readonly List<(string Name, Func<Task> Check)> Checks =
    [
        ("域名规则匹配", CheckDomainRules),
        ("PAC 路由生成", CheckPacDocument),
        ("上游地址解析", CheckUpstreamParsing),
        ("HTTP CONNECT 上游转发", CheckHttpUpstreamConnect),
        ("HTTP 明文上游转发", CheckHttpUpstreamPlainRequest),
        ("非目标域名直连", CheckDirectTunnel),
        ("SOCKS5 域名转发", CheckSocks5Upstream),
        ("隧道流式传输", CheckStreamingTunnel),
        ("PAC 本地服务", CheckPacServer),
        ("配置读写", CheckConfigRoundTrip),
        ("Windows 凭据存储", CheckCredentialStore),
        ("目标进程环境变量继承", CheckLauncherEnvironment),
        ("日志脱敏", CheckLogRedaction)
    ];

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--write-env")
        {
            var values = string.Join(
                Environment.NewLine,
                $"HTTP_PROXY={Environment.GetEnvironmentVariable("HTTP_PROXY")}",
                $"HTTPS_PROXY={Environment.GetEnvironmentVariable("HTTPS_PROXY")}",
                $"ALL_PROXY={Environment.GetEnvironmentVariable("ALL_PROXY")}",
                $"NO_PROXY={Environment.GetEnvironmentVariable("NO_PROXY")}");
            var temporaryFile = args[1] + ".tmp";
            await File.WriteAllTextAsync(temporaryFile, values);
            File.Move(temporaryFile, args[1]);
            return 0;
        }

        var failures = 0;
        foreach (var check in Checks)
        {
            try
            {
                await RunWithTimeoutAsync(check.Check, TimeSpan.FromSeconds(20));
                Console.WriteLine($"PASS  {check.Name}");
            }
            catch (Exception ex)
            {
                failures++;
                Console.WriteLine($"FAIL  {check.Name}");
                Console.WriteLine($"      {ex.GetType().Name}: {ex.Message}");
            }
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? $"全部 {Checks.Count} 项检查通过。"
            : $"{failures}/{Checks.Count} 项检查失败。");
        return failures == 0 ? 0 : 1;
    }

    private static Task CheckDomainRules()
    {
        var rules = new DomainRules(["openai.com", "*.chatgpt.com", "api-*.example.org"]);
        Assert(rules.IsMatch("openai.com"), "裸域名应命中");
        Assert(rules.IsMatch("api.openai.com"), "子域名应命中");
        Assert(rules.IsMatch("chatgpt.com"), "通配规则应包含主域");
        Assert(rules.IsMatch("chatgpt.com."), "末尾点应被规范化");
        Assert(rules.IsMatch("api-v2.example.org"), "局部通配应命中");
        Assert(!rules.IsMatch("notopenai.com"), "后缀相似域名不应命中");
        Assert(!rules.IsMatch("localhost"), "localhost 不应命中");
        Assert(!rules.IsMatch("127.0.0.1"), "回环地址不应命中");
        return Task.CompletedTask;
    }

    private static Task CheckPacDocument()
    {
        var pac = PacDocument.Build(32123, ["openai.com", "*.chatgpt.com"]);
        Assert(pac.Contains("PROXY 127.0.0.1:32123", StringComparison.Ordinal), "PAC 应包含本地代理");
        Assert(pac.Contains("\"openai.com\"", StringComparison.Ordinal), "PAC 应包含规则");
        Assert(pac.Contains("return \"DIRECT\";", StringComparison.Ordinal), "PAC 应包含直连分支");
        Assert(!pac.Contains("PROXY 127.0.0.1:32123; DIRECT", StringComparison.Ordinal), "目标域名不应回退直连");
        return Task.CompletedTask;
    }

    private static Task CheckUpstreamParsing()
    {
        var http = UpstreamProxyConfig.Parse("127.0.0.1:7890", null);
        Assert(http.Kind == UpstreamProxyKind.Http && http.Port == 7890, "HTTP 上游解析失败");

        var socks = UpstreamProxyConfig.Parse("socks5://proxy.local:1081", new ProxyCredential("u", "p"));
        Assert(socks.Kind == UpstreamProxyKind.Socks5 && socks.Port == 1081, "SOCKS5 上游解析失败");

        AssertThrows<FormatException>(() => UpstreamProxyConfig.Parse("https://proxy.local:443", null));
        AssertThrows<FormatException>(() => UpstreamProxyConfig.Parse("http://user:pass@proxy.local:8080", null));
        return Task.CompletedTask;
    }

    private static async Task CheckHttpUpstreamConnect()
    {
        var receivedConnect = string.Empty;
        await using var upstream = new TestTcpServer(async stream =>
        {
            var header = await HttpHeaderReader.ReadAsync(stream, 16 * 1024, CancellationToken.None);
            receivedConnect = header.Text;
            await stream.WriteAsync(
                Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n"));
            await EchoAsync(stream);
        });
        await upstream.StartAsync();

        var log = CreateLog();
        var proxyConfig = new UpstreamProxyConfig(UpstreamProxyKind.Http, "127.0.0.1", upstream.Port);
        await using var proxy = new LocalProxyServer(new DomainRules(["example.test"]), proxyConfig, log);
        await proxy.StartAsync();

        using var client = await ConnectAsync(proxy.Port);
        var clientStream = client.GetStream();
        await clientStream.WriteAsync(Encoding.ASCII.GetBytes(
            "CONNECT example.test:443 HTTP/1.1\r\nHost: example.test:443\r\n\r\n"));
        var response = await HttpHeaderReader.ReadAsync(clientStream, 16 * 1024, CancellationToken.None);
        Assert(response.Text.StartsWith("HTTP/1.1 200", StringComparison.Ordinal), "CONNECT 未成功");
        Assert(receivedConnect.Contains("CONNECT example.test:443", StringComparison.Ordinal), "上游未收到 CONNECT");

        await AssertEchoAsync(clientStream, "hello-http-tunnel");
    }

    private static async Task CheckHttpUpstreamPlainRequest()
    {
        var receivedRequest = string.Empty;
        await using var upstream = new TestTcpServer(async stream =>
        {
            var header = await HttpHeaderReader.ReadAsync(stream, 16 * 1024, CancellationToken.None);
            receivedRequest = header.Text;
            const string body = "plain-ok";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}"));
        });
        await upstream.StartAsync();

        var log = CreateLog();
        var upstreamConfig = new UpstreamProxyConfig(
            UpstreamProxyKind.Http,
            "127.0.0.1",
            upstream.Port,
            new ProxyCredential("proxy-user", "proxy-pass"));
        await using var proxy = new LocalProxyServer(new DomainRules(["example.test"]), upstreamConfig, log);
        await proxy.StartAsync();

        using var client = await ConnectAsync(proxy.Port);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            "GET http://example.test/path?q=1 HTTP/1.1\r\nHost: example.test\r\nConnection: close\r\n\r\n"));
        var response = await HttpHeaderReader.ReadAsync(stream, 16 * 1024, CancellationToken.None);
        var bodyBuffer = new byte[8];
        var initialBodyLength = Math.Min(bodyBuffer.Length, response.TrailingBytes.Length);
        response.TrailingBytes.AsSpan(0, initialBodyLength).CopyTo(bodyBuffer);
        if (initialBodyLength < bodyBuffer.Length)
        {
            await stream.ReadExactlyAsync(bodyBuffer.AsMemory(initialBodyLength));
        }

        Assert(receivedRequest.StartsWith("GET http://example.test/path?q=1 HTTP/1.1", StringComparison.Ordinal),
            "HTTP 上游请求行不是绝对 URI");
        var expectedAuthorization = "Proxy-Authorization: Basic "
            + Convert.ToBase64String(Encoding.UTF8.GetBytes("proxy-user:proxy-pass"));
        Assert(receivedRequest.Contains(expectedAuthorization, StringComparison.Ordinal),
            "HTTP 上游认证头未生成");
        Assert(response.Text.StartsWith("HTTP/1.1 200 OK", StringComparison.Ordinal), "明文响应无效");
        Assert(Encoding.ASCII.GetString(bodyBuffer) == "plain-ok", "明文响应体不正确");
    }

    private static async Task CheckDirectTunnel()
    {
        await using var echoServer = new TestTcpServer(EchoAsync);
        await echoServer.StartAsync();

        var log = CreateLog();
        var upstreamConfig = new UpstreamProxyConfig(UpstreamProxyKind.Http, "127.0.0.1", 1);
        await using var proxy = new LocalProxyServer(new DomainRules(["example.test"]), upstreamConfig, log);
        await proxy.StartAsync();

        using var client = await ConnectAsync(proxy.Port);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            $"CONNECT 127.0.0.1:{echoServer.Port} HTTP/1.1\r\nHost: 127.0.0.1:{echoServer.Port}\r\n\r\n"));
        var response = await HttpHeaderReader.ReadAsync(stream, 16 * 1024, CancellationToken.None);
        Assert(response.Text.StartsWith("HTTP/1.1 200", StringComparison.Ordinal), "直连隧道未建立");
        await AssertEchoAsync(stream, "direct-only");
    }

    private static async Task CheckSocks5Upstream()
    {
        string? requestedHost = null;
        await using var socks = new TestTcpServer(async stream =>
        {
            var greeting = new byte[3];
            await stream.ReadExactlyAsync(greeting);
            Assert(greeting[0] == 0x05, "SOCKS greeting 版本无效");
            await stream.WriteAsync(new byte[] { 0x05, 0x00 });

            var head = new byte[5];
            await stream.ReadExactlyAsync(head);
            Assert(head[3] == 0x03, "SOCKS 应传递域名");
            var hostBytes = new byte[head[4]];
            await stream.ReadExactlyAsync(hostBytes);
            requestedHost = Encoding.ASCII.GetString(hostBytes);
            var port = new byte[2];
            await stream.ReadExactlyAsync(port);

            await stream.WriteAsync(new byte[] { 0x05, 0x00, 0x00, 0x01, 127, 0, 0, 1, 0, 1 });
            await EchoAsync(stream);
        });
        await socks.StartAsync();

        var log = CreateLog();
        var upstreamConfig = new UpstreamProxyConfig(UpstreamProxyKind.Socks5, "127.0.0.1", socks.Port);
        await using var proxy = new LocalProxyServer(new DomainRules(["example.test"]), upstreamConfig, log);
        await proxy.StartAsync();

        using var client = await ConnectAsync(proxy.Port);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            "CONNECT example.test:443 HTTP/1.1\r\nHost: example.test:443\r\n\r\n"));
        var response = await HttpHeaderReader.ReadAsync(stream, 16 * 1024, CancellationToken.None);
        Assert(response.Text.StartsWith("HTTP/1.1 200", StringComparison.Ordinal), "SOCKS5 隧道未建立");
        Assert(requestedHost == "example.test", "SOCKS5 未收到目标域名");
        await AssertEchoAsync(stream, "socks-domain");
    }

    private static async Task CheckStreamingTunnel()
    {
        await using var upstream = new TestTcpServer(async stream =>
        {
            await HttpHeaderReader.ReadAsync(stream, 16 * 1024, CancellationToken.None);
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n"));
            await stream.WriteAsync(new byte[] { (byte)'A' });
            await Task.Delay(500);
            await stream.WriteAsync(new byte[] { (byte)'B' });
            await Task.Delay(500);
        });
        await upstream.StartAsync();

        var log = CreateLog();
        var upstreamConfig = new UpstreamProxyConfig(UpstreamProxyKind.Http, "127.0.0.1", upstream.Port);
        await using var proxy = new LocalProxyServer(new DomainRules(["example.test"]), upstreamConfig, log);
        await proxy.StartAsync();

        using var client = await ConnectAsync(proxy.Port);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            "CONNECT example.test:443 HTTP/1.1\r\nHost: example.test:443\r\n\r\n"));
        var response = await HttpHeaderReader.ReadAsync(stream, 16 * 1024, CancellationToken.None);
        Assert(response.Text.StartsWith("HTTP/1.1 200", StringComparison.Ordinal), "流式测试隧道未建立");

        var stopwatch = Stopwatch.StartNew();
        var first = response.TrailingBytes.Length > 0
            ? response.TrailingBytes[0]
            : await ReadByteWithTimeoutAsync(stream, TimeSpan.FromMilliseconds(350));
        var firstElapsed = stopwatch.Elapsed;
        var second = await ReadByteWithTimeoutAsync(stream, TimeSpan.FromSeconds(3));
        var gap = stopwatch.Elapsed - firstElapsed;

        Assert(first == 'A' && second == 'B', "流式字节内容不正确");
        Assert(firstElapsed < TimeSpan.FromMilliseconds(400), "首个分块被缓冲");
        Assert(gap >= TimeSpan.FromMilliseconds(350), "第二个分块未按上游节奏到达");
    }

    private static async Task CheckPacServer()
    {
        var log = CreateLog();
        var content = PacDocument.Build(40123, ["openai.com"]);
        await using var pac = new PacServer(content, log);
        await pac.StartAsync();

        using var client = await ConnectAsync(pac.Port);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            $"GET /proxy.pac HTTP/1.1\r\nHost: 127.0.0.1:{pac.Port}\r\nConnection: close\r\n\r\n"));
        var response = await HttpHeaderReader.ReadAsync(stream, 16 * 1024, CancellationToken.None);
        Assert(response.Text.StartsWith("HTTP/1.1 200", StringComparison.Ordinal), "PAC 服务未返回 200");

        using var memory = new MemoryStream();
        memory.Write(response.TrailingBytes);
        await stream.CopyToAsync(memory);
        var body = Encoding.UTF8.GetString(memory.ToArray());
        Assert(body.Contains("FindProxyForURL", StringComparison.Ordinal), "PAC 响应体缺少脚本");
    }

    private static Task CheckConfigRoundTrip()
    {
        var directory = Path.Combine(Path.GetTempPath(), "CodeXProxy-Checks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var file = Path.Combine(directory, "config.json");
            var store = new ConfigStore(file);
            var config = new AppConfig
            {
                UpstreamProxy = "socks5://127.0.0.1:1080",
                DomainRules = ["*.openai.com", "chatgpt.com"]
            };
            store.Save(config);

            var loaded = store.Load();
            Assert(loaded.Warning is null, "配置读取不应有警告");
            Assert(loaded.Config.UpstreamProxy == "socks5://127.0.0.1:1080", "上游地址未保存");
            Assert(loaded.Config.DomainRules.SequenceEqual(["openai.com", "chatgpt.com"]), "域名规则未规范化");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        return Task.CompletedTask;
    }

    private static Task CheckCredentialStore()
    {
        var target = $"CodeXProxy/Checks/{Guid.NewGuid():N}";
        var store = new CredentialStore(target);
        try
        {
            store.Save("check-user", "p@ss-中文");
            var loaded = store.Load();
            Assert(loaded is not null, "凭据读取为空");
            Assert(loaded!.Username == "check-user", "凭据用户名不一致");
            Assert(loaded.Password == "p@ss-中文", "凭据密码不一致");
        }
        finally
        {
            store.Delete();
        }

        return Task.CompletedTask;
    }

    private static async Task CheckLauncherEnvironment()
    {
        var outputFile = Path.Combine(
            Path.GetTempPath(),
            "CodeXProxy-Checks",
            $"env-{Guid.NewGuid():N}.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(outputFile)!);
        try
        {
            var processPath = Environment.ProcessPath
                ?? throw new InvalidOperationException("无法确定当前测试进程路径。");
            string executable;
            string arguments;
            if (string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            {
                executable = processPath;
                arguments = $"\"{Assembly.GetEntryAssembly()!.Location}\" --write-env \"{outputFile}\"";
            }
            else
            {
                executable = processPath;
                arguments = $"--write-env \"{outputFile}\"";
            }

            TargetAppLauncher.Launch(
                new TargetAppConfig
                {
                    DisplayName = "EnvCheck",
                    Path = executable,
                    Arguments = arguments,
                    WorkingDirectory = Path.GetDirectoryName(processPath) ?? string.Empty
                },
                43210,
                CreateLog());

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!File.Exists(outputFile) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
            }

            Assert(File.Exists(outputFile), "目标进程未写出环境变量");
            var content = await File.ReadAllTextAsync(outputFile);
            Assert(content.Contains("HTTP_PROXY=http://127.0.0.1:43210", StringComparison.Ordinal), "HTTP_PROXY 未继承");
            Assert(content.Contains("HTTPS_PROXY=http://127.0.0.1:43210", StringComparison.Ordinal), "HTTPS_PROXY 未继承");
            Assert(content.Contains("ALL_PROXY=http://127.0.0.1:43210", StringComparison.Ordinal), "ALL_PROXY 未继承");
            Assert(content.Contains("NO_PROXY=localhost,127.0.0.1,::1,[::1]", StringComparison.Ordinal), "NO_PROXY 不正确");
        }
        finally
        {
            File.Delete(outputFile);
        }
    }

    private static Task CheckLogRedaction()
    {
        var redacted = ProxyLogStore.Redact(
            "Authorization: Bearer top-secret token=abc123 https://user:pass@example.com/path password=hunter2");
        Assert(!redacted.Contains("top-secret", StringComparison.Ordinal), "Authorization 值未脱敏");
        Assert(!redacted.Contains("abc123", StringComparison.Ordinal), "token 值未脱敏");
        Assert(!redacted.Contains("user:pass", StringComparison.Ordinal), "URL 用户信息未脱敏");
        Assert(!redacted.Contains("hunter2", StringComparison.Ordinal), "password 值未脱敏");
        return Task.CompletedTask;
    }

    private static ProxyLogStore CreateLog()
    {
        var directory = Path.Combine(Path.GetTempPath(), "CodeXProxy-Checks", "logs");
        Directory.CreateDirectory(directory);
        return new ProxyLogStore(directory);
    }

    private static async Task AssertEchoAsync(Stream stream, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await stream.WriteAsync(bytes);
        var received = new byte[bytes.Length];
        await stream.ReadExactlyAsync(received);
        Assert(Encoding.UTF8.GetString(received) == text, "隧道回显不一致");
    }

    private static async Task<TcpClient> ConnectAsync(int port)
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        return client;
    }

    private static async Task<byte> ReadByteWithTimeoutAsync(Stream stream, TimeSpan timeout)
    {
        var buffer = new byte[1];
        using var cancellation = new CancellationTokenSource(timeout);
        await stream.ReadExactlyAsync(buffer, cancellation.Token);
        return buffer[0];
    }

    private static async Task EchoAsync(Stream stream)
    {
        var buffer = new byte[4096];
        while (true)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(buffer);
            }
            catch (IOException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            if (read == 0)
            {
                return;
            }

            await stream.WriteAsync(buffer.AsMemory(0, read));
        }
    }

    private static async Task RunWithTimeoutAsync(Func<Task> action, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        var task = action();
        var completed = await Task.WhenAny(task, Task.Delay(Timeout.InfiniteTimeSpan, cancellation.Token));
        if (completed != task)
        {
            throw new TimeoutException($"检查超过 {timeout.TotalSeconds:0} 秒。");
        }

        await task;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void AssertThrows<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"预期抛出 {typeof(TException).Name}。");
    }

    private sealed class TestTcpServer : IAsyncDisposable
    {
        private readonly Func<NetworkStream, Task> _handler;
        private readonly CancellationTokenSource _shutdown = new();
        private readonly List<Task> _clients = [];
        private readonly object _gate = new();
        private TcpListener? _listener;
        private Task? _acceptLoop;

        public TestTcpServer(Func<NetworkStream, Task> handler)
        {
            _handler = handler;
        }

        public int Port { get; private set; }

        public Task StartAsync()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _acceptLoop = AcceptLoopAsync();
            return Task.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            _shutdown.Cancel();
            _listener?.Stop();
            try
            {
                if (_acceptLoop is not null)
                {
                    await _acceptLoop;
                }
            }
            catch
            {
            }

            Task[] pending;
            lock (_gate)
            {
                pending = _clients.ToArray();
            }

            try
            {
                await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch
            {
            }

            _shutdown.Dispose();
        }

        private async Task AcceptLoopAsync()
        {
            while (!_shutdown.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener!.AcceptTcpClientAsync(_shutdown.Token);
                }
                catch
                {
                    return;
                }

                var task = HandleAsync(client);
                lock (_gate)
                {
                    _clients.Add(task);
                }

                _ = task.ContinueWith(
                    completed =>
                    {
                        lock (_gate)
                        {
                            _clients.Remove(completed);
                        }
                    },
                    TaskScheduler.Default);
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    await _handler(client.GetStream());
                }
                catch
                {
                }
            }
        }
    }
}
