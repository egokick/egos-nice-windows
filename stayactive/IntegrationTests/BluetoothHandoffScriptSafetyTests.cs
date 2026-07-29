using StayActive;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace stayactive.IntegrationTests;

public sealed class BluetoothHandoffScriptSafetyTests
{
    private const string ReturnToLaptopScriptName = "33-return-laptop-bluetooth-to-host.ps1";
    private const string BluetoothToVmScriptName = "37-repair-bluetooth-passthrough.ps1";
    private const string StartVmScriptName = "34-start-workvm-ready.ps1";
    private const string ExactVirtualBoxProxyInstanceId = @"USB\VID_80EE&PID_CAFE\000000000";

    [Theory]
    [InlineData(
        "Open VM",
        "_openWorkVmMenuItem",
        "OpenWorkVm",
        "PassBluetoothToVm",
        BluetoothToVmScriptName)]
    [InlineData(
        "Put Bluetooth on VM",
        "_switchBluetoothToVmMenuItem",
        "SwitchBluetoothToVm",
        "PassBluetoothToVm",
        BluetoothToVmScriptName)]
    [InlineData(
        "Put Bluetooth on laptop",
        "_returnBluetoothToLaptopMenuItem",
        "ReturnBluetoothToLaptop",
        "ReturnBluetoothToLaptop",
        ReturnToLaptopScriptName)]
    public void MenuAction_RoutesToIntendedServiceOperationAndScript(
        string label,
        string menuField,
        string handler,
        string serviceOperation,
        string scriptName)
    {
        var program = ReadRepositoryFile("stayactive", "Program.cs");
        Assert.Contains(
            $"{menuField} = new ToolStripMenuItem(\"{label}\")",
            program,
            StringComparison.Ordinal);
        Assert.Contains(
            $"{menuField}.Click += (_, _) => {handler}();",
            program,
            StringComparison.Ordinal);

        var handlerBody = ExtractBraceBlock(
            program,
            $"private void {handler}()");
        var serviceCalls = Regex.Matches(
                handlerBody,
                @"_workVmService\.(?<operation>[A-Za-z_][A-Za-z0-9_]*)\s*\(")
            .Select(match => match.Groups["operation"].Value)
            .ToArray();
        Assert.Equal(new[] { serviceOperation }, serviceCalls);

        var service = ReadRepositoryFile("stayactive", "WorkVmService.cs");
        var operationBody = ExtractBraceBlock(
            service,
            $"public void {serviceOperation}()");
        var scriptPathProperty = serviceOperation == "ReturnBluetoothToLaptop"
            ? "BluetoothToLaptopScriptPath"
            : "BluetoothToVmScriptPath";
        Assert.Contains(scriptPathProperty, operationBody, StringComparison.Ordinal);
        Assert.Matches(
            $@"{Regex.Escape(scriptPathProperty)}\s*=>[\s\S]*?""{Regex.Escape(scriptName)}""",
            service);
    }

