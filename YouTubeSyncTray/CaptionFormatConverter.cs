using System.Text;
using System.Text.RegularExpressions;

namespace YouTubeSyncTray;

internal static class CaptionFormatConverter
{
    private static readonly Regex TimestampRegex = new(@"(?<time>\d{1,2}:\d{2}:\d{2}),(?<ms>\d{3})", RegexOptions.Compiled);

    public static string ConvertSrtToWebVtt(string srtContent)
    {
        if (string.IsNullOrWhiteSpace(srtContent))
        {
            return "WEBVTT\n";
        }

        var normalized = srtContent
            .Replace("\uFEFF", string.Empty, StringComparison.Ordinal)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var lines = normalized.Split('\n');
        var builder = new StringBuilder();
        builder.Append("WEBVTT\n\n");

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                builder.Append('\n');
                continue;
            }

            if (int.TryParse(trimmed, out _)
                && index + 1 < lines.Length
                && lines[index + 1].Contains("-->", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.Contains("-->", StringComparison.Ordinal))
            {
                builder.AppendLine(TimestampRegex.Replace(line, match => $"{match.Groups["time"].Value}.{match.Groups["ms"].Value}"));
                continue;
            }

            builder.AppendLine(line);
        }

        return builder.ToString().TrimEnd('\n') + "\n";
    }
}
