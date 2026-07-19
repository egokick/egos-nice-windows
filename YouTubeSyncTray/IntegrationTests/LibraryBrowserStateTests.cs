using YouTubeSyncTray;

namespace YouTubeSyncTray.IntegrationTests;

public sealed class LibraryBrowserStateTests
{
    [Fact]
    public void CalculateSyncScopeCounts_UsesConfiguredRange_AndDownloadedIntersection()
    {
        var counts = LibraryBrowserState.CalculateSyncScopeCounts(
            downloadedVideoIds: ["a", "c", "x"],
            syncTargetVideoIds: ["a", "b", "c"]);

        Assert.Equal(2, counts.DownloadedCount);
        Assert.Equal(3, counts.TargetCount);
        Assert.Equal(1, counts.FailedCount);
    }

    [Fact]
    public void CalculateSyncScopeCounts_ReturnsNullCounts_WhenNoCurrentWatchLaterOrderIsKnown()
    {
        var counts = LibraryBrowserState.CalculateSyncScopeCounts(
            downloadedVideoIds: ["a", "b"],
            syncTargetVideoIds: []);

        Assert.Null(counts.DownloadedCount);
        Assert.Null(counts.TargetCount);
        Assert.Null(counts.FailedCount);
    }

    [Fact]
    public void SetSyncTargets_ExposesUndownloadedVideoTitles_AndAdvancesTheLibraryVersion()
    {
        var state = new LibraryBrowserState([]);
        var initialVersion = state.GetSnapshot(new AppSettings()).LibraryVersion;

        state.SetSyncTargets(
        [
            new SyncService.WatchLaterVideo("pending01", "First pending video"),
            new SyncService.WatchLaterVideo("pending02", "Second pending video")
        ]);

        Assert.Equal(
            [
                new LibraryBrowserState.PendingVideo("pending01", "First pending video"),
                new LibraryBrowserState.PendingVideo("pending02", "Second pending video")
            ],
            state.GetPendingSyncVideosSnapshot());
        Assert.True(state.GetSnapshot(new AppSettings()).LibraryVersion > initialVersion);
    }
}
