using System.Diagnostics;
using System.Text;
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

    // True when start.bat builds/starts a detached process and then exits.
    // The Admin Panel can wait for these launchers and surface their real errors.
    internal bool BatchExitsAfterLaunch { get; init; }

    // When present, the Admin Panel uses this mutex as an opt-in runtime status probe.
    internal string? RuntimeMutexName { get; init; }

    // Browser-only documents do not own a process that the Admin Panel can safely stop.
    internal bool SupportsRuntimeControl { get; init; } = true;

    // Some apps surface their runtime state outside the hover action as well.
    internal bool ShowRuntimeStatusBadge { get; init; }

    // Exact app-relative script/project paths that identify hosted runtimes.
    internal IReadOnlyList<string> RuntimeCommandLineRelativePaths { get; init; } = [];

    // Optional windowless launcher used only for the per-user startup registration.
    internal string? AutoStartLauncherRelativePath { get; init; }

    // Shows an app-specific launch settings button and forwards persisted arguments.
    internal bool HasLaunchSettings { get; init; }

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
            "NiceWindows.ParakeetMic")
        {
            RuntimeCommandLineRelativePaths =
                ["start.bat", "start-parakeet-mic.bat", "transcribe_mic.py"]
        },
        new(
            "continuous-transcriber",
            "Continuous Transcriber",
            "Continuously capture speech and retain useful local transcripts.",
            "Continuous-transcriber",
            AdminAppLogoKind.Generated,
            "continuous-transcriber",
            "NiceWindows.ContinuousTranscriber")
        {
            BatchExitsAfterLaunch = true,
            RuntimeMutexName = @"Local\ContinuousMicrophoneTranscriberMonitor",
            ShowRuntimeStatusBadge = true,
            RuntimeCommandLineRelativePaths =
                ["monitor_transcriber.py", "transcribe_microphone.py"],
            AutoStartLauncherRelativePath = "start-hidden.vbs",
            HasLaunchSettings = true
        },
        new(
            "power-mode-toggle",
            "Power Mode Toggle",
            "Switch between low-power and high-performance system profiles.",
            "PowerModeToggle",
            AdminAppLogoKind.Generated,
            "power-mode-toggle",
            "PowerModeToggle")
        {
            NativeStartupExecutablePath = @"PowerModeToggle\bin\Release\net10.0-windows\win-x64\publish\PowerModeToggle.exe",
            BatchExitsAfterLaunch = true,
            RuntimeMutexName = "PowerModeToggle.Singleton"
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
            NativeStartupExecutablePath = @"stayactive\bin\Release\net10.0-windows\stayactive.exe",
            BatchExitsAfterLaunch = true,
            RuntimeMutexName = "StayActive.Singleton"
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
            NativeStartupExecutablePath = @"voicecodex\bin\Debug\net10.0-windows\voicecodex.exe",
            RuntimeMutexName = "VoiceCodex.Singleton",
            RuntimeCommandLineRelativePaths =
                ["start.bat", "start-voicecodex.bat"]
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
            BatchExitsAfterLaunch = true,
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
            BatchExitsAfterLaunch = true,
            LocalWebUrls = ["http://finance.local:5137/"]
        },
        new(
            "workflow-manager",
            "Workflow Manager",
            "Organize and run repeatable AI-assisted development workflows.",
            "workflow-manager",
            AdminAppLogoKind.Embedded,
            "workflow-manager",
            "NiceWindows.WorkflowManager")
        {
            BatchExitsAfterLaunch = true,
            SupportsRuntimeControl = false
        },
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
            BatchExitsAfterLaunch = true,
            RuntimeMutexName = "YouTubeSyncTray.Singleton",
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
            NativeStartupExecutablePath = @"LightDarkToggle\bin\Release\net10.0-windows\LightDarkToggle.exe",
            BatchExitsAfterLaunch = true,
            RuntimeMutexName = "LightDarkToggle.Singleton"
        },
        new(
            "nemotron-mic",
            "Nemotron Mic",
            "Local microphone transcription using NVIDIA Nemotron speech models.",
            "nemotron-mic",
            AdminAppLogoKind.Embedded,
            "nemotron-mic",
            "NiceWindows.NemotronMic")
        {
            RuntimeCommandLineRelativePaths =
                ["start.bat", "start-nemotron-mic.bat", "transcribe_mic.py"]
        },
        new(
            "ollama-coder-agent",
            "Ollama Coder Agent",
            "Run a local coding agent backed by models served through Ollama.",
            "ollama-coder-agent",
            AdminAppLogoKind.Embedded,
            "ollama-coder-agent",
            "NiceWindows.OllamaCoderAgent")
        {
            RuntimeCommandLineRelativePaths =
                ["start.bat", "start-coder-files.bat", "coder_files_agent.py"]
        }
    ];

    public static IReadOnlyList<AdminAppDefinition> Apps { get; } = Array.AsReadOnly(Catalog);

    public static AdminAppDefinition GetById(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        return Catalog.FirstOrDefault(app => string.Equals(app.Id, id, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"No admin app is registered with the id '{id}'.");
    }
}

