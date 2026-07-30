namespace stayactive.IntegrationTests;

public sealed class DockerWorkScriptSafetyTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void HandoffScripts_UseExactAdapterAndSharedGlobalMutex()
    {
        var common = Read("docker-work", "scripts", "common.ps1");

        Assert.Contains(
            "$script:BluetoothHardwareId = \"13d3:3602\"",
            common,
            StringComparison.Ordinal);
        Assert.Contains(
            "Global\\StayActiveWorkVmBluetoothHandoff",
            common,
            StringComparison.Ordinal);
        Assert.Contains(
            "--hardware-id\", $script:BluetoothHardwareId",
            common,
            StringComparison.Ordinal);
        Assert.DoesNotContain("/remove-device", common, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "Remove-PnpDevice",
            common,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "\"--force\"",
            Read("docker-work", "scripts", "setup.ps1"),
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("open-work.ps1")]
    [InlineData("put-bluetooth-on-container.ps1")]
    [InlineData("put-bluetooth-on-laptop.ps1")]
    [InlineData("stop-work.ps1")]
    public void MutatingScripts_AcquireAndReleaseSharedLock(string fileName)
    {
        var script = Read("docker-work", "scripts", fileName);

        Assert.Contains("Enter-DockerWorkBluetoothLock", script);
        Assert.Contains("Exit-DockerWorkBluetoothLock", script);
        Assert.Contains("finally", script);
    }

    [Theory]
    [InlineData("open-work.ps1")]
    [InlineData("put-bluetooth-on-container.ps1")]
    public void ContainerHandoffs_RollBackToLaptopOnFailure(string fileName)
    {
        var script = Read("docker-work", "scripts", fileName);

        Assert.Contains("catch", script);
        Assert.Contains("Detach-DockerWorkBluetooth", script);
        Assert.DoesNotContain("37-repair-bluetooth-passthrough.ps1", script);
        Assert.DoesNotContain("34-start-workvm-ready.ps1", script);
    }

    [Fact]
    public void CommonRecovery_NeverRebootsThePcOrRemovesPhysicalPnp()
    {
        var scripts = string.Join(
            "\n",
            Directory.GetFiles(
                    Path.Combine(RepositoryRoot, "docker-work", "scripts"),
                    "*.ps1")
                .Select(File.ReadAllText));

        Assert.DoesNotContain("Restart-Computer", scripts, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Stop-Computer", scripts, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("shutdown.exe", scripts, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/remove-device", scripts, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Remove-PnpDevice", scripts, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VirtualBoxReleaseOccursBeforeDockerLock()
    {
        foreach (var fileName in new[]
                 {
                     "open-work.ps1",
                     "put-bluetooth-on-container.ps1",
                     "put-bluetooth-on-laptop.ps1",
                     "stop-work.ps1"
                 })
        {
            var script = Read("docker-work", "scripts", fileName);
            var release = script.IndexOf(
                "Release-VirtualBoxBluetooth",
                StringComparison.Ordinal);
            var acquire = script.IndexOf(
                "Enter-DockerWorkBluetoothLock",
                StringComparison.Ordinal);

            Assert.True(release >= 0);
            Assert.True(acquire > release);
        }
    }

    [Fact]
    public void ContainerDetach_PreventsDbusBluetoothAutoactivation()
    {
        var dockerfile = Read("docker-work", "Dockerfile");
        var detach = Read("docker-work", "container", "prepare-detach.sh");

        Assert.Contains(
            "rm -f /usr/share/dbus-1/system-services/org.bluez.service",
            dockerfile);
        Assert.Contains("while pgrep -x bluetoothd", detach);
        Assert.Contains("if pgrep -x bluetoothd", detach);
        Assert.Contains("btmgmt power off", detach);
    }

    [Fact]
    public void PasskeyTest_UsesAnOriginMatchingItsRelyingPartyId()
    {
        var launcher = Read(
            "docker-work",
            "scripts",
            "open-passkey-test.ps1");
        var page = Read(
            "docker-work",
            "container",
            "passkey-test.html");

        Assert.Contains(
            "http://localhost:8000/",
            launcher,
            StringComparison.Ordinal);
        Assert.Contains(
            "id: \"localhost\"",
            page,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "json/new?http://127.0.0.1:8000/",
            launcher,
            StringComparison.Ordinal);
    }

    private static string Read(params string[] path)
    {
        return File.ReadAllText(
            Path.Combine(new[] { RepositoryRoot }.Concat(path).ToArray()));
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "docker-work"))
                && Directory.Exists(Path.Combine(current.FullName, "stayactive")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
