using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using Microsoft.Win32;

namespace StayActive;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, "StayActive.Singleton", out var createdNew);
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
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _activeMenuItem;
    private readonly ToolStripMenuItem _jiggleMouseMenuItem;
    private readonly ToolStripMenuItem _typeTextMenuItem;
    private readonly ToolStripMenuItem _startupMenuItem;
    private readonly ToolStripMenuItem _dimScreenMenuItem;
    private readonly ToolStripMenuItem _enableAfterInactivityMenuItem;
    private readonly ToolStripControlHost _idleThresholdHost;
    private readonly ToolStripMenuItem _editTextMenuItem;
    private readonly Icon _activeIcon;
    private readonly Icon _inactiveIcon;
    private readonly SynchronizationContext _uiContext;
    private readonly InactivitySliderControl _idleThresholdControl;
    private readonly StickyContextMenuStrip _contextMenu;
    private readonly object _settingsLock = new();
    private readonly object _idleSessionLock = new();
    private readonly ActivitySessionController _activityController;
    private readonly IUserActivityMonitor _userActivityMonitor;

    private AppSettings _settings;
    private CancellationTokenSource? _idleSessionCancellation;
    private CancellationTokenSource? _runnerCancellation;
    private Task? _runnerTask;
    private bool _powerAssertionActive;

    public TrayApplicationContext()
    {
        _uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _settings = SettingsStore.Load();
        _activityController = new ActivitySessionController(new SystemIdleMonitor(), new SystemMonitorBrightnessService());
        _userActivityMonitor = new UserActivityMonitor();
        _userActivityMonitor.RealUserActivity += (_, _) => _uiContext.Post(_ => HandleRealUserActivity(), null);
        _activeIcon = TrayIconFactory.CreateEyeOpenIcon();
        _inactiveIcon = TrayIconFactory.CreateEyeClosedIcon();

        _activeMenuItem = new ToolStripMenuItem("Active")
        {
            CheckOnClick = true
        };
        _activeMenuItem.Click += (_, _) => SetActive(_activeMenuItem.Checked, showBalloon: true);

        _jiggleMouseMenuItem = new ToolStripMenuItem("Jiggle mouse")
        {
            CheckOnClick = true
        };
        _jiggleMouseMenuItem.Click += (_, _) => ToggleJiggleMouse();

        _typeTextMenuItem = new ToolStripMenuItem("Type text")
        {
            CheckOnClick = true
        };
        _typeTextMenuItem.Click += (_, _) => ToggleTypeText();

        _startupMenuItem = new ToolStripMenuItem("Run at Windows startup")
        {
            CheckOnClick = true
        };
        _startupMenuItem.Click += (_, _) => ToggleStartup();

        _dimScreenMenuItem = new ToolStripMenuItem("Dim screen when active")
        {
            CheckOnClick = true
        };
        _dimScreenMenuItem.Click += (_, _) => ToggleDimScreen();

        _enableAfterInactivityMenuItem = new ToolStripMenuItem("Enable after inactivity")
        {
            CheckOnClick = true
        };
        _enableAfterInactivityMenuItem.Click += (_, _) => ToggleEnableAfterInactivity();

        _idleThresholdControl = new InactivitySliderControl(_settings.EnableAfterInactivitySeconds);
        _idleThresholdControl.ThresholdCommitted += (_, _) => UpdateIdleThreshold(_idleThresholdControl.TotalSeconds);
        _idleThresholdHost = CreateControlHost(_idleThresholdControl);

        _editTextMenuItem = new ToolStripMenuItem("Edit text file");
        _editTextMenuItem.Click += (_, _) => OpenTextFile();

        var exitMenuItem = new ToolStripMenuItem("Exit");
        exitMenuItem.Click += (_, _) => ExitThread();

        _contextMenu = new StickyContextMenuStrip();
        _contextMenu.Opening += (_, _) => RefreshUi();
        _contextMenu.RegisterStickyItem(_activeMenuItem);
        _contextMenu.RegisterStickyItem(_jiggleMouseMenuItem);
        _contextMenu.RegisterStickyItem(_typeTextMenuItem);
        _contextMenu.RegisterStickyItem(_startupMenuItem);
        _contextMenu.RegisterStickyItem(_dimScreenMenuItem);
        _contextMenu.RegisterStickyItem(_enableAfterInactivityMenuItem);
        _contextMenu.Items.Add(_activeMenuItem);
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add(_jiggleMouseMenuItem);
        _contextMenu.Items.Add(_typeTextMenuItem);
        _contextMenu.Items.Add(_startupMenuItem);
        _contextMenu.Items.Add(_dimScreenMenuItem);
        _contextMenu.Items.Add(_enableAfterInactivityMenuItem);
        _contextMenu.Items.Add(_idleThresholdHost);
        _contextMenu.Items.Add(_editTextMenuItem);
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add(exitMenuItem);

        _notifyIcon = new NotifyIcon
        {
            ContextMenuStrip = _contextMenu,
            Icon = _settings.IsActive ? _activeIcon : _inactiveIcon,
            Visible = true
        };
        _notifyIcon.MouseClick += NotifyIconOnMouseClick;

        RefreshActivityController();
        RefreshUi();
        EnsureStartupPreference();
        ApplyRunnerState();
    }

    protected override void ExitThreadCore()
    {
        StopRunner();
        SyncIdleSessionCancellation(idleSessionActive: false);
        _activityController.Shutdown();
        _userActivityMonitor.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _activeIcon.Dispose();
        _inactiveIcon.Dispose();
        base.ExitThreadCore();
    }

    private void NotifyIconOnMouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            SetActive(!GetSettingsSnapshot().IsActive, showBalloon: true);
        }
    }

    private AppSettings GetSettingsSnapshot()
    {
        lock (_settingsLock)
        {
            return _settings.Clone();
        }
    }

    private void UpdateSettings(Action<AppSettings> update)
    {
        lock (_settingsLock)
        {
            update(_settings);
            SettingsStore.Save(_settings);
        }
    }

    private void SyncIdleSessionCancellation(bool idleSessionActive)
    {
        CancellationTokenSource? cancellationToDispose = null;

        lock (_idleSessionLock)
        {
            if (idleSessionActive)
            {
                if (_idleSessionCancellation is null || _idleSessionCancellation.IsCancellationRequested)
                {
                    cancellationToDispose = _idleSessionCancellation;
                    _idleSessionCancellation = new CancellationTokenSource();
                }
            }
            else if (_idleSessionCancellation is not null)
            {
                cancellationToDispose = _idleSessionCancellation;
                _idleSessionCancellation = null;
            }
        }

        if (cancellationToDispose is null)
        {
            return;
        }

        try
        {
            cancellationToDispose.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            cancellationToDispose.Dispose();
        }
    }

    private CancellationToken GetIdleSessionToken()
    {
        lock (_idleSessionLock)
        {
            return _idleSessionCancellation?.Token ?? CancellationToken.None;
        }
    }

    private CancellationTokenSource? CreateActivityScope(CancellationToken runnerToken, bool idleSessionActive)
    {
        if (!idleSessionActive)
        {
            return null;
        }

        var idleSessionToken = GetIdleSessionToken();
        if (!idleSessionToken.CanBeCanceled)
        {
            return null;
        }

        return CancellationTokenSource.CreateLinkedTokenSource(runnerToken, idleSessionToken);
    }

    private bool RefreshActivityController()
    {
        var changed = _activityController.Refresh(GetSettingsSnapshot());
        SyncIdleSessionCancellation(_activityController.IdleSessionActive);
        return changed;
    }

    private void SetActive(bool isActive, bool showBalloon)
    {
        UpdateSettings(settings => settings.IsActive = isActive);
        RefreshActivityController();
        RefreshUi();
        ApplyRunnerState();

        if (showBalloon)
        {
            ShowInfoBalloon(isActive ? "StayActive enabled." : "StayActive disabled.");
        }
    }

    private void ToggleJiggleMouse()
    {
        UpdateSettings(settings => settings.JiggleMouseEnabled = _jiggleMouseMenuItem.Checked);
        RefreshActivityController();
        RefreshUi();
        ApplyRunnerState();
    }

    private void ToggleTypeText()
    {
        UpdateSettings(settings => settings.TypeTextEnabled = _typeTextMenuItem.Checked);
        RefreshActivityController();
        RefreshUi();
        ApplyRunnerState();
    }

    private void ToggleDimScreen()
    {
        UpdateSettings(settings => settings.DimScreenWhenActiveEnabled = _dimScreenMenuItem.Checked);
        RefreshActivityController();
        RefreshUi();
    }

    private void ToggleEnableAfterInactivity()
    {
        UpdateSettings(settings => settings.EnableAfterInactivityEnabled = _enableAfterInactivityMenuItem.Checked);
        RefreshActivityController();
        RefreshUi();
        ApplyRunnerState();
    }

    private void OpenTextFile()
    {
        try
        {
            var path = SettingsStore.EnsureTextFileExists();
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ShowErrorBalloon($"Could not open text file: {ex.Message}");
        }
    }

    private void ToggleStartup()
    {
        try
        {
            StartupService.SetRunAtStartup(_startupMenuItem.Checked);
            RefreshUi();
        }
        catch (Exception ex)
        {
            _startupMenuItem.Checked = StartupService.IsRunAtStartupEnabled();
            ShowErrorBalloon($"Startup update failed: {ex.Message}");
        }
    }

    private void EnsureStartupPreference()
    {
        try
        {
            StartupService.EnsureInitialized();
        }
        catch (Exception ex)
        {
            ShowErrorBalloon($"Startup setup failed: {ex.Message}");
        }
    }

    private void UpdateIdleThreshold(int totalSeconds)
    {
        var settings = GetSettingsSnapshot();
        if (settings.EnableAfterInactivitySeconds == totalSeconds)
        {
            return;
        }

        UpdateSettings(current => current.EnableAfterInactivitySeconds = totalSeconds);
        RefreshActivityController();
        RefreshUi();
        ApplyRunnerState();
    }

    private void HandleRealUserActivity()
    {
        var settings = GetSettingsSnapshot();
        if (!_activityController.HandleRealUserActivity(settings))
        {
            return;
        }

        SyncIdleSessionCancellation(idleSessionActive: false);
        RefreshUi();
    }

    private void ApplyRunnerState()
    {
        var settings = GetSettingsSnapshot();
        var hasWorkEnabled = settings.JiggleMouseEnabled || settings.TypeTextEnabled;
        if (hasWorkEnabled && (settings.IsActive || settings.EnableAfterInactivityEnabled))
        {
            StartRunner();
            return;
        }

        StopRunner();
    }

    private void StartRunner()
    {
        if (_runnerTask is { IsCompleted: false })
        {
            return;
        }

        _runnerCancellation = new CancellationTokenSource();
        _runnerTask = Task.Run(() => RunLoopAsync(_runnerCancellation.Token));
    }

    private void StopRunner()
    {
        var cancellation = _runnerCancellation;
        _runnerCancellation = null;
        PowerAssertion.Clear();
        _powerAssertionActive = false;
        if (cancellation is null)
        {
            return;
        }

        cancellation.Cancel();
        cancellation.Dispose();
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var settings = GetSettingsSnapshot();

            try
            {
                if (_activityController.Refresh(settings))
                {
                    SyncIdleSessionCancellation(_activityController.IdleSessionActive);
                    _uiContext.Post(_ => RefreshUi(), null);
                }
                else
                {
                    SyncIdleSessionCancellation(_activityController.IdleSessionActive);
                }

                var effectiveActive = _activityController.IsEffectivelyActive(settings);
                if (!effectiveActive)
                {
                    if (_powerAssertionActive)
                    {
                        PowerAssertion.Clear();
                        _powerAssertionActive = false;
                    }

                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                    continue;
                }

                using var activityScope = CreateActivityScope(cancellationToken, _activityController.IdleSessionActive);
                var activityToken = activityScope?.Token ?? cancellationToken;

                if (!_powerAssertionActive)
                {
                    PowerAssertion.Enable();
                    _powerAssertionActive = true;
                }

                if (settings.JiggleMouseEnabled)
                {
                    InputSimulator.JiggleMouse(activityToken);
                }

                if (settings.TypeTextEnabled)
                {
                    var text = SettingsStore.ReadTypingText();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        await TypeTextAsync(text, activityToken);
                        await Task.Delay(TimeSpan.FromSeconds(5), activityToken);
                        continue;
                    }
                }

                await Task.Delay(ActivityProfile.GetIdleDelay(), activityToken);
            }
            catch (OperationCanceledException)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    continue;
                }

                break;
            }
            catch (Exception ex)
            {
                _uiContext.Post(_ => ShowErrorBalloon($"StayActive error: {ex.Message}"), null);
                await Task.Delay(ActivityProfile.GetIdleDelay(), cancellationToken);
            }
        }
    }

    private async Task TypeTextAsync(string text, CancellationToken cancellationToken)
    {
        for (var index = 0; index < text.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var character = text[index];
            InputSimulator.TypeCharacter(character);
            var delay = TypingProfile.GetDelay(text, index);
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }

            if (character == '\n')
            {
                var linePause = TypingProfile.GetLineCompletionPause();
                if (linePause is not null)
                {
                    await Task.Delay(linePause.Value, cancellationToken);
                }
            }
        }
    }

    private void RefreshUi()
    {
        var settings = GetSettingsSnapshot();
        _activeMenuItem.Checked = settings.IsActive;
        _jiggleMouseMenuItem.Checked = settings.JiggleMouseEnabled;
        _typeTextMenuItem.Checked = settings.TypeTextEnabled;
        _startupMenuItem.Checked = StartupService.IsRunAtStartupEnabled();
        _dimScreenMenuItem.Checked = settings.DimScreenWhenActiveEnabled;
        _enableAfterInactivityMenuItem.Checked = settings.EnableAfterInactivityEnabled;
        _idleThresholdControl.TotalSeconds = settings.EnableAfterInactivitySeconds;
        _idleThresholdHost.Visible = settings.EnableAfterInactivityEnabled;

        var effectiveActive = _activityController.IsEffectivelyActive(settings);
        _notifyIcon.Icon = effectiveActive ? _activeIcon : _inactiveIcon;
        _notifyIcon.Text = effectiveActive
            ? (settings.IsActive ? "StayActive: active" : "StayActive: active after inactivity")
            : "StayActive: inactive";
    }

    private void ShowInfoBalloon(string message)
    {
        _notifyIcon.ShowBalloonTip(1500, "StayActive", message, ToolTipIcon.Info);
    }

    private void ShowErrorBalloon(string message)
    {
        _notifyIcon.ShowBalloonTip(2500, "StayActive", message, ToolTipIcon.Error);
    }

    private static ToolStripControlHost CreateControlHost(Control control)
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

