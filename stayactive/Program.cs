using System.Diagnostics;
using System.Drawing.Drawing2D;
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
    private readonly ToolStripMenuItem _editTextMenuItem;
    private readonly Icon _activeIcon;
    private readonly Icon _inactiveIcon;
    private readonly SynchronizationContext _uiContext;

    private AppSettings _settings;
    private CancellationTokenSource? _runnerCancellation;
    private Task? _runnerTask;

    public TrayApplicationContext()
    {
        _uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _settings = SettingsStore.Load();
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

        _editTextMenuItem = new ToolStripMenuItem("Edit text file");
        _editTextMenuItem.Click += (_, _) => OpenTextFile();

        var exitMenuItem = new ToolStripMenuItem("Exit");
        exitMenuItem.Click += (_, _) => ExitThread();

        var menu = new ContextMenuStrip();
        menu.Opening += (_, _) => RefreshUi();
        menu.Items.Add(_activeMenuItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_jiggleMouseMenuItem);
        menu.Items.Add(_typeTextMenuItem);
        menu.Items.Add(_startupMenuItem);
        menu.Items.Add(_editTextMenuItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitMenuItem);

        _notifyIcon = new NotifyIcon
        {
            ContextMenuStrip = menu,
            Icon = _settings.IsActive ? _activeIcon : _inactiveIcon,
            Visible = true
        };
        _notifyIcon.MouseClick += NotifyIconOnMouseClick;

        RefreshUi();
        EnsureStartupPreference();
        ApplyRunnerState();
    }

    protected override void ExitThreadCore()
    {
        StopRunner();
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
            SetActive(!_settings.IsActive, showBalloon: true);
        }
    }

    private void SetActive(bool isActive, bool showBalloon)
    {
        _settings.IsActive = isActive;
        SettingsStore.Save(_settings);
        RefreshUi();
        ApplyRunnerState();

        if (showBalloon)
        {
            ShowInfoBalloon(isActive ? "StayActive enabled." : "StayActive disabled.");
        }
    }

    private void ToggleJiggleMouse()
    {
        _settings.JiggleMouseEnabled = _jiggleMouseMenuItem.Checked;
        SettingsStore.Save(_settings);
        RefreshUi();
        ApplyRunnerState();
    }

    private void ToggleTypeText()
    {
        _settings.TypeTextEnabled = _typeTextMenuItem.Checked;
        SettingsStore.Save(_settings);
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

    private void ApplyRunnerState()
    {
        if (_settings.IsActive && (_settings.JiggleMouseEnabled || _settings.TypeTextEnabled))
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
            var settings = SettingsStore.Load();
            _settings = settings;

            try
            {
                if (settings.JiggleMouseEnabled)
                {
                    InputSimulator.JiggleMouse();
                }

                if (settings.TypeTextEnabled)
                {
                    var text = SettingsStore.ReadTypingText();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        await TypeTextAsync(text, cancellationToken);
                        await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                        continue;
                    }
                }

                await Task.Delay(ActivityProfile.GetIdleDelay(), cancellationToken);
            }
            catch (OperationCanceledException)
            {
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
        _activeMenuItem.Checked = _settings.IsActive;
        _jiggleMouseMenuItem.Checked = _settings.JiggleMouseEnabled;
        _typeTextMenuItem.Checked = _settings.TypeTextEnabled;
        _startupMenuItem.Checked = StartupService.IsRunAtStartupEnabled();

        _notifyIcon.Icon = _settings.IsActive ? _activeIcon : _inactiveIcon;
        _notifyIcon.Text = _settings.IsActive
            ? "StayActive: active"
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
}

internal sealed class AppSettings
{
    public bool StartupInitialized { get; set; }

    public bool IsActive { get; set; }

    public bool JiggleMouseEnabled { get; set; } = true;

    public bool TypeTextEnabled { get; set; }
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

    public static void JiggleMouse()
    {
        var before = Cursor.Position;
        var pattern = ActivityProfile.GetCirclePattern();

        for (var step = 0; step < pattern.Steps; step++)
        {
            var angle = (pattern.StartAngleDegrees + (step * 360.0 / pattern.Steps)) * Math.PI / 180.0;
            var wobble = Math.Sin(angle * 2.0) * 1.5;
            var radius = pattern.Radius + wobble;
            Cursor.Position = new Point(
                before.X + (int)Math.Round(Math.Cos(angle) * radius),
                before.Y + (int)Math.Round(Math.Sin(angle) * radius));
            Thread.Sleep(30);
        }

        Cursor.Position = before;
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
                        dwFlags = MouseeventfMove
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
                        wVk = virtualKey
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
                        dwFlags = KeyeventfKeyup
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
                        dwFlags = KeyeventfUnicode
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
                        dwFlags = KeyeventfUnicode | KeyeventfKeyup
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
