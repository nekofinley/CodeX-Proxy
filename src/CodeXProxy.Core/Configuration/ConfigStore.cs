using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeXProxy.Configuration;

public sealed class ConfigLoadResult
{
    public required AppConfig Config { get; init; }

    public string? Warning { get; init; }

    public bool LoadedFromDisk { get; init; }
}

public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _configFile;

    public ConfigStore(string? configFile = null)
    {
        _configFile = configFile ?? AppPaths.ConfigFile;
    }

    public ConfigLoadResult Load()
    {
        var defaults = new AppConfig();
        if (!File.Exists(_configFile))
        {
            defaults.Normalize();
            return new ConfigLoadResult { Config = defaults };
        }

        try
        {
            var json = File.ReadAllText(_configFile);
            var config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
            if (config is null)
            {
                throw new InvalidDataException("配置文件为空。");
            }

            config.Normalize();
            return new ConfigLoadResult { Config = config, LoadedFromDisk = true };
        }
        catch (Exception ex)
        {
            defaults.Normalize();
            return new ConfigLoadResult
            {
                Config = defaults,
                Warning = $"配置读取失败，已使用默认值：{ex.Message}",
                LoadedFromDisk = true
            };
        }
    }

    public void Save(AppConfig config)
    {
        config.Normalize();
        var directory = Path.GetDirectoryName(_configFile);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(config, JsonOptions);
        var temporaryFile = _configFile + ".tmp";
        File.WriteAllText(temporaryFile, json);
        File.Move(temporaryFile, _configFile, overwrite: true);
    }
}

