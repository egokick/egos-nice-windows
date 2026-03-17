using System.Text.Json;
using YouTubeSyncTray;

namespace YouTubeSyncTray.IntegrationTests;

public sealed class YouTubeAccountDiscoveryServiceTests
{
    [Fact]
    public void ParseAccountsListResponse_ReadsYouTubeChannelIdentities()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "actions": [
                {
                  "getMultiPageMenuAction": {
                    "menu": {
                      "multiPageMenuRenderer": {
                        "sections": [
                          {
                            "accountSectionListRenderer": {
                              "contents": [
                                {
                                  "accountItemSectionRenderer": {
                                    "contents": [
                                      {
                                        "accountItem": {
                                          "accountName": { "simpleText": "egokick doge" },
                                          "channelHandle": { "simpleText": "@egokickdoge3550" },
                                          "accountByline": { "simpleText": "No subscribers" },
                                          "accountPhoto": {
                                            "thumbnails": [
                                              {
                                                "url": "https://yt3.ggpht.com/doge=s32-c-k-c0x00ffffff-no-rj",
                                                "width": 32,
                                                "height": 32
                                              },
                                              {
                                                "url": "https://yt3.ggpht.com/doge=s88-c-k-c0x00ffffff-no-rj",
                                                "width": 88,
                                                "height": 88
                                              }
                                            ]
                                          },
                                          "isSelected": true,
                                          "serviceEndpoint": {
                                            "selectActiveIdentityEndpoint": {
                                              "supportedTokens": [
                                                {
                                                  "accountSigninToken": {
                                                    "signinUrl": "/signin?action_handle_signin=true&authuser=0&next=%2F"
                                                  }
                                                }
                                              ]
                                            }
                                          }
                                        }
                                      },
                                      {
                                        "accountItem": {
                                          "accountName": { "simpleText": "egokick" },
                                          "channelHandle": { "simpleText": "@egokick" },
                                          "accountByline": { "simpleText": "33 subscribers" },
                                          "accountPhoto": {
                                            "thumbnails": [
                                              {
                                                "url": "https://yt3.ggpht.com/egokick=s32-c-k-c0x00ffffff-no-rj",
                                                "width": 32,
                                                "height": 32
                                              },
                                              {
                                                "url": "https://yt3.ggpht.com/egokick=s88-c-k-c0x00ffffff-no-rj",
                                                "width": 88,
                                                "height": 88
                                              }
                                            ]
                                          },
                                          "isSelected": false,
                                          "serviceEndpoint": {
                                            "selectActiveIdentityEndpoint": {
                                              "supportedTokens": [
                                                {
                                                  "pageIdToken": {
                                                    "pageId": "101659640671648366543"
                                                  }
                                                },
                                                {
                                                  "accountSigninToken": {
                                                    "signinUrl": "/signin?action_handle_signin=true&authuser=0&pageid=101659640671648366543&next=%2F"
                                                  }
                                                },
                                                {
                                                  "datasyncIdToken": {
                                                    "datasyncIdToken": "101659640671648366543||108399420979782935353"
                                                  }
                                                }
                                              ]
                                            }
                                          }
                                        }
                                      }
                                    ]
                                  }
                                }
                              ]
                            }
                          }
                        ]
                      }
                    }
                  }
                }
              ]
            }
            """);

        var accounts = YouTubeAccountDiscoveryService.ParseAccountsListResponse(document.RootElement, fallbackAuthUserIndex: 3);

        Assert.Equal(2, accounts.Count);
        Assert.Equal("egokick doge", accounts[0].DisplayName);
        Assert.True(accounts[0].IsSelected);
        Assert.Equal(0, accounts[0].AuthUserIndex);
        Assert.Equal("https://yt3.ggpht.com/doge=s88-c-k-c0x00ffffff-no-rj", accounts[0].AvatarUrl);
        Assert.Equal("egokick", accounts[1].DisplayName);
        Assert.Equal("@egokick", accounts[1].Handle);
        Assert.Equal("33 subscribers", accounts[1].Byline);
        Assert.Equal("https://yt3.ggpht.com/egokick=s88-c-k-c0x00ffffff-no-rj", accounts[1].AvatarUrl);
        Assert.Equal("101659640671648366543", accounts[1].PageId);
        Assert.Equal("101659640671648366543||108399420979782935353", accounts[1].DatasyncId);
        Assert.Equal("yt|page|101659640671648366543", accounts[1].AccountKey);
        Assert.Contains("@egokick", accounts[1].Label, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/signin?action_handle_signin=true&authuser=0&next=%2F", 2, 0)]
    [InlineData("/signin?action_handle_signin=true&pageid=123&next=%2F", 2, 2)]
    [InlineData("", 4, 4)]
    public void ParseAuthUserIndex_UsesSigninUrlWhenPresent(string signInUrl, int fallback, int expected)
    {
        Assert.Equal(expected, YouTubeAccountDiscoveryService.ParseAuthUserIndex(signInUrl, fallback));
    }

    [Fact]
    public void DiscoverAccounts_CachedOnlyReturnsPersistedAccounts()
    {
        var root = Path.Combine(Path.GetTempPath(), "YouTubeSyncTray.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var paths = new YoutubeSyncPaths
            {
                RootPath = root,
                BrowserProfilesPath = Path.Combine(root, "browser-profiles"),
                DownloadsPath = Path.Combine(root, "downloads"),
                YtDlpPath = Path.Combine(root, "yt-dlp.exe"),
                CookiesPath = Path.Combine(root, "youtube-cookies.txt"),
                CookiesMetadataPath = Path.Combine(root, "youtube-cookies.metadata.json"),
                ArchivePath = Path.Combine(root, "watch-later.archive.txt"),
                TempPath = Path.Combine(root, "temp"),
                LogsPath = Path.Combine(root, "logs"),
                ThumbnailCachePath = Path.Combine(root, "thumb-cache")
            };

            Directory.CreateDirectory(paths.BrowserProfilesPath);
            Directory.CreateDirectory(paths.DownloadsPath);
            Directory.CreateDirectory(paths.TempPath);
            Directory.CreateDirectory(paths.LogsPath);
            Directory.CreateDirectory(paths.ThumbnailCachePath);

            File.WriteAllText(paths.CookiesPath, "cookie-data");
            new CookieExportMetadataStore(paths).Save(BrowserCookieSource.Chrome, "Default");

            var cookiesInfo = new FileInfo(paths.CookiesPath);
            var persistedAccounts = new[]
            {
                new YouTubeAccountOption(
                    "yt|handle|@egokickdoge3550",
                    "egokick doge",
                    "@egokickdoge3550",
                    "No subscribers",
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    0,
                    true)
            };
            var persistedCache = new YouTubeAccountDiscoveryService.PersistedCache(
                BrowserCookieSource.Chrome,
                "Default",
                paths.CookiesPath,
                cookiesInfo.Length,
                cookiesInfo.LastWriteTimeUtc.Ticks,
                0,
                DateTimeOffset.UtcNow,
                persistedAccounts);
            File.WriteAllText(
                Path.Combine(root, "youtube-account-discovery-cache.json"),
                JsonSerializer.Serialize(persistedCache));

            var service = new YouTubeAccountDiscoveryService(paths);
            var settings = new AppSettings
            {
                BrowserCookies = BrowserCookieSource.Chrome,
                BrowserProfile = "Default",
                SelectedYouTubeAccountKey = "yt|handle|@egokickdoge3550"
            };

            var accounts = service.DiscoverAccounts(settings, browserAuthUserIndex: 0, allowNetwork: false);
            var selected = service.ResolveSelectedAccount(settings, browserAuthUserIndex: 0, allowNetwork: false);

            var account = Assert.Single(accounts);
            Assert.Equal("yt|handle|@egokickdoge3550", account.AccountKey);
            Assert.Equal("egokick doge", account.DisplayName);
            Assert.Equal(account, selected);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ResolveSelectedAccount_FallsBackToExplicitSelectionWhenDiscoveryIsUnavailable()
    {
        var root = Path.Combine(Path.GetTempPath(), "YouTubeSyncTray.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var paths = new YoutubeSyncPaths
            {
                RootPath = root,
                BrowserProfilesPath = Path.Combine(root, "browser-profiles"),
                DownloadsPath = Path.Combine(root, "downloads"),
                YtDlpPath = Path.Combine(root, "yt-dlp.exe"),
                CookiesPath = Path.Combine(root, "youtube-cookies.txt"),
                CookiesMetadataPath = Path.Combine(root, "youtube-cookies.metadata.json"),
                ArchivePath = Path.Combine(root, "watch-later.archive.txt"),
                TempPath = Path.Combine(root, "temp"),
                LogsPath = Path.Combine(root, "logs"),
                ThumbnailCachePath = Path.Combine(root, "thumb-cache")
            };

            var service = new YouTubeAccountDiscoveryService(paths);
            var settings = new AppSettings
            {
                BrowserCookies = BrowserCookieSource.Chrome,
                BrowserProfile = "Default",
                SelectedYouTubeAccountKey = "yt|page|101659640671648366543"
            };

            var selected = service.ResolveSelectedAccount(settings, browserAuthUserIndex: 2, allowNetwork: false);

            Assert.True(selected.HasValue);
            Assert.Equal("yt|page|101659640671648366543", selected.Value.AccountKey);
            Assert.Equal("101659640671648366543", selected.Value.PageId);
            Assert.Equal(2, selected.Value.AuthUserIndex);
            Assert.Contains("Saved account", selected.Value.Byline, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DiscoverAccounts_CachedOnlyUsesPersistedAccountsWhenCookieExportChanges()
    {
        var root = Path.Combine(Path.GetTempPath(), "YouTubeSyncTray.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var paths = new YoutubeSyncPaths
            {
                RootPath = root,
                BrowserProfilesPath = Path.Combine(root, "browser-profiles"),
                DownloadsPath = Path.Combine(root, "downloads"),
                YtDlpPath = Path.Combine(root, "yt-dlp.exe"),
                CookiesPath = Path.Combine(root, "youtube-cookies.txt"),
                CookiesMetadataPath = Path.Combine(root, "youtube-cookies.metadata.json"),
                ArchivePath = Path.Combine(root, "watch-later.archive.txt"),
                TempPath = Path.Combine(root, "temp"),
                LogsPath = Path.Combine(root, "logs"),
                ThumbnailCachePath = Path.Combine(root, "thumb-cache")
            };

            Directory.CreateDirectory(paths.BrowserProfilesPath);
            Directory.CreateDirectory(paths.DownloadsPath);
            Directory.CreateDirectory(paths.TempPath);
            Directory.CreateDirectory(paths.LogsPath);
            Directory.CreateDirectory(paths.ThumbnailCachePath);

            File.WriteAllText(paths.CookiesPath, "old-cookie-data");
            new CookieExportMetadataStore(paths).Save(BrowserCookieSource.Chrome, "Default");

            var originalCookiesInfo = new FileInfo(paths.CookiesPath);
            var persistedAccounts = new[]
            {
                new YouTubeAccountOption(
                    "yt|page|101659640671648366543",
                    "egokick",
                    "@egokick",
                    "33 subscribers",
                    string.Empty,
                    "101659640671648366543",
                    string.Empty,
                    string.Empty,
                    0,
                    false),
                new YouTubeAccountOption(
                    "yt|handle|@egokickdoge3550",
                    "egokick doge",
                    "@egokickdoge3550",
                    "No subscribers",
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    0,
                    true)
            };
            var persistedCache = new YouTubeAccountDiscoveryService.PersistedCache(
                BrowserCookieSource.Chrome,
                "Default",
                paths.CookiesPath,
                originalCookiesInfo.Length,
                originalCookiesInfo.LastWriteTimeUtc.Ticks,
                0,
                DateTimeOffset.UtcNow,
                persistedAccounts);
            File.WriteAllText(
                Path.Combine(root, "youtube-account-discovery-cache.json"),
                JsonSerializer.Serialize(persistedCache));

            File.WriteAllText(paths.CookiesPath, "new-cookie-data-that-does-not-match-the-old-cache-metadata");

            var service = new YouTubeAccountDiscoveryService(paths);
            var settings = new AppSettings
            {
                BrowserCookies = BrowserCookieSource.Chrome,
                BrowserProfile = "Default",
                SelectedYouTubeAccountKey = "yt|handle|@egokickdoge3550"
            };

            var accounts = service.DiscoverAccounts(settings, browserAuthUserIndex: 0, allowNetwork: false);

            Assert.Equal(2, accounts.Count);
            Assert.Contains(accounts, account => account.AccountKey == "yt|page|101659640671648366543");
            Assert.Contains(accounts, account => account.AccountKey == "yt|handle|@egokickdoge3550");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
