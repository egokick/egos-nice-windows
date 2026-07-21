using System.Diagnostics;
using Microsoft.Win32;

namespace AdminPanel;

internal enum AdminAppLogoKind
{
    Generated,
    Embedded
}

internal sealed record AdminAppDefinition(
    string Id,
    string DisplayName,
    string Description,
    string FolderName,
    AdminAppLogoKind LogoKind,
    string LogoKey,
    string RunValueName)
{
    internal string? NativeStartupExecutablePath { get; init; }

    internal bool PreferBatchStartup { get; init; }

    internal IReadOnlyList<string> LocalWebUrls { get; init; } = [];
}

internal static class AdminAppCatalog
{
    private static readonly AdminAppDefinition[] Catalog =
    [
        new(
            "parakeet-mic",
            "Parakeet Mic",
            "Fast local microphone transcription powered by NVIDIA Parakeet.",
            "parakeet-mic",
            AdminAppLogoKind.Embedded,
            "parakeet-mic",
            "NiceWindows.ParakeetMic"),
        new(
            "power-mode-toggle",
            "Power Mode Toggle",
            "Switch between low-power and high-performance system profiles.",
            "PowerModeToggle",
            AdminAppLogoKind.Generated,
            "power-mode-toggle",
            "PowerModeToggle")
        {
            NativeStartupExecutablePath = @"PowerModeToggle\bin\Release\net10.0-windows\win-x64\publish\PowerModeToggle.exe"
        },
        new(
            "stayactive",
            "Stay Active",
            "Keep the computer awake and manage active work sessions.",
            "stayactive",
            AdminAppLogoKind.Generated,
            "stayactive",
            "StayActive")
        {
            NativeStartupExecutablePath = @"stayactive\bin\Release\net10.0-windows\stayactive.exe"
        },
        new(
            "voicecodex",
            "Voice Codex",
            "Control Codex hands-free with speech capture and voice commands.",
            "voicecodex",
            AdminAppLogoKind.Generated,
            "voicecodex",
            "VoiceCodex")
        {
            NativeStartupExecutablePath = @"voicecodex\bin\Debug\net10.0-windows\voicecodex.exe"
        },
        new(
            "wifidevices",
            "Wi-Fi Devices",
            "Monitor devices, activity, and services on the local network.",
            "wifidevices",
            AdminAppLogoKind.Generated,
            "wifidevices",
            "WifiDevices")
        {
            NativeStartupExecutablePath = @"wifidevices\bin\Debug\net10.0-windows\wifidevices.exe",
            PreferBatchStartup = true,
            LocalWebUrls = ["http://127.0.0.1:5136/"]
        },
        new(
            "finance",
            "Finance",
            "Track account balances, debt, credit, and payoff-interest previews.",
            "finance",
            AdminAppLogoKind.Generated,
            "finance",
            "Finance")
        {
            NativeStartupExecutablePath = @"finance\bin\Debug\net10.0-windows\finance.exe",
            PreferBatchStartup = true,
            LocalWebUrls = ["http://127.0.0.1:5137/"]
        },
        new(
            "workflow-manager",
            "Workflow Manager",
            "Organize and run repeatable AI-assisted development workflows.",
            "workflow-manager",
            AdminAppLogoKind.Embedded,
            "workflow-manager",
            "NiceWindows.WorkflowManager"),
        new(
            "youtube-sync-tray",
            "YouTube Sync Tray",
            "Keep a local YouTube library synchronized from the system tray.",
            "YouTubeSyncTray",
            AdminAppLogoKind.Generated,
            "youtube-sync-tray",
            "YouTubeSyncTray")
        {
            NativeStartupExecutablePath = @"YouTubeSyncTray\bin\Release\net10.0-windows\YouTubeSyncTray.exe",
            LocalWebUrls = ["http://tom.localhost/", "http://127.0.0.1:48173/"]
        },
        new(
            "light-dark-toggle",
            "Light / Dark Toggle",
            "Switch Windows appearance, schedules, and display dimming.",
            "LightDarkToggle",
            AdminAppLogoKind.Generated,
            "light-dark-toggle",
            "LightDarkToggle")
        {
            NativeStartupExecutablePath = @"LightDarkToggle\bin\Release\net10.0-windows\LightDarkToggle.exe"
        },
        new(
            "nemotron-mic",
            "Nemotron Mic",
            "Local microphone transcription using NVIDIA Nemotron speech models.",
            "nemotron-mic",
            AdminAppLogoKind.Embedded,
            "nemotron-mic",
            "NiceWindows.NemotronMic"),
        new(
            "ollama-coder-agent",
            "Ollama Coder Agent",
            "Run a local coding agent backed by models served through Ollama.",
            "ollama-coder-agent",
            AdminAppLogoKind.Embedded,
            "ollama-coder-agent",
            "NiceWindows.OllamaCoderAgent")
    ];

