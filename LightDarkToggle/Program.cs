using System.ComponentModel;
using System.Management;
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
    private readonly ToolStripMenuItem _startupMenuItem;
    private readonly ToolStripMenuItem _timedModeMenuItem;
    private readonly ToolStripSeparator _timedSeparatorMenuItem;
    private readonly ScheduleSliderControl _lightSliderControl;
    private readonly ScheduleSliderControl _darkSliderControl;
    private readonly ToolStripControlHost _lightSliderHost;
    private readonly ToolStripControlHost _darkSliderHost;
    private readonly ToolStripSeparator _brightnessSeparatorMenuItem;
    private readonly BrightnessSliderControl _brightnessSliderControl;
    private readonly ToolStripControlHost _brightnessSliderHost;
    private readonly Icon _lightIcon;
    private readonly Icon _darkIcon;
    private readonly System.Windows.Forms.Timer _scheduleTimer;

    private AppSettings _settings;

    public TrayApplicationContext()
    {
        _settings = SettingsStore.Load();
        _lightIcon = TrayIconFactory.CreateSunIcon();
        _darkIcon = TrayIconFactory.CreateMoonIcon();

        _toggleMenuItem = new ToolStripMenuItem();
        _toggleMenuItem.Click += (_, _) => ToggleThemeManually();

        _startupMenuItem = new ToolStripMenuItem("Run at Windows startup")
        {
            CheckOnClick = true
        };
        _startupMenuItem.Click += (_, _) => ToggleStartup();

        _timedModeMenuItem = new ToolStripMenuItem("Timed Light/Dark")
        {
            CheckOnClick = true
        };
        _timedModeMenuItem.Click += (_, _) => ToggleTimedMode();

        _lightSliderControl = new ScheduleSliderControl("Light", _settings.LightHour);
        _lightSliderControl.HourCommitted += (_, _) => UpdateScheduledHour(isLightHour: true, _lightSliderControl.Hour);
        _lightSliderHost = CreateSliderHost(_lightSliderControl);

        _darkSliderControl = new ScheduleSliderControl("Dark", _settings.DarkHour);
        _darkSliderControl.HourCommitted += (_, _) => UpdateScheduledHour(isLightHour: false, _darkSliderControl.Hour);
        _darkSliderHost = CreateSliderHost(_darkSliderControl);

        _timedSeparatorMenuItem = new ToolStripSeparator();
        _brightnessSeparatorMenuItem = new ToolStripSeparator();

        _brightnessSliderControl = new BrightnessSliderControl();
        _brightnessSliderControl.BrightnessChanged += (_, _) => UpdateBrightness(_brightnessSliderControl.Brightness);
        _brightnessSliderHost = CreateSliderHost(_brightnessSliderControl);

        var exitMenuItem = new ToolStripMenuItem("Exit");
        exitMenuItem.Click += (_, _) => ExitThread();

        _notifyIcon = new NotifyIcon
        {
            Icon = _darkIcon,
            Visible = true,
            Text = "LightDarkToggle",
            ContextMenuStrip = new ContextMenuStrip()
        };

        _notifyIcon.ContextMenuStrip.Opening += (_, _) => RefreshMenuText(refreshBrightness: true);
        _notifyIcon.ContextMenuStrip.Items.Add(_toggleMenuItem);
        _notifyIcon.ContextMenuStrip.Items.Add(_startupMenuItem);
        _notifyIcon.ContextMenuStrip.Items.Add(_timedModeMenuItem);
        _notifyIcon.ContextMenuStrip.Items.Add(_timedSeparatorMenuItem);
        _notifyIcon.ContextMenuStrip.Items.Add(_lightSliderHost);
        _notifyIcon.ContextMenuStrip.Items.Add(_darkSliderHost);
        _notifyIcon.ContextMenuStrip.Items.Add(_brightnessSeparatorMenuItem);
        _notifyIcon.ContextMenuStrip.Items.Add(_brightnessSliderHost);
        _notifyIcon.ContextMenuStrip.Items.Add(new ToolStripSeparator());
        _notifyIcon.ContextMenuStrip.Items.Add(exitMenuItem);
        _notifyIcon.MouseClick += NotifyIconOnMouseClick;

        _scheduleTimer = new System.Windows.Forms.Timer
        {
            Interval = (int)TimeSpan.FromMinutes(1).TotalMilliseconds
        };
        _scheduleTimer.Tick += (_, _) => ApplyTimedThemeIfEnabled(showBalloon: false);
        _scheduleTimer.Start();

        ApplyTimedThemeIfEnabled(showBalloon: false);
        RefreshBrightnessControl();
        RefreshMenuText();
    }

    protected override void ExitThreadCore()
    {
        _scheduleTimer.Stop();
        _scheduleTimer.Dispose();
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
            ToggleThemeManually();
        }
    }

    private void ToggleThemeManually()
    {
        try
        {
            var enableLightMode = !ThemeService.IsLightModeEnabled();
            ApplyTheme(enableLightMode);
            ShowInfoBalloon(enableLightMode ? "Switched to light mode." : "Switched to dark mode.");
        }
        catch (Exception ex)
        {
            ShowErrorBalloon($"Toggle failed: {ex.Message}");
        }
    }

    private void ToggleStartup()
    {
        try
        {
            var enableStartup = _startupMenuItem.Checked;
            StartupService.SetRunAtStartup(enableStartup);
            RefreshMenuText();

            ShowInfoBalloon(enableStartup
                ? "LightDarkToggle will launch when you sign in."
                : "LightDarkToggle will no longer launch when you sign in.");
        }
        catch (Exception ex)
        {
            _startupMenuItem.Checked = StartupService.IsRunAtStartupEnabled();
            ShowErrorBalloon($"Startup update failed: {ex.Message}");
        }
    }

    private void ToggleTimedMode()
    {
        var enableTimedMode = _timedModeMenuItem.Checked;
        _settings.TimedModeEnabled = enableTimedMode;
        SettingsStore.Save(_settings);
        RefreshMenuText();

        if (!enableTimedMode)
        {
            ShowInfoBalloon("Timed Light/Dark disabled.");
            return;
        }

        ApplyTimedThemeIfEnabled(showBalloon: true);
    }

    private void ApplyTimedThemeIfEnabled(bool showBalloon)
    {
        if (!_settings.TimedModeEnabled)
        {
            return;
        }

        try
        {
            var enableLightMode = TimedThemeScheduler.ShouldUseLightTheme(
                DateTime.Now,
                _settings.LightHour,
                _settings.DarkHour);

            if (ThemeService.IsLightModeEnabled() == enableLightMode)
            {
                RefreshMenuText();
                return;
            }

            ApplyTheme(enableLightMode);
            if (showBalloon)
            {
                ShowInfoBalloon(enableLightMode
                    ? "Timed Light/Dark switched to light mode."
                    : "Timed Light/Dark switched to dark mode.");
            }
        }
        catch (Exception ex)
        {
            ShowErrorBalloon($"Timed Light/Dark failed: {ex.Message}");
        }
    }

    private void ApplyTheme(bool enableLightMode)
    {
        ThemeService.SetWindowsTheme(enableLightMode);
        ThemeService.SetWindowsTerminalPowerShellScheme(enableLightMode ? LightScheme : DarkScheme);
        ThemeService.NotifyThemeChanged();
        RefreshMenuText();
    }

    private void RefreshMenuText(bool refreshBrightness = false)
    {
        var isLightMode = ThemeService.IsLightModeEnabled();
        _toggleMenuItem.Text = isLightMode ? "Switch to dark mode" : "Switch to light mode";
        _notifyIcon.Text = isLightMode ? "LightDarkToggle: light mode" : "LightDarkToggle: dark mode";
        _notifyIcon.Icon = isLightMode ? _lightIcon : _darkIcon;

        _startupMenuItem.Checked = StartupService.IsRunAtStartupEnabled();
        _timedModeMenuItem.Checked = _settings.TimedModeEnabled;
        if (!_lightSliderControl.IsInteracting)
        {
            _lightSliderControl.Hour = _settings.LightHour;
        }

        if (!_darkSliderControl.IsInteracting)
        {
            _darkSliderControl.Hour = _settings.DarkHour;
        }

        var showTimedControls = _settings.TimedModeEnabled;
        _timedSeparatorMenuItem.Visible = showTimedControls;
        _lightSliderHost.Visible = showTimedControls;
        _darkSliderHost.Visible = showTimedControls;

        if (refreshBrightness)
        {
            RefreshBrightnessControl();
        }
    }

    private void ShowInfoBalloon(string message)
    {
        _notifyIcon.ShowBalloonTip(1500, "LightDarkToggle", message, ToolTipIcon.Info);
    }

    private void ShowErrorBalloon(string message)
    {
        _notifyIcon.ShowBalloonTip(2500, "LightDarkToggle", message, ToolTipIcon.Error);
    }

    private static string FormatHour(int hour)
    {
        return DateTime.Today.AddHours(hour).ToString("h tt");
    }

    private void UpdateScheduledHour(bool isLightHour, int hour)
    {
        if (isLightHour)
        {
            _settings.LightHour = hour;
        }
        else
        {
            _settings.DarkHour = hour;
        }

        SettingsStore.Save(_settings);
        RefreshMenuText();

        if (_settings.TimedModeEnabled)
        {
            ApplyTimedThemeIfEnabled(showBalloon: true);
        }
    }

    private void RefreshBrightnessControl()
    {
        if (_brightnessSliderControl.IsInteracting)
        {
            return;
        }

        if (BrightnessService.TryGetCurrentBrightness(out var brightness))
        {
            _brightnessSliderControl.SetAvailable(brightness);
            return;
        }

        _brightnessSliderControl.SetUnavailable();
    }

    private void UpdateBrightness(int brightness)
    {
        try
        {
            if (BrightnessService.TrySetBrightness(brightness))
            {
                return;
            }

            RefreshBrightnessControl();
            ShowErrorBalloon("Brightness control is unavailable on this display.");
        }
        catch (Exception ex)
        {
            RefreshBrightnessControl();
            ShowErrorBalloon($"Brightness update failed: {ex.Message}");
        }
    }

    private static ToolStripControlHost CreateSliderHost(Control control)
    {
        return new ToolStripControlHost(control)
        {
            AutoSize = false,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            Size = control.Size
        };
    }
}

