using StayActive;

namespace stayactive.IntegrationTests;

public sealed class ActivitySessionControllerTests
{
    [Fact]
    public void Refresh_StartsIdleSession_AndDimsOnce_WhenThresholdIsReached()
    {
        var idleMonitor = new FakeIdleMonitor(TimeSpan.FromMinutes(8));
        var brightnessService = new FakeBrightnessService(
            CreateSnapshot("DISPLAY-A", 70),
            CreateSnapshot("DISPLAY-B", 45));
        var controller = new ActivitySessionController(idleMonitor, brightnessService);
        var settings = CreateSettings();

        var changed = controller.Refresh(settings);

        Assert.True(changed);
        Assert.True(controller.IdleSessionActive);
        Assert.True(controller.IsEffectivelyActive(settings));
        Assert.Equal(1, brightnessService.CaptureCalls);
        Assert.Equal(1, brightnessService.DimCalls);
        Assert.Equal(0, brightnessService.RestoreCalls);
    }

    [Fact]
    public void HandleRealUserActivity_EndsIdleSession_RestoresBrightness_AndKeepsWaitingModeEnabled()
    {
        var idleMonitor = new FakeIdleMonitor(TimeSpan.FromMinutes(8));
        var firstSnapshot = CreateSnapshot("DISPLAY-A", 70);
        var brightnessService = new FakeBrightnessService(firstSnapshot);
        var controller = new ActivitySessionController(idleMonitor, brightnessService);
        var settings = CreateSettings();
        controller.Refresh(settings);

        var changed = controller.HandleRealUserActivity(settings);

        Assert.True(changed);
        Assert.False(controller.IdleSessionActive);
        Assert.False(controller.IsEffectivelyActive(settings));
        Assert.True(settings.EnableAfterInactivityEnabled);
        Assert.Equal(1, brightnessService.RestoreCalls);
        Assert.Same(firstSnapshot, brightnessService.RestoredSnapshots.Single());
    }

    [Fact]
    public void Refresh_DoesNotRedimWhileIdleSessionRemainsActive()
    {
        var idleMonitor = new FakeIdleMonitor(TimeSpan.FromMinutes(8));
        var brightnessService = new FakeBrightnessService(CreateSnapshot("DISPLAY-A", 70));
        var controller = new ActivitySessionController(idleMonitor, brightnessService);
        var settings = CreateSettings();

        controller.Refresh(settings);
        var changed = controller.Refresh(settings);

        Assert.False(changed);
        Assert.Equal(1, brightnessService.CaptureCalls);
        Assert.Equal(1, brightnessService.DimCalls);
        Assert.Equal(0, brightnessService.RestoreCalls);
    }

    [Fact]
    public void Refresh_CapturesFreshBrightnessSnapshot_WhenIdleSessionActivatesAgain()
    {
        var idleMonitor = new FakeIdleMonitor(TimeSpan.FromMinutes(8));
        var firstSnapshot = CreateSnapshot("DISPLAY-A", 70);
        var secondSnapshot = CreateSnapshot("DISPLAY-A", 55);
        var brightnessService = new FakeBrightnessService(firstSnapshot, secondSnapshot);
        var controller = new ActivitySessionController(idleMonitor, brightnessService);
        var settings = CreateSettings();

        controller.Refresh(settings);
        controller.HandleRealUserActivity(settings);
        idleMonitor.InactiveDuration = TimeSpan.FromMinutes(8);
        var changed = controller.Refresh(settings);

        Assert.True(changed);
        Assert.True(controller.IdleSessionActive);
        Assert.Equal(2, brightnessService.CaptureCalls);
        Assert.Equal(2, brightnessService.DimCalls);
        Assert.Equal(1, brightnessService.RestoreCalls);
        Assert.Same(firstSnapshot, brightnessService.RestoredSnapshots.Single());
    }

    [Fact]
    public void Refresh_ClearsIdleSessionWithoutRestoring_WhenManualActiveTakesOver()
    {
        var idleMonitor = new FakeIdleMonitor(TimeSpan.FromMinutes(8));
        var brightnessService = new FakeBrightnessService(CreateSnapshot("DISPLAY-A", 70));
        var controller = new ActivitySessionController(idleMonitor, brightnessService);
        var settings = CreateSettings();
        controller.Refresh(settings);
        settings.IsActive = true;

        var changed = controller.Refresh(settings);

        Assert.True(changed);
        Assert.False(controller.IdleSessionActive);
        Assert.True(controller.IsEffectivelyActive(settings));
        Assert.Equal(1, brightnessService.DimCalls);
        Assert.Equal(0, brightnessService.RestoreCalls);
    }

    private static AppSettings CreateSettings()
    {
        return new AppSettings
        {
            EnableAfterInactivityEnabled = true,
            EnableAfterInactivitySeconds = 300,
            DimScreenWhenActiveEnabled = true
        };
    }

    private static BrightnessSnapshot CreateSnapshot(string instanceName, byte brightness)
    {
        return new BrightnessSnapshot(new[] { new MonitorBrightnessLevel(instanceName, brightness) });
    }

    private sealed class FakeIdleMonitor : IIdleMonitor
    {
        public FakeIdleMonitor(TimeSpan inactiveDuration)
        {
            InactiveDuration = inactiveDuration;
        }

        public TimeSpan InactiveDuration { get; set; }

        public TimeSpan GetInactiveDuration()
        {
            return InactiveDuration;
        }
    }

    private sealed class FakeBrightnessService : IMonitorBrightnessService
    {
        private readonly Queue<BrightnessSnapshot> _snapshots;

        public FakeBrightnessService(params BrightnessSnapshot[] snapshots)
        {
            _snapshots = new Queue<BrightnessSnapshot>(snapshots);
        }

        public int CaptureCalls { get; private set; }

        public int DimCalls { get; private set; }

        public int RestoreCalls { get; private set; }

        public List<BrightnessSnapshot> RestoredSnapshots { get; } = new();

        public BrightnessSnapshot? CaptureCurrentBrightness()
        {
            CaptureCalls++;
            return _snapshots.Count == 0 ? null : _snapshots.Dequeue();
        }

        public bool TryDimToLowest()
        {
            DimCalls++;
            return true;
        }

        public void Restore(BrightnessSnapshot snapshot)
        {
            RestoreCalls++;
            RestoredSnapshots.Add(snapshot);
        }
    }
}
