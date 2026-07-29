using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace StayActive;

internal enum BluetoothControlTarget
{
    Unknown,
    Laptop,
    Vm
}

internal sealed record WorkVmStatus(
    bool WorkVmFolderExists,
    bool StartScriptExists,
    bool BluetoothToVmScriptExists,
    bool BluetoothToLaptopScriptExists,
    string? VmState,
    BluetoothControlTarget BluetoothControlTarget);

internal interface IWorkVmProcessRunner
{
    string? RunAndCapture(string fileName, string arguments, TimeSpan timeout);

    void RunAndWait(string fileName, string arguments, bool elevated, TimeSpan timeout);

    void Start(string fileName, string arguments, bool elevated);
}

internal sealed class SystemWorkVmProcessRunner : IWorkVmProcessRunner
{
    public string? RunAndCapture(string fileName, string arguments, TimeSpan timeout)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };

        process.Start();
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            return null;
        }

        return standardOutput.GetAwaiter().GetResult() + standardError.GetAwaiter().GetResult();
    }

    public void RunAndWait(string fileName, string arguments, bool elevated, TimeSpan timeout)
    {
        using var process = new Process
        {
            StartInfo = CreateStartInfo(fileName, arguments, elevated)
        };

        process.Start();
        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
            }
            catch
            {
            }

            throw new TimeoutException(
                $"{Path.GetFileName(fileName)} did not finish within {timeout.TotalMinutes:0.#} minutes.");
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{Path.GetFileName(fileName)} exited with code {process.ExitCode}.");
        }
    }

    public void Start(string fileName, string arguments, bool elevated)
    {
        Process.Start(CreateStartInfo(fileName, arguments, elevated));
    }

    private static ProcessStartInfo CreateStartInfo(string fileName, string arguments, bool elevated)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = true
        };

        if (!elevated)
        {
            return startInfo;
        }

        startInfo.Verb = "runas";
        return startInfo;
    }
}

internal sealed class WorkVmService
{
    private const string DefaultVmName = "WorkRDP";
    private const string BluetoothHardwareId = @"USB\VID_13D3&PID_3602&MI_00";
    private const string HealthyHostBluetoothMarker = "STAYACTIVE_BLUETOOTH_HOST_READY";
    // Both scripts are transactional and restore filters/services in finally
    // blocks. Their nested, bounded VM/PnP checks can exceed ten minutes on a
    // slow host; leave enough headroom so the tray app never kills them during
    // rollback and strands Bluetooth between owners.
    private static readonly TimeSpan BluetoothToVmTimeout = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan BluetoothToLaptopTimeout = TimeSpan.FromMinutes(20);

    private readonly IWorkVmProcessRunner _runner;
    private readonly string _repoRoot;
    private readonly string _vmName;

    public WorkVmService()
        : this(new SystemWorkVmProcessRunner(), GetDefaultRepoRoot(), DefaultVmName)
    {
    }

    internal WorkVmService(IWorkVmProcessRunner runner, string repoRoot, string vmName = DefaultVmName)
    {
        _runner = runner;
        _repoRoot = repoRoot;
        _vmName = vmName;
    }

    public string WorkVmFolder => Path.Combine(_repoRoot, "workvm");

    public string StartScriptPath => Path.Combine(WorkVmFolder, "scripts", "34-start-workvm-ready.ps1");

    public string BluetoothToVmScriptPath => Path.Combine(WorkVmFolder, "scripts", "37-repair-bluetooth-passthrough.ps1");

    public string BluetoothToLaptopScriptPath => Path.Combine(WorkVmFolder, "scripts", "33-return-laptop-bluetooth-to-host.ps1");

    private string BluetoothToVmLogPath => Path.Combine(WorkVmFolder, ".cache", "bluetooth-passthrough-repair.log");

    private string BluetoothToLaptopLogPath => Path.Combine(WorkVmFolder, ".cache", "bluetooth-return-to-host.log");

    public WorkVmStatus GetStatus()
    {
        return new WorkVmStatus(
            Directory.Exists(WorkVmFolder),
            File.Exists(StartScriptPath),
            File.Exists(BluetoothToVmScriptPath),
            File.Exists(BluetoothToLaptopScriptPath),
            GetVmState(),
            GetBluetoothControlTarget());
    }

