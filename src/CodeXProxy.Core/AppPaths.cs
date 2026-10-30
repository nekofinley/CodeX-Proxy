namespace CodeXProxy;

public static class AppPaths
{
    public static string ConfigDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CodeXProxy");

    public static string ConfigFile { get; } = Path.Combine(ConfigDirectory, "config.json");

    public static string LocalDataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodeXProxy");

    public static string ProxyBackupFile { get; } = Path.Combine(LocalDataDirectory, "proxy-backup.json");

    public static string LogDirectory { get; } = Path.Combine(LocalDataDirectory, "logs");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(LocalDataDirectory);
        Directory.CreateDirectory(LogDirectory);
    }
}

