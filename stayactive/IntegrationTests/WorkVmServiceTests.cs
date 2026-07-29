using StayActive;
using System.Text;

namespace stayactive.IntegrationTests;

public sealed class WorkVmServiceTests : IDisposable
{
    private readonly string _repoRoot;
    private readonly FakeWorkVmProcessRunner _runner = new();

    public WorkVmServiceTests()
    {
        _repoRoot = Path.Combine(Path.GetTempPath(), "stayactive-workvm-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_repoRoot, "workvm", "scripts"));
        CreateScript("37-repair-bluetooth-passthrough.ps1");
        CreateScript("33-return-laptop-bluetooth-to-host.ps1");
        CreateScript("34-start-workvm-ready.ps1");
    }

    [Fact]
    public void StartVmReady_StartsReadyScriptWithoutElevation()
    {
        var service = new WorkVmService(_runner, _repoRoot);

        service.StartVmReady();

        var start = Assert.Single(_runner.Starts);
        Assert.Equal("powershell.exe", start.FileName);
        Assert.Contains("-EncodedCommand ", start.Arguments);
        Assert.False(start.Elevated);

        var command = DecodeEncodedCommand(start.Arguments);
        Assert.Contains("34-start-workvm-ready.ps1", command);
        Assert.Contains("-VMName 'WorkRDP'", command);
        Assert.Contains("WorkVM launch failed:", command);
        Assert.Contains("Read-Host 'Press Enter to close'", command);
    }

    [Fact]
    public void PassBluetoothToVm_StartsElevatedHandoffScript()
    {
        var service = new WorkVmService(_runner, _repoRoot);

        service.PassBluetoothToVm();

        var run = Assert.Single(_runner.WaitedRuns);
        Assert.Contains("37-repair-bluetooth-passthrough.ps1", run.Arguments);
        Assert.True(run.Elevated);
        Assert.True(run.Timeout >= TimeSpan.FromMinutes(15));
    }

    [Fact]
    public void ReturnBluetoothToLaptop_StartsElevatedReturnScript()
    {
        var service = new WorkVmService(_runner, _repoRoot);

        service.ReturnBluetoothToLaptop();

        var run = Assert.Single(_runner.WaitedRuns);
        Assert.Contains("33-return-laptop-bluetooth-to-host.ps1", run.Arguments);
        Assert.True(run.Elevated);
        Assert.True(run.Timeout >= TimeSpan.FromMinutes(15));
    }

    [Fact]
    public void PassBluetoothToVm_WhenScriptFails_SurfacesLastLoggedError()
    {
        Directory.CreateDirectory(Path.Combine(_repoRoot, "workvm", ".cache"));
        File.WriteAllText(
            Path.Combine(_repoRoot, "workvm", ".cache", "bluetooth-passthrough-repair.log"),
            "[2026-07-29 08:00:00] setup\n" +
            "[2026-07-29 08:00:01] ERROR: Exact Bluetooth attachment failed.\n");
        _runner.WaitException = new InvalidOperationException("powershell.exe exited with code 1.");
        var service = new WorkVmService(_runner, _repoRoot);

        var exception = Assert.Throws<InvalidOperationException>(() => service.PassBluetoothToVm());

        Assert.Equal("Exact Bluetooth attachment failed.", exception.Message);
    }

    [Fact]
    public void EnsureLaptopBluetoothEnabled_StartsElevatedEnableCommand()
    {
        var service = new WorkVmService(_runner, _repoRoot);

        service.EnsureLaptopBluetoothEnabled();

        var start = Assert.Single(_runner.Starts);
        Assert.Equal("powershell.exe", start.FileName);
        Assert.Contains("-EncodedCommand ", start.Arguments);
        Assert.True(start.Elevated);

        var command = DecodeEncodedCommand(start.Arguments);
        Assert.Contains("Start-Service", command);
        Assert.Contains("USB\\VID_13D3&PID_3602&MI_00*", command);
        Assert.Contains("pnputil /enable-device", command);
        Assert.Contains("pnputil /scan-devices", command);
    }

    [Fact]
    public void GetStatus_WhenBluetoothUsbAttachedToVm_ReportsVm()
    {
        _runner.CapturedOutputByArgument["showvminfo \"WorkRDP\" --machinereadable"] = "VMState=\"running\"";
        _runner.CapturedOutputByArgument["showvminfo \"WorkRDP\""] = """
            Currently attached USB devices:

            UUID: 25fc3ad5-de61-4499-9cd0-622ab8b19cea
            VendorId: 0x13d3 (13D3)
            ProductId: 0x3602 (3602)
            Product: Wireless_Device
            """;
        var service = new WorkVmService(_runner, _repoRoot);

        var status = service.GetStatus();

        Assert.Equal(BluetoothControlTarget.Vm, status.BluetoothControlTarget);
    }

    [Fact]
    public void GetStatus_WhenAttachedUsbUsesWindowsLineEndings_ReportsVm()
    {
        _runner.CapturedOutputByArgument["showvminfo \"WorkRDP\" --machinereadable"] =
            "VMState=\"running\"\r\n";
        _runner.CapturedOutputByArgument["showvminfo \"WorkRDP\""] =
            "Currently attached USB devices:\r\n" +
            "\r\n" +
            "UUID: 25fc3ad5-de61-4499-9cd0-622ab8b19cea\r\n" +
            "VendorId: 0x13d3 (13D3)\r\n" +
            "ProductId: 0x3602 (3602)\r\n" +
            "Product: Wireless_Device\r\n" +
            "\r\n" +
            "Bandwidth groups: <none>\r\n";
        var service = new WorkVmService(_runner, _repoRoot);

        var status = service.GetStatus();

        Assert.Equal(BluetoothControlTarget.Vm, status.BluetoothControlTarget);
    }

    [Fact]
    public void GetStatus_WhenVendorAndProductIdsAreOnDifferentAttachedDevices_DoesNotReportVm()
    {
        _runner.CapturedOutputByArgument["showvminfo \"WorkRDP\" --machinereadable"] = "VMState=\"running\"";
        _runner.CapturedOutputByArgument["showvminfo \"WorkRDP\""] = """
            Currently attached USB devices:

            UUID: 11111111-1111-1111-1111-111111111111
            VendorId: 0x13d3 (13D3)
            ProductId: 0x9999 (9999)

            UUID: 22222222-2222-2222-2222-222222222222
            VendorId: 0x9999 (9999)
            ProductId: 0x3602 (3602)
            """;
        var service = new WorkVmService(_runner, _repoRoot);

        var status = service.GetStatus();

        Assert.Equal(BluetoothControlTarget.Unknown, status.BluetoothControlTarget);
    }

    [Fact]
    public void GetStatus_WhenAttachedDeviceOnlyHasBluetoothNames_DoesNotReportVm()
    {
        _runner.CapturedOutputByArgument["showvminfo \"WorkRDP\" --machinereadable"] = "VMState=\"running\"";
        _runner.CapturedOutputByArgument["showvminfo \"WorkRDP\""] = """
            Currently attached USB devices:

            UUID: 33333333-3333-3333-3333-333333333333
            VendorId: 0x9999 (9999)
            ProductId: 0x8888 (8888)
            Manufacturer: MediaTek Inc.
            Product: Wireless_Device
            """;
        var service = new WorkVmService(_runner, _repoRoot);

        var status = service.GetStatus();

        Assert.Equal(BluetoothControlTarget.Unknown, status.BluetoothControlTarget);
    }

    [Theory]
    [InlineData("0x13d30", "0x3602")]
    [InlineData("0x13d3", "0x36020")]
    [InlineData("0x13d4", "0x3602")]
    [InlineData("0x13d3", "0x3603")]
    public void GetStatus_WhenAttachedUsbIdsAreNotExact_DoesNotReportVm(
        string vendorId,
        string productId)
    {
        _runner.CapturedOutputByArgument["showvminfo \"WorkRDP\" --machinereadable"] = "VMState=\"running\"";
        _runner.CapturedOutputByArgument["showvminfo \"WorkRDP\""] = $"""
            Currently attached USB devices:

            UUID: 44444444-4444-4444-4444-444444444444
            VendorId: {vendorId}
            ProductId: {productId}
            """;
        var service = new WorkVmService(_runner, _repoRoot);

        var status = service.GetStatus();

        Assert.Equal(BluetoothControlTarget.Unknown, status.BluetoothControlTarget);
    }

    [Fact]
    public void GetStatus_WhenBluetoothUsbCapturedButNotAttached_DoesNotReportVm()
    {
        _runner.CapturedOutputByArgument["showvminfo \"WorkRDP\" --machinereadable"] = "VMState=\"running\"";
        _runner.CapturedOutputByArgument["showvminfo \"WorkRDP\""] = "Currently attached USB devices: <none>";
        _runner.CapturedOutputByArgument["list usbhost"] = """
            UUID:               25fc3ad5-de61-4499-9cd0-622ab8b19cea
            VendorId:           0x13d3 (13D3)
            ProductId:          0x3602 (3602)
            Manufacturer:       IMC Networks
            Current State:      Captured
            """;
        var service = new WorkVmService(_runner, _repoRoot);

        var status = service.GetStatus();

        Assert.Equal(BluetoothControlTarget.Unknown, status.BluetoothControlTarget);
    }

    [Fact]
    public void GetStatus_WhenHostInterfaceIsDisabledButNotAttached_DoesNotReportVm()
    {
        _runner.CapturedOutputByArgument["showvminfo \"WorkRDP\" --machinereadable"] = "VMState=\"running\"";
        _runner.CapturedOutputByArgument["showvminfo \"WorkRDP\""] = "Currently attached USB devices: <none>";
        _runner.HostBluetoothProbeOutput = "Disabled`nCM_PROB_DISABLED";
        var service = new WorkVmService(_runner, _repoRoot);

        var status = service.GetStatus();

        Assert.Equal(BluetoothControlTarget.Unknown, status.BluetoothControlTarget);
    }

    [Fact]
    public void GetStatus_WhenHostParentAndInterfaceArePresentAndHealthy_ReportsLaptop()
    {
        _runner.CapturedOutputByArgument["showvminfo \"WorkRDP\" --machinereadable"] = "VMState=\"running\"";
        _runner.CapturedOutputByArgument["showvminfo \"WorkRDP\""] = "Currently attached USB devices: <none>";
        _runner.HostBluetoothProbeOutput = "STAYACTIVE_BLUETOOTH_HOST_READY";
        var service = new WorkVmService(_runner, _repoRoot);

        var status = service.GetStatus();

        Assert.Equal(BluetoothControlTarget.Laptop, status.BluetoothControlTarget);

        var probeCall = Assert.Single(
            _runner.CapturedRuns,
            call => call.FileName.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase));
        var probeCommand = DecodeEncodedCommand(probeCall.Arguments);
        Assert.Contains("Get-PnpDevice -PresentOnly", probeCommand);
        Assert.Contains("USB\\VID_13D3&PID_3602\\*", probeCommand);
        Assert.Contains("USB\\VID_13D3&PID_3602&MI_00\\*", probeCommand);
        Assert.Contains("[string]$Device.Status -eq 'OK'", probeCommand);
        Assert.Contains("$problem -eq 'CM_PROB_NONE'", probeCommand);
        Assert.Contains("$parents.Count -eq 1", probeCommand);
        Assert.Contains("$interfaces.Count -eq 1", probeCommand);
        Assert.Contains("Get-Service -Name 'bthserv'", probeCommand);
        Assert.Contains("[string]$service.Status -eq 'Running'", probeCommand);
    }