internal sealed class AppSettings
{
    public bool StartupInitialized { get; set; }

    public bool IsActive { get; set; }

    public bool JiggleMouseEnabled { get; set; } = true;

    public bool TypeTextEnabled { get; set; }

    public bool DimScreenWhenActiveEnabled { get; set; }

    public bool EnableAfterInactivityEnabled { get; set; }

    public int EnableAfterInactivitySeconds { get; set; } = 300;

    public AppSettings Clone()
    {
        return new AppSettings
        {
            StartupInitialized = StartupInitialized,
            IsActive = IsActive,
            JiggleMouseEnabled = JiggleMouseEnabled,
            TypeTextEnabled = TypeTextEnabled,
            DimScreenWhenActiveEnabled = DimScreenWhenActiveEnabled,
            EnableAfterInactivityEnabled = EnableAfterInactivityEnabled,
            EnableAfterInactivitySeconds = EnableAfterInactivitySeconds
        };
    }
}

internal static class StartupService
{
    private const string RunRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "StayActive";

    public static void EnsureInitialized()
    {
        var settings = SettingsStore.Load();
        if (settings.StartupInitialized)
        {
            return;
        }

        SetRunAtStartup(true);
        settings.StartupInitialized = true;
        SettingsStore.Save(settings);
    }

