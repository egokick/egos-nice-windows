using System.Security.Cryptography;
using System.Text;

namespace YouTubeSyncTray;

internal sealed class AccountScopeResolver
{
    private readonly YoutubeSyncPaths _paths;
    private readonly BrowserAccountDiscoveryService _browserAccountDiscovery;
    private readonly YouTubeAccountDiscoveryService _youTubeAccountDiscovery;
    private readonly KnownLibraryScopeStore _knownLibraryScopeStore;

    public AccountScopeResolver(
        YoutubeSyncPaths paths,
        BrowserAccountDiscoveryService browserAccountDiscovery,
        YouTubeAccountDiscoveryService youTubeAccountDiscovery,
        KnownLibraryScopeStore knownLibraryScopeStore)
    {
        _paths = paths;
        _browserAccountDiscovery = browserAccountDiscovery;
        _youTubeAccountDiscovery = youTubeAccountDiscovery;
        _knownLibraryScopeStore = knownLibraryScopeStore;
    }

    public ResolvedAccountScope Resolve(AppSettings settings)
    {
        settings.Normalize();

        var browserAccount = _browserAccountDiscovery.ResolveSelectedAccount(settings);
        var youTubeAccount = _youTubeAccountDiscovery.ResolveSelectedAccount(
            settings,
            browserAccount?.AuthUserIndex,
            allowNetwork: false);
        var browserAccountKey = browserAccount?.AccountKey ?? settings.SelectedBrowserAccountKey ?? string.Empty;
        var youTubeAccountKey = youTubeAccount?.AccountKey ?? settings.SelectedYouTubeAccountKey ?? string.Empty;
        var preferredFolderName = BuildFolderName(browserAccount, youTubeAccount, settings);
        var folderName = ResolveExistingFolderName(
            _paths.DownloadsPath,
            _paths.ThumbnailCachePath,
            Path.Combine(_paths.RootPath, "archives"),
            GetScopedIdentityKey(browserAccount, youTubeAccount, settings),
            preferredFolderName);
        var downloadsPath = Path.Combine(_paths.DownloadsPath, folderName);
        var thumbnailCachePath = Path.Combine(_paths.ThumbnailCachePath, folderName);
        var archivesPath = Path.Combine(_paths.RootPath, "archives");
        var archivePath = Path.Combine(archivesPath, folderName + ".watch-later.archive.txt");

        Directory.CreateDirectory(downloadsPath);
        Directory.CreateDirectory(thumbnailCachePath);
        Directory.CreateDirectory(archivesPath);
        MaybeMigrateLegacyRootDownloads(downloadsPath);
        MaybeMigrateBrowserFallbackScope(browserAccount, youTubeAccount, settings, downloadsPath, thumbnailCachePath, archivePath);

        var scope = new ResolvedAccountScope(
            KnownLibraryScopeStore.BuildScopeKey(browserAccountKey, youTubeAccountKey, folderName),
            browserAccount,
            youTubeAccount,
            browserAccountKey,
            GetBrowserLabel(browserAccount, settings) ?? string.Empty,
            browserAccount?.Email ?? string.Empty,
            browserAccount?.Profile ?? settings.BrowserProfile,
            browserAccount?.AuthUserIndex,
            youTubeAccountKey,
            FirstNonEmpty(
                youTubeAccount?.DisplayName,
                TryParseYouTubeSelectionLabel(settings.SelectedYouTubeAccountKey))
            ?? string.Empty,
            FirstNonEmpty(
                youTubeAccount?.Handle,
                TryParseYouTubeHandle(settings.SelectedYouTubeAccountKey))
            ?? string.Empty,
            youTubeAccount?.AuthUserIndex,
            folderName,
            downloadsPath,
            thumbnailCachePath,
            archivePath);
        _knownLibraryScopeStore.Register(scope);
        return scope;
    }

    internal static string BuildFolderName(
        BrowserAccountOption? browserAccount,
        YouTubeAccountOption? youTubeAccount,
        AppSettings settings)
    {
        return BuildFolderName(
            GetBrowserIdentityKey(browserAccount, settings),
            GetBrowserLabel(browserAccount, settings),
            GetYouTubeIdentityKey(youTubeAccount, settings),
            GetYouTubeLabel(youTubeAccount, settings),
            settings);
    }

    internal static string BuildFolderName(
        string? browserIdentityKey,
        string? browserLabel,
        string? youTubeIdentityKey,
        string? youTubeLabel,
        AppSettings settings)
    {
        settings.Normalize();

        var identityKey =
            youTubeIdentityKey
            ?? browserIdentityKey
            ?? $"{settings.BrowserCookies}|{settings.BrowserProfile}";
        var label =
            FirstNonEmpty(youTubeLabel, browserLabel)
            ?? $"{ChromiumBrowserLocator.GetDisplayName(settings.BrowserCookies)} {settings.BrowserProfile}";

        var normalizedLabel = NormalizeFolderSegment(label);
        var hash = GetFolderHash(identityKey);
        return $"{normalizedLabel} [{hash}]";
    }

