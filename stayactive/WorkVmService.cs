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

        return process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
    }

    public void Start(string fileName, string arguments, bool elevated)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = true
        };

        if (elevated)
        {
            startInfo.Verb = "runas";
        }

        Process.Start(startInfo);
    }
}

internal sealed class WorkVmService
{
    private const string DefaultVmName = "WorkRDP";
    private const string BluetoothHardwareId = @"USB\VID_13D3&PID_3602&MI_00";

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
        _runner.Start(
            "powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -File {Quote(BluetoothToVmScriptPath)} -VMName {Quote(_vmName)}",
            elevated: true);
    }

    public void ReturnBluetoothToLaptop()
    {
        EnsureScriptExists(BluetoothToLaptopScriptPath);
        _runner.Start(
            "powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -File {Quote(BluetoothToLaptopScriptPath)} -VMName {Quote(_vmName)}",
            elevated: true);
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

            var usbHost = _runner.RunAndCapture(
                vboxManage,
                "list usbhost",
                TimeSpan.FromSeconds(3));

            if (!string.IsNullOrWhiteSpace(usbHost)
                && ContainsCapturedBluetoothUsb(usbHost))
            {
                return BluetoothControlTarget.Vm;
            }
        }

        var output = _runner.RunAndCapture(
            "powershell.exe",
            "-NoProfile -ExecutionPolicy Bypass -Command \"$device = Get-PnpDevice | Where-Object { $_.InstanceId -like 'USB\\VID_13D3&PID_3602&MI_00*' } | Select-Object -First 1; if ($null -ne $device) { [string]$device.Status; [string]$device.Problem }\"",
            TimeSpan.FromSeconds(3));

        if (string.IsNullOrWhiteSpace(output))
        {
            return BluetoothControlTarget.Unknown;
        }

        if (output.Contains("Disabled", StringComparison.OrdinalIgnoreCase))
        {
            return BluetoothControlTarget.Vm;
        }

        if (output.Contains("OK", StringComparison.OrdinalIgnoreCase)
            || output.Contains("Started", StringComparison.OrdinalIgnoreCase))
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

        return attachedSection.Contains("13d3", StringComparison.OrdinalIgnoreCase)
            || attachedSection.Contains("3602", StringComparison.OrdinalIgnoreCase)
            || attachedSection.Contains("Wireless_Device", StringComparison.OrdinalIgnoreCase)
            || attachedSection.Contains("MediaTek", StringComparison.OrdinalIgnoreCase)
            || attachedSection.Contains("IMC Networks", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsCapturedBluetoothUsb(string usbHost)
    {
        var blocks = usbHost.Split(
            new[] { "\r\n\r\n", "\n\n" },
            StringSplitOptions.RemoveEmptyEntries);

        return blocks.Any(block =>
            block.Contains("VendorId:", StringComparison.OrdinalIgnoreCase)
            && block.Contains("0x13d3", StringComparison.OrdinalIgnoreCase)
            && block.Contains("ProductId:", StringComparison.OrdinalIgnoreCase)
            && block.Contains("0x3602", StringComparison.OrdinalIgnoreCase)
            && block.Contains("Current State:", StringComparison.OrdinalIgnoreCase)
            && block.Contains("Captured", StringComparison.OrdinalIgnoreCase));
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
}
