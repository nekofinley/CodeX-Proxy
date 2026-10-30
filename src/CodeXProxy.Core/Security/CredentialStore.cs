using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace CodeXProxy.Security;

public sealed record ProxyCredential(string Username, string Password);

public sealed class CredentialStore
{
    public const string DefaultTargetName = "CodeXProxy/UpstreamProxy";
    private const uint CredentialTypeGeneric = 1;
    private const uint CredentialPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    private readonly string _targetName;

    public CredentialStore(string? targetName = null)
    {
        _targetName = targetName ?? DefaultTargetName;
    }

    public ProxyCredential? Load()
    {
        if (!NativeMethods.CredRead(_targetName, CredentialTypeGeneric, 0, out var handle))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound)
            {
                return null;
            }

            throw new Win32Exception(error, "读取 Windows 凭据失败。");
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeMethods.Credential>(handle);
            var password = string.Empty;
            if (credential.CredentialBlob != IntPtr.Zero && credential.CredentialBlobSize > 0)
            {
                var bytes = new byte[credential.CredentialBlobSize];
                Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
                password = Encoding.UTF8.GetString(bytes);
            }

            return new ProxyCredential(credential.UserName ?? string.Empty, password);
        }
        finally
        {
            NativeMethods.CredFree(handle);
        }
    }

    public void Save(string username, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);

        var passwordBytes = Encoding.UTF8.GetBytes(password ?? string.Empty);
        var blob = Marshal.AllocHGlobal(Math.Max(passwordBytes.Length, 1));
        try
        {
            if (passwordBytes.Length > 0)
            {
                Marshal.Copy(passwordBytes, 0, blob, passwordBytes.Length);
            }

            var credential = new NativeMethods.Credential
            {
                Type = CredentialTypeGeneric,
                TargetName = _targetName,
                Comment = "CodeX Proxy upstream credential",
                CredentialBlobSize = (uint)passwordBytes.Length,
                CredentialBlob = blob,
                Persist = CredentialPersistLocalMachine,
                UserName = username
            };

            if (!NativeMethods.CredWrite(ref credential, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "保存 Windows 凭据失败。");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(blob);
        }
    }

    public void Delete()
    {
        if (!NativeMethods.CredDelete(_targetName, CredentialTypeGeneric, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorNotFound)
            {
                throw new Win32Exception(error, "删除 Windows 凭据失败。");
            }
        }
    }

    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct Credential
        {
            public uint Flags;
            public uint Type;

            [MarshalAs(UnmanagedType.LPWStr)]
            public string? TargetName;

            [MarshalAs(UnmanagedType.LPWStr)]
            public string? Comment;

            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;

            [MarshalAs(UnmanagedType.LPWStr)]
            public string? TargetAlias;

            [MarshalAs(UnmanagedType.LPWStr)]
            public string? UserName;
        }

        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool CredRead(
            string target,
            uint type,
            uint reservedFlag,
            out IntPtr credentialPtr);

        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool CredWrite(ref Credential credential, uint flags);

        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool CredDelete(string target, uint type, uint flags);

        [DllImport("advapi32.dll")]
        public static extern void CredFree(IntPtr buffer);
    }
}

