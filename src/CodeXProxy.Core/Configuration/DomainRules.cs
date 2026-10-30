using System.Text.RegularExpressions;

namespace CodeXProxy.Configuration;

public sealed class DomainRules
{
    public static readonly IReadOnlyList<string> DefaultRules =
    [
        "openai.com",
        "chatgpt.com",
        "oaistatic.com",
        "oaiusercontent.com"
    ];

    private readonly IReadOnlyList<RuleMatcher> _matchers;

    public DomainRules(IEnumerable<string> rules)
    {
        var normalized = new List<string>();
        foreach (var rawRule in rules)
        {
            var rule = NormalizeRule(rawRule);
            if (rule is not null && !normalized.Contains(rule, StringComparer.OrdinalIgnoreCase))
            {
                normalized.Add(rule);
            }
        }

        NormalizedRules = normalized;
        _matchers = normalized.Select(CreateMatcher).ToList();
    }

    public IReadOnlyList<string> NormalizedRules { get; }

    public bool IsMatch(string? host)
    {
        return FindMatch(host) is not null;
    }

    public string? FindMatch(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return null;
        }

        var normalizedHost = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (IsLocalHost(normalizedHost))
        {
            return null;
        }

        return _matchers.FirstOrDefault(matcher => matcher.IsMatch(normalizedHost))?.Rule;
    }

    public static string? NormalizeRule(string? rawRule)
    {
        if (string.IsNullOrWhiteSpace(rawRule))
        {
            return null;
        }

        var rule = rawRule.Trim().ToLowerInvariant();
        if (rule.StartsWith('#'))
        {
            return null;
        }

        if (rule.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(rule, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
            {
                return null;
            }

            rule = uri.Host;
        }

        rule = rule.Trim().TrimEnd('.');
        if (rule.StartsWith('.'))
        {
            rule = rule[1..];
        }
        else if (rule.StartsWith("*.", StringComparison.Ordinal))
        {
            rule = rule[2..];
        }

        if (rule.Contains('/') || rule.Contains(':') || rule.Any(char.IsWhiteSpace))
        {
            return null;
        }

        if (rule.Length == 0 || rule is "*" or ".")
        {
            return null;
        }

        return rule;
    }

    public static bool IsLocalHost(string host)
    {
        var normalized = host.Trim().TrimEnd('.').ToLowerInvariant();
        return normalized is "localhost" or "127.0.0.1" or "::1" or "[::1]" or "0.0.0.0"
            || normalized.StartsWith("127.", StringComparison.Ordinal);
    }

    private static RuleMatcher CreateMatcher(string rule)
    {
        if (!rule.Contains('*'))
        {
            var escaped = Regex.Escape(rule);
            return new RuleMatcher(
                rule,
                new Regex($"^(?:.*\\.)?{escaped}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
        }

        var pattern = "^" + Regex.Escape(rule).Replace("\\*", ".*", StringComparison.Ordinal) + "$";
        return new RuleMatcher(
            rule,
            new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
    }

    private sealed record RuleMatcher(string Rule, Regex Pattern)
    {
        public bool IsMatch(string host) => Pattern.IsMatch(host);
    }
}

