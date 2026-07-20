using System.ComponentModel;
using System.Management;
using System.Text;
using Microsoft.Win32;

namespace PowerModeToggle;

internal sealed record HpOmenPowerProfileState(
    string? RequestedOmenMode,
    int? RefreshRateHz,
    int? MaximumRefreshRateHz,
    string? WindowsPlanName,
    Guid? WindowsPowerMode)
{
    private static readonly Guid BestPowerEfficiency = new("961cc777-2547-4f9d-8174-7d86181b8a7a");
    private static readonly Guid BestPerformance = new("ded574b5-45a0-4f42-8737-46345c09c238");

    public LaptopPowerMode? DetectedMode
    {
        get
        {
            if ((RequestedOmenMode is null
                 || string.Equals(RequestedOmenMode, HpOmenFirmwareService.PerformanceMarker, StringComparison.Ordinal))
                && RefreshRateHz is > 0
                && MaximumRefreshRateHz is > 0
                && RefreshRateHz == MaximumRefreshRateHz
                && WindowsPowerMode == BestPerformance)
            {
                return LaptopPowerMode.HighPower;
            }

            if ((RequestedOmenMode is null
                 || string.Equals(RequestedOmenMode, HpOmenFirmwareService.EcoMarker, StringComparison.Ordinal))
                && RefreshRateHz is > 0 and <= 60
                && WindowsPowerMode == BestPowerEfficiency)
            {
                return LaptopPowerMode.LowPower;
            }

            return null;
        }
    }

    public string ToSummary()
    {
        var omenMode = RequestedOmenMode is null ? "OMEN mode unknown" : $"OMEN {RequestedOmenMode}";
        var refresh = RefreshRateHz is { } rate ? $"{rate} Hz" : "refresh unknown";
        var maximum = MaximumRefreshRateHz is { } max ? $"max {max} Hz" : "maximum refresh unknown";
        var plan = string.IsNullOrWhiteSpace(WindowsPlanName) ? "Windows plan unknown" : WindowsPlanName;
        var overlay = WindowsPowerMode switch
        {
            var mode when mode == BestPerformance => "Best performance",
            var mode when mode == BestPowerEfficiency => "Best power efficiency",
            null => "Windows power mode unknown",
            _ => "Balanced Windows power mode"
        };
        return $"{omenMode}; {refresh} ({maximum}); {plan}; {overlay}";
    }
}

internal static class HpOmenPowerProfileBackend
{
    public static PowerProfileApplyResult Apply(LaptopPowerMode mode)
    {
        var errors = new List<string>();
        var highPower = mode == LaptopPowerMode.HighPower;

        try
        {
            HpOmenFirmwareService.SetMode(highPower);
        }
        catch (Exception ex)
        {
            errors.Add($"HP OMEN firmware mode: {ex.Message}");
        }

        try
        {
            WindowsPowerService.SetPowerMode(highPower);
        }
        catch (Exception ex)
        {
            errors.Add($"Windows power mode: {ex.Message}");
        }

        try
        {
            var targetRefreshRate = highPower
                ? DisplayRefreshRateService.TryGetHighestPrimaryDisplayRefreshRate()
                  ?? throw new InvalidOperationException("Windows could not determine the display's maximum refresh rate.")
                : 60;
            DisplayRefreshRateService.SetPrimaryDisplayRefreshRate(targetRefreshRate);
        }
        catch (Exception ex)
        {
            errors.Add($"Display refresh rate: {ex.Message}");
        }

        var state = WaitForRequestedState(mode, TimeSpan.FromSeconds(8));
        if (state.DetectedMode != mode)
        {
            errors.Add($"Verification: the HP OMEN laptop did not remain fully in {mode} ({state.ToSummary()}).");
        }

        return new PowerProfileApplyResult(mode, PowerProfileState.FromHpOmen(state), errors);
    }

    public static HpOmenPowerProfileState ReadState()
    {
        return new HpOmenPowerProfileState(
            HpOmenFirmwareService.TryReadMarker(),
            DisplayRefreshRateService.TryGetPrimaryDisplayRefreshRate(),
            DisplayRefreshRateService.TryGetHighestPrimaryDisplayRefreshRate(),
            WindowsPowerService.TryGetActivePlanName(),
            WindowsPowerService.TryGetPowerMode());
    }

    private static HpOmenPowerProfileState WaitForRequestedState(LaptopPowerMode mode, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        HpOmenPowerProfileState state;
        do
        {
            state = ReadState();
            if (state.DetectedMode == mode)
            {
                return state;
            }

            Thread.Sleep(400);
        }
        while (DateTime.UtcNow < deadline);

        return state;
    }
}

internal static class HpOmenFirmwareService
{
    internal const string EcoMarker = "Eco";
    internal const string PerformanceMarker = "Performance";

