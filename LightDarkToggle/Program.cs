using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace LightDarkToggle;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, "LightDarkToggle.Singleton", out var createdNew);
        if (!createdNew)
        {
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApplicationContext());
    }
}

internal sealed class TrayApplicationContext : ApplicationContext
{
    private const string LightScheme = "Tango Light";
    private const string DarkScheme = "One Half Dark";

    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _toggleMenuItem;
    private readonly Icon _lightIcon;
    private readonly Icon _darkIcon;

    public TrayApplicationContext()
    {
        _lightIcon = TrayIconFactory.CreateSunIcon();
        _darkIcon = TrayIconFactory.CreateMoonIcon();
        _toggleMenuItem = new ToolStripMenuItem();
        _toggleMenuItem.Click += (_, _) => ToggleTheme();

        var exitMenuItem = new ToolStripMenuItem("Exit");
        exitMenuItem.Click += (_, _) => ExitThread();

        _notifyIcon = new NotifyIcon
        {
            Icon = _darkIcon,
            Visible = true,
            Text = "LightDarkToggle",
            ContextMenuStrip = new ContextMenuStrip()
        };

        _notifyIcon.ContextMenuStrip.Items.Add(_toggleMenuItem);
        _notifyIcon.ContextMenuStrip.Items.Add(new ToolStripSeparator());
        _notifyIcon.ContextMenuStrip.Items.Add(exitMenuItem);
        _notifyIcon.MouseClick += NotifyIconOnMouseClick;

        RefreshMenuText();
    }

    protected override void ExitThreadCore()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _lightIcon.Dispose();
        _darkIcon.Dispose();
        base.ExitThreadCore();
    }

    private void NotifyIconOnMouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            ToggleTheme();
        }
    }

    private void ToggleTheme()
    {
        try
        {
            var enableLightMode = !ThemeService.IsLightModeEnabled();
            ThemeService.SetWindowsTheme(enableLightMode);
            ThemeService.SetWindowsTerminalPowerShellScheme(enableLightMode ? LightScheme : DarkScheme);
            ThemeService.NotifyThemeChanged();
            RefreshMenuText();

            _notifyIcon.ShowBalloonTip(
                1500,
                "LightDarkToggle",
                enableLightMode ? "Switched to light mode." : "Switched to dark mode.",
                ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            _notifyIcon.ShowBalloonTip(
                2500,
                "LightDarkToggle",
                $"Toggle failed: {ex.Message}",
                ToolTipIcon.Error);
        }
    }

    private void RefreshMenuText()
    {
        var isLightMode = ThemeService.IsLightModeEnabled();
        _toggleMenuItem.Text = isLightMode ? "Switch to dark mode" : "Switch to light mode";
        _notifyIcon.Text = isLightMode ? "LightDarkToggle: light mode" : "LightDarkToggle: dark mode";
        _notifyIcon.Icon = isLightMode ? _lightIcon : _darkIcon;
    }
}

