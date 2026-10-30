using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace CodeXProxy.Windows;

public sealed class SavedRegistryValue
{
    public RegistryValueKind Kind { get; set; }

    public string? Text { get; set; }

    public int? Dword { get; set; }

    public long? Qword { get; set; }

    public byte[]? Binary { get; set; }

    public string[]? MultiString { get; set; }

    public static SavedRegistryValue From(object value, RegistryValueKind kind)
    {
        return kind switch
        {
            RegistryValueKind.String or RegistryValueKind.ExpandString => new SavedRegistryValue
            {
                Kind = kind,
                Text = Convert.ToString(value)
            },
            RegistryValueKind.DWord => new SavedRegistryValue
            {
                Kind = kind,
                Dword = Convert.ToInt32(value)
            },
            RegistryValueKind.QWord => new SavedRegistryValue
            {
                Kind = kind,
                Qword = Convert.ToInt64(value)
            },
            RegistryValueKind.Binary => new SavedRegistryValue
            {
                Kind = kind,
                Binary = (byte[])value
            },
            RegistryValueKind.MultiString => new SavedRegistryValue
            {
                Kind = kind,
                MultiString = (string[])value
            },
            _ => new SavedRegistryValue
            {
                Kind = RegistryValueKind.String,
                Text = Convert.ToString(value)
            }
        };
    }

    [JsonIgnore]
    public object Value => Kind switch
    {
        RegistryValueKind.String or RegistryValueKind.ExpandString => Text ?? string.Empty,
        RegistryValueKind.DWord => Dword ?? 0,
        RegistryValueKind.QWord => Qword ?? 0L,
        RegistryValueKind.Binary => Binary ?? [],
        RegistryValueKind.MultiString => MultiString ?? [],
        _ => Text ?? string.Empty
    };
}

public static class InternetSettings
{
    public const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    public static readonly IReadOnlyList<string> TrackedValueNames =
    [
        "AutoConfigURL",
        "ProxyEnable",
        "ProxyServer",
        "ProxyOverride",
        "AutoDetect",
        "ProxyHttp1.1"
    ];

    public static Dictionary<string, SavedRegistryValue> Capture()
    {
        var values = new Dictionary<string, SavedRegistryValue>(StringComparer.OrdinalIgnoreCase);
        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: false);
        if (key is null)
        {
            return values;
        }

        foreach (var name in TrackedValueNames)
        {
            var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (value is null)
            {
                continue;
            }

            var kind = key.GetValueKind(name);
            values[name] = SavedRegistryValue.From(value, kind);
        }

        return values;
    }

    public static void ApplySnapshot(IReadOnlyDictionary<string, SavedRegistryValue> snapshot)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RegistryPath, writable: true)
            ?? throw new InvalidOperationException("无法打开当前用户的 Internet Settings 注册表项。");

        foreach (var name in TrackedValueNames)
        {
            if (snapshot.TryGetValue(name, out var saved))
            {
                key.SetValue(name, saved.Value, saved.Kind);
            }
            else
            {
                key.DeleteValue(name, throwOnMissingValue: false);
            }
        }
    }

    public static string? ReadAutoConfigUrl()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: false);
        return key?.GetValue("AutoConfigURL", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }

    public static void ApplyPacConfiguration(string pacUrl)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RegistryPath, writable: true)
            ?? throw new InvalidOperationException("无法打开当前用户的 Internet Settings 注册表项。");
        key.SetValue("AutoConfigURL", pacUrl, RegistryValueKind.String);
        key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
        key.SetValue("AutoDetect", 0, RegistryValueKind.DWord);
    }

    public static void NotifyChanged()
    {
        NativeMethods.InternetSetOption(IntPtr.Zero, NativeMethods.InternetOptionSettingsChanged, IntPtr.Zero, 0);
        NativeMethods.InternetSetOption(IntPtr.Zero, NativeMethods.InternetOptionRefresh, IntPtr.Zero, 0);
        NativeMethods.SendMessageTimeout(
            NativeMethods.HwndBroadcast,
            NativeMethods.WmSettingChange,
            IntPtr.Zero,
            "Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings",
            NativeMethods.SmtoAbortIfHung,
            1500,
            out _);
    }

    private static class NativeMethods
    {
        public const int InternetOptionRefresh = 37;
        public const int InternetOptionSettingsChanged = 39;
        public static readonly IntPtr HwndBroadcast = new(0xffff);
        public const uint WmSettingChange = 0x001A;
        public const uint SmtoAbortIfHung = 0x0002;

        [DllImport("wininet.dll", SetLastError = true)]
        public static extern bool InternetSetOption(
            IntPtr internetHandle,
            int option,
            IntPtr buffer,
            int bufferLength);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr SendMessageTimeout(
            IntPtr hWnd,
            uint message,
            IntPtr wParam,
            string lParam,
            uint flags,
            uint timeout,
            out IntPtr result);
    }
}