    internal static string ResolveExistingFolderName(
        string downloadsRoot,
        string thumbnailCacheRoot,
        string archivesRoot,
        string identityKey,
        string preferredFolderName)
    {
        var hashSuffix = $" [{GetFolderHash(identityKey)}]";
        var existingFolderName = FindExistingFolderName(downloadsRoot, hashSuffix)
            ?? FindExistingFolderName(thumbnailCacheRoot, hashSuffix)
            ?? FindExistingArchiveFolderName(archivesRoot, hashSuffix);
        return existingFolderName ?? preferredFolderName;
    }

    private void MaybeMigrateLegacyRootDownloads(string scopedDownloadsPath)
    {
        try
        {
            if (Directory.EnumerateFileSystemEntries(scopedDownloadsPath).Any())
            {
                return;
            }

            var rootFiles = Directory.GetFiles(_paths.DownloadsPath, "*", SearchOption.TopDirectoryOnly)
                .Where(IsLegacyDownloadArtifact)
                .ToArray();
            if (rootFiles.Length == 0)
            {
                return;
            }

            foreach (var sourcePath in rootFiles)
            {
                var destinationPath = Path.Combine(scopedDownloadsPath, Path.GetFileName(sourcePath));
                if (File.Exists(destinationPath))
                {
                    continue;
                }

                File.Move(sourcePath, destinationPath);
            }
        }
        catch
        {
            // Migration is best-effort. Fresh account-scoped storage will still work if moving old files fails.
        }
    }

    private void MaybeMigrateBrowserFallbackScope(
        BrowserAccountOption? browserAccount,
        YouTubeAccountOption? youTubeAccount,
        AppSettings settings,
        string downloadsPath,
        string thumbnailCachePath,
        string archivePath)
    {
        if (!youTubeAccount.HasValue || browserAccount is null)
        {
            return;
        }

        try
        {
            if (Directory.EnumerateFileSystemEntries(downloadsPath).Any())
            {
                return;
            }

            var browserFolderName = BuildFolderName(browserAccount, null, settings);
            var browserDownloadsPath = Path.Combine(_paths.DownloadsPath, browserFolderName);
            if (!Directory.Exists(browserDownloadsPath)
                || !Directory.EnumerateFileSystemEntries(browserDownloadsPath).Any())
            {
                return;
            }

            MoveDirectoryContents(browserDownloadsPath, downloadsPath);

            var browserThumbnailPath = Path.Combine(_paths.ThumbnailCachePath, browserFolderName);
            if (Directory.Exists(browserThumbnailPath))
            {
                MoveDirectoryContents(browserThumbnailPath, thumbnailCachePath);
            }

            var browserArchivePath = Path.Combine(_paths.RootPath, "archives", browserFolderName + ".watch-later.archive.txt");
            if (File.Exists(browserArchivePath) && !File.Exists(archivePath))
            {
                File.Move(browserArchivePath, archivePath);
            }
        }
        catch
        {
            // Scope migration is best-effort. A later sync will repopulate the account-scoped folder if needed.
        }
    }

