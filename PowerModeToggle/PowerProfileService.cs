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
    IReadOnlyList<string> Errors)
{
    public bool Success => Errors.Count == 0;
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
        return Machine.Profile switch
        {
            HardwareProfile.AsusLaptop => LaptopPowerProfileBackend.Apply(mode),
            HardwareProfile.GigabyteDesktop => DesktopPowerProfileBackend.Apply(mode),
            _ => new PowerProfileApplyResult(
                mode,
                PowerProfileState.Unsupported(Machine),
                [$"No power profile is configured for {Machine.Description}."])
        };
    }

    public static PowerProfileState ReadState()
    {
        return Machine.Profile switch
        {
            HardwareProfile.AsusLaptop => PowerProfileState.FromLaptop(LaptopPowerProfileBackend.ReadState()),
            HardwareProfile.GigabyteDesktop => PowerProfileState.FromDesktop(DesktopPowerProfileBackend.ReadState()),
            _ => PowerProfileState.Unsupported(Machine)
        };
    }
}
