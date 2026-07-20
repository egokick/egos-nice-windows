using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PowerModeToggle;

internal sealed record PowerTelemetryReading(
    DateTimeOffset CapturedAtUtc,
    double? EstimatedUsageWatts,
    double? IntervalEnergyWh,
    double IntervalSeconds,
    string SourceKey,
    string SourceDescription,
    bool CoversWholeSystem);

internal sealed record PowerSavingsSnapshot(
    LaptopPowerMode Mode,
    double? CurrentUsageWatts,
    double? EstimatedHighUsageWatts,
    double? EstimatedLowUsageWatts,
    double? EstimatedSavingWatts,
    double EcoSessionEnergySavedWh,
    double TotalEnergySavedWh,
    string SourceKey,
    string SourceDescription,
    bool CoversWholeSystem,
    int BaselineSamples,
    int RequiredBaselineSamples,
    bool HighBaselineAvailable,
    bool LowBaselineAvailable);

internal sealed class PowerUsageBaseline
{
    public double? HighWatts { get; set; }

    public DateTimeOffset? HighUpdatedUtc { get; set; }

    public double? LowWatts { get; set; }

    public DateTimeOffset? LowUpdatedUtc { get; set; }

    public PowerUsageBaseline Clone()
    {
        return new PowerUsageBaseline
        {
            HighWatts = HighWatts,
            HighUpdatedUtc = HighUpdatedUtc,
            LowWatts = LowWatts,
            LowUpdatedUtc = LowUpdatedUtc
        };
    }
}

internal sealed record PowerSavingsPersistentState(
    double TotalEnergySavedWh,
    Dictionary<string, PowerUsageBaseline> Baselines);

internal sealed class PowerSavingsEstimator
{
    internal const int RequiredBaselineSamples = 8;
    private const int BaselineWindowSize = 30;
    private const int CurrentWindowSize = 5;
    private static readonly TimeSpan MaximumBaselineAge = TimeSpan.FromDays(30);

    private readonly Dictionary<string, PowerUsageBaseline> _baselines;
    private readonly Queue<double> _baselineWindow = new();
    private readonly Queue<double> _currentWindow = new();

    private string? _activeSourceKey;
    private LaptopPowerMode? _previousMode;
    private double _ecoSessionEnergySavedWh;
    private double _totalEnergySavedWh;