    private static bool IsLegacyDownloadArtifact(string path)
    {
        var extension = Path.GetExtension(path);
        return path.EndsWith(".info.json", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".mkv", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".webm", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".m4v", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".mov", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".webp", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".srt", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".vtt", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".part", StringComparison.OrdinalIgnoreCase);
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static string GetScopedIdentityKey(
        BrowserAccountOption? browserAccount,
        YouTubeAccountOption? youTubeAccount,
        AppSettings settings)
    {
        settings.Normalize();
        return GetYouTubeIdentityKey(youTubeAccount, settings)
            ?? GetBrowserIdentityKey(browserAccount, settings)
            ?? $"{settings.BrowserCookies}|{settings.BrowserProfile}";
    }

    private static string? GetBrowserIdentityKey(BrowserAccountOption? browserAccount, AppSettings settings)
    {
        settings.Normalize();
        return FirstNonEmpty(
            browserAccount?.AccountKey,
            settings.SelectedBrowserAccountKey);
    }

    private static string? GetYouTubeIdentityKey(YouTubeAccountOption? youTubeAccount, AppSettings settings)
    {
        settings.Normalize();
        return FirstNonEmpty(
            youTubeAccount?.AccountKey,
            settings.SelectedYouTubeAccountKey);
    }

    private static string? GetBrowserLabel(BrowserAccountOption? browserAccount, AppSettings settings)
    {
        settings.Normalize();
        return FirstNonEmpty(
            browserAccount?.DisplayName,
            browserAccount?.Email,
            TryParseBrowserSelectionLabel(settings.SelectedBrowserAccountKey));
    }

    private static string? GetYouTubeLabel(YouTubeAccountOption? youTubeAccount, AppSettings settings)
    {
        settings.Normalize();
        return FirstNonEmpty(
            youTubeAccount?.DisplayName,
            youTubeAccount?.Handle,
            TryParseYouTubeSelectionLabel(settings.SelectedYouTubeAccountKey));
    }

    private static string? TryParseBrowserSelectionLabel(string? accountKey)
    {
        if (string.IsNullOrWhiteSpace(accountKey))
        {
            return null;
        }

        var parts = accountKey.Split('|', 3, StringSplitOptions.TrimEntries);
        return parts.Length == 3 && !string.IsNullOrWhiteSpace(parts[2])
            ? parts[2]
            : null;
    }

    private static string? TryParseYouTubeSelectionLabel(string? accountKey)
    {
        if (string.IsNullOrWhiteSpace(accountKey))
        {
            return null;
        }

        var parts = accountKey.Split('|', StringSplitOptions.TrimEntries);
        if (parts.Length < 3 || !string.Equals(parts[0], "yt", StringComparison.Ordinal))
        {
            return null;
        }

        return parts[1] switch
        {
            "handle" => parts[2],
            "name" => parts[2],
            "page" => $"Channel {parts[2]}",
            "authuser" => $"YouTube {parts[2]}",
            _ => null
        };
    }

    private static string? TryParseYouTubeHandle(string? accountKey)
    {
        if (string.IsNullOrWhiteSpace(accountKey))
        {
            return null;
        }

        var parts = accountKey.Split('|', StringSplitOptions.TrimEntries);
        return parts.Length >= 3 && string.Equals(parts[1], "handle", StringComparison.Ordinal)
            ? parts[2]
            : null;
    }

    private static string GetFolderHash(string identityKey) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identityKey)))[..8];

    private static string? FindExistingFolderName(string rootPath, string hashSuffix)
    {
        if (!Directory.Exists(rootPath))
        {
            return null;
        }

        foreach (var directory in Directory.GetDirectories(rootPath, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(directory);
            if (name.EndsWith(hashSuffix, StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }
        }

        return null;
    }

    private static string? FindExistingArchiveFolderName(string archivesRoot, string hashSuffix)
    {
        if (!Directory.Exists(archivesRoot))
        {
            return null;
        }

        foreach (var archivePath in Directory.GetFiles(archivesRoot, "*.watch-later.archive.txt", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(archivePath);
            if (!name.EndsWith(".watch-later.archive.txt", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var folderName = name[..^".watch-later.archive.txt".Length];
            if (folderName.EndsWith(hashSuffix, StringComparison.OrdinalIgnoreCase))
            {
                return folderName;
            }
        }

        return null;
    }

    private static string NormalizeFolderSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Trim())
        {
            builder.Append(invalid.Contains(character) || char.IsControl(character) ? '_' : character);
        }

        var normalized = builder.ToString()
            .Replace(":", " ", StringComparison.Ordinal)
            .Replace("/", " ", StringComparison.Ordinal)
            .Replace("\\", " ", StringComparison.Ordinal)
            .Replace("\"", string.Empty, StringComparison.Ordinal)
            .Trim(' ', '.');

        if (normalized.Length == 0)
        {
            normalized = "Account";
        }

        if (normalized.Length > 48)
        {
            normalized = normalized[..48].TrimEnd();
        }

        return normalized;
    }

    private static void MoveDirectoryContents(string sourcePath, string destinationPath)
    {
        Directory.CreateDirectory(destinationPath);
        foreach (var sourceFile in Directory.GetFiles(sourcePath, "*", SearchOption.TopDirectoryOnly))
        {
            var destinationFile = Path.Combine(destinationPath, Path.GetFileName(sourceFile));
            if (!File.Exists(destinationFile))
            {
                File.Move(sourceFile, destinationFile);
            }
        }
    }

    internal readonly record struct ResolvedAccountScope(
        string ScopeKey,
        BrowserAccountOption? BrowserAccount,
        YouTubeAccountOption? YouTubeAccount,
        string BrowserAccountKey,
        string BrowserDisplayName,
        string BrowserEmail,
        string BrowserProfile,
        int? BrowserAuthUserIndex,
        string YouTubeAccountKey,
        string YouTubeDisplayName,
        string YouTubeHandle,
        int? YouTubeAuthUserIndex,
        string FolderName,
        string DownloadsPath,
        string ThumbnailCachePath,
        string ArchivePath)
    {
        public bool HasYouTubeScope => !string.IsNullOrWhiteSpace(YouTubeAccountKey);
    }
}