    private const int OmenCommand = 0x20008;
    private const int SetPerformanceModeCommandType = 0x1A;
    private const int GetSystemDesignDataCommandType = 0x28;
    private const string MarkerPath = @"Software\PowerModeToggle\HpOmen";
    private const string MarkerName = "RequestedMode";

    public static void SetMode(bool highPower)
    {
        var policyVersion = TryGetThermalPolicyVersion();
        var firmwareMode = highPower
            ? policyVersion == 1 ? (byte)0x31 : (byte)0x01
            : (byte)0x00;

        var result = HpBiosWmi.Execute(
            OmenCommand,
            SetPerformanceModeCommandType,
            [0xFF, firmwareMode, 0x00, 0x00],
            4);
        if (result.ReturnCode != 0)
        {
            throw new InvalidOperationException(
                $"HP BIOS rejected {(highPower ? PerformanceMarker : EcoMarker)} mode (return code {result.ReturnCode}).");
        }

        using var key = Registry.CurrentUser.CreateSubKey(MarkerPath, writable: true)
                        ?? throw new InvalidOperationException("The HP OMEN mode marker could not be saved.");
        key.SetValue(MarkerName, highPower ? PerformanceMarker : EcoMarker, RegistryValueKind.String);
    }

    public static string? TryReadMarker()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(MarkerPath, writable: false);
            return key?.GetValue(MarkerName) as string;
        }
        catch
        {
            return null;
        }
    }

    private static int TryGetThermalPolicyVersion()
    {
        try
        {
            var result = HpBiosWmi.Execute(
                OmenCommand,
                GetSystemDesignDataCommandType,
                null,
                128);
            return result.ReturnCode == 0 && result.Data.Length > 3 ? result.Data[3] : 0;
        }
        catch
        {
            // The 16-wd0xxx firmware accepts the legacy performance byte as a
            // fallback if its policy-version query is temporarily unavailable.
            return 0;
        }
    }
}

internal sealed record HpBiosWmiResult(uint ReturnCode, byte[] Data);

internal static class HpBiosWmi
{
    private const string NamespacePath = @"\\.\root\WMI";
    private const string InterfaceClass = "hpqBIntM";
    private const string InputClass = "hpqBDataIn";

    public static HpBiosWmiResult Execute(uint command, uint commandType, byte[]? inputData, int returnDataSize)
    {
        var scope = new ManagementScope(NamespacePath, new ConnectionOptions
        {
            EnablePrivileges = true,
            Impersonation = ImpersonationLevel.Impersonate,
            Authentication = AuthenticationLevel.PacketPrivacy,
            Timeout = TimeSpan.FromSeconds(8)
        });
        scope.Connect();

        using var searcher = new ManagementObjectSearcher(
            scope,
            new ObjectQuery($"SELECT * FROM {InterfaceClass}"),
            new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(8), ReturnImmediately = false });
        using var interfaces = searcher.Get();
        using var biosInterface = interfaces.Cast<ManagementObject>().FirstOrDefault()
                                  ?? throw new InvalidOperationException("The HP BIOS WMI interface was not found.");

        using var inputClass = new ManagementClass(scope, new ManagementPath(InputClass), null);
        using var wmiInput = inputClass.CreateInstance()
                             ?? throw new InvalidOperationException("The HP BIOS WMI input buffer could not be created.");
        wmiInput["Sign"] = Encoding.ASCII.GetBytes("SECU");
        wmiInput["Command"] = command;
        wmiInput["CommandType"] = commandType;
        wmiInput["Size"] = (uint)(inputData?.Length ?? 0);
        if (inputData is not null)
        {
            // hpqBData has a WmiSizeIs qualifier tied to Size. Passing a
            // padded 128-byte buffer while declaring Size = 4 is rejected by
            // HP's provider; output sizing is selected by the method name.
            wmiInput["hpqBData"] = inputData;
        }

        var methodName = returnDataSize switch
        {
            <= 0 => "hpqBIOSInt0",
            <= 4 => "hpqBIOSInt4",
            <= 128 => "hpqBIOSInt128",
            <= 1024 => "hpqBIOSInt1024",
            _ => "hpqBIOSInt4096"
        };
        using var methodInput = biosInterface.GetMethodParameters(methodName);
        methodInput["InData"] = wmiInput;
        using var methodOutput = biosInterface.InvokeMethod(
            methodName,
            methodInput,
            new InvokeMethodOptions { Timeout = TimeSpan.FromSeconds(8) })
            ?? throw new InvalidOperationException("The HP BIOS WMI call returned no result.");
        using var output = methodOutput["OutData"] as ManagementBaseObject
                           ?? throw new InvalidOperationException("The HP BIOS WMI call returned no output buffer.");

        var returnCode = Convert.ToUInt32(output["rwReturnCode"] ?? uint.MaxValue);
        var data = output.Properties["Data"]?.Value as byte[] ?? [];
        return new HpBiosWmiResult(returnCode, data);
    }
}
