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

        var start = Assert.Single(_runner.Starts);
        Assert.Contains("37-repair-bluetooth-passthrough.ps1", start.Arguments);
        Assert.True(start.Elevated);
    }

    [Fact]
    public void ReturnBluetoothToLaptop_StartsElevatedReturnScript()
    {
        var service = new WorkVmService(_runner, _repoRoot);

        service.ReturnBluetoothToLaptop();

        var start = Assert.Single(_runner.Starts);
        Assert.Contains("33-return-laptop-bluetooth-to-host.ps1", start.Arguments);
        Assert.True(start.Elevated);
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
            VendorId: 13d3
            ProductId: 3602
            Product: Wireless_Device
            """;
        var service = new WorkVmService(_runner, _repoRoot);

        var status = service.GetStatus();

        Assert.Equal(BluetoothControlTarget.Vm, status.BluetoothControlTarget);
    }

    [Fact]
    public void GetStatus_WhenBluetoothUsbCapturedByVirtualBox_ReportsVm()
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

        Assert.Equal(BluetoothControlTarget.Vm, status.BluetoothControlTarget);
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

        public Dictionary<string, string> CapturedOutputByArgument { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string? RunAndCapture(string fileName, string arguments, TimeSpan timeout)
        {
            return CapturedOutputByArgument.TryGetValue(arguments, out var output)
                ? output
                : null;
        }

        public void Start(string fileName, string arguments, bool elevated)
        {
            Starts.Add(new StartCall(fileName, arguments, elevated));
        }
    }

    private sealed record StartCall(string FileName, string Arguments, bool Elevated);
}
