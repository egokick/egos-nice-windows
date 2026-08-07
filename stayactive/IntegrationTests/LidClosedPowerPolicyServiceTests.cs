using StayActive;

namespace stayactive.IntegrationTests;

public sealed class LidClosedPowerPolicyServiceTests
{
    [Fact]
    public void Enable_SetsLidAndBothTimeoutsToNeverForAcAndBattery()
    {
        var runner = new RecordingPowerPolicyRunner();
        var service = new LidClosedPowerPolicyService(runner);

        service.SetKeepAwakeWhenLidClosed(true);

        Assert.Contains("/setdcvalueindex SCHEME_CURRENT 4f971e89-eebd-4455-a8de-9e59040e7347 5ca83367-6e45-459f-a27b-476b1d01c936 0", runner.Commands);
        Assert.Contains("/setacvalueindex SCHEME_CURRENT 4f971e89-eebd-4455-a8de-9e59040e7347 5ca83367-6e45-459f-a27b-476b1d01c936 0", runner.Commands);
        Assert.Contains("/setdcvalueindex SCHEME_CURRENT SUB_SLEEP STANDBYIDLE 0", runner.Commands);
        Assert.Contains("/setacvalueindex SCHEME_CURRENT SUB_SLEEP STANDBYIDLE 0", runner.Commands);
        Assert.Contains("/setdcvalueindex SCHEME_CURRENT SUB_SLEEP HIBERNATEIDLE 0", runner.Commands);
        Assert.Contains("/setacvalueindex SCHEME_CURRENT SUB_SLEEP HIBERNATEIDLE 0", runner.Commands);
        Assert.Equal("/setactive SCHEME_CURRENT", runner.Commands[^1]);
    }

    [Fact]
    public void Disable_RestoresSleepOnLidCloseAndFiniteTimeouts()
    {
        var runner = new RecordingPowerPolicyRunner();
        var service = new LidClosedPowerPolicyService(runner);

        service.SetKeepAwakeWhenLidClosed(false);

        Assert.Contains("/setdcvalueindex SCHEME_CURRENT 4f971e89-eebd-4455-a8de-9e59040e7347 5ca83367-6e45-459f-a27b-476b1d01c936 1", runner.Commands);
        Assert.Contains("/setacvalueindex SCHEME_CURRENT 4f971e89-eebd-4455-a8de-9e59040e7347 5ca83367-6e45-459f-a27b-476b1d01c936 1", runner.Commands);
        Assert.Contains("/setdcvalueindex SCHEME_CURRENT SUB_SLEEP STANDBYIDLE 900", runner.Commands);
        Assert.Contains("/setacvalueindex SCHEME_CURRENT SUB_SLEEP STANDBYIDLE 1800", runner.Commands);
        Assert.Contains("/setdcvalueindex SCHEME_CURRENT SUB_SLEEP HIBERNATEIDLE 3600", runner.Commands);
        Assert.Contains("/setacvalueindex SCHEME_CURRENT SUB_SLEEP HIBERNATEIDLE 10800", runner.Commands);
    }

    [Fact]
    public void IsConfigured_RequiresBothAcAndBatterySleepAndHibernateTimeoutsToBeNever()
    {
        var runner = new RecordingPowerPolicyRunner
        {
            QueryOutput = "Current AC Power Setting Index: 0x00000000\nCurrent DC Power Setting Index: 0x00000000"
        };
        var service = new LidClosedPowerPolicyService(runner);

        Assert.True(service.IsKeepAwakeWhenLidClosedConfigured());

        runner.QueryOutput = "Current AC Power Setting Index: 0x00000000\nCurrent DC Power Setting Index: 0x000000b4";
        Assert.False(service.IsKeepAwakeWhenLidClosedConfigured());
    }

    private sealed class RecordingPowerPolicyRunner : IPowerPolicyProcessRunner
    {
        public List<string> Commands { get; } = [];

        public string QueryOutput { get; set; } = string.Empty;

        public string RunAndCapture(string arguments, TimeSpan timeout) => QueryOutput;

        public void RunAndWait(string arguments, TimeSpan timeout) => Commands.Add(arguments);
    }
}
