using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using Taildesk.Shared;

namespace Taildesk.UpdateGuardian;

/// <summary>
/// Owns the full dedicated-user token and loaded profile for one isolated sshd
/// process. The password used to obtain the token exists only in zeroable
/// unmanaged memory and is invalidated before this object is returned.
/// </summary>
internal sealed class SshDaemonUserContext : IDisposable
{
    private const uint ProfileInfoNoUi = 0x00000001;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const int ProfileUnloadAttempts = 6;
    private const int ProfileUnloadRetryMilliseconds = 250;
    private readonly SafeAccessTokenHandle _token;
    private IntPtr _profileHandle;
    private bool _disposed;

    private SshDaemonUserContext(SafeAccessTokenHandle token, IntPtr profileHandle)
    {
        _token = token;
        _profileHandle = profileHandle;
    }

    public static SshDaemonUserContext Create()
    {
        SafeAccessTokenHandle? token = null;
        using (var password = UnmanagedPassword.Create())
        {
            SetAccountPassword(password.Pointer);
            try
            {
                token = SshAdminToken.LogonFullAdministrator(password.Pointer);
            }
            finally
            {
                // A captured password cannot be reused after the token exists.
                using var replacement = UnmanagedPassword.Create();
                try { SetAccountPassword(replacement.Pointer); }
                catch
                {
                    token?.Dispose();
                    token = null;
                    throw;
                }
            }
        }

        if (token is null) throw new InvalidOperationException("Windows did not return the SSH administrator token.");
        var profile = new ProfileInfo
        {
            Size = (uint)Marshal.SizeOf<ProfileInfo>(),
            Flags = ProfileInfoNoUi,
            UserName = RemoteAdministrationProtocol.SshAccountName
        };
        try
        {
            using var backup = ScopedProcessPrivilege.Enable("SeBackupPrivilege");
            using var restore = ScopedProcessPrivilege.Enable("SeRestorePrivilege");
            if (!LoadUserProfileW(token, ref profile))
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Windows could not load the dedicated SSH administrator profile.");
        }
        catch
        {
            token.Dispose();
            throw;
        }

        return new SshDaemonUserContext(token, profile.ProfileHandle);
    }

    public static void RotateUnknownPassword()
    {
        using var replacement = UnmanagedPassword.Create();
        SetAccountPassword(replacement.Pointer);
    }

