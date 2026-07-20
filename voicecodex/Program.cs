using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Speech.AudioFormat;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Speech.Synthesis;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using NAudio.Wave;
using Whisper.net;
using Whisper.net.Ggml;

namespace VoiceCodex;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length >= 1 && string.Equals(args[0], "--benchmark", StringComparison.OrdinalIgnoreCase))
        {
            BenchmarkRunner.Run(playAudio: args.Contains("--play", StringComparer.OrdinalIgnoreCase)).GetAwaiter().GetResult();
            return;
        }

        if (args.Length >= 2 && string.Equals(args[0], "--dispatch", StringComparison.OrdinalIgnoreCase))
        {
            var instruction = string.Join(" ", args.Skip(1));
            var decision = CommandGate.Evaluate(instruction);
            Console.WriteLine($"{decision.Kind}: {decision.Reason}");
            if (decision.Kind == CommandDecisionKind.LocalResponse)
            {
                Console.WriteLine(decision.LocalResponse);
            }
            else if (decision.Kind == CommandDecisionKind.Accept)
            {
                ExecuteAcceptedCommand(decision.CommandText);
            }

            return;
        }

        if (args.Length >= 2 && string.Equals(args[0], "--dispatch-many", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var instruction in string.Join(" ", args.Skip(1)).Split(";;", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var decision = CommandGate.Evaluate(instruction);
                Console.WriteLine($"{instruction} => {decision.Kind}: {decision.Reason}");
                if (decision.Kind == CommandDecisionKind.LocalResponse)
                {
                    Console.WriteLine(decision.LocalResponse);
                }
                else if (decision.Kind == CommandDecisionKind.Accept)
                {
                    ExecuteAcceptedCommand(decision.CommandText);
                }
            }

            return;
        }

        if (args.Length >= 2 && string.Equals(args[0], "--gate-many", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var instruction in string.Join(" ", args.Skip(1)).Split(";;", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var decision = CommandGate.Evaluate(instruction);
                Console.WriteLine($"{instruction} => {decision.Kind}: {decision.Reason} | {decision.CommandText}");
            }

            return;
        }

        if (args.Length >= 2 && string.Equals(args[0], "--target-terminal-parse-many", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var instruction in string.Join(" ", args.Skip(1)).Split(";;", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                Console.WriteLine($"{instruction} => {TargetedTerminalHandoff.ExplainDryRun(instruction)}");
            }

            return;
        }

        using var mutex = new Mutex(true, "VoiceCodex.Singleton", out var createdNew);
        if (!createdNew)
        {
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApplicationContext());
    }

    private static void ExecuteAcceptedCommand(string commandText)
    {
        if (!LocalCommandExecutor.TryExecute(commandText, out _))
        {
            CodexController.SendInstruction(commandText);
        }
    }
}

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _toggleMenuItem;
    private readonly ToolStripMenuItem _startupMenuItem;
    private readonly ToolStripMenuItem _voiceResponsesMenuItem;
    private readonly ToolStripMenuItem _tinyModelMenuItem;
    private readonly ToolStripMenuItem _baseModelMenuItem;
    private readonly ToolStripMenuItem _smallModelMenuItem;
    private readonly ToolStripMenuItem _statusMenuItem;
    private readonly ToolStripMenuItem _partialPhraseMenuItem;
    private readonly ToolStripMenuItem _lastPhraseMenuItem;
    private readonly ToolStripMenuItem _lastDispatchMenuItem;
    private readonly Icon _enabledIcon;
    private readonly Icon _workingIcon;
    private readonly Icon _disabledIcon;
    private readonly SpeechListener _speechListener;
    private readonly TargetedTerminalHandoff _targetedTerminalHandoff = new();
    private readonly VoiceFeedback _voiceFeedback = new();
    private readonly SynchronizationContext _uiContext;
    private AppSettings _settings;
    private bool _enabled;
    private bool _dispatching;
    private string _status = "Disabled";
    private string _partialPhrase = "No speech detected yet";
    private string _lastPhrase = "No phrase captured yet";
    private string _lastDispatch = "No command sent yet";
    private ActivityLogForm? _activityLogForm;

    public TrayApplicationContext()
    {
        _uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _settings = SettingsStore.Load();
        if (_settings.ApplyFastDefaults())
        {
            SettingsStore.Save(_settings);
        }

        _enabledIcon = TrayIconFactory.CreateMicrophoneIcon(Color.FromArgb(48, 188, 112));
        _workingIcon = TrayIconFactory.CreateMicrophoneIcon(Color.FromArgb(255, 184, 77));
        _disabledIcon = TrayIconFactory.CreateMicrophoneIcon(Color.FromArgb(138, 145, 154));

        _toggleMenuItem = new ToolStripMenuItem();
        _toggleMenuItem.Click += (_, _) => ToggleListening();

        _startupMenuItem = new ToolStripMenuItem("Run at Windows startup")
        {
            CheckOnClick = true
        };
        _startupMenuItem.Click += (_, _) => ToggleStartup();

        _voiceResponsesMenuItem = new ToolStripMenuItem("Voice responses")
        {
            CheckOnClick = true
        };
        _voiceResponsesMenuItem.Click += (_, _) => ToggleVoiceResponses();

        _tinyModelMenuItem = CreateModelMenuItem("Whisper tiny.en - fastest", WhisperModelSize.TinyEn);
        _baseModelMenuItem = CreateModelMenuItem("Whisper base.en - balanced", WhisperModelSize.BaseEn);
        _smallModelMenuItem = CreateModelMenuItem("Whisper small.en - more accurate", WhisperModelSize.SmallEn);

        _statusMenuItem = CreateDisabledMenuItem(_status);
        _partialPhraseMenuItem = CreateDisabledMenuItem(_partialPhrase);
        _lastPhraseMenuItem = CreateDisabledMenuItem(_lastPhrase);
        _lastDispatchMenuItem = CreateDisabledMenuItem(_lastDispatch);

        var activityLogMenuItem = new ToolStripMenuItem("Show activity log");
        activityLogMenuItem.Click += (_, _) => ShowActivityLog();

        var openLogsMenuItem = new ToolStripMenuItem("Open log folder");
        openLogsMenuItem.Click += (_, _) => OpenLogFolder();

        var testFeedbackMenuItem = new ToolStripMenuItem("Test feedback");
        testFeedbackMenuItem.Click += (_, _) =>
        {
            RecordActivity("Feedback test: tray notifications, menu status, and activity log are working.");
            ShowInfoBalloon("Feedback test: VoiceCodex is responding.");
        };

        var selfTestSwitchAppsMenuItem = new ToolStripMenuItem("Self-test: switch apps via Codex");
        selfTestSwitchAppsMenuItem.Click += (_, _) => DispatchPhrase("codex switch to the previous application using alt tab and then stop");

        var selfTestTerminalTabMenuItem = new ToolStripMenuItem("Self-test: switch terminal tab via Codex");
        selfTestTerminalTabMenuItem.Click += (_, _) => DispatchPhrase("codex switch to the next tab in the existing Windows Terminal window and then stop");

        var selfTestMultipleMenuItem = new ToolStripMenuItem("Self-test: multiple asks via Codex");
        selfTestMultipleMenuItem.Click += (_, _) =>
        {
            DispatchPhrase("this is a test one two");
            DispatchPhrase("can you hear me");
            DispatchPhrase("what can I say");
            DispatchPhrase("say I heard the VoiceCodex self test and then stop");
        };

        var startControllerMenuItem = new ToolStripMenuItem("Start controller Codex");
        startControllerMenuItem.Click += (_, _) => StartController();

        var exitMenuItem = new ToolStripMenuItem("Exit");
        exitMenuItem.Click += (_, _) => ExitThread();

        _notifyIcon = new NotifyIcon
        {
            Icon = _disabledIcon,
            Visible = true,
            Text = "VoiceCodex: disabled",
            ContextMenuStrip = new ContextMenuStrip()
        };
        _notifyIcon.ContextMenuStrip.Items.Add(_toggleMenuItem);
        _notifyIcon.ContextMenuStrip.Items.Add(_startupMenuItem);
        _notifyIcon.ContextMenuStrip.Items.Add(_voiceResponsesMenuItem);
        _notifyIcon.ContextMenuStrip.Items.Add(new ToolStripSeparator());
        _notifyIcon.ContextMenuStrip.Items.Add(_tinyModelMenuItem);
        _notifyIcon.ContextMenuStrip.Items.Add(_baseModelMenuItem);
        _notifyIcon.ContextMenuStrip.Items.Add(_smallModelMenuItem);
        _notifyIcon.ContextMenuStrip.Items.Add(new ToolStripSeparator());
        _notifyIcon.ContextMenuStrip.Items.Add(_statusMenuItem);
        _notifyIcon.ContextMenuStrip.Items.Add(_partialPhraseMenuItem);
        _notifyIcon.ContextMenuStrip.Items.Add(_lastPhraseMenuItem);
        _notifyIcon.ContextMenuStrip.Items.Add(_lastDispatchMenuItem);
        _notifyIcon.ContextMenuStrip.Items.Add(new ToolStripSeparator());
        _notifyIcon.ContextMenuStrip.Items.Add(activityLogMenuItem);
        _notifyIcon.ContextMenuStrip.Items.Add(openLogsMenuItem);
        _notifyIcon.ContextMenuStrip.Items.Add(startControllerMenuItem);
        _notifyIcon.ContextMenuStrip.Items.Add(testFeedbackMenuItem);
        _notifyIcon.ContextMenuStrip.Items.Add(selfTestSwitchAppsMenuItem);
        _notifyIcon.ContextMenuStrip.Items.Add(selfTestTerminalTabMenuItem);
        _notifyIcon.ContextMenuStrip.Items.Add(selfTestMultipleMenuItem);
        _notifyIcon.ContextMenuStrip.Items.Add(new ToolStripSeparator());
        _notifyIcon.ContextMenuStrip.Items.Add(exitMenuItem);
        _notifyIcon.ContextMenuStrip.Opening += (_, _) => RefreshMenu();
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                ToggleListening();
            }
        };

        _speechListener = new SpeechListener(() => _settings.WhisperModel);
        _speechListener.SpeechDetected += (_, _) => OnSpeechDetected();
        _speechListener.PartialPhraseRecognized += (_, phrase) => OnPartialPhrase(phrase);
        _speechListener.PhraseRejected += (_, message) => OnPhraseRejected(message);
        _speechListener.PhraseRecognized += (_, phrase) => DispatchPhrase(phrase);
        _speechListener.ListenerFaulted += (_, message) => OnListenerFaulted(message);

        RecordActivity("VoiceCodex started. Left-click the tray icon to enable listening.");
        Speak("VoiceCodex started.");
        RefreshMenu();
    }

    protected override void ExitThreadCore()
    {
        _speechListener.Dispose();
        _voiceFeedback.Dispose();
        _activityLogForm?.Close();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _enabledIcon.Dispose();
        _workingIcon.Dispose();
        _disabledIcon.Dispose();
        base.ExitThreadCore();
    }

    private void ToggleListening()
    {
        try
        {
            if (_enabled)
            {
                _speechListener.Stop();
                _enabled = false;
                _dispatching = false;
                _status = "Disabled";
                RecordActivity("Listening disabled.");
                RefreshMenu();
                ShowInfoBalloon("VoiceCodex is no longer listening.");
                Speak("Listening disabled.");
                return;
            }

            _speechListener.Start();
            _enabled = true;
            _status = "Listening";
            _partialPhrase = "Waiting for speech";
            RecordActivity("Listening enabled.");
            RefreshMenu();
            ShowInfoBalloon("VoiceCodex is listening.");
            Speak("Listening.");
        }
        catch (Exception ex)
        {
            _enabled = false;
            _dispatching = false;
            _status = "Listening failed";
            RecordActivity($"Listening failed: {ex.Message}");
            RefreshMenu();
            ShowErrorBalloon($"Listening failed: {ex.Message}");
            Speak("Listening failed.");
        }
    }

    private void DispatchPhrase(string phrase)
    {
        PostToUi(() =>
        {
            if (string.IsNullOrWhiteSpace(phrase))
            {
                return;
            }

            _lastPhrase = phrase.Trim();
            if (_targetedTerminalHandoff.TryHandle(_lastPhrase, out var terminalResult))
            {
                _partialPhrase = terminalResult.Message;
                _lastDispatch = terminalResult.DispatchStatus;
                _status = "Listening";
                _dispatching = false;
                RecordActivity(terminalResult.Activity);
                RefreshMenu();
                if (terminalResult.IsError)
                {
                    ShowErrorBalloon(terminalResult.Message);
                }
                else if (terminalResult.ShowBalloon)
                {
                    ShowInfoBalloon(terminalResult.Message);
                }

                Speak(terminalResult.SpokenText);
                return;
            }

            var decision = CommandGate.Evaluate(_lastPhrase);
            if (decision.Kind == CommandDecisionKind.LocalResponse)
            {
                _partialPhrase = decision.LocalResponse;
                _lastDispatch = $"Answered locally {DateTime.Now:T}";
                _status = "Listening";
                _dispatching = false;
                RecordActivity($"Answered locally: {_lastPhrase}. Response: {decision.LocalResponse}");
                RefreshMenu();
                Speak(decision.LocalResponse);
                return;
            }

            if (decision.Kind == CommandDecisionKind.Ignore)
            {
                _partialPhrase = $"No action: {decision.Reason}";
                _lastDispatch = $"Ignored {DateTime.Now:T}";
                _status = "Listening";
                _dispatching = false;
                RecordActivity($"No action for transcript: {_lastPhrase}. Reason: {decision.Reason}");
                RefreshMenu();
                return;
            }

            _partialPhrase = "Accepted command";
            _status = "Sending to controller Codex";
            _dispatching = true;
            RecordActivity($"Accepted command: {decision.CommandText}");
            RefreshMenu();

            try
            {
                if (LocalCommandExecutor.TryExecute(decision.CommandText, out var localResult))
                {
                    _lastDispatch = $"Handled locally {DateTime.Now:T}";
                    RecordActivity($"Handled locally: {localResult}");
                    ShowInfoBalloon(localResult);
                    Speak("Done.");
                }
                else
                {
                    Speak($"Command: {decision.CommandText}");
                    CodexController.SendInstruction(decision.CommandText);
                    _lastDispatch = $"Sent to controller {DateTime.Now:T}";
                    RecordActivity("Sent command to persistent VoiceCodex Controller.");
                    ShowInfoBalloon($"Sent to controller: {Truncate(decision.CommandText, 90)}");
                    Speak("Sent to controller.");
                }

                _status = "Listening";
                _dispatching = false;
                RefreshMenu();
            }
            catch (Exception ex)
            {
                _lastDispatch = "Controller send failed";
                _status = "Listening";
                _dispatching = false;
                RecordActivity($"Controller send failed: {ex.Message}");
                RefreshMenu();
                ShowErrorBalloon($"Controller send failed: {ex.Message}");
                Speak("Controller send failed.");
            }
        });
    }

    private void OnSpeechDetected()
    {
        PostToUi(() =>
        {
            if (!_enabled)
            {
                return;
            }

            _status = "Speech detected";
            _partialPhrase = "Listening to phrase...";
            RecordActivity("Speech detected.");
            RefreshMenu();
        });
    }

    private void OnPartialPhrase(string phrase)
    {
        PostToUi(() =>
        {
            if (!_enabled)
            {
                return;
            }

            _status = "Hearing speech";
            _partialPhrase = phrase;
            RecordActivity($"Partial: {phrase}");
            RefreshMenu();
        });
    }

    private void OnPhraseRejected(string message)
    {
        PostToUi(() =>
        {
            if (!_enabled)
            {
                return;
            }

            _status = "Listening";
            _partialPhrase = message;
            RecordActivity(message);
            RefreshMenu();
        });
    }

    private void OnListenerFaulted(string message)
    {
        PostToUi(() =>
        {
            _status = "Recognizer error";
            RecordActivity($"Recognizer error: {message}");
            RefreshMenu();
            ShowErrorBalloon(message);
            Speak("Transcription error.");
        });
    }

    private void ToggleStartup()
    {
        try
        {
            StartupService.SetRunAtStartup(_startupMenuItem.Checked);
            RecordActivity(_startupMenuItem.Checked ? "Startup enabled." : "Startup disabled.");
            RefreshMenu();
            Speak(_startupMenuItem.Checked ? "Startup enabled." : "Startup disabled.");
        }
        catch (Exception ex)
        {
            _startupMenuItem.Checked = StartupService.IsRunAtStartupEnabled();
            ShowErrorBalloon($"Startup update failed: {ex.Message}");
        }
    }

    private void RefreshMenu()
    {
        _toggleMenuItem.Text = _enabled ? "Disable listening" : "Enable listening";
        _startupMenuItem.Checked = StartupService.IsRunAtStartupEnabled();
        _voiceResponsesMenuItem.Checked = _settings.VoiceResponsesEnabled;
        _tinyModelMenuItem.Checked = _settings.WhisperModel == WhisperModelSize.TinyEn;
        _baseModelMenuItem.Checked = _settings.WhisperModel == WhisperModelSize.BaseEn;
        _smallModelMenuItem.Checked = _settings.WhisperModel == WhisperModelSize.SmallEn;
        _statusMenuItem.Text = $"Status: {_status}";
        _partialPhraseMenuItem.Text = $"Heard: {Truncate(_partialPhrase, 90)}";
        _lastPhraseMenuItem.Text = $"Last accepted: {Truncate(_lastPhrase, 90)}";
        _lastDispatchMenuItem.Text = $"Last dispatch: {Truncate(_lastDispatch, 90)}";
        _notifyIcon.Icon = _dispatching ? _workingIcon : _enabled ? _enabledIcon : _disabledIcon;
        _notifyIcon.Text = Truncate(_enabled ? $"VoiceCodex: {_status}" : "VoiceCodex: disabled", 63);
    }

    private ToolStripMenuItem CreateModelMenuItem(string text, WhisperModelSize model)
    {
        var item = new ToolStripMenuItem(text);
        item.Click += (_, _) =>
        {
            _settings.WhisperModel = model;
            SettingsStore.Save(_settings);
            RecordActivity($"Whisper model set to {model.DisplayName}.");
            RefreshMenu();
            Speak($"Whisper model set to {model.SpokenName}.");
        };
        return item;
    }

    private void ToggleVoiceResponses()
    {
        _settings.VoiceResponsesEnabled = _voiceResponsesMenuItem.Checked;
        SettingsStore.Save(_settings);
        RecordActivity(_settings.VoiceResponsesEnabled ? "Voice responses enabled." : "Voice responses disabled.");
        RefreshMenu();
        Speak(_settings.VoiceResponsesEnabled ? "Voice responses enabled." : "Voice responses disabled.", force: true);
    }

    private void StartController()
    {
        try
        {
            CodexController.EnsureRunning();
            _lastDispatch = "Controller running";
            RecordActivity("Persistent VoiceCodex Controller is running.");
            RefreshMenu();
            Speak("Controller running.");
        }
        catch (Exception ex)
        {
            _lastDispatch = "Controller start failed";
            RecordActivity($"Controller start failed: {ex.Message}");
            RefreshMenu();
            ShowErrorBalloon($"Controller start failed: {ex.Message}");
            Speak("Controller start failed.");
        }
    }

    private void ShowActivityLog()
    {
        if (_activityLogForm is null || _activityLogForm.IsDisposed)
        {
            _activityLogForm = new ActivityLogForm();
        }

        _activityLogForm.Show();
        _activityLogForm.Activate();
    }

    private void OpenLogFolder()
    {
        Directory.CreateDirectory(AppPaths.LogDirectory);
        Process.Start(new ProcessStartInfo
        {
            FileName = AppPaths.LogDirectory,
            UseShellExecute = true
        });
    }

    private void RecordActivity(string message)
    {
        ActivityLog.Add(message);
        _activityLogForm?.RefreshLog();
    }

    private void Speak(string text, bool force = false)
    {
        if (!force && !_settings.VoiceResponsesEnabled)
        {
            return;
        }

        if (_enabled)
        {
            _speechListener.SuppressCaptureFor(EstimateSpeechDuration(text));
        }

        _voiceFeedback.Speak(text);
    }

    private static TimeSpan EstimateSpeechDuration(string text)
    {
        var milliseconds = Math.Clamp(900 + text.Length * 65, 1200, 9000);
        return TimeSpan.FromMilliseconds(milliseconds);
    }

    private void PostToUi(Action action)
    {
        _uiContext.Post(_ => action(), null);
    }

    private void ShowInfoBalloon(string message)
    {
        _notifyIcon.ShowBalloonTip(1200, "VoiceCodex", message, ToolTipIcon.Info);
    }

    private void ShowErrorBalloon(string message)
    {
        _notifyIcon.ShowBalloonTip(3500, "VoiceCodex", message, ToolTipIcon.Error);
    }

    private static ToolStripMenuItem CreateDisabledMenuItem(string text)
    {
        return new ToolStripMenuItem(text)
        {
            Enabled = false
        };
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : string.Concat(value.AsSpan(0, maxLength - 3), "...");
    }
}

