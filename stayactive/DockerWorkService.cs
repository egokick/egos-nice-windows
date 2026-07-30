namespace StayActive;

internal enum DockerBluetoothControlTarget
{
    Unknown,
    Laptop,
    Container,
    VirtualBox
}

internal sealed record DockerWorkStatus(
    bool DockerWorkFolderExists,
    bool SetupComplete,
    bool OpenScriptExists,
    bool BluetoothToContainerScriptExists,
    bool BluetoothToLaptopScriptExists,
    string? ContainerState,
    DockerBluetoothControlTarget BluetoothControlTarget);

internal sealed class DockerWorkService
{
    private static readonly TimeSpan ActionTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(45);
    private const string NoVncUrl =
        "http://127.0.0.1:6080/vnc.html?autoconnect=1&resize=scale";

    private readonly IWorkVmProcessRunner _runner;
    private readonly string _repoRoot;

    public DockerWorkService()
        : this(new SystemWorkVmProcessRunner(), GetDefaultRepoRoot())
    {
    }

    internal DockerWorkService(IWorkVmProcessRunner runner, string repoRoot)
    {
        _runner = runner;
        _repoRoot = repoRoot;
    }

    public string DockerWorkFolder => Path.Combine(_repoRoot, "docker-work");

    public string OpenScriptPath =>
        Path.Combine(DockerWorkFolder, "scripts", "open-work.ps1");

    public string BluetoothToContainerScriptPath =>
        Path.Combine(DockerWorkFolder, "scripts", "put-bluetooth-on-container.ps1");

    public string BluetoothToLaptopScriptPath =>
        Path.Combine(DockerWorkFolder, "scripts", "put-bluetooth-on-laptop.ps1");

    public string StatusScriptPath =>
        Path.Combine(DockerWorkFolder, "scripts", "status.ps1");

    public string SetupMarkerPath =>
        Path.Combine(DockerWorkFolder, ".state", "setup-complete.json");

    private string LogPath =>
        Path.Combine(DockerWorkFolder, ".cache", "docker-work.log");

    public DockerWorkStatus GetStatus()
    {
        var folderExists = Directory.Exists(DockerWorkFolder);
        var setupComplete = File.Exists(SetupMarkerPath);
        var openExists = File.Exists(OpenScriptPath);
        var toContainerExists = File.Exists(BluetoothToContainerScriptPath);
        var toLaptopExists = File.Exists(BluetoothToLaptopScriptPath);

        if (!File.Exists(StatusScriptPath))
        {
            return new DockerWorkStatus(
                folderExists,
                setupComplete,
                openExists,
                toContainerExists,
                toLaptopExists,
                null,
                DockerBluetoothControlTarget.Unknown);
        }

        var output = _runner.RunAndCapture(
            "powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -File {Quote(StatusScriptPath)}",
            StatusTimeout);

        var owner = DockerBluetoothControlTarget.Unknown;
        string? containerState = null;
        foreach (var rawLine in (output ?? string.Empty).Split(
            new[] { "\r\n", "\n" },
            StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            owner = line switch
            {
                "STAYACTIVE_BLUETOOTH_OWNER=LAPTOP" =>
                    DockerBluetoothControlTarget.Laptop,
                "STAYACTIVE_BLUETOOTH_OWNER=CONTAINER" =>
                    DockerBluetoothControlTarget.Container,
                "STAYACTIVE_BLUETOOTH_OWNER=VM" =>
                    DockerBluetoothControlTarget.VirtualBox,
                _ => owner
            };

            const string containerPrefix = "STAYACTIVE_DOCKER_CONTAINER=";
            if (line.StartsWith(containerPrefix, StringComparison.Ordinal))
            {
                containerState = line[containerPrefix.Length..];
            }
        }

        return new DockerWorkStatus(
            folderExists,
            setupComplete,
            openExists,
            toContainerExists,
            toLaptopExists,
            containerState,
            owner);
    }

    public void OpenWithBluetooth()
    {
        EnsureReady();
        RunAction(OpenScriptPath, "-NoOpen");
        _runner.Start(NoVncUrl, string.Empty, elevated: false);
    }

    public void PutBluetoothOnContainer()
    {
        EnsureReady();
        RunAction(BluetoothToContainerScriptPath);
    }

    public void PutBluetoothOnLaptop()
    {
        // Returning the radio is the recovery operation. It must remain
        // available even when setup failed before writing its completion
        // marker or when the container/distro is currently unavailable.
        RunAction(BluetoothToLaptopScriptPath);
    }

    private void EnsureReady()
    {
        if (!File.Exists(SetupMarkerPath))
        {
            throw new InvalidOperationException(
                "Docker work-browser setup is incomplete. Run docker-work\\scripts\\setup.ps1.");
        }
    }

    private void RunAction(string scriptPath, params string[] extraArguments)
    {
        if (!File.Exists(scriptPath))
        {
            throw new FileNotFoundException(
                "Required Docker work-browser script was not found.",
                scriptPath);
        }

        var arguments =
            $"-NoProfile -ExecutionPolicy Bypass -File {Quote(scriptPath)} -NoElevate";
        if (extraArguments.Length > 0)
        {
            arguments += " " + string.Join(" ", extraArguments);
        }

        try
        {
            _runner.RunAndWait(
                "powershell.exe",
                arguments,
                elevated: true,
                ActionTimeout);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or TimeoutException)
        {
            var loggedError = TryReadLastScriptError(LogPath);
            throw new InvalidOperationException(loggedError ?? exception.Message, exception);
        }
    }

    private static string? TryReadLastScriptError(string logPath)
    {
        try
        {
            if (!File.Exists(logPath))
            {
                return null;
            }

            foreach (var line in File.ReadLines(logPath).Reverse())
            {
                var markerIndex = line.IndexOf("] ERROR: ", StringComparison.Ordinal);
                if (markerIndex < 0)
                {
                    continue;
                }

                var message = line[(markerIndex + "] ERROR: ".Length)..].Trim();
                return string.IsNullOrWhiteSpace(message) ? null : message;
            }
        }
        catch
        {
        }

        return null;
    }

    private static string GetDefaultRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "docker-work"))
                || Directory.Exists(Path.Combine(current.FullName, "stayactive")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }

        return Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    }

    private static string Quote(string value)
    {
        return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }
}
