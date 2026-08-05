using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace AdminPanel;

internal sealed record ContinuousTranscriberLaunchSettings(
    bool KeepAudio,
    int ChunkSeconds,
    int Threads)
{
    public static ContinuousTranscriberLaunchSettings Defaults => new(
        KeepAudio: true,
        ChunkSeconds: 15,
        Threads: Math.Max(1, Environment.ProcessorCount));

    public ContinuousTranscriberLaunchSettings Normalize()
    {
        return this with
        {
            ChunkSeconds = Math.Clamp(ChunkSeconds, 3, 3600),
            Threads = Math.Clamp(Threads, 1, 256)
        };
    }

    public IReadOnlyList<string> ToArguments()
    {
        var normalized = Normalize();
        return
        [
            "--mode",
            normalized.KeepAudio ? "keep-audio" : "default",
            "--chunk-seconds",
            normalized.ChunkSeconds.ToString(CultureInfo.InvariantCulture),
            "--threads",
            normalized.Threads.ToString(CultureInfo.InvariantCulture)
        ];
    }
}

internal static class ContinuousTranscriberSettingsStore
{
    private const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LightDarkToggle");

    private static readonly string SettingsPath = Path.Combine(
        SettingsDirectory,
        "continuous-transcriber.json");

    public static ContinuousTranscriberLaunchSettings Load(out string warningMessage)
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                warningMessage = string.Empty;
                return ContinuousTranscriberLaunchSettings.Defaults;
            }

            var document = JsonSerializer.Deserialize<SettingsDocument>(
                File.ReadAllText(SettingsPath));
            if (document is null)
            {
                throw new JsonException("The settings document was empty.");
            }

            warningMessage = string.Empty;
            return new ContinuousTranscriberLaunchSettings(
                    document.KeepAudio,
                    document.ChunkSeconds,
                    document.Threads)
                .Normalize();
        }
        catch (Exception exception) when (exception is IOException
                                             or UnauthorizedAccessException
                                             or JsonException
                                             or NotSupportedException)
        {
            warningMessage =
                $"Continuous Transcriber settings could not be loaded; safe defaults will be used. {exception.Message}";
            return ContinuousTranscriberLaunchSettings.Defaults;
        }
    }

    public static bool TrySave(
        ContinuousTranscriberLaunchSettings settings,
        out string errorMessage)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var normalized = settings.Normalize();
        var temporaryPath = string.Empty;
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            temporaryPath = Path.Combine(
                SettingsDirectory,
                $"continuous-transcriber.{Guid.NewGuid():N}.tmp");
            var document = new SettingsDocument
            {
                SchemaVersion = CurrentSchemaVersion,
                KeepAudio = normalized.KeepAudio,
                ChunkSeconds = normalized.ChunkSeconds,
                Threads = normalized.Threads
            };
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(document, JsonOptions));
            File.Move(temporaryPath, SettingsPath, overwrite: true);
            errorMessage = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is IOException
                                             or UnauthorizedAccessException
                                             or System.Security.SecurityException)
        {
            errorMessage = $"Continuous Transcriber settings could not be saved: {exception.Message}";
            return false;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(temporaryPath))
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch
                {
                }
            }
        }
    }

    private sealed class SettingsDocument
    {
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        public bool KeepAudio { get; set; } = true;

        public int ChunkSeconds { get; set; } = 15;

        public int Threads { get; set; } = Math.Max(1, Environment.ProcessorCount);
    }
}

internal static class AdminAppLaunchSettings
{
    public static IReadOnlyList<string> GetArguments(AdminAppDefinition app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.HasLaunchSettings
            ? ContinuousTranscriberSettingsStore.Load(out _).ToArguments()
            : [];
    }
}

internal sealed class ContinuousTranscriberSettingsDialog : Form
{
    private readonly CheckBox _keepAudio;
    private readonly CheckBox _startWithWindows;
    private readonly NumericUpDown _chunkSeconds;
    private readonly NumericUpDown _threads;

    public ContinuousTranscriberSettingsDialog(
        AdminAppDefinition app,
        ContinuousTranscriberLaunchSettings settings,
        AdminPalette palette,
        bool isRunning,
        bool startWithWindows)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(settings);
        Settings = settings.Normalize();
        StartWithWindows = startWithWindows;

        Text = "Continuous Transcriber Settings";
        AccessibleName = Text;
        AccessibleDescription = "Choose how Continuous Transcriber records and processes microphone audio.";
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(520, 418);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
        BackColor = palette.Window;
        ForeColor = palette.Text;

        var title = new Label
        {
            Bounds = new Rectangle(24, 20, 472, 34),
            Font = new Font("Segoe UI", 16f, FontStyle.Bold, GraphicsUnit.Point),
            Text = "Launch settings",
            ForeColor = palette.Text,
            BackColor = Color.Transparent
        };
        var description = new Label
        {
            Bounds = new Rectangle(24, 60, 472, 44),
            Text = $"{app.Description} {(isRunning ? "Launching will restart it with these settings." : "These settings are saved and reused when you launch from the app card.")}",
            ForeColor = palette.SecondaryText,
            BackColor = Color.Transparent
        };

