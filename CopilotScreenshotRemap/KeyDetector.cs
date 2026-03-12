using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CopilotKeyDetector;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var logFile = args.Length > 0
            ? args[0]
            : Path.Combine(AppContext.BaseDirectory, "copilot-key-detect.log");

        ApplicationConfiguration.Initialize();
        using var form = new DetectorForm(logFile);
        Application.Run(form);
    }
}

internal sealed class DetectorForm : Form
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const int WmQuit = 0x0012;

    private readonly string _logFile;
    private readonly HookProc _hookProc;
    private IntPtr _hookHandle;

    public DetectorForm(string logFile)
    {
        _logFile = logFile;
        _hookProc = HookCallback;
        ShowInTaskbar = true;
        Text = "Copilot Key Detector";
        Width = 420;
        Height = 120;

        var label = new Label
        {
            Dock = DockStyle.Fill,
            Text = "Press the Copilot key once. The key data will be written to:\r\n" + logFile + "\r\nClose this window after testing.",
            TextAlign = ContentAlignment.MiddleCenter
        };

        Controls.Add(label);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _hookHandle = SetWindowsHookEx(WhKeyboardLl, _hookProc, GetModuleHandle(Process.GetCurrentProcess().MainModule!.ModuleName), 0);
        if (_hookHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException($"SetWindowsHookEx failed with {Marshal.GetLastWin32Error()}.");
        }
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (_hookHandle != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }

        base.OnHandleDestroyed(e);
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == (IntPtr)WmKeyDown || wParam == (IntPtr)WmSysKeyDown))
        {
            var data = Marshal.PtrToStructure<Kbdllhookstruct>(lParam);
            var shiftDown = (GetAsyncKeyState(0x10) & 0x8000) != 0;
            var ctrlDown = (GetAsyncKeyState(0x11) & 0x8000) != 0;
            var altDown = (GetAsyncKeyState(0x12) & 0x8000) != 0;
            var lWinDown = (GetAsyncKeyState(0x5B) & 0x8000) != 0;
            var rWinDown = (GetAsyncKeyState(0x5C) & 0x8000) != 0;

            var line = $"{DateTime.Now:O} vk=0x{data.VkCode:X2} scan=0x{data.ScanCode:X2} flags=0x{data.Flags:X2} shift={shiftDown} ctrl={ctrlDown} alt={altDown} lwin={lWinDown} rwin={rWinDown}";
            File.AppendAllText(_logFile, line + Environment.NewLine);
        }

        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Kbdllhookstruct
    {
        public uint VkCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr DwExtraInfo;
    }

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
