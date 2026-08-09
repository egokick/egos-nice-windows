using Microsoft.Win32;

namespace PowerModeToggle;

internal enum LaptopPowerMode
{
    LowPower,
    HighPower
}

internal enum HardwareProfile
{
    Unsupported,
    AsusLaptop,
    HpOmenLaptop,
    GigabyteDesktop
}

internal sealed record MachineIdentity(
    HardwareProfile Profile,
    string Manufacturer,
    string ProductName,
    string BaseBoardProduct,
    string ProcessorName)
{
    public string Description => string.Join(
        " / ",
        new[] { Manufacturer, ProductName, ProcessorName }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
}

internal sealed record PowerProfileState(
    HardwareProfile HardwareProfile,
    LaptopPowerMode? DetectedMode,
    string Summary,
    object? Details)
{
    public static PowerProfileState FromLaptop(LaptopPowerProfileState state)
    {
        return new PowerProfileState(
            HardwareProfile.AsusLaptop,
            state.DetectedMode,
            state.ToSummary(),
            state);
    }

    public static PowerProfileState FromDesktop(DesktopPowerProfileState state)
    {
        return new PowerProfileState(
            HardwareProfile.GigabyteDesktop,
            state.DetectedMode,
            state.ToSummary(),
            state);
    }

    public static PowerProfileState FromHpOmen(HpOmenPowerProfileState state)
    {
        return new PowerProfileState(
            HardwareProfile.HpOmenLaptop,
            state.DetectedMode,
            state.ToSummary(),
            state);
    }

    public static PowerProfileState Unsupported(MachineIdentity machine)
    {
        var description = string.IsNullOrWhiteSpace(machine.Description)
            ? "Unknown Windows PC"
            : machine.Description;
        return new PowerProfileState(
            HardwareProfile.Unsupported,
            null,
            $"Unsupported hardware: {description}",
            machine);
    }
}

internal sealed record PowerProfileApplyResult(
    LaptopPowerMode Mode,
    PowerProfileState State,
    IReadOnlyList<string> Errors,
    IReadOnlyList<PowerSettingApplyResult> SettingResults)
{
    public bool Success => Errors.Count == 0;
}

internal sealed record PowerSettingState(
    string Id,
    string Name,
    string LowPowerValue,
    string HighPowerValue,
    string CurrentValue,
    bool MatchesLowPower,
    bool MatchesHighPower);

internal sealed record PowerSettingApplyResult(
    PowerSettingState Setting,
    LaptopPowerMode TargetMode,
    bool Success,
    string? Error);

internal static class PowerSettingIds
{
    public const string AsusGpuMode = "asus-gpu-mode";
    public const string AsusOperatingMode = "asus-operating-mode";
    public const string WindowsPowerPlan = "windows-power-plan";
    public const string WindowsPowerMode = "windows-power-mode";
    public const string DisplayRefreshRate = "display-refresh-rate";
    public const string HpOmenFirmwareMode = "hp-omen-firmware-mode";
    public const string DesktopCpuPlan = "desktop-cpu-plan";
    public const string NvidiaPowerLimit = "nvidia-power-limit";
    public const string MonitorBrightness = "monitor-brightness";
}

internal static class MachineProfileDetector
{
    private const string SystemBiosPath = @"HARDWARE\DESCRIPTION\System\BIOS";
    private const string ProcessorPath = @"HARDWARE\DESCRIPTION\System\CentralProcessor\0";
    private const string ArmouryGpuModePath = @"SOFTWARE\ASUS\Armoury Crate Service\GPUMode";

    private static readonly Lazy<MachineIdentity> DetectedMachine = new(Detect);

    public static MachineIdentity Current => DetectedMachine.Value;

    private static MachineIdentity Detect()
    {
        var manufacturer = ReadLocalMachineString(SystemBiosPath, "SystemManufacturer");
        var productName = ReadLocalMachineString(SystemBiosPath, "SystemProductName");
        var baseBoardManufacturer = ReadLocalMachineString(SystemBiosPath, "BaseBoardManufacturer");
        var baseBoardProduct = ReadLocalMachineString(SystemBiosPath, "BaseBoardProduct");
        var processorName = ReadLocalMachineString(ProcessorPath, "ProcessorNameString");
        var armouryControlsAvailable = RegistryKeyExists(ArmouryGpuModePath);

        return Classify(
            manufacturer,
            productName,
            baseBoardManufacturer,
            baseBoardProduct,
            processorName,
            armouryControlsAvailable);
    }

    internal static MachineIdentity Classify(
        string manufacturer,
        string productName,
        string baseBoardManufacturer,
        string baseBoardProduct,
        string processorName,
        bool armouryControlsAvailable)
    {
        var isTargetDesktop = Contains(baseBoardManufacturer, "Gigabyte")
                              && string.Equals(
                                  baseBoardProduct,
                                  "Z790 EAGLE AX",
                                  StringComparison.OrdinalIgnoreCase)
                              && Contains(processorName, "i9-14900K");
        if (isTargetDesktop)
        {
            return new MachineIdentity(
                HardwareProfile.GigabyteDesktop,
                manufacturer,
                productName,
                baseBoardProduct,
                processorName);
        }

        var isTargetHpOmen = Contains(manufacturer, "HP")
                             && Contains(productName, "OMEN by HP Gaming Laptop 16-wd0")
                             && string.Equals(
                                 baseBoardProduct,
                                 "8BA9",
                                 StringComparison.OrdinalIgnoreCase);
        if (isTargetHpOmen)
        {
            return new MachineIdentity(
                HardwareProfile.HpOmenLaptop,
                manufacturer,
                productName,
                baseBoardProduct,
                processorName);
        }

        var isAsusHardware = Contains(manufacturer, "ASUS")
                             || Contains(manufacturer, "ASUSTeK")
                             || Contains(baseBoardManufacturer, "ASUS")
                             || Contains(baseBoardManufacturer, "ASUSTeK");
        var isAsusLaptop = isAsusHardware && armouryControlsAvailable;
        return new MachineIdentity(
            isAsusLaptop ? HardwareProfile.AsusLaptop : HardwareProfile.Unsupported,
            manufacturer,
            productName,
            baseBoardProduct,
            processorName);
    }

    private static string ReadLocalMachineString(string path, string name)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(path, writable: false);
            return key?.GetValue(name) as string ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool RegistryKeyExists(string path)
    {
        try
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = machine.OpenSubKey(path, writable: false);
            return key is not null;
        }
        catch
        {
            return false;
        }
    }

    private static bool Contains(string value, string expected)
    {
        return value.Contains(expected, StringComparison.OrdinalIgnoreCase);
    }
}