    public PowerSavingsEstimator(
        double totalEnergySavedWh,
        IReadOnlyDictionary<string, PowerUsageBaseline>? baselines = null)
    {
        _totalEnergySavedWh = Math.Max(0, totalEnergySavedWh);
        _baselines = baselines?.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal)
                     ?? new Dictionary<string, PowerUsageBaseline>(StringComparer.Ordinal);
        RemoveExpiredBaselines(DateTimeOffset.UtcNow);
    }

    public PowerSavingsSnapshot Update(LaptopPowerMode mode, PowerTelemetryReading reading)
    {
        var sourceChanged = !string.Equals(_activeSourceKey, reading.SourceKey, StringComparison.Ordinal);
        var modeChanged = _previousMode is not null && _previousMode != mode;
        if (sourceChanged || modeChanged)
        {
            _currentWindow.Clear();
            _baselineWindow.Clear();
        }

        if (modeChanged && mode == LaptopPowerMode.LowPower)
        {
            _ecoSessionEnergySavedWh = 0;
        }

        _activeSourceKey = reading.SourceKey;
        _previousMode = mode;

        if (!_baselines.TryGetValue(reading.SourceKey, out var baseline))
        {
            baseline = new PowerUsageBaseline();
            _baselines.Add(reading.SourceKey, baseline);
        }

        if (reading.EstimatedUsageWatts is { } rawWatts && IsReasonablePower(rawWatts))
        {
            AddSample(_currentWindow, rawWatts, CurrentWindowSize);
            AddSample(_baselineWindow, rawWatts, BaselineWindowSize);

            if (_baselineWindow.Count >= RequiredBaselineSamples)
            {
                var learnedWatts = CalculateTrimmedMean(_baselineWindow);
                if (mode == LaptopPowerMode.HighPower)
                {
                    baseline.HighWatts = learnedWatts;
                    baseline.HighUpdatedUtc = reading.CapturedAtUtc;
                }
                else
                {
                    baseline.LowWatts = learnedWatts;
                    baseline.LowUpdatedUtc = reading.CapturedAtUtc;
                }
            }

            if (mode == LaptopPowerMode.LowPower
                && GetFreshHighBaseline(baseline, reading.CapturedAtUtc) is { } highWatts
                && reading.IntervalEnergyWh is { } measuredIntervalWh
                && reading.IntervalSeconds is >= 0.5 and <= 15)
            {
                var rawSavingWatts = highWatts - rawWatts;
                var noiseFloorWatts = Math.Max(2.0, highWatts * 0.03);
                if (rawSavingWatts > noiseFloorWatts)
                {
                    var predictedHighWh = highWatts * reading.IntervalSeconds / 3600d;
                    var intervalSavedWh = Math.Max(0, predictedHighWh - measuredIntervalWh);
                    _ecoSessionEnergySavedWh += intervalSavedWh;
                    _totalEnergySavedWh += intervalSavedWh;
                }
            }
        }

        var currentWatts = _currentWindow.Count > 0 ? CalculateMedian(_currentWindow) : (double?)null;
        var highBaseline = GetFreshHighBaseline(baseline, reading.CapturedAtUtc);
        var lowBaseline = GetFreshLowBaseline(baseline, reading.CapturedAtUtc);
        var estimatedHigh = mode == LaptopPowerMode.HighPower ? currentWatts : highBaseline;
        var estimatedLow = mode == LaptopPowerMode.LowPower ? currentWatts : lowBaseline;
        var estimatedSaving = estimatedHigh is { } high && estimatedLow is { } low
            ? Math.Max(0, high - low)
            : (double?)null;

        return new PowerSavingsSnapshot(
            mode,
            currentWatts,
            estimatedHigh,
            estimatedLow,
            estimatedSaving,
            _ecoSessionEnergySavedWh,
            _totalEnergySavedWh,
            reading.SourceKey,
            reading.SourceDescription,
            reading.CoversWholeSystem,
            _baselineWindow.Count,
            RequiredBaselineSamples,
            highBaseline is not null,
            lowBaseline is not null);
    }

    public PowerSavingsPersistentState GetPersistentState()
    {
        return new PowerSavingsPersistentState(
            _totalEnergySavedWh,
            _baselines.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal));
    }

    private void RemoveExpiredBaselines(DateTimeOffset now)
    {
        foreach (var baseline in _baselines.Values)
        {
            if (baseline.HighUpdatedUtc is not { } highUpdated || now - highUpdated > MaximumBaselineAge)
            {
                baseline.HighWatts = null;
                baseline.HighUpdatedUtc = null;
            }

            if (baseline.LowUpdatedUtc is not { } lowUpdated || now - lowUpdated > MaximumBaselineAge)
            {
                baseline.LowWatts = null;
                baseline.LowUpdatedUtc = null;
            }
        }
    }

    private static double? GetFreshHighBaseline(PowerUsageBaseline baseline, DateTimeOffset now)
    {
        return baseline.HighWatts is { } watts
               && baseline.HighUpdatedUtc is { } updated
               && now - updated <= MaximumBaselineAge
            ? watts
            : null;
    }

    private static double? GetFreshLowBaseline(PowerUsageBaseline baseline, DateTimeOffset now)
    {
        return baseline.LowWatts is { } watts
               && baseline.LowUpdatedUtc is { } updated
               && now - updated <= MaximumBaselineAge
            ? watts
            : null;
    }

    private static bool IsReasonablePower(double watts)
    {
        return double.IsFinite(watts) && watts is >= 0 and <= 2000;
    }

    private static void AddSample(Queue<double> samples, double value, int capacity)
    {
        samples.Enqueue(value);
        while (samples.Count > capacity)
        {
            samples.Dequeue();
        }
    }

    private static double CalculateMedian(IEnumerable<double> samples)
    {
        var ordered = samples.Order().ToArray();
        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 0
            ? (ordered[middle - 1] + ordered[middle]) / 2d
            : ordered[middle];
    }

    private static double CalculateTrimmedMean(IEnumerable<double> samples)
    {
        var ordered = samples.Order().ToArray();
        var trim = ordered.Length >= 10 ? Math.Max(1, ordered.Length / 10) : 0;
        var retained = ordered.Skip(trim).Take(ordered.Length - trim * 2).ToArray();
        return retained.Average();
    }
}

