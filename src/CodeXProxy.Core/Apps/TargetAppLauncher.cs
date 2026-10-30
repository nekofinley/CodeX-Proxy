using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using CodeXProxy.Configuration;
using CodeXProxy.Logging;

namespace CodeXProxy.Apps;

public sealed record AppLaunchResult(int ProcessId);

public static class TargetAppLauncher
{
    public static AppLaunchResult Launch(TargetAppConfig target, int localProxyPort, ProxyLogStore log)
    {
        if (localProxyPort is < 1 or > 65535)
        {
            throw new InvalidOperationException("本地代理尚未启动。");
        }

        if (string.IsNullOrWhiteSpace(target.Path))
        {
            throw new InvalidOperationException($"尚未配置 {target.DisplayName} 的可执行文件或快捷方式。");
        }

        var resolved = ResolveLaunch(target);
        var proxyUrl = $"http://127.0.0.1:{localProxyPort}";
        var noProxy = "localhost,127.0.0.1,::1,[::1]";

        var startInfo = new ProcessStartInfo
        {
            FileName = resolved.Executable,
            Arguments = resolved.Arguments,
            UseShellExecute = false,
            CreateNoWindow = false,
            WorkingDirectory = ResolveWorkingDirectory(resolved, target)
        };

        SetProxyEnvironment(startInfo, proxyUrl, noProxy);

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"无法启动 {target.DisplayName}。");
        log.Success($"{target.DisplayName} 已启动（PID {process.Id}）", route: "APP");
        return new AppLaunchResult(process.Id);
    }

    public static IReadOnlyDictionary<string, string> BuildProxyEnvironment(
        int localProxyPort,
        IReadOnlyDictionary<string, string>? existing = null)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (existing is not null)
        {
            foreach (var pair in existing)
            {
                values[pair.Key] = pair.Value;
            }
        }

        var proxyUrl = $"http://127.0.0.1:{localProxyPort}";
        values["HTTP_PROXY"] = proxyUrl;
        values["HTTPS_PROXY"] = proxyUrl;
        values["ALL_PROXY"] = proxyUrl;
        values["NO_PROXY"] = "localhost,127.0.0.1,::1,[::1]";
        values["http_proxy"] = proxyUrl;
        values["https_proxy"] = proxyUrl;
        values["all_proxy"] = proxyUrl;
        values["no_proxy"] = "localhost,127.0.0.1,::1,[::1]";
        return values;
    }

    public static TargetAppConfig DiscoverCodex()
    {
        var command = ResolveFromPath("codex.exe") ?? ResolveFromPath("codex.cmd") ?? ResolveFromPath("codex");
        return new TargetAppConfig
        {
            DisplayName = "Codex",
            Path = command ?? string.Empty
        };
    }

    private static void SetProxyEnvironment(ProcessStartInfo startInfo, string proxyUrl, string noProxy)
    {
        startInfo.Environment["HTTP_PROXY"] = proxyUrl;
        startInfo.Environment["HTTPS_PROXY"] = proxyUrl;
        startInfo.Environment["ALL_PROXY"] = proxyUrl;
        startInfo.Environment["NO_PROXY"] = noProxy;
        startInfo.Environment["http_proxy"] = proxyUrl;
        startInfo.Environment["https_proxy"] = proxyUrl;
        startInfo.Environment["all_proxy"] = proxyUrl;
        startInfo.Environment["no_proxy"] = noProxy;
    }

    private static ResolvedLaunch ResolveLaunch(TargetAppConfig target)
    {
        var expandedPath = Environment.ExpandEnvironmentVariables(target.Path.Trim().Trim('"'));
        var arguments = Environment.ExpandEnvironmentVariables(target.Arguments ?? string.Empty).Trim();

        if (expandedPath.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                "不支持 shell:AppsFolder 快捷方式。请填写可直接启动的 .exe 或 .lnk 路径；Store/MSIX 应用受限。");
        }

        if (expandedPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            if (!File.Exists(expandedPath))
            {
                throw new FileNotFoundException("找不到快捷方式。", expandedPath);
            }

            var shortcut = ResolveShortcut(expandedPath);
            if (string.IsNullOrWhiteSpace(shortcut.TargetPath))
            {
                throw new InvalidOperationException("快捷方式没有可解析的目标程序。");
            }

            var combinedArguments = JoinArguments(shortcut.Arguments, arguments);
            return new ResolvedLaunch(shortcut.TargetPath, combinedArguments, shortcut.WorkingDirectory);
        }

        var executable = ResolveExecutable(expandedPath);
        var extension = Path.GetExtension(executable).ToLowerInvariant();
        if (extension is ".cmd" or ".bat")
        {
            var commandShell = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
            var commandLine = $"/k \"\"{executable}\"{(arguments.Length > 0 ? " " + arguments : string.Empty)}\"";
            return new ResolvedLaunch(commandShell, commandLine, Path.GetDirectoryName(executable) ?? string.Empty);
        }

        if (extension == ".ps1")
        {
            var shell = ResolveFromPath("pwsh.exe") ?? ResolveFromPath("powershell.exe")
                ?? throw new InvalidOperationException("找不到 PowerShell。");
            return new ResolvedLaunch(
                shell,
                $"-NoExit -ExecutionPolicy Bypass -File \"{executable}\"{(arguments.Length > 0 ? " " + arguments : string.Empty)}",
                Path.GetDirectoryName(executable) ?? string.Empty);
        }

        return new ResolvedLaunch(executable, arguments, Path.GetDirectoryName(executable) ?? string.Empty);
    }

    private static string ResolveExecutable(string value)
    {
        if (File.Exists(value))
        {
            return Path.GetFullPath(value);
        }

        if (value.Contains(Path.DirectorySeparatorChar)
            || value.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new FileNotFoundException("找不到目标程序。", value);
        }

        return ResolveFromPath(value)
            ?? throw new FileNotFoundException("在 PATH 中找不到目标程序。", value);
    }

    private static string? ResolveFromPath(string command)
    {
        if (Path.IsPathRooted(command) && File.Exists(command))
        {
            return Path.GetFullPath(command);
        }

        var hasExtension = Path.HasExtension(command);
        var extensions = hasExtension
            ? new[] { string.Empty }
            : new[] { ".exe", ".cmd", ".bat", string.Empty };
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var extension in extensions)
            {
                try
                {
                    var candidate = Path.Combine(directory.Trim().Trim('"'), command + extension);
                    if (File.Exists(candidate))
                    {
                        return Path.GetFullPath(candidate);
                    }
                }
                catch (ArgumentException)
                {
                }
            }
        }

        return null;
    }

    private static string ResolveWorkingDirectory(ResolvedLaunch resolved, TargetAppConfig target)
    {
        if (!string.IsNullOrWhiteSpace(target.WorkingDirectory))
        {
            var directory = Environment.ExpandEnvironmentVariables(target.WorkingDirectory.Trim('"'));
            if (Directory.Exists(directory))
            {
                return Path.GetFullPath(directory);
            }
        }

        if (!string.IsNullOrWhiteSpace(resolved.WorkingDirectory)
            && Directory.Exists(resolved.WorkingDirectory))
        {
            return resolved.WorkingDirectory;
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private static ShortcutInfo ResolveShortcut(string path)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("无法加载 Windows 快捷方式解析器。");
        object? shell = null;
        object? shortcut = null;
        try
        {
            shell = Activator.CreateInstance(shellType)
                ?? throw new InvalidOperationException("无法创建 Windows 快捷方式解析器。");
            dynamic dynamicShell = shell;
            shortcut = dynamicShell.CreateShortcut(path);
            dynamic dynamicShortcut = shortcut;
            return new ShortcutInfo(
                (string?)dynamicShortcut.TargetPath ?? string.Empty,
                (string?)dynamicShortcut.Arguments ?? string.Empty,
                (string?)dynamicShortcut.WorkingDirectory ?? string.Empty);
        }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut))
            {
                Marshal.FinalReleaseComObject(shortcut);
            }

            if (shell is not null && Marshal.IsComObject(shell))
            {
                Marshal.FinalReleaseComObject(shell);
            }
        }
    }

    private static string JoinArguments(string first, string second)
    {
        if (string.IsNullOrWhiteSpace(first))
        {
            return second;
        }

        if (string.IsNullOrWhiteSpace(second))
        {
            return first;
        }

        return first.TrimEnd() + " " + second.TrimStart();
    }

    private sealed record ResolvedLaunch(string Executable, string Arguments, string WorkingDirectory);

    private sealed record ShortcutInfo(string TargetPath, string Arguments, string WorkingDirectory);
}
