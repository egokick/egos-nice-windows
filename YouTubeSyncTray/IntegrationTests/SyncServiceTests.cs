using YouTubeSyncTray;

namespace YouTubeSyncTray.IntegrationTests;

public sealed class SyncServiceTests
{
    [Fact]
    public void BuildHighestQualityFormatArguments_PrefersBestAvailableStreams_WithoutResolutionCap()
    {
        var arguments = SyncService.BuildHighestQualityFormatArguments();

        Assert.Contains("--format-sort-reset", arguments, StringComparison.Ordinal);
        Assert.Contains("bv*[ext=mp4][protocol^=http]+ba[ext=m4a][protocol^=http]", arguments, StringComparison.Ordinal);
        Assert.Contains("bv*[ext=mp4]+ba[ext=m4a]", arguments, StringComparison.Ordinal);
        Assert.Contains("bv*[ext=webm]+ba[ext=webm]", arguments, StringComparison.Ordinal);
        Assert.Contains("bv*+ba/b", arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("1080", arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("height<=", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSubtitleDownloadArguments_UsesSidecarCaptionsWithoutEmbeddingThem()
    {
        var arguments = SyncService.BuildSubtitleDownloadArguments();

        Assert.Contains("--write-subs", arguments, StringComparison.Ordinal);
        Assert.Contains("--write-auto-subs", arguments, StringComparison.Ordinal);
        Assert.Contains("--convert-subs srt", arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("--embed-subs", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildYouTubeExtractorArguments_IncludesMissingPotFormats()
    {
        var arguments = SyncService.BuildYouTubeExtractorArguments();

        Assert.Contains("youtube:lang=en", arguments, StringComparison.Ordinal);
        Assert.Contains("formats=missing_pot", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildRetryArguments_CapsRetriesAtThree()
    {
        var arguments = SyncService.BuildRetryArguments();

        Assert.Contains("--retries 3", arguments, StringComparison.Ordinal);
        Assert.Contains("--fragment-retries 3", arguments, StringComparison.Ordinal);
        Assert.Contains("--file-access-retries 3", arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("10", arguments, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, new[] { 1 })]
    [InlineData(10, new[] { 10 })]
    [InlineData(25, new[] { 10, 25 })]
    [InlineData(100, new[] { 10, 50, 100 })]
    [InlineData(150, new[] { 10, 50, 100, 150 })]
    [InlineData(5000, new[] { 10, 50, 100, 250, 500, 1000, 2000, 5000 })]
    public void BuildWatchLaterProbeRanges_ExpandsFromRecentItems(int downloadCount, int[] expected)
    {
        Assert.Equal(expected, SyncService.BuildWatchLaterProbeRanges(downloadCount));
    }

    [Theory]
    [InlineData(10, 10, false)]
    [InlineData(50, 10, true)]
    [InlineData(50, 0, true)]
    public void ShouldStopWatchLaterProbe_OnlyStopsForEmptyOrShortRanges(
        int rangeEnd,
        int returnedCount,
        bool expected)
    {
        Assert.Equal(expected, SyncService.ShouldStopWatchLaterProbe(rangeEnd, returnedCount));
    }

    [Theory]
    [InlineData("ERROR: Sign in to confirm you're not a bot")]
    [InlineData("ERROR: Please sign in to continue")]
    [InlineData("ERROR: This video may be inappropriate for some users. Sign in to confirm your age")]
    public void LooksLikeAuthFailureOutput_MatchesModernYoutubeAuthFailures(string output)
    {
        Assert.True(SyncService.LooksLikeAuthFailureOutput(output));
    }

    [Fact]
    public void LooksLikeAuthFailureOutput_DoesNotMatchOrdinaryProgress()
    {
        Assert.False(SyncService.LooksLikeAuthFailureOutput("[download]  52.4% of 123.45MiB at 3.41MiB/s ETA 00:16"));
    }

    [Fact]
    public void LooksLikeAuthFailureOutput_DoesNotMatchFragmentForbiddenFailures()
    {
        const string output = """
            [info] DxiQVRX_r-k: Downloading 1 format(s): 96
            [hlsnative] Downloading m3u8 manifest
            [download] Got error: HTTP Error 403: Forbidden. Retrying fragment 2 (1/3)...
            """;

        Assert.False(SyncService.LooksLikeAuthFailureOutput(output));
    }

    [Fact]
    public void LooksLikeAuthFailureOutput_DoesNotMatchDrmOrUnavailableFormatFailures()
    {
        const string output = """
            [download] Got error: HTTP Error 403: Forbidden. Retrying fragment 45 (8/10)...
            WARNING: This video is drm protected and only images are available for download. use --list-formats to see them
            ERROR: [youtube] MhXL3Hk9fWk: Requested format is not available. Use --list-formats for a list of available formats
            ERROR: Did not get any data blocks
            """;

        Assert.False(SyncService.LooksLikeAuthFailureOutput(output));
    }

    [Theory]
    [InlineData("ERROR: [youtube] abc123: Private video. Sign in if you've been granted access to this video")]
    [InlineData("ERROR: [youtube] def456: requested content is not available")]
    public void LooksLikeAuthFailureOutput_DoesNotMatchUnavailableItemFailures(string output)
    {
        Assert.False(SyncService.LooksLikeAuthFailureOutput(output));
    }

    [Fact]
    public void BuildMostRecentFirstWatchLaterIds_PreservesYtDlpWatchLaterOrder()
    {
        var orderedIds = SyncService.BuildMostRecentFirstWatchLaterIds(
            ["newer02", "newer01", "older02", "older01"]);

        Assert.Collection(
            orderedIds,
            first => Assert.Equal("newer02", first),
            second => Assert.Equal("newer01", second),
            third => Assert.Equal("older02", third),
            fourth => Assert.Equal("older01", fourth));
    }

    [Fact]
    public void DescribeNonFatalSyncIssueOutput_PicksConcreteYtDlpErrorDetail()
    {
        const string output = """
            [download] Got error: HTTP Error 403: Forbidden. Retrying fragment 45 (8/10)...
            WARNING: This video is drm protected and only images are available for download. use --list-formats to see them
            ERROR: [youtube] MhXL3Hk9fWk: Requested format is not available. Use --list-formats for a list of available formats
            ERROR: Did not get any data blocks
            """;

        var issue = SyncService.DescribeNonFatalSyncIssueOutput(output);

        Assert.Equal("Some videos were skipped because YouTube only exposed DRM-protected or unavailable formats.", issue.Summary);
        Assert.Equal(
            "ERROR: [youtube] MhXL3Hk9fWk: Requested format is not available. Use --list-formats for a list of available formats",
            issue.Detail);
    }

    [Fact]
    public void DescribeNonFatalSyncIssueOutput_ExplainsMediaUrlForbidden()
    {
        const string output = """
            [info] DxiQVRX_r-k: Downloading 1 format(s): 18
            ERROR: unable to download video data: HTTP Error 403: Forbidden
            """;

        var issue = SyncService.DescribeNonFatalSyncIssueOutput(output);

        Assert.Equal(
            "Some videos were skipped because YouTube rejected yt-dlp's media download URLs even though the playlist metadata was accessible.",
            issue.Summary);
        Assert.Equal("ERROR: unable to download video data: HTTP Error 403: Forbidden", issue.Detail);
    }
}
