using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PowerModeToggle;

internal enum LaptopPowerMode
{
    LowPower,
    HighPower
}

internal sealed record PowerProfileState(
    int? RefreshRateHz,
    int? MonitorBrightnessPercent,
    double? NvidiaPowerLimitWatts,
    double? NvidiaMinimumPowerLimitWatts,
    double? NvidiaDefaultPowerLimitWatts,
    double? NvidiaMaximumPowerLimitWatts,
    string? WindowsPlanName)
{
    public LaptopPowerMode? DetectedMode
    {
        get
        {
            if (RefreshRateHz is >= 160
                && MonitorBrightnessPercent is >= 95
                && NvidiaPowerLimitWatts is { } highGpuLimit
                && Math.Abs(highGpuLimit - NvidiaPowerService.HighPowerLimitWatts) <= 1.0
                && IsOneOfPlans(DesktopWindowsPowerService.HighPlanName, "High performance"))
            {
                return LaptopPowerMode.HighPower;
            }

            if (RefreshRateHz is > 0 and <= 61
                && MonitorBrightnessPercent is >= 0 and <= 40
                && NvidiaPowerLimitWatts is { } lowGpuLimit
                && Math.Abs(lowGpuLimit - NvidiaPowerService.LowPowerLimitWatts) <= 1.0
                && IsOneOfPlans(DesktopWindowsPowerService.LowPlanName, "Power saver"))
            {
                return LaptopPowerMode.LowPower;
            }

            return null;
        }
    }

    private bool IsOneOfPlans(params string[] planNames)
    {
        return planNames.Any(planName =>
            string.Equals(WindowsPlanName, planName, StringComparison.OrdinalIgnoreCase));
    }

    public string ToSummary()
    {
        var refresh = RefreshRateHz is { } rate ? $"{rate} Hz" : "refresh unknown";
        var brightness = MonitorBrightnessPercent is { } level ? $"{level}% brightness" : "brightness unknown";
        var gpu = NvidiaPowerLimitWatts is { } watts ? $"GPU limit {watts:0} W" : "GPU limit unknown";
        var plan = string.IsNullOrWhiteSpace(WindowsPlanName) ? "Windows plan unknown" : WindowsPlanName;
        return $"{refresh}; {brightness}; {gpu}; {plan}";
    }
}

internal sealed record PowerProfileApplyResult(LaptopPowerMode Mode, PowerProfileState State, IReadOnlyList<string> Errors)
{
    public bool Success => Errors.Count == 0;
}

internal static class PowerProfileService
{
    public static PowerProfileApplyResult Apply(LaptopPowerMode mode)
    {
        var errors = new List<string>();
        var highPower = mode == LaptopPowerMode.HighPower;

        void ApplyStep(string name, Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                errors.Add($"{name}: {ex.Message}");
            }
        }

        // Reduce the largest possible heat source first when entering Low.
        // Restore the GPU ceiling last when entering High so the supporting
        // CPU/display settings are already in place before full GPU power.
        if (!highPower)
        {
            ApplyStep("NVIDIA GPU power limit", () => NvidiaPowerService.SetAndVerify(highPower: false));
        }

        ApplyStep("Windows CPU power plan", () => DesktopWindowsPowerService.SetProfile(highPower));

        // DDC/CI handles can briefly disappear while Windows changes the
        // display timing, so set brightness before changing refresh rate.
        ApplyStep("Monitor brightness", () => MonitorBrightnessService.SetPrimaryMonitorBrightnessPercent(highPower ? 100 : 35));
        ApplyStep("Display refresh rate", () => DisplayRefreshRateService.SetPrimaryDisplayRefreshRate(highPower ? 165 : 60));

        if (highPower)
        {
            ApplyStep("NVIDIA GPU power limit", () => NvidiaPowerService.SetAndVerify(highPower: true));
        }

        var state = WaitForRequestedState(mode, TimeSpan.FromSeconds(10));
        if (state.DetectedMode != mode)
        {
            errors.Add($"Verification: the desktop did not remain fully in {mode} ({state.ToSummary()}).");
        }