    public void StartVmReady()
    {
        EnsureScriptExists(StartScriptPath);
        _runner.Start(
            "powershell.exe",
            "-NoProfile -ExecutionPolicy Bypass -EncodedCommand " + EncodePowerShellCommand(
                BuildScriptCommandWithErrorPrompt(
                    StartScriptPath,
                    ("VMName", _vmName))),
            elevated: false);
    }

    public void PassBluetoothToVm()
    {
        EnsureScriptExists(BluetoothToVmScriptPath);
        RunBluetoothScriptAndWait(
            BluetoothToVmScriptPath,
            BluetoothToVmLogPath,
            BluetoothToVmTimeout);
    }

    public void ReturnBluetoothToLaptop()
    {
        EnsureScriptExists(BluetoothToLaptopScriptPath);
        RunBluetoothScriptAndWait(
            BluetoothToLaptopScriptPath,
            BluetoothToLaptopLogPath,
            BluetoothToLaptopTimeout);
    }

    public void EnsureLaptopBluetoothEnabled()
    {
        _runner.Start(
            "powershell.exe",
            "-NoProfile -ExecutionPolicy Bypass -EncodedCommand " + EncodePowerShellCommand(BuildEnableBluetoothCommand()),
            elevated: true);
    }

    private string? GetVmState()
    {
        var vboxManage = GetVBoxManagePath();
        if (vboxManage is null)
        {
            return null;
        }

        var output = _runner.RunAndCapture(
            vboxManage,
            $"showvminfo {Quote(_vmName)} --machinereadable",
            TimeSpan.FromSeconds(3));

        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        foreach (var line in output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
        {
            if (!line.StartsWith("VMState=", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = line["VMState=".Length..].Trim().Trim('"');
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        return null;
    }

    private BluetoothControlTarget GetBluetoothControlTarget()
    {
        var vboxManage = GetVBoxManagePath();
        if (vboxManage is not null)
        {
            var vmInfo = _runner.RunAndCapture(
                vboxManage,
                $"showvminfo {Quote(_vmName)}",
                TimeSpan.FromSeconds(3));

            if (!string.IsNullOrWhiteSpace(vmInfo)
                && ContainsAttachedBluetoothUsb(vmInfo))
            {
                return BluetoothControlTarget.Vm;
            }

        }

        var output = _runner.RunAndCapture(
            "powershell.exe",
            "-NoProfile -ExecutionPolicy Bypass -EncodedCommand " + EncodePowerShellCommand(BuildHostBluetoothProbeCommand()),
            TimeSpan.FromSeconds(3));

        if (string.IsNullOrWhiteSpace(output))
        {
            return BluetoothControlTarget.Unknown;
        }

        if (output
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Any(line => string.Equals(
                line.Trim(),
                HealthyHostBluetoothMarker,
                StringComparison.Ordinal)))
        {
            return BluetoothControlTarget.Laptop;
        }

        return BluetoothControlTarget.Unknown;
    }

    private static bool ContainsAttachedBluetoothUsb(string vmInfo)
    {
        var match = Regex.Match(
            vmInfo,
            @"Currently attached USB devices:\s*(?<devices>[\s\S]*?)(?:\r?\nBandwidth groups:|\r?\nShared folders:|\r?\nVRDE:|\r?\nUSB Device Filters:|\z)",
            RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return false;
        }

        var attachedSection = match.Groups["devices"].Value;
        if (attachedSection.Contains("<none>", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var deviceBlocks = Regex.Matches(
            attachedSection,
            @"^[ \t]*UUID[ \t]*:.*?(?=^[ \t]*UUID[ \t]*:|\z)",
            RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Singleline);

        return deviceBlocks.Any(block =>
            ContainsExactUsbId(block.Value, "VendorId", "13d3")
            && ContainsExactUsbId(block.Value, "ProductId", "3602"));
    }

    private static bool ContainsExactUsbId(string deviceBlock, string fieldName, string expectedHexValue)
    {
        return Regex.IsMatch(
            deviceBlock,
            $@"^[ \t]*{Regex.Escape(fieldName)}[ \t]*:[ \t]*(?:0x)?{Regex.Escape(expectedHexValue)}[ \t]*(?:\([^)\r\n]+\))?[ \t]*\r?$",
            RegexOptions.IgnoreCase | RegexOptions.Multiline);
    }

    private string? GetVBoxManagePath()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Oracle", "VirtualBox", "VBoxManage.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Oracle", "VirtualBox", "VBoxManage.exe")
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string GetDefaultRepoRoot()
    {
        var appDirectory = AppContext.BaseDirectory;
        var current = new DirectoryInfo(appDirectory);

        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "workvm"))
                || Directory.Exists(Path.Combine(current.FullName, "stayactive")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return Path.GetFullPath(Path.Combine(appDirectory, "..", "..", "..", ".."));
    }

    private static void EnsureScriptExists(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Required WorkVM script was not found.", path);
        }
    }

    private void RunBluetoothScriptAndWait(string scriptPath, string logPath, TimeSpan timeout)
    {
        try
        {
            _runner.RunAndWait(
                "powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -File {Quote(scriptPath)} -VMName {Quote(_vmName)}",
                elevated: true,
                timeout);
        }
        catch (Exception exception) when (exception is InvalidOperationException or TimeoutException)
        {
            var loggedError = TryReadLastScriptError(logPath);
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
                if (markerIndex >= 0)
                {
                    var message = line[(markerIndex + "] ERROR: ".Length)..].Trim();
                    return string.IsNullOrWhiteSpace(message) ? null : message;
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static string Quote(string value)
    {
        return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    private static string EncodePowerShellCommand(string command)
    {
        return Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
    }

    private static string BuildScriptCommandWithErrorPrompt(string scriptPath, params (string Name, string Value)[] parameters)
    {
        var arguments = new StringBuilder()
            .Append("& ")
            .Append(PowerShellSingleQuote(scriptPath));

        foreach (var (name, value) in parameters)
        {
            arguments
                .Append(" -")
                .Append(name)
                .Append(' ')
                .Append(PowerShellSingleQuote(value));
        }

        return $$"""
            $ErrorActionPreference = 'Stop'

            try {
                {{arguments}}
                if ($LASTEXITCODE -ne $null -and $LASTEXITCODE -ne 0) {
                    throw "WorkVM script exited with code $LASTEXITCODE."
                }

                exit 0
            }
            catch {
                Write-Host ''
                Write-Host 'WorkVM launch failed:' -ForegroundColor Red
                Write-Host $_.Exception.Message -ForegroundColor Red
                Write-Host ''
                Read-Host 'Press Enter to close'
                exit 1
            }
            """;
    }

    private static string PowerShellSingleQuote(string value)
    {
        return "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    }

    private static string BuildEnableBluetoothCommand()
    {
        return """
            $ErrorActionPreference = 'Stop'

            Get-Service -Name bthserv,BluetoothUserService* -ErrorAction SilentlyContinue |
                Where-Object { $_.Status -ne 'Running' } |
                Start-Service

            $device = Get-PnpDevice |
                Where-Object { $_.InstanceId -like 'USB\VID_13D3&PID_3602&MI_00*' } |
                Select-Object -First 1

            if ($null -eq $device) {
                throw 'MediaTek Bluetooth adapter was not found.'
            }

            if ($device.Status -ne 'OK' -or $device.Problem -eq 'CM_PROB_DISABLED') {
                pnputil /enable-device "$($device.InstanceId)"
                if ($LASTEXITCODE -ne 0) {
                    throw "pnputil failed to enable Bluetooth adapter with exit code $LASTEXITCODE."
                }
            }

            pnputil /scan-devices | Out-Null
            """;
    }

    private static string BuildHostBluetoothProbeCommand()
    {
        return $$"""
            $devices = @(Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue)

            $parents = @($devices |
                Where-Object { $_.InstanceId -like 'USB\VID_13D3&PID_3602\*' })

            $interfaces = @($devices |
                Where-Object { $_.InstanceId -like 'USB\VID_13D3&PID_3602&MI_00\*' })

            function Test-HealthyBluetoothDevice {
                param([object]$Device)

                if ($null -eq $Device) {
                    return $false
                }

                $problem = [string]$Device.Problem
                return (
                    [string]$Device.Status -eq 'OK' -and
                    (
                        [string]::IsNullOrWhiteSpace($problem) -or
                        $problem -eq '0' -or
                        $problem -eq 'CM_PROB_NONE'
                    )
                )
            }

            $service = Get-Service -Name 'bthserv' -ErrorAction SilentlyContinue
            if (
                $parents.Count -eq 1 -and
                $interfaces.Count -eq 1 -and
                (Test-HealthyBluetoothDevice $parents[0]) -and
                (Test-HealthyBluetoothDevice $interfaces[0]) -and
                $null -ne $service -and
                [string]$service.Status -eq 'Running'
            ) {
                '{{HealthyHostBluetoothMarker}}'
            }
            """;
    }
}
