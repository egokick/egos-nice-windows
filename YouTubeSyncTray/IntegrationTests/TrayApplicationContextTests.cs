using YouTubeSyncTray;

namespace YouTubeSyncTray.IntegrationTests;

public sealed class TrayApplicationContextTests
{
    [Fact]
    public void ShouldPauseAutomaticSync_ReturnsTrue_WhenNothingIsMissingAfterSync()
    {
        var summary = CreateSummary(targetCount: 5, downloadedCount: 2, alreadyPresentCount: 3, missingAfterSyncCount: 0);

        Assert.True(TrayApplicationContext.ShouldPauseAutomaticSync(summary));
    }

    [Fact]
    public void ShouldPauseAutomaticSync_ReturnsFalse_WhenVideosAreStillMissing()
    {
        var summary = CreateSummary(targetCount: 5, downloadedCount: 2, alreadyPresentCount: 1, missingAfterSyncCount: 2);

        Assert.False(TrayApplicationContext.ShouldPauseAutomaticSync(summary));
    }

    [Fact]
    public void BuildSyncStatus_AppendsPauseNotice_WhenAutomaticSyncIsPaused()
    {
        var summary = CreateSummary(targetCount: 4, downloadedCount: 0, alreadyPresentCount: 4, missingAfterSyncCount: 0);

        var status = TrayApplicationContext.BuildSyncStatus(summary, automaticSyncPaused: true);

        Assert.Contains("Automatic sync is paused until you click Sync Now again.", status);
    }

    private static SyncService.SyncSummary CreateSummary(
        int targetCount,
        int downloadedCount,
        int alreadyPresentCount,
        int missingAfterSyncCount,
        int archiveRepairedCount = 0,
        int? watchLaterTotalCount = null,
        string? nonFatalIssue = null)
    {
        return new SyncService.SyncSummary(
            RequestedCount: targetCount,
            TargetCount: targetCount,
            DownloadedCount: downloadedCount,
            AlreadyPresentCount: alreadyPresentCount,
            ArchiveRepairedCount: archiveRepairedCount,
            MissingAfterSyncCount: missingAfterSyncCount,
            WatchLaterTotalCount: watchLaterTotalCount,
            NonFatalIssue: nonFatalIssue);
    }
}
