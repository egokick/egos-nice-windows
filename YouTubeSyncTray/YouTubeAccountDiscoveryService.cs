using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace YouTubeSyncTray;

internal sealed class YouTubeAccountDiscoveryService
{
    private static readonly TimeSpan SuccessCacheLifetime = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan FailureCacheLifetime = TimeSpan.FromSeconds(15);
    private static readonly Regex ApiKeyRegex = new("\"INNERTUBE_API_KEY\":\"([^\"]+)\"", RegexOptions.Compiled);
    private static readonly Regex ClientNameRegex = new("\"INNERTUBE_CLIENT_NAME\":\"([^\"]+)\"", RegexOptions.Compiled);
    private static readonly Regex ClientVersionRegex = new("\"INNERTUBE_CLIENT_VERSION\":\"([^\"]+)\"", RegexOptions.Compiled);
    private static readonly Regex SessionIndexRegex = new("\"SESSION_INDEX\":\"([^\"]+)\"", RegexOptions.Compiled);
    private static readonly Regex AuthorizedUserRegex = new("\"authorizedUserIndex\":(\\d+)", RegexOptions.Compiled);

    private readonly YoutubeSyncPaths _paths;
    private readonly CookieExportMetadataStore _metadataStore;
    private readonly object _cacheGate = new();

    private CacheEntry? _cache;

    public YouTubeAccountDiscoveryService(YoutubeSyncPaths paths)
    {
        _paths = paths;
        _metadataStore = new CookieExportMetadataStore(paths);
    }

    public IReadOnlyList<YouTubeAccountOption> DiscoverAccounts(
        AppSettings settings,
        int? browserAuthUserIndex = null,
        bool allowNetwork = true)
    {
        settings.Normalize();
        if (!TryGetCacheKey(settings, browserAuthUserIndex, out var cacheKey))
        {
            return [];
        }

        lock (_cacheGate)
        {
            if (_cache is not null
                && _cache.Value.Key == cacheKey
                && _cache.Value.ExpiresAtUtc > DateTimeOffset.UtcNow)
            {
                return _cache.Value.Accounts;
            }
        }

        var persistedAccounts = LoadPersistedAccounts(cacheKey);
        if (!allowNetwork)
        {
            if (persistedAccounts is not null)
            {
                UpdateCache(cacheKey, persistedAccounts, SuccessCacheLifetime);
                return persistedAccounts;
            }

            return [];
        }

        try
        {
            var accounts = FetchAccounts(cacheKey);
            UpdateCache(cacheKey, accounts, SuccessCacheLifetime);
            SavePersistedAccounts(cacheKey, accounts);
            return accounts;
        }
        catch (Exception ex)
        {
            TrayLog.Write(_paths, $"YouTube account discovery failed: {ex.Message}");
            if (persistedAccounts is not null)
            {
                UpdateCache(cacheKey, persistedAccounts, SuccessCacheLifetime);
                return persistedAccounts;
            }

            UpdateCache(cacheKey, [], FailureCacheLifetime);
            return [];
        }
    }

    public YouTubeAccountOption? ResolveSelectedAccount(
        AppSettings settings,
        int? browserAuthUserIndex = null,
        bool allowNetwork = true)
    {
        var accounts = DiscoverAccounts(settings, browserAuthUserIndex, allowNetwork);
        var explicitAccountKey = settings.SelectedYouTubeAccountKey;
        if (string.IsNullOrWhiteSpace(explicitAccountKey)
            && !string.IsNullOrWhiteSpace(settings.SelectedAccountKey)
            && settings.SelectedAccountKey.StartsWith("yt|", StringComparison.Ordinal))
        {
            explicitAccountKey = settings.SelectedAccountKey;
        }

        if (accounts.Count == 0)
        {
            return BuildFallbackSelectedAccount(explicitAccountKey, browserAuthUserIndex ?? 0);
        }

        if (!string.IsNullOrWhiteSpace(explicitAccountKey))
        {
            var explicitSelection = accounts.FirstOrDefault(account =>
                string.Equals(account.AccountKey, explicitAccountKey, StringComparison.Ordinal));
            if (!string.IsNullOrWhiteSpace(explicitSelection.AccountKey))
            {
                return explicitSelection;
            }

            return BuildFallbackSelectedAccount(explicitAccountKey, browserAuthUserIndex ?? 0);
        }

        var currentSelection = accounts.FirstOrDefault(account => account.IsSelected);
        if (!string.IsNullOrWhiteSpace(currentSelection.AccountKey))
        {
            return currentSelection;
        }

        return accounts[0];
    }

