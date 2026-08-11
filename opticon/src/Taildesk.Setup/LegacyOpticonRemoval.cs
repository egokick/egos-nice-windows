using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Security.AccessControl;
using System.Security.Principal;
using Taildesk.Shared;

namespace Taildesk.Setup;

/// <summary>
/// Removes any existing Opticon generation from its two fixed product roots
/// before a source-only clean install. The verified, device-bound invitation
/// authorizes replacement; cleanup remains handle-bound and fail-closed for
/// links, junctions, path swaps, or objects that cannot be pinned safely.
/// </summary>
internal static class LegacyOpticonRemoval
{
    // Handles stay open for every validated node until handle-based deletion,
    // so keep this deliberately low rather than exhausting the machine on a
    // corrupted existing tree.
    private const int MaximumTreeEntries = 4_096;
    private const uint GenericRead = 0x80000000;
    private const uint ReadControl = 0x00020000;
    private const uint WriteDac = 0x00040000;
    private const uint WriteOwner = 0x00080000;
    private const uint Delete = 0x00010000;
    private const uint FileListDirectory = 0x00000001;
    private const uint FileReadAttributes = 0x00000080;
    private const uint Synchronize = 0x00100000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeReparsePoint = 0x00000400;
    private const uint SeFileObject = 1;
    private const uint OwnerSecurityInformation = 0x00000001;
    private const uint DaclSecurityInformation = 0x00000004;
    private const uint ProtectedDaclSecurityInformation = 0x80000000;
    private const int FileDispositionInformationClass = 4;
    // Opticon releases registered components through these fixed task names;
    // they did not install an Opticon Windows service. Deliberately do not
    // enumerate or mutate generic Windows services during cleanup.
    private static readonly string[] TaskNames =
    [
        RemoteAdministrationProtocol.GuardianWatchdogTaskName,
        RemoteAdministrationProtocol.GuardianTaskName,
        RemoteAdministrationProtocol.SshSupervisorTaskName,
        RemoteAdministrationProtocol.AgentTaskName,
        "Taildesk Fly Route",
        "Opticon Command Center"
    ];

    /// <summary>
    /// Runs only from the verified source launcher, after that launcher has
    /// matched the signed source archive.  No deletion begins until every
    /// present task and both fixed roots have passed preflight.
    /// </summary>
    internal static async Task RemoveLegacyInstallationIfPresentAsync(Action<string> report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var plan = await CreatePlanAsync();
        if (plan is null) return;

        report("An existing Opticon installation was detected. The verified invitation authorizes its automatic replacement.");

        // Re-run every non-mutating proof immediately before cleanup. A changed
        // task/root is a hard stop, not authorization to remove whatever now
        // occupies a familiar name.
        plan = await CreatePlanAsync()
               ?? throw new InvalidOperationException("The existing Opticon installation disappeared before removal.");

        report("Stopping Opticon scheduled tasks at their fixed names...");
        foreach (var task in plan.Tasks)
            await EndTaskAsync(task.Name);
        foreach (var task in plan.Tasks)
            await DeleteTaskAsync(task.Name);

        report("Stopping processes running from Opticon's fixed installation root...");
        await StopValidatedProcessesAsync(plan.InstallDirectory);

        report("Removing the existing Opticon installation and machine state...");
        var sealedRoots = new List<PinnedDirectoryTree>();
        try
        {
            // The 1.1.38 tree can have a weak inherited ACL. Each root and
            // every descendant directory is opened without following a
            // reparse point and sealed through that open handle. The root
            // handles remain open without FILE_SHARE_DELETE until their empty
            // roots are deleted through those same handles, so an
            // unprivileged process cannot swap a checked tree for a junction
            // between validation and deletion.
            foreach (var directory in plan.DirectoriesToRemove)
            {
                var root = SealDirectoryTreeForDeletion(directory);
                sealedRoots.Add(root);
            }
            foreach (var root in sealedRoots) root.DeleteAllPinnedEntries();
        }
        finally { foreach (var root in sealedRoots) root.Dispose(); }

        foreach (var directory in plan.DirectoriesToRemove)
        {
            if (Directory.Exists(directory) || File.Exists(directory))
                throw new IOException("The existing Opticon directory could not be removed completely.");
        }

        report("The existing Opticon installation was removed. Tailscale and RustDesk were left unchanged.");
    }

