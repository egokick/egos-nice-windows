using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using Taildesk.Shared;

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
        // Reboot continuation runs as SYSTEM at startup. Resolve the active
        // interactive session rather than Session 0 so a resumed installer
        // continues to use the same user's known-folder policy.
        if (sessionId == 0)
        {
            var active = WTSGetActiveConsoleSessionId();
            if (active != uint.MaxValue) sessionId = active;
        }
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
            if (!Directory.Exists(full))
            {
                // A missing standard known folder is optional shared content.
                // It remains absent from AgentConfig instead of blocking the
                // device enrollment, regardless of whether Windows normally
                // puts it below the profile root or redirects it elsewhere.
                return full;
            }

            // Windows Known Folders can legitimately be redirected (including
            // OneDrive Files On-Demand). Resolve the final directory object
            // rather than rejecting every reparse point, then enforce the same
            // no-system-root policy used by the SYSTEM Agent.
            var final = ResolveFinalDirectoryTarget(full);
            _ = PathGuard.ValidateRemoteFileRoot(final);
            if (!IsWithin(final, profile) && !IsOwnedBy(final, identifier))
                throw new InvalidDataException(
                    $"The selected user's redirected {valueName} folder is not owned by the interactive user.");
            return final;
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
        if (full.Equals(root, StringComparison.OrdinalIgnoreCase))
            return;
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

    private static bool IsWithin(string path, string root)
    {
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return path.Equals(root, StringComparison.OrdinalIgnoreCase)
               || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOwnedBy(string path, SecurityIdentifier expectedOwner)
    {
        var security = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner);
        return security.GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier owner
               && owner.Equals(expectedOwner);
    }

    private static string ResolveFinalDirectoryTarget(string path)
    {
        const uint fileShareRead = 0x00000001;
        const uint fileShareWrite = 0x00000002;
        const uint fileShareDelete = 0x00000004;
        const uint openExisting = 3;
        const uint fileFlagBackupSemantics = 0x02000000;
        using var handle = CreateFile(
            path,
            0,
            fileShareRead | fileShareWrite | fileShareDelete,
            IntPtr.Zero,
            openExisting,
            fileFlagBackupSemantics,
            IntPtr.Zero);
        if (handle.IsInvalid)
            throw new System.ComponentModel.Win32Exception(
                Marshal.GetLastWin32Error(), "Windows could not resolve the final known-folder target.");

        var length = 512u;
        while (length <= 32 * 1024)
        {
            var buffer = new System.Text.StringBuilder(checked((int)length));
            var written = GetFinalPathNameByHandle(handle, buffer, length, 0);
            if (written == 0)
                throw new System.ComponentModel.Win32Exception(
                    Marshal.GetLastWin32Error(), "Windows could not read the final known-folder target.");
            if (written < length)
            {
                var value = buffer.ToString();
                if (value.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase))
                    value = "\\\\" + value[8..];
                else if (value.StartsWith("\\\\?\\", StringComparison.OrdinalIgnoreCase))
                    value = value[4..];
                return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
            }
            length = checked(written + 1);
        }
        throw new InvalidDataException("The final known-folder target path is unexpectedly long.");
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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle handle,
        System.Text.StringBuilder path,
        uint pathLength,
        uint flags);

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();
}