        return new PowerProfileApplyResult(mode, state, errors);
    }

    public static PowerProfileState ReadState()
    {
        int? refreshRate = null;
        int? brightness = null;
        NvidiaPowerState? gpu = null;
        string? planName = null;

        try
        {
            refreshRate = DisplayRefreshRateService.TryGetPrimaryDisplayRefreshRate();
        }
        catch
        {
        }

        try
        {
            brightness = MonitorBrightnessService.TryGetPrimaryMonitorBrightnessPercent();
        }
        catch
        {
        }

        try
        {
            gpu = NvidiaPowerService.ReadState();
        }
        catch
        {
        }

        try
        {
            planName = DesktopWindowsPowerService.TryGetActivePlanName();
        }
        catch
        {
        }

        return new PowerProfileState(
            refreshRate,
            brightness,
            gpu?.CurrentLimitWatts,
            gpu?.MinimumLimitWatts,
            gpu?.DefaultLimitWatts,
            gpu?.MaximumLimitWatts,
            planName);
    }

    private static PowerProfileState WaitForRequestedState(LaptopPowerMode mode, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        PowerProfileState state;
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

internal static class DesktopWindowsPowerService
{
    internal const string LowPlanName = "PowerModeToggleDesktop Low";
    internal const string HighPlanName = "PowerModeToggleDesktop High";

    private static readonly Regex PlanPattern = new(
        @"(?<guid>[0-9a-fA-F-]{36})\s+\((?<name>[^)]+)\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static void SetProfile(bool highPower)
    {
        var planName = highPower ? HighPlanName : LowPlanName;
        var plan = EnsurePlan(planName);
        ConfigurePlan(plan.Guid, highPower);
        RunPowerCfg("/setactive", plan.Guid.ToString("D"));

        var activePlan = TryGetActivePlanName();
        if (!string.Equals(activePlan, planName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Requested {planName}, but Windows reports {activePlan ?? "an unknown active plan"}.");
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

    private static PowerPlan EnsurePlan(string planName)
    {
        var existing = ReadPowerPlans().FirstOrDefault(plan =>
            string.Equals(plan.Name, planName, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            return existing;
        }

        var duplicateOutput = RunPowerCfg("/duplicatescheme", "SCHEME_BALANCED");
        var guidMatch = Regex.Match(duplicateOutput, @"[0-9a-fA-F-]{36}", RegexOptions.CultureInvariant);
        if (!guidMatch.Success || !Guid.TryParse(guidMatch.Value, out var guid))
        {
            throw new InvalidOperationException("Windows created a power plan but did not report its GUID.");
        }

        RunPowerCfg(
            "/changename",
            guid.ToString("D"),
            planName,
            "Managed by PowerModeToggleDesktop. Built-in Windows plans are not modified.");
        return new PowerPlan(guid, planName);
    }

    private static void ConfigurePlan(Guid planGuid, bool highPower)
    {
        var guid = planGuid.ToString("D");
        var maximumProcessorState = highPower ? 100 : 80;
        var energyPreference = highPower ? 5 : 95;
        var schedulingPolicy = highPower ? 5 : 4;
        var boostMode = highPower ? 2 : 0;
        var coolingPolicy = highPower ? 1 : 0;
        var pcieLinkState = highPower ? 0 : 2;

        SetAcValue(guid, "SUB_PROCESSOR", "PROCTHROTTLEMIN", 5);
        SetAcValue(guid, "SUB_PROCESSOR", "PROCTHROTTLEMIN1", 5);
        SetAcValue(guid, "SUB_PROCESSOR", "PROCTHROTTLEMAX", maximumProcessorState);
        SetAcValue(guid, "SUB_PROCESSOR", "PROCTHROTTLEMAX1", maximumProcessorState);
        SetAcValue(guid, "SUB_PROCESSOR", "PERFAUTONOMOUS", 1);
        SetAcValue(guid, "SUB_PROCESSOR", "PERFEPP", energyPreference);
        SetAcValue(guid, "SUB_PROCESSOR", "PERFEPP1", energyPreference);
        SetAcValue(guid, "SUB_PROCESSOR", "SCHEDPOLICY", schedulingPolicy);
        SetAcValue(guid, "SUB_PROCESSOR", "SHORTSCHEDPOLICY", schedulingPolicy);
        SetAcValue(guid, "SUB_PROCESSOR", "PERFBOOSTMODE", boostMode);
        SetAcValue(guid, "SUB_PROCESSOR", "SYSCOOLPOL", coolingPolicy);
        SetAcValue(guid, "SUB_PCIEXPRESS", "ASPM", pcieLinkState);
    }

    private static void SetAcValue(string planGuid, string subgroup, string setting, int value)
    {
        RunPowerCfg("/setacvalueindex", planGuid, subgroup, setting, value.ToString(CultureInfo.InvariantCulture));
    }

    private static IReadOnlyList<PowerPlan> ReadPowerPlans()
    {
        var output = RunPowerCfg("/L");
        var plans = new List<PowerPlan>();
        foreach (Match match in PlanPattern.Matches(output))
        {
            if (Guid.TryParse(match.Groups["guid"].Value, out var guid))
            {
                plans.Add(new PowerPlan(guid, match.Groups["name"].Value.Trim()));
            }
        }

        return plans;
    }

    private static string RunPowerCfg(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "powercfg.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException("Could not start powercfg.exe.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(10_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("powercfg.exe did not finish in time.");
        }

        if (process.ExitCode != 0)
        {
            var details = string.IsNullOrWhiteSpace(error) ? output : error;
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(details)
                ? $"powercfg.exe failed with exit code {process.ExitCode}."
                : details.Trim());
        }

        return output;
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

    private sealed record PowerPlan(Guid Guid, string Name);

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr userPowerKey, out IntPtr activePolicyGuid);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}

internal sealed record NvidiaPowerState(
    double CurrentLimitWatts,
    double MinimumLimitWatts,
    double DefaultLimitWatts,
    double MaximumLimitWatts);

internal static class NvidiaPowerService
{
    internal const double LowPowerLimitWatts = 150;
    internal const double HighPowerLimitWatts = 450;

    private static readonly Lazy<string> NvidiaSmiPath = new(FindNvidiaSmi);

    public static NvidiaPowerState ReadState()
    {
        var output = RunNvidiaSmi(
            "--id=0",
            "--query-gpu=power.limit,power.min_limit,power.default_limit,power.max_limit",
            "--format=csv,noheader,nounits");
        var firstLine = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
                        ?? throw new InvalidOperationException("nvidia-smi returned no GPU power data.");
        var values = firstLine.Split(',', StringSplitOptions.TrimEntries);
        if (values.Length != 4
            || !TryParseNumber(values[0], out var current)
            || !TryParseNumber(values[1], out var minimum)
            || !TryParseNumber(values[2], out var defaultLimit)
            || !TryParseNumber(values[3], out var maximum))
        {
            throw new InvalidOperationException($"Could not parse the NVIDIA power limits: {firstLine}");
        }

        return new NvidiaPowerState(current, minimum, defaultLimit, maximum);
    }

    public static void SetAndVerify(bool highPower)
    {
        var state = ReadState();
        var requested = highPower ? HighPowerLimitWatts : LowPowerLimitWatts;
        if (requested < state.MinimumLimitWatts - 0.5 || requested > state.MaximumLimitWatts + 0.5)
        {
            throw new InvalidOperationException(
                $"The requested {requested:0} W limit is outside this GPU's " +
                $"{state.MinimumLimitWatts:0}-{state.MaximumLimitWatts:0} W range.");
        }

        if (Math.Abs(state.CurrentLimitWatts - requested) > 1.0)
        {
            RunNvidiaSmi("--id=0", "--power-limit=" + requested.ToString("0", CultureInfo.InvariantCulture));
        }

        Thread.Sleep(600);
        var verified = ReadState();
        if (Math.Abs(verified.CurrentLimitWatts - requested) > 1.0)
        {
            throw new InvalidOperationException(
                $"Requested {requested:0} W, but the GPU reports {verified.CurrentLimitWatts:0} W.");
        }
    }

    private static bool TryParseNumber(string value, out double result)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
    }

    private static string RunNvidiaSmi(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = NvidiaSmiPath.Value,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException("Could not start nvidia-smi.exe.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(15_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("nvidia-smi.exe did not finish in time.");
        }

        if (process.ExitCode != 0)
        {
            var details = string.IsNullOrWhiteSpace(error) ? output : error;
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(details)
                ? $"nvidia-smi.exe failed with exit code {process.ExitCode}."
                : details.Trim());
        }

        return output;
    }

    private static string FindNvidiaSmi()
    {
        var candidates = new List<string>();
        var registryPath = Registry.GetValue(
            @"HKEY_LOCAL_MACHINE\SOFTWARE\NVIDIA Corporation\NVSMI",
            "NVSMIPATH",
            null) as string;
        if (!string.IsNullOrWhiteSpace(registryPath))
        {
            candidates.Add(Directory.Exists(registryPath)
                ? Path.Combine(registryPath, "nvidia-smi.exe")
                : registryPath);
        }

        candidates.Add(Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe"));
        candidates.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "NVIDIA Corporation",
            "NVSMI",
            "nvidia-smi.exe"));

        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                candidates.Add(Path.Combine(directory.Trim(), "nvidia-smi.exe"));
            }
            catch
            {
            }
        }

        return candidates.FirstOrDefault(File.Exists)
               ?? throw new FileNotFoundException("nvidia-smi.exe was not found for the installed NVIDIA driver.");
    }
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
            throw new InvalidOperationException("The primary display could not be found.");
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
        // Windows reports the monitor's 59.94 Hz timing as either 59 or 60 Hz.
        if (verifiedRate is null || Math.Abs(verifiedRate.Value - refreshRateHz) > 1)
        {
            throw new InvalidOperationException(
                $"Requested {refreshRateHz} Hz, but Windows reports {verifiedRate?.ToString() ?? "an unknown rate"}.");
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
        public int BitsPerPixel;
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
    private static extern bool EnumDisplaySettingsEx(
        string deviceName,
        int modeNumber,
        ref DeviceMode deviceMode,
        int flags);

    [DllImport("user32.dll", CharSet = CharSet.Ansi)]
    private static extern int ChangeDisplaySettingsEx(
        string deviceName,
        ref DeviceMode deviceMode,
        IntPtr window,
        int flags,
        IntPtr parameters);
}

internal static class MonitorBrightnessService
{
    private const uint MonitorDefaultToPrimary = 1;

    public static int? TryGetPrimaryMonitorBrightnessPercent()
    {
        var monitors = GetPrimaryPhysicalMonitors();
        try
        {
            foreach (var monitor in monitors)
            {
                if (GetMonitorBrightness(monitor.Handle, out var minimum, out var current, out var maximum))
                {
                    return ToPercent(minimum, current, maximum);
                }
            }

            return null;
        }
        finally
        {
            Destroy(monitors);
        }
    }

    public static void SetPrimaryMonitorBrightnessPercent(int brightnessPercent)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            try
            {
                SetPrimaryMonitorBrightnessPercentOnce(brightnessPercent);
                return;
            }
            catch (Exception ex) when (attempt < 4)
            {
                lastError = ex;
                Thread.Sleep(750);
            }
        }

        throw new InvalidOperationException(
            $"The monitor did not accept {brightnessPercent}% brightness after four attempts.",
            lastError);
    }

    private static void SetPrimaryMonitorBrightnessPercentOnce(int brightnessPercent)
    {
        brightnessPercent = Math.Clamp(brightnessPercent, 0, 100);
        var monitors = GetPrimaryPhysicalMonitors();
        var changed = 0;
        try
        {
            foreach (var monitor in monitors)
            {
                if (!GetMonitorBrightness(monitor.Handle, out var minimum, out _, out var maximum))
                {
                    continue;
                }

                var target = minimum + (uint)Math.Round((maximum - minimum) * brightnessPercent / 100d);
                if (!SetMonitorBrightness(monitor.Handle, target))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        $"The monitor rejected {brightnessPercent}% brightness.");
                }

                changed++;
            }
        }
        finally
        {
            Destroy(monitors);
        }

        if (changed == 0)
        {
            throw new InvalidOperationException("The primary monitor does not expose DDC/CI brightness control.");
        }

        Thread.Sleep(250);
        var verified = TryGetPrimaryMonitorBrightnessPercent();
        if (verified is null || Math.Abs(verified.Value - brightnessPercent) > 2)
        {
            throw new InvalidOperationException(
                $"Requested {brightnessPercent}% brightness, but the monitor reports " +
                $"{verified?.ToString() ?? "an unknown value"}%.");
        }
    }

    private static int ToPercent(uint minimum, uint current, uint maximum)
    {
        if (maximum <= minimum)
        {
            return current > 0 ? 100 : 0;
        }

        return (int)Math.Round((current - minimum) * 100d / (maximum - minimum));
    }

    private static PhysicalMonitor[] GetPrimaryPhysicalMonitors()
    {
        var primary = MonitorFromPoint(new NativePoint(0, 0), MonitorDefaultToPrimary);
        if (primary == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not find the primary monitor.");
        }

        if (!GetNumberOfPhysicalMonitorsFromHMONITOR(primary, out var count) || count == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not enumerate the primary monitor.");
        }

        var monitors = new PhysicalMonitor[checked((int)count)];
        if (!GetPhysicalMonitorsFromHMONITOR(primary, count, monitors))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not open the primary monitor.");
        }

        return monitors;
    }

    private static void Destroy(IEnumerable<PhysicalMonitor> monitors)
    {
        foreach (var monitor in monitors)
        {
            if (monitor.Handle != IntPtr.Zero)
            {
                _ = DestroyPhysicalMonitor(monitor.Handle);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativePoint(int x, int y)
    {
        public readonly int X = x;
        public readonly int Y = y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PhysicalMonitor
    {
        public IntPtr Handle;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr monitor, out uint count);

    [DllImport("dxva2.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(
        IntPtr monitor,
        uint count,
        [Out] PhysicalMonitor[] physicalMonitors);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorBrightness(
        IntPtr monitor,
        out uint minimumBrightness,
        out uint currentBrightness,
        out uint maximumBrightness);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetMonitorBrightness(IntPtr monitor, uint newBrightness);

    [DllImport("dxva2.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyPhysicalMonitor(IntPtr monitor);
}
