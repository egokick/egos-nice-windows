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

    [Fact]
    public void BuildRedownloadStatus_ReportsFailuresAndRestoreNotice()
    {
        var summary = new SyncService.RedownloadSummary(
            RequestedCount: 3,
            RedownloadedCount: 2,
            FailedCount: 1,
            NonFatalIssue: null);

        var status = TrayApplicationContext.BuildRedownloadStatus(summary);

        Assert.Equal("Redownloaded 2 video(s) at best quality; 1 failed and the previous download(s) were restored when available.", status);
    }

    [Fact]
    public void BuildRedownloadStatus_AppendsNonFatalIssue()
    {
        var summary = new SyncService.RedownloadSummary(
            RequestedCount: 1,
            RedownloadedCount: 1,
            FailedCount: 0,
            NonFatalIssue: "Some videos were skipped because YouTube only exposed DRM-protected or unavailable formats.");

        var status = TrayApplicationContext.BuildRedownloadStatus(summary);

        Assert.Equal("Redownloaded 1 video(s) at best quality; Some videos were skipped because YouTube only exposed DRM-protected or unavailable formats.", status);
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