internal readonly record struct AdminAppProcessIdentity(
    string? ExecutablePath,
    IReadOnlyList<string> CommandLineMarkers);

internal static class AdminAppProcessIdentityResolver
{
    public static AdminAppProcessIdentity Resolve(AdminAppDefinition app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var repositoryRoot = Path.TrimEndingDirectorySeparator(
                                 NiceWindowsRepositoryLocator.GetRepositoryRoot())
                             + Path.DirectorySeparatorChar;
        var appFolder = Path.TrimEndingDirectorySeparator(
                            NiceWindowsRepositoryLocator.GetAppFolder(app))
                        + Path.DirectorySeparatorChar;

        string? executablePath = null;
        if (!string.IsNullOrWhiteSpace(app.NativeStartupExecutablePath))
        {
            executablePath = Path.GetFullPath(
                Path.Combine(repositoryRoot, app.NativeStartupExecutablePath));
            if (!executablePath.StartsWith(repositoryRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"The runtime executable for {app.DisplayName} is outside the repository.");
            }
        }

        var commandLineMarkers = app.RuntimeCommandLineRelativePaths
            .Select(relativePath => Path.GetFullPath(Path.Combine(appFolder, relativePath)))
            .ToArray();
        if (commandLineMarkers.Any(marker =>
                !marker.StartsWith(appFolder, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"A runtime marker for {app.DisplayName} is outside its app folder.");
        }

        if (app.SupportsRuntimeControl
            && string.IsNullOrWhiteSpace(executablePath)
            && commandLineMarkers.Length == 0)
        {
            throw new InvalidOperationException(
                $"{app.DisplayName} does not define a safe runtime process identity.");
        }

        return new AdminAppProcessIdentity(executablePath, commandLineMarkers);
    }
}

internal static class AdminAppRuntimeStatusService
{
    internal readonly record struct RuntimeState(bool? IsRunning, string ErrorMessage);

    public static Task<IReadOnlyDictionary<string, RuntimeState>> GetRuntimeStatesAsync(
        IReadOnlyList<AdminAppDefinition> apps)
    {
        ArgumentNullException.ThrowIfNull(apps);

        var appSnapshot = apps.ToArray();
        return Task.Run<IReadOnlyDictionary<string, RuntimeState>>(
            () => GetRuntimeStates(appSnapshot));
    }

    private static IReadOnlyDictionary<string, RuntimeState> GetRuntimeStates(
        IReadOnlyList<AdminAppDefinition> apps)
    {
        var states = new Dictionary<string, RuntimeState>(StringComparer.OrdinalIgnoreCase);
        var processProbedApps = new List<AdminAppDefinition>();

        foreach (var app in apps)
        {
            if (!app.SupportsRuntimeControl)
            {
                states[app.Id] = new RuntimeState(false, string.Empty);
                continue;
            }

            if (string.IsNullOrWhiteSpace(app.RuntimeMutexName))
            {
                processProbedApps.Add(app);
                continue;
            }

            states[app.Id] = TryGetMutexRunning(
                app,
                out var running,
                out var errorMessage)
                ? new RuntimeState(running, string.Empty)
                : new RuntimeState(null, errorMessage);
        }

        if (processProbedApps.Count == 0)
        {
            return states;
        }

        if (TryGetRunningProcessAppIds(
                processProbedApps,
                out var runningAppIds,
                out var processError))
        {
            foreach (var app in processProbedApps)
            {
                states[app.Id] = new RuntimeState(
                    runningAppIds.Contains(app.Id),
                    string.Empty);
            }
        }
        else
        {
            foreach (var app in processProbedApps)
            {
                states[app.Id] = new RuntimeState(null, processError);
            }
        }

        return states;
    }