    public static bool IsRunAtStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunRegistryPath, false);
        var value = key?.GetValue(AppName) as string;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return string.Equals(value.Trim('"'), Application.ExecutablePath, StringComparison.OrdinalIgnoreCase);
    }

    public static void SetRunAtStartup(bool enable)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunRegistryPath);
        if (enable)
        {
            key.SetValue(AppName, $"\"{Application.ExecutablePath}\"", RegistryValueKind.String);
            return;
        }

        if (key.GetValue(AppName) is not null)
        {
            key.DeleteValue(AppName, throwOnMissingValue: false);
        }
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
        "StayActive");

    private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "settings.json");
    private static readonly string TextFilePath = Path.Combine(SettingsDirectory, "type-text.txt");

    public static AppSettings Load()
    {
        try
        {
            EnsureTextFileExists();
            if (!File.Exists(SettingsPath))
            {
                return new AppSettings();
            }

            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(SettingsDirectory);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
    }

    public static string EnsureTextFileExists()
    {
        Directory.CreateDirectory(SettingsDirectory);
        if (!File.Exists(TextFilePath))
        {
            File.WriteAllText(
                TextFilePath,
                "Paste the text you want StayActive to type here." + Environment.NewLine);
        }

        return TextFilePath;
    }

    public static string ReadTypingText()
    {
        return File.ReadAllText(EnsureTextFileExists());
    }
}

