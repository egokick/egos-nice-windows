namespace PowerModeToggle;

internal static class AppIdentity
{
    public const string DisplayName = "PowerModeToggle";

    public static string SettingsFolder => MachineProfileDetector.Current.Profile == HardwareProfile.GigabyteDesktop
        ? "PowerModeToggleDesktop"
        : "PowerModeToggle";

    public static string HighProfileDescription => MachineProfileDetector.Current.Profile switch
    {
        HardwareProfile.GigabyteDesktop => "165 Hz, 450 W GPU, performance CPU, 100% brightness",
        HardwareProfile.AsusLaptop => "120 Hz, Performance",
        HardwareProfile.HpOmenLaptop => "maximum refresh rate, OMEN Performance, Windows Best performance",
        _ => "unsupported hardware"
    };

    public static string LowProfileDescription => MachineProfileDetector.Current.Profile switch
    {
        HardwareProfile.GigabyteDesktop => "60 Hz, 150 W GPU, efficient CPU, 35% brightness",
        HardwareProfile.AsusLaptop => "60 Hz, Eco/Silent",
        HardwareProfile.HpOmenLaptop => "60 Hz, OMEN Eco, Windows Best power efficiency",
        _ => "unsupported hardware"
    };

    public const string SingletonMutex = DisplayName + ".Singleton";
    public const string PowerHelperPipePrefix = DisplayName + ".PowerProfile";
}