internal sealed class SpeechListener : IDisposable
{
    private const int SampleRate = 16000;
    private const int Channels = 1;
    private const float SpeechThreshold = 0.018f;
    private static readonly TimeSpan MinimumUtteranceDuration = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan EndSilenceDuration = TimeSpan.FromMilliseconds(550);
    private static readonly TimeSpan MaximumUtteranceDuration = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(750);

    private readonly object gate = new();
    private readonly SemaphoreSlim transcriptionGate = new(1, 1);
    private readonly Func<WhisperModelSize> getModelSize;

    private WaveInEvent? waveIn;
    private WaveFileWriter? writer;
    private DateTime utteranceStartedAt;
    private DateTime lastSpeechAt;
    private DateTime lastProgressAt;
    private string? activeWavPath;
    private bool started;
    private DateTime suppressCaptureUntilUtc;
    private WhisperModelSize? loadedModelSize;
    private WhisperFactory? whisperFactory;

    public SpeechListener(Func<WhisperModelSize> getModelSize)
    {
        this.getModelSize = getModelSize;
    }

    public event EventHandler? SpeechDetected;

    public event EventHandler<string>? PartialPhraseRecognized;

    public event EventHandler<string>? PhraseRecognized;

    public event EventHandler<string>? PhraseRejected;

    public event EventHandler<string>? ListenerFaulted;

    public void Start()
    {
        if (started)
        {
            return;
        }

        started = true;
        PartialPhraseRecognized?.Invoke(this, "Whisper microphone capture starting");
        _ = Task.Run(EnsureModelReadyAsync);

        waveIn = new WaveInEvent
        {
            DeviceNumber = 0,
            WaveFormat = new WaveFormat(SampleRate, 16, Channels),
            BufferMilliseconds = 50
        };
        waveIn.DataAvailable += OnAudioAvailable;
        waveIn.RecordingStopped += (_, e) =>
        {
            if (e.Exception is not null)
            {
                ListenerFaulted?.Invoke(this, e.Exception.Message);
            }
        };
        waveIn.StartRecording();
    }

    public void Stop()
    {
        if (!started)
        {
            return;
        }

        started = false;
        waveIn?.StopRecording();
        waveIn?.Dispose();
        waveIn = null;

        string? wavToTranscribe = null;
        lock (gate)
        {
            wavToTranscribe = FinishActiveUtterance(deleteFile: false);
        }

        if (wavToTranscribe is not null)
        {
            _ = Task.Run(() => TranscribeAsync(wavToTranscribe));
        }
    }

    public void Dispose()
    {
        Stop();
        whisperFactory?.Dispose();
        transcriptionGate.Dispose();
    }

    public void SuppressCaptureFor(TimeSpan duration)
    {
        lock (gate)
        {
            var until = DateTime.UtcNow + duration;
            if (until > suppressCaptureUntilUtc)
            {
                suppressCaptureUntilUtc = until;
            }

            FinishActiveUtterance(deleteFile: true);
        }
    }

    private void OnAudioAvailable(object? sender, WaveInEventArgs e)
    {
        if (!started)
        {
            return;
        }

        var now = DateTime.UtcNow;
        lock (gate)
        {
            if (now < suppressCaptureUntilUtc)
            {
                FinishActiveUtterance(deleteFile: true);
                return;
            }
        }

        var rms = CalculateRms(e.Buffer, e.BytesRecorded);
        string? wavToTranscribe = null;

        lock (gate)
        {
            if (rms >= SpeechThreshold)
            {
                if (writer is null)
                {
                    StartUtterance(now);
                    SpeechDetected?.Invoke(this, EventArgs.Empty);
                }

                lastSpeechAt = now;
            }

            if (writer is not null)
            {
                writer.Write(e.Buffer, 0, e.BytesRecorded);

                if (now - lastProgressAt >= ProgressInterval)
                {
                    lastProgressAt = now;
                    PartialPhraseRecognized?.Invoke(this, $"Recording voice... {(now - utteranceStartedAt).TotalSeconds:0.0}s");
                }

                var duration = now - utteranceStartedAt;
                var silence = now - lastSpeechAt;
                if ((duration >= MinimumUtteranceDuration && silence >= EndSilenceDuration)
                    || duration >= MaximumUtteranceDuration)
                {
                    wavToTranscribe = FinishActiveUtterance(deleteFile: false);
                }
            }
        }

        if (wavToTranscribe is not null)
        {
            _ = Task.Run(() => TranscribeAsync(wavToTranscribe));
        }
    }

    private void StartUtterance(DateTime now)
    {
        Directory.CreateDirectory(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VoiceCodex",
            "utterances"));

        activeWavPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VoiceCodex",
            "utterances",
            $"utterance-{DateTime.Now:yyyyMMdd-HHmmss-fff}.wav");
        utteranceStartedAt = now;
        lastSpeechAt = now;
        lastProgressAt = now;
        writer = new WaveFileWriter(activeWavPath, new WaveFormat(SampleRate, 16, Channels));
    }

    private string? FinishActiveUtterance(bool deleteFile)
    {
        if (writer is null || activeWavPath is null)
        {
            return null;
        }

        var path = activeWavPath;
        writer.Dispose();
        writer = null;
        activeWavPath = null;

        if (deleteFile)
        {
            TryDelete(path);
            return null;
        }

        return path;
    }

    private async Task EnsureModelReadyAsync()
    {
        var modelSize = getModelSize();
        if (loadedModelSize == modelSize && whisperFactory is not null)
        {
            return;
        }

        try
        {
            var modelPath = GetModelPath(modelSize);
            Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);
            if (!File.Exists(modelPath))
            {
                PartialPhraseRecognized?.Invoke(this, $"Downloading local Whisper {modelSize.DisplayName} model once");
                await using var modelStream = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(modelSize.GgmlType);
                await using var fileWriter = File.Open(modelPath, FileMode.Create, FileAccess.Write, FileShare.None);
                await modelStream.CopyToAsync(fileWriter);
            }

            whisperFactory?.Dispose();
            whisperFactory = WhisperFactory.FromPath(modelPath);
            loadedModelSize = modelSize;
            PartialPhraseRecognized?.Invoke(this, $"Local Whisper {modelSize.DisplayName} ready");
        }
        catch (Exception ex)
        {
            ListenerFaulted?.Invoke(this, $"Whisper model setup failed: {ex.Message}");
        }
    }

    private async Task TranscribeAsync(string wavPath)
    {
        try
        {
            PartialPhraseRecognized?.Invoke(this, "Transcribing locally with Whisper...");
            var stopwatch = Stopwatch.StartNew();
            await EnsureModelReadyAsync();
            if (whisperFactory is null)
            {
                PhraseRejected?.Invoke(this, "Whisper is not ready yet.");
                return;
            }

            await transcriptionGate.WaitAsync();
            try
            {
                using var processor = whisperFactory.CreateBuilder()
                    .WithLanguage("en")
                    .Build();
                await using var fileStream = File.OpenRead(wavPath);
                var builder = new StringBuilder();

                await foreach (var result in processor.ProcessAsync(fileStream))
                {
                    builder.Append(result.Text);
                }

                var text = NormalizeTranscript(builder.ToString());
                stopwatch.Stop();
                if (string.IsNullOrWhiteSpace(text))
                {
                    PhraseRejected?.Invoke(this, "Whisper heard audio, but produced no text.");
                    return;
                }

                PartialPhraseRecognized?.Invoke(this, $"Transcribed in {stopwatch.ElapsedMilliseconds} ms: {text}");
                PartialPhraseRecognized?.Invoke(this, text);
                PhraseRecognized?.Invoke(this, text);
            }
            finally
            {
                transcriptionGate.Release();
            }
        }
        catch (Exception ex)
        {
            ListenerFaulted?.Invoke(this, $"Whisper transcription failed: {ex.Message}");
        }
        finally
        {
            TryDelete(wavPath);
        }
    }

    private static float CalculateRms(byte[] buffer, int bytesRecorded)
    {
        if (bytesRecorded == 0)
        {
            return 0;
        }

        double sumSquares = 0;
        var sampleCount = bytesRecorded / 2;
        for (var i = 0; i < bytesRecorded; i += 2)
        {
            var sample = BitConverter.ToInt16(buffer, i) / 32768f;
            sumSquares += sample * sample;
        }

        return (float)Math.Sqrt(sumSquares / sampleCount);
    }

    private static string NormalizeTranscript(string transcript)
    {
        return transcript
            .Replace("[BLANK_AUDIO]", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("[ Silence ]", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();
    }

    private static string GetModelPath(WhisperModelSize modelSize)
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VoiceCodex",
            "models",
            modelSize.FileName);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
        }
    }
}

