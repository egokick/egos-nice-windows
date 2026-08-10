using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace Taildesk.Shared;

/// <summary>
/// Creates machine-writable state without a create-then-repair ACL window.
/// Existing objects are validated, never silently repaired: a pre-seeded weak
/// directory or reparse point is a hard installation failure.
/// </summary>
public static class MachineStorageSecurity
{
    private static readonly SecurityIdentifier SystemSid =
        new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier AdministratorsSid =
        new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private const FileSystemRights BootstrapUserRights =
        FileSystemRights.ReadAndExecute | FileSystemRights.Delete;

    public static void EnsureOpticonMachineState()
    {
        // SshAccessDataDirectory is intentionally excluded. Its isolated SSH
        // supervisor owns a stricter SYSTEM-only or SYSTEM-and-daemon ACL,
        // which must not be normalized to this general storage contract.
        EnsureRestrictedDirectoryTree(
            AppPaths.MachineDataDirectory,
            AppPaths.AgentDataDirectory,
            AppPaths.UpdateDataDirectory,
            AppPaths.SshDataDirectory,
            AppPaths.SetupStagingDirectory,
            AppPaths.ControllerHandoffDirectory);
    }

    public static bool IsProtectedMachinePath(string path)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppPaths.MachineDataDirectory));
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return IsWithin(full, root);
    }

    public static void EnsureRestrictedDirectoryTree(string root, params string[] requiredDirectories)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Protected machine storage requires Windows ACLs.");

        var commonData = Path.TrimEndingDirectorySeparator(Path.GetFullPath(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)));
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!string.Equals(Path.GetDirectoryName(fullRoot), commonData, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The protected machine-state root must be a direct child of ProgramData.");
        RejectReparsePoint(commonData, "ProgramData root");
        EnsureRestrictedDirectory(fullRoot);

        foreach (var requested in requiredDirectories)
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(requested));
            if (!IsWithin(full, fullRoot))
                throw new InvalidDataException("A protected machine-state directory escaped its fixed root.");
            var relative = Path.GetRelativePath(fullRoot, full);
            var current = fullRoot;
            foreach (var component in relative.Split(
                         Path.DirectorySeparatorChar,
                         StringSplitOptions.RemoveEmptyEntries))
            {
                if (component is "." or "..")
                    throw new InvalidDataException("A protected machine-state directory has an unsafe path.");
                current = Path.Combine(current, component);
                EnsureRestrictedDirectory(current);
            }
        }
    }

    public static string CreateRestrictedChildDirectory(string parent, string prefix)
    {
        RequireRestrictedDirectory(parent);
        if (string.IsNullOrWhiteSpace(prefix)
            || prefix.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            throw new ArgumentException("The protected staging prefix is invalid.", nameof(prefix));

        for (var attempt = 0; attempt < 16; attempt++)
        {
            var candidate = Path.Combine(parent, prefix + Guid.NewGuid().ToString("N"));
            try
            {
                new DirectoryInfo(candidate).Create(CreateRestrictedDirectorySecurity());
                RequireRestrictedDirectory(candidate);
                return candidate;
            }
            catch (IOException) when (Directory.Exists(candidate) || File.Exists(candidate))
            {
                // A cryptographically random collision is harmless; never adopt it.
            }
        }
        throw new IOException("A unique protected staging directory could not be created.");
    }

    public static void RequireRestrictedDirectory(string path)
    {
        var full = Path.GetFullPath(path);
        if (File.Exists(full))
            throw new InvalidDataException($"The protected directory path is a file: {full}");
        if (!Directory.Exists(full))
            throw new DirectoryNotFoundException($"The protected directory is missing: {full}");
        RejectReparsePoint(full, "protected directory");
        RequireExactMachineAcl(new DirectoryInfo(full).GetAccessControl(
            AccessControlSections.Owner | AccessControlSections.Access), isDirectory: true);
    }

    public static void RequireRestrictedFileIfExists(string path)
    {
        if (!File.Exists(path))
        {
            if (Directory.Exists(path))
                throw new InvalidDataException($"The protected file path is a directory: {path}");
            return;
        }
        RequireRestrictedFile(path);
    }

    public static void RequireRestrictedFile(string path)
    {
        var full = Path.GetFullPath(path);
        var attributes = File.GetAttributes(full);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new InvalidDataException($"The protected file is not a regular file: {full}");
        RequireExactMachineAcl(new FileInfo(full).GetAccessControl(
            AccessControlSections.Owner | AccessControlSections.Access), isDirectory: false);
    }

    public static byte[] ReadRestrictedFile(string path, int maximumBytes)
    {
        if (maximumBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        var full = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(full)
                     ?? throw new InvalidOperationException("The protected file has no parent directory.");
        RequireRestrictedDirectory(parent);
        RequireRestrictedFile(full);
        using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 || stream.Length > maximumBytes)
            throw new InvalidDataException("The protected machine file has an invalid size.");
        var content = new byte[checked((int)stream.Length)];
        stream.ReadExactly(content);
        return content;
    }

    public static void DeleteRestrictedFileIfExists(string path)
    {
        var full = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(full)
                     ?? throw new InvalidOperationException("The protected file has no parent directory.");
        RequireRestrictedDirectory(parent);
        if (!File.Exists(full))
        {
            if (Directory.Exists(full))
                throw new InvalidDataException("The protected file path is a directory.");
            return;
        }
        RequireRestrictedFile(full);
        File.Delete(full);
    }

    public static async Task WriteRestrictedFileAtomicAsync(
        string path,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken = default)
    {
        var full = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(full)
                     ?? throw new InvalidOperationException("The protected file has no parent directory.");
        RequireRestrictedDirectory(parent);
        RequireRestrictedFileIfExists(full);

        var temporary = Path.Combine(parent, "." + Path.GetFileName(full) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(content, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            new FileInfo(temporary).SetAccessControl(CreateRestrictedFileSecurity());
            RequireRestrictedFile(temporary);
            File.Move(temporary, full, overwrite: true);
            RequireRestrictedFile(full);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    public static async Task<bool> WriteRestrictedFileCreateNewAsync(
        string path,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken = default)
    {
        var full = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(full)
                     ?? throw new InvalidOperationException("The protected file has no parent directory.");
        RequireRestrictedDirectory(parent);
        if (File.Exists(full))
        {
            RequireRestrictedFile(full);
            return false;
        }
        if (Directory.Exists(full))
            throw new InvalidDataException("The protected file path is a directory.");
        var completed = Path.Combine(
            parent,
            "." + Path.GetFileName(full) + "." + Guid.NewGuid().ToString("N") + ".completed");
        try
        {
            await WriteRestrictedFileAtomicAsync(completed, content, cancellationToken);
            try
            {
                File.Move(completed, full, overwrite: false);
                RequireRestrictedFile(full);
                return true;
            }
            catch (IOException) when (File.Exists(full))
            {
                RequireRestrictedFile(full);
                return false;
            }
        }
        finally
        {
            try { DeleteRestrictedFileIfExists(completed); } catch { }
        }
    }

    public static void SealRestrictedFile(string path)
    {
        var full = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(full)
                     ?? throw new InvalidOperationException("The protected file has no parent directory.");
        RequireRestrictedDirectory(parent);
        var attributes = File.GetAttributes(full);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new InvalidDataException("Only a regular file can be sealed for elevated execution.");
        new FileInfo(full).SetAccessControl(CreateRestrictedFileSecurity());
        RequireRestrictedFile(full);
    }

    public static async Task WriteUserBootstrapAsync(
        string path,
        ReadOnlyMemory<byte> content,
        string userSid,
        CancellationToken cancellationToken = default)
    {
        var sid = RequireAccountSid(userSid);
        var full = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(full)
                     ?? throw new InvalidOperationException("The controller bootstrap has no parent directory.");
        if (!string.Equals(full, Path.GetFullPath(AppPaths.ControllerBootstrapFile), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The controller bootstrap path is not the fixed protected handoff path.");
        RequireRestrictedDirectory(parent);
        if (File.Exists(full) || Directory.Exists(full))
            throw new InvalidOperationException("An unconsumed controller bootstrap already exists.");

        var completed = Path.Combine(parent, ".bootstrap." + Guid.NewGuid().ToString("N") + ".completed");
        try
        {
            await WriteRestrictedFileAtomicAsync(completed, content, cancellationToken);
            new FileInfo(completed).SetAccessControl(CreateUserBootstrapSecurity(sid));
            RequireUserBootstrap(completed, sid, requireFixedPath: false);
            File.Move(completed, full, overwrite: false);
            RequireUserBootstrap(full, sid, requireFixedPath: true);
        }
        finally
        {
            try { if (File.Exists(completed)) File.Delete(completed); } catch { }
        }
    }

    public static byte[] ReadUserBootstrap(string path, string userSid, int maximumBytes)
    {
        var sid = RequireAccountSid(userSid);
        var full = Path.GetFullPath(path);
        if (!string.Equals(full, Path.GetFullPath(AppPaths.ControllerBootstrapFile), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The controller bootstrap path is not the fixed protected handoff path.");
        RequireUserBootstrap(full, sid, requireFixedPath: true);
        using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.None);
        if (stream.Length is <= 0 || stream.Length > maximumBytes)
            throw new InvalidDataException("The controller bootstrap has an invalid size.");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }

    public static void DeleteUserBootstrap(string path, string userSid)
    {
        var sid = RequireAccountSid(userSid);
        var full = Path.GetFullPath(path);
        if (!string.Equals(full, Path.GetFullPath(AppPaths.ControllerBootstrapFile), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The controller bootstrap path is not the fixed protected handoff path.");
        RequireUserBootstrap(full, sid, requireFixedPath: true);
        File.Delete(full);
    }

    public static void DeleteRestrictedDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            if (File.Exists(path))
                throw new InvalidDataException("The protected directory path is a file.");
            return;
        }
        RequireRestrictedDirectory(path);
        RejectReparseTree(path);
        Directory.Delete(path, recursive: true);
    }

    private static void EnsureRestrictedDirectory(string path)
    {
        var directory = new DirectoryInfo(path);
        if (!directory.Exists)
        {
            if (File.Exists(path))
                throw new InvalidDataException($"The protected directory path is a file: {path}");
            try
            {
                directory.Create(CreateRestrictedDirectorySecurity());
            }
            catch (IOException) when (Directory.Exists(path))
            {
                // A competing creator won. Its object must pass exact validation.
            }
        }
        RequireRestrictedDirectory(path);
    }

    private static void RequireUserBootstrap(
        string path,
        SecurityIdentifier userSid,
        bool requireFixedPath)
    {
        var full = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(full)
                      ?? throw new InvalidOperationException("The controller bootstrap has no parent directory.");
        if (!string.Equals(Path.GetFullPath(parent), Path.GetFullPath(AppPaths.ControllerHandoffDirectory),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The controller bootstrap escaped its fixed protected handoff directory.");
        if (requireFixedPath
            && !string.Equals(full, Path.GetFullPath(AppPaths.ControllerBootstrapFile), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The controller bootstrap path is not the fixed protected handoff path.");
        var attributes = File.GetAttributes(full);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new InvalidDataException("The controller bootstrap is not a regular file.");
        var security = new FileInfo(full).GetAccessControl(
            AccessControlSections.Owner | AccessControlSections.Access);
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner is null || (!owner.Equals(SystemSid) && !owner.Equals(AdministratorsSid)))
            throw new UnauthorizedAccessException("The controller bootstrap owner is not SYSTEM or Administrators.");
        if (!security.AreAccessRulesProtected)
            throw new UnauthorizedAccessException("The controller bootstrap inherits unsafe permissions.");
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .OfType<FileSystemAccessRule>().ToArray();
        if (rules.Length != 3
            || rules.Any(rule => rule.IsInherited || rule.AccessControlType != AccessControlType.Allow)
            || !HasExactRule(rules, SystemSid, FileSystemRights.FullControl, InheritanceFlags.None)
            || !HasExactRule(rules, AdministratorsSid, FileSystemRights.FullControl, InheritanceFlags.None)
            || !HasExactRule(rules, userSid, BootstrapUserRights, InheritanceFlags.None))
            throw new UnauthorizedAccessException("The controller bootstrap ACL is not the exact protected handoff ACL.");
    }

    private static void RequireExactMachineAcl(FileSystemSecurity security, bool isDirectory)
    {
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner is null || (!owner.Equals(SystemSid) && !owner.Equals(AdministratorsSid)))
            throw new UnauthorizedAccessException("The protected machine object owner is not SYSTEM or Administrators.");
        if (!security.AreAccessRulesProtected)
            throw new UnauthorizedAccessException("The protected machine object inherits unsafe permissions.");
        var inheritance = isDirectory
            ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit
            : InheritanceFlags.None;
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .OfType<FileSystemAccessRule>().ToArray();
        if (rules.Length != 2
            || rules.Any(rule => rule.IsInherited || rule.AccessControlType != AccessControlType.Allow)
            || !HasExactRule(rules, SystemSid, FileSystemRights.FullControl, inheritance)
            || !HasExactRule(rules, AdministratorsSid, FileSystemRights.FullControl, inheritance))
            throw new UnauthorizedAccessException("The protected machine object ACL is not exactly SYSTEM and Administrators full control.");
    }

    private static bool HasExactRule(
        IEnumerable<FileSystemAccessRule> rules,
        SecurityIdentifier sid,
        FileSystemRights rights,
        InheritanceFlags inheritance) =>
        rules.Any(rule => rule.IdentityReference.Equals(sid)
                          && rule.FileSystemRights == rights
                          && rule.InheritanceFlags == inheritance
                          && rule.PropagationFlags == PropagationFlags.None);

    private static DirectorySecurity CreateRestrictedDirectorySecurity()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(AdministratorsSid);
        var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(
            SystemSid, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            AdministratorsSid, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    private static FileSecurity CreateRestrictedFileSecurity()
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(AdministratorsSid);
        security.AddAccessRule(new FileSystemAccessRule(SystemSid, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(AdministratorsSid, FileSystemRights.FullControl, AccessControlType.Allow));
        return security;
    }

    private static FileSecurity CreateUserBootstrapSecurity(SecurityIdentifier userSid)
    {
        var security = CreateRestrictedFileSecurity();
        security.AddAccessRule(new FileSystemAccessRule(userSid, BootstrapUserRights, AccessControlType.Allow));
        return security;
    }

    private static SecurityIdentifier RequireAccountSid(string value)
    {
        var sid = new SecurityIdentifier(value);
        var components = sid.Value.Split('-');
        var localAccount = sid.Value.StartsWith("S-1-5-21-", StringComparison.Ordinal) && sid.IsAccountSid();
        var cloudAccount = components.Length == 8
                           && components[0] == "S" && components[1] == "1"
                           && components[2] == "12" && components[3] == "1"
                           && components.Skip(4).All(component =>
                               uint.TryParse(component, System.Globalization.NumberStyles.None,
                                   System.Globalization.CultureInfo.InvariantCulture, out _));
        if (!localAccount && !cloudAccount)
            throw new InvalidDataException("The interactive controller user SID is not a supported local or cloud user SID.");
        return sid;
    }

    private static void RejectReparsePoint(string path, string description)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"The {description} is a reparse point: {path}");
    }

    private static void RejectReparseTree(string path)
    {
        var pending = new Stack<string>();
        pending.Push(path);
        while (pending.TryPop(out var directory))
        {
            RejectReparsePoint(directory, "protected machine-state tree");
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException($"The protected machine-state tree contains a reparse point: {entry}");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
            }
        }
    }

    private static bool IsWithin(string path, string root) =>
        path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}

public sealed class MachineJsonFileStore<T> where T : new()
{
    private const int MaximumJsonBytes = 4 * 1024 * 1024;
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public MachineJsonFileStore(string path)
    {
        _path = Path.GetFullPath(path);
        if (!MachineStorageSecurity.IsProtectedMachinePath(_path))
            throw new InvalidDataException("The machine JSON path escaped the protected machine-state root.");
    }

    public async Task<T> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var parent = Path.GetDirectoryName(_path)
                         ?? throw new InvalidOperationException("The machine JSON file has no parent directory.");
            MachineStorageSecurity.RequireRestrictedDirectory(parent);
            if (!File.Exists(_path))
            {
                if (Directory.Exists(_path))
                    throw new InvalidDataException("The machine JSON path is a directory.");
                return new T();
            }
            var content = MachineStorageSecurity.ReadRestrictedFile(_path, MaximumJsonBytes);
            return JsonSerializer.Deserialize<T>(content, JsonDefaults.Options)
                   ?? throw new InvalidDataException("The protected machine JSON file is empty.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(T value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        var content = JsonSerializer.SerializeToUtf8Bytes(value, JsonDefaults.Options);
        if (content.Length is <= 0 or > MaximumJsonBytes)
            throw new InvalidDataException("The protected machine JSON payload has an invalid size.");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var parent = Path.GetDirectoryName(_path)
                         ?? throw new InvalidOperationException("The machine JSON file has no parent directory.");
            MachineStorageSecurity.RequireRestrictedDirectory(parent);
            await MachineStorageSecurity.WriteRestrictedFileAtomicAsync(_path, content, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }
}
