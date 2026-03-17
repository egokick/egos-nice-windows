using YouTubeSyncTray;

namespace YouTubeSyncTray.IntegrationTests;

public sealed class SyncServiceTests
{
    [Fact]
    public void BuildHighestQualityFormatArguments_PrefersBestAvailableStreams_WithoutResolutionCap()
    {
        var arguments = SyncService.BuildHighestQualityFormatArguments();

        Assert.Contains("--format-sort-reset", arguments, StringComparison.Ordinal);
        Assert.Contains("bv*+ba/b", arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("1080", arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("height<=", arguments, StringComparison.Ordinal);
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
}