        _keepAudio = new CheckBox
        {
            Bounds = new Rectangle(24, 116, 472, 34),
            Checked = Settings.KeepAudio,
            Text = "Keep audio that produced a successful transcript",
            Font = new Font("Segoe UI", 10f, FontStyle.Bold, GraphicsUnit.Point),
            ForeColor = palette.Text,
            BackColor = Color.Transparent,
            AccessibleDescription = "When checked, successfully transcribed WAV files are retained. Silence and failed transcripts are still deleted."
        };
        var keepHint = new Label
        {
            Bounds = new Rectangle(48, 150, 448, 36),
            Text = "Silence, empty recognition results, and failed chunks are always deleted.",
            ForeColor = palette.SecondaryText,
            BackColor = Color.Transparent
        };

        var chunkLabel = CreateFieldLabel("Recording chunk length", 198, palette);
        _chunkSeconds = CreateNumberInput(
            Settings.ChunkSeconds,
            minimum: 3,
            maximum: 3600,
            top: 192,
            palette);
        var chunkUnit = CreateUnitLabel("seconds", 198, palette);

        var threadLabel = CreateFieldLabel("CPU transcription threads", 244, palette);
        _threads = CreateNumberInput(
            Settings.Threads,
            minimum: 1,
            maximum: 256,
            top: 238,
            palette);
        var threadUnit = CreateUnitLabel("threads", 244, palette);

        var divider = new Panel
        {
            Bounds = new Rectangle(24, 340, 472, 1),
            BackColor = palette.CardBorder
        };
        var cancelButton = CreateDialogButton(
            "Cancel",
            new Rectangle(274, 360, 104, 38),
            palette.MutedButton,
            palette.Text);
        cancelButton.DialogResult = DialogResult.Cancel;

        _startWithWindows = new CheckBox
        {
            Bounds = new Rectangle(24, 294, 472, 30),
            Checked = StartWithWindows,
            Text = "Start with Windows",
            Font = new Font("Segoe UI", 10f, FontStyle.Bold, GraphicsUnit.Point),
            ForeColor = palette.Text,
            BackColor = Color.Transparent,
            AccessibleDescription = "Launch this app automatically when you sign in to Windows."
        };

        var launchButton = CreateDialogButton(
            isRunning ? "Restart" : "Launch",
            new Rectangle(388, 360, 108, 38),
            palette.Accent,
            palette.AccentText);
        launchButton.AccessibleDescription = isRunning
            ? "Restart Continuous Transcriber with the selected settings."
            : "Launch Continuous Transcriber with the selected settings.";
        launchButton.Click += (_, _) =>
        {
            StartWithWindows = _startWithWindows.Checked;
            Settings = new ContinuousTranscriberLaunchSettings(
                    _keepAudio.Checked,
                    Decimal.ToInt32(_chunkSeconds.Value),
                    Decimal.ToInt32(_threads.Value))
                .Normalize();
            DialogResult = DialogResult.OK;
            Close();
        };

        var dashboardButton = CreateDialogButton(
            "View dashboard",
            new Rectangle(24, 360, 144, 38),
            palette.AccentSoft,
            palette.Text);
        dashboardButton.AccessibleDescription =
            "Start the local Continuous Transcriber dashboard in the notification area and open it in your browser.";
        dashboardButton.Click += async (_, _) =>
        {
            dashboardButton.Enabled = false;
            dashboardButton.Text = "Opening…";
            var result = await ContinuousTranscriberDashboardLauncher.LaunchAsync();
            if (!result.Success)
            {
                MessageBox.Show(
                    this,
                    result.ErrorMessage,
                    "Could not open dashboard",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }

            if (!IsDisposed)
            {
                dashboardButton.Enabled = true;
                dashboardButton.Text = "View dashboard";
            }
        };

        Controls.AddRange(
        [
            title,
            description,
            _keepAudio,
            keepHint,
            chunkLabel,
            _chunkSeconds,
            chunkUnit,
            threadLabel,
            _threads,
            threadUnit,
            _startWithWindows,
            divider,
            dashboardButton,
            cancelButton,
            launchButton
        ]);
        AcceptButton = launchButton;
        CancelButton = cancelButton;
    }

    public ContinuousTranscriberLaunchSettings Settings { get; private set; }

    public bool StartWithWindows { get; private set; }

    private static Label CreateFieldLabel(string text, int top, AdminPalette palette)
    {
        return new Label
        {
            Bounds = new Rectangle(24, top, 260, 28),
            Text = text,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = palette.Text,
            BackColor = Color.Transparent
        };
    }

    private static Label CreateUnitLabel(string text, int top, AdminPalette palette)
    {
        return new Label
        {
            Bounds = new Rectangle(424, top, 72, 28),
            Text = text,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = palette.SecondaryText,
            BackColor = Color.Transparent
        };
    }

