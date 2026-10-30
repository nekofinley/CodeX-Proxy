using System.Text;
using System.Text.RegularExpressions;

namespace CodeXProxy.Logging;

public enum ProxyLogLevel
{
    Info,
    Success,
    Warning,
    Error
}

public sealed record ProxyLogEntry(
    DateTimeOffset Timestamp,
    ProxyLogLevel Level,
    string Message,
    string? Host = null,
    string? Rule = null,
    string? Route = null,
    int? LatencyMs = null);

public sealed partial class ProxyLogStore
{
    private const int MaxEntries = 1500;
    private readonly object _gate = new();
    private readonly string _logFile;
    private readonly List<ProxyLogEntry> _entries = [];

    public ProxyLogStore(string? logDirectory = null)
    {
        var directory = logDirectory ?? AppPaths.LogDirectory;
        Directory.CreateDirectory(directory);
        _logFile = Path.Combine(directory, $"codexproxy-{DateTime.Now:yyyyMMdd}.log");
    }

    public event Action<ProxyLogEntry>? EntryAdded;

    public IReadOnlyList<ProxyLogEntry> Snapshot()
    {
        lock (_gate)
        {
            return _entries.ToArray();
        }
    }

    public void Info(string message, string? host = null, string? rule = null, string? route = null, int? latencyMs = null)
        => Add(new ProxyLogEntry(DateTimeOffset.Now, ProxyLogLevel.Info, message, host, rule, route, latencyMs));

    public void Success(string message, string? host = null, string? rule = null, string? route = null, int? latencyMs = null)
        => Add(new ProxyLogEntry(DateTimeOffset.Now, ProxyLogLevel.Success, message, host, rule, route, latencyMs));

    public void Warning(string message, string? host = null, string? rule = null, string? route = null)
        => Add(new ProxyLogEntry(DateTimeOffset.Now, ProxyLogLevel.Warning, message, host, rule, route));

    public void Error(string message, string? host = null, string? rule = null, string? route = null)
        => Add(new ProxyLogEntry(DateTimeOffset.Now, ProxyLogLevel.Error, message, host, rule, route));

    public void Add(ProxyLogEntry entry)
    {
        var safeEntry = entry with { Message = Redact(entry.Message) };
        var route = string.IsNullOrWhiteSpace(safeEntry.Route) ? string.Empty : $" route={safeEntry.Route}";
        var host = string.IsNullOrWhiteSpace(safeEntry.Host) ? string.Empty : $" host={safeEntry.Host}";
        var latency = safeEntry.LatencyMs is null ? string.Empty : $" latency={safeEntry.LatencyMs}ms";
        var line = $"{safeEntry.Timestamp:yyyy-MM-dd HH:mm:ss.fff} {safeEntry.Level.ToString().ToUpperInvariant()}{host}{route}{latency} {safeEntry.Message}{Environment.NewLine}";
        lock (_gate)
        {
            _entries.Add(safeEntry);
            if (_entries.Count > MaxEntries)
            {
                _entries.RemoveRange(0, _entries.Count - MaxEntries);
            }

            try
            {
                File.AppendAllText(_logFile, line, Encoding.UTF8);
            }
            catch
            {
                // Logging must never take down the proxy.
            }
        }

        try
        {
            EntryAdded?.Invoke(safeEntry);
        }
        catch
        {
            // UI subscribers are isolated from the proxy path.
        }
    }

    public static string Redact(string value)
    {
        var redacted = UserInfoPattern().Replace(value, "$1***@");
        redacted = AuthorizationPattern().Replace(redacted, "$1***");
        redacted = CookiePattern().Replace(redacted, "$1***");
        redacted = SecretPattern().Replace(redacted, "$1$2***");
        return redacted;
    }

    [GeneratedRegex(@"\b([a-z][a-z0-9+.-]*://)[^/\s@]+@", RegexOptions.IgnoreCase)]
    private static partial Regex UserInfoPattern();

    [GeneratedRegex(@"(?i)\b((?:proxy-)?authorization\s*[:=]\s*)(?:(?:bearer|basic|negotiate)\s+)?[^\s,;]+")]
    private static partial Regex AuthorizationPattern();

    [GeneratedRegex(@"(?i)\b((?:set-)?cookie\s*[:=]\s*)[^\r\n]+")]
    private static partial Regex CookiePattern();

    [GeneratedRegex(@"(?i)\b(authorization|cookie|set-cookie|token|access_token|refresh_token|password|secret|api[_-]?key)(\s*[:=]\s*)[^\s,;]+")]
    private static partial Regex SecretPattern();
}
