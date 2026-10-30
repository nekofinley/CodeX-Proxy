using System.Text;

namespace CodeXProxy.Proxy;

public sealed record HttpHeaderLine(string Name, string Value);

public sealed class ProxyHttpRequestHead
{
    private ProxyHttpRequestHead(
        string method,
        string target,
        string version,
        IReadOnlyList<HttpHeaderLine> headers)
    {
        Method = method;
        Target = target;
        Version = version;
        Headers = headers;
    }

    public string Method { get; }

    public string Target { get; }

    public string Version { get; }

    public IReadOnlyList<HttpHeaderLine> Headers { get; }

    public string? GetHeader(string name)
    {
        return Headers.FirstOrDefault(header => header.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;
    }

    public string BuildForwardHeader(
        string requestTarget,
        string? proxyAuthorization,
        string? targetAuthority)
    {
        var builder = new StringBuilder();
        builder.Append(Method).Append(' ').Append(requestTarget).Append(' ').Append(Version).Append("\r\n");

        var hasHost = false;
        foreach (var header in Headers)
        {
            if (header.Name.Equals("Proxy-Connection", StringComparison.OrdinalIgnoreCase)
                || header.Name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (header.Name.Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                hasHost = true;
            }

            builder.Append(header.Name).Append(": ").Append(header.Value).Append("\r\n");
        }

        if (!hasHost && !string.IsNullOrWhiteSpace(targetAuthority))
        {
            builder.Append("Host: ").Append(targetAuthority).Append("\r\n");
        }

        if (!string.IsNullOrWhiteSpace(proxyAuthorization))
        {
            builder.Append("Proxy-Authorization: ").Append(proxyAuthorization).Append("\r\n");
        }

        builder.Append("\r\n");
        return builder.ToString();
    }

    public static ProxyHttpRequestHead Parse(string headerText)
    {
        var lines = headerText.Split("\r\n", StringSplitOptions.None);
        if (lines.Length == 0 || string.IsNullOrWhiteSpace(lines[0]))
        {
            throw new FormatException("HTTP 请求行为空。");
        }

        var requestLine = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (requestLine.Length != 3)
        {
            throw new FormatException("HTTP 请求行无效。");
        }

        var headers = new List<HttpHeaderLine>();
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0)
            {
                break;
            }

            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                throw new FormatException("HTTP 请求头无效。");
            }

            headers.Add(new HttpHeaderLine(line[..separator].Trim(), line[(separator + 1)..].Trim()));
        }

        return new ProxyHttpRequestHead(requestLine[0], requestLine[1], requestLine[2], headers);
    }
}

public sealed record HttpHeaderBlock(byte[] HeaderBytes, byte[] TrailingBytes, string Text);

public static class HttpHeaderReader
{
    private static readonly byte[] Terminator = "\r\n\r\n"u8.ToArray();

    public static async Task<HttpHeaderBlock> ReadAsync(
        Stream stream,
        int maxHeaderBytes,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream(capacity: 4096);
        var chunk = new byte[4096];

        while (buffer.Length < maxHeaderBytes)
        {
            var wanted = Math.Min(chunk.Length, maxHeaderBytes - (int)buffer.Length);
            var read = await stream.ReadAsync(chunk.AsMemory(0, wanted), cancellationToken);
            if (read == 0)
            {
                break;
            }

            buffer.Write(chunk, 0, read);
            var bytes = buffer.GetBuffer();
            var terminatorIndex = bytes.AsSpan(0, (int)buffer.Length).IndexOf(Terminator);
            if (terminatorIndex >= 0)
            {
                var headerLength = terminatorIndex + Terminator.Length;
                var headerBytes = bytes.AsSpan(0, headerLength).ToArray();
                var trailingBytes = bytes.AsSpan(headerLength, (int)buffer.Length - headerLength).ToArray();
                return new HttpHeaderBlock(
                    headerBytes,
                    trailingBytes,
                    Encoding.Latin1.GetString(headerBytes));
            }
        }

        throw new InvalidDataException("HTTP 请求头过长或连接在请求头完成前关闭。");
    }
}