internal static class PowerProfileService
{
    public static MachineIdentity Machine => MachineProfileDetector.Current;

    public static PowerProfileApplyResult Apply(LaptopPowerMode mode)
    {
        var result = Machine.Profile switch
        {
            HardwareProfile.AsusLaptop => LaptopPowerProfileBackend.Apply(mode),
            HardwareProfile.HpOmenLaptop => HpOmenPowerProfileBackend.Apply(mode),
            HardwareProfile.GigabyteDesktop => DesktopPowerProfileBackend.Apply(mode),
            _ => new PowerProfileApplyResult(
                mode,
                PowerProfileState.Unsupported(Machine),
                [$"No power profile is configured for {Machine.Description}."],
                [])
        };

        return result with
        {
            SettingResults = BuildApplyResults(result.State, mode, result.Errors)
        };
    }

    public static PowerProfileApplyResult ApplySetting(string settingId, LaptopPowerMode mode)
    {
        var errors = new List<string>();
        try
        {
            switch (Machine.Profile)
            {
                case HardwareProfile.AsusLaptop:
                    LaptopPowerProfileBackend.ApplySetting(settingId, mode);
                    break;
                case HardwareProfile.HpOmenLaptop:
                    HpOmenPowerProfileBackend.ApplySetting(settingId, mode);
                    break;
                case HardwareProfile.GigabyteDesktop:
                    DesktopPowerProfileBackend.ApplySetting(settingId, mode);
                    break;
                default:
                    throw new InvalidOperationException($"No power settings are configured for {Machine.Description}.");
            }
        }
        catch (Exception ex)
        {
            errors.Add(ex.Message);
        }

        // Several vendor services apply changes asynchronously. Give their public
        // state a short settling window before reporting the individual result.
        Thread.Sleep(750);
        var state = ReadState();
        var setting = GetSettings(state).FirstOrDefault(candidate => candidate.Id == settingId);
        if (setting is null)
        {
            errors.Add($"The managed setting '{settingId}' is not available on this machine.");
            return new PowerProfileApplyResult(mode, state, errors, []);
        }

        var matchesTarget = mode == LaptopPowerMode.HighPower
            ? setting.MatchesHighPower
            : setting.MatchesLowPower;
        if (!matchesTarget && errors.Count == 0)
        {
            errors.Add($"Windows reports {setting.CurrentValue}; expected " +
                       (mode == LaptopPowerMode.HighPower ? setting.HighPowerValue : setting.LowPowerValue) + ".");
        }

        var applyResult = new PowerSettingApplyResult(
            setting,
            mode,
            errors.Count == 0,
            errors.Count == 0 ? null : string.Join("; ", errors));
        return new PowerProfileApplyResult(mode, state, errors, [applyResult]);
    }

