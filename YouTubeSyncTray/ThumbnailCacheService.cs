using System.Diagnostics;

namespace YouTubeSyncTray;

internal sealed class ThumbnailCacheService
{
    private readonly YoutubeSyncPaths _paths;
    private readonly SemaphoreSlim _gate = new(4, 4);

    public ThumbnailCacheService(YoutubeSyncPaths paths)
    {
        _paths = paths;
    }

    public async Task<string?> EnsureThumbnailAsync(VideoItem item, CancellationToken cancellationToken)
    {
        var cachePath = Path.Combine(_paths.ThumbnailCachePath, $"{item.VideoId}.jpg");
        var sourcePath = File.Exists(item.ThumbnailPath) ? item.ThumbnailPath : item.VideoPath;
        if (File.Exists(cachePath) && File.Exists(sourcePath))
        {
            if (File.GetLastWriteTimeUtc(cachePath) >= File.GetLastWriteTimeUtc(sourcePath))
            {
                return cachePath;
            }
        }

        if (string.IsNullOrWhiteSpace(_paths.FfmpegRootPath))
        {
            return null;
        }

        var ffmpegExe = Path.Combine(_paths.FfmpegRootPath, "bin", "ffmpeg.exe");
        if (!File.Exists(ffmpegExe) || !File.Exists(sourcePath))
        {
            return null;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(cachePath))
            {
                return cachePath;
            }

            var arguments = sourcePath.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)
                ? $"-y -i \"{sourcePath}\" -frames:v 1 \"{cachePath}\""
                : $"-y -i \"{sourcePath}\" -frames:v 1 -q:v 3 \"{cachePath}\"";

            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = ffmpegExe,
                Arguments = arguments,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            });

            if (process is null)
            {
                return null;
            }

            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode == 0 && File.Exists(cachePath) ? cachePath : null;
        }
        finally
        {
            _gate.Release();
        }
    }
}
