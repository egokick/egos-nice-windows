using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace Taildesk.Setup;

public sealed class InteractiveUserProfile
{
    public string Sid { get; init; } = string.Empty;
    public string Desktop { get; init; } = string.Empty;
    public string Documents { get; init; } = string.Empty;
    public string Downloads { get; init; } = string.Empty;
    public string Pictures { get; init; } = string.Empty;
    public string Videos { get; init; } = string.Empty;

    public static InteractiveUserProfile Resolve()
    {
        var sessionId = checked((uint)Process.GetCurrentProcess().SessionId);
        var user = QuerySession(sessionId, WtsInfoClass.UserName);
        var domain = QuerySession(sessionId, WtsInfoClass.DomainName);
        if (string.IsNullOrWhiteSpace(user))
            throw new InvalidOperationException(
                "Setup could not identify the interactive user in its own Windows session.");

        var account = string.IsNullOrWhiteSpace(domain)
            ? user
            : string.Concat(domain, @"\", user);
        var identifier = (SecurityIdentifier)new NTAccount(account)
            .Translate(typeof(SecurityIdentifier));
        var sid = identifier.Value;
        var components = sid.Split('-');
        var localAccount = sid.StartsWith("S-1-5-21-", StringComparison.Ordinal)
                           && identifier.IsAccountSid();
        var cloudAccount = components.Length == 8
                           && components[0] == "S" && components[1] == "1"
                           && components[2] == "12" && components[3] == "1"
                           && components.Skip(4).All(component =>
                               uint.TryParse(
                                   component,
                                   System.Globalization.NumberStyles.None,
                                   System.Globalization.CultureInfo.InvariantCulture,
                                   out _));
        if (!localAccount && !cloudAccount)
            throw new InvalidDataException(
                "Setup supports only a local/domain or Entra interactive user SID.");

        using var profileKey = Registry.LocalMachine.OpenSubKey(
            string.Concat(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\",
                sid),
            writable: false);
        var profileValue = profileKey?.GetValue(
            "ProfileImagePath",
            null,
            RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        if (string.IsNullOrWhiteSpace(profileValue))
            throw new InvalidDataException(
                "Windows did not provide the selected user's trusted ProfileList path.");
        var profile = Path.TrimEndingDirectorySeparator(Path.GetFullPath(
            Environment.ExpandEnvironmentVariables(profileValue)));
        if (!Directory.Exists(profile))
            throw new DirectoryNotFoundException(
                "The selected user's trusted profile directory does not exist.");
        RequireNoReparseTraversal(profile, profile);

        using var shell = Registry.Users.OpenSubKey(
            string.Concat(
                sid,
                @"\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders"),
            writable: false);
        using var userEnvironment = Registry.Users.OpenSubKey(
            string.Concat(sid, @"\Environment"),
            writable: false);

        string Folder(string valueName, string fallback)
        {
            var value = shell?.GetValue(
                valueName,
                null,
                RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            var expanded = string.IsNullOrWhiteSpace(value)
                ? Path.Combine(profile, fallback)
                : value.Replace("%USERPROFILE%", profile, StringComparison.OrdinalIgnoreCase);
            if (userEnvironment is not null)
            {
                foreach (var environmentName in userEnvironment.GetValueNames())
                {
                    if (userEnvironment.GetValue(
                            environmentName,
                            null,
                            RegistryValueOptions.DoNotExpandEnvironmentNames) is string environmentValue)
                        expanded = expanded.Replace(
                            $"%{environmentName}%",
                            environmentValue,
                            StringComparison.OrdinalIgnoreCase);
                }
            }
            expanded = Environment.ExpandEnvironmentVariables(expanded);
            if (expanded.Contains('%'))
                throw new InvalidDataException(
                    $"The selected user's {valueName} path contains an unresolved environment variable.");
            var full = Path.GetFullPath(expanded);
            if (!full.StartsWith(profile + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"The selected user's {valueName} path escaped the trusted ProfileList root.");
            RequireNoReparseTraversal(profile, full);
            return full;
        }

        return new InteractiveUserProfile
        {
            Sid = sid,
            Desktop = Folder("Desktop", "Desktop"),
            Documents = Folder("Personal", "Documents"),
            Downloads = Folder("{374DE290-123F-4565-9164-39C4925E467B}", "Downloads"),
            Pictures = Folder("My Pictures", "Pictures"),
            Videos = Folder("My Video", "Videos")
        };
    }

    private static void RequireNoReparseTraversal(string profile, string target)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(profile));
        var full = Path.GetFullPath(target);
        if (!full.Equals(root, StringComparison.OrdinalIgnoreCase)
            && !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The selected user path escaped its trusted profile.");
        var current = root;
        if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The selected user's ProfileList root is a reparse point.");
        foreach (var component in Path.GetRelativePath(root, full).Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (component is "." or "..")
                throw new InvalidDataException("The selected user path is unsafe.");
            current = Path.Combine(current, component);
            if (!File.Exists(current) && !Directory.Exists(current)) break;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException(
                    "The selected user path contains a reparse point.");
        }
    }

    private static string QuerySession(uint sessionId, WtsInfoClass infoClass)
    {
        if (!WTSQuerySessionInformation(
                IntPtr.Zero, sessionId, infoClass, out var buffer, out _))
            throw new System.ComponentModel.Win32Exception(
                Marshal.GetLastWin32Error(),
                "Windows could not query the interactive Setup session.");
        try { return Marshal.PtrToStringUni(buffer) ?? string.Empty; }
        finally { WTSFreeMemory(buffer); }
    }

    private enum WtsInfoClass
    {
        UserName = 5,
        DomainName = 7
    }

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool WTSQuerySessionInformation(
        IntPtr server,
        uint sessionId,
        WtsInfoClass infoClass,
        out IntPtr buffer,
        out uint bytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);
}
