namespace YouTubeSyncTray;

internal sealed class YoutubeSyncPaths
{
    public required string RootPath { get; init; }
    public required string BrowserProfilesPath { get; init; }
    public required string DownloadsPath { get; init; }
    public required string YtDlpPath { get; init; }
    public required string CookiesPath { get; init; }
    public required string CookiesMetadataPath { get; init; }
    public required string ArchivePath { get; init; }
    public required string TempPath { get; init; }
    public required string LogsPath { get; init; }
    public required string ThumbnailCachePath { get; init; }
    public string? FfmpegRootPath { get; init; }

    public static YoutubeSyncPaths Discover()
    {
        var assetRoot = FindAssetRoot();
        var stateRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "YouTubeSyncTray");
        var downloadsRoot = ResolveDownloadsRoot(assetRoot);

        MaybeMigrateLegacyState(assetRoot, stateRoot);
        MaybeMigrateDownloads(stateRoot, assetRoot, downloadsRoot);
        return Create(stateRoot, assetRoot, downloadsRoot);
    }

    private static YoutubeSyncPaths Create(string root, string assetRoot, string downloadsRoot)
    {
        var ffmpegRoot = Directory.Exists(Path.Combine(assetRoot, "tools"))
            ? Directory.GetDirectories(Path.Combine(assetRoot, "tools"), "ffmpeg-*", SearchOption.TopDirectoryOnly).FirstOrDefault()
            : null;

        var paths = new YoutubeSyncPaths
        {
            RootPath = root,
            BrowserProfilesPath = Path.Combine(root, "browser-profiles"),
            DownloadsPath = downloadsRoot,
            YtDlpPath = Path.Combine(assetRoot, "yt-dlp.exe"),
            CookiesPath = Path.Combine(root, "youtube-cookies.txt"),
            CookiesMetadataPath = Path.Combine(root, "youtube-cookies.metadata.json"),
            ArchivePath = Path.Combine(root, "watch-later.archive.txt"),
            TempPath = Path.Combine(root, "temp"),
            LogsPath = Path.Combine(root, "logs"),
            ThumbnailCachePath = Path.Combine(root, "thumb-cache"),
            FfmpegRootPath = ffmpegRoot
        };

        Directory.CreateDirectory(paths.RootPath);
        Directory.CreateDirectory(paths.BrowserProfilesPath);
        Directory.CreateDirectory(paths.DownloadsPath);
        Directory.CreateDirectory(paths.TempPath);
        Directory.CreateDirectory(paths.LogsPath);
        Directory.CreateDirectory(paths.ThumbnailCachePath);
        return paths;
    }

    private static string ResolveDownloadsRoot(string assetRoot)
    {
        var projectRoot = FindProjectRoot(assetRoot);
        if (!string.IsNullOrWhiteSpace(projectRoot))
        {
            return Path.Combine(projectRoot, "Downloads");
        }

        var assetParent = Directory.GetParent(assetRoot)?.FullName;
        return string.IsNullOrWhiteSpace(assetParent)
            ? Path.Combine(AppContext.BaseDirectory, "Downloads")
            : Path.Combine(assetParent, "Downloads");
    }

    private static string? FindProjectRoot(string assetRoot)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "YouTubeSyncTray.csproj")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        current = new DirectoryInfo(assetRoot);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "YouTubeSyncTray.csproj")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return null;
    }

    private static string FindAssetRoot()
    {
        var bundledRoot = Path.Combine(AppContext.BaseDirectory, "youtube-sync");
        if (Directory.Exists(bundledRoot) && File.Exists(Path.Combine(bundledRoot, "yt-dlp.exe")))
        {
            return bundledRoot;
        }

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 8 && current is not null; depth++, current = current.Parent)
        {
            var candidateRoot = Path.Combine(current.FullName, "youtube-sync");
            if (Directory.Exists(candidateRoot) && File.Exists(Path.Combine(candidateRoot, "yt-dlp.exe")))
            {
                return candidateRoot;
            }
        }

        return bundledRoot;
    }

    private static void MaybeMigrateLegacyState(string legacyRoot, string stateRoot)
    {
        if (string.Equals(
                Path.GetFullPath(legacyRoot).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(stateRoot).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!Directory.Exists(legacyRoot) || HasExistingState(stateRoot))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(stateRoot);
            CopyDirectoryIfExists(ResolveLegacyDirectoryPath(legacyRoot, "browser-profiles"), Path.Combine(stateRoot, "browser-profiles"));
            CopyDirectoryIfExists(ResolveLegacyDirectoryPath(legacyRoot, "thumb-cache"), Path.Combine(stateRoot, "thumb-cache"));
            CopyDirectoryIfExists(ResolveLegacyDirectoryPath(legacyRoot, "logs"), Path.Combine(stateRoot, "logs"));
            CopyFileIfExists(ResolveLegacyFilePath(legacyRoot, "watch-later.archive.txt"), Path.Combine(stateRoot, "watch-later.archive.txt"));
            CopyFileIfExists(ResolveLegacyFilePath(legacyRoot, "youtube-cookies.txt"), Path.Combine(stateRoot, "youtube-cookies.txt"));
        }
        catch
        {
            // Legacy migration is best-effort. Fresh state will still be created if copying fails.
        }
    }

    private static void MaybeMigrateDownloads(string stateRoot, string legacyRoot, string downloadsRoot)
    {
        if (HasExistingState(downloadsRoot))
        {
            return;
        }

        try
        {
            var stateDownloads = Path.Combine(stateRoot, "downloads");
            if (Directory.Exists(stateDownloads) && Directory.EnumerateFileSystemEntries(stateDownloads).Any())
            {
                CopyDirectoryIfExists(stateDownloads, downloadsRoot);
                return;
            }

            var legacyDownloads = ResolveLegacyDirectoryPath(legacyRoot, "downloads");
            if (Directory.Exists(legacyDownloads) && Directory.EnumerateFileSystemEntries(legacyDownloads).Any())
            {
                CopyDirectoryIfExists(legacyDownloads, downloadsRoot);
            }
        }
        catch
        {
            // Download migration is best-effort. The app will still create a fresh Downloads folder.
        }
    }

    private static bool HasExistingState(string stateRoot)
    {
        if (!Directory.Exists(stateRoot))
        {
            return false;
        }

        return Directory.EnumerateFileSystemEntries(stateRoot).Any();
    }

    private static void CopyDirectoryIfExists(string sourcePath, string destinationPath)
    {
        if (!Directory.Exists(sourcePath))
        {
            return;
        }

        foreach (var directory in Directory.GetDirectories(sourcePath, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourcePath, directory);
            Directory.CreateDirectory(Path.Combine(destinationPath, relativePath));
        }

        Directory.CreateDirectory(destinationPath);
        foreach (var file in Directory.GetFiles(sourcePath, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourcePath, file);
            var destinationFile = Path.Combine(destinationPath, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
            File.Copy(file, destinationFile, overwrite: false);
        }
    }

    private static void CopyFileIfExists(string sourcePath, string destinationPath)
    {
        if (!File.Exists(sourcePath))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        File.Copy(sourcePath, destinationPath, overwrite: false);
    }

    private static string ResolveLegacyDirectoryPath(string legacyRoot, string name)
    {
        var currentPath = Path.Combine(legacyRoot, name);
        if (Directory.Exists(currentPath))
        {
            return currentPath;
        }

        return Path.Combine(legacyRoot, "legacy-" + name);
    }

    private static string ResolveLegacyFilePath(string legacyRoot, string name)
    {
        var currentPath = Path.Combine(legacyRoot, name);
        if (File.Exists(currentPath))
        {
            return currentPath;
        }

        return Path.Combine(legacyRoot, "legacy-" + name);
    }
}