    public CreatedProcess CreateProcessSuspended(
        string applicationName,
        string commandLine,
        string currentDirectory,
        uint creationFlags)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var environment = SanitizedEnvironmentBlock.Create(_token);
        var startup = new StartupInfo { Size = (uint)Marshal.SizeOf<StartupInfo>() };
        var mutableCommandLine = new System.Text.StringBuilder(commandLine);
        if (!CreateProcessAsUserW(
                _token,
                applicationName,
                mutableCommandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                inheritHandles: false,
                creationFlags | CreateUnicodeEnvironment,
                environment.Pointer,
                currentDirectory,
                ref startup,
                out var process))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not create the elevated isolated SSH daemon.");
        }
        return new CreatedProcess(process.ProcessHandle, process.ThreadHandle, process.ProcessId);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_profileHandle != IntPtr.Zero)
            {
                using var backup = ScopedProcessPrivilege.Enable("SeBackupPrivilege");
                using var restore = ScopedProcessPrivilege.Enable("SeRestorePrivilege");
                var lastError = 0;
                for (var attempt = 1; attempt <= ProfileUnloadAttempts; attempt++)
                {
                    if (UnloadUserProfile(_token, _profileHandle))
                    {
                        _profileHandle = IntPtr.Zero;
                        break;
                    }

                    lastError = Marshal.GetLastWin32Error();
                    if (attempt < ProfileUnloadAttempts)
                        Thread.Sleep(ProfileUnloadRetryMilliseconds * attempt);
                }

                if (_profileHandle != IntPtr.Zero)
                    throw new Win32Exception(
                        lastError,
                        $"Windows could not unload the dedicated SSH administrator profile after {ProfileUnloadAttempts} attempts.");
            }
        }
        finally
        {
            _token.Dispose();
        }
    }

    private static void SetAccountPassword(IntPtr password)
    {
        var value = new UserInfo1003 { Password = password };
        var result = NetUserSetInfo(
            null,
            RemoteAdministrationProtocol.SshAccountName,
            1003,
            ref value,
            out _);
        if (result != 0)
            throw new InvalidOperationException($"Windows could not rotate the dedicated SSH account password (NetAPI error {result}).");
    }

    internal readonly record struct CreatedProcess(IntPtr ProcessHandle, IntPtr ThreadHandle, uint ProcessId);

    private sealed class UnmanagedPassword : IDisposable
    {
        private const int CharacterCount = 64;
        private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789!@#$%+-_";
        private IntPtr _pointer;
        public IntPtr Pointer => _pointer;

        private UnmanagedPassword(IntPtr pointer) => _pointer = pointer;

        public static UnmanagedPassword Create()
        {
            var characters = new char[CharacterCount];
            var random = RandomNumberGenerator.GetBytes(CharacterCount);
            try
            {
                characters[0] = 'A';
                characters[1] = 'a';
                characters[2] = '7';
                characters[3] = '!';
                for (var index = 4; index < characters.Length; index++)
                    characters[index] = Alphabet[random[index] % Alphabet.Length];
                for (var index = characters.Length - 1; index > 0; index--)
                {
                    var target = random[index] % (index + 1);
                    (characters[index], characters[target]) = (characters[target], characters[index]);
                }

                var pointer = Marshal.AllocHGlobal((characters.Length + 1) * sizeof(char));
                Marshal.Copy(characters, 0, pointer, characters.Length);
                Marshal.WriteInt16(pointer, characters.Length * sizeof(char), 0);
                return new UnmanagedPassword(pointer);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(random);
                Array.Clear(characters);
            }
        }

        public void Dispose()
        {
            if (_pointer == IntPtr.Zero) return;
            for (var offset = 0; offset <= CharacterCount; offset++)
                Marshal.WriteInt16(_pointer, offset * sizeof(char), 0);
            Marshal.FreeHGlobal(_pointer);
            _pointer = IntPtr.Zero;
        }
    }

    private sealed class SanitizedEnvironmentBlock : IDisposable
    {
        private IntPtr _pointer;
        public IntPtr Pointer => _pointer;
        private SanitizedEnvironmentBlock(IntPtr pointer) => _pointer = pointer;

        public static SanitizedEnvironmentBlock Create(SafeAccessTokenHandle token)
        {
            if (!CreateEnvironmentBlock(out var source, token, inherit: false))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not create the SSH administrator environment.");
            try
            {
                var variables = ReadEnvironment(source);
                foreach (var name in variables.Keys.Where(IsDangerousVariable).ToArray()) variables.Remove(name);

                var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                var system32 = Path.Combine(windows, "System32");
                variables["SystemRoot"] = windows;
                variables["windir"] = windows;
                variables["ComSpec"] = Path.Combine(system32, "cmd.exe");
                variables["PATH"] = string.Join(';',
                [
                    system32,
                    windows,
                    Path.Combine(system32, "WindowsPowerShell", "v1.0"),
                    Path.Combine(system32, "OpenSSH")
                ]);
                variables["PATHEXT"] = ".COM;.EXE;.BAT;.CMD";
                variables["ProgramData"] = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                variables["ProgramFiles"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                variables["ProgramW6432"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

                var entries = variables
                    .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(pair => $"{pair.Key}={pair.Value}");
                var joined = string.Join('\0', entries) + "\0\0";
                var characters = joined.ToCharArray();
                try
                {
                    var destination = Marshal.AllocHGlobal(characters.Length * sizeof(char));
                    Marshal.Copy(characters, 0, destination, characters.Length);
                    return new SanitizedEnvironmentBlock(destination);
                }
                finally
                {
                    Array.Clear(characters);
                }
            }
            finally
            {
                _ = DestroyEnvironmentBlock(source);
            }
        }

        public void Dispose()
        {
            if (_pointer == IntPtr.Zero) return;
            Marshal.FreeHGlobal(_pointer);
            _pointer = IntPtr.Zero;
        }

        private static Dictionary<string, string> ReadEnvironment(IntPtr block)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var offset = 0;
            while (true)
            {
                var value = Marshal.PtrToStringUni(IntPtr.Add(block, offset * sizeof(char)));
                if (string.IsNullOrEmpty(value)) break;
                offset += value.Length + 1;
                var separator = value.IndexOf('=');
                if (separator <= 0) continue;
                result[value[..separator]] = value[(separator + 1)..];
                if (offset > 128 * 1024)
                    throw new InvalidDataException("The SSH administrator environment exceeds its bounded size.");
            }
            return result;
        }

        private static bool IsDangerousVariable(string name) =>
            name.Equals("__COMPAT_LAYER", StringComparison.OrdinalIgnoreCase)
            || name.Equals("DOTNET_STARTUP_HOOKS", StringComparison.OrdinalIgnoreCase)
            || name.Equals("OPENSSL_CONF", StringComparison.OrdinalIgnoreCase)
            || name.Equals("OPENSSL_MODULES", StringComparison.OrdinalIgnoreCase)
            || name.Equals("SSH_AUTH_SOCK", StringComparison.OrdinalIgnoreCase)
            || name.Equals("GIT_SSH", StringComparison.OrdinalIgnoreCase)
            || name.Equals("GIT_SSH_COMMAND", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("COR_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("CORECLR_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("COMPLUS_", StringComparison.OrdinalIgnoreCase);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProfileInfo
    {
        public uint Size;
        public uint Flags;
        [MarshalAs(UnmanagedType.LPWStr)] public string UserName;
        public string? ProfilePath;
        public string? DefaultPath;
        public string? ServerName;
        public string? PolicyPath;
        public IntPtr ProfileHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public uint Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public uint X;
        public uint Y;
        public uint XSize;
        public uint YSize;
        public uint XCountChars;
        public uint YCountChars;
        public uint FillAttribute;
        public ushort ShowWindow;
        public ushort Reserved2;
        public IntPtr Reserved2Pointer;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr ProcessHandle;
        public IntPtr ThreadHandle;
        public uint ProcessId;
        public uint ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UserInfo1003
    {
        public IntPtr Password;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessAsUserW(
        SafeAccessTokenHandle token,
        string applicationName,
        System.Text.StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("userenv.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LoadUserProfileW(
        SafeAccessTokenHandle token,
        ref ProfileInfo profileInfo);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnloadUserProfile(SafeAccessTokenHandle token, IntPtr profileHandle);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateEnvironmentBlock(
        out IntPtr environment,
        SafeAccessTokenHandle token,
        [MarshalAs(UnmanagedType.Bool)] bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("Netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUserSetInfo(
        string? serverName,
        string userName,
        int level,
        ref UserInfo1003 buffer,
        out uint parameterError);
}
