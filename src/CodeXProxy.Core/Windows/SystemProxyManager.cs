using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeXProxy.Windows;

public sealed class ProxyBackupState
{
    public int FormatVersion { get; set; } = 1;

    public string Token { get; set; } = Guid.NewGuid().ToString("N");

    public string PacUrl { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    public Dictionary<string, SavedRegistryValue> Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record ProxyRecoveryResult(bool FileFound, bool Restored, string Message);

public sealed class SystemProxyManager
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object _gate = new();
    private readonly string _backupFile;
    private ProxyBackupState? _activeState;

    public SystemProxyManager(string? backupFile = null)
    {
        _backupFile = backupFile ?? AppPaths.ProxyBackupFile;
    }

    public bool IsEnabled
    {
        get
        {
            lock (_gate)
            {
                return _activeState is not null;
            }
        }
    }

    public ProxyRecoveryResult RecoverIfNeeded()
    {
        lock (_gate)
        {
            if (!File.Exists(_backupFile))
            {
                return new ProxyRecoveryResult(false, false, "未发现遗留的系统代理备份。");
            }

            ProxyBackupState? state;
            try
            {
                state = JsonSerializer.Deserialize<ProxyBackupState>(
                    File.ReadAllText(_backupFile),
                    JsonOptions);
            }
            catch (Exception ex)
            {
                var corruptFile = _backupFile + $".corrupt-{DateTime.Now:yyyyMMddHHmmss}";
                try
                {
                    File.Move(_backupFile, corruptFile, overwrite: true);
                }
                catch
                {
                }

                return new ProxyRecoveryResult(
                    true,
                    false,
                    $"发现损坏的代理备份，无法自动恢复：{ex.Message}。文件已保留为 {Path.GetFileName(corruptFile)}。");
            }

            if (state is null || state.FormatVersion != 1 || string.IsNullOrWhiteSpace(state.PacUrl))
            {
                return new ProxyRecoveryResult(true, false, "代理备份格式无效，未修改系统设置。");
            }

            var currentPacUrl = InternetSettings.ReadAutoConfigUrl();
            if (!string.Equals(currentPacUrl, state.PacUrl, StringComparison.OrdinalIgnoreCase))
            {
                TryDeleteBackup();
                return new ProxyRecoveryResult(
                    true,
                    false,
                    "发现遗留备份，但当前 PAC 已由其他程序修改；未覆盖当前设置，已清理过期备份。");
            }

            try
            {
                InternetSettings.ApplySnapshot(state.Values);
                InternetSettings.NotifyChanged();
                TryDeleteBackup();
                return new ProxyRecoveryResult(true, true, "已恢复上次异常退出前的 Windows 代理设置。");
            }
            catch (Exception ex)
            {
                return new ProxyRecoveryResult(true, false, $"自动恢复系统代理失败：{ex.Message}");
            }
        }
    }

    public void Enable(string pacUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pacUrl);

        lock (_gate)
        {
            if (_activeState is not null)
            {
                throw new InvalidOperationException("系统代理已经处于启用状态。");
            }

            var state = new ProxyBackupState
            {
                PacUrl = pacUrl,
                Values = InternetSettings.Capture()
            };

            var directory = Path.GetDirectoryName(_backupFile);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            WriteBackup(state);
            try
            {
                InternetSettings.ApplyPacConfiguration(pacUrl);
                InternetSettings.NotifyChanged();
                _activeState = state;
            }
            catch
            {
                try
                {
                    InternetSettings.ApplySnapshot(state.Values);
                    InternetSettings.NotifyChanged();
                    TryDeleteBackup();
                }
                catch
                {
                }

                throw;
            }
        }
    }

    public void Restore()
    {
        lock (_gate)
        {
            if (_activeState is null)
            {
                return;
            }

            InternetSettings.ApplySnapshot(_activeState.Values);
            InternetSettings.NotifyChanged();
            _activeState = null;
            TryDeleteBackup();
        }
    }

    private void WriteBackup(ProxyBackupState state)
    {
        var temporaryFile = _backupFile + ".tmp";
        File.WriteAllText(temporaryFile, JsonSerializer.Serialize(state, JsonOptions));
        File.Move(temporaryFile, _backupFile, overwrite: true);
    }

    private void TryDeleteBackup()
    {
        try
        {
            File.Delete(_backupFile);
        }
        catch
        {
        }
    }
}

