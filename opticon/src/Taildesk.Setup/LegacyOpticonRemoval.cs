using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;
using System.Xml.Linq;
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
    private const uint WriteDac = 0x00040000;
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
    private const uint DaclSecurityInformation = 0x00000004;
    private const uint ProtectedDaclSecurityInformation = 0x80000000;
    private const int FileDispositionInformationClass = 4;
    private const int FileDispositionInformationExClass = 21;
    private const uint FileDispositionDelete = 0x00000001;
    private const uint FileDispositionForceImageSectionCheck = 0x00000004;
    private const uint FileDispositionIgnoreReadonlyAttribute = 0x00000010;
    private const uint ProcessQueryLimitedInformation = 0x00001000;
    private const int ErrorAccessDenied = 5;
    private const int ErrorNotSupported = 50;
    private const int ErrorInvalidParameter = 87;
    private const int ErrorCallNotImplemented = 120;
    private const int ErrorPrivilegeNotHeld = 1314;
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;
    private const int ErrorDirectoryNotEmpty = 145;
    private const int TaskPresenceProbeAbsentExitCode = 3;
    private const string TaskPresenceProbeScript = """
        $ErrorActionPreference = 'Stop'
        $service = $null
        $folder = $null
        $task = $null
        try {
            $service = New-Object -ComObject 'Schedule.Service'
            $service.Connect()
            $folder = $service.GetFolder('\')
            try {
                $task = $folder.GetTask($env:TAILDESK_EXPECTED_TASK_NAME)
            } catch {
                # HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND). All other COM,
                # access, RPC, and PowerShell failures escape and therefore
                # fail closed. PowerShell can surface this HRESULT as either a
                # COMException or FileNotFoundException depending on runtime.
                if ($_.Exception.HResult -eq -2147024894) { exit 3 }
                throw
            }
            exit 0
        } finally {
            if ($null -ne $task) {
                [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($task) | Out-Null
            }
            if ($null -ne $folder) {
                [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($folder) | Out-Null
            }
            if ($null -ne $service) {
                [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($service) | Out-Null
            }
        }
        """;
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
        "Opticon Command Center",
        "Taildesk Setup Resume"
    ];

    /// <summary>
    /// Rehearses every non-mutating fixed-root and fixed-task proof before the
    /// source launcher crosses the destructive replacement boundary.
    /// </summary>
    internal static async Task<bool> PreflightLegacyInstallationIfPresentAsync() =>
        await CreatePlanAsync() is not null;

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

        // Prior releases intentionally protect machine state for SYSTEM. The
        // elevated source launcher holds these privileges, but Windows leaves
        // them disabled until a backup/restore operation explicitly enables
        // them. FILE_FLAG_BACKUP_SEMANTICS then permits the handle-bound walk
        // to pin those protected descendants without broadening their ACLs by
        // path or weakening the no-reparse boundary.
        using var backupPrivilege = ScopedProcessPrivilege.Enable("SeBackupPrivilege");
        using var restorePrivilege = ScopedProcessPrivilege.Enable("SeRestorePrivilege");

        var sealedRoots = new List<PinnedDirectoryTree>();
        var taskStateMutationAttempted = false;
        var filesystemDeletionStarted = false;
        try
        {
            report("Disabling Opticon scheduled tasks at their fixed names...");
            foreach (var task in plan.Tasks)
            {
                // Record the mutation boundary before invoking schtasks. A
                // failed process result is not proof that Task Scheduler made
                // no change, so recovery must include this task as well.
                taskStateMutationAttempted = true;
                await SetTaskEnabledStateAsync(task.Name, enabled: false);
            }

            report("Stopping Opticon scheduled tasks at their fixed names...");
            foreach (var task in plan.Tasks)
                await EndTaskAsync(task.Name);

            report("Stopping processes running from Opticon's fixed installation root...");
            await StopValidatedProcessesAsync(plan.InstallDirectory);

            report("Removing the existing Opticon installation and machine state...");
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

            var daclOnlySeals = sealedRoots.Sum(root => root.DaclOnlySealCount);
            var pinnedOnlySeals = sealedRoots.Sum(root => root.PinnedOnlySealCount);
            if (pinnedOnlySeals > 0)
            {
                report($"Windows refused ACL replacement on {pinnedOnlySeals} existing Opticon " +
                       (pinnedOnlySeals == 1 ? "directory" : "directories") +
                       "; cleanup retained exact no-reparse, no-share-delete handles and continued without path-based deletion.");
            }
            else if (daclOnlySeals > 0)
            {
                report($"Windows refused the protected-DACL flag on {daclOnlySeals} existing Opticon " +
                       (daclOnlySeals == 1 ? "directory" : "directories") +
                       "; cleanup applied the SYSTEM/Administrators DACL and retained exact pinned handles.");
            }

            // Reassert disabled state in case an already-running legacy
            // process tried to re-enable its watchdog while the protected tree
            // was being pinned. Then quiesce the same fixed task instances and
            // path-verified processes immediately before dispositioning the
            // exact handles.
            foreach (var task in plan.Tasks)
            {
                await SetTaskEnabledStateAsync(task.Name, enabled: false);
                await EndTaskAsync(task.Name);
            }
            await StopValidatedProcessesAsync(plan.InstallDirectory);

            foreach (var root in sealedRoots)
            {
                // From this point forward, never re-enable a definition that
                // could launch a partially deleted legacy installation.
                filesystemDeletionStarted = true;
                root.DeleteAllPinnedEntries();
            }
        }
        catch (Exception removalError) when (!filesystemDeletionStarted)
        {
            if (taskStateMutationAttempted)
            {
                try
                {
                    await RestoreOriginalTaskEnabledStatesAsync(plan.Tasks);
                }
                catch (Exception restoreError)
                {
                    throw new AggregateException(
                        "Opticon cleanup failed before deletion and one or more original scheduled-task states could not be restored.",
                        removalError,
                        restoreError);
                }
            }
            throw;
        }
        finally { foreach (var root in sealedRoots) root.Dispose(); }

        foreach (var directory in plan.DirectoriesToRemove)
        {
            if (Directory.Exists(directory) || File.Exists(directory))
                throw new IOException("The existing Opticon directory could not be removed completely.");
        }

        // Keep the definitions registered until every pinned filesystem entry
        // is gone. A handle/ACL failure therefore stops old processes but does
        // not also erase the recovery task definitions.
        foreach (var task in plan.Tasks)
            await DeleteTaskAsync(task.Name);

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
            tree.RecordAclSeal(SealDirectoryDacl(root));
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
                    // directory with the additional WRITE_DAC right required
                    // to seal it through that handle.
                    entry.Dispose();
                    entry = OpenPinnedEntry(path, directory: true, depth: parent.Depth + 1);
                    tree.RecordAclSeal(SealDirectoryDacl(entry));
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
        // GENERIC_READ and READ_CONTROL are unnecessary for deletion and can
        // be denied on protected SYSTEM-only state. Backup/restore privilege
        // plus FILE_FLAG_BACKUP_SEMANTICS authorizes only the explicit rights
        // needed to identify, seal, and delete the exact pinned node.
        var desiredAccess = Delete | FileReadAttributes | Synchronize;
        if (directory == true)
            desiredAccess |= WriteDac | FileListDirectory;
        // A retained directory handle is also the quiescence boundary for its
        // namespace. Omitting share-write as well as share-delete makes the
        // preflight fail if an existing writer is still active and prevents a
        // new writer from entering before handle-bound disposition. The
        // short type-probe and regular-file handles retain share-write; every
        // discovered directory is immediately reopened with this stricter
        // boundary before traversal continues.
        var shareMode = directory == true
            ? FileShareRead
            : FileShareRead | FileShareWrite;
        var handle = CreateFile(
            path,
            desiredAccess,
            shareMode,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException(
                $"Could not open existing Opticon path '{DescribeCleanupPath(path)}' safely " +
                $"with requested access 0x{desiredAccess:X8} (Win32 error {error}).");
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

    private static string DescribeCleanupPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        foreach (var (folder, label) in new[]
                 {
                     (Environment.SpecialFolder.ProgramFiles, "%ProgramFiles%"),
                     (Environment.SpecialFolder.CommonApplicationData, "%ProgramData%")
                 })
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(
                Environment.GetFolderPath(folder)));
            if (fullPath.Equals(root, StringComparison.OrdinalIgnoreCase)) return label;
            var prefix = root + Path.DirectorySeparatorChar;
            if (fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return label + Path.DirectorySeparatorChar + fullPath[prefix.Length..];
        }
        return "[outside fixed Opticon roots]";
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

    private static DirectorySealStrength SealDirectoryDacl(PinnedEntry entry)
    {
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        // SetSecurityInfo propagates inheritable ACEs to existing descendants.
        // Protected legacy descendants can reject that implicit path-based
        // propagation even though this exact pinned directory handle grants
        // WRITE_DAC. Cleanup seals every directory explicitly through its own
        // no-reparse handle, so these ACEs must apply to this directory only.
        var inheritance = AceFlags.None;
        var dacl = new RawAcl(revision: 2, capacity: 2);
        dacl.InsertAce(0, new CommonAce(inheritance, AceQualifier.AccessAllowed,
            (int)FileSystemRights.FullControl, system, isCallback: false, opaque: null));
        dacl.InsertAce(1, new CommonAce(inheritance, AceQualifier.AccessAllowed,
            (int)FileSystemRights.FullControl, administrators, isCallback: false, opaque: null));
        var daclBytes = new byte[dacl.BinaryLength];
        dacl.GetBinaryForm(daclBytes, 0);
        var daclBuffer = Marshal.AllocHGlobal(daclBytes.Length);
        try
        {
            Marshal.Copy(daclBytes, 0, daclBuffer, daclBytes.Length);
            // Retain the existing owner. Replacement needs only to protect a
            // SYSTEM/Administrators DACL while the tree is pinned. Asking
            // SetSecurityInfo to transfer ownership as well is unnecessary
            // and can return ERROR_ACCESS_DENIED on an otherwise writable
            // protected root even when SeRestorePrivilege is enabled.
            const uint securityInformation = DaclSecurityInformation | ProtectedDaclSecurityInformation;
            var result = SetSecurityInfo(
                entry.Handle,
                SeFileObject,
                securityInformation,
                IntPtr.Zero,
                IntPtr.Zero,
                daclBuffer,
                IntPtr.Zero);
            if (result == 0) return DirectorySealStrength.ProtectedDacl;

            // Some legacy roots grant WRITE_DAC to the backup-intent handle
            // but still reject PROTECTED_DACL_SECURITY_INFORMATION. Replacing
            // the DACL without changing its protection bit still removes weak
            // access from the directory itself, and the retained exact handle
            // supplies the path-swap boundary for the short cleanup window.
            if (IsAclSealCompatibilityError(result))
            {
                var daclOnlyResult = SetSecurityInfo(
                    entry.Handle,
                    SeFileObject,
                    DaclSecurityInformation,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    daclBuffer,
                    IntPtr.Zero);
                if (daclOnlyResult == 0) return DirectorySealStrength.DaclOnly;

                // ACL replacement is defense in depth: all deletion remains
                // bound to validated handles opened without following reparse
                // points or sharing delete. An ACL-denied legacy directory can
                // therefore be removed safely without falling back to a path.
                if (IsAclSealCompatibilityError(daclOnlyResult))
                    return DirectorySealStrength.PinnedHandleOnly;

                ThrowAclSealFailure(entry, DaclSecurityInformation, daclOnlyResult);
            }

            ThrowAclSealFailure(entry, securityInformation, result);
            return DirectorySealStrength.PinnedHandleOnly;
        }
        finally
        {
            Marshal.FreeHGlobal(daclBuffer);
        }
    }

    private static bool IsAclSealCompatibilityError(uint result)
        => result is ErrorAccessDenied or ErrorInvalidParameter or ErrorPrivilegeNotHeld;

    private static void ThrowAclSealFailure(PinnedEntry entry, uint securityInformation, uint result)
        => throw new UnauthorizedAccessException(
            $"Could not seal the existing Opticon directory ACL at " +
            $"'{DescribeCleanupPath(entry.Path)}' with security information " +
            $"0x{securityInformation:X8} using handle access " +
            $"0x{(Delete | FileReadAttributes | Synchronize | WriteDac | FileListDirectory):X8} " +
            $"(Win32 error {result}).");

    private static void MarkPinnedEntryForDeletion(PinnedEntry entry)
    {
        var extendedError = 0;
        var legacyError = 0;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var extendedDisposition = new FileDispositionInformationEx
            {
                Flags = FileDispositionDelete
                        | FileDispositionForceImageSectionCheck
                        | FileDispositionIgnoreReadonlyAttribute
            };
            if (SetFileInformationByHandle(
                    entry.Handle,
                    FileDispositionInformationExClass,
                    ref extendedDisposition,
                    Marshal.SizeOf<FileDispositionInformationEx>()))
                return;
            extendedError = Marshal.GetLastWin32Error();

            // FILE_DISPOSITION_INFO_EX is present on every supported Opticon
            // Windows build, but retain the classic handle disposition for
            // unusual filesystems that reject the extended flags.
            var disposition = new FileDispositionInformation { DeleteFile = true };
            if (SetFileInformationByHandle(
                    entry.Handle,
                    FileDispositionInformationClass,
                    ref disposition,
                    Marshal.SizeOf<FileDispositionInformation>()))
                return;
            legacyError = Marshal.GetLastWin32Error();

            if (!IsTransientDeletionError(extendedError)
                && !IsTransientDeletionError(legacyError)
                && extendedError is not (ErrorNotSupported or ErrorInvalidParameter or ErrorCallNotImplemented))
                break;
            if (attempt < 4) Thread.Sleep(TimeSpan.FromMilliseconds(150 * (attempt + 1)));
        }

        throw new IOException(
            $"Could not delete pinned Opticon path '{DescribeCleanupPath(entry.Path)}' " +
            $"(extended Win32 error {extendedError}; " +
            $"classic Win32 error {legacyError}).");
    }

    private static bool IsTransientDeletionError(int error)
        => error is ErrorAccessDenied or ErrorSharingViolation or ErrorLockViolation or ErrorDirectoryNotEmpty;

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
        var result = await RunSchtasksAsync(["/Query", "/TN", taskName, "/XML"]);
        if (!result.Succeeded)
        {
            if (!await IsTaskPresentAtFixedNameAsync(taskName)) return null;
            throw new InvalidDataException(
                $"Could not determine whether scheduled task '{taskName}' is present. No files were removed: {DescribeResult(result)}");
        }
        return new ValidatedTask(taskName, ReadTaskEnabledState(taskName, result.StandardOutput));
    }

    private static bool ReadTaskEnabledState(string taskName, string taskXml)
    {
        const int maximumTaskXmlCharacters = 1_048_576;
        if (string.IsNullOrWhiteSpace(taskXml) || taskXml.Length > maximumTaskXmlCharacters)
            throw new InvalidDataException(
                $"Scheduled task '{taskName}' returned an empty or unexpectedly large definition. No files were removed.");

        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = maximumTaskXmlCharacters
            };
            using var text = new StringReader(taskXml);
            using var reader = XmlReader.Create(text, settings);
            var document = XDocument.Load(reader, LoadOptions.None);
            var root = document.Root;
            if (root is null || !root.Name.LocalName.Equals("Task", StringComparison.Ordinal))
                throw new InvalidDataException("The scheduled-task definition has an unexpected root element.");

            var taskNamespace = root.Name.Namespace;
            var taskSettings = root.Elements(taskNamespace + "Settings").ToArray();
            if (taskSettings.Length != 1)
                throw new InvalidDataException("The scheduled-task definition does not contain exactly one Settings element.");

            var enabledElements = taskSettings[0].Elements(taskNamespace + "Enabled").ToArray();
            if (enabledElements.Length == 0)
            {
                // The Task Scheduler schema defaults Settings.Enabled to true.
                return true;
            }
            if (enabledElements.Length != 1)
                throw new InvalidDataException("The scheduled-task definition contains multiple Enabled settings.");

            return enabledElements[0].Value.Trim() switch
            {
                "true" or "1" => true,
                "false" or "0" => false,
                _ => throw new InvalidDataException("The scheduled-task Enabled setting is invalid.")
            };
        }
        catch (Exception exception) when (exception is XmlException or InvalidOperationException)
        {
            throw new InvalidDataException(
                $"Could not read the enabled state of scheduled task '{taskName}'. No files were removed.",
                exception);
        }
    }

    private static async Task SetTaskEnabledStateAsync(string taskName, bool enabled)
    {
        var option = enabled ? "/ENABLE" : "/DISABLE";
        var result = await RunSchtasksAsync(["/Change", "/TN", taskName, option]);
        if (!result.Succeeded)
            throw new InvalidOperationException(
                $"Could not {(enabled ? "enable" : "disable")} validated Opticon task '{taskName}': {DescribeResult(result)}");

        var observed = await QueryTaskIfPresentAsync(taskName);
        if (observed is null || observed.WasEnabled != enabled)
            throw new InvalidOperationException(
                $"Windows did not confirm that validated Opticon task '{taskName}' was {(enabled ? "enabled" : "disabled")}.");
    }

    private static async Task RestoreOriginalTaskEnabledStatesAsync(IReadOnlyList<ValidatedTask> tasks)
    {
        var failures = new List<Exception>();
        foreach (var task in tasks.Where(task => task.WasEnabled))
        {
            try
            {
                await SetTaskEnabledStateAsync(task.Name, enabled: true);
            }
            catch (Exception exception)
            {
                // Attempt every restoration even if an earlier task changed or
                // disappeared. The aggregate preserves each recovery failure.
                failures.Add(exception);
            }
        }

        if (failures.Count > 0)
            throw new AggregateException("One or more scheduled tasks could not be restored to their original enabled state.", failures);
    }

    private static async Task EndTaskAsync(string taskName)
    {
        var result = await RunSchtasksAsync(["/End", "/TN", taskName]);
        // schtasks localizes the "not running" diagnostic. Exit code 1 is
        // also its documented ordinary no-active-instance result; the bounded
        // path-verified process sweep below remains authoritative.
        if (result.Succeeded || result.ExitCode == 1 || IsTaskInactive(result)) return;
        throw new InvalidOperationException($"Could not stop validated Opticon task '{taskName}': {DescribeResult(result)}");
    }

    private static async Task DeleteTaskAsync(string taskName)
    {
        var result = await RunSchtasksAsync(["/Delete", "/TN", taskName, "/F"]);
        if (!result.Succeeded && await IsTaskPresentAtFixedNameAsync(taskName))
            throw new InvalidOperationException($"Could not delete validated Opticon task '{taskName}': {DescribeResult(result)}");
    }

    private static async Task<bool> IsTaskPresentAtFixedNameAsync(string taskName)
    {
        var executable = Path.GetFullPath(Path.Combine(
            Environment.SystemDirectory,
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe"));
        if (!File.Exists(executable) || (File.GetAttributes(executable) & FileAttributes.ReparsePoint) != 0)
            throw new FileNotFoundException("The fixed Windows task-presence probe is unavailable.", executable);

        var environment = BuildSystemToolEnvironment()
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        environment["TAILDESK_EXPECTED_TASK_NAME"] = taskName;
        var result = await ProcessRunner.RunAsync(
            executable,
            [
                "-NoLogo",
                "-NoProfile",
                "-NonInteractive",
                "-ExecutionPolicy", "Bypass",
                "-Command", TaskPresenceProbeScript
            ],
            TimeSpan.FromSeconds(20),
            environment: environment,
            clearEnvironment: true);

        if (result.Succeeded) return true;
        if (result.ExitCode == TaskPresenceProbeAbsentExitCode) return false;
        throw new InvalidDataException(
            $"Could not prove whether scheduled task '{taskName}' is present. No files were removed: {DescribeResult(result)}");
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
            .Select(path => Path.GetFileNameWithoutExtension(path)!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Require two consecutive quiet snapshots. A legacy Guardian can be
        // configured for restart and an already-fired scheduled task can race
        // the first snapshot; one point-in-time Process.GetProcesses call is
        // therefore insufficient before deleting mapped executables.
        var quietPasses = 0;
        for (var pass = 0; pass < 8; pass++)
        {
            var found = await StopValidatedProcessPassAsync(canonicalRoot, expectedNames);
            quietPasses = found == 0 ? quietPasses + 1 : 0;
            if (quietPasses >= 2) return;
            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        throw new InvalidOperationException(
            "An existing Opticon process kept restarting from the fixed installation root.");
    }

    private static async Task<int> StopValidatedProcessPassAsync(
        string canonicalRoot,
        IReadOnlySet<string> expectedNames)
    {
        var found = 0;
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                string processName;
                try { processName = process.ProcessName; }
                catch (Exception exception) when (exception is InvalidOperationException
                                                  or System.ComponentModel.Win32Exception)
                {
                    continue;
                }
                if (!expectedNames.Contains(processName)) continue;
                if (!TryGetProcessImagePath(process, out var image))
                {
                    try
                    {
                        if (process.HasExited) continue;
                    }
                    catch (InvalidOperationException)
                    {
                        // The process exited between the name and image-path
                        // observations, so there is no running instance left to
                        // classify.
                        continue;
                    }
                    catch (System.ComponentModel.Win32Exception exception)
                    {
                        throw new InvalidDataException(
                            $"Could not prove whether a running process named '{processName}' belongs to Opticon. No Opticon files were removed.",
                            exception);
                    }

                    // A still-running executable with a name present under the
                    // fixed install root must be inspectable before cleanup can
                    // safely decide that it is unrelated.
                    throw new InvalidDataException(
                        $"Could not prove the path of a running process named '{processName}'. No Opticon files were removed.");
                }
                if (!image.StartsWith(canonicalRoot, StringComparison.OrdinalIgnoreCase)) continue;
                found++;
                try
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
                }
                catch (InvalidOperationException)
                {
                    // The process exited after the snapshot and before Kill.
                }
                catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or TimeoutException)
                {
                    throw new InvalidOperationException(
                        $"Could not stop the existing Opticon process '{Path.GetFileName(image)}'.", exception);
                }
            }
        }
        return found;
    }

    private static bool TryGetProcessImagePath(Process process, out string image)
    {
        image = string.Empty;
        SafeProcessHandle? handle = null;
        try
        {
            handle = OpenProcess(ProcessQueryLimitedInformation, inheritHandle: false, process.Id);
            if (!handle.IsInvalid)
            {
                var capacity = 32_768u;
                var buffer = new StringBuilder((int)capacity);
                if (QueryFullProcessImageNameW(handle, flags: 0, buffer, ref capacity)
                    && TryNormalizeImagePath(buffer.ToString(), out image))
                    return true;
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                          or ArgumentException
                                          or NotSupportedException
                                          or PathTooLongException)
        {
            // Fall through to MainModule, which can still work for processes
            // that reject PROCESS_QUERY_LIMITED_INFORMATION on older builds.
        }
        finally
        {
            handle?.Dispose();
        }

        try { return TryNormalizeImagePath(process.MainModule?.FileName, out image); }
        catch (Exception exception) when (exception is InvalidOperationException
                                          or System.ComponentModel.Win32Exception
                                          or NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryNormalizeImagePath(string? candidate, out string image)
    {
        image = string.Empty;
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        try
        {
            image = Path.GetFullPath(candidate);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or NotSupportedException
                                          or PathTooLongException)
        {
            return false;
        }
    }

    internal sealed record RemovalPlan(
        string InstallDirectory,
        IReadOnlyList<string> DirectoriesToRemove,
        IReadOnlyList<ValidatedTask> Tasks)
    {
        internal IReadOnlyList<string> TaskNames => Tasks.Select(task => task.Name).ToArray();
    }

    internal sealed record ValidatedTask(string Name, bool WasEnabled);

    private enum DirectorySealStrength
    {
        ProtectedDacl,
        DaclOnly,
        PinnedHandleOnly
    }

    private sealed class PinnedDirectoryTree : IDisposable
    {
        private readonly List<PinnedEntry> _entries = [];

        internal int Count => _entries.Count;
        internal int DaclOnlySealCount { get; private set; }
        internal int PinnedOnlySealCount { get; private set; }

        internal void Add(PinnedEntry entry) => _entries.Add(entry);

        internal void RecordAclSeal(DirectorySealStrength strength)
        {
            if (strength == DirectorySealStrength.DaclOnly) DaclOnlySealCount++;
            else if (strength == DirectorySealStrength.PinnedHandleOnly) PinnedOnlySealCount++;
        }

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
            MarkPinnedEntryForDeletion(entry);
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

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInformationEx
    {
        internal uint Flags;
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

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle handle,
        int fileInformationClass,
        ref FileDispositionInformationEx fileInformation,
        int bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        int processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(
        SafeProcessHandle process,
        int flags,
        StringBuilder executableName,
        ref uint size);

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
