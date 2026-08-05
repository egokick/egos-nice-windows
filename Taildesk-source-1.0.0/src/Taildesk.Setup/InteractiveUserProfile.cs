using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace Taildesk.Setup;

public sealed class InteractiveUserProfile
{
    public string AccountName { get; init; } = Environment.UserName;
    public string ProfilePath { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public string LocalAppData { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    public string Desktop { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    public string Startup { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
    public string Programs { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
    public string Documents { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    public string Downloads { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    public string Pictures { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
    public string Videos { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);

    public static InteractiveUserProfile Resolve()
    {
        try
        {
            var session = WTSGetActiveConsoleSessionId();
            var user = QuerySession(session, WtsInfoClass.UserName);
            var domain = QuerySession(session, WtsInfoClass.DomainName);
            if (string.IsNullOrWhiteSpace(user)) return new InteractiveUserProfile();

            var account = string.IsNullOrWhiteSpace(domain) ? user : $"{domain}\\{user}";
            var sid = ((SecurityIdentifier)new NTAccount(account).Translate(typeof(SecurityIdentifier))).Value;
            using var profileKey = Registry.LocalMachine.OpenSubKey($"SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\ProfileList\\{sid}");
            var profileValue = profileKey?.GetValue("ProfileImagePath") as string;
            if (string.IsNullOrWhiteSpace(profileValue)) return new InteractiveUserProfile();
            var profile = Environment.ExpandEnvironmentVariables(profileValue);

            using var shell = Registry.Users.OpenSubKey($"{sid}\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\User Shell Folders");
            using var userEnvironment = Registry.Users.OpenSubKey($"{sid}\\Environment");
            string Folder(string valueName, string fallback)
            {
                var value = shell?.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                if (string.IsNullOrWhiteSpace(value)) return Path.Combine(profile, fallback);
                var expanded = value.Replace("%USERPROFILE%", profile, StringComparison.OrdinalIgnoreCase);
                if (userEnvironment is not null)
                {
                    foreach (var environmentName in userEnvironment.GetValueNames())
                    {
                        if (userEnvironment.GetValue(environmentName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string environmentValue)
                        {
                            expanded = expanded.Replace($"%{environmentName}%", environmentValue, StringComparison.OrdinalIgnoreCase);
                        }
                    }
                }
                expanded = Environment.ExpandEnvironmentVariables(expanded);
                return expanded.Contains('%') ? Path.Combine(profile, fallback) : expanded;
            }

            return new InteractiveUserProfile
            {
                AccountName = account,
                ProfilePath = profile,
                LocalAppData = Folder("Local AppData", "AppData\\Local"),
                Desktop = Folder("Desktop", "Desktop"),
                Startup = Folder("Startup", "AppData\\Roaming\\Microsoft\\Windows\\Start Menu\\Programs\\Startup"),
                Programs = Folder("Programs", "AppData\\Roaming\\Microsoft\\Windows\\Start Menu\\Programs"),
                Documents = Folder("Personal", "Documents"),
                Downloads = Folder("{374DE290-123F-4565-9164-39C4925E467B}", "Downloads"),
                Pictures = Folder("My Pictures", "Pictures"),
                Videos = Folder("My Video", "Videos")
            };
        }
        catch
        {
            return new InteractiveUserProfile();
        }
    }

    private static string QuerySession(uint sessionId, WtsInfoClass infoClass)
    {
        if (!WTSQuerySessionInformation(IntPtr.Zero, sessionId, infoClass, out var buffer, out _)) return string.Empty;
        try { return Marshal.PtrToStringUni(buffer) ?? string.Empty; }
        finally { WTSFreeMemory(buffer); }
    }

    private enum WtsInfoClass
    {
        UserName = 5,
        DomainName = 7
    }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

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
