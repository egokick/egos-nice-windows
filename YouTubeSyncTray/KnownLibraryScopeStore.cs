using System.Text.Json;

namespace YouTubeSyncTray;

internal sealed class KnownLibraryScopeStore
{
    private readonly string _storePath;
    private readonly object _gate = new();
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public KnownLibraryScopeStore(YoutubeSyncPaths paths)
    {
        _storePath = Path.Combine(paths.RootPath, "known-library-scopes.json");
    }

    public IReadOnlyList<KnownLibraryScopeRecord> LoadScopes()
    {
        lock (_gate)
        {
            var file = LoadFile();
            return file.Scopes
                .Select(CloneRecord)
                .OrderByDescending(scope => scope.IsAvailableOnDisk)
                .ThenByDescending(scope => scope.LastSuccessfulSyncAtUtc ?? DateTimeOffset.MinValue)
                .ThenByDescending(scope => scope.LastSeenAtUtc)
                .ThenBy(scope => scope.FolderName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public IReadOnlyList<KnownLibraryScopeRecord> GetScopesForBrowserAccount(string? browserAccountKey)
    {
        if (string.IsNullOrWhiteSpace(browserAccountKey))
        {
            return [];
        }

        var normalizedBrowserAccountKey = browserAccountKey.Trim();
        return LoadScopes()
            .Where(scope => string.Equals(scope.BrowserAccountKey, normalizedBrowserAccountKey, StringComparison.Ordinal))
            .ToList();
    }

    public KnownLibraryScopeRecord? GetMostRecentYouTubeScope(string? browserAccountKey)
    {
        return GetScopesForBrowserAccount(browserAccountKey)
            .Where(scope => scope.IsAvailableOnDisk && !string.IsNullOrWhiteSpace(scope.YouTubeAccountKey))
            .OrderByDescending(scope => scope.LastSuccessfulSyncAtUtc ?? DateTimeOffset.MinValue)
            .ThenByDescending(scope => scope.LastSeenAtUtc)
            .FirstOrDefault();
    }

    public bool HasAvailableYouTubeScope(string? browserAccountKey, string? youTubeAccountKey)
    {
        if (string.IsNullOrWhiteSpace(browserAccountKey) || string.IsNullOrWhiteSpace(youTubeAccountKey))
        {
            return false;
        }

        var normalizedBrowserAccountKey = browserAccountKey.Trim();
        var normalizedYouTubeAccountKey = youTubeAccountKey.Trim();
        return LoadScopes().Any(scope =>
            scope.IsAvailableOnDisk
            && string.Equals(scope.BrowserAccountKey, normalizedBrowserAccountKey, StringComparison.Ordinal)
            && string.Equals(scope.YouTubeAccountKey, normalizedYouTubeAccountKey, StringComparison.Ordinal));
    }

    public string? GetRememberedYouTubeAccountKey(string? browserAccountKey)
    {
        if (string.IsNullOrWhiteSpace(browserAccountKey))
        {
            return null;
        }

        var normalizedBrowserAccountKey = browserAccountKey.Trim();
        lock (_gate)
        {
            var file = LoadFile();
            return file.LastSelectedYouTubeAccountByBrowserAccount.TryGetValue(normalizedBrowserAccountKey, out var rememberedKey)
                && !string.IsNullOrWhiteSpace(rememberedKey)
                ? rememberedKey.Trim()
                : null;
        }
    }

    public void RememberSelectedYouTubeAccount(string? browserAccountKey, string? youTubeAccountKey)
    {
        if (string.IsNullOrWhiteSpace(browserAccountKey))
        {
            return;
        }

        var normalizedBrowserAccountKey = browserAccountKey.Trim();
        lock (_gate)
        {
            var file = LoadFile();
            if (string.IsNullOrWhiteSpace(youTubeAccountKey))
            {
                if (file.LastSelectedYouTubeAccountByBrowserAccount.Remove(normalizedBrowserAccountKey))
                {
                    SaveFile(file);
                }

                return;
            }

            var normalizedYouTubeAccountKey = youTubeAccountKey.Trim();
            if (file.LastSelectedYouTubeAccountByBrowserAccount.TryGetValue(normalizedBrowserAccountKey, out var existingValue)
                && string.Equals(existingValue, normalizedYouTubeAccountKey, StringComparison.Ordinal))
            {
                return;
            }

            file.LastSelectedYouTubeAccountByBrowserAccount[normalizedBrowserAccountKey] = normalizedYouTubeAccountKey;
            SaveFile(file);
        }
    }

    public void Register(AccountScopeResolver.ResolvedAccountScope scope)
    {
        lock (_gate)
        {
            var file = LoadFile();
            var record = GetOrCreateRecord(file, scope);
            ApplyScopeMetadata(record, scope);
            record.LastSeenAtUtc = DateTimeOffset.UtcNow;
            record.IsAvailableOnDisk = Directory.Exists(scope.DownloadsPath);
            SaveFile(file);
        }
    }

    public void UpdateScopeInventory(
        AccountScopeResolver.ResolvedAccountScope scope,
        int downloadedVideoCount,
        DateTimeOffset? lastSuccessfulSyncAtUtc = null)
    {
        lock (_gate)
        {
            var file = LoadFile();
            var record = GetOrCreateRecord(file, scope);
            ApplyScopeMetadata(record, scope);
            record.DownloadedVideoCount = Math.Max(downloadedVideoCount, 0);
            record.LastSeenAtUtc = DateTimeOffset.UtcNow;
            record.IsAvailableOnDisk = Directory.Exists(scope.DownloadsPath);
            if (lastSuccessfulSyncAtUtc.HasValue)
            {
                record.LastSuccessfulSyncAtUtc = lastSuccessfulSyncAtUtc.Value;
            }

            SaveFile(file);
        }
    }

    public static BrowserAccountOption CreateBrowserAccountOption(KnownLibraryScopeRecord scope)
    {
        var browser = TryParseBrowser(scope.BrowserAccountKey, out var parsedBrowser)
            ? parsedBrowser
            : BrowserCookieSource.Chrome;
        var profile = !string.IsNullOrWhiteSpace(scope.BrowserProfile)
            ? scope.BrowserProfile
            : ParseBrowserProfile(scope.BrowserAccountKey) ?? "Default";
        var displayName = FirstNonEmpty(scope.BrowserDisplayName, scope.BrowserEmail, ParseBrowserIdentity(scope.BrowserAccountKey));
        var email = scope.BrowserEmail ?? string.Empty;

        return new BrowserAccountOption(
            scope.BrowserAccountKey,
            browser,
            ChromiumBrowserLocator.GetDisplayName(browser),
            profile,
            Math.Max(scope.BrowserAuthUserIndex ?? 0, 0),
            ParseBrowserIdentity(scope.BrowserAccountKey) ?? string.Empty,
            email,
            displayName,
            string.Empty);
    }

    public static YouTubeAccountOption CreateYouTubeAccountOption(KnownLibraryScopeRecord scope, int fallbackAuthUserIndex)
    {
        var authUserIndex = Math.Max(scope.YouTubeAuthUserIndex ?? fallbackAuthUserIndex, 0);
        var accountKey = scope.YouTubeAccountKey ?? string.Empty;
        var fallback = YouTubeAccountDiscoveryService.BuildFallbackSelectedAccount(accountKey, authUserIndex);
        if (fallback.HasValue)
        {
            var fallbackValue = fallback.Value;
            var displayName = FirstNonEmpty(scope.YouTubeDisplayName, fallbackValue.DisplayName);
            var handle = FirstNonEmpty(scope.YouTubeHandle, fallbackValue.Handle);
            var byline = FirstNonEmpty(
                string.IsNullOrWhiteSpace(scope.LastSuccessfulSyncAtUtc?.ToString("u"))
                    ? null
                    : $"Saved locally. Last sync {scope.LastSuccessfulSyncAtUtc:yyyy-MM-dd HH:mm} UTC",
                fallbackValue.Byline);
            return fallbackValue with
            {
                DisplayName = displayName,
                Handle = handle,
                Byline = byline
            };
        }

        return new YouTubeAccountOption(
            accountKey,
            FirstNonEmpty(scope.YouTubeDisplayName, scope.YouTubeHandle, "Saved account"),
            scope.YouTubeHandle ?? string.Empty,
            scope.LastSuccessfulSyncAtUtc.HasValue
                ? $"Saved locally. Last sync {scope.LastSuccessfulSyncAtUtc:yyyy-MM-dd HH:mm} UTC"
                : "Saved locally",
            string.Empty,
            ParseYouTubePageId(accountKey) ?? string.Empty,
            string.Empty,
            string.Empty,
            authUserIndex,
            false);
    }

    private ScopeStoreFile LoadFile()
    {
        try
        {
            if (!File.Exists(_storePath))
            {
                return new ScopeStoreFile();
            }

            var file = JsonSerializer.Deserialize<ScopeStoreFile>(File.ReadAllText(_storePath), _jsonOptions) ?? new ScopeStoreFile();
            Normalize(file);
            RefreshAvailability(file);
            return file;
        }
        catch
        {
            return new ScopeStoreFile();
        }
    }

    private void SaveFile(ScopeStoreFile file)
    {
        Normalize(file);
        Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);
        File.WriteAllText(_storePath, JsonSerializer.Serialize(file, _jsonOptions));
    }

    private static void Normalize(ScopeStoreFile file)
    {
        file.Scopes = file.Scopes
            .Where(scope => scope is not null && !string.IsNullOrWhiteSpace(scope.FolderName))
            .Select(scope =>
            {
                Normalize(scope!);
                return scope!;
            })
            .GroupBy(scope => scope.FolderName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(scope => scope.LastSuccessfulSyncAtUtc ?? DateTimeOffset.MinValue)
                .ThenByDescending(scope => scope.LastSeenAtUtc)
                .First())
            .ToList();

        file.LastSelectedYouTubeAccountByBrowserAccount = file.LastSelectedYouTubeAccountByBrowserAccount
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
            .ToDictionary(
                pair => pair.Key.Trim(),
                pair => pair.Value.Trim(),
                StringComparer.Ordinal);
    }

    private static void Normalize(KnownLibraryScopeRecord scope)
    {
        scope.ScopeKey = string.IsNullOrWhiteSpace(scope.ScopeKey) ? BuildScopeKey(scope) : scope.ScopeKey.Trim();
        scope.FolderName = scope.FolderName.Trim();
        scope.DownloadsPath = scope.DownloadsPath?.Trim() ?? string.Empty;
        scope.ThumbnailCachePath = scope.ThumbnailCachePath?.Trim() ?? string.Empty;
        scope.ArchivePath = scope.ArchivePath?.Trim() ?? string.Empty;
        scope.BrowserAccountKey = scope.BrowserAccountKey?.Trim() ?? string.Empty;
        scope.BrowserDisplayName = scope.BrowserDisplayName?.Trim() ?? string.Empty;
        scope.BrowserEmail = scope.BrowserEmail?.Trim() ?? string.Empty;
        scope.BrowserProfile = scope.BrowserProfile?.Trim() ?? "Default";
        scope.YouTubeAccountKey = scope.YouTubeAccountKey?.Trim() ?? string.Empty;
        scope.YouTubeDisplayName = scope.YouTubeDisplayName?.Trim() ?? string.Empty;
        scope.YouTubeHandle = scope.YouTubeHandle?.Trim() ?? string.Empty;
        scope.DownloadedVideoCount = Math.Max(scope.DownloadedVideoCount, 0);
        scope.IsAvailableOnDisk = !string.IsNullOrWhiteSpace(scope.DownloadsPath) && Directory.Exists(scope.DownloadsPath);
    }

    private static void RefreshAvailability(ScopeStoreFile file)
    {
        foreach (var scope in file.Scopes)
        {
            scope.IsAvailableOnDisk = !string.IsNullOrWhiteSpace(scope.DownloadsPath) && Directory.Exists(scope.DownloadsPath);
        }
    }

    private static KnownLibraryScopeRecord GetOrCreateRecord(
        ScopeStoreFile file,
        AccountScopeResolver.ResolvedAccountScope scope)
    {
        var existing = file.Scopes.FirstOrDefault(record =>
            string.Equals(record.FolderName, scope.FolderName, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            return existing;
        }

        var created = new KnownLibraryScopeRecord
        {
            ScopeKey = scope.ScopeKey,
            FolderName = scope.FolderName
        };
        file.Scopes.Add(created);
        return created;
    }

    private static void ApplyScopeMetadata(KnownLibraryScopeRecord record, AccountScopeResolver.ResolvedAccountScope scope)
    {
        record.ScopeKey = scope.ScopeKey;
        record.FolderName = scope.FolderName;
        record.DownloadsPath = scope.DownloadsPath;
        record.ThumbnailCachePath = scope.ThumbnailCachePath;
        record.ArchivePath = scope.ArchivePath;
        record.BrowserAccountKey = scope.BrowserAccountKey;
        record.BrowserDisplayName = scope.BrowserDisplayName;
        record.BrowserEmail = scope.BrowserEmail;
        record.BrowserProfile = scope.BrowserProfile;
        record.BrowserAuthUserIndex = scope.BrowserAuthUserIndex;
        record.YouTubeAccountKey = scope.YouTubeAccountKey;
        record.YouTubeDisplayName = scope.YouTubeDisplayName;
        record.YouTubeHandle = scope.YouTubeHandle;
        record.YouTubeAuthUserIndex = scope.YouTubeAuthUserIndex;
    }

    private static KnownLibraryScopeRecord CloneRecord(KnownLibraryScopeRecord source)
    {
        return new KnownLibraryScopeRecord
        {
            ScopeKey = source.ScopeKey,
            FolderName = source.FolderName,
            DownloadsPath = source.DownloadsPath,
            ThumbnailCachePath = source.ThumbnailCachePath,
            ArchivePath = source.ArchivePath,
            BrowserAccountKey = source.BrowserAccountKey,
            BrowserDisplayName = source.BrowserDisplayName,
            BrowserEmail = source.BrowserEmail,
            BrowserProfile = source.BrowserProfile,
            BrowserAuthUserIndex = source.BrowserAuthUserIndex,
            YouTubeAccountKey = source.YouTubeAccountKey,
            YouTubeDisplayName = source.YouTubeDisplayName,
            YouTubeHandle = source.YouTubeHandle,
            YouTubeAuthUserIndex = source.YouTubeAuthUserIndex,
            DownloadedVideoCount = source.DownloadedVideoCount,
            LastSeenAtUtc = source.LastSeenAtUtc,
            LastSuccessfulSyncAtUtc = source.LastSuccessfulSyncAtUtc,
            IsAvailableOnDisk = source.IsAvailableOnDisk
        };
    }

    private static string BuildScopeKey(KnownLibraryScopeRecord scope)
    {
        return BuildScopeKey(scope.BrowserAccountKey, scope.YouTubeAccountKey, scope.FolderName);
    }

    internal static string BuildScopeKey(string? browserAccountKey, string? youTubeAccountKey, string folderName)
    {
        if (!string.IsNullOrWhiteSpace(browserAccountKey) && !string.IsNullOrWhiteSpace(youTubeAccountKey))
        {
            return browserAccountKey.Trim() + "||" + youTubeAccountKey.Trim();
        }

        if (!string.IsNullOrWhiteSpace(youTubeAccountKey))
        {
            return youTubeAccountKey.Trim();
        }

        if (!string.IsNullOrWhiteSpace(browserAccountKey))
        {
            return browserAccountKey.Trim();
        }

        return folderName.Trim();
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }

    private static bool TryParseBrowser(string? browserAccountKey, out BrowserCookieSource browser)
    {
        browser = BrowserCookieSource.Chrome;
        if (string.IsNullOrWhiteSpace(browserAccountKey))
        {
            return false;
        }

        var parts = browserAccountKey.Split('|', 3, StringSplitOptions.TrimEntries);
        return parts.Length >= 2 && Enum.TryParse(parts[0], ignoreCase: true, out browser);
    }

    private static string? ParseBrowserProfile(string? browserAccountKey)
    {
        if (string.IsNullOrWhiteSpace(browserAccountKey))
        {
            return null;
        }

        var parts = browserAccountKey.Split('|', 3, StringSplitOptions.TrimEntries);
        return parts.Length >= 2 && !string.IsNullOrWhiteSpace(parts[1])
            ? parts[1]
            : null;
    }

    private static string? ParseBrowserIdentity(string? browserAccountKey)
    {
        if (string.IsNullOrWhiteSpace(browserAccountKey))
        {
            return null;
        }

        var parts = browserAccountKey.Split('|', 3, StringSplitOptions.TrimEntries);
        return parts.Length == 3 && !string.IsNullOrWhiteSpace(parts[2])
            ? parts[2]
            : null;
    }

    private static string? ParseYouTubePageId(string? accountKey)
    {
        if (string.IsNullOrWhiteSpace(accountKey))
        {
            return null;
        }

        var parts = accountKey.Split('|', StringSplitOptions.TrimEntries);
        return parts.Length >= 3 && string.Equals(parts[1], "page", StringComparison.Ordinal)
            ? parts[2]
            : null;
    }

    private sealed class ScopeStoreFile
    {
        public List<KnownLibraryScopeRecord> Scopes { get; set; } = [];

        public Dictionary<string, string> LastSelectedYouTubeAccountByBrowserAccount { get; set; } =
            new(StringComparer.Ordinal);
    }
}

internal sealed class KnownLibraryScopeRecord
{
    public string ScopeKey { get; set; } = string.Empty;

    public string FolderName { get; set; } = string.Empty;

    public string DownloadsPath { get; set; } = string.Empty;

    public string ThumbnailCachePath { get; set; } = string.Empty;

    public string ArchivePath { get; set; } = string.Empty;

    public string BrowserAccountKey { get; set; } = string.Empty;

    public string BrowserDisplayName { get; set; } = string.Empty;

    public string BrowserEmail { get; set; } = string.Empty;

    public string BrowserProfile { get; set; } = "Default";

    public int? BrowserAuthUserIndex { get; set; }

    public string YouTubeAccountKey { get; set; } = string.Empty;

    public string YouTubeDisplayName { get; set; } = string.Empty;

    public string YouTubeHandle { get; set; } = string.Empty;

    public int? YouTubeAuthUserIndex { get; set; }

    public int DownloadedVideoCount { get; set; }

    public DateTimeOffset LastSeenAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? LastSuccessfulSyncAtUtc { get; set; }

    public bool IsAvailableOnDisk { get; set; }
}
