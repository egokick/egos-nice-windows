using System.Diagnostics;
using System.Runtime.InteropServices;

namespace StayActive;

internal interface IUserActivityMonitor : IDisposable
{
    event EventHandler? RealUserActivity;
}

internal sealed class UserActivityMonitor : IUserActivityMonitor
{
    private const int WhKeyboardLl = 13;
    private const int WhMouseLl = 14;
    private const uint LlkhfInjected = 0x00000010;
    private const uint LlmhfInjected = 0x00000001;

    private readonly HookProc _keyboardHookProc;
    private readonly HookProc _mouseHookProc;
    private IntPtr _keyboardHook;
    private IntPtr _mouseHook;

    public UserActivityMonitor()
    {
        _keyboardHookProc = KeyboardHookCallback;
        _mouseHookProc = MouseHookCallback;
        _keyboardHook = InstallHook(WhKeyboardLl, _keyboardHookProc);
        _mouseHook = InstallHook(WhMouseLl, _mouseHookProc);
    }

    public event EventHandler? RealUserActivity;

    public void Dispose()
    {
        if (_keyboardHook != IntPtr.Zero)
        {
            _ = UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }

        if (_mouseHook != IntPtr.Zero)
        {
            _ = UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<KeyboardHookInfo>(lParam);
            if ((info.flags & LlkhfInjected) == 0 && info.dwExtraInfo != InputSimulator.SyntheticInputMarker)
            {
                RealUserActivity?.Invoke(this, EventArgs.Empty);
            }
        }

        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<MouseHookInfo>(lParam);
            if ((info.flags & LlmhfInjected) == 0 && info.dwExtraInfo != InputSimulator.SyntheticInputMarker)
            {
                RealUserActivity?.Invoke(this, EventArgs.Empty);
            }
        }

        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private static IntPtr InstallHook(int hookId, HookProc callback)
    {
        using var currentProcess = Process.GetCurrentProcess();
        using var currentModule = currentProcess.MainModule;
        var moduleHandle = currentModule is null
            ? IntPtr.Zero
            : GetModuleHandle(currentModule.ModuleName);
        var hook = SetWindowsHookEx(hookId, callback, moduleHandle, 0);
        if (hook == IntPtr.Zero)
        {
            throw new InvalidOperationException($"Failed to install hook {hookId}. Win32 error: {Marshal.GetLastWin32Error()}");
        }

        return hook;
    }

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardHookInfo
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseHookInfo
    {
        public Point pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
}
