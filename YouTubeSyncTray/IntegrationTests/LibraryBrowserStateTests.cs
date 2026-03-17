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
}