    public static IReadOnlyList<AdminAppDefinition> Apps { get; } = Array.AsReadOnly(Catalog);

    public static AdminAppDefinition GetById(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        return Catalog.FirstOrDefault(app => string.Equals(app.Id, id, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"No admin app is registered with the id '{id}'.");
    }
}

internal static class NiceWindowsRepositoryLocator
{
    private const string KnownRepositoryPath = @"C:\source\egos-nice-windows";

    public static string GetRepositoryRoot()
    {
        if (TryGetRepositoryRoot(out var repositoryRoot, out var errorMessage))
        {
            return repositoryRoot;
        }

        throw new DirectoryNotFoundException(errorMessage);
    }

    public static bool TryGetRepositoryRoot(out string repositoryRoot, out string errorMessage)
    {
        var starts = new[]
        {
            (Location: AppContext.BaseDirectory, Label: "application directory"),
            (Location: Environment.CurrentDirectory, Label: "current directory")
        };

        var searched = new List<string>();
        foreach (var start in starts)
        {
            var found = WalkUpToRepository(start.Location, searched);
            if (found is not null)
            {
                repositoryRoot = found;
                errorMessage = string.Empty;
                return true;
            }
        }

        searched.Add(KnownRepositoryPath);
        if (IsRepositoryRoot(KnownRepositoryPath))
        {
            repositoryRoot = Path.GetFullPath(KnownRepositoryPath);
            errorMessage = string.Empty;
            return true;
        }

        repositoryRoot = string.Empty;
        errorMessage =
            "The Nice Windows repository could not be located. Expected the LightDarkToggle project at the suite root. " +
            $"Searched upward from the application and current directories, then checked '{KnownRepositoryPath}'. " +
            $"Locations checked: {string.Join(", ", searched.Distinct(StringComparer.OrdinalIgnoreCase))}.";
        return false;
    }

    public static string GetAppFolder(AdminAppDefinition app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return Path.Combine(GetRepositoryRoot(), app.FolderName);
    }

    public static string GetStartBatchPath(AdminAppDefinition app)
    {
        return Path.Combine(GetAppFolder(app), "start.bat");
    }

