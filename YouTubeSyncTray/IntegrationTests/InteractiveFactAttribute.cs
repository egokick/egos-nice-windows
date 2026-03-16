namespace YouTubeSyncTray.IntegrationTests;

internal sealed class InteractiveFactAttribute : FactAttribute
{
    private const string EnableEnv = "YOUTUBE_SYNC_RUN_INTERACTIVE";

    public InteractiveFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(EnableEnv), "1", StringComparison.Ordinal))
        {
            Skip = $"Set {EnableEnv}=1 to run this live browser-backed integration test.";
        }
    }
}