    [Fact]
    public void BluetoothMenuActionFailuresSurfaceTheUnderlyingScriptMessage()
    {
        var program = ReadRepositoryFile("stayactive", "Program.cs");
        var actionBody = ExtractBraceBlock(
            program,
            "private void BeginWorkVmAction(");
        Assert.Contains("Task.Run(action)", actionBody, StringComparison.Ordinal);
        Assert.Contains("if (task.Exception is not null)", actionBody, StringComparison.Ordinal);
        Assert.Contains("task.Exception.GetBaseException().Message", actionBody, StringComparison.Ordinal);
        Assert.Contains("ShowErrorBalloon", actionBody, StringComparison.Ordinal);

        foreach (var testCase in new[]
                 {
                     new ScriptFailureCase(
                         BluetoothToVmScriptName,
                         "bluetooth-passthrough-repair.log",
                         "VM Bluetooth handoff failed.",
                         static service => service.PassBluetoothToVm()),
                     new ScriptFailureCase(
                         ReturnToLaptopScriptName,
                         "bluetooth-return-to-host.log",
                         "Laptop Bluetooth return failed.",
                         static service => service.ReturnBluetoothToLaptop())
                 })
        {
            var repoRoot = Path.Combine(
                Path.GetTempPath(),
                "stayactive-script-failure-tests-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(repoRoot, "workvm", "scripts"));
                Directory.CreateDirectory(Path.Combine(repoRoot, "workvm", ".cache"));
                File.WriteAllText(
                    Path.Combine(repoRoot, "workvm", "scripts", testCase.ScriptName),
                    "# test");
                File.WriteAllText(
                    Path.Combine(repoRoot, "workvm", ".cache", testCase.LogName),
                    $"[2026-07-29 08:00:00] ERROR: {testCase.LoggedError}\n");

                var runner = new FailingProcessRunner();
                var service = new WorkVmService(runner, repoRoot);

                var exception = Assert.Throws<InvalidOperationException>(
                    () => testCase.Invoke(service));

                Assert.Equal(testCase.LoggedError, exception.Message);
                var run = Assert.Single(runner.WaitedRuns);
                Assert.Contains(testCase.ScriptName, run.Arguments, StringComparison.Ordinal);
                Assert.True(run.Elevated);
            }
            finally
            {
                if (Directory.Exists(repoRoot))
                {
                    Directory.Delete(repoRoot, recursive: true);
                }
            }
        }
    }

