using System.Text.RegularExpressions;

namespace Taildesk.Shared;

public static class RustDeskConfiguration
{
    public const string PrivateRendezvousServer = "127.0.0.1:21116";

    public static IReadOnlyDictionary<string, string> ManagedHostOptions { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["direct-server"] = "Y",
            ["direct-access-port"] = "21118",
            ["custom-rendezvous-server"] = "127.0.0.1",
            ["relay-server"] = "127.0.0.1",
            ["whitelist"] = ",",
            ["access-mode"] = "full",
            ["enable-keyboard"] = "Y",
            ["enable-clipboard"] = "Y",
            ["enable-file-transfer"] = "Y",
            ["approve-mode"] = "password",
            ["verification-method"] = "use-permanent-password",
            ["allow-only-conn-window-open"] = "N",
            ["allow-logon-screen-password"] = "Y",
            ["enable-lan-discovery"] = "N",
            ["allow-remote-config-modification"] = "N",
            ["allow-remote-cm-modification"] = "N",
            ["enable-trusted-devices"] = "N",
            ["hide-tray"] = "Y",
            ["hide-stop-service"] = "Y",
            ["disable-discovery-panel"] = "Y",
            ["allow-auto-update"] = "N",
            ["enable-udp-punch"] = "N",
            ["enable-ipv6-punch"] = "N"
        };

    public static string HardenManagedHost(string? existing)
    {
        var lines = Regex.Split((existing ?? string.Empty).Replace("\r\n", "\n"), "\n").ToList();
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);

        lines.RemoveAll(line => Regex.IsMatch(line, "^\\s*rendezvous-server\\s*=", RegexOptions.IgnoreCase));
        SetTopLevel(lines, "rendezvous_server", PrivateRendezvousServer);

        var sectionStart = lines.FindIndex(line => line.Trim().Equals("[options]", StringComparison.OrdinalIgnoreCase));
        if (sectionStart < 0)
        {
            if (lines.Count > 0 && lines[^1].Length > 0) lines.Add(string.Empty);
            lines.Add("[options]");
            sectionStart = lines.Count - 1;
        }

        foreach (var option in ManagedHostOptions)
        {
            var sectionEnd = FindSectionEnd(lines, sectionStart);
            var pattern = "^\\s*" + Regex.Escape(option.Key) + "\\s*=";
            var index = -1;
            for (var line = sectionStart + 1; line < sectionEnd; line++)
            {
                if (Regex.IsMatch(lines[line], pattern, RegexOptions.IgnoreCase)) { index = line; break; }
            }
            var value = $"{option.Key} = '{option.Value.Replace("'", "''", StringComparison.Ordinal)}'";
            if (index >= 0) lines[index] = value;
            else lines.Insert(sectionEnd, value);
        }

        return string.Join("\r\n", lines) + "\r\n";
    }

    public static bool IsManagedHostHardened(string content) =>
        string.Equals(Normalize(content), Normalize(HardenManagedHost(content)), StringComparison.Ordinal);

    private static void SetTopLevel(List<string> lines, string key, string value)
    {
        var firstSection = lines.FindIndex(line => line.TrimStart().StartsWith("[", StringComparison.Ordinal));
        if (firstSection < 0) firstSection = lines.Count;
        var pattern = "^\\s*" + Regex.Escape(key) + "\\s*=";
        for (var index = 0; index < firstSection; index++)
        {
            if (!Regex.IsMatch(lines[index], pattern, RegexOptions.IgnoreCase)) continue;
            lines[index] = $"{key} = '{value}'";
            return;
        }
        lines.Insert(0, $"{key} = '{value}'");
    }

    private static int FindSectionEnd(List<string> lines, int sectionStart)
    {
        for (var index = sectionStart + 1; index < lines.Count; index++)
            if (lines[index].TrimStart().StartsWith("[", StringComparison.Ordinal)) return index;
        return lines.Count;
    }

    private static string Normalize(string value) => value.Replace("\r\n", "\n").Trim();
}