internal sealed class BrightnessSliderControl : UserControl
{
    private readonly Label _titleLabel;
    private readonly Label _valueLabel;
    private readonly TrackBar _trackBar;
    private bool _isInteracting;

    public BrightnessSliderControl()
    {
        Size = new Size(280, 78);
        Margin = Padding.Empty;
        Padding = Padding.Empty;
        BackColor = SystemColors.Control;
        DoubleBuffered = true;

        _titleLabel = new Label
        {
            AutoSize = true,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            Location = new Point(10, 8),
            Text = "Brightness"
        };

        _valueLabel = new Label
        {
            AutoSize = true,
            Location = new Point(184, 10)
        };

        _trackBar = new TrackBar
        {
            Minimum = 0,
            Maximum = 100,
            TickFrequency = 10,
            LargeChange = 10,
            SmallChange = 1,
            AutoSize = false,
            Bounds = new Rectangle(10, 30, 260, 36),
            Value = 50
        };
        _trackBar.Scroll += (_, _) =>
        {
            UpdateValueLabel();
            BrightnessChanged?.Invoke(this, EventArgs.Empty);
        };
        _trackBar.MouseDown += (_, _) => _isInteracting = true;
        _trackBar.MouseUp += (_, _) => _isInteracting = false;
        _trackBar.MouseCaptureChanged += (_, _) =>
        {
            if (Control.MouseButtons == MouseButtons.None)
            {
                _isInteracting = false;
            }
        };

        Controls.Add(_titleLabel);
        Controls.Add(_valueLabel);
        Controls.Add(_trackBar);

        UpdateValueLabel();
    }

