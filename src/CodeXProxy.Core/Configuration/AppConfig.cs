namespace CodeXProxy.Configuration;

public sealed class AppConfig
{
    public int SchemaVersion { get; set; } = 1;

    public string UpstreamProxy { get; set; } = "http://127.0.0.1:7890";

    public string UpstreamUsername { get; set; } = string.Empty;

    public List<string> DomainRules { get; set; } = Configuration.DomainRules.DefaultRules.ToList();

    public TargetAppConfig ChatGptApp { get; set; } = new()
    {
        DisplayName = "ChatGPT",
        Path = DiscoverChatGpt(),
        Arguments = string.Empty,
        WorkingDirectory = string.Empty
    };

    public TargetAppConfig CodexApp { get; set; } = new()
    {
        DisplayName = "Codex",
        Path = string.Empty,
        Arguments = string.Empty,
        WorkingDirectory = string.Empty
    };

    public string TestTarget { get; set; } = "chatgpt.com:443";

    public bool MinimizeToTray { get; set; } = true;

    public void Normalize()
    {
        UpstreamProxy = (UpstreamProxy ?? string.Empty).Trim();
        UpstreamUsername = (UpstreamUsername ?? string.Empty).Trim();
        TestTarget = string.IsNullOrWhiteSpace(TestTarget) ? "chatgpt.com:443" : TestTarget.Trim();

        var normalizedRules = new List<string>();
        var rules = DomainRules ?? [];
        foreach (var rawRule in rules)
        {
            var rule = global::CodeXProxy.Configuration.DomainRules.NormalizeRule(rawRule);
            if (rule is not null && !normalizedRules.Contains(rule, StringComparer.OrdinalIgnoreCase))
            {
                normalizedRules.Add(rule);
            }
        }

        DomainRules = normalizedRules;
        ChatGptApp ??= new TargetAppConfig { DisplayName = "ChatGPT" };
        CodexApp ??= new TargetAppConfig { DisplayName = "Codex" };
        ChatGptApp.Normalize("ChatGPT");
        CodexApp.Normalize("Codex");
    }

    public AppConfig Clone()
    {
        return new AppConfig
        {
            SchemaVersion = SchemaVersion,
            UpstreamProxy = UpstreamProxy,
            UpstreamUsername = UpstreamUsername,
            DomainRules = DomainRules.ToList(),
            ChatGptApp = ChatGptApp.Clone(),
            CodexApp = CodexApp.Clone(),
            TestTarget = TestTarget,
            MinimizeToTray = MinimizeToTray
        };
    }

    private static string DiscoverChatGpt()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new[]
        {
            Path.Combine(localAppData, "Programs", "ChatGPT", "ChatGPT.exe"),
            Path.Combine(localAppData, "ChatGPT", "ChatGPT.exe")
        };

        return candidates.FirstOrDefault(File.Exists) ?? string.Empty;
    }
}

public sealed class TargetAppConfig
{
    public string DisplayName { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public string Arguments { get; set; } = string.Empty;

    public string WorkingDirectory { get; set; } = string.Empty;

    public void Normalize(string fallbackName)
    {
        DisplayName = string.IsNullOrWhiteSpace(DisplayName) ? fallbackName : DisplayName.Trim();
        Path = (Path ?? string.Empty).Trim();
        Arguments = (Arguments ?? string.Empty).Trim();
        WorkingDirectory = (WorkingDirectory ?? string.Empty).Trim();
    }

    public TargetAppConfig Clone()
    {
        return new TargetAppConfig
        {
            DisplayName = DisplayName,
            Path = Path,
            Arguments = Arguments,
            WorkingDirectory = WorkingDirectory
        };
    }
}

