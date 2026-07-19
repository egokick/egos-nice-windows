using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PowerModeToggle;

internal sealed record LaptopPowerProfileState(
    int? RefreshRateHz,
    int? AsusGpuEco,
    int? AsusPerformanceMode,
    string? ArmouryGpuMode,
    string? ArmouryOperatingMode,
    string? WindowsPlanName,
    Guid? WindowsPowerMode)
{
    private static readonly Guid BestPowerEfficiency = new("961cc777-2547-4f9d-8174-7d86181b8a7a");
    public LaptopPowerMode? DetectedMode
    {
        get
        {
            if (RefreshRateHz >= 100
                && string.Equals(ArmouryGpuMode, "Optimized", StringComparison.OrdinalIgnoreCase)
                && string.Equals(ArmouryOperatingMode, "Performance", StringComparison.OrdinalIgnoreCase)
                && string.Equals(WindowsPlanName, "Performance", StringComparison.OrdinalIgnoreCase))
            {
                return LaptopPowerMode.HighPower;
            }

            if (RefreshRateHz is > 0 and <= 70
                && AsusGpuEco == 1
                && string.Equals(ArmouryGpuMode, "Eco", StringComparison.OrdinalIgnoreCase)
                && string.Equals(ArmouryOperatingMode, "Silent", StringComparison.OrdinalIgnoreCase)
                && string.Equals(WindowsPlanName, "Silent", StringComparison.OrdinalIgnoreCase)
                && WindowsPowerMode == BestPowerEfficiency)
            {
                return LaptopPowerMode.LowPower;
            }

            return null;
        }
    }

    public string ToSummary()
    {
        var refresh = RefreshRateHz is { } rate ? $"{rate} Hz" : "refresh unknown";
        var gpu = AsusGpuEco switch
        {
            1 => "GPU Eco",
            0 => "GPU enabled",
            _ => "GPU mode unknown"
        };
        var armoury = string.IsNullOrWhiteSpace(ArmouryGpuMode) ? "Armoury mode unknown" : $"Armoury {ArmouryGpuMode}";
        var operatingMode = string.IsNullOrWhiteSpace(ArmouryOperatingMode)
            ? "fan mode unknown"
            : $"{ArmouryOperatingMode} fans";
        var plan = string.IsNullOrWhiteSpace(WindowsPlanName) ? "Windows plan unknown" : WindowsPlanName;
        return $"{refresh}; {gpu}; {armoury}; {operatingMode}; {plan}";
    }
}
internal static class LaptopPowerProfileBackend
{
    public static PowerProfileApplyResult Apply(LaptopPowerMode mode)
    {
        var errors = new List<string>();
        var highPower = mode == LaptopPowerMode.HighPower;
        var gpuEco = highPower && IsPluggedIn() ? 0 : 1;

        try
        {
            ArmouryCrateGpuModeService.SetModeAndReload(mode, gpuEco == 1);
        }
        catch (Exception ex)
        {
            errors.Add($"Armoury Crate GPU mode: {ex.Message}");
        }

        try
        {
            WindowsPowerService.SetProfile(highPower);
        }
        catch (Exception ex)
        {
            errors.Add($"Windows power mode: {ex.Message}");
        }

        // Armoury Crate reacts to Windows plan changes and can rewrite the firmware
        // profile. Let that reaction finish, then make the requested ASUS mode final.
        Thread.Sleep(750);

        try
        {
            using var asus = new AsusHardwareService();
            if (!asus.TrySetAndVerify(AsusHardwareService.GpuEcoEndpoint, gpuEco, out var gpuError))
            {
                errors.Add($"ASUS GPU mode: {gpuError}");
            }

            var asusMode = highPower ? AsusHardwareService.PerformanceMode : AsusHardwareService.SilentMode;
            if (!asus.TrySet(AsusHardwareService.PerformanceEndpoint, asusMode, out var modeError))
            {
                errors.Add($"ASUS fan/performance mode: {modeError}");
            }
        }
        catch (Exception ex)
        {
            errors.Add($"ASUS controls: {ex.Message}");
        }

        try
        {
            DisplayRefreshRateService.SetPrimaryDisplayRefreshRate(highPower ? 120 : 60);
        }
        catch (Exception ex)
        {
            errors.Add($"Display refresh rate: {ex.Message}");
        }

        var state = WaitForRequestedState(mode, TimeSpan.FromSeconds(10));
        if (state.DetectedMode != mode)
        {
            errors.Add($"Verification: the laptop did not remain fully in {mode} ({state.ToSummary()}).");
        }

        return new PowerProfileApplyResult(mode, PowerProfileState.FromLaptop(state), errors);
    }