internal sealed class PowerTelemetryService : IDisposable
{
    private readonly HardwareProfile _profile;
    private readonly List<ICumulativeEnergySource> _componentSources = [];
    private readonly BatteryDischargeSource? _batterySource;
    private readonly Dictionary<string, double> _previousTotals = new(StringComparer.Ordinal);

    private long _previousTimestamp;
    private string? _previousSourceKey;
    private double? _previousBatteryWatts;
    private bool _disposed;

    public PowerTelemetryService(HardwareProfile profile)
    {
        _profile = profile;
        if (profile is HardwareProfile.GigabyteDesktop or HardwareProfile.AsusLaptop or HardwareProfile.HpOmenLaptop)
        {
            if (WindowsRaplEnergySource.TryCreate() is { } rapl)
            {
                _componentSources.Add(rapl);
            }

            if (NvidiaEnergySource.TryCreate() is { } nvidia)
            {
                _componentSources.Add(nvidia);
            }
        }

        if (profile is HardwareProfile.AsusLaptop or HardwareProfile.HpOmenLaptop)
        {
            _batterySource = new BatteryDischargeSource();
        }
    }

    public PowerTelemetryReading Sample()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var capturedAt = DateTimeOffset.UtcNow;
        var timestamp = Stopwatch.GetTimestamp();
        if (_batterySource?.TryReadDischarge(out var batteryWatts) == true)
        {
            return SampleBattery(capturedAt, timestamp, batteryWatts);
        }

        var totals = new Dictionary<string, (double TotalWh, string Name)>(StringComparer.Ordinal);
        foreach (var source in _componentSources)
        {
            if (source.TryReadTotalEnergyWh(out var totalWh))
            {
                totals[source.Key] = (totalWh, source.DisplayName);
            }
        }

        if (totals.Count == 0)
        {
            ResetPreviousSample();
            return new PowerTelemetryReading(
                capturedAt,
                null,
                null,
                0,
                "unavailable",
                "Power telemetry unavailable",
                false);
        }

        var ordered = totals.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray();
        var sourceKey = string.Join("+", ordered.Select(pair => pair.Key));
        var description = string.Join(" + ", ordered.Select(pair => pair.Value.Name)) + " (components)";
        var elapsedSeconds = GetElapsedSeconds(timestamp);
        double? intervalWh = null;
        double? watts = null;

        if (string.Equals(sourceKey, _previousSourceKey, StringComparison.Ordinal)
            && elapsedSeconds is >= 0.5 and <= 15
            && ordered.All(pair => _previousTotals.ContainsKey(pair.Key)))
        {
            var deltas = ordered
                .Select(pair => pair.Value.TotalWh - _previousTotals[pair.Key])
                .ToArray();
            if (deltas.All(delta => double.IsFinite(delta) && delta >= 0))
            {
                intervalWh = deltas.Sum();
                var calculatedWatts = intervalWh.Value * 3600d / elapsedSeconds;
                if (calculatedWatts is >= 0 and <= 2000)
                {
                    watts = calculatedWatts;
                }
                else
                {
                    intervalWh = null;
                }
            }
        }