    public event EventHandler? BrightnessChanged;

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool IsInteracting => _isInteracting || _trackBar.Capture;

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Brightness
    {
        get => _trackBar.Value;
        set
        {
            var normalized = Math.Clamp(value, 0, 100);
            if (_trackBar.Value != normalized)
            {
                _trackBar.Value = normalized;
            }

            UpdateValueLabel();
        }
    }

    public void SetAvailable(int brightness)
    {
        _trackBar.Enabled = true;
        Brightness = brightness;
    }

    public void SetUnavailable()
    {
        _isInteracting = false;
        _trackBar.Enabled = false;
        _valueLabel.Text = "Unavailable";
    }

    private void UpdateValueLabel()
    {
        _valueLabel.Text = _trackBar.Enabled ? $"{_trackBar.Value}%" : "Unavailable";
    }
}

internal sealed class ScheduleSliderControl : UserControl
{
    private readonly Label _titleLabel;
    private readonly Label _valueLabel;
    private readonly TrackBar _trackBar;
    private bool _isInteracting;

    public ScheduleSliderControl(string title, int hour)
    {
        Size = new Size(280, 78);
        Margin = Padding.Empty;
        Padding = Padding.Empty;
        BackColor = SystemColors.Control;
        DoubleBuffered = true;

        _titleLabel = new Label
        {
            AutoSize = true,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            Location = new Point(10, 8),
            Text = title
        };

        _valueLabel = new Label
        {
            AutoSize = true,
            Location = new Point(196, 10)
        };

        _trackBar = new TrackBar
        {
            Minimum = 0,
            Maximum = 24,
            TickFrequency = 1,
            LargeChange = 1,
            SmallChange = 1,
            AutoSize = false,
            Bounds = new Rectangle(10, 30, 260, 36),
            Value = Math.Clamp(hour, 0, 24)
        };
        _trackBar.Scroll += (_, _) =>
        {
            _valueLabel.Text = FormatHour(_trackBar.Value);
        };
        _trackBar.MouseDown += (_, _) => _isInteracting = true;
        _trackBar.MouseUp += (_, _) => CommitCurrentValue();
        _trackBar.KeyUp += (_, e) =>
        {
            if (e.KeyCode is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Home or Keys.End or Keys.PageDown or Keys.PageUp)
            {
                CommitCurrentValue();
            }
        };
        _trackBar.MouseCaptureChanged += (_, _) =>
        {
            if (_isInteracting && Control.MouseButtons == MouseButtons.None)
            {
                CommitCurrentValue();
            }
        };

        Controls.Add(_titleLabel);
        Controls.Add(_valueLabel);
        Controls.Add(_trackBar);

        _valueLabel.Text = FormatHour(_trackBar.Value);
    }