internal static class BrightnessService
{
    public static BrightnessSnapshot? CaptureCurrentBrightness()
    {
        using var brightnessSearcher = new ManagementObjectSearcher(
            @"root\wmi",
            "SELECT * FROM WmiMonitorBrightness WHERE Active = TRUE");

        var levels = new List<MonitorBrightnessLevel>();
        foreach (ManagementObject brightness in brightnessSearcher.Get())
        {
            var instanceName = brightness["InstanceName"] as string;
            var currentBrightness = brightness["CurrentBrightness"];
            if (string.IsNullOrWhiteSpace(instanceName) || currentBrightness is null)
            {
                continue;
            }

            levels.Add(new MonitorBrightnessLevel(instanceName, Convert.ToByte(currentBrightness)));
        }

        return levels.Count == 0
            ? null
            : new BrightnessSnapshot(levels);
    }

    public static bool TrySetLowestBrightness()
    {
        var lowestBrightness = GetLowestSupportedBrightness();
        var updated = false;

        using var methodsSearcher = new ManagementObjectSearcher(
            @"root\wmi",
            "SELECT * FROM WmiMonitorBrightnessMethods WHERE Active = TRUE");

        foreach (ManagementObject method in methodsSearcher.Get())
        {
            method.InvokeMethod("WmiSetBrightness", new object[] { 1u, lowestBrightness });
            updated = true;
        }

        return updated;
    }

