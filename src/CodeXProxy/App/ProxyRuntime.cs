using CodeXProxy.Configuration;
using CodeXProxy.Logging;
using CodeXProxy.Proxy;
using CodeXProxy.Security;
using CodeXProxy.Windows;

namespace CodeXProxy.App;

internal sealed class ProxyRuntime : IAsyncDisposable
{
    private readonly LocalProxyServer _localProxy;
    private readonly PacServer _pacServer;
    private readonly SystemProxyManager _systemProxy;
    private bool _systemProxyActive;
    private bool _servicesStopped;

    private ProxyRuntime(
        LocalProxyServer localProxy,
        PacServer pacServer,
        SystemProxyManager systemProxy,
        string upstreamDisplay,
        int ruleCount)
    {
        _localProxy = localProxy;
        _pacServer = pacServer;
        _systemProxy = systemProxy;
        UpstreamDisplay = upstreamDisplay;
        RuleCount = ruleCount;
    }

    public int ProxyPort => _localProxy.Port;

    public int PacPort => _pacServer.Port;

    public string PacUrl => _pacServer.Url;

    public string UpstreamDisplay { get; }

    public int RuleCount { get; }

    public int ActiveConnections => _localProxy.ActiveConnectionCount;

    public static async Task<ProxyRuntime> StartAsync(
        AppConfig config,
        ProxyCredential? credential,
        ProxyLogStore log,
        SystemProxyManager systemProxy,
        CancellationToken cancellationToken = default)
    {
        var rules = new DomainRules(config.DomainRules);
        if (rules.NormalizedRules.Count == 0)
        {
            throw new InvalidOperationException("至少需要一条有效的域名规则。");
        }

        var upstream = UpstreamProxyConfig.Parse(config.UpstreamProxy, credential);
        var localProxy = new LocalProxyServer(rules, upstream, log);
        PacServer? pacServer = null;
        var systemProxyEnabled = false;
        try
        {
            await localProxy.StartAsync();
            var pacContent = PacDocument.Build(localProxy.Port, rules.NormalizedRules);
            pacServer = new PacServer(pacContent, log);
            await pacServer.StartAsync();

            systemProxy.Enable(pacServer.Url);
            systemProxyEnabled = true;

            var runtime = new ProxyRuntime(
                localProxy,
                pacServer,
                systemProxy,
                upstream.EndpointDisplay,
                rules.NormalizedRules.Count)
            {
                _systemProxyActive = true
            };
            log.Success(
                $"代理已开启：{rules.NormalizedRules.Count} 条规则，系统 PAC 已指向本地服务",
                route: "SYSTEM");
            return runtime;
        }
        catch
        {
            if (systemProxyEnabled)
            {
                try
                {
                    systemProxy.Restore();
                }
                catch
                {
                }
            }

            if (pacServer is not null)
            {
                await pacServer.DisposeAsync();
            }

            await localProxy.DisposeAsync();
            throw;
        }
    }

    public async Task StopAsync()
    {
        Exception? restoreError = null;
        if (_systemProxyActive)
        {
            try
            {
                _systemProxy.Restore();
                _systemProxyActive = false;
            }
            catch (Exception ex)
            {
                restoreError = ex;
            }
        }

        if (!_servicesStopped)
        {
            _servicesStopped = true;
            await _pacServer.DisposeAsync();
            await _localProxy.DisposeAsync();
        }

        if (restoreError is not null)
        {
            throw new InvalidOperationException(
                $"本地服务已停止，但 Windows 代理恢复失败：{restoreError.Message}。下次启动会重试恢复。",
                restoreError);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}

