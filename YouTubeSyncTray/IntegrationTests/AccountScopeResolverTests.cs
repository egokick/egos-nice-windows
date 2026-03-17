using YouTubeSyncTray;

namespace YouTubeSyncTray.IntegrationTests;

public sealed class AccountScopeResolverTests
{
    [Fact]
    public void BuildFolderName_UsesDifferentFoldersForDifferentYouTubeAccounts()
    {
        var settings = new AppSettings
        {
            BrowserCookies = BrowserCookieSource.Chrome,
            BrowserProfile = "Default",
            SelectedBrowserAccountKey = "Chrome|Default|egokick@gmail.com",
            SelectedYouTubeAccountKey = "yt|page|101659640671648366543"
        };

        var browserAccount = new BrowserAccountOption(
            "Chrome|Default|egokick@gmail.com",
            BrowserCookieSource.Chrome,
            "Google Chrome",
            "Default",
            0,
            "gaia-1",
            "egokick@gmail.com",
            "egokick",
            string.Empty);
        var firstYouTubeAccount = new YouTubeAccountOption(
            "yt|page|101659640671648366543",
            "egokick",
            "@egokick",
            "33 subscribers",
            string.Empty,
            "101659640671648366543",
            string.Empty,
            string.Empty,
            0,
            false);
        var secondYouTubeAccount = new YouTubeAccountOption(
            "yt|handle|@egokickdoge3550",
            "egokick doge",
            "@egokickdoge3550",
            "No subscribers",
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            0,
            true);

        var firstFolder = AccountScopeResolver.BuildFolderName(browserAccount, firstYouTubeAccount, settings);
        var secondFolder = AccountScopeResolver.BuildFolderName(browserAccount, secondYouTubeAccount, settings);

        Assert.NotEqual(firstFolder, secondFolder);
        Assert.Contains("egokick", firstFolder, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("egokick doge", secondFolder, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Normalize_MigratesLegacySelectedAccountKeyIntoSplitSelection()
    {
        var browserSettings = new AppSettings
        {
            SelectedAccountKey = "Chrome|Default|egokick@gmail.com"
        };

        browserSettings.Normalize();

        Assert.Equal("Chrome|Default|egokick@gmail.com", browserSettings.SelectedBrowserAccountKey);
        Assert.Null(browserSettings.SelectedYouTubeAccountKey);

        var youtubeSettings = new AppSettings
        {
            SelectedAccountKey = "yt|page|101659640671648366543"
        };

        youtubeSettings.Normalize();

        Assert.Null(youtubeSettings.SelectedBrowserAccountKey);
        Assert.Equal("yt|page|101659640671648366543", youtubeSettings.SelectedYouTubeAccountKey);
    }

    [Fact]
    public void ResolveExistingFolderName_ReusesExistingHashMatchedFolderWhenOfflineLabelDiffers()
    {
        var root = Path.Combine(Path.GetTempPath(), "YouTubeSyncTray.Tests", Guid.NewGuid().ToString("N"));
        var downloadsRoot = Path.Combine(root, "downloads");
        var thumbnailCacheRoot = Path.Combine(root, "thumb-cache");
        var archivesRoot = Path.Combine(root, "archives");
        Directory.CreateDirectory(downloadsRoot);
        Directory.CreateDirectory(thumbnailCacheRoot);
        Directory.CreateDirectory(archivesRoot);

        try
        {
            var settings = new AppSettings
            {
                BrowserCookies = BrowserCookieSource.Chrome,
                BrowserProfile = "Default",
                SelectedBrowserAccountKey = "Chrome|Default|egokick@gmail.com",
                SelectedYouTubeAccountKey = "yt|page|101659640671648366543"
            };

            var browserAccount = new BrowserAccountOption(
                "Chrome|Default|egokick@gmail.com",
                BrowserCookieSource.Chrome,
                "Google Chrome",
                "Default",
                0,
                "gaia-1",
                "egokick@gmail.com",
                "egokick",
                string.Empty);
            var onlineYouTubeAccount = new YouTubeAccountOption(
                "yt|page|101659640671648366543",
                "egokick",
                "@egokick",
                "33 subscribers",
                string.Empty,
                "101659640671648366543",
                string.Empty,
                string.Empty,
                0,
                true);

            var existingFolderName = AccountScopeResolver.BuildFolderName(browserAccount, onlineYouTubeAccount, settings);
            Directory.CreateDirectory(Path.Combine(downloadsRoot, existingFolderName));

            var offlinePreferredFolderName = AccountScopeResolver.BuildFolderName(
                settings.SelectedBrowserAccountKey,
                "egokick@gmail.com",
                settings.SelectedYouTubeAccountKey,
                "Channel 101659640671648366543",
                settings);

            Assert.NotEqual(existingFolderName, offlinePreferredFolderName);

            var resolvedFolderName = AccountScopeResolver.ResolveExistingFolderName(
                downloadsRoot,
                thumbnailCacheRoot,
                archivesRoot,
                settings.SelectedYouTubeAccountKey!,
                offlinePreferredFolderName);

            Assert.Equal(existingFolderName, resolvedFolderName);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