    public static LaptopPowerProfileState ReadState()
    {
        int? gpuEco = null;
        int? performanceMode = null;
        try
        {
            using var asus = new AsusHardwareService();
            gpuEco = asus.TryGet(AsusHardwareService.GpuEcoEndpoint);
            performanceMode = asus.TryGet(AsusHardwareService.PerformanceEndpoint);
        }
        catch
        {
        }

        var windowsPlanName = WindowsPowerService.TryGetActivePlanName();
        var operatingMode = ArmouryCrateGpuModeService.TryGetOperatingMode();

        // Armoury Crate removes its External SelectedMode value after some service
        // reloads even though the requested mode remains active. The Windows plans
        // are kept in lockstep with the Armoury operating/fan modes by Apply(), so
        // use the active plan as the durable verification signal in that case.
        operatingMode ??= windowsPlanName switch
        {
            "Performance" => "Performance",
            "Silent" => "Silent",
            _ => null
        };

        return new LaptopPowerProfileState(
            DisplayRefreshRateService.TryGetPrimaryDisplayRefreshRate(),
            gpuEco,
            performanceMode,
            ArmouryCrateGpuModeService.TryGetMode(),
            operatingMode,
            windowsPlanName,
            WindowsPowerService.TryGetPowerMode());
    }

    private static bool IsPluggedIn()
    {
        return SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Online;
    }

    private static LaptopPowerProfileState WaitForRequestedState(LaptopPowerMode mode, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        LaptopPowerProfileState state;
        do
        {
            state = ReadState();
            if (state.DetectedMode == mode)
            {
                return state;
            }

            Thread.Sleep(500);
        }
        while (DateTime.UtcNow < deadline);

        return state;
    }
}

internal static class ArmouryCrateGpuModeService
{
    private const string GpuRegistryPath = @"SOFTWARE\ASUS\Armoury Crate Service\GPUMode";
    private const string ExternalModeRegistryPath = @"SOFTWARE\ASUS\Armoury Crate Service\ThrottlePlugin\External";
    private const string AtkStatusRegistryPath = @"SOFTWARE\ASUS\Armoury Crate Service\ThrottlePlugin\ROG ATKStatus";
    private const string ServiceName = "ArmouryCrateService";

