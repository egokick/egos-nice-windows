namespace PowerModeToggle;

internal static class AppIdentity
{
#if DESKTOP_POWER_TOGGLE
    public const string DisplayName = "PowerModeToggleDesktop";
    public const string SettingsFolder = "PowerModeToggleDesktop";
    public const string HighProfileDescription = "165 Hz, 450 W GPU, performance CPU, 100% brightness";
    public const string LowProfileDescription = "60 Hz, 150 W GPU, efficient CPU, 35% brightness";
#else
    public const string DisplayName = "PowerModeToggle";
    public const string SettingsFolder = "PowerModeToggle";
    public const string HighProfileDescription = "120 Hz, Performance";
    public const string LowProfileDescription = "60 Hz, Eco/Silent";
#endif

    public const string SingletonMutex = DisplayName + ".Singleton";
    public const string PowerHelperPipePrefix = DisplayName + ".PowerProfile";
}
