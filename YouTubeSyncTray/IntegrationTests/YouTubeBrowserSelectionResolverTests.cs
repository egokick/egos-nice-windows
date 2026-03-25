using YouTubeSyncTray;

namespace YouTubeSyncTray.IntegrationTests;

public sealed class YouTubeBrowserSelectionResolverTests
{
    [Fact]
    public void ResolvePreferredBrowserAccount_PrefersDatasyncLinkedBrowserIdentity()
    {
        var currentBrowserAccount = new BrowserAccountOption(
            "Edge|Default|edge-account",
            BrowserCookieSource.Edge,
            "Microsoft Edge",
            "Default",
            0,
            "edge-account",
            "edge@example.com",
            "Edge User",
            string.Empty);
        var browserAccounts = new[]
        {
            currentBrowserAccount,
            new BrowserAccountOption(
                "Chrome|Default|108399420979782935353",
                BrowserCookieSource.Chrome,
                "Google Chrome",
                "Default",
                0,
                "108399420979782935353",
                "egokick@gmail.com",
                "egokick doge",
                string.Empty)
        };
        var youTubeAccounts = new[]
        {
            new YouTubeAccountOption(
                "yt|handle|@egokickdoge3550",
                "egokick doge",
                "@egokickdoge3550",
                "No subscribers",
                string.Empty,
                string.Empty,
                string.Empty,
                "108399420979782935353||",
                0,
                true)
        };

        var preferredBrowserAccount = YouTubeBrowserSelectionResolver.ResolvePreferredBrowserAccount(
            "yt|handle|@egokickdoge3550",
            youTubeAccounts,
            browserAccounts,
            knownScopes: [],
            currentBrowserAccount);

        Assert.True(preferredBrowserAccount.HasValue);
        Assert.Equal("Chrome|Default|108399420979782935353", preferredBrowserAccount.Value.AccountKey);
    }

    [Fact]
    public void ResolvePreferredBrowserAccount_FallsBackToMostRecentKnownScope()
    {
        var browserAccounts = new[]
        {
            new BrowserAccountOption(
                "Edge|Default|edge-account",
                BrowserCookieSource.Edge,
                "Microsoft Edge",
                "Default",
                0,
                "edge-account",
                "edge@example.com",
                "Edge User",
                string.Empty),
            new BrowserAccountOption(
                "Chrome|Default|chrome-account",
                BrowserCookieSource.Chrome,
                "Google Chrome",
                "Default",
                0,
                "chrome-account",
                "chrome@example.com",
                "Chrome User",
                string.Empty)
        };
        var knownScopes = new[]
        {
            new KnownLibraryScopeRecord
            {
                BrowserAccountKey = "Chrome|Default|chrome-account",
                YouTubeAccountKey = "yt|page|123",
                IsAvailableOnDisk = true,
                LastSeenAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
                LastSuccessfulSyncAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5)
            }
        };

        var preferredBrowserAccount = YouTubeBrowserSelectionResolver.ResolvePreferredBrowserAccount(
            "yt|page|123",
            youTubeAccounts: [],
            browserAccounts,
            knownScopes,
            currentBrowserAccount: browserAccounts[0]);

        Assert.True(preferredBrowserAccount.HasValue);
        Assert.Equal("Chrome|Default|chrome-account", preferredBrowserAccount.Value.AccountKey);
    }
}