    internal static YouTubeAccountOption? BuildFallbackSelectedAccount(string? accountKey, int fallbackAuthUserIndex)
    {
        if (string.IsNullOrWhiteSpace(accountKey))
        {
            return null;
        }

        var trimmedKey = accountKey.Trim();
        var parts = trimmedKey.Split('|', StringSplitOptions.TrimEntries);
        if (parts.Length < 3 || !string.Equals(parts[0], "yt", StringComparison.Ordinal))
        {
            return null;
        }

        return parts[1] switch
        {
            "page" => new YouTubeAccountOption(
                trimmedKey,
                $"Channel {parts[2]}",
                string.Empty,
                "Saved account",
                string.Empty,
                parts[2],
                string.Empty,
                string.Empty,
                Math.Max(fallbackAuthUserIndex, 0),
                true),
            "handle" => new YouTubeAccountOption(
                trimmedKey,
                parts[2].TrimStart('@'),
                parts[2],
                "Saved account",
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                Math.Max(fallbackAuthUserIndex, 0),
                true),
            "name" => new YouTubeAccountOption(
                trimmedKey,
                parts[2],
                string.Empty,
                "Saved account",
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                Math.Max(fallbackAuthUserIndex, 0),
                true),
            "authuser" when int.TryParse(parts[2], out var parsedAuthUserIndex) => new YouTubeAccountOption(
                trimmedKey,
                $"YouTube {parts[2]}",
                string.Empty,
                "Saved account",
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                Math.Max(parsedAuthUserIndex, 0),
                true),
            _ => null
        };
    }

