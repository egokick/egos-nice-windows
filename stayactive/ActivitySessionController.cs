namespace StayActive;

internal interface IIdleMonitor
{
    TimeSpan GetInactiveDuration();
}

internal interface IMonitorBrightnessService
{
    BrightnessSnapshot? CaptureCurrentBrightness();

    bool TryDimToLowest();

    void Restore(BrightnessSnapshot snapshot);
}

internal sealed record MonitorBrightnessLevel(string InstanceName, byte Brightness);

internal sealed record BrightnessSnapshot(IReadOnlyList<MonitorBrightnessLevel> Levels);

internal sealed class SystemIdleMonitor : IIdleMonitor
{
    public TimeSpan GetInactiveDuration()
    {
        return IdleMonitor.GetInactiveDuration();
    }
}

internal sealed class SystemMonitorBrightnessService : IMonitorBrightnessService
{
    public BrightnessSnapshot? CaptureCurrentBrightness()
    {
        return BrightnessService.CaptureCurrentBrightness();
    }

    public bool TryDimToLowest()
    {
        return BrightnessService.TrySetLowestBrightness();
    }

    public void Restore(BrightnessSnapshot snapshot)
    {
        BrightnessService.Restore(snapshot);
    }
}

internal sealed class ActivitySessionController
{
    private readonly IIdleMonitor _idleMonitor;
    private readonly IMonitorBrightnessService _brightnessService;
    private bool _idleSessionActive;
    private bool _brightnessApplied;
    private BrightnessSnapshot? _brightnessSnapshot;

    public ActivitySessionController(IIdleMonitor idleMonitor, IMonitorBrightnessService brightnessService)
    {
        _idleMonitor = idleMonitor;
        _brightnessService = brightnessService;
    }

    public bool IdleSessionActive => _idleSessionActive;

    public bool IsEffectivelyActive(AppSettings settings)
    {
        return settings.IsActive || _idleSessionActive;
    }

    public bool Refresh(AppSettings settings)
    {
        var previousEffective = IsEffectivelyActive(settings);
        var previousIdleSessionActive = _idleSessionActive;

        if (settings.IsActive || !settings.EnableAfterInactivityEnabled)
        {
            _idleSessionActive = false;
        }
        else if (!_idleSessionActive
                 && _idleMonitor.GetInactiveDuration() >= TimeSpan.FromSeconds(settings.EnableAfterInactivitySeconds))
        {
            _idleSessionActive = true;
        }

        var currentEffective = IsEffectivelyActive(settings);
        UpdateBrightness(settings, currentEffective);
        return previousIdleSessionActive != _idleSessionActive || previousEffective != currentEffective;
    }

    public bool HandleRealUserActivity(AppSettings settings)
    {
        if (!_idleSessionActive)
        {
            return false;
        }

        _idleSessionActive = false;
        UpdateBrightness(settings, IsEffectivelyActive(settings));
        return true;
    }

    public void Shutdown()
    {
        RestoreBrightnessIfNeeded();
        _idleSessionActive = false;
    }

    private void UpdateBrightness(AppSettings settings, bool currentEffective)
    {
        if (currentEffective && settings.DimScreenWhenActiveEnabled)
        {
            if (_brightnessApplied)
            {
                return;
            }

            _brightnessSnapshot = _brightnessService.CaptureCurrentBrightness();
            if (_brightnessService.TryDimToLowest())
            {
                _brightnessApplied = true;
                return;
            }

            _brightnessSnapshot = null;
            return;
        }

        RestoreBrightnessIfNeeded();
    }

    private void RestoreBrightnessIfNeeded()
    {
        if (!_brightnessApplied)
        {
            _brightnessSnapshot = null;
            return;
        }

        if (_brightnessSnapshot is not null)
        {
            _brightnessService.Restore(_brightnessSnapshot);
        }

        _brightnessSnapshot = null;
        _brightnessApplied = false;
    }
}