    public static void Restore(BrightnessSnapshot snapshot)
    {
        var brightnessByMonitor = snapshot.Levels.ToDictionary(level => level.InstanceName, level => level.Brightness, StringComparer.OrdinalIgnoreCase);
        if (brightnessByMonitor.Count == 0)
        {
            return;
        }

        using var methodsSearcher = new ManagementObjectSearcher(
            @"root\wmi",
            "SELECT * FROM WmiMonitorBrightnessMethods WHERE Active = TRUE");

        foreach (ManagementObject method in methodsSearcher.Get())
        {
            var instanceName = method["InstanceName"] as string;
            if (string.IsNullOrWhiteSpace(instanceName)
                || !brightnessByMonitor.TryGetValue(instanceName, out var brightness))
            {
                continue;
            }

            method.InvokeMethod("WmiSetBrightness", new object[] { 1u, brightness });
        }
    }

    private static byte GetLowestSupportedBrightness()
    {
        using var brightnessSearcher = new ManagementObjectSearcher(
            @"root\wmi",
            "SELECT * FROM WmiMonitorBrightness WHERE Active = TRUE");

        byte? lowest = null;
        foreach (ManagementObject brightness in brightnessSearcher.Get())
        {
            if (brightness["Levels"] is not Array levels)
            {
                continue;
            }

            foreach (var level in levels)
            {
                var brightnessLevel = Convert.ToByte(level);
                if (lowest is null || brightnessLevel < lowest.Value)
                {
                    lowest = brightnessLevel;
                }
            }
        }

        return lowest ?? 0;
    }
}

internal sealed class InactivitySliderControl : UserControl
{
    private static readonly int[] SliderValues = BuildSliderValues();

    private readonly Label _valueLabel;
    private readonly TrackBar _trackBar;
    private readonly System.Windows.Forms.Timer _commitTimer;
    private int _committedSeconds;
    private bool _isInteracting;

    public InactivitySliderControl(int totalSeconds)
    {
        Size = new Size(300, 78);
        Margin = Padding.Empty;
        Padding = Padding.Empty;
        BackColor = SystemColors.Control;
        DoubleBuffered = true;

        var titleLabel = new Label
        {
            AutoSize = true,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            Location = new Point(10, 8),
            Text = "After idle for"
        };

        _valueLabel = new Label
        {
            AutoSize = true,
            Location = new Point(172, 10)
        };

        _trackBar = new TrackBar
        {
            Minimum = 0,
            Maximum = SliderValues.Length - 1,
            TickFrequency = 5,
            LargeChange = 3,
            SmallChange = 1,
            AutoSize = false,
            Bounds = new Rectangle(10, 30, 280, 36),
            Value = SecondsToSliderIndex(totalSeconds)
        };
        _commitTimer = new System.Windows.Forms.Timer
        {
            Interval = 250
        };
        _commitTimer.Tick += (_, _) => CommitCurrentValue();
        _trackBar.Scroll += (_, _) =>
        {
            _valueLabel.Text = FormatDuration(TotalSeconds);
            ScheduleCommit();
        };
        _trackBar.MouseDown += (_, _) => _isInteracting = true;
        _trackBar.MouseUp += (_, _) => CommitCurrentValue();
        _trackBar.MouseWheel += (_, _) => ScheduleCommit();
        _trackBar.KeyUp += (_, e) =>
        {
            if (e.KeyCode is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Home or Keys.End or Keys.PageDown or Keys.PageUp)
            {
                CommitCurrentValue();
            }
        };
        _trackBar.Leave += (_, _) => CommitCurrentValue();
        _trackBar.MouseCaptureChanged += (_, _) =>
        {
            if (_isInteracting && Control.MouseButtons == MouseButtons.None)
            {
                CommitCurrentValue();
            }
        };

        Controls.Add(titleLabel);
        Controls.Add(_valueLabel);
        Controls.Add(_trackBar);

        _committedSeconds = TotalSeconds;
        _valueLabel.Text = FormatDuration(TotalSeconds);
    }