internal sealed class TargetedTerminalHandoff
{
    private static readonly string[] TerminalProcessNames = ["WindowsTerminal", "wt", "powershell", "pwsh", "cmd", "wezterm-gui", "alacritty"];
    private static readonly string[] TargetStopWords =
    [
        "the",
        "a",
        "an",
        "one",
        "that",
        "thats",
        "is",
        "its",
        "handling",
        "working",
        "on",
        "for",
        "with",
        "codex",
        "terminal",
        "session",
        "pane",
        "split",
        "left",
        "right",
        "top",
        "bottom"
    ];

    private PendingTerminalHandoff? pending;

    public bool TryHandle(string transcript, out TargetedTerminalHandoffResult result)
    {
        if (pending is not null)
        {
            var currentPending = pending.Value;
            if (IsCancel(transcript))
            {
                pending = null;
                result = TargetedTerminalHandoffResult.Info(
                    "Cancelled targeted terminal handoff.",
                    "Cancelled targeted terminal handoff.",
                    "Targeted terminal cancelled");
                return true;
            }

            if (TryParseRequest(transcript, out var replacementRequest))
            {
                pending = null;
                return TryStartRequest(replacementRequest, out result);
            }

            if (TryResolveClarification(transcript, currentPending, out var candidate, out var paneDirection, out var explanation))
            {
                pending = null;
                result = SendToTerminal(currentPending.Request, candidate, paneDirection, explanation);
                return true;
            }

            result = TargetedTerminalHandoffResult.Info(
                "I still need which terminal. Say first, second, left, right, active terminal, or cancel.",
                $"Clarification did not resolve targeted terminal: {transcript}",
                "Targeted terminal clarification needed",
                showBalloon: false);
            return true;
        }

        if (!TryParseRequest(transcript, out var request))
        {
            result = TargetedTerminalHandoffResult.NotHandled;
            return false;
        }

        return TryStartRequest(request, out result);
    }

    public static string ExplainDryRun(string transcript)
    {
        return TryParseRequest(transcript, out var request)
            ? $"target='{request.TargetHint}' side='{request.SideHint}' payload='{request.Payload}'"
            : "not a targeted terminal handoff";
    }

    private bool TryStartRequest(TargetedTerminalRequest request, out TargetedTerminalHandoffResult result)
    {
        var candidates = DiscoverCandidates(request.TargetHint);
        if (candidates.Count == 0)
        {
            result = TargetedTerminalHandoffResult.Error(
                "I could not find a terminal window for that Codex handoff.",
                $"No terminal candidates for target '{request.TargetHint}'. Payload: {request.Payload}",
                "Targeted terminal not found");
            return true;
        }

        if (request.SideHint is PaneDirection.Left or PaneDirection.Right && candidates.Count > 1)
        {
            var sideCandidate = SelectByWindowSide(candidates, request.SideHint);
            result = SendToTerminal(request, sideCandidate, PaneDirection.None, $"selected {request.SideHint.ToString().ToLowerInvariant()} terminal window");
            return true;
        }

        if (request.SideHint is PaneDirection.Left or PaneDirection.Right && candidates.Count == 1)
        {
            result = SendToTerminal(request, candidates[0], request.SideHint, $"selected {request.SideHint.ToString().ToLowerInvariant()} split pane");
            return true;
        }

        var strongCandidates = SelectStrongCandidates(candidates, request.TargetHint);
        if (strongCandidates.Count == 1)
        {
            result = SendToTerminal(request, strongCandidates[0], PaneDirection.None, "single best target");
            return true;
        }

        var clarificationCandidates = strongCandidates.Count > 0 ? strongCandidates : candidates.Take(5).ToList();
        pending = new PendingTerminalHandoff(request, clarificationCandidates);
        result = TargetedTerminalHandoffResult.Info(
            BuildClarificationPrompt(clarificationCandidates),
            $"Targeted terminal needs clarification for '{request.TargetHint}'. Candidates: {string.Join(" | ", clarificationCandidates.Select(candidate => candidate.Description))}",
            "Targeted terminal clarification needed",
            showBalloon: true);
        return true;
    }

    private static List<TerminalCandidate> SelectStrongCandidates(List<TerminalCandidate> candidates, string targetHint)
    {
        if (candidates.Count == 0)
        {
            return [];
        }

        var targetTokens = GetTargetTokens(targetHint);
        if (targetTokens.Count == 0)
        {
            return candidates.Take(1).ToList();
        }

        var best = candidates[0].Score;
        var threshold = Math.Max(0.42, best - 0.12);
        var strong = candidates
            .Where(candidate => candidate.Score >= threshold)
            .Take(5)
            .ToList();

        return strong.Count == 1 || best >= 0.82 && candidates.Count > 1 && best - candidates[1].Score >= 0.20
            ? [candidates[0]]
            : strong;
    }

    private static TargetedTerminalHandoffResult SendToTerminal(
        TargetedTerminalRequest request,
        TerminalCandidate candidate,
        PaneDirection paneDirection,
        string selectionReason)
    {
        if (!IsWindow(candidate.Hwnd))
        {
            return TargetedTerminalHandoffResult.Error(
                "That terminal window is no longer available.",
                $"Targeted terminal vanished before send: {candidate.Description}",
                "Targeted terminal unavailable");
        }

        try
        {
            ShowWindow(candidate.Hwnd, ShowNormal);
            SetForegroundWindow(candidate.Hwnd);
            Thread.Sleep(220);

            if (paneDirection == PaneDirection.Left)
            {
                SendKeys.SendWait("%{LEFT}");
                Thread.Sleep(120);
            }
            else if (paneDirection == PaneDirection.Right)
            {
                SendKeys.SendWait("%{RIGHT}");
                Thread.Sleep(120);
            }

            var previousClipboardText = TryGetClipboardText(out var clipboardHadText);
            Clipboard.SetText(request.Payload);
            SendKeys.SendWait("^v");
            Thread.Sleep(80);
            SendKeys.SendWait("{ENTER}");
            Thread.Sleep(80);
            if (clipboardHadText && previousClipboardText is not null)
            {
                Clipboard.SetText(previousClipboardText);
            }

            var side = paneDirection is PaneDirection.Left or PaneDirection.Right
                ? $" {paneDirection.ToString().ToLowerInvariant()} pane"
                : string.Empty;
            var message = $"Sent to {candidate.ShortDescription}{side}.";
            return TargetedTerminalHandoffResult.Info(
                message,
                $"{message} Reason: {selectionReason}. Payload: {request.Payload}",
                "Sent to targeted terminal");
        }
        catch (Exception ex)
        {
            return TargetedTerminalHandoffResult.Error(
                $"Targeted terminal send failed: {ex.Message}",
                $"Targeted terminal send failed for {candidate.Description}: {ex.Message}",
                "Targeted terminal send failed");
        }
    }

    private static bool TryResolveClarification(
        string transcript,
        PendingTerminalHandoff pendingRequest,
        out TerminalCandidate candidate,
        out PaneDirection paneDirection,
        out string explanation)
    {
        var text = Normalize(transcript);
        paneDirection = PaneDirection.None;

        if (text.Contains("active", StringComparison.Ordinal) || text.Contains("current", StringComparison.Ordinal))
        {
            var active = GetForegroundWindow();
            var activeCandidate = pendingRequest.Candidates.FirstOrDefault(candidate => candidate.Hwnd == active);
            if (activeCandidate.Hwnd != IntPtr.Zero)
            {
                candidate = activeCandidate;
                explanation = "selected active terminal";
                return true;
            }
        }

        var ordinal = TryGetOrdinal(text);
        if (ordinal is not null && ordinal.Value >= 0 && ordinal.Value < pendingRequest.Candidates.Count)
        {
            candidate = pendingRequest.Candidates[ordinal.Value];
            explanation = $"selected option {ordinal.Value + 1}";
            return true;
        }

        if (text.Contains("left", StringComparison.Ordinal))
        {
            paneDirection = pendingRequest.Candidates.Count == 1 ? PaneDirection.Left : PaneDirection.None;
            candidate = pendingRequest.Candidates.Count == 1
                ? pendingRequest.Candidates[0]
                : SelectByWindowSide(pendingRequest.Candidates, PaneDirection.Left);
            explanation = pendingRequest.Candidates.Count == 1 ? "selected left split pane" : "selected left terminal window";
            return true;
        }

        if (text.Contains("right", StringComparison.Ordinal))
        {
            paneDirection = pendingRequest.Candidates.Count == 1 ? PaneDirection.Right : PaneDirection.None;
            candidate = pendingRequest.Candidates.Count == 1
                ? pendingRequest.Candidates[0]
                : SelectByWindowSide(pendingRequest.Candidates, PaneDirection.Right);
            explanation = pendingRequest.Candidates.Count == 1 ? "selected right split pane" : "selected right terminal window";
            return true;
        }

        if (text.Contains("newest", StringComparison.Ordinal)
            || text.Contains("latest", StringComparison.Ordinal)
            || text.Contains("recent", StringComparison.Ordinal))
        {
            candidate = pendingRequest.Candidates
                .OrderByDescending(candidate => candidate.ProcessStartedAt)
                .First();
            explanation = "selected newest terminal";
            return true;
        }

        var scored = pendingRequest.Candidates
            .Select(candidate => candidate with { Score = ScoreCandidate(candidate, text) })
            .OrderByDescending(candidate => candidate.Score)
            .ToList();
        if (scored.Count > 0 && scored[0].Score >= 0.45 && (scored.Count == 1 || scored[0].Score - scored[1].Score >= 0.16))
        {
            candidate = scored[0];
            explanation = "selected by clarified title hint";
            return true;
        }

        candidate = default;
        explanation = string.Empty;
        return false;
    }

    private static bool TryParseRequest(string transcript, out TargetedTerminalRequest request)
    {
        request = default;
        if (string.IsNullOrWhiteSpace(transcript))
        {
            return false;
        }

        var normalized = Normalize(transcript);
        if (!normalized.Contains("terminal", StringComparison.Ordinal)
            || !ContainsAny(normalized, "tell", "ask", "send", "paste"))
        {
            return false;
        }

        if (!TryFindPayloadBounds(transcript, out var targetEnd, out var payloadStart))
        {
            return false;
        }

        var payload = TrimPayload(transcript[payloadStart..]);
        if (string.IsNullOrWhiteSpace(payload))
        {
            return false;
        }

        var targetText = transcript[..targetEnd];
        var targetHint = ExtractTargetHint(targetText);
        var sideHint = GetSideHint(normalized);
        request = new TargetedTerminalRequest(
            string.IsNullOrWhiteSpace(targetHint) ? "codex terminal" : targetHint,
            payload,
            sideHint);
        return true;
    }

    private static bool TryFindPayloadBounds(string transcript, out int targetEnd, out int payloadStart)
    {
        targetEnd = -1;
        payloadStart = -1;
        var lower = transcript.ToLowerInvariant();
        var terminalIndex = lower.IndexOf("terminal", StringComparison.Ordinal);
        if (terminalIndex < 0)
        {
            return false;
        }

        var markers = new[]
        {
            " and tell it to ",
            " then tell it to ",
            " tell it to ",
            " and ask it to ",
            " then ask it to ",
            " ask it to ",
            " and send it ",
            " then send it ",
            " send it ",
            " and paste ",
            " then paste ",
            " paste ",
            " and tell codex to ",
            " tell codex to ",
            " and ask codex to ",
            " ask codex to ",
            " and send codex ",
            " send codex "
        };

        foreach (var marker in markers)
        {
            var index = lower.IndexOf(marker, terminalIndex, StringComparison.Ordinal);
            if (index >= 0)
            {
                targetEnd = index;
                payloadStart = index + marker.Length;
                return true;
            }
        }

        var colonIndex = transcript.IndexOf(':', terminalIndex);
        if (colonIndex >= 0)
        {
            targetEnd = colonIndex;
            payloadStart = colonIndex + 1;
            return true;
        }

        var toIndex = lower.IndexOf(" to ", terminalIndex + "terminal".Length, StringComparison.Ordinal);
        if (toIndex >= 0)
        {
            targetEnd = toIndex;
            payloadStart = toIndex + " to ".Length;
            return true;
        }

        return TryFindImplicitPayloadBounds(transcript, terminalIndex, out targetEnd, out payloadStart);
    }

    private static bool TryFindImplicitPayloadBounds(string transcript, int terminalIndex, out int targetEnd, out int payloadStart)
    {
        targetEnd = -1;
        payloadStart = -1;
        var lower = transcript.ToLowerInvariant();
        var searchStart = terminalIndex + "terminal".Length;
        foreach (var verb in new[] { "design", "review", "fix", "build", "test", "update", "change", "create", "open", "find", "look", "run", "write", "implement" })
        {
            var marker = " " + verb + " ";
            var index = lower.IndexOf(marker, searchStart, StringComparison.Ordinal);
            if (index < 0)
            {
                continue;
            }

            targetEnd = index;
            payloadStart = index + 1;
            return true;
        }

        return false;
    }