    private static NumericUpDown CreateNumberInput(
        int value,
        int minimum,
        int maximum,
        int top,
        AdminPalette palette)
    {
        return new NumericUpDown
        {
            Bounds = new Rectangle(318, top, 96, 30),
            Minimum = minimum,
            Maximum = maximum,
            Value = Math.Clamp(value, minimum, maximum),
            TextAlign = HorizontalAlignment.Right,
            BackColor = palette.Surface,
            ForeColor = palette.Text,
            BorderStyle = BorderStyle.FixedSingle
        };
    }

    private static Button CreateDialogButton(
        string text,
        Rectangle bounds,
        Color background,
        Color foreground)
    {
        var button = new Button
        {
            Bounds = bounds,
            Text = text,
            FlatStyle = FlatStyle.Flat,
            BackColor = background,
            ForeColor = foreground,
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false
        };
        button.FlatAppearance.BorderSize = 0;
        return button;
    }
}

internal static class ContinuousTranscriberDashboardLauncher
{
    internal readonly record struct LaunchResult(bool Success, string ErrorMessage);

    public static Task<LaunchResult> LaunchAsync()
    {
        return Task.Run(() =>
        {
            try
            {
                var recorderFolder = Path.Combine(
                    NiceWindowsRepositoryLocator.GetRepositoryRoot(),
                    "Continuous-transcriber");
                var launcherPath = Path.Combine(recorderFolder, "start-dashboard.bat");
                if (!File.Exists(launcherPath))
                {
                    return new LaunchResult(false, $"Dashboard launcher not found: {launcherPath}");
                }

                var commandProcessor = Environment.GetEnvironmentVariable("ComSpec");
                if (string.IsNullOrWhiteSpace(commandProcessor) || !File.Exists(commandProcessor))
                {
                    commandProcessor = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.System),
                        "cmd.exe");
                }

                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = commandProcessor,
                    Arguments = $"/d /c call \"{launcherPath}\"",
                    WorkingDirectory = recorderFolder,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                });
                if (process is null)
                {
                    return new LaunchResult(false, "Windows did not start the dashboard launcher.");
                }

                var standardErrorTask = process.StandardError.ReadToEndAsync();
                var standardOutputTask = process.StandardOutput.ReadToEndAsync();
                process.WaitForExit();
                var standardError = standardErrorTask.GetAwaiter().GetResult().Trim();
                var standardOutput = standardOutputTask.GetAwaiter().GetResult().Trim();
                if (process.ExitCode == 0)
                {
                    return new LaunchResult(true, string.Empty);
                }

                var details = string.IsNullOrWhiteSpace(standardError) ? standardOutput : standardError;
                return new LaunchResult(
                    false,
                    string.IsNullOrWhiteSpace(details)
                        ? $"Dashboard launcher exited with code {process.ExitCode}."
                        : details[^Math.Min(details.Length, 1200)..]);
            }
            catch (Exception exception) when (exception is IOException
                                                 or UnauthorizedAccessException
                                                 or InvalidOperationException
                                                 or System.ComponentModel.Win32Exception)
            {
                return new LaunchResult(false, exception.Message);
            }
        });
    }
}
internal sealed class AdminAppSettingsDialog : Form
{
    private readonly CheckBox _startWithWindows;

    public AdminAppSettingsDialog(AdminAppDefinition app, AdminPalette palette, bool startWithWindows)
    {
        ArgumentNullException.ThrowIfNull(app);
        Text = $"{app.DisplayName} Settings";
        AccessibleName = Text;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(520, 270);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9f);
        BackColor = palette.Window;
        ForeColor = palette.Text;

        var title = new Label { Bounds = new Rectangle(24, 20, 472, 34), Font = new Font("Segoe UI", 16f, FontStyle.Bold), Text = "Settings", ForeColor = palette.Text, BackColor = Color.Transparent };
        var description = new Label { Bounds = new Rectangle(24, 66, 472, 64), Text = app.Description, ForeColor = palette.SecondaryText, BackColor = Color.Transparent };
        _startWithWindows = new CheckBox { Bounds = new Rectangle(24, 148, 472, 32), Text = "Start with Windows", Checked = startWithWindows, Font = new Font("Segoe UI", 10f, FontStyle.Bold), ForeColor = palette.Text, BackColor = Color.Transparent, AccessibleDescription = "Launch this app automatically when you sign in to Windows." };
        var cancel = CreateButton("Cancel", new Rectangle(274, 204, 104, 38), palette.MutedButton, palette.Text);
        cancel.DialogResult = DialogResult.Cancel;
        var save = CreateButton("Save", new Rectangle(388, 204, 108, 38), palette.Accent, palette.AccentText);
        save.Click += (_, _) => { StartWithWindows = _startWithWindows.Checked; DialogResult = DialogResult.OK; Close(); };
        Controls.AddRange([title, description, _startWithWindows, cancel, save]);
        AcceptButton = save;
        CancelButton = cancel;
    }

    public bool StartWithWindows { get; private set; }

    private static Button CreateButton(string text, Rectangle bounds, Color background, Color foreground) => new()
    {
        Bounds = bounds,
        Text = text,
        FlatStyle = FlatStyle.Flat,
        BackColor = background,
        ForeColor = foreground,
        Cursor = Cursors.Hand,
        UseVisualStyleBackColor = false
    };
}