    public event EventHandler? ThresholdCommitted;

    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int TotalSeconds
    {
        get => SliderValues[_trackBar.Value];
        set
        {
            var normalized = SecondsToSliderIndex(value);
            if (_trackBar.Value == normalized)
            {
                _committedSeconds = SliderValues[normalized];
                _valueLabel.Text = FormatDuration(TotalSeconds);
                return;
            }

            _trackBar.Value = normalized;
            _committedSeconds = TotalSeconds;
            _valueLabel.Text = FormatDuration(TotalSeconds);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _commitTimer.Dispose();
        }

        base.Dispose(disposing);
    }

    private static int SecondsToSliderIndex(int totalSeconds)
    {
        var normalized = Math.Clamp(totalSeconds, SliderValues[0], SliderValues[^1]);
        var bestIndex = 0;
        var bestDistance = int.MaxValue;

        for (var index = 0; index < SliderValues.Length; index++)
        {
            var distance = Math.Abs(SliderValues[index] - normalized);
            if (distance >= bestDistance)
            {
                continue;
            }

            bestDistance = distance;
            bestIndex = index;
        }

        return bestIndex;
    }

    private static string FormatDuration(int totalSeconds)
    {
        var duration = TimeSpan.FromSeconds(totalSeconds);
        if (duration.TotalMinutes < 1)
        {
            return "30 sec";
        }

        if (duration.TotalMinutes < 60)
        {
            return duration.TotalMinutes == 1
                ? "1 min"
                : $"{duration.TotalMinutes:0} min";
        }

        if (duration.TotalHours < 24)
        {
            return Math.Abs(duration.TotalHours - Math.Round(duration.TotalHours)) < 0.001
                ? $"{duration.TotalHours:0} hr"
                : $"{duration.TotalHours:0.#} hr";
        }

        return "24 hr";
    }

    private void CommitCurrentValue()
    {
        _commitTimer.Stop();
        _isInteracting = false;
        var totalSeconds = TotalSeconds;
        _valueLabel.Text = FormatDuration(totalSeconds);
        if (totalSeconds == _committedSeconds)
        {
            return;
        }

        _committedSeconds = totalSeconds;
        ThresholdCommitted?.Invoke(this, EventArgs.Empty);
    }

    private void ScheduleCommit()
    {
        _commitTimer.Stop();
        _commitTimer.Start();
    }

    private static int[] BuildSliderValues()
    {
        var values = new List<int>();

        for (var seconds = 30; seconds <= 45 * 60; seconds += 30)
        {
            values.Add(seconds);
        }

        for (var hours = 1; hours <= 24; hours++)
        {
            values.Add(hours * 60 * 60);
        }

        return values.ToArray();
    }
}

internal sealed class StickyContextMenuStrip : ContextMenuStrip
{
    private readonly HashSet<ToolStripItem> _stickyItems = new();
    private ToolStripItem? _lastClickedItem;

    public StickyContextMenuStrip()
    {
        ItemClicked += (_, e) => _lastClickedItem = e.ClickedItem;
        Closing += OnClosing;
    }

    public void RegisterStickyItem(ToolStripItem item)
    {
        _stickyItems.Add(item);
    }

    private void OnClosing(object? sender, ToolStripDropDownClosingEventArgs e)
    {
        if (e.CloseReason == ToolStripDropDownCloseReason.ItemClicked
            && _lastClickedItem is not null
            && _stickyItems.Contains(_lastClickedItem))
        {
            e.Cancel = true;
        }
    }
}

internal static class IdleMonitor
{
    public static TimeSpan GetInactiveDuration()
    {
        var info = new LastInputInfo
        {
            cbSize = (uint)Marshal.SizeOf<LastInputInfo>()
        };

        if (!GetLastInputInfo(ref info))
        {
            return TimeSpan.Zero;
        }

        var currentTick = unchecked((uint)Environment.TickCount64);
        var elapsedMilliseconds = unchecked(currentTick - info.dwTime);
        return TimeSpan.FromMilliseconds(elapsedMilliseconds);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetLastInputInfo(ref LastInputInfo plii);

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint cbSize;
        public uint dwTime;
    }
}

