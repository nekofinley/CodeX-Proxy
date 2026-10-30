using CodeXProxy.Security;

namespace CodeXProxy.Proxy;

public enum UpstreamProxyKind
{
    Http,
    Socks5
}

public sealed class UpstreamProxyConfig
{
    public UpstreamProxyConfig(
        UpstreamProxyKind kind,
        string host,
        int port,
        ProxyCredential? credential = null)
    {
        Kind = kind;
        Host = host;
        Port = port;
        Credential = credential;
    }

    public UpstreamProxyKind Kind { get; }

    public string Host { get; }

    public int Port { get; }

    public ProxyCredential? Credential { get; }

    public string RouteLabel => Kind == UpstreamProxyKind.Http ? "HTTP upstream" : "SOCKS5 upstream";

    public string EndpointDisplay
    {
        get
        {
            var host = Host.Contains(':', StringComparison.Ordinal) ? $"[{Host}]" : Host;
            return $"{Kind.ToString().ToLowerInvariant()}://{host}:{Port}";
        }
    }

    public static UpstreamProxyConfig Parse(string? address, ProxyCredential? credential)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            throw new FormatException("请填写上游代理地址。");
        }

        var normalized = address.Trim();
        if (!normalized.Contains("://", StringComparison.Ordinal))
        {
            normalized = "http://" + normalized;
        }

        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
        {
            throw new FormatException("上游代理地址格式无效。");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new FormatException("请不要在代理地址中写入用户名或密码，请使用独立输入框。");
        }

        if (!string.IsNullOrEmpty(uri.PathAndQuery) && uri.PathAndQuery != "/")
        {
            throw new FormatException("上游代理地址不能包含路径。");
        }

        var kind = uri.Scheme.ToLowerInvariant() switch
        {
            "http" => UpstreamProxyKind.Http,
            "socks5" or "socks5h" => UpstreamProxyKind.Socks5,
            "https" => throw new FormatException("暂不支持 HTTPS 上游代理，请使用 http:// 或 socks5://。"),
            _ => throw new FormatException("仅支持 http:// 和 socks5:// 上游代理。")
        };

        if (string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new FormatException("上游代理地址缺少主机名。");
        }

        var defaultPort = kind == UpstreamProxyKind.Http ? 8080 : 1080;
        var port = uri.Port > 0 ? uri.Port : defaultPort;
        return new UpstreamProxyConfig(kind, uri.Host, port, credential);
    }
}

public sealed record TargetEndpoint(string Host, int Port)
{
    public string Authority
    {
        get
        {
            var host = Host.Contains(':', StringComparison.Ordinal) ? $"[{Host}]" : Host;
            return $"{host}:{Port}";
        }
    }

    public static TargetEndpoint Parse(string target, int defaultPort)
    {
        var value = target.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new FormatException("目标地址为空。");
        }

        if (value.StartsWith("[", StringComparison.Ordinal))
        {
            var closingBracket = value.IndexOf(']');
            if (closingBracket <= 1)
            {
                throw new FormatException("IPv6 目标地址无效。");
            }

            var host = value[1..closingBracket];
            var remainder = value[(closingBracket + 1)..];
            var port = defaultPort;
            if (remainder.StartsWith(':'))
            {
                if (!int.TryParse(remainder[1..], out port) || port is < 1 or > 65535)
                {
                    throw new FormatException("目标端口无效。");
                }
            }
            else if (remainder.Length > 0)
            {
                throw new FormatException("目标地址无效。");
            }

            return new TargetEndpoint(host, port);
        }

        if (value.Contains(':', StringComparison.Ordinal))
        {
            var separator = value.LastIndexOf(':');
            if (separator <= 0)
            {
                throw new FormatException("目标地址无效。");
            }

            var host = value[..separator];
            if (!int.TryParse(value[(separator + 1)..], out var port) || port is < 1 or > 65535)
            {
                throw new FormatException("目标端口无效。");
            }

            if (host.Contains(':', StringComparison.Ordinal))
            {
                throw new FormatException("IPv6 地址需要使用方括号。");
            }

            return new TargetEndpoint(host, port);
        }

        return new TargetEndpoint(value, defaultPort);
    }
}

public sealed class UpstreamProxyException : IOException
{
    public UpstreamProxyException(string message)
        : base(message)
    {
    }
}

