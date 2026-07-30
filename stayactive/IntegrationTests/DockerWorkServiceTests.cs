using StayActive;

namespace stayactive.IntegrationTests;

public sealed class DockerWorkServiceTests : IDisposable
{
    private readonly string _repoRoot;
    private readonly FakeRunner _runner = new();

    public DockerWorkServiceTests()
    {
        _repoRoot = Path.Combine(
            Path.GetTempPath(),
            "stayactive-docker-work-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_repoRoot, "docker-work", "scripts"));
        Directory.CreateDirectory(Path.Combine(_repoRoot, "docker-work", ".state"));
        CreateFile("docker-work", "scripts", "open-work.ps1");
        CreateFile("docker-work", "scripts", "put-bluetooth-on-container.ps1");
        CreateFile("docker-work", "scripts", "put-bluetooth-on-laptop.ps1");
        CreateFile("docker-work", "scripts", "status.ps1");
        CreateFile("docker-work", ".state", "setup-complete.json");
    }

    [Fact]
    public void OpenWithBluetooth_WaitsForElevatedOpenBeforeLaunchingNoVnc()
    {
        var service = new DockerWorkService(_runner, _repoRoot);

        service.OpenWithBluetooth();

        var operation = Assert.Single(_runner.WaitedRuns);
        Assert.Contains("open-work.ps1", operation.Arguments);
        Assert.Contains("-NoOpen", operation.Arguments);
        Assert.True(operation.Elevated);
        Assert.True(operation.Timeout >= TimeSpan.FromMinutes(20));

        var launch = Assert.Single(_runner.Starts);
        Assert.Equal(
            "http://127.0.0.1:6080/vnc.html?autoconnect=1&resize=scale",
            launch.FileName);
        Assert.False(launch.Elevated);
        Assert.Equal(new[] { "wait", "start" }, _runner.Sequence);
    }

    [Fact]
    public void PutBluetoothOnContainer_UsesExactElevatedDockerScript()
    {
        var service = new DockerWorkService(_runner, _repoRoot);

        service.PutBluetoothOnContainer();

        var operation = Assert.Single(_runner.WaitedRuns);
        Assert.Contains("put-bluetooth-on-container.ps1", operation.Arguments);
        Assert.DoesNotContain("37-repair-bluetooth-passthrough.ps1", operation.Arguments);
        Assert.True(operation.Elevated);
    }

    [Fact]
    public void PutBluetoothOnLaptop_UsesExactElevatedDockerScript()
    {
        var service = new DockerWorkService(_runner, _repoRoot);

        service.PutBluetoothOnLaptop();

        var operation = Assert.Single(_runner.WaitedRuns);
        Assert.Contains("put-bluetooth-on-laptop.ps1", operation.Arguments);
        Assert.DoesNotContain("33-return-laptop-bluetooth-to-host.ps1", operation.Arguments);
        Assert.True(operation.Elevated);
    }

    [Fact]
    public void FailedAction_SurfacesLastTimestampedLogError()
    {
        Directory.CreateDirectory(Path.Combine(_repoRoot, "docker-work", ".cache"));
        File.WriteAllText(
            Path.Combine(_repoRoot, "docker-work", ".cache", "docker-work.log"),
            "[2026-07-29 17:00:00] starting\n" +
            "[2026-07-29 17:00:01] ERROR: Exact USB/IP attach failed.\n");
        _runner.WaitException = new InvalidOperationException("exit code 1");
        var service = new DockerWorkService(_runner, _repoRoot);

        var error = Assert.Throws<InvalidOperationException>(
            service.PutBluetoothOnContainer);

        Assert.Equal("Exact USB/IP attach failed.", error.Message);
    }

    [Fact]
    public void GetStatus_ParsesContainerAndLaptopMarkers()
    {
        _runner.CapturedOutput =
            "STAYACTIVE_BLUETOOTH_OWNER=LAPTOP\r\n" +
            "STAYACTIVE_DOCKER_CONTAINER=RUNNING\r\n";
        var service = new DockerWorkService(_runner, _repoRoot);

        var status = service.GetStatus();

        Assert.True(status.SetupComplete);
        Assert.Equal("RUNNING", status.ContainerState);
        Assert.Equal(
            DockerBluetoothControlTarget.Laptop,
            status.BluetoothControlTarget);
        var capture = Assert.Single(_runner.CapturedRuns);
        Assert.Contains("status.ps1", capture.Arguments);
        Assert.InRange(
            capture.Timeout,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void MissingSetupMarker_BlocksContainerAttachButNotLaptopRecovery()
    {
        File.Delete(Path.Combine(
            _repoRoot,
            "docker-work",
            ".state",
            "setup-complete.json"));
        var service = new DockerWorkService(_runner, _repoRoot);

        var error = Assert.Throws<InvalidOperationException>(
            service.PutBluetoothOnContainer);

        Assert.Contains("setup is incomplete", error.Message);
        Assert.Empty(_runner.WaitedRuns);

        service.PutBluetoothOnLaptop();

        var recovery = Assert.Single(_runner.WaitedRuns);
        Assert.Contains(
            "put-bluetooth-on-laptop.ps1",
            recovery.Arguments,
            StringComparison.Ordinal);
        Assert.True(recovery.Elevated);
    }

    public void Dispose()
    {
        Directory.Delete(_repoRoot, recursive: true);
    }

    private void CreateFile(params string[] parts)
    {
        File.WriteAllText(Path.Combine(new[] { _repoRoot }.Concat(parts).ToArray()), "# test");
    }

    private sealed class FakeRunner : IWorkVmProcessRunner
    {
        public List<StartCall> Starts { get; } = new();
        public List<WaitCall> WaitedRuns { get; } = new();
        public List<CaptureCall> CapturedRuns { get; } = new();
        public List<string> Sequence { get; } = new();
        public string? CapturedOutput { get; set; }
        public Exception? WaitException { get; set; }

        public string? RunAndCapture(
            string fileName,
            string arguments,
            TimeSpan timeout)
        {
            CapturedRuns.Add(new CaptureCall(fileName, arguments, timeout));
            return CapturedOutput;
        }

        public void RunAndWait(
            string fileName,
            string arguments,
            bool elevated,
            TimeSpan timeout)
        {
            Sequence.Add("wait");
            WaitedRuns.Add(new WaitCall(fileName, arguments, elevated, timeout));
            if (WaitException is not null)
            {
                throw WaitException;
            }
        }

        public void Start(string fileName, string arguments, bool elevated)
        {
            Sequence.Add("start");
            Starts.Add(new StartCall(fileName, arguments, elevated));
        }
    }

    private sealed record StartCall(
        string FileName,
        string Arguments,
        bool Elevated);

    private sealed record WaitCall(
        string FileName,
        string Arguments,
        bool Elevated,
        TimeSpan Timeout);

    private sealed record CaptureCall(
        string FileName,
        string Arguments,
        TimeSpan Timeout);
}
