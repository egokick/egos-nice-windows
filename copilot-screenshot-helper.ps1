$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Windows.Forms

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$pidFile = Join-Path $scriptDir "copilot-screenshot-helper.pid"
$logFile = Join-Path $scriptDir "copilot-screenshot-helper.log"

$source = @"
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

public static class CopilotScreenshotHook
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int VK_F23 = 0x86;
    private const int VK_SHIFT = 0x10;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;

    private static IntPtr _hookId = IntPtr.Zero;
    private static HookProc _proc = HookCallback;
    private static long _lastTriggerTicks = 0;

    public static void Run()
    {
        _hookId = SetHook(_proc);
        Application.Run();
        UnhookWindowsHookEx(_hookId);
    }

    private static IntPtr SetHook(HookProc proc)
    {
        using (Process curProcess = Process.GetCurrentProcess())
        using (ProcessModule curModule = curProcess.MainModule)
        {
            return SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(curModule.ModuleName), 0);
        }
    }

    private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
        {
            int vkCode = Marshal.ReadInt32(lParam);
            bool shiftDown = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;
            bool winDown = (GetAsyncKeyState(VK_LWIN) & 0x8000) != 0 || (GetAsyncKeyState(VK_RWIN) & 0x8000) != 0;

            if (vkCode == VK_F23 && shiftDown && winDown)
            {
                long nowTicks = DateTime.UtcNow.Ticks;
                if (nowTicks - _lastTriggerTicks > TimeSpan.FromMilliseconds(400).Ticks)
                {
                    _lastTriggerTicks = nowTicks;
                    Process.Start(new ProcessStartInfo("ms-screenclip:") { UseShellExecute = true });
                }

                return (IntPtr)1;
            }
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}
"@

Set-Content -Path $logFile -Value "Starting helper $PID at $(Get-Date -Format o)"
Add-Type -TypeDefinition $source -ReferencedAssemblies System.Windows.Forms
Set-Content -Path $pidFile -Value $PID

try {
    [CopilotScreenshotHook]::Run()
}
catch {
    Add-Content -Path $logFile -Value $_.ToString()
    throw
}
finally {
    Remove-Item $pidFile -Force -ErrorAction SilentlyContinue
}