    public static void SetModeAndReload(LaptopPowerMode mode, bool gpuEco)
    {
        CloseStaleArmouryApp();

        using (var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
        using (var key = machine.OpenSubKey(GpuRegistryPath, writable: true)
                         ?? throw new InvalidOperationException("The Armoury Crate GPU-mode registry key was not found."))
        {
            var optimized = mode == LaptopPowerMode.HighPower;
            key.SetValue("IsOptimized", optimized ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("IsEcoMode", gpuEco ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("KeepStatus", optimized ? 0 : 1, RegistryValueKind.DWord);
            key.Flush();
        }

        using (var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
        using (var externalMode = machine.OpenSubKey(ExternalModeRegistryPath, writable: true)
                                  ?? throw new InvalidOperationException("The Armoury Crate operating-mode registry key was not found."))
        using (var atkStatus = machine.OpenSubKey(AtkStatusRegistryPath, writable: true)
                              ?? throw new InvalidOperationException("The Armoury Crate ATK operating-mode registry key was not found."))
        {
            var highPower = mode == LaptopPowerMode.HighPower;

            // External.SelectedMode stores the ASUS firmware value (Performance
            // = 0, Silent = 2). The ATK status values use Armoury's UI enum
            // (Silent = 1, Performance = 2).
            externalMode.SetValue("SelectedMode", highPower ? 0 : 2, RegistryValueKind.DWord);
            atkStatus.SetValue("ThrottleModeOnAC", highPower ? 2 : 1, RegistryValueKind.DWord);
            atkStatus.SetValue("ThrottleModeOnDC", highPower ? 2 : 1, RegistryValueKind.DWord);
            externalMode.Flush();
            atkStatus.Flush();
        }

        RestartService();
    }

    public static string? TryGetMode()
    {
        try
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = machine.OpenSubKey(GpuRegistryPath, writable: false);
            if (key is null)
            {
                return null;
            }

            if (Convert.ToInt32(key.GetValue("IsOptimized", 0)) == 1)
            {
                return "Optimized";
            }

            return Convert.ToInt32(key.GetValue("IsEcoMode", 0)) == 1 ? "Eco" : "Standard";
        }
        catch
        {
            return null;
        }
    }

    public static string? TryGetOperatingMode()
    {
        try
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = machine.OpenSubKey(ExternalModeRegistryPath, writable: false);
            if (key is null)
            {
                return null;
            }

            return Convert.ToInt32(key.GetValue("SelectedMode", -1)) switch
            {
                0 => "Performance",
                1 => "Turbo",
                2 => "Silent",
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    private static void RestartService()
    {
        using var service = new ServiceController(ServiceName);
        try
        {
            service.Refresh();
            if (service.Status != ServiceControllerStatus.Stopped)
            {
                if (service.Status != ServiceControllerStatus.StopPending)
                {
                    service.Stop();
                }

                service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
            }

            service.Start();
            service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
        }
        catch
        {
            // Do not leave Armoury Crate disabled if a timeout or transient
            // service-control error occurs during the reload.
            try
            {
                service.Refresh();
                if (service.Status == ServiceControllerStatus.Stopped)
                {
                    service.Start();
                    service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
                }
            }
            catch
            {
            }

            throw;
        }
    }

    private static void CloseStaleArmouryApp()
    {
        foreach (var process in Process.GetProcessesByName("ArmouryCrate"))
        {
            using (process)
            {
                try
                {
                    if (process.MainWindowHandle != IntPtr.Zero)
                    {
                        process.CloseMainWindow();
                        process.WaitForExit(1500);
                    }

                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: false);
                        process.WaitForExit(5000);
                    }
                }
                catch (InvalidOperationException)
                {
                    // The process exited between discovery and shutdown.
                }
            }
        }
    }
}

internal sealed class AsusHardwareService : IDisposable
{
    internal const uint GpuEcoEndpoint = 0x00090020;
    internal const uint PerformanceEndpoint = 0x00120075;
    internal const int PerformanceMode = 0;
    internal const int SilentMode = 2;

    private const string DevicePath = @"\\.\ATKACPI";
    private const uint ControlCode = 0x0022240C;
    private const uint DeviceStatusMethod = 0x53545344;
    private const uint DeviceSetMethod = 0x53564544;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 1;
    private const uint FileShareWrite = 2;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x80;

    private IntPtr _handle;

    public AsusHardwareService()
    {
        _handle = CreateFile(
            DevicePath,
            GenericRead | GenericWrite,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            FileAttributeNormal,
            IntPtr.Zero);

        if (_handle == new IntPtr(-1))
        {
            var error = Marshal.GetLastWin32Error();
            _handle = IntPtr.Zero;
            throw new Win32Exception(error, "The ASUS ACPI control device is unavailable.");
        }
    }

    public int? TryGet(uint endpoint)
    {
        var value = DeviceGet(endpoint);
        return value >= 0 ? value : null;
    }

    public bool TrySetAndVerify(uint endpoint, int targetValue, out string error)
    {
        const int maxAttempts = 3;
        var lastValue = DeviceGet(endpoint);
        var lastResult = 1;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (lastValue != targetValue)
            {
                lastResult = DeviceSet(endpoint, targetValue);
            }

            // Armoury Crate can react asynchronously and overwrite a direct GPU
            // change. A stable read after its reaction window is the real success.
            Thread.Sleep(900);
            lastValue = DeviceGet(endpoint);
            if (lastValue == targetValue)
            {
                error = string.Empty;
                return true;
            }
        }

        error = lastValue < 0
            ? "the setting is not supported or could not be read back"
            : $"requested {targetValue}, but the firmware reports {lastValue} (command result {lastResult})";
        return false;
    }

    public bool TrySet(uint endpoint, int targetValue, out string error)
    {
        var currentValue = DeviceGet(endpoint);
        if (currentValue == targetValue)
        {
            error = string.Empty;
            return true;
        }

        var setResult = DeviceSet(endpoint, targetValue);
        if (setResult == 1)
        {
            error = string.Empty;
            return true;
        }

        error = $"the firmware command returned {setResult}";
        return false;
    }

    public void Dispose()
    {
        if (_handle == IntPtr.Zero)
        {
            return;
        }

        CloseHandle(_handle);
        _handle = IntPtr.Zero;
    }

    private int DeviceGet(uint endpoint)
    {
        var arguments = new byte[8];
        BitConverter.GetBytes(endpoint).CopyTo(arguments, 0);
        var output = CallMethod(DeviceStatusMethod, arguments);
        return BitConverter.ToInt32(output, 0) - 65536;
    }

    private int DeviceSet(uint endpoint, int value)
    {
        var arguments = new byte[8];
        BitConverter.GetBytes(endpoint).CopyTo(arguments, 0);
        BitConverter.GetBytes((uint)value).CopyTo(arguments, 4);
        var output = CallMethod(DeviceSetMethod, arguments);
        return BitConverter.ToInt32(output, 0);
    }

    private byte[] CallMethod(uint methodId, byte[] arguments)
    {
        ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);

        var input = new byte[8 + arguments.Length];
        BitConverter.GetBytes(methodId).CopyTo(input, 0);
        BitConverter.GetBytes((uint)arguments.Length).CopyTo(input, 4);
        arguments.CopyTo(input, 8);

        var output = new byte[16];
        if (!DeviceIoControl(
                _handle,
                ControlCode,
                input,
                (uint)input.Length,
                output,
                (uint)output.Length,
                out _,
                IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The ASUS firmware command failed.");
        }

        return output;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        IntPtr device,
        uint ioControlCode,
        byte[] inputBuffer,
        uint inputBufferSize,
        byte[] outputBuffer,
        uint outputBufferSize,
        out uint bytesReturned,
        IntPtr overlapped);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}

internal static class WindowsPowerService
{
    private static readonly Guid BestPowerEfficiency = new("961cc777-2547-4f9d-8174-7d86181b8a7a");
    private static readonly Guid BestPerformance = new("ded574b5-45a0-4f42-8737-46345c09c238");

    public static void SetProfile(bool highPower)
    {
        var planName = highPower ? "Performance" : "Silent";
        var plan = ReadPowerPlans().FirstOrDefault(candidate =>
            string.Equals(candidate.Name, planName, StringComparison.OrdinalIgnoreCase));
        if (plan is null)
        {
            throw new InvalidOperationException($"The ASUS {planName} power plan was not found.");
        }

        var planResult = PowerSetActiveScheme(IntPtr.Zero, plan.Guid);
        if (planResult != 0)
        {
            throw new Win32Exception((int)planResult, $"Could not activate the {planName} power plan.");
        }

        var powerMode = highPower ? BestPerformance : BestPowerEfficiency;
        var modeResult = PowerSetActiveOverlayScheme(powerMode);
        if (modeResult != 0)
        {
            throw new Win32Exception((int)modeResult, "Could not change the Windows power mode.");
        }
    }

    public static string? TryGetActivePlanName()
    {
        try
        {
            var activeGuid = GetActiveScheme();
            return ReadPowerPlans().FirstOrDefault(plan => plan.Guid == activeGuid)?.Name;
        }
        catch
        {
            return null;
        }
    }

    public static Guid? TryGetPowerMode()
    {
        try
        {
            return PowerGetEffectiveOverlayScheme(out var scheme) == 0 ? scheme : null;
        }
        catch
        {
            return null;
        }
    }

    private static Guid GetActiveScheme()
    {
        var result = PowerGetActiveScheme(IntPtr.Zero, out var guidPointer);
        if (result != 0)
        {
            throw new Win32Exception((int)result, "Could not read the active Windows power plan.");
        }

        try
        {
            return Marshal.PtrToStructure<Guid>(guidPointer);
        }
        finally
        {
            _ = LocalFree(guidPointer);
        }
    }

    private static IReadOnlyList<PowerPlan> ReadPowerPlans()
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "powercfg.exe",
            Arguments = "/L",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        }) ?? throw new InvalidOperationException("Could not start powercfg.exe.");

        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit(5000);
        if (!process.HasExited || process.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                ? "Windows could not list the installed power plans."
                : error.Trim());
        }

        var plans = new List<PowerPlan>();
        foreach (Match match in Regex.Matches(
                     output,
                     @"Power Scheme GUID:\s*(?<guid>[0-9a-fA-F-]{36})\s*\((?<name>[^)]+)\)",
                     RegexOptions.CultureInvariant))
        {
            if (Guid.TryParse(match.Groups["guid"].Value, out var guid))
            {
                plans.Add(new PowerPlan(guid, match.Groups["name"].Value.Trim()));
            }
        }

        return plans;
    }

    private sealed record PowerPlan(Guid Guid, string Name);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveScheme(IntPtr rootPowerKey, [MarshalAs(UnmanagedType.LPStruct)] Guid schemeGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr userPowerKey, out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll", EntryPoint = "PowerSetActiveOverlayScheme")]
    private static extern uint PowerSetActiveOverlayScheme(Guid overlaySchemeGuid);

    [DllImport("powrprof.dll", EntryPoint = "PowerGetEffectiveOverlayScheme")]
    private static extern uint PowerGetEffectiveOverlayScheme(out Guid effectiveOverlayGuid);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}

