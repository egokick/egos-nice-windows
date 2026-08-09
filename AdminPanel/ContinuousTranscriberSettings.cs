using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AdminPanel;

internal sealed record ContinuousTranscriberLaunchSettings(
    string Microphone,
    bool KeepAudio,
    int ChunkSeconds,
    int Threads)
{
    public static ContinuousTranscriberLaunchSettings Defaults => new(
        Microphone: string.Empty,
        KeepAudio: true,
        ChunkSeconds: 15,
        Threads: Math.Max(1, Environment.ProcessorCount));

    public ContinuousTranscriberLaunchSettings Normalize()
    {
        return this with
        {
            Microphone = (Microphone ?? string.Empty).Trim(),
            ChunkSeconds = Math.Clamp(ChunkSeconds, 3, 3600),
            Threads = Math.Clamp(Threads, 1, 256)
        };
    }

    public IReadOnlyList<string> ToArguments()
    {
        var normalized = Normalize();
        var arguments = new List<string>
        {
            "--mode",
            normalized.KeepAudio ? "keep-audio" : "default",
            "--chunk-seconds",
            normalized.ChunkSeconds.ToString(CultureInfo.InvariantCulture),
            "--threads",
            normalized.Threads.ToString(CultureInfo.InvariantCulture)
        };
        if (!string.IsNullOrWhiteSpace(normalized.Microphone))
        {
            arguments.Add("--mic");
            arguments.Add(normalized.Microphone);
        }

        return arguments;
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
                    document.Microphone ?? string.Empty,
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
                Microphone = normalized.Microphone,
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

        public string? Microphone { get; set; }

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
    private readonly ComboBox _microphone;
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
        ClientSize = new Size(640, 460);
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
            Bounds = new Rectangle(24, 20, 592, 34),
            Font = new Font("Segoe UI", 16f, FontStyle.Bold, GraphicsUnit.Point),
            Text = "Launch settings",
            ForeColor = palette.Text,
            BackColor = Color.Transparent
        };
        var description = new Label
        {
            Bounds = new Rectangle(24, 60, 592, 44),
            Text = $"{app.Description} {(isRunning ? "Launching will restart it with these settings." : "These settings are saved and reused when you launch from the app card.")}",
            ForeColor = palette.SecondaryText,
            BackColor = Color.Transparent
        };

        _keepAudio = new CheckBox
        {
            Bounds = new Rectangle(24, 116, 592, 34),
            Checked = Settings.KeepAudio,
            Text = "Keep audio that produced a successful transcript",
            Font = new Font("Segoe UI", 10f, FontStyle.Bold, GraphicsUnit.Point),
            ForeColor = palette.Text,
            BackColor = Color.Transparent,
            AccessibleDescription = "When checked, successfully transcribed WAV files are retained. Silence and failed transcripts are still deleted."
        };
        var keepHint = new Label
        {
            Bounds = new Rectangle(48, 150, 568, 36),
            Text = "Silence, empty recognition results, and failed chunks are always deleted.",
            ForeColor = palette.SecondaryText,
            BackColor = Color.Transparent
        };

        var microphoneLabel = CreateFieldLabel("Recording microphone", 198, palette);
        _microphone = new ComboBox
        {
            Bounds = new Rectangle(248, 192, 368, 30),
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = palette.Surface,
            ForeColor = palette.Text,
            AccessibleName = "Recording microphone",
            AccessibleDescription = "Select the DirectShow microphone used for continuous transcription."
        };
        var microphones = ContinuousTranscriberMicrophoneService.GetAvailableMicrophones();
        var selectedMicrophone = string.IsNullOrWhiteSpace(Settings.Microphone)
            ? ContinuousTranscriberMicrophoneService.GetDefaultMicrophone()
            : Settings.Microphone;
        if (!string.IsNullOrWhiteSpace(selectedMicrophone)
            && !microphones.Contains(selectedMicrophone, StringComparer.OrdinalIgnoreCase))
        {
            microphones.Insert(0, selectedMicrophone);
        }
        _microphone.Items.AddRange(microphones.Cast<object>().ToArray());
        if (_microphone.Items.Count > 0)
        {
            var selectedIndex = microphones.FindIndex(name =>
                string.Equals(name, selectedMicrophone, StringComparison.OrdinalIgnoreCase));
            _microphone.SelectedIndex = selectedIndex >= 0 ? selectedIndex : 0;
        }

        var chunkLabel = CreateFieldLabel("Recording chunk length", 244, palette);
        _chunkSeconds = CreateNumberInput(
            Settings.ChunkSeconds,
            minimum: 3,
            maximum: 3600,
            top: 238,
            palette);
        var chunkUnit = CreateUnitLabel("seconds", 244, palette);

        var threadLabel = CreateFieldLabel("CPU transcription threads", 290, palette);
        _threads = CreateNumberInput(
            Settings.Threads,
            minimum: 1,
            maximum: 256,
            top: 284,
            palette);
        var threadUnit = CreateUnitLabel("threads", 290, palette);

        var divider = new Panel
        {
            Bounds = new Rectangle(24, 382, 592, 1),
            BackColor = palette.CardBorder
        };
        var cancelButton = CreateDialogButton(
            "Cancel",
            new Rectangle(420, 400, 104, 38),
            palette.MutedButton,
            palette.Text);
        cancelButton.DialogResult = DialogResult.Cancel;

        _startWithWindows = new CheckBox
        {
            Bounds = new Rectangle(24, 336, 592, 30),
            Checked = StartWithWindows,
            Text = "Start with Windows",
            Font = new Font("Segoe UI", 10f, FontStyle.Bold, GraphicsUnit.Point),
            ForeColor = palette.Text,
            BackColor = Color.Transparent,
            AccessibleDescription = "Launch this app automatically when you sign in to Windows."
        };

        var launchButton = CreateDialogButton(
            isRunning ? "Restart" : "Launch",
            new Rectangle(530, 400, 86, 38),
            palette.Accent,
            palette.AccentText);
        launchButton.AccessibleDescription = isRunning
            ? "Restart Continuous Transcriber with the selected settings."
            : "Launch Continuous Transcriber with the selected settings.";
        launchButton.Click += (_, _) =>
        {
            StartWithWindows = _startWithWindows.Checked;
            Settings = new ContinuousTranscriberLaunchSettings(
                    _microphone.SelectedItem as string ?? string.Empty,
                    _keepAudio.Checked,
                    Decimal.ToInt32(_chunkSeconds.Value),
                    Decimal.ToInt32(_threads.Value))
                .Normalize();
            DialogResult = DialogResult.OK;
            Close();
        };

        var dashboardButton = CreateDialogButton(
            "View dashboard",
            new Rectangle(24, 400, 144, 38),
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

        var runtimeLogsButton = CreateDialogButton(
            "View runtime logs",
            new Rectangle(176, 400, 184, 38),
            palette.AccentSoft,
            palette.Text);
        runtimeLogsButton.AccessibleDescription =
            "Show the current Continuous Transcriber monitor and worker log, including recording errors.";
        runtimeLogsButton.Click += (_, _) =>
        {
            using var logDialog = new ContinuousTranscriberRuntimeLogDialog(palette);
            logDialog.ShowDialog(this);
        };

        Controls.AddRange(
        [
            title,
            description,
            _keepAudio,
            keepHint,
            microphoneLabel,
            _microphone,
            chunkLabel,
            _chunkSeconds,
            chunkUnit,
            threadLabel,
            _threads,
            threadUnit,
            _startWithWindows,
            divider,
            dashboardButton,
            runtimeLogsButton,
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

internal static class ContinuousTranscriberRuntimeLog
{
    private const int MaximumDisplayedBytes = 512 * 1024;

    public static string GetPath()
    {
        return Path.Combine(
            NiceWindowsRepositoryLocator.GetRepositoryRoot(),
            "Continuous-transcriber",
            "transcription-errors.log");
    }

    public static string ReadLatest()
    {
        var path = GetPath();
        if (!File.Exists(path))
        {
            return "No runtime log has been written yet.";
        }

        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite);
            var skippedPrefix = string.Empty;
            if (stream.Length > MaximumDisplayedBytes)
            {
                stream.Seek(-MaximumDisplayedBytes, SeekOrigin.End);
                skippedPrefix =
                    $"Showing the latest {MaximumDisplayedBytes / 1024} KB of the runtime log.{Environment.NewLine}{Environment.NewLine}";
            }

            using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
            return skippedPrefix + reader.ReadToEnd();
        }
        catch (Exception exception) when (exception is IOException
                                             or UnauthorizedAccessException
                                             or DecoderFallbackException)
        {
            return $"Could not read the runtime log at '{path}'.{Environment.NewLine}{Environment.NewLine}{exception.Message}";
        }
    }
}

internal sealed class ContinuousTranscriberRuntimeLogDialog : Form
{
    private readonly TextBox _logText;

    public ContinuousTranscriberRuntimeLogDialog(AdminPalette palette)
    {
        Text = "Continuous Transcriber Runtime Logs";
        AccessibleName = Text;
        AccessibleDescription = "Displays the Continuous Transcriber monitor and worker log.";
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(780, 560);
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize = new Size(640, 440);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
        BackColor = palette.Window;
        ForeColor = palette.Text;

        var title = new Label
        {
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            Bounds = new Rectangle(20, 18, 740, 28),
            Font = new Font("Segoe UI", 14f, FontStyle.Bold, GraphicsUnit.Point),
            Text = "Runtime logs",
            ForeColor = palette.Text,
            BackColor = Color.Transparent
        };
        var path = new Label
        {
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            Bounds = new Rectangle(20, 50, 740, 34),
            Text = ContinuousTranscriberRuntimeLog.GetPath(),
            ForeColor = palette.SecondaryText,
            BackColor = Color.Transparent,
            AutoEllipsis = true
        };
        _logText = new TextBox
        {
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            Bounds = new Rectangle(20, 90, 740, 402),
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Font = new Font("Cascadia Mono", 9f, FontStyle.Regular, GraphicsUnit.Point),
            BackColor = palette.Surface,
            ForeColor = palette.Text,
            BorderStyle = BorderStyle.FixedSingle,
            AccessibleName = "Runtime log content"
        };
        var refresh = CreateButton(
            "Refresh",
            new Rectangle(536, 508, 106, 34),
            palette.AccentSoft,
            palette.Text);
        refresh.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        refresh.AccessibleDescription = "Reload the latest Continuous Transcriber runtime log entries.";
        refresh.Click += (_, _) => Reload();
        var close = CreateButton(
            "Close",
            new Rectangle(654, 508, 106, 34),
            palette.Accent,
            palette.AccentText);
        close.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        close.DialogResult = DialogResult.Cancel;

        Controls.AddRange([title, path, _logText, refresh, close]);
        CancelButton = close;
        Reload();
    }

    private void Reload()
    {
        _logText.Text = ContinuousTranscriberRuntimeLog.ReadLatest();
        _logText.SelectionStart = _logText.TextLength;
        _logText.ScrollToCaret();
    }

    private static Button CreateButton(string text, Rectangle bounds, Color background, Color foreground)
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
                var dashboardFolder = Path.Combine(
                    NiceWindowsRepositoryLocator.GetRepositoryRoot(),
                    "continuous-transcriber-dashboard");
                var launcherPath = Path.Combine(dashboardFolder, "start.bat");
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
                    WorkingDirectory = dashboardFolder,
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