    private static string ExtractTargetHint(string targetText)
    {
        var lower = Normalize(targetText);
        foreach (var phrase in new[]
                 {
                     "switch to",
                     "go to",
                     "focus",
                     "tell",
                     "ask",
                     "send",
                     "paste",
                     "the one",
                     "one thats",
                     "one that is",
                     "that is",
                     "thats"
                 })
        {
            lower = lower.Replace(phrase, " ", StringComparison.Ordinal);
        }

        var tokens = lower
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.Length > 1 && !TargetStopWords.Contains(token, StringComparer.Ordinal))
            .ToArray();
        return string.Join(' ', tokens);
    }

    private static string TrimPayload(string text)
    {
        var result = text.Trim();
        foreach (var prefix in new[] { "to ", "that ", "this " })
        {
            if (result.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                result = result[prefix.Length..].Trim();
            }
        }

        return result.Trim().Trim('"', '\'', '“', '”', '‘', '’');
    }

    private static List<TerminalCandidate> DiscoverCandidates(string targetHint)
    {
        var candidates = new List<TerminalCandidate>();
        EnumWindows((hwnd, _) =>
        {
            var title = GetWindowTitle(hwnd);
            if (string.IsNullOrWhiteSpace(title))
            {
                return true;
            }

            GetWindowThreadProcessId(hwnd, out var processId);
            var processName = GetProcessName(processId);
            if (!IsTerminalCandidate(processName, title))
            {
                return true;
            }

            GetWindowRect(hwnd, out var rect);
            var candidate = new TerminalCandidate(
                hwnd,
                title,
                processName,
                rect,
                SafeProcessStartTime(processId),
                0);
            candidate = candidate with { Score = ScoreCandidate(candidate, targetHint) };
            candidates.Add(candidate);
            return true;
        }, IntPtr.Zero);

        return candidates
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.ProcessStartedAt)
            .Take(8)
            .ToList();
    }

    private static double ScoreCandidate(TerminalCandidate candidate, string targetHint)
    {
        var targetTokens = GetTargetTokens(targetHint);
        var title = Normalize(candidate.Title);
        var titleTokens = title.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var tokenScore = targetTokens.Count == 0 ? 0.50 : ScoreTokenOverlap(targetTokens, titleTokens);
        var codexBonus = title.Contains("codex", StringComparison.Ordinal) ? 0.18 : 0d;
        var processBonus = TerminalProcessNames.Any(name => string.Equals(candidate.ProcessName, name, StringComparison.OrdinalIgnoreCase)) ? 0.10 : 0d;
        return Math.Min(1d, tokenScore + codexBonus + processBonus);
    }

    private static double ScoreTokenOverlap(List<string> targetTokens, string[] titleTokens)
    {
        if (targetTokens.Count == 0 || titleTokens.Length == 0)
        {
            return 0d;
        }

        double score = 0;
        foreach (var targetToken in targetTokens)
        {
            if (titleTokens.Any(token => token == targetToken))
            {
                score += 1d;
                continue;
            }

            if (titleTokens.Any(token => token.Length > 3 && (token.Contains(targetToken, StringComparison.Ordinal) || targetToken.Contains(token, StringComparison.Ordinal))))
            {
                score += 0.75d;
                continue;
            }

            if (titleTokens.Any(token => token.Length > 3 && EditDistanceAtMost(token, targetToken, 2)))
            {
                score += 0.50d;
            }
        }

        return score / targetTokens.Count * 0.78;
    }

    private static List<string> GetTargetTokens(string targetHint)
    {
        return Normalize(targetHint)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.Length > 2 && !TargetStopWords.Contains(token, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static bool EditDistanceAtMost(string left, string right, int maxDistance)
    {
        if (Math.Abs(left.Length - right.Length) > maxDistance)
        {
            return false;
        }

        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        var current = new int[right.Length + 1];
        for (var i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            var rowBest = current[0];
            for (var j = 1; j <= right.Length; j++)
            {
                var cost = left[i - 1] == right[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
                rowBest = Math.Min(rowBest, current[j]);
            }

            if (rowBest > maxDistance)
            {
                return false;
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length] <= maxDistance;
    }

    private static TerminalCandidate SelectByWindowSide(IReadOnlyList<TerminalCandidate> candidates, PaneDirection side)
    {
        return side == PaneDirection.Left
            ? candidates.OrderBy(candidate => candidate.CenterX).First()
            : candidates.OrderByDescending(candidate => candidate.CenterX).First();
    }

    private static string BuildClarificationPrompt(IReadOnlyList<TerminalCandidate> candidates)
    {
        return candidates.Count == 1
            ? $"I found one likely terminal: {candidates[0].ShortDescription}. Say left or right for the split pane, active terminal, or cancel."
            : $"I found {candidates.Count} possible Codex terminals. Say first, second, left, right, newest, active terminal, or cancel.";
    }

    private static PaneDirection GetSideHint(string normalized)
    {
        if (normalized.Contains("left", StringComparison.Ordinal))
        {
            return PaneDirection.Left;
        }

        return normalized.Contains("right", StringComparison.Ordinal) ? PaneDirection.Right : PaneDirection.None;
    }

    private static int? TryGetOrdinal(string text)
    {
        if (text.Contains("first", StringComparison.Ordinal) || text.Contains("number one", StringComparison.Ordinal) || text == "one" || text == "1")
        {
            return 0;
        }

        if (text.Contains("second", StringComparison.Ordinal) || text.Contains("number two", StringComparison.Ordinal) || text == "two" || text == "2")
        {
            return 1;
        }

        if (text.Contains("third", StringComparison.Ordinal) || text.Contains("number three", StringComparison.Ordinal) || text == "three" || text == "3")
        {
            return 2;
        }

        if (text.Contains("fourth", StringComparison.Ordinal) || text.Contains("number four", StringComparison.Ordinal) || text == "four" || text == "4")
        {
            return 3;
        }

        return null;
    }

    private static bool IsCancel(string transcript)
    {
        var text = Normalize(transcript);
        return text is "cancel" or "never mind" or "nevermind" or "stop" or "forget it";
    }

    private static bool IsTerminalCandidate(string processName, string title)
    {
        return TerminalProcessNames.Any(name => string.Equals(processName, name, StringComparison.OrdinalIgnoreCase))
            || title.Contains("terminal", StringComparison.OrdinalIgnoreCase)
            || title.Contains("powershell", StringComparison.OrdinalIgnoreCase)
            || title.Contains("command prompt", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetProcessName(uint processId)
    {
        try
        {
            return Process.GetProcessById((int)processId).ProcessName;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static DateTime SafeProcessStartTime(uint processId)
    {
        try
        {
            return Process.GetProcessById((int)processId).StartTime;
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    private static string? TryGetClipboardText(out bool hadText)
    {
        try
        {
            hadText = Clipboard.ContainsText();
            return hadText ? Clipboard.GetText() : null;
        }
        catch
        {
            hadText = false;
            return null;
        }
    }

    private static bool ContainsAny(string text, params string[] values)
    {
        return values.Any(value => text.Contains(value, StringComparison.Ordinal));
    }

    private static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var ch in text.ToLowerInvariant())
        {
            builder.Append(char.IsLetterOrDigit(ch) || char.IsWhiteSpace(ch) ? ch : ' ');
        }

        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        var length = GetWindowTextLength(hwnd);
        if (length <= 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(length + 1);
        _ = GetWindowText(hwnd, builder, builder.Capacity);
        return builder.ToString();
    }

    private const int ShowNormal = 1;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Rect
    {
        public readonly int Left;
        public readonly int Top;
        public readonly int Right;
        public readonly int Bottom;
    }

    private readonly record struct TargetedTerminalRequest(string TargetHint, string Payload, PaneDirection SideHint);

    private readonly record struct PendingTerminalHandoff(TargetedTerminalRequest Request, IReadOnlyList<TerminalCandidate> Candidates);

    private readonly record struct TerminalCandidate(IntPtr Hwnd, string Title, string ProcessName, Rect Bounds, DateTime ProcessStartedAt, double Score)
    {
        public int CenterX => Bounds.Left + ((Bounds.Right - Bounds.Left) / 2);

        public string ShortDescription => string.IsNullOrWhiteSpace(Title) ? ProcessName : Truncate(Title, 54);

        public string Description => $"{ShortDescription} [{ProcessName}] score={Score:0.00}";
    }

    private enum PaneDirection
    {
        None,
        Left,
        Right
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : string.Concat(value.AsSpan(0, maxLength - 3), "...");
    }
}

internal readonly record struct TargetedTerminalHandoffResult(
    bool Handled,
    string Message,
    string Activity,
    string DispatchStatus,
    string SpokenText,
    bool ShowBalloon,
    bool IsError)
{
    public static readonly TargetedTerminalHandoffResult NotHandled = new(false, string.Empty, string.Empty, string.Empty, string.Empty, false, false);

    public static TargetedTerminalHandoffResult Info(string message, string activity, string dispatchStatus, bool showBalloon = true)
    {
        return new TargetedTerminalHandoffResult(true, message, activity, dispatchStatus, message, showBalloon, false);
    }

    public static TargetedTerminalHandoffResult Error(string message, string activity, string dispatchStatus)
    {
        return new TargetedTerminalHandoffResult(true, message, activity, dispatchStatus, message, true, true);
    }
}

internal enum CommandDecisionKind
{
    Ignore,
    LocalResponse,
    Accept
}

internal sealed record CommandDecision(CommandDecisionKind Kind, string CommandText, string Reason, string LocalResponse = "");

internal static class CommandGate
{
    private static readonly string[] WakePrefixes =
    [
        "hey codex",
        "voice codex",
        "codex",
        "computer"
    ];

    private static readonly string[] ActionStarts =
    [
        "switch ",
        "focus ",
        "open ",
        "close ",
        "type ",
        "paste ",
        "enter ",
        "press ",
        "send ",
        "run ",
        "start ",
        "stop ",
        "click ",
        "move ",
        "scroll ",
        "go to ",
        "change ",
        "create ",
        "edit ",
        "delete ",
        "find ",
        "fix ",
        "look ",
        "search ",
        "investigate ",
        "review ",
        "check ",
        "build ",
        "test ",
        "install ",
        "commit "
    ];

    private static readonly string[] LeadingFillers =
    [
        "alright",
        "all right",
        "okay",
        "ok",
        "great",
        "cool",
        "thanks",
        "thank you",
        "please",
        "so",
        "now"
    ];

    private static readonly string[] PolitePrefixes =
    [
        "could you ",
        "can you ",
        "would you ",
        "will you ",
        "please ",
        "i need you to ",
        "i want you to ",
        "can we ",
        "could we ",
        "let's ",
        "lets "
    ];

    public static CommandDecision Evaluate(string transcript)
    {
        var cleaned = Normalize(transcript);
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return new CommandDecision(CommandDecisionKind.Ignore, string.Empty, "empty transcript");
        }

        if (TryGetLocalResponse(cleaned, out var localResponse))
        {
            return new CommandDecision(CommandDecisionKind.LocalResponse, string.Empty, "local VoiceCodex question", localResponse);
        }

        cleaned = StripLeadingFillers(cleaned);

        foreach (var wakePrefix in WakePrefixes)
        {
            if (cleaned == wakePrefix)
            {
                return new CommandDecision(
                    CommandDecisionKind.LocalResponse,
                    string.Empty,
                    "local VoiceCodex question",
                    "VoiceCodex is listening. Say a command like switch to the next terminal tab, or type hello into the terminal.");
            }

            if (cleaned.StartsWith(wakePrefix + " ", StringComparison.Ordinal))
            {
                var command = cleaned[(wakePrefix.Length + 1)..].Trim();
                if (string.IsNullOrWhiteSpace(command))
                {
                    return new CommandDecision(CommandDecisionKind.Ignore, string.Empty, "wake word only");
                }

                return new CommandDecision(CommandDecisionKind.Accept, command, $"optional prefix '{wakePrefix}'");
            }
        }

        cleaned = StripPolitePrefix(StripLeadingFillers(cleaned));

        if (FastIntentClassifier.TryClassify(cleaned, out var fastDecision))
        {
            return fastDecision;
        }

        if (SemanticIntentClassifier.TryClassify(cleaned, out var semanticDecision))
        {
            return semanticDecision;
        }

        var fallbackCommand = CommandGateFallback.NormalizeForAction(cleaned);
        if (CommandGateFallback.StartsWithAction(fallbackCommand))
        {
            return new CommandDecision(CommandDecisionKind.Accept, fallbackCommand, "agent lane: likely action command");
        }

        var embeddedCommand = TryExtractEmbeddedCommand(fallbackCommand);
        if (embeddedCommand is not null && CommandGateFallback.LooksLikeAction(embeddedCommand))
        {
            return new CommandDecision(CommandDecisionKind.Accept, embeddedCommand, "agent lane: embedded action command");
        }

        if (IsCasualOrTestSpeech(cleaned))
        {
            return new CommandDecision(CommandDecisionKind.Ignore, string.Empty, "local intent: casual or incomplete speech");
        }

        return CodexCommandClassifier.Classify(transcript, cleaned);
    }

    private static bool TryGetLocalResponse(string cleaned, out string response)
    {
        if (cleaned is "can you hear me" or "do you hear me" or "are you listening" or "can you hear me now"
            || cleaned.Contains("can you hear me", StringComparison.Ordinal)
            || cleaned.Contains("are you listening", StringComparison.Ordinal))
        {
            response = "Yes. I can hear you. I will only act when I hear a computer-control command.";
            return true;
        }

        if (cleaned.Contains("what is the wake", StringComparison.Ordinal)
            || cleaned.Contains("what is the wake word", StringComparison.Ordinal)
            || cleaned.Contains("wake phrase", StringComparison.Ordinal)
            || cleaned.Contains("how do i use", StringComparison.Ordinal)
            || cleaned.Contains("what can i say", StringComparison.Ordinal)
            || cleaned is "help")
        {
            response = "There is no required wake phrase. Say clear commands like switch to the next terminal tab, focus terminal, or type this text. You can say Codex first if the command is ambiguous.";
            return true;
        }

        if (cleaned.Contains("what did you hear", StringComparison.Ordinal))
        {
            response = "The latest transcript is shown in the VoiceCodex tray activity log.";
            return true;
        }

        if (cleaned.Contains("what is your name", StringComparison.Ordinal)
            || cleaned.Contains("who are you", StringComparison.Ordinal))
        {
            response = "I am VoiceCodex.";
            return true;
        }

        response = string.Empty;
        return false;
    }

    private static string StripLeadingFillers(string text)
    {
        var result = text;
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var filler in LeadingFillers.OrderByDescending(value => value.Length))
            {
                if (result == filler)
                {
                    return string.Empty;
                }

                if (result.StartsWith(filler + " ", StringComparison.Ordinal))
                {
                    result = result[(filler.Length + 1)..].Trim();
                    changed = true;
                    break;
                }
            }
        }

        return result;
    }

    private static string StripPolitePrefix(string text)
    {
        foreach (var prefix in PolitePrefixes.OrderByDescending(value => value.Length))
        {
            if (text.StartsWith(prefix, StringComparison.Ordinal))
            {
                return text[prefix.Length..].Trim();
            }
        }

        return text;
    }

    private static string? TryExtractEmbeddedCommand(string text)
    {
        foreach (var actionStart in ActionStarts)
        {
            var action = actionStart.Trim();
            var marker = " " + action + " ";
            var index = text.IndexOf(marker, StringComparison.Ordinal);
            if (index >= 0)
            {
                return text[(index + 1)..].Trim();
            }
        }

        return null;
    }

    private static bool IsCasualOrTestSpeech(string cleaned)
    {
        if (cleaned.Contains("this is a test", StringComparison.Ordinal)
            || cleaned.StartsWith("testing", StringComparison.Ordinal)
            || cleaned.StartsWith("test ", StringComparison.Ordinal)
            || cleaned is "test")
        {
            return true;
        }

        return cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 4
            && !ActionStarts.Any(cleaned.StartsWith);
    }

    private static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var ch in text.ToLowerInvariant())
        {
            builder.Append(char.IsLetterOrDigit(ch) || char.IsWhiteSpace(ch) ? ch : ' ');
        }

        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}

internal static class FastIntentClassifier
{
    private static readonly string[] IgnorePhrases =
    [
        "cough",
        "this is a test",
        "testing",
        "test one two"
    ];

    public static bool TryClassify(string text, out CommandDecision decision)
    {
        if (IgnorePhrases.Any(phrase => text == phrase || text.Contains(phrase, StringComparison.Ordinal)))
        {
            decision = new CommandDecision(CommandDecisionKind.Ignore, string.Empty, "fast local intent: non-command speech");
            return true;
        }

        if (TryChrome(text, out var command)
            || TryWindowManagement(text, out command)
            || TryMouseClick(text, out command)
            || TryTerminalTab(text, out command)
            || TryFocusTerminal(text, out command)
            || TrySwitchApplication(text, out command)
            || TryTyping(text, out command)
            || TryKeyPress(text, out command))
        {
            decision = new CommandDecision(CommandDecisionKind.Accept, command, "fast local intent");
            return true;
        }

        decision = new CommandDecision(CommandDecisionKind.Ignore, string.Empty, string.Empty);
        return false;
    }

    private static bool TryWindowManagement(string text, out string command)
    {
        var mentionsTerminal = text.Contains("terminal", StringComparison.Ordinal)
            || text.Contains("powershell", StringComparison.Ordinal);
        var mentionsBrowser = text.Contains("browser", StringComparison.Ordinal)
            || text.Contains("chrome", StringComparison.Ordinal);

        if ((text.Contains("bring", StringComparison.Ordinal)
                || text.Contains("focus", StringComparison.Ordinal)
                || text.Contains("front", StringComparison.Ordinal)
                || text.Contains("switch to", StringComparison.Ordinal))
            && mentionsTerminal
            && text.Contains("max", StringComparison.Ordinal))
        {
            command = "Bring the terminal to the front and maximize it.";
            return true;
        }

        if (text.Contains("max", StringComparison.Ordinal)
            && (mentionsTerminal || mentionsBrowser || text.Contains("window", StringComparison.Ordinal) || text.Contains("app", StringComparison.Ordinal)))
        {
            command = mentionsTerminal
                ? "Maximize the terminal."
                : mentionsBrowser
                    ? "Maximize the browser."
                    : "Maximize the active window.";
            return true;
        }

        if (text.Contains("minimize", StringComparison.Ordinal)
            && (mentionsTerminal || mentionsBrowser || text.Contains("window", StringComparison.Ordinal) || text.Contains("app", StringComparison.Ordinal)))
        {
            command = mentionsTerminal
                ? "Minimize the terminal."
                : mentionsBrowser
                    ? "Minimize the browser."
                    : "Minimize the active window.";
            return true;
        }

        if (text.Contains("restore", StringComparison.Ordinal)
            && (mentionsTerminal || mentionsBrowser || text.Contains("window", StringComparison.Ordinal) || text.Contains("app", StringComparison.Ordinal)))
        {
            command = mentionsTerminal
                ? "Restore the terminal."
                : mentionsBrowser
                    ? "Restore the browser."
                    : "Restore the active window.";
            return true;
        }

        command = string.Empty;
        return false;
    }

    private static bool TryMouseClick(string text, out string command)
    {
        if (!text.Contains("click", StringComparison.Ordinal))
        {
            command = string.Empty;
            return false;
        }

        if (text.Contains("double click", StringComparison.Ordinal))
        {
            command = "Double click.";
            return true;
        }

        if (text.Contains("right click", StringComparison.Ordinal))
        {
            command = "Right click.";
            return true;
        }

        var match = Regex.Match(text, @"click at (?<x>\d{1,5}) (?<y>\d{1,5})", RegexOptions.CultureInvariant);
        if (match.Success)
        {
            command = $"Click at {match.Groups["x"].Value}, {match.Groups["y"].Value}.";
            return true;
        }

        if (text.Contains("center", StringComparison.Ordinal) || text.Contains("middle", StringComparison.Ordinal))
        {
            command = "Click the center of the active window.";
            return true;
        }

        command = "Click.";
        return true;
    }

    private static bool TryChrome(string text, out string command)
    {
        if (text.Contains("open", StringComparison.Ordinal)
            && (text.Contains("chrome", StringComparison.Ordinal)
                || text.Contains("browser", StringComparison.Ordinal)
                || text.Contains("curl", StringComparison.Ordinal)))
        {
            command = "Open Chrome.";
            return true;
        }

        command = string.Empty;
        return false;
    }

    private static bool TryTerminalTab(string text, out string command)
    {
        if ((text.Contains("terminal tab", StringComparison.Ordinal) || text.Contains("tab in terminal", StringComparison.Ordinal))
            && text.Contains("previous", StringComparison.Ordinal))
        {
            command = "Switch to the previous terminal tab.";
            return true;
        }

        if ((text.Contains("terminal tab", StringComparison.Ordinal) || text.Contains("tab in terminal", StringComparison.Ordinal))
            && (text.Contains("next", StringComparison.Ordinal) || text.Contains("switch", StringComparison.Ordinal)))
        {
            command = "Switch to the next terminal tab.";
            return true;
        }

        command = string.Empty;
        return false;
    }

    private static bool TryFocusTerminal(string text, out string command)
    {
        if ((text.Contains("focus", StringComparison.Ordinal) || text.Contains("switch to", StringComparison.Ordinal))
            && (text.Contains("terminal", StringComparison.Ordinal) || text.Contains("powershell", StringComparison.Ordinal)))
        {
            command = "Focus the terminal.";
            return true;
        }

        command = string.Empty;
        return false;
    }

    private static bool TrySwitchApplication(string text, out string command)
    {
        if (text.Contains("switch", StringComparison.Ordinal)
            && (text.Contains("application", StringComparison.Ordinal) || text.Contains("app", StringComparison.Ordinal) || text.Contains("window", StringComparison.Ordinal)))
        {
            command = "Switch to the previous application.";
            return true;
        }

        command = string.Empty;
        return false;
    }

    private static bool TryTyping(string text, out string command)
    {
        var marker = "type ";
        var index = text.IndexOf(marker, StringComparison.Ordinal);
        if (index >= 0)
        {
            command = "Type " + text[(index + marker.Length)..].Trim();
            return true;
        }

        marker = "paste ";
        index = text.IndexOf(marker, StringComparison.Ordinal);
        if (index >= 0)
        {
            command = "Paste " + text[(index + marker.Length)..].Trim();
            return true;
        }

        command = string.Empty;
        return false;
    }

    private static bool TryKeyPress(string text, out string command)
    {
        if (text.Contains("press enter", StringComparison.Ordinal))
        {
            command = "Press Enter.";
            return true;
        }

        if (text.Contains("control shift tab", StringComparison.Ordinal) || text.Contains("ctrl shift tab", StringComparison.Ordinal))
        {
            command = "Press Ctrl+Shift+Tab.";
            return true;
        }

        if (text.Contains("control tab", StringComparison.Ordinal) || text.Contains("ctrl tab", StringComparison.Ordinal))
        {
            command = "Press Ctrl+Tab.";
            return true;
        }

        command = string.Empty;
        return false;
    }
}

internal static class SemanticIntentClassifier
{
    private static readonly SemanticExample[] Examples =
    [
        new("make this window bigger", "Maximize the active window."),
        new("make the current window bigger", "Maximize the active window."),
        new("make it bigger", "Maximize the active window."),
        new("make this full screen", "Maximize the active window."),
        new("full screen this window", "Maximize the active window."),
        new("expand this window", "Maximize the active window."),
        new("make this window smaller", "Minimize the active window."),
        new("make the current window smaller", "Minimize the active window."),
        new("hide this window", "Minimize the active window."),
        new("get this window out of the way", "Minimize the active window."),
        new("put the window back", "Restore the active window."),
        new("put this window back", "Restore the active window."),
        new("normal size window", "Restore the active window."),
        new("go back to the terminal", "Focus the terminal."),
        new("return to the terminal", "Focus the terminal."),
        new("show me the terminal", "Focus the terminal."),
        new("bring up the terminal", "Focus the terminal."),
        new("take me to the terminal", "Focus the terminal."),
        new("jump to the terminal", "Focus the terminal."),
        new("next shell tab", "Switch to the next terminal tab."),
        new("next command line tab", "Switch to the next terminal tab."),
        new("go to the next shell tab", "Switch to the next terminal tab."),
        new("previous shell tab", "Switch to the previous terminal tab."),
        new("last shell tab", "Switch to the previous terminal tab."),
        new("go to the previous shell tab", "Switch to the previous terminal tab."),
        new("other window", "Switch to the previous application."),
        new("last window", "Switch to the previous application."),
        new("previous window", "Switch to the previous application."),
        new("other app", "Switch to the previous application."),
        new("last app", "Switch to the previous application."),
        new("alt tab", "Switch to the previous application."),
        new("hit enter", "Press Enter."),
        new("return key", "Press Enter."),
        new("next tab", "Press Ctrl+Tab."),
        new("go to the next tab", "Press Ctrl+Tab."),
        new("previous tab", "Press Ctrl+Shift+Tab.", 0.92)
    ];

    public static bool TryClassify(string text, out CommandDecision decision)
    {
        text = Normalize(text);
        if (string.IsNullOrWhiteSpace(text))
        {
            decision = new CommandDecision(CommandDecisionKind.Ignore, string.Empty, string.Empty);
            return false;
        }

        if (TryTextEntry(text, out var command, out var reason)
            || TryStructuredIntent(text, out command, out reason)
            || TryExampleMatch(text, out command, out reason))
        {
            decision = new CommandDecision(CommandDecisionKind.Accept, command, reason);
            return true;
        }

        decision = new CommandDecision(CommandDecisionKind.Ignore, string.Empty, string.Empty);
        return false;
    }

    private static bool TryTextEntry(string text, out string command, out string reason)
    {
        var marker = FindTextEntryMarker(text);
        if (marker is null)
        {
            command = string.Empty;
            reason = string.Empty;
            return false;
        }

        var target = GetTextTarget(text);
        if (target == TextTarget.None)
        {
            command = string.Empty;
            reason = string.Empty;
            return false;
        }

        var payload = CleanTextPayload(text[marker.Value.EndIndex..]);
        if (string.IsNullOrWhiteSpace(payload))
        {
            command = string.Empty;
            reason = string.Empty;
            return false;
        }

        var noEnter = text.Contains("do not press enter", StringComparison.Ordinal)
            || text.Contains("dont press enter", StringComparison.Ordinal)
            || text.Contains("without pressing enter", StringComparison.Ordinal)
            || text.Contains("no enter", StringComparison.Ordinal);

        command = target == TextTarget.Terminal
            ? $"Type {payload} into the terminal{(noEnter ? " but do not press enter" : string.Empty)}."
            : $"Type {payload} into the active app.";
        reason = $"semantic local intent: text entry via '{marker.Value.Word}'";
        return true;
    }

    private static bool TryStructuredIntent(string text, out string command, out string reason)
    {
        if (HasTerminalReference(text) && HasTabReference(text) && HasPreviousReference(text))
        {
            command = "Switch to the previous terminal tab.";
            reason = "semantic local intent: previous terminal tab";
            return true;
        }

        if (HasTerminalReference(text) && HasTabReference(text) && HasNextReference(text))
        {
            command = "Switch to the next terminal tab.";
            reason = "semantic local intent: next terminal tab";
            return true;
        }

        if (HasTerminalReference(text)
            && ContainsAny(text, "go back", "return", "show", "bring up", "take me", "jump", "back to", "go to"))
        {
            command = "Focus the terminal.";
            reason = "semantic local intent: focus terminal";
            return true;
        }

        if (ContainsAny(text, "other window", "last window", "previous window", "other app", "last app", "previous app", "alt tab"))
        {
            command = "Switch to the previous application.";
            reason = "semantic local intent: app switch";
            return true;
        }

        if (TryWindowResize(text, out command, out reason))
        {
            return true;
        }

        if ((text.Contains("enter", StringComparison.Ordinal) || text.Contains("return", StringComparison.Ordinal))
            && ContainsAny(text, "hit", "key", "press", "submit"))
        {
            command = "Press Enter.";
            reason = "semantic local intent: enter key";
            return true;
        }

        if (HasTabReference(text) && HasNextReference(text))
        {
            command = "Press Ctrl+Tab.";
            reason = "semantic local intent: next tab";
            return true;
        }

        if (ContainsAny(text, "select that", "choose that", "tap that"))
        {
            command = "Click.";
            reason = "semantic local intent: click";
            return true;
        }

        command = string.Empty;
        reason = string.Empty;
        return false;
    }

    private static bool TryWindowResize(string text, out string command, out string reason)
    {
        if (!HasWindowReference(text))
        {
            command = string.Empty;
            reason = string.Empty;
            return false;
        }

        if (ContainsAny(text, "bigger", "larger", "maximize", "maximise", "full screen", "fullscreen", "expand"))
        {
            command = $"Maximize {GetWindowTargetPhrase(text)}.";
            reason = "semantic local intent: maximize window";
            return true;
        }

        if (ContainsAny(text, "smaller", "minimize", "minimise", "hide", "out of the way"))
        {
            command = $"Minimize {GetWindowTargetPhrase(text)}.";
            reason = "semantic local intent: minimize window";
            return true;
        }

        if (ContainsAny(text, "restore", "normal size", "put the window back", "put this window back", "put it back"))
        {
            command = $"Restore {GetWindowTargetPhrase(text)}.";
            reason = "semantic local intent: restore window";
            return true;
        }

        command = string.Empty;
        reason = string.Empty;
        return false;
    }

    private static bool TryExampleMatch(string text, out string command, out string reason)
    {
        var bestScore = 0d;
        SemanticExample? bestExample = null;
        foreach (var example in Examples)
        {
            var score = Similarity(text, example.NormalizedUtterance);
            if (score > bestScore)
            {
                bestScore = score;
                bestExample = example;
            }
        }

        if (bestExample is not null && bestScore >= bestExample.Value.MinimumScore)
        {
            command = bestExample.Value.Command;
            reason = $"semantic local intent: example match {bestScore:0.00}";
            return true;
        }

        command = string.Empty;
        reason = string.Empty;
        return false;
    }

    private static TextEntryMarker? FindTextEntryMarker(string text)
    {
        foreach (var word in new[] { "put", "write", "enter", "send" })
        {
            if (text.StartsWith(word + " ", StringComparison.Ordinal))
            {
                return new TextEntryMarker(word, word.Length + 1);
            }

            var marker = " " + word + " ";
            var index = text.IndexOf(marker, StringComparison.Ordinal);
            if (index >= 0)
            {
                return new TextEntryMarker(word, index + marker.Length);
            }
        }

        return null;
    }

    private static TextTarget GetTextTarget(string text)
    {
        if (HasTerminalReference(text))
        {
            return TextTarget.Terminal;
        }

        return ContainsAny(text, "here", "active app", "current app", "active window", "current window", "this app", "this window")
            ? TextTarget.Active
            : TextTarget.None;
    }

    private static string CleanTextPayload(string text)
    {
        var result = text.Trim();
        foreach (var marker in new[]
                 {
                     " into the terminal",
                     " in the terminal",
                     " to the terminal",
                     " into terminal",
                     " in terminal",
                     " to terminal",
                     " into the shell",
                     " in the shell",
                     " to the shell",
                     " into shell",
                     " in shell",
                     " to shell",
                     " into powershell",
                     " in powershell",
                     " to powershell",
                     " into the active app",
                     " in the active app",
                     " into the current app",
                     " in the current app",
                     " into the active window",
                     " in the active window",
                     " into the current window",
                     " in the current window",
                     " here",
                     " but do not press enter",
                     " dont press enter",
                     " do not press enter",
                     " without pressing enter",
                     " no enter"
                 })
        {
            var index = result.IndexOf(marker, StringComparison.Ordinal);
            if (index >= 0)
            {
                result = result[..index].Trim();
            }
        }

        foreach (var prefix in new[] { "the text ", "text ", "this text " })
        {
            if (result.StartsWith(prefix, StringComparison.Ordinal))
            {
                result = result[prefix.Length..].Trim();
            }
        }

        return result.Trim('"');
    }

    private static bool HasTerminalReference(string text)
    {
        return ContainsAny(text, "terminal", "powershell", "shell", "command line", "console", "prompt");
    }

    private static bool HasBrowserReference(string text)
    {
        return ContainsAny(text, "browser", "chrome");
    }

    private static bool HasWindowReference(string text)
    {
        return HasTerminalReference(text)
            || HasBrowserReference(text)
            || ContainsAny(text, "window", "app", "active", "current", "this", "it");
    }

    private static bool HasTabReference(string text)
    {
        return text.Contains("tab", StringComparison.Ordinal);
    }

    private static bool HasNextReference(string text)
    {
        return ContainsAny(text, "next", "forward", "advance", "right");
    }

    private static bool HasPreviousReference(string text)
    {
        return ContainsAny(text, "previous", "last", "back", "left");
    }

    private static string GetWindowTargetPhrase(string text)
    {
        if (HasTerminalReference(text))
        {
            return "the terminal";
        }

        return HasBrowserReference(text) ? "the browser" : "the active window";
    }

    private static bool ContainsAny(string text, params string[] values)
    {
        return values.Any(value => text.Contains(value, StringComparison.Ordinal));
    }

    private static double Similarity(string left, string right)
    {
        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            return 1d;
        }

        var leftTokens = left.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var rightTokens = right.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (leftTokens.Length == 0 || rightTokens.Length == 0)
        {
            return 0d;
        }

        var shared = leftTokens.Intersect(rightTokens, StringComparer.Ordinal).Count();
        var dice = (2d * shared) / (leftTokens.Length + rightTokens.Length);

        return left.Contains(right, StringComparison.Ordinal) || right.Contains(left, StringComparison.Ordinal)
            ? Math.Max(dice, 0.95d)
            : dice;
    }

    private static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var ch in text.ToLowerInvariant())
        {
            builder.Append(char.IsLetterOrDigit(ch) || char.IsWhiteSpace(ch) ? ch : ' ');
        }

        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private readonly record struct SemanticExample(string Utterance, string Command, double MinimumScore = 0.80)
    {
        public string NormalizedUtterance { get; } = Normalize(Utterance);
    }

    private readonly record struct TextEntryMarker(string Word, int EndIndex);

    private enum TextTarget
    {
        None,
        Active,
        Terminal
    }
}

internal static class CodexCommandClassifier
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(25);

    public static CommandDecision Classify(string originalTranscript, string normalizedTranscript)
    {
        var outputPath = Path.Combine(Path.GetTempPath(), $"voicecodex-classify-{Guid.NewGuid():N}.json");
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            process.StartInfo.ArgumentList.Add("-NoProfile");
            process.StartInfo.ArgumentList.Add("-ExecutionPolicy");
            process.StartInfo.ArgumentList.Add("Bypass");
            process.StartInfo.ArgumentList.Add("-Command");
            process.StartInfo.ArgumentList.Add(BuildCommand(outputPath));

            process.Start();
            process.StandardInput.Write(BuildPrompt(originalTranscript));
            process.StandardInput.Close();

            if (!process.WaitForExit((int)Timeout.TotalMilliseconds))
            {
                TryKill(process);
                return FallbackClassify(normalizedTranscript, "Codex classifier timed out");
            }

            if (!File.Exists(outputPath))
            {
                return FallbackClassify(normalizedTranscript, "Codex classifier produced no output");
            }

            return ParseDecision(File.ReadAllText(outputPath), normalizedTranscript);
        }
        catch (Exception ex)
        {
            return FallbackClassify(normalizedTranscript, $"Codex classifier failed: {ex.Message}");
        }
        finally
        {
            TryDelete(outputPath);
        }
    }

    private static string BuildCommand(string outputPath)
    {
        return string.Join(
            " ",
            "codex",
            "-m gpt-5.5",
            "-c model_reasoning_effort=\"low\"",
            "-c service_tier=\"fast\"",
            "-s read-only",
            "-a never",
            "exec",
            "--skip-git-repo-check",
            "-C",
            QuotePowerShellString(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
            "-o",
            QuotePowerShellString(outputPath),
            "-");
    }

    private static string BuildPrompt(string transcript)
    {
        return
            """
            Classify this VoiceCodex speech transcript.

            Return only JSON with this exact shape:
            {"action":"accept"|"ignore","command":"...","reason":"..."}

            Accept only if the user is asking the computer to do something, such as switching apps or terminal tabs, focusing a window, typing/pasting text, pressing keys, clicking, opening/running something, or managing an existing terminal/Codex session.
            Ignore casual speech, mic tests, conversation, status questions, and incomplete fragments that do not contain a usable command.
            If accepted, rewrite command as a direct imperative without filler words. Preserve important constraints like "do not press enter".

            Transcript:
            """ + Environment.NewLine + transcript + Environment.NewLine;
    }

    private static string QuotePowerShellString(string value)
    {
        return "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    }

    private static CommandDecision ParseDecision(string output, string normalizedTranscript)
    {
        var json = ExtractJson(output);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var action = root.TryGetProperty("action", out var actionElement) ? actionElement.GetString() : "ignore";
        var command = root.TryGetProperty("command", out var commandElement) ? commandElement.GetString() ?? string.Empty : string.Empty;
        var reason = root.TryGetProperty("reason", out var reasonElement) ? reasonElement.GetString() ?? "classified by Codex" : "classified by Codex";

        if (string.Equals(action, "accept", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(command))
        {
            return new CommandDecision(CommandDecisionKind.Accept, command.Trim(), $"Codex classifier: {reason}");
        }

        return new CommandDecision(CommandDecisionKind.Ignore, string.Empty, $"Codex classifier: {reason}");
    }

    private static CommandDecision FallbackClassify(string normalizedTranscript, string reason)
    {
        var text = CommandGateFallback.NormalizeForAction(normalizedTranscript);
        return CommandGateFallback.LooksLikeAction(text)
            ? new CommandDecision(CommandDecisionKind.Accept, text, $"{reason}; fallback accepted likely command")
            : new CommandDecision(CommandDecisionKind.Ignore, string.Empty, $"{reason}; fallback ignored");
    }

    private static string ExtractJson(string output)
    {
        var start = output.IndexOf('{');
        var end = output.LastIndexOf('}');
        if (start < 0 || end < start)
        {
            throw new InvalidOperationException("No JSON object found.");
        }

        return output[start..(end + 1)];
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
        }
    }
}

internal static class CommandGateFallback
{
    private static readonly string[] ActionWords =
    [
        "switch",
        "focus",
        "open",
        "close",
        "type",
        "paste",
        "enter",
        "press",
        "send",
        "run",
        "start",
        "stop",
        "click",
        "move",
        "scroll",
        "go to",
        "change",
        "create",
        "edit",
        "delete",
        "find",
        "fix",
        "look",
        "search",
        "investigate",
        "review",
        "check",
        "build",
        "test",
        "install",
        "commit"
    ];

    public static string NormalizeForAction(string text)
    {
        foreach (var prefix in new[] { "great ", "okay ", "ok ", "alright ", "please ", "could you ", "can you ", "would you " })
        {
            if (text.StartsWith(prefix, StringComparison.Ordinal))
            {
                text = text[prefix.Length..].Trim();
            }
        }

        return text;
    }

    public static bool StartsWithAction(string text)
    {
        return ActionWords.Any(word => text == word || text.StartsWith(word + " ", StringComparison.Ordinal));
    }

    public static bool LooksLikeAction(string text)
    {
        return ActionWords.Any(word => text == word
            || text.StartsWith(word + " ", StringComparison.Ordinal)
            || text.Contains(" " + word + " ", StringComparison.Ordinal));
    }
}

internal static class LocalCommandExecutor
{
    public static bool WouldHandle(string commandText)
    {
        var normalized = Normalize(commandText);
        return IsOpenChromeCommand(normalized)
            || IsOpenBrowserCommand(normalized)
            || WouldHandleWindowManagement(normalized)
            || WouldHandleMouseClick(normalized)
            || WouldHandleTerminalControl(normalized)
            || WouldHandleAppSwitch(normalized)
            || normalized.Contains("press enter", StringComparison.Ordinal)
            || normalized.Contains("ctrl shift tab", StringComparison.Ordinal)
            || normalized.Contains("control shift tab", StringComparison.Ordinal)
            || normalized.Contains("ctrl tab", StringComparison.Ordinal)
            || normalized.Contains("control tab", StringComparison.Ordinal)
            || WouldHandleActiveTextEntry(normalized)
            || WouldHandleTerminalTextEntry(normalized);
    }

    public static bool TryExecute(string commandText, out string result)
    {
        var normalized = Normalize(commandText);
        if (IsOpenChromeCommand(normalized) || IsOpenBrowserCommand(normalized))
        {
            if (TryOpenChrome())
            {
                result = "Opened Chrome.";
                return true;
            }

            OpenDefaultBrowser();
            result = "Opened the default browser.";
            return true;
        }

        if (TryWindowManagement(normalized, out result)
            || TryMouseClick(normalized, out result)
            || TryTerminalControl(normalized, out result)
            || TryAppSwitch(normalized, out result)
            || TryKeyPress(normalized, out result)
            || TryActiveTextEntry(commandText, normalized, out result)
            || TryTerminalTextEntry(commandText, normalized, out result))
        {
            return true;
        }

        result = string.Empty;
        return false;
    }

    private static bool IsOpenChromeCommand(string normalized)
    {
        return normalized.Contains("open", StringComparison.Ordinal)
            && normalized.Contains("chrome", StringComparison.Ordinal);
    }

    private static bool IsOpenBrowserCommand(string normalized)
    {
        return normalized.Contains("open", StringComparison.Ordinal)
            && normalized.Contains("browser", StringComparison.Ordinal)
            && !normalized.Contains("edge", StringComparison.Ordinal)
            && !normalized.Contains("firefox", StringComparison.Ordinal);
    }

    private static bool WouldHandleTerminalControl(string normalized)
    {
        return normalized.Contains("terminal tab", StringComparison.Ordinal)
            || normalized.Contains("ctrl tab", StringComparison.Ordinal)
            || normalized.Contains("control tab", StringComparison.Ordinal)
            || (normalized.Contains("focus", StringComparison.Ordinal)
                && (normalized.Contains("terminal", StringComparison.Ordinal) || normalized.Contains("powershell", StringComparison.Ordinal)));
    }

    private static bool WouldHandleWindowManagement(string normalized)
    {
        if (normalized.Contains("max", StringComparison.Ordinal)
            || normalized.Contains("minimize", StringComparison.Ordinal)
            || normalized.Contains("restore", StringComparison.Ordinal))
        {
            return normalized.Contains("window", StringComparison.Ordinal)
                || normalized.Contains("app", StringComparison.Ordinal)
                || normalized.Contains("terminal", StringComparison.Ordinal)
                || normalized.Contains("powershell", StringComparison.Ordinal)
                || normalized.Contains("browser", StringComparison.Ordinal)
                || normalized.Contains("chrome", StringComparison.Ordinal);
        }

        return normalized.Contains("bring", StringComparison.Ordinal)
            && normalized.Contains("front", StringComparison.Ordinal)
            && (normalized.Contains("terminal", StringComparison.Ordinal) || normalized.Contains("powershell", StringComparison.Ordinal));
    }

    private static bool WouldHandleMouseClick(string normalized)
    {
        return normalized.Contains("click", StringComparison.Ordinal);
    }

    private static bool WouldHandleAppSwitch(string normalized)
    {
        return normalized.Contains("switch", StringComparison.Ordinal)
            && (normalized.Contains("application", StringComparison.Ordinal) || normalized.Contains("app", StringComparison.Ordinal) || normalized.Contains("window", StringComparison.Ordinal));
    }

    private static bool WouldHandleActiveTextEntry(string normalized)
    {
        return (normalized.Contains("type", StringComparison.Ordinal) || normalized.Contains("paste", StringComparison.Ordinal))
            && !normalized.Contains("password", StringComparison.Ordinal)
            && !normalized.Contains("terminal", StringComparison.Ordinal);
    }

    private static bool WouldHandleTerminalTextEntry(string normalized)
    {
        return (normalized.Contains("type", StringComparison.Ordinal) || normalized.Contains("paste", StringComparison.Ordinal))
            && !normalized.Contains("password", StringComparison.Ordinal);
    }

    private static bool TryOpenChrome()
    {
        foreach (var path in GetChromeCandidates())
        {
            if (!File.Exists(path))
            {
                continue;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
            return true;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "chrome.exe",
                UseShellExecute = true
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static IEnumerable<string> GetChromeCandidates()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        yield return Path.Combine(programFiles, "Google", "Chrome", "Application", "chrome.exe");
        yield return Path.Combine(programFilesX86, "Google", "Chrome", "Application", "chrome.exe");
        yield return Path.Combine(localAppData, "Google", "Chrome", "Application", "chrome.exe");
    }

    private static void OpenDefaultBrowser()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "https://www.google.com",
            UseShellExecute = true
        });
    }

    private static bool TryWindowManagement(string normalized, out string result)
    {
        var showCommand = GetShowCommand(normalized);
        var target = GetWindowTarget(normalized);
        if (showCommand is null || target == WindowTarget.None)
        {
            result = string.Empty;
            return false;
        }

        var hwnd = target switch
        {
            WindowTarget.Terminal => FindProcessWindow(["WindowsTerminal", "powershell", "pwsh", "cmd"]),
            WindowTarget.Browser => FindProcessWindow(["chrome", "msedge", "firefox"]),
            WindowTarget.Active => GetForegroundWindow(),
            _ => IntPtr.Zero
        };

        if (hwnd == IntPtr.Zero)
        {
            result = "No matching window was found.";
            return true;
        }

        ShowWindow(hwnd, showCommand.Value);
        SetForegroundWindow(hwnd);
        Thread.Sleep(80);
        result = showCommand.Value switch
        {
            ShowMaximized => target == WindowTarget.Terminal ? "Focused and maximized the terminal." : "Maximized the window.",
            ShowMinimized => target == WindowTarget.Terminal ? "Minimized the terminal." : "Minimized the window.",
            ShowRestore => target == WindowTarget.Terminal ? "Restored the terminal." : "Restored the window.",
            _ => "Updated the window."
        };
        return true;
    }

    private static bool TryMouseClick(string normalized, out string result)
    {
        if (!normalized.Contains("click", StringComparison.Ordinal))
        {
            result = string.Empty;
            return false;
        }

        var match = Regex.Match(normalized, @"click at (?<x>\d{1,5}) (?<y>\d{1,5})", RegexOptions.CultureInvariant);
        if (match.Success
            && int.TryParse(match.Groups["x"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var x)
            && int.TryParse(match.Groups["y"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var y))
        {
            SetCursorPos(x, y);
        }
        else if ((normalized.Contains("center", StringComparison.Ordinal) || normalized.Contains("middle", StringComparison.Ordinal))
            && TryGetForegroundWindowCenter(out x, out y))
        {
            SetCursorPos(x, y);
        }

        if (normalized.Contains("right click", StringComparison.Ordinal))
        {
            MouseClick(MouseEventRightDown, MouseEventRightUp);
            result = "Right clicked.";
            return true;
        }

        if (normalized.Contains("double click", StringComparison.Ordinal))
        {
            MouseClick(MouseEventLeftDown, MouseEventLeftUp);
            Thread.Sleep(60);
            MouseClick(MouseEventLeftDown, MouseEventLeftUp);
            result = "Double clicked.";
            return true;
        }

        MouseClick(MouseEventLeftDown, MouseEventLeftUp);
        result = "Clicked.";
        return true;
    }

    private static bool TryTerminalControl(string normalized, out string result)
    {
        if (normalized.Contains("terminal tab", StringComparison.Ordinal) && normalized.Contains("previous", StringComparison.Ordinal))
        {
            FocusTerminalWindow();
            SendKeys.SendWait("^+{TAB}");
            result = "Switched to the previous terminal tab.";
            return true;
        }

        if (normalized.Contains("terminal tab", StringComparison.Ordinal) || normalized.Contains("ctrl tab", StringComparison.Ordinal) || normalized.Contains("control tab", StringComparison.Ordinal))
        {
            FocusTerminalWindow();
            SendKeys.SendWait("^{TAB}");
            result = "Switched to the next terminal tab.";
            return true;
        }

        if (normalized.Contains("focus", StringComparison.Ordinal)
            && (normalized.Contains("terminal", StringComparison.Ordinal) || normalized.Contains("powershell", StringComparison.Ordinal)))
        {
            FocusTerminalWindow();
            result = "Focused the terminal.";
            return true;
        }

        result = string.Empty;
        return false;
    }

    private static bool TryAppSwitch(string normalized, out string result)
    {
        if (normalized.Contains("switch", StringComparison.Ordinal)
            && (normalized.Contains("application", StringComparison.Ordinal) || normalized.Contains("app", StringComparison.Ordinal) || normalized.Contains("window", StringComparison.Ordinal)))
        {
            SendKeys.SendWait("%{TAB}");
            result = "Switched application.";
            return true;
        }

        result = string.Empty;
        return false;
    }

    private static bool TryTerminalTextEntry(string original, string normalized, out string result)
    {
        var lower = original.ToLowerInvariant();
        var typeIndex = lower.IndexOf("type ", StringComparison.Ordinal);
        var pasteIndex = lower.IndexOf("paste ", StringComparison.Ordinal);
        var markerIndex = typeIndex >= 0 ? typeIndex + "type ".Length : pasteIndex >= 0 ? pasteIndex + "paste ".Length : -1;
        if (markerIndex < 0 || normalized.Contains("password", StringComparison.Ordinal))
        {
            result = string.Empty;
            return false;
        }

        var text = original[markerIndex..].Trim();
        text = RemoveTrailingTerminalTarget(text);
        if (string.IsNullOrWhiteSpace(text))
        {
            result = string.Empty;
            return false;
        }

        var pressEnter = !normalized.Contains("do not press enter", StringComparison.Ordinal)
            && !normalized.Contains("dont press enter", StringComparison.Ordinal);

        FocusTerminalWindow();
        Clipboard.SetText(text);
        SendKeys.SendWait("^v");
        if (pressEnter)
        {
            SendKeys.SendWait("{ENTER}");
        }

        result = pressEnter ? "Typed text into the terminal and pressed Enter." : "Typed text into the terminal.";
        return true;
    }

    private static bool TryActiveTextEntry(string original, string normalized, out string result)
    {
        if (!WouldHandleActiveTextEntry(normalized))
        {
            result = string.Empty;
            return false;
        }

        var lower = original.ToLowerInvariant();
        var typeIndex = lower.IndexOf("type ", StringComparison.Ordinal);
        var pasteIndex = lower.IndexOf("paste ", StringComparison.Ordinal);
        var markerIndex = typeIndex >= 0 ? typeIndex + "type ".Length : pasteIndex >= 0 ? pasteIndex + "paste ".Length : -1;
        if (markerIndex < 0)
        {
            result = string.Empty;
            return false;
        }

        var text = RemoveTrailingActiveTarget(original[markerIndex..].Trim());
        if (string.IsNullOrWhiteSpace(text))
        {
            result = string.Empty;
            return false;
        }

        var pressEnter = normalized.Contains("press enter", StringComparison.Ordinal)
            && !normalized.Contains("do not press enter", StringComparison.Ordinal)
            && !normalized.Contains("dont press enter", StringComparison.Ordinal);

        Clipboard.SetText(text);
        SendKeys.SendWait("^v");
        if (pressEnter)
        {
            SendKeys.SendWait("{ENTER}");
        }

        result = pressEnter ? "Typed text into the active app and pressed Enter." : "Typed text into the active app.";
        return true;
    }

    private static bool TryKeyPress(string normalized, out string result)
    {
        if (normalized.Contains("press enter", StringComparison.Ordinal) || normalized is "enter")
        {
            SendKeys.SendWait("{ENTER}");
            result = "Pressed Enter.";
            return true;
        }

        if (normalized.Contains("control shift tab", StringComparison.Ordinal) || normalized.Contains("ctrl shift tab", StringComparison.Ordinal))
        {
            SendKeys.SendWait("^+{TAB}");
            result = "Pressed Ctrl+Shift+Tab.";
            return true;
        }

        if (normalized.Contains("control tab", StringComparison.Ordinal) || normalized.Contains("ctrl tab", StringComparison.Ordinal))
        {
            SendKeys.SendWait("^{TAB}");
            result = "Pressed Ctrl+Tab.";
            return true;
        }

        result = string.Empty;
        return false;
    }

    private static string RemoveTrailingTerminalTarget(string text)
    {
        foreach (var marker in new[] { " into the terminal", " in the terminal", " to the terminal" })
        {
            var index = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                return text[..index].Trim().Trim('"');
            }
        }

        return text.Trim().Trim('"');
    }

    private static string RemoveTrailingActiveTarget(string text)
    {
        foreach (var marker in new[]
                 {
                     " into the active app",
                     " in the active app",
                     " into the current app",
                     " in the current app",
                     " into the active window",
                     " in the active window",
                     " into the current window",
                     " in the current window",
                     " here"
                 })
        {
            var index = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                return text[..index].Trim().Trim('"');
            }
        }

        return text.Trim().Trim('"');
    }

    private static void FocusTerminalWindow()
    {
        var hwnd = FindProcessWindow(["WindowsTerminal", "powershell", "pwsh", "cmd"]);
        if (hwnd != IntPtr.Zero)
        {
            ShowWindow(hwnd, ShowNormal);
            SetForegroundWindow(hwnd);
            Thread.Sleep(100);
        }
    }

    private static IntPtr FindProcessWindow(string[] processNames)
    {
        return processNames
            .SelectMany(Process.GetProcessesByName)
            .Where(process => process.MainWindowHandle != IntPtr.Zero)
            .OrderByDescending(process => SafeStartTime(process))
            .Select(process => process.MainWindowHandle)
            .FirstOrDefault();
    }

    private static WindowTarget GetWindowTarget(string normalized)
    {
        if (normalized.Contains("terminal", StringComparison.Ordinal) || normalized.Contains("powershell", StringComparison.Ordinal))
        {
            return WindowTarget.Terminal;
        }

        if (normalized.Contains("browser", StringComparison.Ordinal) || normalized.Contains("chrome", StringComparison.Ordinal))
        {
            return WindowTarget.Browser;
        }

        return normalized.Contains("window", StringComparison.Ordinal)
            || normalized.Contains("app", StringComparison.Ordinal)
            || normalized.Contains("active", StringComparison.Ordinal)
            || normalized.Contains("current", StringComparison.Ordinal)
            ? WindowTarget.Active
            : WindowTarget.None;
    }

    private static int? GetShowCommand(string normalized)
    {
        if (normalized.Contains("minimize", StringComparison.Ordinal))
        {
            return ShowMinimized;
        }

        if (normalized.Contains("restore", StringComparison.Ordinal))
        {
            return ShowRestore;
        }

        if (normalized.Contains("max", StringComparison.Ordinal)
            || (normalized.Contains("bring", StringComparison.Ordinal) && normalized.Contains("front", StringComparison.Ordinal)))
        {
            return ShowMaximized;
        }

        return null;
    }

    private static bool TryGetForegroundWindowCenter(out int x, out int y)
    {
        var hwnd = GetForegroundWindow();
        if (hwnd != IntPtr.Zero && GetWindowRect(hwnd, out var rect))
        {
            x = rect.Left + ((rect.Right - rect.Left) / 2);
            y = rect.Top + ((rect.Bottom - rect.Top) / 2);
            return true;
        }

        x = 0;
        y = 0;
        return false;
    }

    private static void MouseClick(uint down, uint up)
    {
        mouse_event(down, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(30);
        mouse_event(up, 0, 0, 0, UIntPtr.Zero);
    }

    private static DateTime SafeStartTime(Process process)
    {
        try
        {
            return process.StartTime;
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    private static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var ch in text.ToLowerInvariant())
        {
            builder.Append(char.IsLetterOrDigit(ch) || char.IsWhiteSpace(ch) ? ch : ' ');
        }

        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private const int ShowNormal = 1;
    private const int ShowMinimized = 6;
    private const int ShowRestore = 9;
    private const int ShowMaximized = 3;
    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
    private const uint MouseEventRightDown = 0x0008;
    private const uint MouseEventRightUp = 0x0010;

    private enum WindowTarget
    {
        None,
        Active,
        Terminal,
        Browser
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);
}

internal static class CodexController
{
    private const string ControllerTitle = "VoiceCodex Controller";
    private static IntPtr controllerHwnd;

    public static void EnsureRunning()
    {
        if (IsWindow(controllerHwnd))
        {
            return;
        }

        if (TryFindControllerWindow(out controllerHwnd))
        {
            return;
        }

        var startedAt = DateTime.Now;
        StartControllerTerminal();

        for (var i = 0; i < 120; i++)
        {
            Thread.Sleep(300);
            if (TryFindControllerWindow(out controllerHwnd)
                || TryFindNewWindowsTerminal(startedAt, out controllerHwnd)
                || TryFindAnyWindowsTerminal(out controllerHwnd))
            {
                Thread.Sleep(6000);
                return;
            }
        }

        throw new InvalidOperationException("Timed out waiting for the controller window.");
    }

    public static void SendInstruction(string commandText)
    {
        EnsureRunning();
        if (!IsWindow(controllerHwnd))
        {
            throw new InvalidOperationException("Controller window was not found.");
        }

        Clipboard.SetText($"VoiceCodex command: {commandText}{Environment.NewLine}Only perform this command. If anything is ambiguous, ask a brief clarification instead of taking unrelated action.");
        ShowWindow(controllerHwnd, ShowNormal);
        SetForegroundWindow(controllerHwnd);
        Thread.Sleep(250);
        SendKeys.SendWait("^v");
        Thread.Sleep(80);
        SendKeys.SendWait("{ENTER}");
    }

    private static void StartControllerTerminal()
    {
        var scriptPath = EnsureControllerScript();
        if (TryStartWindowsTerminal())
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoExit -ExecutionPolicy Bypass -File {QuoteCommandLineArgument(scriptPath)}",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Normal
        });
    }

    private static bool TryStartWindowsTerminal()
    {
        try
        {
            var scriptPath = EnsureControllerScript();
            Process.Start(new ProcessStartInfo
            {
                FileName = GetWindowsTerminalPath(),
                Arguments = $"new-tab --title {QuoteCommandLineArgument(ControllerTitle)} powershell.exe -NoExit -ExecutionPolicy Bypass -File {QuoteCommandLineArgument(scriptPath)}",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Normal
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string EnsureControllerScript()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VoiceCodex");
        Directory.CreateDirectory(directory);

        var scriptPath = Path.Combine(directory, "start-controller.ps1");
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $host.UI.RawUI.WindowTitle = '{{ControllerTitle}}'
            $codex = Join-Path $env:APPDATA 'npm\codex.ps1'
            $prompt = @'
            You are the persistent VoiceCodex Controller for this Windows computer.
            The tray app pastes one command at a time. Only perform the latest pasted command.
            For app launches on Windows, prefer Start-Process with a real executable path or a known executable name such as chrome.exe, msedge.exe, not URI-like strings.
            For Chrome, try:
            C:\Program Files\Google\Chrome\Application\chrome.exe
            C:\Program Files (x86)\Google\Chrome\Application\chrome.exe
            $env:LOCALAPPDATA\Google\Chrome\Application\chrome.exe
            If a command is ambiguous, ask one brief clarification instead of taking unrelated action.
            '@
            if (Test-Path -LiteralPath $codex) {
                & $codex -m 'gpt-5.5' -c 'model_reasoning_effort="low"' -c 'service_tier="fast"' -s danger-full-access -a never $prompt
            } else {
                codex -m 'gpt-5.5' -c 'model_reasoning_effort="low"' -c 'service_tier="fast"' -s danger-full-access -a never $prompt
            }
            """;

        File.WriteAllText(scriptPath, script, Encoding.UTF8);
        return scriptPath;
    }

    private static bool TryFindControllerWindow(out IntPtr hwnd)
    {
        var foundHwnd = IntPtr.Zero;
        EnumWindows((candidate, _) =>
        {
            var title = GetWindowTitle(candidate);
            if (title.Contains(ControllerTitle, StringComparison.OrdinalIgnoreCase))
            {
                foundHwnd = candidate;
                return false;
            }

            return true;
        }, IntPtr.Zero);

        hwnd = foundHwnd;
        return hwnd != IntPtr.Zero;
    }

    private static bool TryFindNewWindowsTerminal(DateTime startedAt, out IntPtr hwnd)
    {
        hwnd = Process.GetProcessesByName("WindowsTerminal")
            .Where(process =>
            {
                try
                {
                    return process.MainWindowHandle != IntPtr.Zero && process.StartTime >= startedAt.AddSeconds(-2);
                }
                catch
                {
                    return false;
                }
            })
            .OrderByDescending(process =>
            {
                try
                {
                    return process.StartTime;
                }
                catch
                {
                    return DateTime.MinValue;
                }
            })
            .Select(process => process.MainWindowHandle)
            .FirstOrDefault();

        return hwnd != IntPtr.Zero;
    }

    private static bool TryFindAnyWindowsTerminal(out IntPtr hwnd)
    {
        hwnd = Process.GetProcessesByName("WindowsTerminal")
            .Where(process => process.MainWindowHandle != IntPtr.Zero)
            .OrderByDescending(process =>
            {
                try
                {
                    return process.StartTime;
                }
                catch
                {
                    return DateTime.MinValue;
                }
            })
            .Select(process => process.MainWindowHandle)
            .FirstOrDefault();

        return hwnd != IntPtr.Zero;
    }

    private static string GetWindowsTerminalPath()
    {
        var appAliasPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft",
            "WindowsApps",
            "wt.exe");

        return File.Exists(appAliasPath) ? appAliasPath : "wt.exe";
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        var length = GetWindowTextLength(hwnd);
        if (length <= 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(length + 1);
        _ = GetWindowText(hwnd, builder, builder.Capacity);
        return builder.ToString();
    }

    private static string QuotePowerShellArgument(string value)
    {
        return "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    }

    private static string QuoteCommandLineArgument(string value)
    {
        return "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    private static string QuotePowerShellString(string value)
    {
        return "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private const int ShowNormal = 1;

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}

internal static class AppPaths
{
    public static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VoiceCodex",
        "logs");
}

internal sealed record BenchmarkCase(int Index, string Request, string Transcript, string Decision, string Route, long TranscribeMs, long ClassifyMs, long CompleteMs, long TotalMs);

internal static class BenchmarkRunner
{
    private static readonly string[] Requests =
    [
        "this is a test one two",
        "can you hear me",
        "what can I say",
        "open the chrome browser",
        "open chrome",
        "great could you switch the current terminal tab",
        "switch to the next terminal tab",
        "switch to the previous terminal tab",
        "switch windows",
        "focus the terminal",
        "bring the terminal to the front and maximize it",
        "maximize the active window",
        "minimize the active window",
        "restore the active window",
        "switch to the previous application",
        "type hello world into the terminal but do not press enter",
        "paste npm run build into the terminal",
        "type hello into the active app",
        "paste git status here",
        "press enter",
        "press control tab",
        "click",
        "double click",
        "right click",
        "click center",
        "click at 300 400",
        "open the browser",
        "could you focus powershell",
        "testing testing",
        "what is your name",
        "please type git status",
        "switch to the other window"
    ];

    public static async Task Run(bool playAudio)
    {
        var workingDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VoiceCodex",
            "benchmarks",
            DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(workingDirectory);

        using var transcriber = await BenchmarkTranscriber.CreateAsync(WhisperModelSize.TinyEn);
        var rows = new List<BenchmarkCase>();

        Console.WriteLine("| # | Request | Transcript | Decision | Route | STT ms | Classify ms | Complete ms | Total ms |");
        Console.WriteLine("|---:|---|---|---|---|---:|---:|---:|---:|");

        for (var i = 0; i < Requests.Length; i++)
        {
            var request = Requests[i];
            var wavPath = Path.Combine(workingDirectory, $"{i + 1:00}.wav");
            SynthesizeToWav(request, wavPath);

            if (playAudio)
            {
                PlayWav(wavPath);
            }

            var total = Stopwatch.StartNew();
            var stt = Stopwatch.StartNew();
            var transcript = await transcriber.TranscribeAsync(wavPath);
            stt.Stop();

            var classify = Stopwatch.StartNew();
            var decision = CommandGate.Evaluate(transcript);
            classify.Stop();

            var complete = Stopwatch.StartNew();
            var route = CompleteDryRun(decision);
            complete.Stop();
            total.Stop();

            var row = new BenchmarkCase(
                i + 1,
                request,
                transcript,
                decision.Kind == CommandDecisionKind.Accept ? $"Accept: {decision.CommandText}" : decision.Kind.ToString(),
                route,
                stt.ElapsedMilliseconds,
                classify.ElapsedMilliseconds,
                complete.ElapsedMilliseconds,
                total.ElapsedMilliseconds);
            rows.Add(row);

            Console.WriteLine($"| {row.Index} | {Escape(row.Request)} | {Escape(row.Transcript)} | {Escape(row.Decision)} | {Escape(row.Route)} | {row.TranscribeMs} | {row.ClassifyMs} | {row.CompleteMs} | {row.TotalMs} |");
        }

        var averages = new
        {
            Transcribe = rows.Average(row => row.TranscribeMs),
            Classify = rows.Average(row => row.ClassifyMs),
            Complete = rows.Average(row => row.CompleteMs),
            Total = rows.Average(row => row.TotalMs)
        };
        Console.WriteLine();
        Console.WriteLine($"Averages: STT {averages.Transcribe:0} ms, classify {averages.Classify:0} ms, complete {averages.Complete:0} ms, total {averages.Total:0} ms.");
        Console.WriteLine($"Audio files: {workingDirectory}");
    }

    private static string CompleteDryRun(CommandDecision decision)
    {
        return decision.Kind switch
        {
            CommandDecisionKind.LocalResponse => "local response",
            CommandDecisionKind.Ignore => "no action",
            CommandDecisionKind.Accept when LocalCommandExecutor.WouldHandle(decision.CommandText) => "local executor",
            CommandDecisionKind.Accept => "controller route",
            _ => "unknown"
        };
    }

    private static void SynthesizeToWav(string text, string wavPath)
    {
        using var synthesizer = new SpeechSynthesizer();
        var format = new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono);
        synthesizer.SetOutputToWaveFile(wavPath, format);
        synthesizer.Speak(text);
        synthesizer.SetOutputToNull();
    }

    private static void PlayWav(string wavPath)
    {
        using var reader = new WaveFileReader(wavPath);
        using var output = new WaveOutEvent();
        output.Init(reader);
        output.Play();
        while (output.PlaybackState == PlaybackState.Playing)
        {
            Thread.Sleep(20);
        }
    }

    private static string Escape(string value)
    {
        return value.Replace("|", "\\|", StringComparison.Ordinal).Replace(Environment.NewLine, " ", StringComparison.Ordinal);
    }
}

internal sealed class BenchmarkTranscriber : IDisposable
{
    private readonly WhisperFactory factory;

    private BenchmarkTranscriber(WhisperFactory factory)
    {
        this.factory = factory;
    }

    public static async Task<BenchmarkTranscriber> CreateAsync(WhisperModelSize modelSize)
    {
        var modelPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VoiceCodex",
            "models",
            modelSize.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);
        if (!File.Exists(modelPath))
        {
            await using var modelStream = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(modelSize.GgmlType);
            await using var fileWriter = File.Open(modelPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await modelStream.CopyToAsync(fileWriter);
        }

        return new BenchmarkTranscriber(WhisperFactory.FromPath(modelPath));
    }

    public async Task<string> TranscribeAsync(string wavPath)
    {
        using var processor = factory.CreateBuilder()
            .WithLanguage("en")
            .Build();
        await using var fileStream = File.OpenRead(wavPath);
        var builder = new StringBuilder();

        await foreach (var result in processor.ProcessAsync(fileStream))
        {
            builder.Append(result.Text);
        }

        return builder.ToString().Trim();
    }

    public void Dispose()
    {
        factory.Dispose();
    }
}

internal sealed class AppSettings
{
    public bool VoiceResponsesEnabled { get; set; } = true;

    public string WhisperModelName { get; set; } = WhisperModelSize.TinyEn.Name;

    public int DefaultsVersion { get; set; }

    [JsonIgnore]
    public WhisperModelSize WhisperModel
    {
        get => WhisperModelSize.FromName(WhisperModelName);
        set => WhisperModelName = value.Name;
    }

    public bool ApplyFastDefaults()
    {
        if (DefaultsVersion >= 1)
        {
            return false;
        }

        WhisperModel = WhisperModelSize.TinyEn;
        DefaultsVersion = 1;
        return true;
    }
}

internal readonly record struct WhisperModelSize(string Name, string DisplayName, string SpokenName, string FileName, GgmlType GgmlType)
{
    public static readonly WhisperModelSize TinyEn = new("tiny.en", "tiny.en", "tiny English", "ggml-tiny.en.bin", GgmlType.TinyEn);
    public static readonly WhisperModelSize BaseEn = new("base.en", "base.en", "base English", "ggml-base.en.bin", GgmlType.BaseEn);
    public static readonly WhisperModelSize SmallEn = new("small.en", "small.en", "small English", "ggml-small.en.bin", GgmlType.SmallEn);

    public static WhisperModelSize FromName(string? name)
    {
        return name?.Trim().ToLowerInvariant() switch
        {
            "tiny.en" => TinyEn,
            "small.en" => SmallEn,
            "base.en" => BaseEn,
            _ => TinyEn
        };
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
        "VoiceCodex");

    private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
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
}

internal static class StartupService
{
    private const string RunRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "VoiceCodex";

    public static bool IsRunAtStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunRegistryPath, false);
        var value = key?.GetValue(AppName) as string;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return string.Equals(value.Trim('"'), Application.ExecutablePath, StringComparison.OrdinalIgnoreCase)
               || value.Contains(@"\voicecodex\start.bat", StringComparison.OrdinalIgnoreCase);
    }

    public static void SetRunAtStartup(bool enable)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunRegistryPath);
        if (enable)
        {
            key.SetValue(AppName, $"\"{Application.ExecutablePath}\"", RegistryValueKind.String);
            return;
        }

        key.DeleteValue(AppName, throwOnMissingValue: false);
    }
}

internal static class ActivityLog
{
    private const int MaxEntries = 300;
    private static readonly Lock Gate = new();
    private static readonly List<string> Entries = [];

    public static void Add(string message)
    {
        lock (Gate)
        {
            Entries.Add($"{DateTime.Now:T}  {message}");
            if (Entries.Count > MaxEntries)
            {
                Entries.RemoveRange(0, Entries.Count - MaxEntries);
            }
        }
    }

    public static string Snapshot()
    {
        lock (Gate)
        {
            return string.Join(Environment.NewLine, Entries);
        }
    }
}

internal sealed class ActivityLogForm : Form
{
    private readonly TextBox _textBox;

    public ActivityLogForm()
    {
        Text = "VoiceCodex Activity";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(760, 420);
        MinimizeBox = true;

        _textBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Font = new Font("Consolas", 10f),
            BackColor = Color.White
        };

        Controls.Add(_textBox);
        RefreshLog();
    }

    public void RefreshLog()
    {
        if (IsDisposed)
        {
            return;
        }

        _textBox.Text = ActivityLog.Snapshot();
        _textBox.SelectionStart = _textBox.TextLength;
        _textBox.ScrollToCaret();
    }
}

internal sealed class VoiceFeedback : IDisposable
{
    private readonly SpeechSynthesizer synthesizer = new();
    private readonly object gate = new();

    public VoiceFeedback()
    {
        synthesizer.Rate = 1;
        synthesizer.Volume = 85;
    }

    public void Speak(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        lock (gate)
        {
            synthesizer.SpeakAsyncCancelAll();
            synthesizer.SpeakAsync(text);
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            synthesizer.SpeakAsyncCancelAll();
            synthesizer.Dispose();
        }
    }
}

internal static class TrayIconFactory
{
    public static Icon CreateMicrophoneIcon(Color accent)
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);

        using var accentBrush = new SolidBrush(accent);
        using var darkPen = new Pen(Color.FromArgb(42, 48, 56), 2.2f)
        {
            StartCap = System.Drawing.Drawing2D.LineCap.Round,
            EndCap = System.Drawing.Drawing2D.LineCap.Round
        };

        graphics.FillRoundedRectangle(accentBrush, new RectangleF(11, 4, 10, 17), 5);
        graphics.DrawArc(darkPen, 7, 12, 18, 13, 0, 180);
        graphics.DrawLine(darkPen, 16, 25, 16, 29);
        graphics.DrawLine(darkPen, 11, 29, 21, 29);

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

internal static class GraphicsExtensions
{
    public static void FillRoundedRectangle(this Graphics graphics, Brush brush, RectangleF bounds, float radius)
    {
        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        graphics.FillPath(brush, path);
    }
}