    public static PowerProfileState ReadState()
    {
        return Machine.Profile switch
        {
            HardwareProfile.AsusLaptop => PowerProfileState.FromLaptop(LaptopPowerProfileBackend.ReadState()),
            HardwareProfile.HpOmenLaptop => PowerProfileState.FromHpOmen(HpOmenPowerProfileBackend.ReadState()),
            HardwareProfile.GigabyteDesktop => PowerProfileState.FromDesktop(DesktopPowerProfileBackend.ReadState()),
            _ => PowerProfileState.Unsupported(Machine)
        };
    }

    public static IReadOnlyList<PowerSettingState> GetSettings(PowerProfileState state)
    {
        return state.Details switch
        {
            LaptopPowerProfileState laptop => GetAsusSettings(laptop),
            HpOmenPowerProfileState hp => GetHpSettings(hp),
            DesktopPowerProfileState desktop => GetDesktopSettings(desktop),
            _ => []
        };
    }

    private static IReadOnlyList<PowerSettingApplyResult> BuildApplyResults(
        PowerProfileState state,
        LaptopPowerMode targetMode,
        IReadOnlyList<string> errors)
    {
        return GetSettings(state).Select(setting =>
        {
            var matches = targetMode == LaptopPowerMode.HighPower
                ? setting.MatchesHighPower
                : setting.MatchesLowPower;
            var relevantErrors = errors.Where(error => ErrorBelongsToSetting(error, setting.Id)).ToArray();
            var error = relevantErrors.Length > 0
                ? string.Join("; ", relevantErrors)
                : matches
                    ? null
                    : $"Current value is {setting.CurrentValue}; expected " +
                      (targetMode == LaptopPowerMode.HighPower ? setting.HighPowerValue : setting.LowPowerValue) + ".";
            return new PowerSettingApplyResult(setting, targetMode, error is null, error);
        }).ToArray();
    }