    public event EventHandler? HourCommitted;

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool IsInteracting => _isInteracting || _trackBar.Capture;

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Hour
    {
        get => _trackBar.Value;
        set
        {
            var normalized = Math.Clamp(value, 0, 24);
            if (_trackBar.Value == normalized)
            {
                return;
            }

            _trackBar.Value = normalized;
            _valueLabel.Text = FormatHour(_trackBar.Value);
        }
    }

    private static string FormatHour(int hour)
    {
        return DateTime.Today.AddHours(hour % 24).ToString("h tt");
    }

    private void CommitCurrentValue()
    {
        _isInteracting = false;
        _valueLabel.Text = FormatHour(_trackBar.Value);
        HourCommitted?.Invoke(this, EventArgs.Empty);
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

internal static class TimedThemeScheduler
{
    public static bool ShouldUseLightTheme(DateTime currentTime, int lightHour, int darkHour)
    {
        lightHour = NormalizeHour(lightHour);
        darkHour = NormalizeHour(darkHour);

        if (lightHour == darkHour)
        {
            return true;
        }

        var currentHour = currentTime.Hour;
        if (lightHour < darkHour)
        {
            return currentHour >= lightHour && currentHour < darkHour;
        }

        return currentHour >= lightHour || currentHour < darkHour;
    }

    private static int NormalizeHour(int hour)
    {
        return ((hour % 24) + 24) % 24;
    }
}

internal static class StartupService
{
    private const string RunRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "LightDarkToggle";

    public static bool IsRunAtStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunRegistryPath, false);
        var value = key?.GetValue(AppName) as string;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return string.Equals(value.Trim('"'), GetStartupCommandPath(), StringComparison.OrdinalIgnoreCase);
    }

    public static void SetRunAtStartup(bool enable)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunRegistryPath);
        if (enable)
        {
            key.SetValue(AppName, $"\"{GetStartupCommandPath()}\"", RegistryValueKind.String);
            return;
        }

        if (key.GetValue(AppName) is not null)
        {
            key.DeleteValue(AppName, throwOnMissingValue: false);
        }
    }

    private static string GetStartupCommandPath()
    {
        return Application.ExecutablePath;
    }
}

internal sealed class AppSettings
{
    public bool TimedModeEnabled { get; set; }

    public int LightHour { get; set; } = 7;

    public int DarkHour { get; set; } = 19;

    public void Normalize()
    {
        LightHour = NormalizeHour(LightHour);
        DarkHour = NormalizeHour(DarkHour);
    }

    private static int NormalizeHour(int hour)
    {
        return ((hour % 24) + 24) % 24;
    }
}

internal static class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LightDarkToggle");

    private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new AppSettings();
            }

            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
            settings.Normalize();
            return settings;
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        settings.Normalize();
        Directory.CreateDirectory(SettingsDirectory);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
    }
}

internal static class BrightnessService
{
    public static bool TryGetCurrentBrightness(out int brightness)
    {
        var readings = ReadDdcBrightnessValues();
        readings.AddRange(ReadWmiBrightnessValues());
        if (readings.Count == 0)
        {
            brightness = 0;
            return false;
        }

        brightness = (int)Math.Round(readings.Average());
        return true;
    }

    public static bool TrySetBrightness(int brightness)
    {
        brightness = Math.Clamp(brightness, 0, 100);
        var updatedDdc = TrySetDdcBrightness(brightness);
        var updatedWmi = TrySetWmiBrightness(brightness);
        return updatedDdc || updatedWmi;
    }