        _previousTotals.Clear();
        foreach (var pair in ordered)
        {
            _previousTotals[pair.Key] = pair.Value.TotalWh;
        }

        _previousSourceKey = sourceKey;
        _previousTimestamp = timestamp;
        _previousBatteryWatts = null;

        return new PowerTelemetryReading(
            capturedAt,
            watts,
            intervalWh,
            elapsedSeconds,
            sourceKey,
            description,
            false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var source in _componentSources)
        {
            source.Dispose();
        }

        _componentSources.Clear();
    }

    private PowerTelemetryReading SampleBattery(DateTimeOffset capturedAt, long timestamp, double batteryWatts)
    {
        const string sourceKey = "battery-discharge";
        var elapsedSeconds = GetElapsedSeconds(timestamp);
        double? intervalWh = null;
        if (string.Equals(sourceKey, _previousSourceKey, StringComparison.Ordinal)
            && _previousBatteryWatts is { } previousWatts
            && elapsedSeconds is >= 0.5 and <= 15)
        {
            intervalWh = (previousWatts + batteryWatts) / 2d * elapsedSeconds / 3600d;
        }

        _previousTotals.Clear();
        _previousSourceKey = sourceKey;
        _previousTimestamp = timestamp;
        _previousBatteryWatts = batteryWatts;

        return new PowerTelemetryReading(
            capturedAt,
            batteryWatts,
            intervalWh,
            elapsedSeconds,
            sourceKey,
            "Laptop battery discharge (whole system)",
            true);
    }

    private double GetElapsedSeconds(long timestamp)
    {
        return _previousTimestamp == 0
            ? 0
            : (timestamp - _previousTimestamp) / (double)Stopwatch.Frequency;
    }

    private void ResetPreviousSample()
    {
        _previousTotals.Clear();
        _previousTimestamp = 0;
        _previousSourceKey = null;
        _previousBatteryWatts = null;
    }
}

internal interface ICumulativeEnergySource : IDisposable
{
    string Key { get; }

    string DisplayName { get; }

    bool TryReadTotalEnergyWh(out double totalEnergyWh);
}

internal sealed class WindowsRaplEnergySource : ICumulativeEnergySource
{
    private const string CounterPath = @"\Energy Meter(RAPL_Package0_PKG)\Energy";
    private const uint PdhFormatDouble = 0x00000200;

    private IntPtr _query;
    private IntPtr _counter;

    private WindowsRaplEnergySource(IntPtr query, IntPtr counter)
    {
        _query = query;
        _counter = counter;
    }

    public string Key => "intel-rapl-package";

    public string DisplayName => "CPU package";

    public static WindowsRaplEnergySource? TryCreate()
    {
        IntPtr query = IntPtr.Zero;
        try
        {
            if (PdhOpenQueryW(null, IntPtr.Zero, out query) != 0
                || PdhAddEnglishCounterW(query, CounterPath, IntPtr.Zero, out var counter) != 0
                || PdhCollectQueryData(query) != 0)
            {
                if (query != IntPtr.Zero)
                {
                    _ = PdhCloseQuery(query);
                }

                return null;
            }

            return new WindowsRaplEnergySource(query, counter);
        }
        catch
        {
            if (query != IntPtr.Zero)
            {
                _ = PdhCloseQuery(query);
            }

            return null;
        }
    }

    public bool TryReadTotalEnergyWh(out double totalEnergyWh)
    {
        totalEnergyWh = 0;
        if (_query == IntPtr.Zero
            || PdhCollectQueryData(_query) != 0
            || PdhGetFormattedCounterValue(_counter, PdhFormatDouble, IntPtr.Zero, out var value) != 0
            || value.CStatus != 0
            || !double.IsFinite(value.DoubleValue)
            || value.DoubleValue < 0)
        {
            return false;
        }

        // Windows EMI exposes accumulated energy in picowatt-hours.
        totalEnergyWh = value.DoubleValue / 1_000_000_000_000d;
        return true;
    }