internal static class TypingProfile
{
    private static readonly object SyncLock = new();
    private static readonly Random Random = new();

    public static TimeSpan? GetLineCompletionPause()
    {
        lock (SyncLock)
        {
            if (Random.NextDouble() >= 0.30)
            {
                return null;
            }

            return TimeSpan.FromSeconds(Random.Next(10, 21));
        }
    }

    public static TimeSpan GetDelay(string text, int index)
    {
        var character = text[index];
        var previous = index > 0 ? text[index - 1] : '\0';
        var next = index + 1 < text.Length ? text[index + 1] : '\0';

        var baseDelayMs = character switch
        {
            '\r' => 0,
            '\n' => 220,
            '\t' => 130,
            ' ' => 85,
            '.' or ',' => 210,
            ';' or ':' => 190,
            '!' or '?' => 240,
            '-' => 95,
            _ when char.IsUpper(character) => 92,
            _ when char.IsDigit(character) => 88,
            _ => 72
        };

        lock (SyncLock)
        {
            var variance = Random.Next(8, 95);
            var delay = baseDelayMs + variance;

            if (char.IsWhiteSpace(character) && next != '\0')
            {
                delay += Random.Next(15, 55);
            }

            if (character is '.' or ',' or ';' or ':' or '!' or '?')
            {
                delay += Random.Next(70, 220);
            }

            if (character == '\n')
            {
                delay += Random.Next(120, 260);
            }

            if (char.IsLetterOrDigit(character) && Random.NextDouble() < 0.16)
            {
                delay += Random.Next(25, 110);
            }

            if (char.IsLetter(character) && char.IsLetter(previous) && char.IsLetter(next) && Random.NextDouble() < 0.10)
            {
                delay = Math.Max(35, delay - Random.Next(8, 28));
            }

            if (Random.NextDouble() < 0.035)
            {
                delay += Random.Next(160, 420);
            }

            return TimeSpan.FromMilliseconds(delay);
        }
    }
}

internal static class ActivityProfile
{
    private static readonly object SyncLock = new();
    private static readonly Random Random = new();

    public static TimeSpan GetIdleDelay()
    {
        lock (SyncLock)
        {
            return TimeSpan.FromSeconds(Random.Next(9, 21));
        }
    }

    public static (int Radius, int Steps, double StartAngleDegrees) GetCirclePattern()
    {
        lock (SyncLock)
        {
            return (
                Radius: Random.Next(9, 19),
                Steps: Random.Next(14, 25),
                StartAngleDegrees: Random.NextDouble() * 360.0);
        }
    }
}

internal static class InputSimulator
{
    private const int InputMouse = 0;
    private const int InputKeyboard = 1;
    private const uint MouseeventfMove = 0x0001;
    private const uint KeyeventfKeyup = 0x0002;
    private const uint KeyeventfUnicode = 0x0004;
    private const ushort VkReturn = 0x0D;
    private const ushort VkTab = 0x09;
    internal static readonly IntPtr SyntheticInputMarker = new(unchecked((long)0x5354415941435449));

    public static void JiggleMouse(CancellationToken cancellationToken)
    {
        var pattern = ActivityProfile.GetCirclePattern();
        var previousX = 0;
        var previousY = 0;

        for (var step = 0; step < pattern.Steps; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var angle = (pattern.StartAngleDegrees + (step * 360.0 / pattern.Steps)) * Math.PI / 180.0;
            var wobble = Math.Sin(angle * 2.0) * 1.5;
            var radius = pattern.Radius + wobble;
            var nextX = (int)Math.Round(Math.Cos(angle) * radius);
            var nextY = (int)Math.Round(Math.Sin(angle) * radius);
            SendMouseMove(nextX - previousX, nextY - previousY);
            previousX = nextX;
            previousY = nextY;
            Thread.Sleep(30);
        }

        if (previousX != 0 || previousY != 0)
        {
            SendMouseMove(-previousX, -previousY);
        }
    }

    public static void TypeCharacter(char character)
    {
        switch (character)
        {
            case '\r':
                return;
            case '\n':
                SendVirtualKey(VkReturn);
                return;
            case '\t':
                SendVirtualKey(VkTab);
                return;
        }

        SendUnicodeCharacter(character);
    }

