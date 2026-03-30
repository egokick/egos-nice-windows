using System.Diagnostics;
using System.Text.Json;

namespace YouTubeSyncTray;

internal sealed class BrowserPlaybackOptimizer
{
    private readonly YoutubeSyncPaths _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public BrowserPlaybackOptimizer(YoutubeSyncPaths paths)
    {
        _paths = paths;
    }

    public async Task<BrowserPlaybackOptimizationSummary> OptimizeLibraryAsync(
        string downloadsPath,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(downloadsPath))
        {
            return default;
        }

        var ffmpegExe = ResolveFfmpegToolPath("ffmpeg.exe");
        var ffprobeExe = ResolveFfmpegToolPath("ffprobe.exe");
        if (string.IsNullOrWhiteSpace(ffmpegExe) || string.IsNullOrWhiteSpace(ffprobeExe))
        {
            return default;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var convertedCount = 0;
            var skippedCount = 0;
            var failedCount = 0;

            foreach (var sourcePath in Directory.GetFiles(downloadsPath, "*.mkv", SearchOption.TopDirectoryOnly)
                         .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var basePath = Path.Combine(
                    Path.GetDirectoryName(sourcePath) ?? downloadsPath,
                    Path.GetFileNameWithoutExtension(sourcePath));

                if (HasBrowserReadySibling(basePath))
                {
                    skippedCount++;
                    continue;
                }

                if (!await CanCreateBrowserReadyMp4Async(ffprobeExe, sourcePath, cancellationToken))
                {
                    skippedCount++;
                    continue;
                }

                var targetPath = basePath + ".mp4";
                var tempPath = basePath + ".browser-ready.mp4";
                DeleteFileIfExists(tempPath);
                progress?.Report($"Optimizing {Path.GetFileName(sourcePath)} for browser playback...");

                var result = await RunProcessAsync(
                    ffmpegExe,
                    BuildBrowserReadyMp4Arguments(sourcePath, tempPath),
                    cancellationToken);

                if (result.ExitCode == 0 && File.Exists(tempPath))
                {
                    File.Move(tempPath, targetPath, overwrite: true);
                    convertedCount++;
                    continue;
                }

                DeleteFileIfExists(tempPath);
                failedCount++;
                TrayLog.Write(
                    _paths,
                    $"Browser playback optimization failed for {Path.GetFileName(sourcePath)}. ExitCode={result.ExitCode}. Detail: {Truncate(result.StdErr)}");
            }

            return new BrowserPlaybackOptimizationSummary(convertedCount, skippedCount, failedCount);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static string BuildBrowserReadyMp4Arguments(string sourcePath, string targetPath)
    {
        return $"-y -i \"{sourcePath}\" -map 0:v:0 -map 0:a:0? -c:v copy -c:a aac -b:a 160k -sn -dn -movflags +faststart \"{targetPath}\"";
    }

    private string? ResolveFfmpegToolPath(string fileName)
    {
        if (string.IsNullOrWhiteSpace(_paths.FfmpegRootPath))
        {
            return null;
        }

        var toolPath = Path.Combine(_paths.FfmpegRootPath, "bin", fileName);
        return File.Exists(toolPath) ? toolPath : null;
    }

    private static bool HasBrowserReadySibling(string basePath)
    {
        return File.Exists(basePath + ".mp4")
            || File.Exists(basePath + ".webm")
            || File.Exists(basePath + ".m4v")
            || File.Exists(basePath + ".mov");
    }

    private static async Task<bool> CanCreateBrowserReadyMp4Async(
        string ffprobeExe,
        string sourcePath,
        CancellationToken cancellationToken)
    {
        var result = await RunProcessAsync(
            ffprobeExe,
            $"-v error -show_entries stream=codec_type,codec_name -of json \"{sourcePath}\"",
            cancellationToken);
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StdOut))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(result.StdOut);
            if (!document.RootElement.TryGetProperty("streams", out var streamsElement)
                || streamsElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var streamElement in streamsElement.EnumerateArray())
            {
                var codecType = streamElement.TryGetProperty("codec_type", out var codecTypeElement)
                    ? codecTypeElement.GetString()
                    : null;
                var codecName = streamElement.TryGetProperty("codec_name", out var codecNameElement)
                    ? codecNameElement.GetString()
                    : null;
                if (string.Equals(codecType, "video", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(codecName, "h264", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static async Task<ProcessResult> RunProcessAsync(
        string fileName,
        string arguments,
        CancellationToken cancellationToken)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        });

        if (process is null)
        {
            return new ProcessResult(-1, string.Empty, string.Empty);
        }

        using var cancellationRegistration = cancellationToken.Register(() => TryStopProcess(process));
        try
        {
            var stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return new ProcessResult(
                process.ExitCode,
                await stdOutTask,
                await stdErrTask);
        }
        finally
        {
            if (cancellationToken.IsCancellationRequested)
            {
                TryStopProcess(process);
            }
        }
    }

    private static void TryStopProcess(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return;
            }

            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
        catch
        {
            // Best-effort cleanup during cancellation/shutdown.
        }
    }

    private static void DeleteFileIfExists(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup. A later attempt can overwrite or delete the temp file.
        }
    }

    private static string Truncate(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var trimmed = text.Trim();
        return trimmed.Length <= 240
            ? trimmed
            : trimmed[..240];
    }

    private readonly record struct ProcessResult(int ExitCode, string StdOut, string StdErr);
}

internal readonly record struct BrowserPlaybackOptimizationSummary(
    int ConvertedCount,
    int SkippedCount,
    int FailedCount);