    [Fact]
    public void BluetoothToVmScript_StartsVmOnlyThroughAuditedReadyScript()
    {
        var script = ReadWorkVmScript(BluetoothToVmScriptName);
        Assert.Contains(
            $"$StartReadyScriptPath = Join-Path $PSScriptRoot \"{StartVmScriptName}\"",
            script,
            StringComparison.Ordinal);

        var startVmFunction = ExtractBraceBlock(script, "function Start-WorkVm");
        Assert.Contains("-FileName \"powershell.exe\"", startVmFunction, StringComparison.Ordinal);
        Assert.Contains("$StartReadyScriptPath", startVmFunction, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkVmLaunchScripts_EnforceColdBootAndSeparatedInputProfile()
    {
        var startScript = ReadWorkVmScript(StartVmScriptName);

        Assert.Contains(
            "$state -in @(\"saved\", \"aborted-saved\")",
            startScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "@(\"discardstate\", $VMName)",
            startScript,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Resuming it without changing powered-off-only settings",
            startScript,
            StringComparison.Ordinal);
        Assert.Contains("\"--cpus=4\"", startScript, StringComparison.Ordinal);
        Assert.Contains("\"--mouse=ps2\"", startScript, StringComparison.Ordinal);
        Assert.Contains("\"--keyboard=ps2\"", startScript, StringComparison.Ordinal);
        Assert.Contains("\"--usb-ohci=off\"", startScript, StringComparison.Ordinal);
        Assert.Contains("\"--usb-xhci=on\"", startScript, StringComparison.Ordinal);
        Assert.Contains(
            "\"GUI/DefaultCloseAction\", \"Shutdown\"",
            startScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"GUI/RestrictedCloseActions\", \"SaveState\"",
            startScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"GUI/RestrictedRuntimeMachineMenuActions\", \"SaveState\"",
            startScript,
            StringComparison.Ordinal);

        var bluetoothScript = ReadWorkVmScript(BluetoothToVmScriptName);
        var prepareFilter = ExtractBraceBlock(
            bluetoothScript,
            "function Prepare-BluetoothUsbFilter");
        Assert.Contains("\"--mouse\", \"ps2\"", prepareFilter, StringComparison.Ordinal);
        Assert.Contains("\"--keyboard\", \"ps2\"", prepareFilter, StringComparison.Ordinal);
        Assert.Contains("\"--usb-ohci\", \"off\"", prepareFilter, StringComparison.Ordinal);
        Assert.Contains("\"--usb-xhci\", \"on\"", prepareFilter, StringComparison.Ordinal);
        Assert.DoesNotContain("\"--usb\", \"on\"", prepareFilter, StringComparison.Ordinal);

        var creationScript = ReadWorkVmScript("20-create-vm.ps1");
        Assert.Contains("[int]$CPUs = 4", creationScript, StringComparison.Ordinal);
        Assert.Contains("\"--mouse=ps2\"", creationScript, StringComparison.Ordinal);
        Assert.Contains("\"--keyboard=ps2\"", creationScript, StringComparison.Ordinal);
        Assert.Contains("\"--usb-ohci=off\"", creationScript, StringComparison.Ordinal);
        Assert.Contains("\"--usb-xhci=on\"", creationScript, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkVmMaintenanceScripts_NeverCreateSavedMemoryState()
    {
        foreach (var scriptName in new[]
                 {
                     StartVmScriptName,
                     BluetoothToVmScriptName,
                     "40-start-vm.ps1",
                     "98-force-bluetooth-passthrough-test.ps1"
                 })
        {
            var script = ReadWorkVmScript(scriptName);
            Assert.DoesNotMatch(
                @"(?im)^\s*(?:&\s*)?.*\bcontrolvm\b.*\bsavestate\b",
                script);
        }
    }

    [Fact]
    public void NativeVirtualBoxTest_IsOneTimeBackedUpAndDoesNotRestartHost()
    {
        var script = ReadWorkVmScript(
            "41-prepare-one-time-native-virtualbox-boot.ps1");

        Assert.Contains("\"/export\", $backupPath", script, StringComparison.Ordinal);
        Assert.Contains(
            "\"hypervisorlaunchtype\", \"Off\"",
            script,
            StringComparison.Ordinal);
        Assert.Contains("\"vsmlaunchtype\", \"Off\"", script, StringComparison.Ordinal);
        Assert.Contains("\"/bootsequence\", $script:TestEntry", script, StringComparison.Ordinal);
        Assert.Contains("Suspend-BitLocker", script, StringComparison.Ordinal);
        Assert.Contains("-RebootCount 1", script, StringComparison.Ordinal);
        Assert.DoesNotMatch(
            @"(?im)^\s*(?:Restart-Computer|shutdown(?:\.exe)?\s+.*(?:/r|/g))\b",
            script);
    }

    [Theory]
    [InlineData("Open VM", BluetoothToVmScriptName)]
    [InlineData("Open VM", StartVmScriptName)]
    [InlineData("Put Bluetooth on VM", BluetoothToVmScriptName)]
    [InlineData("Put Bluetooth on VM", StartVmScriptName)]
    [InlineData("Put Bluetooth on laptop", ReturnToLaptopScriptName)]
    public void MenuReachableScript_NeverRemovesPhysicalBluetoothOrIssuesSystemReboot(
        string menuAction,
        string scriptName)
    {
        var script = ReadWorkVmScript(scriptName);
        var systemRebootCommand = Regex.Match(
            script,
            """
            (?imx)
            ^\s*
            (?:
                Restart-Computer \b |
                Stop-Computer \b |
                shutdown(?:\.exe)? \b [^\r\n]* (?:/r|/g) \b |
                InitiateSystemShutdown(?:Ex)? \b |
                ExitWindowsEx \b
            )
            """);
        Assert.False(
            systemRebootCommand.Success,
            $"{menuAction} reaches {scriptName}, which contains a host reboot command: {systemRebootCommand.Value}");

        Assert.DoesNotMatch(
            @"(?im)^\s*(?:&\s*)?(?:devcon(?:\.exe)?\s+remove|Remove-PnpDevice\b)",
            script);
        Assert.DoesNotMatch(
            @"(?is)\[ValidateSet\([^\]]*[""']remove-device[""'][^\]]*\)\]",
            script);

        var removeInvocationPattern = new Regex(
            """
            (?ix)
            (?:
                -Arguments \s+ @\( \s* "/remove-device"
                |
                (?:&\s*)? pnputil(?:\.exe)? \s+ /remove-device
            )
            """);
        var allRemoveInvocations = removeInvocationPattern.Matches(script).Count;
        var removalFunctions = ExtractPowerShellFunctions(script)
            .Where(function => removeInvocationPattern.IsMatch(function.Body))
            .ToArray();
        Assert.Equal(
            allRemoveInvocations,
            removalFunctions.Sum(function => removeInvocationPattern.Matches(function.Body).Count));

        foreach (var function in removalFunctions)
        {
            var removeTargets = Regex.Matches(
                    function.Body,
                    """
                    (?ix)
                    -Arguments \s+ @\(
                        \s* "/remove-device" \s* ,
                        \s* (?<target> [^,\r\n\)]+)
                    """)
                .Select(match => match.Groups["target"].Value.Trim())
                .ToArray();
            Assert.NotEmpty(removeTargets);
            Assert.All(
                removeTargets,
                target => Assert.Equal("$proxyDevice.InstanceId", target));
            Assert.Contains(
                ExactVirtualBoxProxyInstanceId,
                function.Body,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                @"USB\VID_13D3&PID_3602",
                function.Body,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("/subtree", function.Body, StringComparison.OrdinalIgnoreCase);
            Assert.Matches(@"\.ExitCode\s+-eq\s+3010", function.Body);
            Assert.Matches(@"\.Output\s+-match", function.Body);
            Assert.Matches(
                @"(?is)(?:reboot|restart).*(?:required|needed)|(?:required|needed).*(?:reboot|restart)",
                function.Body);
            Assert.Matches(
                @"(?is)if\s*\(\s*\$[A-Za-z_][A-Za-z0-9_]*\s*\)\s*\{\s*throw\b",
                function.Body);
        }
    }

    [Fact]
    public void ReturnToLaptop_RemoveDeviceTargetsOnlyExactVirtualBoxProxy()
    {
        var script = ReadWorkVmScript(ReturnToLaptopScriptName);
        var removeTargets = Regex.Matches(
                script,
                """
                (?ix)
                -Arguments \s+ @\(
                    \s* "/remove-device" \s* ,
                    \s* (?<target> [^,\r\n\)]+)
                """)
            .Select(match => match.Groups["target"].Value.Trim())
            .ToArray();

        Assert.NotEmpty(removeTargets);
        Assert.All(
            removeTargets,
            target => Assert.Equal("$proxyDevice.InstanceId", target));
        Assert.Contains(
            "$_.InstanceId -eq \"USB\\VID_80EE&PID_CAFE\\000000000\"",
            script,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "\"/remove-device\", $proxyDevice.InstanceId, \"/subtree\"",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "$stillAttached = @(Get-AttachedBluetoothDevices)",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ReturnToLaptop_RemoveDeviceRebootRequiredResultIsFatal()
    {
        var script = ReadWorkVmScript(ReturnToLaptopScriptName);
        var removalResultNames = Regex.Matches(
                script,
                """
                (?ixs)
                \$(?<result> [A-Za-z_][A-Za-z0-9_]*)
                \s* = \s* Invoke-ProcessTimed
                (?:(?! Invoke-ProcessTimed ).)*?
                -Arguments \s+ @\(
                    \s* "/remove-device" \s* ,
                    .*?
                \)
                """)
            .Select(match => match.Groups["result"].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(removalResultNames);
        foreach (var resultName in removalResultNames)
        {
            Assert.Matches(
                $@"(?m)if\s*\(\s*-not\s+\${Regex.Escape(resultName)}\.Success\s*\)\s*\{{",
                script);
            Assert.DoesNotMatch(
                $@"\${Regex.Escape(resultName)}\.ExitCode\s+-ne\s+3010",
                script);
        }

        Assert.Contains(
            "$proxyRemove.Output -match",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "(?:reboot|restart).*(?:required|needed)",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task BluetoothToVm_ObservedBusyNoSerialInventory_UsesActiveFilterReenumeration()
    {
        const string nativeUuid = "97f8dd47-2fc5-4609-8e25-56856f0f4f72";
        const string staleHeldUuid = "aa713f30-9438-4c38-b309-e9d401360dcc";
        var scriptPath = FindRepositoryFile(
            "workvm",
            "scripts",
            BluetoothToVmScriptName);
        var escapedScriptPath = scriptPath.Replace("'", "''", StringComparison.Ordinal);
        var harnessPath = Path.Combine(
            Path.GetTempPath(),
            "stayactive-bluetooth-busy-no-serial-" + Guid.NewGuid().ToString("N") + ".ps1");
        var harness = $$"""
            $ErrorActionPreference = "Stop"
            . '{{escapedScriptPath}}' -LibraryMode

            function Write-Log { param([string]$Message) }
            function Remove-GlobalBluetoothHoldFilter {}
            function Wait-VirtualBoxBluetoothProxyReady {
                param([int]$TimeoutSeconds)
                return $null
            }
            function Add-GlobalBluetoothHoldFilter {
                throw "The obsolete global Hold path must not run for the observed Busy/no-serial inventory."
            }
            function Rebind-HostBluetoothForVirtualBoxCapture {
                if (-not $script:filterActive) {
                    throw "The exact WorkVM filter was not activated before re-enumeration."
                }
                $script:attached = $true
                return $null
            }
            function Set-BluetoothVmFilterActive {
                param([bool]$Active)
                $script:filterActive = $Active
            }
            function Get-HostBluetoothPhysicalDevice {
                return [pscustomobject]@{
                    InstanceId = "USB\VID_13D3&PID_3602\000000000"
                    Status = "OK"
                    Problem = "CM_PROB_NONE"
                }
            }

            $script:attached = $false
            $script:attachUuid = ""
            $script:filterActive = $false
            $script:inventory = @'
            UUID:               {{nativeUuid}}
            VendorId:           0x13d3 (13D3)
            ProductId:          0x3602 (3602)
            Revision:           1.0 (0100)
            Port:               3
            Manufacturer:       IMC Networks
            Address:            {36fc9e60-c465-11cf-8056-444553540000}\0014
            Current State:      Busy

            UUID:               {{staleHeldUuid}}
            VendorId:           0x13d3 (13D3)
            ProductId:          0x3602 (3602)
            Revision:           1.0 (0100)
            Port:               3
            Manufacturer:       IMC Networks
            Current State:      Held
            '@

            function Invoke-VBox {
                param(
                    [string[]]$Arguments,
                    [int]$TimeoutSeconds = 20,
                    [switch]$AllowFail
                )
                if ($Arguments[0] -eq "list" -and $Arguments[1] -eq "usbhost") {
                    return [pscustomobject]@{
                        Success = $true
                        ExitCode = 0
                        Output = $script:inventory
                    }
                }
                if ($Arguments[0] -eq "controlvm" -and $Arguments[2] -eq "usbattach") {
                    throw "Busy Bluetooth must not queue a direct usbattach request."
                }
                throw "Unexpected VBoxManage call: $($Arguments -join ' ')"
            }

            function Get-AttachedBluetoothDevice {
                if (-not $script:attached) {
                    return $null
                }
                return [pscustomobject]@{
                    Uuid = "{{nativeUuid}}"
                    VendorId = "13d3"
                    ProductId = "3602"
                }
            }
            function Wait-BluetoothAttachment {
                param([int]$TimeoutSeconds)
                return Get-AttachedBluetoothDevice
            }

            $result = Attach-BluetoothToVm
            if (-not $script:filterActive) {
                throw "The exact WorkVM filter was not left active after successful handoff."
            }
            if (-not $script:attached) {
                throw "The re-enumeration path did not attach the native UUID."
            }
            if ($result.Uuid -ne "{{nativeUuid}}") {
                throw "showvminfo verification returned the wrong UUID."
            }
            Write-Output "STAYACTIVE_BUSY_NO_SERIAL_FILTER_ATTACH_PASS"
            """;

        try
        {
            File.WriteAllText(harnessPath, harness);
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            process.StartInfo.ArgumentList.Add("-NoProfile");
            process.StartInfo.ArgumentList.Add("-ExecutionPolicy");
            process.StartInfo.ArgumentList.Add("Bypass");
            process.StartInfo.ArgumentList.Add("-File");
            process.StartInfo.ArgumentList.Add(harnessPath);

            process.Start();
            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                }

                throw new TimeoutException("PowerShell behavior harness timed out.");
            }

            var output = await standardOutput;
            var error = await standardError;

            Assert.True(
                process.ExitCode == 0,
                $"PowerShell behavior harness failed with {process.ExitCode}:{Environment.NewLine}{output}{error}");
            Assert.Contains(
                "STAYACTIVE_BUSY_NO_SERIAL_FILTER_ATTACH_PASS",
                output,
                StringComparison.Ordinal);
            Assert.DoesNotContain(staleHeldUuid, output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(harnessPath);
        }
    }

    [Fact]
    public void BluetoothToVm_ProxyAndGuestProofChecksRejectStaleState()
    {
        var script = ReadWorkVmScript(BluetoothToVmScriptName);
        var proxyWait = ExtractBraceBlock(
            script,
            "function Wait-VirtualBoxBluetoothProxyReady");
        Assert.Contains(
            "Get-PresentVirtualBoxBluetoothProxyDevice",
            proxyWait,
            StringComparison.Ordinal);
        Assert.Contains(
            "Get-VirtualBoxBluetoothProxyRows",
            proxyWait,
            StringComparison.Ordinal);
        Assert.Contains(
            "$null -ne $proxyDevice",
            proxyWait,
            StringComparison.Ordinal);

        var attach = ExtractBraceBlock(script, "function Attach-BluetoothToVm");
        Assert.Contains("Get-DirectAttachBluetoothDevice", attach, StringComparison.Ordinal);
        Assert.Contains("Rebind-HostBluetoothForVirtualBoxCapture", attach, StringComparison.Ordinal);
        Assert.DoesNotContain("nativeHeldDevices", attach, StringComparison.Ordinal);
        Assert.DoesNotContain("Get-HostBluetoothUsbDevices", script, StringComparison.Ordinal);

        var directAttach = ExtractBraceBlock(
            script,
            "function Get-DirectAttachBluetoothDevice");
        Assert.Contains(
            "$_.State -eq \"Available\"",
            directAttach,
            StringComparison.Ordinal);
        Assert.Contains(
            "The exact native Bluetooth row is Busy",
            directAttach,
            StringComparison.Ordinal);
        Assert.Contains(
            "Get-HostBluetoothPhysicalDevice",
            directAttach,
            StringComparison.Ordinal);
        Assert.Contains(
            "Test-PnpHealthy",
            directAttach,
            StringComparison.Ordinal);

        var globalHold = ExtractBraceBlock(
            script,
            "function Add-GlobalBluetoothHoldFilter");
        Assert.Contains("\"--vendorid\"", globalHold, StringComparison.Ordinal);
        Assert.Contains("\"--productid\"", globalHold, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "\"--serialnumber\"",
            globalHold,
            StringComparison.Ordinal);

        Assert.Contains(
            "Set-BluetoothVmFilterActive -Active $true",
            attach,
            StringComparison.Ordinal);

        var configManagerCycle = ExtractBraceBlock(
            script,
            "function Invoke-ConfigManagerBluetoothParentCycle");
        Assert.Contains(
            "CM_Disable_DevNode",
            configManagerCycle,
            StringComparison.Ordinal);
        Assert.Contains(
            "CM_Enable_DevNode",
            configManagerCycle,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "0x8",
            configManagerCycle,
            StringComparison.Ordinal);

        var proof = ExtractBraceBlock(script, "function Test-GuestBluetoothProof");
        Assert.Contains("Get-VmSessionStartUtc", proof, StringComparison.Ordinal);
        Assert.Contains("$verifiedAt -ge $sessionStart", proof, StringComparison.Ordinal);
        Assert.Contains(".TotalHours -le 12", proof, StringComparison.Ordinal);
        Assert.DoesNotContain(".TotalDays -le 180", proof, StringComparison.Ordinal);

        var guestHealth = ExtractBraceBlock(
            script,
            "function Ensure-GuestBluetoothHealthy");
        Assert.Contains(
            "USB ownership is nevertheless confirmed.",
            guestHealth,
            StringComparison.Ordinal);
        Assert.Contains(
            "return $false",
            guestHealth,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "this VM has no matching Bluetooth proof",
            guestHealth,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ReturnToLaptop_HealthyNativeOwnershipDoesNotFailOnAdvisoryProxy()
    {
        var script = ReadWorkVmScript(ReturnToLaptopScriptName);
        var restore = ExtractBraceBlock(script, "function Restore-HostBluetooth");

        Assert.Contains(
            "The native Bluetooth adapter is healthy; an inert exact VirtualBox proxy devnode is still visible and is being treated as advisory.",
            restore,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "The native Bluetooth adapter is healthy, but an exact VirtualBox proxy is still present.",
            restore,
            StringComparison.Ordinal);
    }

    private static string ReadWorkVmScript(string scriptName)
    {
        return ReadRepositoryFile("workvm", "scripts", scriptName);
    }

    private static string ReadRepositoryFile(params string[] relativePathParts)
    {
        return File.ReadAllText(FindRepositoryFile(relativePathParts));
    }

    private static string FindRepositoryFile(params string[] relativePathParts)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(
                new[] { current.FullName }
                    .Concat(relativePathParts)
                    .ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate {Path.Combine(relativePathParts)} from {AppContext.BaseDirectory}.");
    }

    private static string ExtractBraceBlock(string source, string declaration)
    {
        var declarationIndex = source.IndexOf(declaration, StringComparison.Ordinal);
        Assert.True(declarationIndex >= 0, $"Declaration was not found: {declaration}");

        var openingBraceIndex = source.IndexOf('{', declarationIndex);
        Assert.True(openingBraceIndex >= 0, $"Opening brace was not found: {declaration}");

        var depth = 0;
        for (var index = openingBraceIndex; index < source.Length; index++)
        {
            switch (source[index])
            {
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                    {
                        return source[declarationIndex..(index + 1)];
                    }
                    break;
            }
        }

        throw new InvalidOperationException($"Closing brace was not found: {declaration}");
    }

    private static IReadOnlyList<PowerShellFunction> ExtractPowerShellFunctions(string script)
    {
        return Regex.Matches(
                script,
                @"(?im)^\s*function\s+(?<name>[A-Za-z_][A-Za-z0-9_-]*)\s*\{")
            .Select(match => new PowerShellFunction(
                match.Groups["name"].Value,
                ExtractBraceBlock(script, match.Value[..match.Value.LastIndexOf('{')].Trim())))
            .ToArray();
    }

    private sealed class FailingProcessRunner : IWorkVmProcessRunner
    {
        public List<WaitedRun> WaitedRuns { get; } = new();

        public string? RunAndCapture(string fileName, string arguments, TimeSpan timeout)
        {
            return null;
        }

        public void RunAndWait(string fileName, string arguments, bool elevated, TimeSpan timeout)
        {
            WaitedRuns.Add(new WaitedRun(fileName, arguments, elevated, timeout));
            throw new InvalidOperationException("powershell.exe exited with code 1.");
        }

        public void Start(string fileName, string arguments, bool elevated)
        {
            throw new NotSupportedException();
        }
    }

    private sealed record ScriptFailureCase(
        string ScriptName,
        string LogName,
        string LoggedError,
        Action<WorkVmService> Invoke);

    private sealed record WaitedRun(
        string FileName,
        string Arguments,
        bool Elevated,
        TimeSpan Timeout);

    private sealed record PowerShellFunction(string Name, string Body);
}