    private static void SendMouseMove(int deltaX, int deltaY)
    {
        var inputs = new[]
        {
            new Input
            {
                type = InputMouse,
                U = new InputUnion
                {
                    mi = new MouseInput
                    {
                        dx = deltaX,
                        dy = deltaY,
                        dwFlags = MouseeventfMove,
                        dwExtraInfo = SyntheticInputMarker
                    }
                }
            }
        };

        _ = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
    }

    private static void SendVirtualKey(ushort virtualKey)
    {
        var inputs = new[]
        {
            new Input
            {
                type = InputKeyboard,
                U = new InputUnion
                {
                    ki = new KeyboardInput
                    {
                        wVk = virtualKey,
                        dwExtraInfo = SyntheticInputMarker
                    }
                }
            },
            new Input
            {
                type = InputKeyboard,
                U = new InputUnion
                {
                    ki = new KeyboardInput
                    {
                        wVk = virtualKey,
                        dwFlags = KeyeventfKeyup,
                        dwExtraInfo = SyntheticInputMarker
                    }
                }
            }
        };

        _ = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
    }

    private static void SendUnicodeCharacter(char character)
    {
        var inputs = new[]
        {
            new Input
            {
                type = InputKeyboard,
                U = new InputUnion
                {
                    ki = new KeyboardInput
                    {
                        wScan = character,
                        dwFlags = KeyeventfUnicode,
                        dwExtraInfo = SyntheticInputMarker
                    }
                }
            },
            new Input
            {
                type = InputKeyboard,
                U = new InputUnion
                {
                    ki = new KeyboardInput
                    {
                        wScan = character,
                        dwFlags = KeyeventfUnicode | KeyeventfKeyup,
                        dwExtraInfo = SyntheticInputMarker
                    }
                }
            }
        };

        _ = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, Input[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public int type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MouseInput mi;

        [FieldOffset(0)]
        public KeyboardInput ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }
}

internal static class PowerAssertion
{
    private const uint EsSystemRequired = 0x00000001;
    private const uint EsDisplayRequired = 0x00000002;
    private const uint EsContinuous = 0x80000000;

    public static void Enable()
    {
        _ = SetThreadExecutionState(EsContinuous | EsSystemRequired | EsDisplayRequired);
    }

    public static void Clear()
    {
        _ = SetThreadExecutionState(EsContinuous);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SetThreadExecutionState(uint esFlags);
}

internal static class TrayIconFactory
{
    public static Icon CreateEyeOpenIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = CreateGraphics(bitmap);
        using var outlinePen = new Pen(Color.FromArgb(32, 32, 32), 2.2f);
        using var irisBrush = new SolidBrush(Color.FromArgb(59, 130, 246));
        using var pupilBrush = new SolidBrush(Color.FromArgb(15, 23, 42));
        using var scleraBrush = new SolidBrush(Color.WhiteSmoke);

        using var eyePath = new GraphicsPath();
        eyePath.AddArc(4, 7, 24, 18, 180, 180);
        eyePath.AddArc(4, 7, 24, 18, 0, 180);
        eyePath.CloseFigure();

        graphics.FillPath(scleraBrush, eyePath);
        graphics.DrawPath(outlinePen, eyePath);
        graphics.FillEllipse(irisBrush, 11, 10, 10, 10);
        graphics.FillEllipse(pupilBrush, 14, 13, 4, 4);
        graphics.FillEllipse(Brushes.White, 16, 12, 2, 2);

        return CreateIcon(bitmap);
    }

    public static Icon CreateEyeClosedIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = CreateGraphics(bitmap);
        using var outlinePen = new Pen(Color.FromArgb(32, 32, 32), 2.4f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        using var lashPen = new Pen(Color.FromArgb(120, 120, 120), 1.7f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        using var alertPen = new Pen(Color.FromArgb(220, 38, 38), 2.2f)
        {
            Alignment = PenAlignment.Inset
        };

        graphics.DrawEllipse(alertPen, 2, 2, 27, 27);
        graphics.DrawArc(outlinePen, 4, 10, 24, 10, 200, 140);
        graphics.DrawLine(lashPen, 9, 14, 6, 10);
        graphics.DrawLine(lashPen, 14, 16, 13, 10);
        graphics.DrawLine(lashPen, 19, 16, 21, 10);
        graphics.DrawLine(lashPen, 24, 14, 27, 10);

        return CreateIcon(bitmap);
    }

    private static Graphics CreateGraphics(Bitmap bitmap)
    {
        var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);
        return graphics;
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
