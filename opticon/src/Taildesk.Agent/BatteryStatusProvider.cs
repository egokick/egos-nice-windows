using System.Runtime.InteropServices;

namespace Taildesk.Agent;

/// <summary>
/// Reads Windows battery telemetry at most once per five minutes. Agent status
/// is requested frequently, so every request between probes returns the cache.
/// </summary>
public sealed class BatteryStatusProvider
{
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);
    private readonly object _gate = new();
    private DateTimeOffset _nextPollAt;
    private int? _cachedPercentage;

    public int? GetBatteryPercentage()
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            if (now < _nextPollAt) return _cachedPercentage;
            _nextPollAt = now.Add(PollInterval);
            _cachedPercentage = ReadBatteryPercentage();
            return _cachedPercentage;
        }
    }

    private static int? ReadBatteryPercentage()
    {
        if (!OperatingSystem.IsWindows() || !GetSystemPowerStatus(out var status)) return null;
        const byte noSystemBattery = 128;
        const byte unknown = byte.MaxValue;
        if (status.BatteryFlag is noSystemBattery or unknown || status.BatteryLifePercent == unknown) return null;
        return Math.Clamp((int)status.BatteryLifePercent, 0, 100);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }
}