    [Theory]
    [InlineData("OK")]
    [InlineData("Started")]
    [InlineData("STAYACTIVE_BLUETOOTH_HOST_READY_BUT_NOT_EXACT")]
    [InlineData("STAYACTIVE_BLUETOOTH_HOST_READY warning")]
    public void GetStatus_WhenHostProbeDoesNotReturnExactHealthyMarker_DoesNotReportLaptop(string output)
    {
        _runner.CapturedOutputByArgument["showvminfo \"WorkRDP\" --machinereadable"] = "VMState=\"running\"";
        _runner.CapturedOutputByArgument["showvminfo \"WorkRDP\""] = "Currently attached USB devices: <none>";
        _runner.HostBluetoothProbeOutput = output;
        var service = new WorkVmService(_runner, _repoRoot);

        var status = service.GetStatus();

        Assert.Equal(BluetoothControlTarget.Unknown, status.BluetoothControlTarget);
    }

    [Fact]
    public void SystemRunner_RunAndCapture_DrainsLargeOutputWithoutDeadlocking()
    {
        var runner = new SystemWorkVmProcessRunner();
        var output = runner.RunAndCapture(
            "powershell.exe",
            "-NoProfile -Command \"[Console]::Out.Write(('x' * 131072))\"",
            TimeSpan.FromSeconds(10));

        Assert.NotNull(output);
        Assert.Equal(131072, output.Length);
    }