    private static List<int> ReadDdcBrightnessValues()
    {
        var readings = new List<int>();

        try
        {
            ForEachPhysicalMonitor(handle =>
            {
                if (!GetMonitorBrightness(handle, out var minBrightness, out var currentBrightness, out var maxBrightness)
                    || maxBrightness <= minBrightness)
                {
                    return false;
                }

                readings.Add(NormalizeBrightness(minBrightness, currentBrightness, maxBrightness));
                return true;
            });
        }
        catch
        {
        }

        return readings;
    }

    private static bool TrySetDdcBrightness(int brightness)
    {
        try
        {
            return ForEachPhysicalMonitor(handle =>
            {
                if (!GetMonitorBrightness(handle, out var minBrightness, out _, out var maxBrightness)
                    || maxBrightness <= minBrightness)
                {
                    return false;
                }

                var rawBrightness = ScaleBrightness(brightness, minBrightness, maxBrightness);
                return SetMonitorBrightness(handle, rawBrightness);
            });
        }
        catch
        {
            return false;
        }
    }

    private static List<int> ReadWmiBrightnessValues()
    {
        var readings = new List<int>();

        try
        {
            using var brightnessSearcher = new ManagementObjectSearcher(
                @"root\wmi",
                "SELECT CurrentBrightness FROM WmiMonitorBrightness WHERE Active = TRUE");

            foreach (ManagementObject brightness in brightnessSearcher.Get())
            {
                readings.Add(Math.Clamp(Convert.ToInt32(brightness["CurrentBrightness"]), 0, 100));
            }
        }
        catch
        {
        }

        return readings;
    }

    private static bool TrySetWmiBrightness(int brightness)
    {
        try
        {
            var updated = false;
            using var methodsSearcher = new ManagementObjectSearcher(
                @"root\wmi",
                "SELECT * FROM WmiMonitorBrightnessMethods WHERE Active = TRUE");

            foreach (ManagementObject method in methodsSearcher.Get())
            {
                method.InvokeMethod("WmiSetBrightness", new object[] { 1u, (byte)brightness });
                updated = true;
            }

            return updated;
        }
        catch
        {
            return false;
        }
    }

    private static bool ForEachPhysicalMonitor(Func<IntPtr, bool> action)
    {
        var updated = false;
        EnumDisplayMonitors(
            IntPtr.Zero,
            IntPtr.Zero,
            (monitorHandle, _, _, _) =>
            {
                if (!GetNumberOfPhysicalMonitorsFromHMONITOR(monitorHandle, out var physicalMonitorCount)
                    || physicalMonitorCount == 0)
                {
                    return true;
                }

                var physicalMonitors = new PhysicalMonitor[(int)physicalMonitorCount];
                if (!GetPhysicalMonitorsFromHMONITOR(monitorHandle, physicalMonitorCount, physicalMonitors))
                {
                    return true;
                }

                try
                {
                    foreach (var physicalMonitor in physicalMonitors)
                    {
                        updated |= action(physicalMonitor.Handle);
                    }
                }
                finally
                {
                    DestroyPhysicalMonitors((uint)physicalMonitors.Length, physicalMonitors);
                }

                return true;
            },
            IntPtr.Zero);

        return updated;
    }

    private static int NormalizeBrightness(uint minBrightness, uint currentBrightness, uint maxBrightness)
    {
        return (int)Math.Round((currentBrightness - minBrightness) * 100d / (maxBrightness - minBrightness));
    }

    private static uint ScaleBrightness(int brightness, uint minBrightness, uint maxBrightness)
    {
        return (uint)Math.Round(minBrightness + ((maxBrightness - minBrightness) * (brightness / 100d)));
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PhysicalMonitor
    {
        public IntPtr Handle;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
    }

    private delegate bool MonitorEnumProc(IntPtr monitorHandle, IntPtr monitorDc, IntPtr monitorRect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(
        IntPtr hdc,
        IntPtr lprcClip,
        MonitorEnumProc lpfnEnum,
        IntPtr dwData);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint numberOfPhysicalMonitors);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(
        IntPtr hMonitor,
        uint physicalMonitorArraySize,
        [Out] PhysicalMonitor[] physicalMonitorArray);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool DestroyPhysicalMonitors(
        uint physicalMonitorArraySize,
        [In] PhysicalMonitor[] physicalMonitorArray);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetMonitorBrightness(
        IntPtr handle,
        out uint minimumBrightness,
        out uint currentBrightness,
        out uint maximumBrightness);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool SetMonitorBrightness(IntPtr handle, uint newBrightness);
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