internal static class TrayIconFactory
{
    public static Icon CreateSunIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);

        using var rayPen = new Pen(Color.FromArgb(255, 215, 64), 2.6f)
        {
            StartCap = System.Drawing.Drawing2D.LineCap.Round,
            EndCap = System.Drawing.Drawing2D.LineCap.Round
        };
        using var sunBrush = new SolidBrush(Color.FromArgb(255, 204, 64));
        using var glowBrush = new SolidBrush(Color.FromArgb(140, 255, 235, 140));

        graphics.FillEllipse(glowBrush, 7, 7, 18, 18);
        graphics.FillEllipse(sunBrush, 10, 10, 12, 12);

        var center = new PointF(16, 16);
        for (var i = 0; i < 8; i++)
        {
            var angle = Math.PI * i / 4.0;
            var start = new PointF(
                center.X + (float)(Math.Cos(angle) * 9),
                center.Y + (float)(Math.Sin(angle) * 9));
            var end = new PointF(
                center.X + (float)(Math.Cos(angle) * 13),
                center.Y + (float)(Math.Sin(angle) * 13));
            graphics.DrawLine(rayPen, start, end);
        }

        return CreateIcon(bitmap);
    }

    public static Icon CreateMoonIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);

        using var moonBrush = new SolidBrush(Color.FromArgb(235, 240, 255));
        using var cutoutBrush = new SolidBrush(Color.Transparent);
        using var starBrush = new SolidBrush(Color.FromArgb(255, 213, 79));

        graphics.FillEllipse(moonBrush, 7, 5, 18, 20);
        graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
        graphics.FillEllipse(cutoutBrush, 13, 4, 14, 20);
        graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceOver;

        graphics.FillEllipse(starBrush, 22, 7, 3, 3);
        graphics.FillEllipse(starBrush, 8, 23, 2, 2);
        graphics.FillEllipse(starBrush, 20, 21, 2, 2);

        return CreateIcon(bitmap);
    }

    private static Icon CreateIcon(Bitmap bitmap)
    {
        var handle = bitmap.GetHicon();
        try
        {
            using var icon = Icon.FromHandle(handle);
            return (Icon)icon.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}

internal static class ThemeService
{
    private const string PersonalizeRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AppsUseLightTheme = "AppsUseLightTheme";
    private const string SystemUsesLightTheme = "SystemUsesLightTheme";
    private const int HwndBroadcast = 0xffff;
    private const int WmSettingChange = 0x001A;
    private const int SmtoAbortIfHung = 0x0002;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static bool IsLightModeEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(PersonalizeRegistryPath, false);
        var value = key?.GetValue(AppsUseLightTheme);
        return value is int intValue && intValue != 0;
    }

    public static void SetWindowsTheme(bool enableLightMode)
    {
        using var key = Registry.CurrentUser.CreateSubKey(PersonalizeRegistryPath);
        var dword = enableLightMode ? 1 : 0;
        key.SetValue(AppsUseLightTheme, dword, RegistryValueKind.DWord);
        key.SetValue(SystemUsesLightTheme, dword, RegistryValueKind.DWord);
    }

    public static void SetWindowsTerminalPowerShellScheme(string schemeName)
    {
        var settingsPath = GetWindowsTerminalSettingsPath();
        if (settingsPath is null || !File.Exists(settingsPath))
        {
            return;
        }

        var json = File.ReadAllText(settingsPath);
        var root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        })?.AsObject();

        if (root is null)
        {
            return;
        }

        var profilesList = root["profiles"]?["list"]?.AsArray();
        if (profilesList is null)
        {
            return;
        }

        var updated = false;
        foreach (var profileNode in profilesList)
        {
            if (profileNode is not JsonObject profile)
            {
                continue;
            }

            var name = profile["name"]?.GetValue<string>() ?? string.Empty;
            var commandLine = profile["commandline"]?.GetValue<string>() ?? string.Empty;
            if (!IsPowerShellProfile(name, commandLine))
            {
                continue;
            }

            profile["colorScheme"] = schemeName;
            updated = true;
        }

        if (!updated)
        {
            return;
        }

        File.WriteAllText(settingsPath, root.ToJsonString(JsonOptions));
    }

    public static void NotifyThemeChanged()
    {
        _ = SendMessageTimeout(
            HwndBroadcast,
            WmSettingChange,
            IntPtr.Zero,
            "ImmersiveColorSet",
            SmtoAbortIfHung,
            100,
            out _);
    }

    private static bool IsPowerShellProfile(string name, string commandLine)
    {
        return name.Contains("PowerShell", StringComparison.OrdinalIgnoreCase)
            || commandLine.Contains("powershell.exe", StringComparison.OrdinalIgnoreCase)
            || commandLine.Contains("pwsh.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetWindowsTerminalSettingsPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidateDirectories = Directory.GetDirectories(localAppData, "Microsoft.WindowsTerminal*", SearchOption.TopDirectoryOnly);
        if (candidateDirectories.Length == 0)
        {
            candidateDirectories = Directory.GetDirectories(Path.Combine(localAppData, "Packages"), "Microsoft.WindowsTerminal*", SearchOption.TopDirectoryOnly);
        }

        foreach (var directory in candidateDirectories)
        {
            var packagedSettings = Path.Combine(directory, "LocalState", "settings.json");
            if (File.Exists(packagedSettings))
            {
                return packagedSettings;
            }

            var unpackagedSettings = Path.Combine(directory, "settings.json");
            if (File.Exists(unpackagedSettings))
            {
                return unpackagedSettings;
            }
        }

        return null;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd,
        uint msg,
        IntPtr wParam,
        string lParam,
        uint fuFlags,
        uint uTimeout,
        out IntPtr lpdwResult);
}
