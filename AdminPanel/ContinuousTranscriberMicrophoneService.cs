using System.Diagnostics;

namespace AdminPanel;

internal static class ContinuousTranscriberMicrophoneService
{
    private const int DeviceQueryTimeoutMilliseconds = 10_000;

    public static List<string> GetAvailableMicrophones()
    {
        var microphones = new List<string>();
        var ffmpeg = GetFfmpegPath();
        if (string.IsNullOrWhiteSpace(ffmpeg))
        {
            return microphones;
        }

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffmpeg,
                    Arguments = "-hide_banner -list_devices true -f dshow -i dummy",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            if (!process.Start())
            {
                return microphones;
            }

            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(DeviceQueryTimeoutMilliseconds))
            {
                process.Kill(entireProcessTree: true);
                return microphones;
            }

            Task.WaitAll(standardOutput, standardError);
            foreach (var line in (standardOutput.Result + Environment.NewLine + standardError.Result)
                         .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!line.EndsWith("(audio)", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var firstQuote = line.IndexOf('"');
                var lastQuote = line.LastIndexOf('"');
                if (firstQuote < 0 || lastQuote <= firstQuote)
                {
                    continue;
                }

                var name = line[(firstQuote + 1)..lastQuote];
                if (!microphones.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    microphones.Add(name);
                }
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                             or System.ComponentModel.Win32Exception
                                             or IOException
                                             or UnauthorizedAccessException)
        {
            // The settings dialog remains usable with the Windows default device.
        }

        return microphones;
    }

    public static string GetDefaultMicrophone()
    {
        var scriptPath = Path.Combine(
            NiceWindowsRepositoryLocator.GetRepositoryRoot(),
            "Continuous-transcriber",
            "get-default-microphone.ps1");
        if (!File.Exists(scriptPath))
        {
            return string.Empty;
        }

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.System),
                        "WindowsPowerShell",
                        "v1.0",
                        "powershell.exe"),
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            if (!process.Start() || !process.WaitForExit(DeviceQueryTimeoutMilliseconds))
            {
                return string.Empty;
            }

            var value = process.StandardOutput.ReadToEnd().Trim();
            return process.ExitCode == 0 ? value : string.Empty;
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                             or System.ComponentModel.Win32Exception
                                             or IOException
                                             or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private static string? GetFfmpegPath()
    {
        try
        {
            var markerPath = Path.Combine(
                NiceWindowsRepositoryLocator.GetRepositoryRoot(),
                "Continuous-transcriber",
                "runtime",
                "bin",
                "ffmpeg.path");
            if (!File.Exists(markerPath))
            {
                return null;
            }

            var path = File.ReadAllText(markerPath).Trim();
            return File.Exists(path) ? path : null;
        }
        catch (Exception exception) when (exception is IOException
                                             or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