    private static string? WalkUpToRepository(string? startPath, ICollection<string> searched)
    {
        if (string.IsNullOrWhiteSpace(startPath))
        {
            return null;
        }

        DirectoryInfo? current;
        try
        {
            current = new DirectoryInfo(Path.GetFullPath(startPath));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            searched.Add($"{startPath} (invalid path)");
            return null;
        }

        while (current is not null)
        {
            searched.Add(current.FullName);
            if (IsRepositoryRoot(current.FullName))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return null;
    }

    private static bool IsRepositoryRoot(string path)
    {
        try
        {
            return File.Exists(Path.Combine(path, "LightDarkToggle", "LightDarkToggle.csproj"));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}

internal static class AdminAppLauncher
{
    private const string DependencyPreparationScript = "ensure-admin-app-dependencies.bat";

    internal readonly record struct LaunchResult(bool Success, string ErrorMessage);

    public static void Launch(AdminAppDefinition app)
    {
        if (!TryLaunch(app, out var errorMessage))
        {
            throw new InvalidOperationException(errorMessage);
        }
    }

    public static bool TryLaunch(AdminAppDefinition app, out string errorMessage)
    {
        return TryStart(app, out errorMessage);
    }

    public static Task<LaunchResult> PrepareAndLaunchAsync(AdminAppDefinition app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return Task.Run(() =>
        {
            if (!TryPrepareDependencies(app, out var errorMessage))
            {
                return new LaunchResult(false, errorMessage);
            }

            if (!TryStart(app, out errorMessage))
            {
                return new LaunchResult(false, errorMessage);
            }

            OpenLocalWebPageWhenReady(app);
            return new LaunchResult(true, string.Empty);
        });
    }

    private static bool TryPrepareDependencies(AdminAppDefinition app, out string errorMessage)
    {
        ArgumentNullException.ThrowIfNull(app);

        try
        {
            var appFolder = NiceWindowsRepositoryLocator.GetAppFolder(app);
            var preparationScript = Path.Combine(
                NiceWindowsRepositoryLocator.GetRepositoryRoot(),
                "scripts",
                DependencyPreparationScript);
            if (!File.Exists(preparationScript))
            {
                errorMessage =
                    $"Cannot prepare {app.DisplayName}: the dependency checker was not found at " +
                    $"'{preparationScript}'.";
                return false;
            }

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = GetCommandProcessorPath(),
                    Arguments = $"/d /c call {QuoteCommandArgument(preparationScript)} " +
                                $"{QuoteCommandArgument(app.Id)} {QuoteCommandArgument(appFolder)}",
                    WorkingDirectory = appFolder,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };

            if (!process.Start())
            {
                errorMessage =
                    $"Windows did not start the dependency checker for {app.DisplayName}.";
                return false;
            }

            var standardOutputTask = process.StandardOutput.ReadToEndAsync();
            var standardErrorTask = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            Task.WaitAll(standardOutputTask, standardErrorTask);

            if (process.ExitCode != 0)
            {
                errorMessage = $"Dependencies for {app.DisplayName} could not be prepared " +
                               $"(exit code {process.ExitCode}).";
                var details = GetOutputTail(standardErrorTask.Result, standardOutputTask.Result);
                if (!string.IsNullOrWhiteSpace(details))
                {
                    errorMessage += $" {details}";
                }

                return false;
            }

            errorMessage = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                             or System.ComponentModel.Win32Exception
                                             or IOException
                                             or UnauthorizedAccessException)
        {
            errorMessage = $"Could not prepare dependencies for {app.DisplayName}: {exception.Message}";
            return false;
        }
    }

    private static bool TryStart(AdminAppDefinition app, out string errorMessage)
    {
        ArgumentNullException.ThrowIfNull(app);

        try
        {
            var appFolder = NiceWindowsRepositoryLocator.GetAppFolder(app);
            var startBatchPath = Path.Combine(appFolder, "start.bat");
            if (!File.Exists(startBatchPath))
            {
                errorMessage =
                    $"Cannot start {app.DisplayName}: its launcher was not found at '{startBatchPath}'.";
                return false;
            }

            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = GetCommandProcessorPath(),
                Arguments = $"/d /c call {QuoteCommandArgument(startBatchPath)}",
                WorkingDirectory = appFolder,
                UseShellExecute = true
            });

            if (process is null)
            {
                errorMessage =
                    $"Windows did not start the launcher for {app.DisplayName} ('{startBatchPath}').";
                return false;
            }

            errorMessage = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                             or System.ComponentModel.Win32Exception
                                             or IOException
                                             or UnauthorizedAccessException)
        {
            errorMessage = $"Could not start {app.DisplayName}: {exception.Message}";
            return false;
        }
    }

    private static void OpenLocalWebPageWhenReady(AdminAppDefinition app)
    {
        if (app.LocalWebUrls.Count == 0)
        {
            return;
        }

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < deadline)
            {
                foreach (var url in app.LocalWebUrls)
                {
                    try
                    {
                        using var response = client.GetAsync(url).GetAwaiter().GetResult();
                        OpenWebPage(url);
                        return;
                    }
                    catch (HttpRequestException)
                    {
                    }
                    catch (TaskCanceledException)
                    {
                    }
                }

                Thread.Sleep(300);
            }