    public void Dispose()
    {
        if (_query == IntPtr.Zero)
        {
            return;
        }

        _ = PdhCloseQuery(_query);
        _query = IntPtr.Zero;
        _counter = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFormattedCounterValue
    {
        public uint CStatus;
        private readonly uint _padding;
        public double DoubleValue;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(
        IntPtr query,
        string fullCounterPath,
        IntPtr userData,
        out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll")]
    private static extern uint PdhGetFormattedCounterValue(
        IntPtr counter,
        uint format,
        IntPtr counterType,
        out PdhFormattedCounterValue value);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);
}

internal sealed class NvidiaEnergySource : ICumulativeEnergySource
{
    private IntPtr _device;
    private bool _initialized;

    private NvidiaEnergySource(IntPtr device)
    {
        _device = device;
        _initialized = true;
    }

    public string Key => "nvidia-total-energy";

    public string DisplayName => "NVIDIA GPU";

    public static NvidiaEnergySource? TryCreate()
    {
        try
        {
            if (NvmlInit() != 0)
            {
                return null;
            }

            if (NvmlDeviceGetHandleByIndex(0, out var device) != 0
                || NvmlDeviceGetTotalEnergyConsumption(device, out _) != 0)
            {
                _ = NvmlShutdown();
                return null;
            }

            return new NvidiaEnergySource(device);
        }
        catch
        {
            try
            {
                _ = NvmlShutdown();
            }
            catch
            {
            }

            return null;
        }
    }

    public bool TryReadTotalEnergyWh(out double totalEnergyWh)
    {
        totalEnergyWh = 0;
        if (!_initialized
            || NvmlDeviceGetTotalEnergyConsumption(_device, out var totalEnergyMilliJoules) != 0)
        {
            return false;
        }

        totalEnergyWh = totalEnergyMilliJoules / 3_600_000d;
        return true;
    }

    public void Dispose()
    {
        if (!_initialized)
        {
            return;
        }

        _initialized = false;
        _device = IntPtr.Zero;
        _ = NvmlShutdown();
    }

    [DllImport("nvml.dll", EntryPoint = "nvmlInit_v2", CallingConvention = CallingConvention.Cdecl)]
    private static extern int NvmlInit();

    [DllImport("nvml.dll", EntryPoint = "nvmlShutdown", CallingConvention = CallingConvention.Cdecl)]
    private static extern int NvmlShutdown();

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByIndex_v2", CallingConvention = CallingConvention.Cdecl)]
    private static extern int NvmlDeviceGetHandleByIndex(uint index, out IntPtr device);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetTotalEnergyConsumption", CallingConvention = CallingConvention.Cdecl)]
    private static extern int NvmlDeviceGetTotalEnergyConsumption(IntPtr device, out ulong energyMilliJoules);
}

internal sealed class BatteryDischargeSource
{
    private const int SystemBatteryStateInformationLevel = 5;

    public bool TryReadDischarge(out double watts)
    {
        watts = 0;
        try
        {
            if (CallNtPowerInformation(
                    SystemBatteryStateInformationLevel,
                    IntPtr.Zero,
                    0,
                    out var state,
                    (uint)Marshal.SizeOf<SystemBatteryState>()) != 0
                || state.BatteryPresent == 0
                || state.Discharging == 0)
            {
                return false;
            }

            var signedRateMilliwatts = unchecked((int)state.Rate);
            if (signedRateMilliwatts == 0 || signedRateMilliwatts == -1)
            {
                return false;
            }

            watts = Math.Abs((double)signedRateMilliwatts) / 1000d;
            return watts is > 0 and <= 1000;
        }
        catch
        {
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct SystemBatteryState
    {
        public byte AcOnLine;
        public byte BatteryPresent;
        public byte Charging;
        public byte Discharging;
        public byte Spare1;
        public byte Spare2;
        public byte Spare3;
        public byte Tag;
        public uint MaxCapacity;
        public uint RemainingCapacity;
        public uint Rate;
        public uint EstimatedTime;
        public uint DefaultAlert1;
        public uint DefaultAlert2;
    }

    [DllImport("powrprof.dll")]
    private static extern uint CallNtPowerInformation(
        int informationLevel,
        IntPtr inputBuffer,
        uint inputBufferLength,
        out SystemBatteryState outputBuffer,
        uint outputBufferLength);
}

internal static class PowerSavingsSelfTest
{
    public static object Run()
    {
        const string sourceKey = "test-energy-source";
        var start = DateTimeOffset.UtcNow;
        var estimator = new PowerSavingsEstimator(0);
        PowerSavingsSnapshot? snapshot = null;

        for (var index = 0; index < PowerSavingsEstimator.RequiredBaselineSamples; index++)
        {
            snapshot = estimator.Update(
                LaptopPowerMode.HighPower,
                Reading(start.AddSeconds(index * 2), 100, sourceKey));
        }

        Assert(snapshot?.HighBaselineAvailable == true, "High baseline was not learned.");
        Assert(snapshot!.EstimatedHighUsageWatts is >= 99.9 and <= 100.1, "High estimate was incorrect.");

        for (var index = 0; index < 10; index++)
        {
            snapshot = estimator.Update(
                LaptopPowerMode.LowPower,
                Reading(start.AddSeconds(20 + index * 2), 60, sourceKey));
        }

        var expectedSavedWh = 40d * 20d / 3600d;
        Assert(snapshot?.EstimatedLowUsageWatts is >= 59.9 and <= 60.1, "Low estimate was incorrect.");
        Assert(snapshot?.EstimatedSavingWatts is >= 39.9 and <= 40.1, "Saving rate was incorrect.");
        Assert(snapshot?.EcoSessionEnergySavedWh is { } saved
               && Math.Abs(saved - expectedSavedWh) < 0.000001,
            "Cumulative saved energy was incorrect.");

        var persisted = estimator.GetPersistentState();
        var restored = new PowerSavingsEstimator(persisted.TotalEnergySavedWh, persisted.Baselines);
        var restoredSnapshot = restored.Update(
            LaptopPowerMode.LowPower,
            Reading(start.AddSeconds(42), 60, sourceKey));
        var expectedRestoredTotal = expectedSavedWh + 40d * 2d / 3600d;
        Assert(restoredSnapshot.HighBaselineAvailable, "Persisted High baseline was not restored.");
        Assert(Math.Abs(restoredSnapshot.TotalEnergySavedWh - expectedRestoredTotal) < 0.000001,
            "Restored estimator did not continue cumulative energy.");

        var beforeMismatch = snapshot!.TotalEnergySavedWh;
        snapshot = estimator.Update(
            LaptopPowerMode.LowPower,
            Reading(start.AddSeconds(42), 10, "different-source"));
        Assert(Math.Abs(snapshot.TotalEnergySavedWh - beforeMismatch) < 0.000001,
            "Energy accumulated across incomparable telemetry sources.");

        return new
        {
            Success = true,
            LearnedHighWatts = 100,
            LearnedLowWatts = 60,
            ExpectedSavingWatts = 40,
            ExpectedSavedWh = expectedSavedWh,
            ActualSavedWh = beforeMismatch
        };
    }

    private static PowerTelemetryReading Reading(DateTimeOffset capturedAt, double watts, string sourceKey)
    {
        const double intervalSeconds = 2;
        return new PowerTelemetryReading(
            capturedAt,
            watts,
            watts * intervalSeconds / 3600d,
            intervalSeconds,
            sourceKey,
            "Test source",
            false);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
