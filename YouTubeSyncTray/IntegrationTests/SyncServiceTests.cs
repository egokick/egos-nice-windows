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

    [Fact]
    public void ParseWatchLaterVideoJson_ReadsIdAndTitleWithoutDelimiterAmbiguity()
    {
        var video = SyncService.ParseWatchLaterVideoJson(
            """["abc123XYZ","A title with\ttabs | pipes and unicode ✓"]""");

        Assert.Equal("abc123XYZ", video?.VideoId);
        Assert.Equal("A title with\ttabs | pipes and unicode ✓", video?.Title);
    }

    [Theory]
    [InlineData("WARNING: retrying")]
    [InlineData("{not-json}")]
    [InlineData("{\"title\":\"Missing ID\"}")]
    public void ParseWatchLaterVideoJson_IgnoresNonVideoOutput(string output)
    {
        Assert.Null(SyncService.ParseWatchLaterVideoJson(output));
    }

    [Fact]
    public void ParseWatchLaterSnapshotJson_ProvidesTotalOrderTitlesAndDeduplicatedTargets()
    {
        const string output = """
            WARNING: a harmless diagnostic before the JSON
            {"playlist_count":4,"entries":[{"id":"newest01","title":"Newest"},{"id":"middle01","title":"How to Sign in"},{"id":"middle01","title":"Duplicate"},{"id":"oldest01","title":"Oldest"}]}
            """;

        var snapshot = SyncService.ParseWatchLaterSnapshotJson(output);

        Assert.Equal(4, snapshot?.TotalCount);
        Assert.Equal(
            [
                new SyncService.WatchLaterVideo("newest01", "Newest"),
                new SyncService.WatchLaterVideo("middle01", "How to Sign in"),
                new SyncService.WatchLaterVideo("oldest01", "Oldest")
            ],
            snapshot?.Videos);
    }

    [Fact]
    public void ParseWatchLaterSnapshotJson_FallsBackToEntryCountWhenPlaylistCountIsMissing()
    {
        var snapshot = SyncService.ParseWatchLaterSnapshotJson(
            "{\"entries\":[{\"id\":\"video001\",\"title\":\"One\"},{\"id\":\"video002\",\"title\":\"Two\"}]}");

        Assert.Equal(2, snapshot?.TotalCount);
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

    [Theory]
    [InlineData("{\"title\":\"A documentary about browser cookies\"}")]
    [InlineData("[download] Destination: Cookies and Cream [abc123].mp4")]
    [InlineData("[info] Successfully loaded 42 cookies")]
    [InlineData("[info] Cookie Database Explained")]
    public void LooksLikeAuthFailureOutput_DoesNotMatchOrdinaryCookieMentions(string output)
    {
        Assert.False(SyncService.LooksLikeAuthFailureOutput(output));
    }

    [Theory]
    [InlineData("[\"abc123XYZ\",\"How to Sign in to a New Laptop\"]")]
    [InlineData("[download] Destination: Please Sign In [abc123XYZ].mp4")]
    [InlineData("[info] A documentary: confirm you're not a bot")]
    [InlineData("{\"title\":\"This playlist does not exist: an investigation\"}")]
    public void LooksLikeAuthFailureOutput_DoesNotMatchAuthPhrasesInTitlesOrProgress(string output)
    {
        Assert.False(SyncService.LooksLikeAuthFailureOutput(output));
    }

    [Theory]
    [InlineData("ERROR: Failed to load cookies from C:\\temp\\cookies.txt")]
    [InlineData("ERROR: unable to read cookies")]
    [InlineData("ERROR: cookies have expired")]
    [InlineData("ERROR: could not copy chrome cookie database")]
    public void LooksLikeAuthFailureOutput_MatchesSpecificCookieFailures(string output)
    {
        Assert.True(SyncService.LooksLikeAuthFailureOutput(output));
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

    [Fact]
    public void BackupFileBundle_TracksPartialMovesSoTheyCanBeRestored()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"YouTubeSyncTrayTests-{Guid.NewGuid():N}");
        var downloadsRoot = Path.Combine(testRoot, "downloads");
        var backupRoot = Path.Combine(testRoot, "backup");
        var firstPath = Path.Combine(downloadsRoot, "video123.mp4");
        var lockedPath = Path.Combine(downloadsRoot, "video123.info.json");
        Directory.CreateDirectory(downloadsRoot);
        File.WriteAllText(firstPath, "original video");
        File.WriteAllText(lockedPath, "original metadata");
        var backups = new List<SyncService.RedownloadBackup>();

        try
        {
            using (var lockedFile = new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.ThrowsAny<IOException>(() =>
                    SyncService.BackupFileBundle(
                        "video123",
                        [firstPath, lockedPath],
                        backupRoot,
                        backups));
            }

            Assert.False(File.Exists(firstPath));
            Assert.True(File.Exists(lockedPath));
            Assert.Single(backups);
            Assert.Equal(2, backups[0].Files.Count);

            SyncService.RestoreBackups(backups);

            Assert.Equal("original video", File.ReadAllText(firstPath));
            Assert.Equal("original metadata", File.ReadAllText(lockedPath));
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }
}