            OpenWebPage(app.LocalWebUrls[0]);
        }
        catch
        {
            // Launching the app remains successful even if its local page cannot be opened.
        }
    }

    private static void OpenWebPage(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // The local app remains running if the default browser cannot be started.
        }
    }

    private static string GetCommandProcessorPath()
    {
        var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var commandProcessorPath = string.IsNullOrWhiteSpace(systemDirectory)
            ? Environment.GetEnvironmentVariable("ComSpec")
            : Path.Combine(systemDirectory, "cmd.exe");

        if (string.IsNullOrWhiteSpace(commandProcessorPath)
            || !File.Exists(commandProcessorPath))
        {
            throw new FileNotFoundException("Windows command processor cmd.exe could not be located.");
        }

        return commandProcessorPath;
    }

    private static string QuoteCommandArgument(string value)
    {
        if (value.Contains('"'))
        {
            throw new ArgumentException("A Windows command path cannot contain a double quote.", nameof(value));
        }

        return $"\"{value}\"";
    }

    private static string GetOutputTail(string standardError, string standardOutput)
    {
        var output = string.Join(
            Environment.NewLine,
            new[] { standardError, standardOutput }.Where(value => !string.IsNullOrWhiteSpace(value)))
            .Trim();
        const int maximumLength = 900;
        return output.Length <= maximumLength
            ? output
            : output[^maximumLength..];
    }
}

internal static class AdminAppAutoStartService
{
    private const string RunRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsEnabled(AdminAppDefinition app)
    {
        if (!TryGetEnabled(app, out var enabled, out var errorMessage))
        {
            throw new InvalidOperationException(errorMessage);
        }

        return enabled;
    }

