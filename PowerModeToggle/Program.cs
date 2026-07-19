using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace PowerModeToggle;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (PowerProfileBroker.TryRunElevatedHelper(args))
        {
            return;
        }

        if (args.Length == 2
            && string.Equals(args[0], "--probe-machine-profile", StringComparison.OrdinalIgnoreCase))
        {
            File.WriteAllText(
                args[1],
                JsonSerializer.Serialize(
                    PowerProfileService.Machine,
                    new JsonSerializerOptions { WriteIndented = true }));
            return;
        }

        if (args.Length == 2
            && string.Equals(args[0], "--probe-power-state", StringComparison.OrdinalIgnoreCase))
        {
            var state = PowerProfileService.ReadState();
            File.WriteAllText(args[1], JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
            return;
        }

        if (args.Length == 3
            && string.Equals(args[0], "--apply-power-profile", StringComparison.OrdinalIgnoreCase))
        {
            if (!Enum.TryParse<LaptopPowerMode>(args[1], ignoreCase: true, out var requestedMode))
            {
                throw new ArgumentException($"Unknown power profile '{args[1]}'.", nameof(args));
            }

            using var broker = new PowerProfileBroker();
            var result = broker.ApplyAsync(requestedMode).GetAwaiter().GetResult();
            File.WriteAllText(args[2], JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            Environment.ExitCode = result.Success ? 0 : 1;
            return;
        }

        if (args.Length == 2 && string.Equals(args[0], "--render-icon-preview", StringComparison.OrdinalIgnoreCase))
        {
            TrayIconFactory.RenderPreview(args[1]);
            return;
        }

        if (args.Length == 2
            && string.Equals(args[0], "--probe-power-telemetry", StringComparison.OrdinalIgnoreCase))
        {
            using var telemetry = new PowerTelemetryService(PowerProfileService.Machine.Profile);
            _ = telemetry.Sample();
            Thread.Sleep(TimeSpan.FromSeconds(2));
            var sample = telemetry.Sample();
            File.WriteAllText(args[1], JsonSerializer.Serialize(sample, new JsonSerializerOptions { WriteIndented = true }));
            Environment.ExitCode = sample.EstimatedUsageWatts is null ? 1 : 0;
            return;
        }

        if (args.Length == 2
            && string.Equals(args[0], "--self-test-power-savings", StringComparison.OrdinalIgnoreCase))
        {
            var result = PowerSavingsSelfTest.Run();
            File.WriteAllText(args[1], JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            return;
        }

        using var mutex = new Mutex(true, AppIdentity.SingletonMutex, out var createdNew);
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
    private readonly ToolStripMenuItem _statusMenuItem;
    private readonly ToolStripMenuItem _highUsageMenuItem;
    private readonly ToolStripMenuItem _lowUsageMenuItem;
    private readonly ToolStripMenuItem _savingRateMenuItem;
    private readonly ToolStripMenuItem _energySavedMenuItem;
    private readonly ToolStripMenuItem _measurementMenuItem;
    private readonly ToolStripMenuItem _autoSwitchMenuItem;
    private readonly ToolStripMenuItem _startupMenuItem;
    private readonly Icon _highPowerIcon;
    private readonly Icon _lowPowerIcon;
    private readonly Icon[] _switchingToHighIcons;
    private readonly Icon[] _switchingToLowIcons;
    private readonly PowerProfileBroker _powerProfileBroker;
    private readonly PowerTelemetryService _powerTelemetryService;
    private readonly PowerSavingsEstimator _powerSavingsEstimator;
    private readonly System.Windows.Forms.Timer _powerSourceTimer;
    private readonly System.Windows.Forms.Timer _powerTelemetryTimer;
    private readonly System.Windows.Forms.Timer _switchingIconTimer;
    private readonly SynchronizationContext _uiContext;

    private AppSettings _settings;
    private LaptopPowerMode _currentMode;
    private PowerLineStatus _lastPowerSource = PowerLineStatus.Unknown;
    private LaptopPowerMode? _queuedMode;
    private string _queuedReason = "Manual switch";
    private Icon[]? _activeSwitchingIcons;
    private int _switchingIconFrame;
    private bool _modeChangeRunning;
    private PowerSavingsSnapshot? _powerSavingsSnapshot;
    private DateTimeOffset _lastPowerSavingsSaveUtc = DateTimeOffset.MinValue;

    public TrayApplicationContext()
    {
        StartupService.MigrateExistingEntry();
        _uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _settings = SettingsStore.Load();
        _currentMode = _settings.LastHighPowerMode ? LaptopPowerMode.HighPower : LaptopPowerMode.LowPower;
        _powerProfileBroker = new PowerProfileBroker();
        _powerTelemetryService = new PowerTelemetryService(PowerProfileService.Machine.Profile);
        _powerSavingsEstimator = new PowerSavingsEstimator(
            _settings.TotalEstimatedEnergySavedWh,
            _settings.PowerUsageBaselines);
        _highPowerIcon = TrayIconFactory.CreateHighPowerIcon();
        _lowPowerIcon = TrayIconFactory.CreateLowPowerIcon();
        _switchingToHighIcons = TrayIconFactory.CreateSwitchingIcons(Color.FromArgb(255, 133, 38));
        _switchingToLowIcons = TrayIconFactory.CreateSwitchingIcons(Color.FromArgb(48, 211, 153));

        _statusMenuItem = new ToolStripMenuItem
        {
            Enabled = false,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold)
        };

        _highUsageMenuItem = CreateTelemetryMenuItem("Estimated HIGH usage: warming up...");
        _lowUsageMenuItem = CreateTelemetryMenuItem("Estimated LOW usage: warming up...");
        _savingRateMenuItem = CreateTelemetryMenuItem("Estimated saving now: learning...");
        _energySavedMenuItem = CreateTelemetryMenuItem("Energy saved in ECO: 0.000 Wh");
        _measurementMenuItem = CreateTelemetryMenuItem("Measurement: detecting sensors...");

        _autoSwitchMenuItem = new ToolStripMenuItem("Auto Switch When Plugged In")
        {
            CheckOnClick = true,
            Checked = _settings.AutoSwitchWhenPluggedIn
        };
        _autoSwitchMenuItem.Click += (_, _) => ToggleAutoSwitch();

        _startupMenuItem = new ToolStripMenuItem("Start With Windows")
        {
            CheckOnClick = true
        };
        _startupMenuItem.Click += (_, _) => ToggleStartup();

        var exitMenuItem = new ToolStripMenuItem("Exit");
        exitMenuItem.Click += (_, _) => ExitThread();

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusMenuItem);
        menu.Items.Add(_highUsageMenuItem);
        menu.Items.Add(_lowUsageMenuItem);
        menu.Items.Add(_savingRateMenuItem);
        menu.Items.Add(_energySavedMenuItem);
        menu.Items.Add(_measurementMenuItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_autoSwitchMenuItem);
        menu.Items.Add(_startupMenuItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitMenuItem);
        menu.Opening += (_, _) => RefreshUi(readHardwareState: true);

        _notifyIcon = new NotifyIcon
        {
            Icon = _lowPowerIcon,
            Visible = true,
            Text = $"{AppIdentity.DisplayName}: LOW power",
            ContextMenuStrip = menu
        };
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                TogglePowerMode();
            }
        };

        _switchingIconTimer = new System.Windows.Forms.Timer
        {
            Interval = 85
        };
        _switchingIconTimer.Tick += (_, _) => AdvanceSwitchingIcon();

        _powerSourceTimer = new System.Windows.Forms.Timer
        {
            Interval = (int)TimeSpan.FromSeconds(10).TotalMilliseconds
        };
        _powerSourceTimer.Tick += (_, _) => CheckForPowerSourceChange(forceApply: false);
        _powerSourceTimer.Start();

        _powerTelemetryTimer = new System.Windows.Forms.Timer
        {
            Interval = (int)TimeSpan.FromSeconds(2).TotalMilliseconds
        };
        _powerTelemetryTimer.Tick += (_, _) => SamplePowerTelemetry();
        _powerTelemetryTimer.Start();

        SystemEvents.PowerModeChanged += SystemEventsOnPowerModeChanged;

        RefreshUi(readHardwareState: true);
        SamplePowerTelemetry();
        ConfigureStartupOnFirstLaunch();
        CheckForPowerSourceChange(forceApply: _settings.AutoSwitchWhenPluggedIn);
    }

    protected override void ExitThreadCore()
    {
        SystemEvents.PowerModeChanged -= SystemEventsOnPowerModeChanged;
        _switchingIconTimer.Stop();
        _switchingIconTimer.Dispose();
        _powerSourceTimer.Stop();
        _powerSourceTimer.Dispose();
        _powerTelemetryTimer.Stop();
        _powerTelemetryTimer.Dispose();
        SavePowerSavings(force: true);
        _powerTelemetryService.Dispose();
        _powerProfileBroker.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _highPowerIcon.Dispose();
        _lowPowerIcon.Dispose();
        foreach (var icon in _switchingToHighIcons)
        {
            icon.Dispose();
        }

        foreach (var icon in _switchingToLowIcons)
        {
            icon.Dispose();
        }

        base.ExitThreadCore();
    }

    private static ToolStripMenuItem CreateTelemetryMenuItem(string text)
    {
        return new ToolStripMenuItem(text)
        {
            Enabled = false
        };
    }

    private void SamplePowerTelemetry()
    {
        try
        {
            var reading = _powerTelemetryService.Sample();
            if (_modeChangeRunning)
            {
                _measurementMenuItem.Text = $"Measurement paused while switching ({reading.SourceDescription})";
                return;
            }

            _powerSavingsSnapshot = _powerSavingsEstimator.Update(_currentMode, reading);
            SavePowerSavings(force: false);
            RefreshPowerTelemetryUi();
        }
        catch (Exception ex)
        {
            _measurementMenuItem.Text = $"Measurement unavailable: {Shorten(ex.Message, 60)}";
        }
    }

    private void SavePowerSavings(bool force)
    {
        var now = DateTimeOffset.UtcNow;
        var state = _powerSavingsEstimator.GetPersistentState();
        _settings.TotalEstimatedEnergySavedWh = state.TotalEnergySavedWh;
        _settings.PowerUsageBaselines = state.Baselines;

        if (!force && now - _lastPowerSavingsSaveUtc < TimeSpan.FromSeconds(30))
        {
            return;
        }

        SettingsStore.Save(_settings);
        _lastPowerSavingsSaveUtc = now;
    }

    private void RefreshPowerTelemetryUi()
    {
        if (_powerSavingsSnapshot is not { } snapshot)
        {
            return;
        }

        var highIsCurrent = snapshot.Mode == LaptopPowerMode.HighPower;
        var lowIsCurrent = snapshot.Mode == LaptopPowerMode.LowPower;
        _highUsageMenuItem.Text = FormatUsage(
            "HIGH",
            snapshot.EstimatedHighUsageWatts,
            highIsCurrent,
            snapshot.HighBaselineAvailable,
            highIsCurrent ? snapshot.BaselineSamples : 0,
            snapshot.RequiredBaselineSamples);
        _lowUsageMenuItem.Text = FormatUsage(
            "LOW",
            snapshot.EstimatedLowUsageWatts,
            lowIsCurrent,
            snapshot.LowBaselineAvailable,
            lowIsCurrent ? snapshot.BaselineSamples : 0,
            snapshot.RequiredBaselineSamples);

        if (snapshot.Mode == LaptopPowerMode.LowPower && !snapshot.HighBaselineAvailable)
        {
            _savingRateMenuItem.Text = "Estimated saving now: learn HIGH for ~20 seconds first";
        }
        else
        {
            _savingRateMenuItem.Text = snapshot.EstimatedSavingWatts is { } savingWatts
                ? $"Estimated saving now: ~{savingWatts:0.0} W"
                : "Estimated saving now: learning comparison...";
        }

        _energySavedMenuItem.Text =
            $"Energy saved in ECO: {FormatEnergy(snapshot.EcoSessionEnergySavedWh)} session; " +
            $"{FormatEnergy(snapshot.TotalEnergySavedWh)} total";
        _measurementMenuItem.Text = $"Measurement: {snapshot.SourceDescription}";
        RefreshNotifyText();
    }

    private static string FormatUsage(
        string mode,
        double? watts,
        bool isCurrent,
        bool baselineAvailable,
        int baselineSamples,
        int requiredBaselineSamples)
    {
        if (watts is { } value)
        {
            return $"Estimated {mode} usage: ~{value:0.0} W ({(isCurrent ? "current" : "learned")})";
        }

        if (isCurrent)
        {
            return $"Estimated {mode} usage: warming up sensors...";
        }

        var collected = baselineAvailable ? requiredBaselineSamples : Math.Min(baselineSamples, requiredBaselineSamples);
        return $"Estimated {mode} usage: learning ({collected}/{requiredBaselineSamples} samples)";
    }

    private static string FormatEnergy(double wattHours)
    {
        return wattHours < 1
            ? $"{wattHours:0.000} Wh"
            : $"{wattHours:0.00} Wh";
    }

    private void RefreshNotifyText()
    {
        var highPower = _currentMode == LaptopPowerMode.HighPower;
        var mode = highPower ? "HIGH" : "LOW";
        var currentUsage = _powerSavingsSnapshot?.CurrentUsageWatts is { } watts
            ? $" ~{watts:0} W"
            : string.Empty;
        var saved = !highPower && _powerSavingsSnapshot is { } snapshot
            ? $" | {FormatEnergy(snapshot.EcoSessionEnergySavedWh)} saved"
            : string.Empty;
        var action = highPower ? "click for LOW" : "click for HIGH";
        var text = $"{AppIdentity.DisplayName}: {mode}{currentUsage}{saved} ({action})";
        _notifyIcon.Text = Shorten(text, 127);
    }

    private static string Shorten(string value, int maximumLength)
    {
        return value.Length <= maximumLength ? value : value[..maximumLength];
    }

    private void TogglePowerMode()
    {
        if (!_modeChangeRunning)
        {
            RefreshUi(readHardwareState: true);
        }

        var target = _currentMode == LaptopPowerMode.HighPower
            ? LaptopPowerMode.LowPower
            : LaptopPowerMode.HighPower;
        RequestPowerMode(target, "Manual switch");
    }

    private void ToggleAutoSwitch()
    {
        _settings.AutoSwitchWhenPluggedIn = _autoSwitchMenuItem.Checked;
        SettingsStore.Save(_settings);

        if (_settings.AutoSwitchWhenPluggedIn)
        {
            ShowInfo("Automatic switching enabled: High on AC, Low on battery.");
            CheckForPowerSourceChange(forceApply: true);
        }
        else
        {
            ShowInfo("Automatic power-source switching disabled.");
        }

        RefreshUi(readHardwareState: false);
    }

    private void ToggleStartup()
    {
        try
        {
            StartupService.SetRunAtStartup(_startupMenuItem.Checked);
            _settings.StartupPreferenceInitialized = true;
            SettingsStore.Save(_settings);
            _startupMenuItem.Checked = StartupService.IsRunAtStartupEnabled();
            ShowInfo(_startupMenuItem.Checked
                ? $"{AppIdentity.DisplayName} will start when you sign in."
                : $"{AppIdentity.DisplayName} will no longer start when you sign in.");
        }
        catch (Exception ex)
        {
            _startupMenuItem.Checked = StartupService.IsRunAtStartupEnabled();
            ShowError($"Could not update Windows startup: {ex.Message}");
        }
    }

    private void ConfigureStartupOnFirstLaunch()
    {
        if (_settings.StartupPreferenceInitialized)
        {
            _startupMenuItem.Checked = StartupService.IsRunAtStartupEnabled();
            return;
        }

        try
        {
            StartupService.SetRunAtStartup(enable: true);
            _settings.StartupPreferenceInitialized = true;
            SettingsStore.Save(_settings);
        }
        catch (Exception ex)
        {
            ShowError($"Could not configure Windows startup: {ex.Message}");
        }

        _startupMenuItem.Checked = StartupService.IsRunAtStartupEnabled();
    }

    private void SystemEventsOnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode is not (PowerModes.StatusChange or PowerModes.Resume))
        {
            return;
        }

        _uiContext.Post(_ => CheckForPowerSourceChange(forceApply: e.Mode == PowerModes.Resume), null);
    }

    private void CheckForPowerSourceChange(bool forceApply)
    {
        var powerSource = SystemInformation.PowerStatus.PowerLineStatus;
        var sourceChanged = powerSource != PowerLineStatus.Unknown && powerSource != _lastPowerSource;
        if (powerSource != PowerLineStatus.Unknown)
        {
            _lastPowerSource = powerSource;
        }

        if (!_settings.AutoSwitchWhenPluggedIn || (!forceApply && !sourceChanged))
        {
            return;
        }

        var target = powerSource switch
        {
            PowerLineStatus.Online => LaptopPowerMode.HighPower,
            PowerLineStatus.Offline => LaptopPowerMode.LowPower,
            _ => (LaptopPowerMode?)null
        };

        if (target is null || (!_modeChangeRunning && target == _currentMode))
        {
            return;
        }

        RequestPowerMode(target.Value, powerSource == PowerLineStatus.Online
            ? "Plugged in"
            : "Running on battery");
    }

    private void RequestPowerMode(LaptopPowerMode targetMode, string reason)
    {
        _queuedMode = targetMode;
        _queuedReason = reason;
        if (!_modeChangeRunning)
        {
            _ = ProcessPowerModeQueueAsync();
        }
    }

    private async Task ProcessPowerModeQueueAsync()
    {
        _modeChangeRunning = true;
        try
        {
            while (_queuedMode is { } targetMode)
            {
                var reason = _queuedReason;
                _queuedMode = null;
                ShowSwitchingState(targetMode);

                try
                {
                    var result = await _powerProfileBroker.ApplyAsync(targetMode);
                    var detectedMode = result.State.DetectedMode;
                    if (detectedMode is not null)
                    {
                        SetCurrentMode(detectedMode.Value);
                    }
                    else if (result.Success)
                    {
                        SetCurrentMode(targetMode);
                    }

                    if (result.Success)
                    {
                        ShowInfo(targetMode == LaptopPowerMode.HighPower
                            ? $"{reason}: High power enabled ({AppIdentity.HighProfileDescription})."
                            : $"{reason}: Low power enabled ({AppIdentity.LowProfileDescription})." );
                    }
                    else
                    {
                        ShowError($"Power mode only partially changed: {string.Join("; ", result.Errors)}");
                    }
                }
                catch (Exception ex)
                {
                    ShowError($"Power mode switch failed: {ex.Message}");
                }
            }
        }
        finally
        {
            _modeChangeRunning = false;
            StopSwitchingAnimation();
            RefreshUi(readHardwareState: true);
        }
    }

    private void SetCurrentMode(LaptopPowerMode mode)
    {
        _currentMode = mode;
        _settings.LastHighPowerMode = mode == LaptopPowerMode.HighPower;
        SettingsStore.Save(_settings);
        RefreshUi(readHardwareState: false);
    }

    private void ShowSwitchingState(LaptopPowerMode targetMode)
    {
        StartSwitchingAnimation(targetMode);
        _statusMenuItem.Text = targetMode == LaptopPowerMode.HighPower
            ? "Switching to HIGH power..."
            : "Switching to LOW power...";
        _notifyIcon.Text = targetMode == LaptopPowerMode.HighPower
            ? $"{AppIdentity.DisplayName}: switching to HIGH"
            : $"{AppIdentity.DisplayName}: switching to LOW";
    }

    private void StartSwitchingAnimation(LaptopPowerMode targetMode)
    {
        _activeSwitchingIcons = targetMode == LaptopPowerMode.HighPower
            ? _switchingToHighIcons
            : _switchingToLowIcons;
        _switchingIconFrame = 0;
        _notifyIcon.Icon = _activeSwitchingIcons[0];
        _switchingIconTimer.Start();
    }

    private void AdvanceSwitchingIcon()
    {
        if (_activeSwitchingIcons is not { Length: > 0 } icons)
        {
            _switchingIconTimer.Stop();
            return;
        }

        _switchingIconFrame = (_switchingIconFrame + 1) % icons.Length;
        _notifyIcon.Icon = icons[_switchingIconFrame];
    }

    private void StopSwitchingAnimation()
    {
        _switchingIconTimer.Stop();
        _activeSwitchingIcons = null;
        _switchingIconFrame = 0;
    }

    private void RefreshUi(bool readHardwareState)
    {
        if (readHardwareState && !_modeChangeRunning)
        {
            var detectedMode = PowerProfileService.ReadState().DetectedMode;
            if (detectedMode is not null)
            {
                _currentMode = detectedMode.Value;
                _settings.LastHighPowerMode = detectedMode == LaptopPowerMode.HighPower;
            }
        }

        var highPower = _currentMode == LaptopPowerMode.HighPower;
        _statusMenuItem.Text = highPower ? "HIGH POWER - ACTIVE" : "LOW POWER - ACTIVE";
        _autoSwitchMenuItem.Checked = _settings.AutoSwitchWhenPluggedIn;
        _startupMenuItem.Checked = StartupService.IsRunAtStartupEnabled();
        if (!_modeChangeRunning)
        {
            _notifyIcon.Icon = highPower ? _highPowerIcon : _lowPowerIcon;
        }
        RefreshNotifyText();
    }

    private void ShowInfo(string message)
    {
        _notifyIcon.ShowBalloonTip(1800, AppIdentity.DisplayName, message, ToolTipIcon.Info);
    }

    private void ShowError(string message)
    {
        _notifyIcon.ShowBalloonTip(3500, AppIdentity.DisplayName, message, ToolTipIcon.Error);
    }
}

