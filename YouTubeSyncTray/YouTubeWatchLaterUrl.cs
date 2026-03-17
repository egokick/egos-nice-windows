namespace YouTubeSyncTray;

internal static class YouTubeWatchLaterUrl
{
    private const string BaseUrl = "https://www.youtube.com/playlist?list=WL";

    public static string Build(int authUserIndex, string? pageId = null)
    {
        var normalizedIndex = Math.Max(authUserIndex, 0);
        var builder = $"{BaseUrl}&authuser={normalizedIndex}";
        if (!string.IsNullOrWhiteSpace(pageId))
        {
            builder += $"&pageid={Uri.EscapeDataString(pageId)}";
        }

        return builder;
    }
}
