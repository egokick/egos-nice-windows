using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CopilotScreenshotRemap;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var pidFile = args.Length > 0
            ? args[0]
            : Path.Combine(AppContext.BaseDirectory, "copilot-screenshot-helper.pid");

        var logFile = args.Length > 1
            ? args[1]
            : Path.Combine(AppContext.BaseDirectory, "copilot-screenshot-helper.log");

        ApplicationConfiguration.Initialize();
        File.WriteAllText(pidFile, Environment.ProcessId.ToString());

        try
        {
            using var form = new HotkeyForm(logFile);
            Application.Run(form);
        }
        catch (Exception ex)
        {
            File.AppendAllText(logFile, $"{DateTime.Now:O} {ex}{Environment.NewLine}");
            throw;
        }
        finally
        {
            TryDelete(pidFile);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}

internal sealed class HotkeyForm : Form
{
    private const int WmHotkey = 0x0312;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint VkF23 = 0x86;
    private const int HotkeyId = 1;

    private readonly string _logFile;
    private long _lastTriggerTicks;

    public HotkeyForm(string logFile)
    {
        _logFile = logFile;
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        WindowState = FormWindowState.Minimized;
        Opacity = 0;
    }

    protected override void SetVisibleCore(bool value)
    {
        base.SetVisibleCore(false);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        if (!RegisterHotKey(Handle, HotkeyId, ModWin | ModShift, VkF23))
        {
            var error = Marshal.GetLastWin32Error();
            File.AppendAllText(_logFile, $"{DateTime.Now:O} RegisterHotKey failed with {error}.{Environment.NewLine}");
            throw new InvalidOperationException($"RegisterHotKey failed with {error}.");
        }
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        UnregisterHotKey(Handle, HotkeyId);
        base.OnHandleDestroyed(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotkey && m.WParam.ToInt32() == HotkeyId)
        {
            var nowTicks = DateTime.UtcNow.Ticks;
            if (nowTicks - _lastTriggerTicks > TimeSpan.FromMilliseconds(400).Ticks)
            {
                _lastTriggerTicks = nowTicks;
                try
                {
                    Process.Start(new ProcessStartInfo("ms-screenclip:") { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    File.AppendAllText(_logFile, $"{DateTime.Now:O} {ex}{Environment.NewLine}");
                }
            }
        }

        base.WndProc(ref m);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
