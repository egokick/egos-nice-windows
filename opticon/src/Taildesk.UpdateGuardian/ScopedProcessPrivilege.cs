using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Taildesk.UpdateGuardian;

/// <summary>Temporarily enables one privilege on the Guardian process token and restores its exact prior state.</summary>
internal sealed class ScopedProcessPrivilege : IDisposable
{
    private const uint TokenQuery = 0x0008;
    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint SePrivilegeEnabled = 0x00000002;
    private const int ErrorNotAllAssigned = 1300;
    private readonly SafeAccessTokenHandle _token;
    private TokenPrivileges _previous;
    private bool _disposed;

    private ScopedProcessPrivilege(SafeAccessTokenHandle token, TokenPrivileges previous)
    {
        _token = token;
        _previous = previous;
    }

    public static ScopedProcessPrivilege Enable(string privilegeName)
    {
        if (!OpenProcessToken(
                GetCurrentProcess(),
                TokenQuery | TokenAdjustPrivileges,
                out var token))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not open the Guardian process token.");
        try
        {
            if (!LookupPrivilegeValueW(null, privilegeName, out var luid))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Windows could not resolve {privilegeName}.");
            var requested = new TokenPrivileges
            {
                PrivilegeCount = 1,
                Privilege = new LuidAndAttributes { Luid = luid, Attributes = SePrivilegeEnabled }
            };
            if (!AdjustTokenPrivileges(
                    token,
                    disableAllPrivileges: false,
                    ref requested,
                    (uint)Marshal.SizeOf<TokenPrivileges>(),
                    out var previous,
                    out _))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Windows could not enable {privilegeName}.");
            var result = Marshal.GetLastWin32Error();
            if (result == ErrorNotAllAssigned)
                throw new UnauthorizedAccessException($"LocalSystem does not hold required privilege {privilegeName}.");
            if (result != 0)
                throw new Win32Exception(result, $"Windows could not enable {privilegeName}.");
            return new ScopedProcessPrivilege(token, previous);
        }
        catch
        {
            token.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (!RestoreTokenPrivileges(
                    _token,
                    disableAllPrivileges: false,
                    ref _previous,
                    0,
                    IntPtr.Zero,
                    IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not restore a Guardian process privilege.");
        }
        finally
        {
            _token.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LuidAndAttributes
    {
        public Luid Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public LuidAndAttributes Privilege;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(
        IntPtr process,
        uint desiredAccess,
        out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValueW(string? systemName, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(
        SafeAccessTokenHandle token,
        [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
        ref TokenPrivileges newState,
        uint bufferLength,
        out TokenPrivileges previousState,
        out uint returnLength);

    [DllImport("advapi32.dll", EntryPoint = "AdjustTokenPrivileges", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RestoreTokenPrivileges(
        SafeAccessTokenHandle token,
        [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
        ref TokenPrivileges newState,
        uint bufferLength,
        IntPtr previousState,
        IntPtr returnLength);
}