    private static async Task<RemovalPlan?> CreatePlanAsync()
    {
        var installDirectory = RequireDirectChild(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Taildesk");
        var machineDataDirectory = RequireDirectChild(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Taildesk");
        var installPresent = RequireDirectoryOrAbsent(installDirectory, "Opticon installation");
        var dataPresent = RequireDirectoryOrAbsent(machineDataDirectory, "Opticon machine state");

        // Fixed Opticon task names are removed regardless of the executable or
        // version currently recorded in their action. This is an explicit clean
        // uninstall, not an attempt to adopt or trust their prior configuration.
        var validatedTasks = new List<ValidatedTask>();
        foreach (var taskName in TaskNames)
        {
            var task = await QueryTaskIfPresentAsync(taskName);
            if (task is not null) validatedTasks.Add(task);
        }

        if (!installPresent && !dataPresent && validatedTasks.Count == 0)
            return null;

        var directories = new List<string>();
        if (installPresent)
        {
            RequireRegularDirectoryTree(installDirectory);
            directories.Add(installDirectory);
        }
        if (dataPresent)
        {
            RequireRegularDirectoryTree(machineDataDirectory);
            directories.Add(machineDataDirectory);
        }

        return new RemovalPlan(installDirectory, directories, validatedTasks);
    }

    private static string RequireDirectChild(string parent, string childName)
    {
        var canonicalParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        if (!Directory.Exists(canonicalParent)
            || (File.GetAttributes(canonicalParent) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The fixed Windows installation root is unavailable or linked.");
        if (string.IsNullOrWhiteSpace(childName) || Path.GetFileName(childName) != childName)
            throw new InvalidDataException("The existing Opticon root name is invalid.");
        var result = Path.GetFullPath(Path.Combine(canonicalParent, childName));
        if (!string.Equals(Path.GetDirectoryName(result), canonicalParent, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The existing Opticon root escaped its fixed Windows parent.");
        return result;
    }

    private static bool RequireDirectoryOrAbsent(string path, string description)
    {
        if (Directory.Exists(path))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"The {description} root is a link or junction.");
            return true;
        }
        if (File.Exists(path))
            throw new InvalidDataException($"The {description} root is a file rather than a directory.");
        return false;
    }

    /// <summary>
    /// Pins every existing Opticon node by handle before deletion. Each handle
    /// is opened with FILE_FLAG_OPEN_REPARSE_POINT and without FILE_SHARE_DELETE.
    /// Directories are then sealed top-down through their own handles. Nothing
    /// is deleted through a path: regular files and empty directories are
    /// removed bottom-up through the exact handles we validated.
    /// </summary>
    private static PinnedDirectoryTree SealDirectoryTreeForDeletion(string directory)
    {
        var tree = new PinnedDirectoryTree();
        try
        {
            var root = OpenPinnedEntry(directory, directory: true, depth: 0);
            SealDirectoryDacl(root.Handle);
            RequirePathStillHasIdentity(root);
            tree.Add(root);
            PinDirectoryChildren(tree, root);
            return tree;
        }
        catch
        {
            tree.Dispose();
            throw;
        }
    }

    private static void PinDirectoryChildren(PinnedDirectoryTree tree, PinnedEntry parent)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(parent.Path, "*", SearchOption.TopDirectoryOnly))
        {
            if (tree.Count >= MaximumTreeEntries)
                throw new InvalidDataException("Opticon cleanup refused an unexpectedly large directory tree.");
            // The parent has already been sealed and held without share-delete,
            // so this name cannot be swapped by an ordinary unprivileged
            // process before its own no-reparse handle is acquired.
            var entry = OpenPinnedEntry(path, directory: null, depth: parent.Depth + 1);
            try
            {
                if (entry.IsDirectory)
                {
                    // A file does not need its ACL changed. Reopen a verified
                    // directory with the additional WRITE_DAC/WRITE_OWNER
                    // rights required to seal it through that handle.
                    entry.Dispose();
                    entry = OpenPinnedEntry(path, directory: true, depth: parent.Depth + 1);
                    SealDirectoryDacl(entry.Handle);
                    RequirePathStillHasIdentity(entry);
                    tree.Add(entry);
                    PinDirectoryChildren(tree, entry);
                }
                else
                {
                    tree.Add(entry);
                }
            }
            catch
            {
                entry.Dispose();
                throw;
            }
        }
    }

    private static PinnedEntry OpenPinnedEntry(string path, bool? directory, int depth)
    {
        var desiredAccess = GenericRead | ReadControl | Delete | FileReadAttributes | Synchronize;
        if (directory == true)
            desiredAccess |= WriteDac | WriteOwner | FileListDirectory;
        var handle = CreateFile(
            path,
            desiredAccess,
            // Deliberately omit FILE_SHARE_DELETE.  The handle remains open
            // until this exact node is removed through it.
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException($"Could not open an existing Opticon path safely (Win32 error {error}).");
        }
        try
        {
            var information = ReadPinnedInformation(handle);
            var isDirectory = (information.FileAttributes & FileAttributeDirectory) != 0;
            if (directory.HasValue && directory.Value != isDirectory)
                throw new InvalidDataException("An existing Opticon cleanup target changed type during validation.");
            return new PinnedEntry(
                path,
                handle,
                new DirectoryIdentity(
                    information.VolumeSerialNumber,
                    ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow),
                isDirectory,
                depth);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static void RequirePathStillHasIdentity(PinnedEntry expected)
    {
        // The retained pinned handle requests DELETE while deliberately
        // withholding FILE_SHARE_DELETE. Reopening through OpenPinnedEntry
        // therefore conflicts with our own handle. The observation handle
        // requests only attributes and shares delete so it is compatible with
        // the retained handle, while that retained handle still prevents any
        // third party from opening the path for deletion or replacement.
        using var observed = CreateFile(
            expected.Path,
            FileReadAttributes | Synchronize,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (observed.IsInvalid)
            throw new IOException(
                $"Could not re-observe a pinned Opticon path safely (Win32 error {Marshal.GetLastWin32Error()}).");
        var information = ReadPinnedInformation(observed);
        var isDirectory = (information.FileAttributes & FileAttributeDirectory) != 0;
        var identity = new DirectoryIdentity(
            information.VolumeSerialNumber,
            ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow);
        if (isDirectory != expected.IsDirectory || identity != expected.Identity)
            throw new InvalidDataException("An existing Opticon directory changed while its cleanup boundary was being established.");
    }

    private static ByHandleFileInformation ReadPinnedInformation(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var information))
            throw new IOException($"Could not inspect an existing Opticon handle (Win32 error {Marshal.GetLastWin32Error()}).");
        if ((information.FileAttributes & FileAttributeReparsePoint) != 0)
            throw new InvalidDataException("Opticon cleanup refuses a link or junction.");
        return information;
    }

    private static void SealDirectoryDacl(SafeFileHandle handle)
    {
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var inheritance = AceFlags.ContainerInherit | AceFlags.ObjectInherit;
        var dacl = new RawAcl(revision: 2, capacity: 2);
        dacl.InsertAce(0, new CommonAce(inheritance, AceQualifier.AccessAllowed,
            (int)FileSystemRights.FullControl, system, isCallback: false, opaque: null));
        dacl.InsertAce(1, new CommonAce(inheritance, AceQualifier.AccessAllowed,
            (int)FileSystemRights.FullControl, administrators, isCallback: false, opaque: null));
        var daclBytes = new byte[dacl.BinaryLength];
        dacl.GetBinaryForm(daclBytes, 0);
        var ownerBytes = new byte[administrators.BinaryLength];
        administrators.GetBinaryForm(ownerBytes, 0);
        var daclBuffer = Marshal.AllocHGlobal(daclBytes.Length);
        var ownerBuffer = Marshal.AllocHGlobal(ownerBytes.Length);
        try
        {
            Marshal.Copy(daclBytes, 0, daclBuffer, daclBytes.Length);
            Marshal.Copy(ownerBytes, 0, ownerBuffer, ownerBytes.Length);
            var result = SetSecurityInfo(
                handle,
                SeFileObject,
                OwnerSecurityInformation | DaclSecurityInformation | ProtectedDaclSecurityInformation,
                ownerBuffer,
                IntPtr.Zero,
                daclBuffer,
                IntPtr.Zero);
            if (result != 0)
                throw new UnauthorizedAccessException(
                    $"Could not seal the existing Opticon directory ACL (Win32 error {result}).");
        }
        finally
        {
            Marshal.FreeHGlobal(ownerBuffer);
            Marshal.FreeHGlobal(daclBuffer);
        }
    }

    private static void MarkPinnedEntryForDeletion(SafeFileHandle handle)
    {
        var disposition = new FileDispositionInformation { DeleteFile = true };
        if (!SetFileInformationByHandle(
                handle,
                FileDispositionInformationClass,
                ref disposition,
                Marshal.SizeOf<FileDispositionInformation>()))
            throw new IOException(
                $"Could not delete a pinned Opticon entry (Win32 error {Marshal.GetLastWin32Error()}).");
    }

    private static void RequireRegularDirectoryTree(string directory)
    {
        if (!Directory.Exists(directory)
            || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("An existing Opticon removal target is missing or linked.");

        var pending = new Stack<string>();
        pending.Push(directory);
        var entries = 0;
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(current, "*", SearchOption.TopDirectoryOnly))
            {
                if (++entries > MaximumTreeEntries)
                    throw new InvalidDataException("Opticon cleanup refused an unexpectedly large directory tree.");
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Opticon cleanup refuses to traverse a link or junction.");
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                }
                else if (!File.Exists(entry))
                {
                    throw new InvalidDataException("An existing Opticon removal target changed during validation.");
                }
            }
        }
    }

    private static async Task<ValidatedTask?> QueryTaskIfPresentAsync(string taskName)
    {
        var result = await RunSchtasksAsync(["/Query", "/TN", taskName]);
        if (!result.Succeeded)
        {
            if (IsTaskAbsent(result)) return null;
            throw new InvalidDataException(
                $"Could not determine whether scheduled task '{taskName}' is present. No files were removed: {DescribeResult(result)}");
        }
        return new ValidatedTask(taskName);
    }

    private static async Task EndTaskAsync(string taskName)
    {
        var result = await RunSchtasksAsync(["/End", "/TN", taskName]);
        if (result.Succeeded || IsTaskInactive(result)) return;
        throw new InvalidOperationException($"Could not stop validated Opticon task '{taskName}': {DescribeResult(result)}");
    }

    private static async Task DeleteTaskAsync(string taskName)
    {
        var result = await RunSchtasksAsync(["/Delete", "/TN", taskName, "/F"]);
        if (!result.Succeeded)
            throw new InvalidOperationException($"Could not delete validated Opticon task '{taskName}': {DescribeResult(result)}");
    }

    private static async Task<ProcessResult> RunSchtasksAsync(IEnumerable<string> arguments)
    {
        var executable = Path.GetFullPath(Path.Combine(Environment.SystemDirectory, "schtasks.exe"));
        if (!File.Exists(executable) || (File.GetAttributes(executable) & FileAttributes.ReparsePoint) != 0)
            throw new FileNotFoundException("The fixed Windows scheduled-task tool is unavailable.", executable);
        return await ProcessRunner.RunAsync(executable, arguments, TimeSpan.FromSeconds(20),
            environment: BuildSystemToolEnvironment(), clearEnvironment: true);
    }

    private static IReadOnlyDictionary<string, string?> BuildSystemToolEnvironment()
    {
        var systemRoot = Path.GetFullPath(Path.Combine(Environment.SystemDirectory, ".."));
        return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["SystemRoot"] = systemRoot,
            ["WINDIR"] = systemRoot,
            ["COMSPEC"] = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            ["PATH"] = Environment.SystemDirectory,
            ["PATHEXT"] = ".COM;.EXE;.BAT;.CMD"
        };
    }