    internal static IReadOnlyList<YouTubeAccountOption> ParseAccountsListResponse(JsonElement root, int fallbackAuthUserIndex)
    {
        if (!root.TryGetProperty("actions", out var actionsElement)
            || actionsElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        foreach (var action in actionsElement.EnumerateArray())
        {
            if (!action.TryGetProperty("getMultiPageMenuAction", out var menuAction)
                || menuAction.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!menuAction.TryGetProperty("menu", out var menuElement)
                || menuElement.ValueKind != JsonValueKind.Object
                || !menuElement.TryGetProperty("multiPageMenuRenderer", out var menuRenderer)
                || menuRenderer.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!menuRenderer.TryGetProperty("sections", out var sectionsElement)
                || sectionsElement.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var accounts = new List<YouTubeAccountOption>();
            foreach (var section in sectionsElement.EnumerateArray())
            {
                if (!section.TryGetProperty("accountSectionListRenderer", out var accountSectionList)
                    || accountSectionList.ValueKind != JsonValueKind.Object
                    || !accountSectionList.TryGetProperty("contents", out var contentsElement)
                    || contentsElement.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var content in contentsElement.EnumerateArray())
                {
                    if (!content.TryGetProperty("accountItemSectionRenderer", out var itemSection)
                        || itemSection.ValueKind != JsonValueKind.Object
                        || !itemSection.TryGetProperty("contents", out var itemContents)
                        || itemContents.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (var item in itemContents.EnumerateArray())
                    {
                        if (!item.TryGetProperty("accountItem", out var accountItem)
                            || accountItem.ValueKind != JsonValueKind.Object)
                        {
                            continue;
                        }

                        var displayName = GetText(accountItem, "accountName");
                        var handle = GetText(accountItem, "channelHandle");
                        var byline = GetText(accountItem, "accountByline");
                        var avatarUrl = GetThumbnailUrl(accountItem, "accountPhoto");
                        var isSelected = accountItem.TryGetProperty("isSelected", out var selectedElement)
                            && selectedElement.ValueKind == JsonValueKind.True;
                        var pageId = string.Empty;
                        var signInUrl = string.Empty;
                        var datasyncId = string.Empty;
                        var authUserIndex = fallbackAuthUserIndex;

                        if (accountItem.TryGetProperty("serviceEndpoint", out var serviceEndpoint)
                            && serviceEndpoint.ValueKind == JsonValueKind.Object
                            && serviceEndpoint.TryGetProperty("selectActiveIdentityEndpoint", out var selectActiveIdentity)
                            && selectActiveIdentity.ValueKind == JsonValueKind.Object
                            && selectActiveIdentity.TryGetProperty("supportedTokens", out var supportedTokens)
                            && supportedTokens.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var token in supportedTokens.EnumerateArray())
                            {
                                if (token.TryGetProperty("pageIdToken", out var pageIdToken)
                                    && pageIdToken.ValueKind == JsonValueKind.Object)
                                {
                                    pageId = GetString(pageIdToken, "pageId");
                                }

                                if (token.TryGetProperty("accountSigninToken", out var signInToken)
                                    && signInToken.ValueKind == JsonValueKind.Object)
                                {
                                    signInUrl = GetString(signInToken, "signinUrl");
                                    authUserIndex = ParseAuthUserIndex(signInUrl, fallbackAuthUserIndex);
                                }

                                if (token.TryGetProperty("datasyncIdToken", out var datasyncToken)
                                    && datasyncToken.ValueKind == JsonValueKind.Object)
                                {
                                    datasyncId = GetString(datasyncToken, "datasyncIdToken");
                                }
                            }
                        }

                        if (string.IsNullOrWhiteSpace(displayName)
                            && string.IsNullOrWhiteSpace(handle)
                            && string.IsNullOrWhiteSpace(pageId))
                        {
                            continue;
                        }

                        accounts.Add(new YouTubeAccountOption(
                            AccountKey: BuildAccountKey(pageId, handle, displayName, authUserIndex),
                            DisplayName: displayName,
                            Handle: handle,
                            Byline: byline,
                            AvatarUrl: avatarUrl,
                            PageId: pageId,
                            SignInUrl: signInUrl,
                            DatasyncId: datasyncId,
                            AuthUserIndex: authUserIndex,
                            IsSelected: isSelected));
                    }
                }
            }

            return accounts;
        }

        return [];
    }

    internal static int ParseAuthUserIndex(string? signInUrl, int fallback)
    {
        if (string.IsNullOrWhiteSpace(signInUrl))
        {
            return fallback;
        }

        if (!Uri.TryCreate("https://www.youtube.com" + signInUrl.Trim(), UriKind.Absolute, out var uri))
        {
            return fallback;
        }

        var query = uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var pair in query)
        {
            var parts = pair.Split('=', 2);
            if (parts.Length != 2
                || !string.Equals(parts[0], "authuser", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var decoded = Uri.UnescapeDataString(parts[1]);
            if (int.TryParse(decoded, out var parsed))
            {
                return parsed;
            }
        }

        return fallback;
    }

    private IReadOnlyList<YouTubeAccountOption> FetchAccounts(CacheKey cacheKey)
    {
        using var handler = new HttpClientHandler
        {
            CookieContainer = CreateCookieContainer(cacheKey.CookiesPath),
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        };
        using var http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

        var homeHtml = GetHomeHtml(http, cacheKey.PreferredAuthUserIndex);
        var config = ParseHomeConfig(homeHtml, cacheKey.PreferredAuthUserIndex);
        var accountsResponse = PostJson(
            http,
            new Uri($"https://www.youtube.com/youtubei/v1/account/accounts_list?key={Uri.EscapeDataString(config.ApiKey)}&prettyPrint=false"),
            config,
            cacheKey.CookiesPath,
            JsonSerializer.SerializeToUtf8Bytes(new
            {
                context = new
                {
                    client = new
                    {
                        clientName = config.ClientName,
                        clientVersion = config.ClientVersion,
                        hl = "en",
                        gl = "US"
                    }
                }
            }));

        using var document = JsonDocument.Parse(accountsResponse);
        return ParseAccountsListResponse(document.RootElement, config.AuthUserIndex);
    }

    private static string GetHomeHtml(HttpClient http, int? preferredAuthUserIndex)
    {
        var uri = preferredAuthUserIndex.HasValue && preferredAuthUserIndex.Value >= 0
            ? new Uri($"https://www.youtube.com/?authuser={preferredAuthUserIndex.Value}")
            : new Uri("https://www.youtube.com/");
        using var request = CreateBrowserRequest(HttpMethod.Get, uri);
        using var response = http.Send(request);
        response.EnsureSuccessStatusCode();
        return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
    }

    private static HomeConfig ParseHomeConfig(string homeHtml, int? preferredAuthUserIndex)
    {
        var apiKey = MatchValue(ApiKeyRegex, homeHtml);
        var clientName = MatchValue(ClientNameRegex, homeHtml);
        var clientVersion = MatchValue(ClientVersionRegex, homeHtml);
        var authUserString = MatchValue(SessionIndexRegex, homeHtml, isRequired: false)
            ?? MatchValue(AuthorizedUserRegex, homeHtml, isRequired: false)
            ?? preferredAuthUserIndex?.ToString()
            ?? "0";
        _ = int.TryParse(authUserString, out var authUserIndex);

        if (string.IsNullOrWhiteSpace(apiKey)
            || string.IsNullOrWhiteSpace(clientName)
            || string.IsNullOrWhiteSpace(clientVersion))
        {
            throw new InvalidOperationException("Could not parse the YouTube homepage configuration.");
        }

        return new HomeConfig(apiKey, clientName, clientVersion, authUserIndex);
    }

    private static string? MatchValue(Regex regex, string value, bool isRequired = true)
    {
        var match = regex.Match(value);
        if (match.Success)
        {
            return match.Groups[1].Value;
        }

        if (!isRequired)
        {
            return null;
        }

        throw new InvalidOperationException($"Could not find '{regex}' in the YouTube homepage response.");
    }

    private static byte[] PostJson(HttpClient http, Uri uri, HomeConfig config, string cookiesPath, byte[] body)
    {
        using var request = CreateBrowserRequest(HttpMethod.Post, uri);
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        var sapisid = GetSapisid(cookiesPath);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var hashBytes = SHA1.HashData(Encoding.UTF8.GetBytes($"{timestamp} {sapisid} https://www.youtube.com"));
        var hash = Convert.ToHexStringLower(hashBytes);
        request.Headers.TryAddWithoutValidation("Authorization", $"SAPISIDHASH {timestamp}_{hash}");
        request.Headers.TryAddWithoutValidation("Origin", "https://www.youtube.com");
        request.Headers.TryAddWithoutValidation("X-Origin", "https://www.youtube.com");
        request.Headers.TryAddWithoutValidation("X-Goog-AuthUser", config.AuthUserIndex.ToString());
        request.Headers.TryAddWithoutValidation("X-Youtube-Client-Name", config.ClientName);
        request.Headers.TryAddWithoutValidation("X-Youtube-Client-Version", config.ClientVersion);
        request.Headers.Referrer = new Uri("https://www.youtube.com/");

        using var response = http.Send(request);
        response.EnsureSuccessStatusCode();
        return response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
    }

    private static HttpRequestMessage CreateBrowserRequest(HttpMethod method, Uri uri)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/134.0.0.0 Safari/537.36");
        request.Headers.Accept.ParseAdd("*/*");
        request.Headers.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        return request;
    }

    private static string GetSapisid(string cookiesPath)
    {
        foreach (var cookie in ReadCookies(cookiesPath))
        {
            if ((cookie.Name == "SAPISID" || cookie.Name == "__Secure-3PAPISID")
                && cookie.Domain.Contains("youtube.com", StringComparison.OrdinalIgnoreCase))
            {
                return cookie.Value;
            }
        }

        throw new InvalidOperationException("The saved YouTube cookie export does not contain a usable SAPISID cookie.");
    }

    private static CookieContainer CreateCookieContainer(string cookiesPath)
    {
        var container = new CookieContainer();
        foreach (var cookie in ReadCookies(cookiesPath))
        {
            try
            {
                container.Add(cookie);
            }
            catch
            {
                // Ignore malformed or duplicate cookies in the export.
            }
        }

        return container;
    }

    private static IEnumerable<Cookie> ReadCookies(string cookiesPath)
    {
        foreach (var line in File.ReadLines(cookiesPath))
        {
            if (string.IsNullOrWhiteSpace(line)
                || line.StartsWith('#'))
            {
                continue;
            }

            var parts = line.Split('\t');
            if (parts.Length < 7)
            {
                continue;
            }

            var domain = parts[0].Trim();
            var path = parts[2].Trim();
            var isSecure = string.Equals(parts[3], "TRUE", StringComparison.OrdinalIgnoreCase);
            var name = parts[5].Trim();
            var value = parts[6];
            if (string.IsNullOrWhiteSpace(domain) || string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            if (domain.StartsWith('.'))
            {
                domain = domain[1..];
            }

            yield return new Cookie(name, value, string.IsNullOrWhiteSpace(path) ? "/" : path, domain)
            {
                Secure = isSecure
            };
        }
    }

    private bool TryGetCacheKey(AppSettings settings, int? browserAuthUserIndex, out CacheKey cacheKey)
    {
        cacheKey = default;
        try
        {
            var info = new FileInfo(_paths.CookiesPath);
            if (!info.Exists
                || info.Length == 0
                || !_metadataStore.Matches(settings.BrowserCookies, settings.BrowserProfile))
            {
                return false;
            }

            cacheKey = new CacheKey(
                settings.BrowserCookies,
                settings.BrowserProfile,
                _paths.CookiesPath,
                info.Length,
                info.LastWriteTimeUtc.Ticks,
                browserAuthUserIndex);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void UpdateCache(CacheKey cacheKey, IReadOnlyList<YouTubeAccountOption> accounts, TimeSpan lifetime)
    {
        lock (_cacheGate)
        {
            _cache = new CacheEntry(cacheKey, DateTimeOffset.UtcNow.Add(lifetime), accounts);
        }
    }

    private IReadOnlyList<YouTubeAccountOption>? LoadPersistedAccounts(CacheKey cacheKey)
    {
        try
        {
            if (!File.Exists(GetPersistentCachePath()))
            {
                return null;
            }

            var persisted = JsonSerializer.Deserialize<PersistedCache>(File.ReadAllText(GetPersistentCachePath()));
            if (persisted is null)
            {
                return null;
            }

            if (!MatchesPersistedCache(persisted, cacheKey)
                && !MatchesPersistedCacheFallback(persisted, cacheKey))
            {
                return null;
            }

            return persisted.Accounts ?? [];
        }
        catch
        {
            return null;
        }
    }

    private void SavePersistedAccounts(CacheKey cacheKey, IReadOnlyList<YouTubeAccountOption> accounts)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(GetPersistentCachePath())!);
            var persisted = new PersistedCache(
                cacheKey.Browser,
                cacheKey.Profile,
                cacheKey.CookiesPath,
                cacheKey.Length,
                cacheKey.LastWriteTimeUtcTicks,
                cacheKey.PreferredAuthUserIndex,
                DateTimeOffset.UtcNow,
                accounts.ToArray());
            File.WriteAllText(GetPersistentCachePath(), JsonSerializer.Serialize(persisted));
        }
        catch
        {
            // Persistent cache writes are best-effort. In-memory caching still applies.
        }
    }

    private string GetPersistentCachePath() =>
        Path.Combine(_paths.RootPath, "youtube-account-discovery-cache.json");

    private static bool MatchesPersistedCache(PersistedCache persistedCache, CacheKey cacheKey)
    {
        return persistedCache.Browser == cacheKey.Browser
            && string.Equals(persistedCache.Profile, cacheKey.Profile, StringComparison.OrdinalIgnoreCase)
            && string.Equals(persistedCache.CookiesPath, cacheKey.CookiesPath, StringComparison.OrdinalIgnoreCase)
            && persistedCache.CookiesLength == cacheKey.Length
            && persistedCache.CookiesLastWriteTimeUtcTicks == cacheKey.LastWriteTimeUtcTicks
            && persistedCache.PreferredAuthUserIndex == cacheKey.PreferredAuthUserIndex;
    }

    private static bool MatchesPersistedCacheFallback(PersistedCache persistedCache, CacheKey cacheKey)
    {
        return persistedCache.Browser == cacheKey.Browser
            && string.Equals(persistedCache.Profile, cacheKey.Profile, StringComparison.OrdinalIgnoreCase)
            && string.Equals(persistedCache.CookiesPath, cacheKey.CookiesPath, StringComparison.OrdinalIgnoreCase)
            && persistedCache.PreferredAuthUserIndex == cacheKey.PreferredAuthUserIndex;
    }

    private static string GetText(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        var simpleText = GetString(property, "simpleText");
        if (!string.IsNullOrWhiteSpace(simpleText))
        {
            return simpleText;
        }

        if (!property.TryGetProperty("runs", out var runsElement)
            || runsElement.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var parts = runsElement.EnumerateArray()
            .Select(run => GetString(run, "text"))
            .Where(text => !string.IsNullOrWhiteSpace(text));
        return string.Join(string.Empty, parts);
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return string.Empty;
        }

        return property.GetString()?.Trim() ?? string.Empty;
    }

    private static string GetThumbnailUrl(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        if (!property.TryGetProperty("thumbnails", out var thumbnailsElement)
            || thumbnailsElement.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        string bestUrl = string.Empty;
        var bestArea = -1L;
        foreach (var thumbnail in thumbnailsElement.EnumerateArray())
        {
            var url = GetString(thumbnail, "url");
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            var width = thumbnail.TryGetProperty("width", out var widthElement) && widthElement.TryGetInt32(out var parsedWidth)
                ? parsedWidth
                : 0;
            var height = thumbnail.TryGetProperty("height", out var heightElement) && heightElement.TryGetInt32(out var parsedHeight)
                ? parsedHeight
                : 0;
            var area = (long)width * height;
            if (area >= bestArea)
            {
                bestArea = area;
                bestUrl = url;
            }
        }

        return bestUrl;
    }

    private static string BuildAccountKey(string pageId, string handle, string displayName, int authUserIndex)
    {
        if (!string.IsNullOrWhiteSpace(pageId))
        {
            return $"yt|page|{pageId}";
        }

        if (!string.IsNullOrWhiteSpace(handle))
        {
            return $"yt|handle|{handle.Trim().ToLowerInvariant()}";
        }

        if (!string.IsNullOrWhiteSpace(displayName))
        {
            return $"yt|name|{displayName.Trim().ToLowerInvariant()}|{authUserIndex}";
        }

        return $"yt|authuser|{authUserIndex}";
    }

    private readonly record struct CacheKey(
        BrowserCookieSource Browser,
        string Profile,
        string CookiesPath,
        long Length,
        long LastWriteTimeUtcTicks,
        int? PreferredAuthUserIndex);

    private readonly record struct CacheEntry(
        CacheKey Key,
        DateTimeOffset ExpiresAtUtc,
        IReadOnlyList<YouTubeAccountOption> Accounts);

    private readonly record struct HomeConfig(
        string ApiKey,
        string ClientName,
        string ClientVersion,
        int AuthUserIndex);

    internal sealed record PersistedCache(
        BrowserCookieSource Browser,
        string Profile,
        string CookiesPath,
        long CookiesLength,
        long CookiesLastWriteTimeUtcTicks,
        int? PreferredAuthUserIndex,
        DateTimeOffset SavedAtUtc,
        IReadOnlyList<YouTubeAccountOption> Accounts);
}

internal readonly record struct YouTubeAccountOption(
    string AccountKey,
    string DisplayName,
    string Handle,
    string Byline,
    string AvatarUrl,
    string PageId,
    string SignInUrl,
    string DatasyncId,
    int AuthUserIndex,
    bool IsSelected)
{
    public string Label
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(DisplayName))
            {
                parts.Add(DisplayName);
            }

            if (!string.IsNullOrWhiteSpace(Handle))
            {
                parts.Add(Handle);
            }

            if (!string.IsNullOrWhiteSpace(Byline))
            {
                parts.Add(Byline);
            }

            return parts.Count == 0
                ? "YouTube account"
                : string.Join(" · ", parts);
        }
    }
}