    private static bool ErrorBelongsToSetting(string error, string settingId)
    {
        if (error.StartsWith("Verification:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return settingId switch
        {
            PowerSettingIds.AsusGpuMode => error.StartsWith("Armoury Crate GPU mode:", StringComparison.OrdinalIgnoreCase)
                                           || error.StartsWith("ASUS GPU mode:", StringComparison.OrdinalIgnoreCase),
            PowerSettingIds.AsusOperatingMode => error.StartsWith("Armoury Crate GPU mode:", StringComparison.OrdinalIgnoreCase)
                                                 || error.StartsWith("ASUS fan/performance mode:", StringComparison.OrdinalIgnoreCase),
            PowerSettingIds.WindowsPowerPlan => error.StartsWith("Windows power plan:", StringComparison.OrdinalIgnoreCase),
            PowerSettingIds.WindowsPowerMode => error.StartsWith("Windows power mode:", StringComparison.OrdinalIgnoreCase),
            PowerSettingIds.DisplayRefreshRate => error.StartsWith("Display refresh rate:", StringComparison.OrdinalIgnoreCase),
            PowerSettingIds.HpOmenFirmwareMode => error.StartsWith("HP OMEN firmware mode:", StringComparison.OrdinalIgnoreCase),
            PowerSettingIds.DesktopCpuPlan => error.StartsWith("Windows CPU power plan:", StringComparison.OrdinalIgnoreCase),
            PowerSettingIds.NvidiaPowerLimit => error.StartsWith("NVIDIA GPU power limit:", StringComparison.OrdinalIgnoreCase),
            PowerSettingIds.MonitorBrightness => error.StartsWith("Monitor brightness:", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static IReadOnlyList<PowerSettingState> GetAsusSettings(LaptopPowerProfileState state)
    {
        var gpu = state.ArmouryGpuMode ?? state.AsusGpuEco switch
        {
            1 => "Eco",
            0 => "Standard",
            _ => "Unknown"
        };
        var operating = state.ArmouryOperatingMode ?? state.AsusPerformanceMode switch
        {
            AsusHardwareService.PerformanceMode => "Performance",
            AsusHardwareService.SilentMode => "Silent",
            _ => "Unknown"
        };
        return
        [
            Setting(PowerSettingIds.AsusGpuMode, "GPU mode", "Eco", "Optimized", gpu,
                Is(gpu, "Eco"), Is(gpu, "Optimized")),
            Setting(PowerSettingIds.AsusOperatingMode, "ASUS fan / performance mode", "Silent", "Performance", operating,
                Is(operating, "Silent"), Is(operating, "Performance")),
            Setting(PowerSettingIds.WindowsPowerPlan, "Windows power plan", "Silent", "Performance",
                state.WindowsPlanName ?? "Unknown", Is(state.WindowsPlanName, "Silent"), Is(state.WindowsPlanName, "Performance")),
            Setting(PowerSettingIds.WindowsPowerMode, "Windows power mode", "Best power efficiency", "Best performance",
                FormatWindowsPowerMode(state.WindowsPowerMode),
                state.WindowsPowerMode == WindowsPowerService.BestPowerEfficiency,
                state.WindowsPowerMode == WindowsPowerService.BestPerformance),
            RefreshSetting(state.RefreshRateHz, 60, 120)
        ];
    }

    private static IReadOnlyList<PowerSettingState> GetHpSettings(HpOmenPowerProfileState state)
    {
        var highRate = state.MaximumRefreshRateHz ?? 165;
        return
        [
            Setting(PowerSettingIds.HpOmenFirmwareMode, "HP OMEN firmware mode", "Eco", "Performance",
                state.RequestedOmenMode ?? "Unknown", Is(state.RequestedOmenMode, "Eco"), Is(state.RequestedOmenMode, "Performance")),
            Setting(PowerSettingIds.WindowsPowerMode, "Windows power mode", "Best power efficiency", "Best performance",
                FormatWindowsPowerMode(state.WindowsPowerMode),
                state.WindowsPowerMode == WindowsPowerService.BestPowerEfficiency,
                state.WindowsPowerMode == WindowsPowerService.BestPerformance),
            RefreshSetting(state.RefreshRateHz, 60, highRate)
        ];
    }

    private static IReadOnlyList<PowerSettingState> GetDesktopSettings(DesktopPowerProfileState state)
    {
        var plan = state.WindowsPlanName ?? "Unknown";
        var gpu = state.NvidiaPowerLimitWatts is { } watts ? $"{watts:0} W" : "Unknown";
        var brightness = state.MonitorBrightnessPercent is { } percent ? $"{percent}%" : "Unknown";
        return
        [
            Setting(PowerSettingIds.DesktopCpuPlan, "Windows CPU policy / power plan",
                DesktopWindowsPowerService.LowPlanName, DesktopWindowsPowerService.HighPlanName, plan,
                Is(plan, DesktopWindowsPowerService.LowPlanName) || Is(plan, "Power saver"),
                Is(plan, DesktopWindowsPowerService.HighPlanName) || Is(plan, "High performance")),
            Setting(PowerSettingIds.NvidiaPowerLimit, "NVIDIA GPU power limit", "150 W", "450 W", gpu,
                Near(state.NvidiaPowerLimitWatts, 150), Near(state.NvidiaPowerLimitWatts, 450)),
            Setting(PowerSettingIds.MonitorBrightness, "Monitor brightness", "35%", "100%", brightness,
                state.MonitorBrightnessPercent is >= 0 and <= 40, state.MonitorBrightnessPercent is >= 95),
            RefreshSetting(state.RefreshRateHz, 60, 165)
        ];
    }

    private static PowerSettingState RefreshSetting(int? current, int low, int high)
    {
        return Setting(PowerSettingIds.DisplayRefreshRate, "Display refresh rate", $"{low} Hz", $"{high} Hz",
            current is { } rate ? $"{rate} Hz" : "Unknown",
            current is { } lowRate && lowRate > 0 && lowRate <= low + 1,
            current is { } highRate && highRate >= high - 5);
    }

    private static PowerSettingState Setting(
        string id, string name, string low, string high, string current, bool matchesLow, bool matchesHigh)
    {
        return new PowerSettingState(id, name, low, high, current, matchesLow, matchesHigh);
    }

    private static string FormatWindowsPowerMode(Guid? mode)
    {
        if (mode == WindowsPowerService.BestPowerEfficiency) return "Best power efficiency";
        if (mode == WindowsPowerService.BestPerformance) return "Best performance";
        return mode is null ? "Unknown" : "Balanced";
    }

    private static bool Is(string? actual, string expected) =>
        string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

    private static bool Near(double? actual, double expected) =>
        actual is { } value && Math.Abs(value - expected) <= 1.0;
}