    private static bool IsTaskAbsent(ProcessResult result)
    {
        var text = result.StandardOutput + "\n" + result.StandardError;
        return result.ExitCode == 1 && text.Contains("cannot find", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTaskInactive(ProcessResult result)
    {
        var text = result.StandardOutput + "\n" + result.StandardError;
        return result.ExitCode != 0 &&
               (text.Contains("not running", StringComparison.OrdinalIgnoreCase)
                || text.Contains("not currently running", StringComparison.OrdinalIgnoreCase)
                || text.Contains("no running instance", StringComparison.OrdinalIgnoreCase));
    }

    private static string DescribeResult(ProcessResult result)
    {
        var text = (result.StandardError + " " + result.StandardOutput).Trim();
        return string.IsNullOrWhiteSpace(text)
            ? "the Windows task tool returned exit code " + result.ExitCode
            : text.Length <= 512 ? text : text[..512];
    }

    private static async Task StopValidatedProcessesAsync(string installDirectory)
    {
        if (!Directory.Exists(installDirectory)) return;
        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installDirectory))
                            + Path.DirectorySeparatorChar;
        var expectedNames = Directory.EnumerateFiles(installDirectory, "*.exe", SearchOption.AllDirectories)
            .Select(Path.GetFileNameWithoutExtension)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (!expectedNames.Contains(process.ProcessName)) continue;
                string image;
                try { image = Path.GetFullPath(process.MainModule?.FileName ?? string.Empty); }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    throw new InvalidDataException(
                        $"Could not prove the path of a running process named '{process.ProcessName}'. No Opticon files were removed.", exception);
                }
                if (!image.StartsWith(canonicalRoot, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
                {
                    throw new InvalidOperationException(
                        $"Could not stop the existing Opticon process '{Path.GetFileName(image)}'.", exception);
                }
            }
        }
    }

    internal sealed record RemovalPlan(
        string InstallDirectory,
        IReadOnlyList<string> DirectoriesToRemove,
        IReadOnlyList<ValidatedTask> Tasks)
    {
        internal IReadOnlyList<string> TaskNames => Tasks.Select(task => task.Name).ToArray();
    }

    internal sealed record ValidatedTask(string Name);

    private sealed class PinnedDirectoryTree : IDisposable
    {
        private readonly List<PinnedEntry> _entries = [];

        internal int Count => _entries.Count;

        internal void Add(PinnedEntry entry) => _entries.Add(entry);

        internal void DeleteAllPinnedEntries()
        {
            try
            {
                // File handles are released first so every parent directory is
                // empty before its own exact handle is marked for deletion.
                foreach (var file in _entries.Where(entry => !entry.IsDirectory))
                    DeleteAndDispose(file);
                foreach (var directory in _entries.Where(entry => entry.IsDirectory)
                             .OrderByDescending(entry => entry.Depth))
                    DeleteAndDispose(directory);
            }
            finally
            {
                // A failed disposition may leave part of the existing product in
                // place, but never opens a path-based fallback deletion route.
                Dispose();
            }
        }

        private static void DeleteAndDispose(PinnedEntry entry)
        {
            MarkPinnedEntryForDeletion(entry.Handle);
            entry.Dispose();
        }

        public void Dispose()
        {
            foreach (var entry in _entries) entry.Dispose();
        }
    }

    private sealed class PinnedEntry : IDisposable
    {
        internal PinnedEntry(
            string path,
            SafeFileHandle handle,
            DirectoryIdentity identity,
            bool isDirectory,
            int depth)
        {
            Path = path;
            Handle = handle;
            Identity = identity;
            IsDirectory = isDirectory;
            Depth = depth;
        }

        internal string Path { get; }
        internal SafeFileHandle Handle { get; }
        internal DirectoryIdentity Identity { get; }
        internal bool IsDirectory { get; }
        internal int Depth { get; }

        public void Dispose() => Handle.Dispose();
    }

    private readonly record struct DirectoryIdentity(uint VolumeSerialNumber, ulong FileIndex);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        internal uint FileAttributes;
        internal uint CreationTimeLow;
        internal uint CreationTimeHigh;
        internal uint LastAccessTimeLow;
        internal uint LastAccessTimeHigh;
        internal uint LastWriteTimeLow;
        internal uint LastWriteTimeHigh;
        internal uint VolumeSerialNumber;
        internal uint FileSizeHigh;
        internal uint FileSizeLow;
        internal uint NumberOfLinks;
        internal uint FileIndexHigh;
        internal uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInformation
    {
        [MarshalAs(UnmanagedType.Bool)]
        internal bool DeleteFile;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle handle,
        out ByHandleFileInformation information);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle handle,
        int fileInformationClass,
        ref FileDispositionInformation fileInformation,
        int bufferSize);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint SetSecurityInfo(
        SafeFileHandle handle,
        uint objectType,
        uint securityInformation,
        IntPtr owner,
        IntPtr group,
        IntPtr dacl,
        IntPtr sacl);

}