internal static class StartupService
{
    private const string RunRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = AppIdentity.DisplayName;
    private const string LegacyDesktopAppName = "PowerModeToggleDesktop";

    public static void MigrateExistingEntry()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunRegistryPath);
        var legacyValue = key.GetValue(LegacyDesktopAppName) as string;
        var unifiedValue = key.GetValue(AppName) as string;
        if (string.IsNullOrWhiteSpace(legacyValue) && string.IsNullOrWhiteSpace(unifiedValue))
        {
            return;
        }

        key.SetValue(AppName, $"\"{Application.ExecutablePath}\"", RegistryValueKind.String);
        key.DeleteValue(LegacyDesktopAppName, throwOnMissingValue: false);
    }

    public static bool IsRunAtStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunRegistryPath, writable: false);
        var value = key?.GetValue(AppName) as string;
        return !string.IsNullOrWhiteSpace(value)
               && string.Equals(value.Trim().Trim('"'), Application.ExecutablePath, StringComparison.OrdinalIgnoreCase);
    }

    public static void SetRunAtStartup(bool enable)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunRegistryPath);
        if (enable)
        {
            key.SetValue(AppName, $"\"{Application.ExecutablePath}\"", RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(AppName, throwOnMissingValue: false);
        }
    }
}

internal sealed class AppSettings
{
    public bool AutoSwitchWhenPluggedIn { get; set; }

