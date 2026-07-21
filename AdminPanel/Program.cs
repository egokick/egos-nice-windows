using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace AdminPanel;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, "NiceWindows.AdminPanel.Singleton", out var createdNew);
        if (!createdNew)
        {
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new AdminPanelApplicationContext());
    }
}

internal sealed class AdminPanelApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _icon;
    private AdminPanelForm? _panel;

    public AdminPanelApplicationContext()
    {
        _icon = AdminPanelTrayIconFactory.CreateLightIcon();

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Open Admin Panel", null, (_, _) => ShowPanel()));
        var startWithWindowsItem = new ToolStripMenuItem("Start with Windows")
        {
            CheckOnClick = true,
            Checked = AdminPanelStartupRegistration.IsEnabled()
        };
        startWithWindowsItem.Click += (_, _) => ToggleStartWithWindows(startWithWindowsItem);
        menu.Opening += (_, _) =>
        {
            startWithWindowsItem.Checked = AdminPanelStartupRegistration.IsEnabled();
        };
        menu.Items.Add(startWithWindowsItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitThread()));

        _notifyIcon = new NotifyIcon
        {
            Icon = _icon,
            Text = "Nice Windows Admin Panel",
            ContextMenuStrip = menu,
            Visible = true
        };
        _notifyIcon.MouseClick += (_, args) =>
        {
            if (args.Button == MouseButtons.Left)
            {
                ShowPanel();
            }
        };
    }

    protected override void ExitThreadCore()
    {
        var panel = _panel;
        _panel = null;
        panel?.Close();
        panel?.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _icon.Dispose();
        base.ExitThreadCore();
    }

    private void ShowPanel()
    {
        if (_panel is null || _panel.IsDisposed)
        {
            var panel = new AdminPanelForm();
            _panel = panel;
            panel.FormClosed += (_, _) =>
            {
                if (ReferenceEquals(_panel, panel))
                {
                    _panel = null;
                }
            };
        }

        if (_panel.WindowState == FormWindowState.Minimized)
        {
            _panel.WindowState = FormWindowState.Normal;
        }

        _panel.CenterAndFitToCurrentScreen();
        if (!_panel.Visible)
        {
            _panel.Show();
        }

        _panel.Activate();
        _panel.BringToFront();
    }

    private void ToggleStartWithWindows(ToolStripMenuItem menuItem)
    {
        if (AdminPanelStartupRegistration.TrySetEnabled(menuItem.Checked, out var errorMessage))
        {
            _notifyIcon.ShowBalloonTip(
                2500,
                "Nice Windows Admin Panel",
                menuItem.Checked
                    ? "The Admin Panel will start with Windows."
                    : "The Admin Panel has been removed from Windows startup.",
                ToolTipIcon.Info);
            return;
        }

        menuItem.Checked = AdminPanelStartupRegistration.IsEnabled();
        _notifyIcon.ShowBalloonTip(
            4000,
            "Nice Windows Admin Panel",
            errorMessage,
            ToolTipIcon.Error);
    }
}

internal static class AdminPanelStartupRegistration
{
    private const string RunRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "NiceWindowsAdminPanel";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunRegistryPath, writable: false);
        return key?.GetValue(ValueName) is string registeredCommand
               && !string.IsNullOrWhiteSpace(registeredCommand);
    }

    public static bool TrySetEnabled(bool enabled, out string errorMessage)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunRegistryPath);
            if (enabled)
            {
                key.SetValue(ValueName, BuildCommand(), RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            errorMessage = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is IOException
                                             or UnauthorizedAccessException
                                             or System.Security.SecurityException)
        {
            errorMessage = $"Could not update Windows startup: {exception.Message}";
            return false;
        }
    }

    private static string BuildCommand()
    {
        var executable = Environment.ProcessPath ?? Application.ExecutablePath;
        if (string.Equals(Path.GetFileName(executable), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            return $"\"{executable}\" \"{typeof(Program).Assembly.Location}\"";
        }

        return $"\"{executable}\"";
    }
}

internal static class AdminPanelThemeService
{
    private const string PersonalizeRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AppsUseLightTheme = "AppsUseLightTheme";

    public static bool IsLightModeEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(PersonalizeRegistryPath, writable: false);
        return key?.GetValue(AppsUseLightTheme) is int value && value != 0;
    }
}

internal static class AdminPanelTrayIconFactory
{
    public static Icon CreateLightIcon() => CreateIcon(
        Color.FromArgb(18, 61, 90),
        Color.FromArgb(59, 130, 246));

    public static Icon CreateDarkIcon() => CreateIcon(
        Color.FromArgb(226, 232, 240),
        Color.FromArgb(96, 165, 250));

    private static Icon CreateIcon(Color background, Color accent)
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);

        using var tile = new SolidBrush(background);
        using var accentBrush = new SolidBrush(accent);
        using var success = new SolidBrush(Color.FromArgb(52, 211, 153));
        using var violet = new SolidBrush(Color.FromArgb(167, 139, 250));
        using var orange = new SolidBrush(Color.FromArgb(251, 146, 60));
        using var path = CreateRoundedRectangle(new RectangleF(2, 2, 28, 28), 8f);
        graphics.FillPath(tile, path);
        graphics.FillRectangle(accentBrush, 7, 7, 7, 7);
        graphics.FillRectangle(success, 18, 7, 7, 7);
        graphics.FillRectangle(violet, 7, 18, 7, 7);
        graphics.FillRectangle(orange, 18, 18, 7, 7);

        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            _ = DestroyIcon(handle);
        }
    }

    private static GraphicsPath CreateRoundedRectangle(RectangleF rectangle, float radius)
    {
        var path = new GraphicsPath();
        var diameter = radius * 2f;
        path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