internal static class DisplayRefreshRateService
{
    private const int CurrentSettings = -1;
    private const int ChangeSuccessful = 0;
    private const int UpdateRegistry = 1;

    public static int? TryGetPrimaryDisplayRefreshRate()
    {
        try
        {
            var deviceName = Screen.PrimaryScreen?.DeviceName;
            if (string.IsNullOrWhiteSpace(deviceName))
            {
                return null;
            }

            var mode = CreateDeviceMode();
            return EnumDisplaySettingsEx(deviceName, CurrentSettings, ref mode, 0)
                ? mode.DisplayFrequency
                : null;
        }
        catch
        {
            return null;
        }
    }

    public static void SetPrimaryDisplayRefreshRate(int refreshRateHz)
    {
        var deviceName = Screen.PrimaryScreen?.DeviceName;
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            throw new InvalidOperationException("The laptop display could not be found.");
        }

        var mode = CreateDeviceMode();
        if (!EnumDisplaySettingsEx(deviceName, CurrentSettings, ref mode, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read the current display mode.");
        }

        mode.DisplayFrequency = refreshRateHz;
        var result = ChangeDisplaySettingsEx(deviceName, ref mode, IntPtr.Zero, UpdateRegistry, IntPtr.Zero);
        if (result != ChangeSuccessful)
        {
            throw new InvalidOperationException($"Windows rejected {refreshRateHz} Hz for {deviceName} (code {result}).");
        }

        var verifiedRate = TryGetPrimaryDisplayRefreshRate();
        if (verifiedRate != refreshRateHz)
        {
            throw new InvalidOperationException($"Requested {refreshRateHz} Hz, but Windows reports {verifiedRate?.ToString() ?? "an unknown rate"}.");
        }
    }

    private static DeviceMode CreateDeviceMode()
    {
        return new DeviceMode
        {
            DeviceName = new string('\0', 32),
            FormName = new string('\0', 32),
            Size = (short)Marshal.SizeOf<DeviceMode>()
        };
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct DeviceMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        public short SpecVersion;
        public short DriverVersion;
        public short Size;
        public short DriverExtra;
        public int Fields;
        public int PositionX;
        public int PositionY;
        public int DisplayOrientation;
        public int DisplayFixedOutput;
        public short Color;
        public short Duplex;
        public short YResolution;
        public short TTOption;
        public short Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string FormName;
        public short LogPixels;
        public short BitsPerPixel;
        public int PelsWidth;
        public int PelsHeight;
        public int DisplayFlags;
        public int DisplayFrequency;
        public int IcmMethod;
        public int IcmIntent;
        public int MediaType;
        public int DitherType;
        public int Reserved1;
        public int Reserved2;
        public int PanningWidth;
        public int PanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettingsEx(string deviceName, int modeNumber, ref DeviceMode deviceMode, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Ansi)]
    private static extern int ChangeDisplaySettingsEx(
        string deviceName,
        ref DeviceMode deviceMode,
        IntPtr window,
        int flags,
        IntPtr parameters);
}