    public bool StartupPreferenceInitialized { get; set; }

    public bool LastHighPowerMode { get; set; }

    public double TotalEstimatedEnergySavedWh { get; set; }

    public Dictionary<string, PowerUsageBaseline> PowerUsageBaselines { get; set; } = new(StringComparer.Ordinal);
}

internal static class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppIdentity.SettingsFolder);
    private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            return File.Exists(SettingsPath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings()
                : new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(SettingsDirectory);
        var temporaryPath = SettingsPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temporaryPath, SettingsPath, overwrite: true);
    }
}

internal static class TrayIconFactory
{
    private const int SwitchingFrameCount = 12;

    public static Icon CreateHighPowerIcon()
    {
        using var bitmap = CreateBase(Color.FromArgb(255, 133, 38));
        using var graphics = Graphics.FromImage(bitmap);
        ConfigureGraphics(graphics);

        using var glowBrush = new SolidBrush(Color.FromArgb(75, 255, 175, 44));
        using var boltBrush = new SolidBrush(Color.FromArgb(255, 216, 82));
        using var edgePen = new Pen(Color.FromArgb(255, 249, 205), 1.25f)
        {
            LineJoin = LineJoin.Round
        };
        graphics.FillEllipse(glowBrush, 7, 5, 20, 23);

        PointF[] bolt =
        [
            new(18.5f, 3.8f), new(8.5f, 17.3f), new(14.8f, 17.3f),
            new(11.9f, 28.2f), new(24.4f, 13.1f), new(17.7f, 13.1f)
        ];
        graphics.FillPolygon(boltBrush, bolt);
        graphics.DrawPolygon(edgePen, bolt);
        return CreateIcon(bitmap);
    }

