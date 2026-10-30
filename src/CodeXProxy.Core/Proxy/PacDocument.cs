using System.Text.Json;

namespace CodeXProxy.Proxy;

public static class PacDocument
{
    public static string Build(int proxyPort, IEnumerable<string> rules)
    {
        var normalizedRules = rules
            .Select(Configuration.DomainRules.NormalizeRule)
            .Where(rule => rule is not null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var rulesJson = JsonSerializer.Serialize(normalizedRules);

        return $$"""
            function FindProxyForURL(url, host) {
              host = host.toLowerCase();
              if (host.length > 0 && host.charAt(host.length - 1) === ".") {
                host = host.substring(0, host.length - 1);
              }

              if (isPlainHostName(host) || host === "localhost" || host === "127.0.0.1" ||
                  host === "::1" || host === "[::1]" || host === "0.0.0.0" ||
                  shExpMatch(host, "127.*")) {
                return "DIRECT";
              }

              var rules = {{rulesJson}};
              for (var i = 0; i < rules.length; i++) {
                var rule = rules[i];
                if (rule.indexOf("*") < 0) {
                  if (host === rule || dnsDomainIs(host, "." + rule)) {
                    return "PROXY 127.0.0.1:{{proxyPort}}";
                  }
                } else if (shExpMatch(host, rule)) {
                  return "PROXY 127.0.0.1:{{proxyPort}}";
                }
              }

              return "DIRECT";
            }
            """;
    }
}