    public static bool TryGetEnabled(
        AdminAppDefinition app,
        out bool enabled,
        out string errorMessage)
    {
        ArgumentNullException.ThrowIfNull(app);

        if (!OperatingSystem.IsWindows())
        {
            enabled = false;
            errorMessage = "Windows startup registration is only available on Windows.";
            return false;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunRegistryPath, writable: false);
            var registeredCommand = key?.GetValue(app.RunValueName) as string;
            if (string.IsNullOrWhiteSpace(registeredCommand))
            {
                enabled = false;
                errorMessage = string.Empty;
                return true;
            }

            var canonicalCommand = GetCanonicalCommand(app);
            var batchCommand = GetBatchCommand(app);
            enabled = string.Equals(
                          registeredCommand,
                          canonicalCommand,
                          StringComparison.OrdinalIgnoreCase)
                      || string.Equals(
                          registeredCommand,
                          batchCommand,
                          StringComparison.OrdinalIgnoreCase)
                      || IsNativeExecutableCommand(app, registeredCommand);
            errorMessage = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                             or IOException
                                             or UnauthorizedAccessException
                                             or System.Security.SecurityException)
        {
            enabled = false;
            errorMessage =
                $"Could not read the Windows startup setting for {app.DisplayName} " +
                $"(HKCU\\{RunRegistryPath}\\{app.RunValueName}): {exception.Message}";
            return false;
        }
    }

    public static void SetEnabled(AdminAppDefinition app, bool enabled)
    {
        if (!TrySetEnabled(app, enabled, out var errorMessage))
        {
            throw new InvalidOperationException(errorMessage);
        }
    }

    public static bool TrySetEnabled(
        AdminAppDefinition app,
        bool enabled,
        out string errorMessage)
    {
        ArgumentNullException.ThrowIfNull(app);

        if (!OperatingSystem.IsWindows())
        {
            errorMessage = "Windows startup registration is only available on Windows.";
            return false;
        }

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunRegistryPath, writable: true)
                ?? throw new InvalidOperationException(
                    $"Windows did not open HKCU\\{RunRegistryPath} for writing.");

            if (enabled)
            {
                key.SetValue(app.RunValueName, GetCanonicalCommand(app), RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(app.RunValueName, throwOnMissingValue: false);
            }

            errorMessage = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                             or IOException
                                             or UnauthorizedAccessException
                                             or System.Security.SecurityException)
        {
            errorMessage =
                $"Could not {(enabled ? "enable" : "disable")} Windows startup for {app.DisplayName} " +
                $"(HKCU\\{RunRegistryPath}\\{app.RunValueName}): {exception.Message}";
            return false;
        }
    }

    public static string GetCanonicalCommand(AdminAppDefinition app)
    {
        ArgumentNullException.ThrowIfNull(app);

        if (!app.PreferBatchStartup
            && !string.IsNullOrWhiteSpace(app.NativeStartupExecutablePath))
        {
            var executablePath = string.Equals(
                app.Id,
                "light-dark-toggle",
                StringComparison.OrdinalIgnoreCase)
                ? Application.ExecutablePath
                : Path.Combine(
                    NiceWindowsRepositoryLocator.GetRepositoryRoot(),
                    app.NativeStartupExecutablePath);
            if (File.Exists(executablePath))
            {
                return QuoteCommandArgument(Path.GetFullPath(executablePath));
            }
        }

        return GetBatchCommand(app);
    }

    private static string GetBatchCommand(AdminAppDefinition app)
    {
        var startBatchPath = NiceWindowsRepositoryLocator.GetStartBatchPath(app);
        if (!File.Exists(startBatchPath))
        {
            throw new FileNotFoundException(
                $"Cannot configure startup for {app.DisplayName}: its launcher was not found at '{startBatchPath}'.",
                startBatchPath);
        }

        var commandProcessorPath = GetCommandProcessorPath();
        var workingDirectory = Path.GetDirectoryName(startBatchPath)
            ?? throw new DirectoryNotFoundException(
                $"The launcher directory for {app.DisplayName} could not be resolved.");
        return $"{QuoteCommandArgument(commandProcessorPath)} /d /c cd /d " +
               $"{QuoteCommandArgument(workingDirectory)} && call {QuoteCommandArgument(startBatchPath)}";
    }

    private static string GetCommandProcessorPath()
    {
        var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var commandProcessorPath = string.IsNullOrWhiteSpace(systemDirectory)
            ? Environment.GetEnvironmentVariable("ComSpec")
            : Path.Combine(systemDirectory, "cmd.exe");

        if (string.IsNullOrWhiteSpace(commandProcessorPath)
            || !string.Equals(Path.GetFileName(commandProcessorPath), "cmd.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new FileNotFoundException("Windows command processor cmd.exe could not be located.");
        }

        return Path.GetFullPath(commandProcessorPath);
    }

    private static string QuoteCommandArgument(string value)
    {
        if (value.Contains('"'))
        {
            throw new ArgumentException("A Windows command path cannot contain a double quote.", nameof(value));
        }

        return $"\"{value}\"";
    }

    private static bool IsNativeExecutableCommand(AdminAppDefinition app, string command)
    {
        if (string.IsNullOrWhiteSpace(app.NativeStartupExecutablePath))
        {
            return false;
        }

        var trimmed = command.TrimStart();
        if (trimmed.Length == 0)
        {
            return false;
        }

        string executable;
        if (trimmed[0] == '"')
        {
            var closingQuote = trimmed.IndexOf('"', 1);
            if (closingQuote < 2)
            {
                return false;
            }

            executable = trimmed[1..closingQuote];
        }
        else
        {
            var firstWhitespace = trimmed.IndexOfAny([' ', '\t', '\r', '\n']);
            executable = firstWhitespace < 0 ? trimmed : trimmed[..firstWhitespace];
        }

        if (!string.Equals(
                Path.GetFileName(executable),
                Path.GetFileName(app.NativeStartupExecutablePath),
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var appFolder = Path.TrimEndingDirectorySeparator(
                NiceWindowsRepositoryLocator.GetAppFolder(app)) + Path.DirectorySeparatorChar;
            var fullExecutablePath = Path.GetFullPath(executable);
            return fullExecutablePath.StartsWith(appFolder, StringComparison.OrdinalIgnoreCase)
                   && File.Exists(fullExecutablePath);
        }
        catch (Exception exception) when (exception is ArgumentException
                                             or NotSupportedException
                                             or PathTooLongException)
        {
            return false;
        }
    }
}