    private static bool TryGetMutexRunning(
        AdminAppDefinition app,
        out bool running,
        out string errorMessage)
    {
        try
        {
            using var runtimeMutex = Mutex.OpenExisting(app.RuntimeMutexName!);
            running = true;
            errorMessage = string.Empty;
            return true;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            running = false;
            errorMessage = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
                                             or IOException
                                             or ArgumentException)
        {
            running = false;
            errorMessage = $"Could not read the runtime status for {app.DisplayName}: {exception.Message}";
            return false;
        }
    }

    private static bool TryGetRunningProcessAppIds(
        IReadOnlyList<AdminAppDefinition> apps,
        out HashSet<string> runningAppIds,
        out string errorMessage)
    {
        runningAppIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var appEntries = apps.Select(app =>
            {
                var identity = AdminAppProcessIdentityResolver.Resolve(app);
                var executable = identity.ExecutablePath is null
                    ? "$null"
                    : $"'{AdminAppLauncher.EscapePowerShellSingleQuotedString(identity.ExecutablePath)}'";
                var commandLineMarkers = string.Join(
                    ", ",
                    identity.CommandLineMarkers.Select(marker =>
                        $"'{AdminAppLauncher.EscapePowerShellSingleQuotedString(marker)}'"));
                return
                    $"[pscustomobject]@{{ Id = '{AdminAppLauncher.EscapePowerShellSingleQuotedString(app.Id)}'; " +
                    $"Executable = {executable}; CommandLineMarkers = @({commandLineMarkers}) }}";
            });
            var script = $$"""
                $ErrorActionPreference = 'Stop'
                $comparison = [System.StringComparison]::OrdinalIgnoreCase
                $apps = @(
                {{string.Join(Environment.NewLine, appEntries)}}
                )
                $commandHostNames = @(
                    'cmd.exe', 'python.exe', 'pythonw.exe', 'py.exe', 'pyw.exe')
                $running = [System.Collections.Generic.HashSet[string]]::new(
                    [System.StringComparer]::OrdinalIgnoreCase)

                foreach ($processInfo in @(Get-CimInstance Win32_Process -ErrorAction Stop)) {
                    $executablePath = [string]$processInfo.ExecutablePath
                    $commandLine = [string]$processInfo.CommandLine
                    foreach ($app in $apps) {
                        $executableMatches =
                            -not [string]::IsNullOrWhiteSpace([string]$app.Executable) -and
                            [string]::Equals(
                                $executablePath,
                                [string]$app.Executable,
                                $comparison)
                        $commandMatches = $false
                        if ($commandHostNames -contains [string]$processInfo.Name -and
                            -not [string]::IsNullOrWhiteSpace($commandLine)) {
                            foreach ($marker in @($app.CommandLineMarkers)) {
                                if ($commandLine.IndexOf([string]$marker, $comparison) -ge 0) {
                                    $commandMatches = $true
                                    break
                                }
                            }
                        }
                        if ($executableMatches -or $commandMatches) {
                            [void]$running.Add($app.Id)
                        }
                    }
                }

                foreach ($id in $running) {
                    Write-Output $id
                }
                """;
            var encodedScript = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = AdminAppLauncher.GetPowerShellPath(),
                    Arguments =
                        "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass " +
                        $"-EncodedCommand {encodedScript}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };

            if (!process.Start())
            {
                errorMessage = "Windows did not start the app status helper.";
                return false;
            }

            if (!process.WaitForExit(10_000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception exception) when (exception is InvalidOperationException
                                                     or System.ComponentModel.Win32Exception
                                                     or NotSupportedException)
                {
                    // The helper may have exited between the timeout and the kill request.
                }

                errorMessage = "Timed out while checking which apps are running.";
                return false;
            }

            var standardOutput = process.StandardOutput.ReadToEnd();
            var standardError = process.StandardError.ReadToEnd();
            if (process.ExitCode != 0)
            {
                errorMessage = "Could not check which apps are running.";
                var details = GetOutputTail(standardError, standardOutput);
                if (!string.IsNullOrWhiteSpace(details))
                {
                    errorMessage += $" {details}";
                }

                return false;
            }

            var knownIds = apps.Select(app => app.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var line in standardOutput.Split(
                         ['\r', '\n'],
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (knownIds.Contains(line))
                {
                    runningAppIds.Add(line);
                }
            }

            errorMessage = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                             or System.ComponentModel.Win32Exception
                                             or IOException
                                             or UnauthorizedAccessException
                                             or ArgumentException
                                             or NotSupportedException
                                             or PathTooLongException)
        {
            errorMessage = $"Could not check which apps are running: {exception.Message}";
            return false;
        }
    }