    [Fact]
    public void SystemRunner_RunAndWait_WhenProcessExitsNonZero_Throws()
    {
        var runner = new SystemWorkVmProcessRunner();
        var exception = Assert.Throws<InvalidOperationException>(() => runner.RunAndWait(
            "powershell.exe",
            "-NoProfile -Command \"exit 23\"",
            elevated: false,
            TimeSpan.FromSeconds(10)));

        Assert.Contains("23", exception.Message);
    }

    [Fact]
    public void SystemRunner_RunAndWait_WhenProcessTimesOut_Throws()
    {
        var runner = new SystemWorkVmProcessRunner();
        Assert.Throws<TimeoutException>(() => runner.RunAndWait(
            "powershell.exe",
            "-NoProfile -Command \"Start-Sleep -Seconds 5\"",
            elevated: false,
            TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public void GetStatus_WhenBluetoothOnlyAppearsInUsbFilter_DoesNotReportVm()
    {
        _runner.CapturedOutputByArgument["showvminfo \"WorkRDP\" --machinereadable"] = "VMState=\"running\"";
        _runner.CapturedOutputByArgument["showvminfo \"WorkRDP\""] = """
            Currently attached USB devices: <none>

            USB Device Filters:

            Index:            0
            Active:           yes
            Name:             Laptop MediaTek Bluetooth Adapter VIDPID
            VendorId:         13d3
            ProductId:        3602
            """;
        _runner.CapturedOutputByArgument["list usbhost"] = """
            UUID:               25fc3ad5-de61-4499-9cd0-622ab8b19cea
            VendorId:           0x13d3 (13D3)
            ProductId:          0x3602 (3602)
            Current State:      Busy
            """;
        _runner.HostBluetoothProbeOutput = "STAYACTIVE_BLUETOOTH_HOST_READY";
        var service = new WorkVmService(_runner, _repoRoot);

        var status = service.GetStatus();

        Assert.Equal(BluetoothControlTarget.Laptop, status.BluetoothControlTarget);
    }

    public void Dispose()
    {
        Directory.Delete(_repoRoot, recursive: true);
    }

    private void CreateScript(string fileName)
    {
        File.WriteAllText(Path.Combine(_repoRoot, "workvm", "scripts", fileName), "# test");
    }

    private static string DecodeEncodedCommand(string arguments)
    {
        var encodedCommand = arguments[(arguments.IndexOf("-EncodedCommand ", StringComparison.Ordinal) + "-EncodedCommand ".Length)..];
        return Encoding.Unicode.GetString(Convert.FromBase64String(encodedCommand));
    }

    private sealed class FakeWorkVmProcessRunner : IWorkVmProcessRunner
    {
        public List<StartCall> Starts { get; } = new();

        public List<RunAndWaitCall> WaitedRuns { get; } = new();

        public List<CaptureCall> CapturedRuns { get; } = new();

        public Dictionary<string, string> CapturedOutputByArgument { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string? HostBluetoothProbeOutput { get; set; }

        public Exception? WaitException { get; set; }

        public string? RunAndCapture(string fileName, string arguments, TimeSpan timeout)
        {
            CapturedRuns.Add(new CaptureCall(fileName, arguments, timeout));

            if (CapturedOutputByArgument.TryGetValue(arguments, out var output))
            {
                return output;
            }

            if (fileName.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase)
                && arguments.Contains("-EncodedCommand ", StringComparison.Ordinal))
            {
                var command = DecodeEncodedCommand(arguments);
                if (command.Contains("Get-PnpDevice -PresentOnly", StringComparison.Ordinal))
                {
                    return HostBluetoothProbeOutput;
                }
            }

            return null;
        }

        public void RunAndWait(string fileName, string arguments, bool elevated, TimeSpan timeout)
        {
            WaitedRuns.Add(new RunAndWaitCall(fileName, arguments, elevated, timeout));
            if (WaitException is not null)
            {
                throw WaitException;
            }
        }

        public void Start(string fileName, string arguments, bool elevated)
        {
            Starts.Add(new StartCall(fileName, arguments, elevated));
        }
    }

    private sealed record RunAndWaitCall(string FileName, string Arguments, bool Elevated, TimeSpan Timeout);

    private sealed record CaptureCall(string FileName, string Arguments, TimeSpan Timeout);

    private sealed record StartCall(string FileName, string Arguments, bool Elevated);
}