    public static Icon CreateLowPowerIcon()
    {
        using var bitmap = CreateBase(Color.FromArgb(48, 211, 153));
        using var graphics = Graphics.FromImage(bitmap);
        ConfigureGraphics(graphics);

        using var leafPath = new GraphicsPath();
        leafPath.StartFigure();
        leafPath.AddBezier(new PointF(5.5f, 20.5f), new PointF(8f, 8f), new PointF(20f, 5f), new PointF(27f, 7f));
        leafPath.AddBezier(new PointF(27f, 7f), new PointF(25f, 20f), new PointF(17f, 27f), new PointF(7f, 25f));
        leafPath.AddBezier(new PointF(7f, 25f), new PointF(5f, 24f), new PointF(4.8f, 22f), new PointF(5.5f, 20.5f));
        leafPath.CloseFigure();

        using var leafBrush = new SolidBrush(Color.FromArgb(61, 222, 164));
        using var leafEdgePen = new Pen(Color.FromArgb(184, 255, 226), 1.1f);
        using var veinPen = new Pen(Color.White, 1.8f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        graphics.FillPath(leafBrush, leafPath);
        graphics.DrawPath(leafEdgePen, leafPath);
        graphics.DrawBezier(veinPen, new PointF(7.5f, 24f), new PointF(13f, 17f), new PointF(19f, 12f), new PointF(24.5f, 8.5f));
        graphics.DrawLine(veinPen, 13.2f, 17.6f, 12.1f, 12.5f);
        graphics.DrawLine(veinPen, 17.6f, 13.8f, 22.2f, 14.2f);
        return CreateIcon(bitmap);
    }

    public static Icon[] CreateSwitchingIcons(Color accentColor)
    {
        var icons = new Icon[SwitchingFrameCount];
        for (var frame = 0; frame < SwitchingFrameCount; frame++)
        {
            using var bitmap = new Bitmap(32, 32);
            using var graphics = Graphics.FromImage(bitmap);
            ConfigureGraphics(graphics);
            graphics.Clear(Color.Transparent);

            using var backgroundBrush = new SolidBrush(Color.FromArgb(24, 35, 51));
            using var trackPen = new Pen(Color.FromArgb(75, accentColor), 2.2f);
            graphics.FillEllipse(backgroundBrush, 1.5f, 1.5f, 29f, 29f);
            graphics.DrawEllipse(trackPen, 3.2f, 3.2f, 25.6f, 25.6f);

            for (var tail = 7; tail >= 0; tail--)
            {
                var pointIndex = (frame - tail + SwitchingFrameCount) % SwitchingFrameCount;
                var angle = (Math.PI * 2 * pointIndex / SwitchingFrameCount) - (Math.PI / 2);
                var centerX = 16f + (float)Math.Cos(angle) * 12.8f;
                var centerY = 16f + (float)Math.Sin(angle) * 12.8f;
                var strength = 1f - (tail / 8f);
                var alpha = (int)(45 + (210 * strength));
                var size = 2.2f + (2.8f * strength);
                var color = tail == 0
                    ? Color.White
                    : Color.FromArgb(alpha, accentColor);
                using var beadBrush = new SolidBrush(color);
                graphics.FillEllipse(beadBrush, centerX - (size / 2), centerY - (size / 2), size, size);
            }

            using var arrowsPen = new Pen(Color.FromArgb(235, 244, 250), 1.65f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };
            graphics.DrawLine(arrowsPen, 10f, 12.5f, 21.5f, 12.5f);
            graphics.DrawLine(arrowsPen, 18.5f, 9.5f, 21.5f, 12.5f);
            graphics.DrawLine(arrowsPen, 18.5f, 15.5f, 21.5f, 12.5f);
            graphics.DrawLine(arrowsPen, 21.5f, 19.5f, 10f, 19.5f);
            graphics.DrawLine(arrowsPen, 13f, 16.5f, 10f, 19.5f);
            graphics.DrawLine(arrowsPen, 13f, 22.5f, 10f, 19.5f);

            icons[frame] = CreateIcon(bitmap);
        }

        return icons;
    }

    public static void RenderPreview(string outputPath)
    {
        using var highIcon = CreateHighPowerIcon();
        using var lowIcon = CreateLowPowerIcon();
        using var preview = new Bitmap(256, 112);
        using var graphics = Graphics.FromImage(preview);
        graphics.Clear(Color.FromArgb(16, 23, 34));
        graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        graphics.PixelOffsetMode = PixelOffsetMode.Half;
        graphics.DrawIcon(highIcon, new Rectangle(28, 8, 64, 64));
        graphics.DrawIcon(lowIcon, new Rectangle(164, 8, 64, 64));
        using var font = new Font("Segoe UI", 11f, FontStyle.Bold);
        using var highBrush = new SolidBrush(Color.FromArgb(255, 179, 96));
        using var lowBrush = new SolidBrush(Color.FromArgb(101, 230, 184));
        graphics.DrawString("HIGH", font, highBrush, 36, 80);
        graphics.DrawString("LOW", font, lowBrush, 178, 80);
        preview.Save(outputPath, System.Drawing.Imaging.ImageFormat.Png);
    }

    private static Bitmap CreateBase(Color accentColor)
    {
        var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        ConfigureGraphics(graphics);
        graphics.Clear(Color.Transparent);
        using var backgroundBrush = new SolidBrush(Color.FromArgb(24, 35, 51));
        using var ringPen = new Pen(accentColor, 2.8f);
        graphics.FillEllipse(backgroundBrush, 2.5f, 2.5f, 27f, 27f);
        graphics.DrawEllipse(ringPen, 2.8f, 2.8f, 26.4f, 26.4f);
        return bitmap;
    }

    private static void ConfigureGraphics(Graphics graphics)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
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