    private static string GetOutputTail(params string[] values)
    {
        return string.Join(
            " | ",
            values
                .SelectMany(value => value.Split(
                    ['\r', '\n'],
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .TakeLast(6));
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

    public static Task<LaunchResult> StopAsync(AdminAppDefinition app)
    {
        ArgumentNullException.ThrowIfNull(app);

        if (!app.SupportsRuntimeControl)
        {
            return Task.FromResult(new LaunchResult(
                false,
                $"{app.DisplayName} does not own a process that can be stopped safely."));
        }

        return Task.Run(() =>
            TryStopExistingProcesses(app, out var errorMessage)
                ? new LaunchResult(true, string.Empty)
                : new LaunchResult(false, errorMessage));
    }

    public static Task<LaunchResult> PrepareAndLaunchAsync(
        AdminAppDefinition app,
        IReadOnlyList<string>? launchArguments = null)
    {
        ArgumentNullException.ThrowIfNull(app);

        return Task.Run(() =>
        {
            // StayActive can have a tray process and one or more short-lived
            // Chrome native-host processes using the same executable. Launch
            // the existing output first: the tray mutex makes this idempotent,
            // and a native-host-only state still receives a real tray process.
            // Most importantly, no restore, stop, or in-place build can race a
            // Chrome native-host reconnect while that executable exists.
            if (IsStayActiveApp(app))
            {
                if (!TryLaunchCurrentNativeExecutable(
                        app,
                        out var launched,
                        out var stayActiveLaunchError))
                {
                    return new LaunchResult(false, stayActiveLaunchError);
                }

                if (launched)
                {
                    return new LaunchResult(true, string.Empty);
                }
            }

            if (!TryPrepareDependencies(app, out var errorMessage))
            {
                return new LaunchResult(false, errorMessage);
            }

            if (app.SupportsRuntimeControl
                && !TryStopExistingProcesses(app, out errorMessage))
            {
                return new LaunchResult(false, errorMessage);
            }

            if (!TryLaunchCurrentNativeExecutableIfCurrent(
                    app,
                    out var currentOutputLaunched,
                    out errorMessage))
            {
                return new LaunchResult(false, errorMessage);
            }

            if (currentOutputLaunched)
            {
                OpenLocalWebPageWhenReady(app);
                return new LaunchResult(true, string.Empty);
            }

            if (!TryStart(app, launchArguments, out errorMessage))
            {
                return new LaunchResult(false, errorMessage);
            }

            OpenLocalWebPageWhenReady(app);
            return new LaunchResult(true, string.Empty);
        });
    }

    private static bool IsStayActiveApp(AdminAppDefinition app)
    {
        return string.Equals(app.Id, "stayactive", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryStopExistingProcesses(
        AdminAppDefinition app,
        out string errorMessage)
    {
        try
        {
            var appFolder = NiceWindowsRepositoryLocator.GetAppFolder(app);
            var identity = AdminAppProcessIdentityResolver.Resolve(app);
            var expectedExecutable = identity.ExecutablePath is null
                ? "$null"
                : $"'{EscapePowerShellSingleQuotedString(identity.ExecutablePath)}'";
            var commandLineMarkers = string.Join(
                ", ",
                identity.CommandLineMarkers.Select(marker =>
                    $"'{EscapePowerShellSingleQuotedString(marker)}'"));
            var script = $$"""
                $ErrorActionPreference = 'Stop'
                $comparison = [System.StringComparison]::OrdinalIgnoreCase
                $currentProcessId = $PID
                $expectedExecutable = {{expectedExecutable}}
                $commandLineMarkers = @({{commandLineMarkers}})
                $commandHostNames = @(
                    'cmd.exe', 'python.exe', 'pythonw.exe', 'py.exe', 'pyw.exe')
                $allProcesses = @(Get-CimInstance Win32_Process -ErrorAction Stop)
                $matches = @(
                    $allProcesses | Where-Object {
                        if ($_.ProcessId -eq $currentProcessId) {
                            return $false
                        }

                        $executableMatches =
                            -not [string]::IsNullOrWhiteSpace([string]$expectedExecutable) -and
                            [string]::Equals(
                                [string]$_.ExecutablePath,
                                [string]$expectedExecutable,
                                $comparison)
                        $commandMatches = $false
                        if ($commandHostNames -contains [string]$_.Name -and
                            -not [string]::IsNullOrWhiteSpace([string]$_.CommandLine)) {
                            foreach ($marker in $commandLineMarkers) {
                                if ($_.CommandLine.IndexOf([string]$marker, $comparison) -ge 0) {
                                    $commandMatches = $true
                                    break
                                }
                            }
                        }
                        $executableMatches -or $commandMatches
                    }
                )

                $targetIds = [System.Collections.Generic.HashSet[int]]::new()
                foreach ($process in $matches) {
                    [void]$targetIds.Add([int]$process.ProcessId)
                }

                $addedDescendant = $true
                while ($addedDescendant) {
                    $addedDescendant = $false
                    foreach ($process in $allProcesses) {
                        if ($targetIds.Contains([int]$process.ParentProcessId) -and
                            $targetIds.Add([int]$process.ProcessId)) {
                            $addedDescendant = $true
                        }
                    }
                }

                $targets = @(
                    $allProcesses | Where-Object {
                        $targetIds.Contains([int]$_.ProcessId)
                    }
                )
                foreach ($process in $targets) {
                    try {
                        Stop-Process -Id $process.ProcessId -Force -ErrorAction Stop
                    }
                    catch {
                        if (Get-Process -Id $process.ProcessId -ErrorAction SilentlyContinue) {
                            throw
                        }

                        # A parent or child may have exited while the matched process
                        # tree was being stopped. That already satisfies the restart.
                    }
                }

                if ($targets.Count -gt 0) {
                    $deadline = [DateTime]::UtcNow.AddSeconds(5)
                    do {
                        $remaining = @(
                            $targets | Where-Object {
                                Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue
                            }
                        )
                        if ($remaining.Count -eq 0) {
                            break
                        }
                        Start-Sleep -Milliseconds 100
                    } while ([DateTime]::UtcNow -lt $deadline)

                    if ($remaining.Count -gt 0) {
                        throw "Timed out waiting for process ID(s) $($remaining.ProcessId -join ', ') to stop."
                    }
                }
                """;
            var encodedScript = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = GetPowerShellPath(),
                    Arguments =
                        $"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass " +
                        $"-EncodedCommand {encodedScript}",
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
                    $"Windows did not start the restart helper for {app.DisplayName}.";
                return false;
            }

            var standardOutputTask = process.StandardOutput.ReadToEndAsync();
            var standardErrorTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(15_000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception exception) when (exception is InvalidOperationException
                                                     or System.ComponentModel.Win32Exception
                                                     or NotSupportedException)
                {
                    // The helper may have exited between the timeout and the kill request.
                }

                errorMessage =
                    $"Timed out while stopping the existing {app.DisplayName} process.";
                return false;
            }

            Task.WaitAll(standardOutputTask, standardErrorTask);
            if (process.ExitCode == 0)
            {
                errorMessage = string.Empty;
                return true;
            }

            errorMessage =
                $"The existing {app.DisplayName} process could not be stopped " +
                $"(exit code {process.ExitCode}).";
            var details = GetOutputTail(standardErrorTask.Result, standardOutputTask.Result);
            if (!string.IsNullOrWhiteSpace(details))
            {
                errorMessage += $" {details}";
            }

            return false;
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                             or System.ComponentModel.Win32Exception
                                             or IOException
                                             or UnauthorizedAccessException
                                             or ArgumentException
                                             or NotSupportedException
                                             or PathTooLongException)
        {
            errorMessage =
                $"Could not stop the existing {app.DisplayName} process: {exception.Message}";
            return false;
        }
    }

    private static bool TryPrepareDependencies(AdminAppDefinition app, out string errorMessage)
    {
        ArgumentNullException.ThrowIfNull(app);

        try
        {
            var appFolder = NiceWindowsRepositoryLocator.GetAppFolder(app);
            if (IsDependencyPreparationCurrent(appFolder))
            {
                errorMessage = string.Empty;
                return true;
            }

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

            WriteDependencyPreparationMarker(appFolder);
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

    private static bool TryLaunchCurrentNativeExecutable(
        AdminAppDefinition app,
        out bool launched,
        out string errorMessage)
    {
        launched = false;
        errorMessage = string.Empty;

        if (app.PreferBatchStartup || string.IsNullOrWhiteSpace(app.NativeStartupExecutablePath))
        {
            return true;
        }

        try
        {
            var repositoryRoot = NiceWindowsRepositoryLocator.GetRepositoryRoot();
            var appFolder = NiceWindowsRepositoryLocator.GetAppFolder(app);
            var executablePath = Path.Combine(repositoryRoot, app.NativeStartupExecutablePath);
            if (!File.Exists(executablePath))
            {
                return true;
            }

            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = executablePath,
                WorkingDirectory = appFolder,
                UseShellExecute = true
            });
            if (process is null)
            {
                errorMessage = $"Windows did not start {app.DisplayName} ('{executablePath}').";
                return false;
            }

            launched = true;
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                             or System.ComponentModel.Win32Exception
                                             or IOException
                                             or UnauthorizedAccessException)
        {
            errorMessage = $"Could not quick-launch {app.DisplayName}: {exception.Message}";
            return false;
        }
    }

    private static bool TryLaunchCurrentNativeExecutableIfCurrent(
        AdminAppDefinition app,
        out bool launched,
        out string errorMessage)
    {
        launched = false;
        errorMessage = string.Empty;

        if (app.PreferBatchStartup || string.IsNullOrWhiteSpace(app.NativeStartupExecutablePath))
        {
            return true;
        }

        try
        {
            var repositoryRoot = NiceWindowsRepositoryLocator.GetRepositoryRoot();
            var appFolder = NiceWindowsRepositoryLocator.GetAppFolder(app);
            var executablePath = Path.Combine(repositoryRoot, app.NativeStartupExecutablePath);
            if (!File.Exists(executablePath) || !IsBuildOutputCurrent(appFolder, executablePath))
            {
                return true;
            }

            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = executablePath,
                WorkingDirectory = appFolder,
                UseShellExecute = true
            });
            if (process is null)
            {
                errorMessage = $"Windows did not start {app.DisplayName} ('{executablePath}').";
                return false;
            }

            launched = true;
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                             or System.ComponentModel.Win32Exception
                                             or IOException
                                             or UnauthorizedAccessException)
        {
            errorMessage = $"Could not quick-launch {app.DisplayName}: {exception.Message}";
            return false;
        }
    }

    private static bool IsBuildOutputCurrent(string appFolder, string executablePath)
    {
        var executableWriteTime = File.GetLastWriteTimeUtc(executablePath);
        foreach (var inputPath in EnumerateBuildInputs(appFolder))
        {
            if (File.GetLastWriteTimeUtc(inputPath) > executableWriteTime)
            {
                return false;
            }
        }

        return true;
    }

    private static IEnumerable<string> EnumerateBuildInputs(string appFolder)
    {
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".csproj", ".fs", ".fsproj", ".props", ".targets", ".resx",
            ".xaml", ".config", ".json", ".xml"
        };

        foreach (var path in Directory.EnumerateFiles(appFolder, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(appFolder, path);
            if (relativePath.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || relativePath.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || relativePath.StartsWith(".git" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || relativePath.StartsWith(".vs" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (extensions.Contains(Path.GetExtension(path))
                || string.Equals(Path.GetFileName(path), "start.bat", StringComparison.OrdinalIgnoreCase))
            {
                yield return path;
            }
        }
    }

    private static bool IsDependencyPreparationCurrent(string appFolder)
    {
        var markerPath = GetDependencyPreparationMarkerPath(appFolder);
        if (!File.Exists(markerPath))
        {
            return false;
        }

        var markerWriteTime = File.GetLastWriteTimeUtc(markerPath);
        return EnumerateDependencyInputs(appFolder)
            .All(path => File.GetLastWriteTimeUtc(path) <= markerWriteTime);
    }

    private static IEnumerable<string> EnumerateDependencyInputs(string appFolder)
    {
        var dependencyFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "packages.lock.json", "nuget.config", "directory.packages.props", "requirements.txt",
            "pyproject.toml", "poetry.lock", "uv.lock", "package.json", "package-lock.json",
            "prepare-runtime.ps1"
        };
        var dependencyExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".csproj", ".fsproj", ".props", ".targets"
        };

        foreach (var path in Directory.EnumerateFiles(appFolder, "*", SearchOption.AllDirectories))
        {
            if (dependencyFileNames.Contains(Path.GetFileName(path))
                || dependencyExtensions.Contains(Path.GetExtension(path)))
            {
                yield return path;
            }
        }

        var scriptsFolder = Path.Combine(NiceWindowsRepositoryLocator.GetRepositoryRoot(), "scripts");
        foreach (var path in Directory.EnumerateFiles(scriptsFolder, "*", SearchOption.TopDirectoryOnly))
        {
            if (string.Equals(Path.GetExtension(path), ".bat", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetExtension(path), ".ps1", StringComparison.OrdinalIgnoreCase))
            {
                yield return path;
            }
        }
    }

    private static string GetDependencyPreparationMarkerPath(string appFolder)
    {
        return Path.Combine(appFolder, "obj", "admin-panel", "dependency-prepared.marker");
    }

    private static void WriteDependencyPreparationMarker(string appFolder)
    {
        try
        {
            var markerPath = GetDependencyPreparationMarkerPath(appFolder);
            Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);
            File.WriteAllText(markerPath, DateTime.UtcNow.ToString("O"));
        }
        catch (IOException)
        {
            // Dependency preparation has still succeeded; this launch will simply not be cached.
        }
        catch (UnauthorizedAccessException)
        {
            // Dependency preparation has still succeeded; this launch will simply not be cached.
        }
    }
    private static bool TryStart(AdminAppDefinition app, out string errorMessage)
    {
        return TryStart(app, [], out errorMessage);
    }

    private static bool TryStart(
        AdminAppDefinition app,
        IReadOnlyList<string>? launchArguments,
        out string errorMessage)
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

            return app.BatchExitsAfterLaunch
                ? TryRunDetachedBatch(
                    app, appFolder, startBatchPath, launchArguments, out errorMessage)
                : TryRunAttachedBatch(
                    app, appFolder, startBatchPath, launchArguments, out errorMessage);
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

    private static bool TryRunDetachedBatch(
        AdminAppDefinition app,
        string appFolder,
        string startBatchPath,
        IReadOnlyList<string>? launchArguments,
        out string errorMessage)
    {
        CleanupOldLaunchLogs();
        var outputPath = Path.Combine(
            Path.GetTempPath(),
            $"nicewindows-admin-launch-{Guid.NewGuid():N}.log");
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = GetCommandProcessorPath(),
                    Arguments = $"/d /c call {QuoteCommandArgument(startBatchPath)}" +
                                FormatForwardedArguments(launchArguments) +
                                $" 1> {QuoteCommandArgument(outputPath)} 2>&1",
                    WorkingDirectory = appFolder,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            if (!process.Start())
            {
                errorMessage =
                    $"Windows did not start the launcher for {app.DisplayName} ('{startBatchPath}').";
                return false;
            }

            process.WaitForExit();
            var output = ReadSharedLaunchLog(outputPath);
            if (process.ExitCode == 0)
            {
                errorMessage = string.Empty;
                return true;
            }

            errorMessage = $"{app.DisplayName} could not be started (exit code {process.ExitCode}).";
            var details = GetOutputTail(string.Empty, output);
            if (!string.IsNullOrWhiteSpace(details))
            {
                errorMessage += $" {details}";
            }

            return false;
        }
        finally
        {
            TryDeleteLaunchLog(outputPath);
        }
    }

