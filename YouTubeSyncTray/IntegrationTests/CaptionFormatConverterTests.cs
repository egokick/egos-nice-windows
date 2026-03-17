namespace YouTubeSyncTray.IntegrationTests;

public sealed class CaptionFormatConverterTests
{
    [Fact]
    public void ConvertSrtToWebVtt_RewritesCueTimingsAndSkipsCueNumbers()
    {
        var srt = """
            1
            00:00:01,250 --> 00:00:03,000
            Hello, world.

            2
            00:00:04,500 --> 00:00:05,750
            Another line.
            """;

        var webVtt = CaptionFormatConverter.ConvertSrtToWebVtt(srt);

        Assert.StartsWith("WEBVTT\n\n", webVtt, StringComparison.Ordinal);
        Assert.Contains("00:00:01.250 --> 00:00:03.000", webVtt, StringComparison.Ordinal);
        Assert.Contains("00:00:04.500 --> 00:00:05.750", webVtt, StringComparison.Ordinal);
        Assert.Contains("Hello, world.", webVtt, StringComparison.Ordinal);
        Assert.DoesNotContain("\n1\n", webVtt, StringComparison.Ordinal);
        Assert.DoesNotContain("\n2\n", webVtt, StringComparison.Ordinal);
    }
}