    private static string ReadSharedLaunchLog(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return string.Empty;
            }

            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (IOException)
        {
            return string.Empty;
        }
        catch (UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private static void CleanupOldLaunchLogs()
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-1);
            foreach (var path in Directory.EnumerateFiles(
                         Path.GetTempPath(),
                         "nicewindows-admin-launch-*.log"))
            {
                if (File.GetLastWriteTimeUtc(path) < cutoff)
                {
                    TryDeleteLaunchLog(path);
                }
            }
        }
        catch (IOException)
        {
            // A launch must not fail because old diagnostic logs are unavailable.
        }
        catch (UnauthorizedAccessException)
        {
            // A launch must not fail because old diagnostic logs are unavailable.
        }
    }

    private static void TryDeleteLaunchLog(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // A detached child can briefly retain the inherited log handle. A later
            // launch cleans up logs older than one day.
        }
        catch (UnauthorizedAccessException)
        {
            // Diagnostic cleanup is best-effort and must not change launch success.
        }
    }

    private static bool TryRunAttachedBatch(
        AdminAppDefinition app,
        string appFolder,
        string startBatchPath,
        IReadOnlyList<string>? launchArguments,
        out string errorMessage)
    {
        var arguments = $"/d /c call {QuoteCommandArgument(startBatchPath)}" +
                        FormatForwardedArguments(launchArguments) +
                        " || (echo. & echo The app failed to start. Review the error above. & " +
                        "echo Press any key to close this window. & pause ^>NUL)";
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = GetCommandProcessorPath(),
            Arguments = arguments,
            WorkingDirectory = appFolder,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Normal
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

    internal static string GetPowerShellPath()
    {
        var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var powerShellPath = string.IsNullOrWhiteSpace(systemDirectory)
            ? "powershell.exe"
            : Path.Combine(systemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

        if (!File.Exists(powerShellPath))
        {
            throw new FileNotFoundException("Windows PowerShell could not be located.");
        }

        return powerShellPath;
    }

    internal static string EscapePowerShellSingleQuotedString(string value)
    {
        return value.Replace("'", "''", StringComparison.Ordinal);
    }

    private static string FormatForwardedArguments(IReadOnlyList<string>? arguments)
    {
        return arguments is not { Count: > 0 }
            ? string.Empty
            : " " + string.Join(" ", arguments.Select(QuoteCommandArgument));
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
            var batchCommand = GetBatchCommand(app) +
                               FormatConfiguredArguments(AdminAppLaunchSettings.GetArguments(app));
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

        if (!string.IsNullOrWhiteSpace(app.AutoStartLauncherRelativePath))
        {
            return GetWindowlessScriptCommand(app) +
                   FormatConfiguredArguments(AdminAppLaunchSettings.GetArguments(app));
        }

        if (!app.PreferBatchStartup
            && !string.IsNullOrWhiteSpace(app.NativeStartupExecutablePath))
        {
            var executablePath = Path.Combine(
                NiceWindowsRepositoryLocator.GetRepositoryRoot(),
                app.NativeStartupExecutablePath);
            if (File.Exists(executablePath))
            {
                return QuoteCommandArgument(Path.GetFullPath(executablePath));
            }
        }

        return GetBatchCommand(app) +
               FormatConfiguredArguments(AdminAppLaunchSettings.GetArguments(app));
    }

    private static string GetWindowlessScriptCommand(AdminAppDefinition app)
    {
        var appFolder = NiceWindowsRepositoryLocator.GetAppFolder(app);
        var launcherPath = Path.GetFullPath(
            Path.Combine(appFolder, app.AutoStartLauncherRelativePath!));
        var normalizedAppFolder =
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(appFolder))
            + Path.DirectorySeparatorChar;
        if (!launcherPath.StartsWith(normalizedAppFolder, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(launcherPath))
        {
            throw new FileNotFoundException(
                $"Cannot configure startup for {app.DisplayName}: its windowless launcher was not found at " +
                $"'{launcherPath}'.",
                launcherPath);
        }

        var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var scriptHostPath = string.IsNullOrWhiteSpace(systemDirectory)
            ? "wscript.exe"
            : Path.Combine(systemDirectory, "wscript.exe");
        if (!File.Exists(scriptHostPath))
        {
            throw new FileNotFoundException("Windows Script Host wscript.exe could not be located.");
        }

        return $"{QuoteCommandArgument(Path.GetFullPath(scriptHostPath))} " +
               QuoteCommandArgument(launcherPath);
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


    private static string FormatConfiguredArguments(IReadOnlyList<string> arguments)
    {
        return arguments.Count == 0
            ? string.Empty
            : " " + string.Join(" ", arguments.Select(QuoteCommandArgument));
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
