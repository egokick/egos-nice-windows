using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Xml;
using System.Xml.Linq;
using Taildesk.Shared;

namespace Taildesk.Setup;

/// <summary>
/// Removes the one legacy installation which cannot enter the source-only
/// protected-machine-state contract.  This is intentionally not a general
/// uninstaller: it recognizes only an intact Opticon 1.1.38 installation,
/// asks for a typed destructive confirmation, and stops if any ownership
/// proof is ambiguous.
/// </summary>
internal static class LegacyOpticonRemoval
{
    internal const string LegacyAgentVersion = "1.1.38";
    internal const string ConfirmationPhrase = "REMOVE LEGACY OPTICON";

    // Handles stay open for every validated node until handle-based deletion,
    // so keep this deliberately low rather than exhausting the machine on a
    // corrupted legacy tree.
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
    // Agent 1.1.38 registered Opticon through these scheduled tasks; it did
    // not install an Opticon Windows service. Deliberately do not enumerate or
    // mutate generic Windows services during legacy cleanup.
    private static readonly LegacyTaskDefinition[] TaskDefinitions =
    [
        new(RemoteAdministrationProtocol.GuardianWatchdogTaskName, "UpdateGuardian", "Taildesk.UpdateGuardian.exe"),
        new(RemoteAdministrationProtocol.GuardianTaskName, "UpdateGuardian", "Taildesk.UpdateGuardian.exe"),
        new(RemoteAdministrationProtocol.SshSupervisorTaskName, "UpdateGuardian", "Taildesk.UpdateGuardian.exe"),
        new(RemoteAdministrationProtocol.AgentTaskName, "Agent", "Taildesk.Agent.exe"),
        new("Taildesk Fly Route", "Admin", "Tools", "Taildesk.RouteKeeper.exe"),
        new("Opticon Command Center", "Admin", "Opticon.exe")
    ];

    /// <summary>
    /// Runs only from the verified source launcher, after that launcher has
    /// matched the signed source archive.  No deletion begins until every
    /// present task and both recognized roots have passed preflight and the
    /// operator types the confirmation phrase.
    /// </summary>
    internal static async Task RemoveLegacyInstallationIfPresentAsync(Action<string> report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var plan = await CreatePlanAsync();
        if (plan is null) return;

        report($"Legacy Opticon {LegacyAgentVersion} was detected. Waiting for explicit removal confirmation...");
        if (!LegacyOpticonRemovalPrompt.Confirm(plan))
            throw new OperationCanceledException(
                "Legacy Opticon removal was canceled. No Opticon, Tailscale, or RustDesk state was changed.");

        // Re-run every non-mutating proof immediately after the user makes the
        // destructive choice.  A changed task/root is a hard stop, not an
        // invitation to remove whatever now occupies a familiar name.
        plan = await CreatePlanAsync()
               ?? throw new InvalidOperationException("The recognized legacy Opticon installation disappeared before removal.");

        report("Stopping only validated legacy Opticon scheduled tasks...");
        foreach (var task in plan.Tasks)
            await EndTaskAsync(task.Name);
        foreach (var task in plan.Tasks)
            await DeleteTaskAsync(task.Name);

        report("Stopping only validated legacy Opticon processes...");
        await StopValidatedProcessesAsync(plan.InstallDirectory);

        report("Removing recognized legacy Opticon files and protected machine state...");
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
                if (string.Equals(directory, plan.InstallDirectory, StringComparison.OrdinalIgnoreCase))
                    RequireRecognizedInstallRoot(directory);
                else
                    RequireRecognizedMachineDataRoot(directory);
                sealedRoots.Add(root);
            }
            foreach (var root in sealedRoots) root.DeleteAllPinnedEntries();
        }
        finally { foreach (var root in sealedRoots) root.Dispose(); }

        foreach (var directory in plan.DirectoriesToRemove)
        {
            if (Directory.Exists(directory) || File.Exists(directory))
                throw new IOException("The legacy Opticon directory could not be removed completely.");
        }

        report("Legacy Opticon 1.1.38 was removed. Tailscale and RustDesk were left unchanged.");
    }

    private static async Task<RemovalPlan?> CreatePlanAsync()
    {
        var installDirectory = RequireDirectChild(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Taildesk");
        var machineDataDirectory = RequireDirectChild(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Taildesk");
        var installPresent = RequireDirectoryOrAbsent(installDirectory, "Opticon installation");
        var dataPresent = RequireDirectoryOrAbsent(machineDataDirectory, "Opticon machine state");

        // An existing task is checked even when its corresponding component is
        // gone, because an orphaned task must never be silently assumed owned.
        var validatedTasks = new List<ValidatedTask>();
        foreach (var definition in TaskDefinitions)
        {
            var task = await QueryValidatedTaskAsync(definition, installDirectory);
            if (task is not null) validatedTasks.Add(task);
        }

        if (!installPresent && !dataPresent && validatedTasks.Count == 0)
            return null;

        // The destructive path is deliberately restricted to the exact legacy
        // version the user has opted to replace.  Partial or differently
        // versioned state is left in place for attended/manual recovery.
        if (!installPresent)
            throw new InvalidDataException(
                "Legacy Opticon cleanup found state or a task without the recognized Program Files installation. No files were removed.");

        RequireRegularDirectoryTree(installDirectory);
        RequireRecognizedInstallRoot(installDirectory);
        var agentExecutable = Path.Combine(installDirectory, "Agent", "Taildesk.Agent.exe");
        if (!File.Exists(agentExecutable) || Directory.Exists(agentExecutable)
            || (File.GetAttributes(agentExecutable) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException(
                "Legacy Opticon cleanup could not prove the installed Agent executable. No files were removed.");
        var version = UpdatePackageVerifier.NormalizeVersion(
            FileVersionInfo.GetVersionInfo(agentExecutable).ProductVersion ?? string.Empty);
        if (!string.Equals(version, LegacyAgentVersion, StringComparison.Ordinal))
            throw new InvalidDataException(
                $"Legacy Opticon cleanup only accepts Agent {LegacyAgentVersion}; the installed Agent reports '{version}'. No files were removed.");

        var directories = new List<string> { installDirectory };
        if (dataPresent)
        {
            RequireRegularDirectoryTree(machineDataDirectory);
            RequireRecognizedMachineDataRoot(machineDataDirectory);
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
            throw new InvalidDataException("The legacy Opticon root name is invalid.");
        var result = Path.GetFullPath(Path.Combine(canonicalParent, childName));
        if (!string.Equals(Path.GetDirectoryName(result), canonicalParent, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The legacy Opticon root escaped its fixed Windows parent.");
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

    private static void RequireRecognizedInstallRoot(string directory)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(entry);
            var allowedDirectory = Directory.Exists(entry)
                                   && (name.Equals("Agent", StringComparison.OrdinalIgnoreCase)
                                       || name.Equals("UpdateGuardian", StringComparison.OrdinalIgnoreCase)
                                       || name.Equals("Admin", StringComparison.OrdinalIgnoreCase)
                                       || name.Equals("Admin.previous", StringComparison.OrdinalIgnoreCase)
                                       || IsRecognizedTransactionDirectory(name));
            var allowedFile = File.Exists(entry)
                              && name.Equals(".controller-install.lock", StringComparison.OrdinalIgnoreCase);
            if (!allowedDirectory && !allowedFile)
                throw new InvalidDataException(
                    "The legacy Opticon installation contains an unrecognized top-level entry. No files were removed.");
        }
    }

    private static void RequireRecognizedMachineDataRoot(string directory)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(entry);
            var allowedDirectory = Directory.Exists(entry) &&
                                   (name.Equals("Agent", StringComparison.OrdinalIgnoreCase)
                                    || name.Equals("Update", StringComparison.OrdinalIgnoreCase)
                                    || name.Equals("Ssh", StringComparison.OrdinalIgnoreCase)
                                    || name.Equals("SetupStaging", StringComparison.OrdinalIgnoreCase)
                                    || name.Equals("ControllerHandoff", StringComparison.OrdinalIgnoreCase));
            var allowedFile = File.Exists(entry)
                              && (name.Equals("install-receipt.json", StringComparison.OrdinalIgnoreCase)
                                  || name.Equals("Set-TaildeskFlyBypassRoute.ps1", StringComparison.OrdinalIgnoreCase));
            if (!allowedDirectory && !allowedFile)
                throw new InvalidDataException(
                    "The legacy Opticon machine-state directory contains an unrecognized top-level entry. No files were removed.");
        }
    }

    private static bool IsRecognizedTransactionDirectory(string name)
    {
        foreach (var component in new[] { "Agent", "Admin" })
        {
            foreach (var state in new[] { "candidate", "rollback", "failed" })
            {
                var prefix = component + "." + state + "-";
                if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    && Guid.TryParseExact(name[prefix.Length..], "N", out _)) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Pins every existing legacy node by handle before deletion. Each handle
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
                throw new InvalidDataException("Legacy Opticon cleanup refused an unexpectedly large directory tree.");
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
            throw new IOException($"Could not open a legacy Opticon path safely (Win32 error {error}).");
        }
        try
        {
            var information = ReadPinnedInformation(handle);
            var isDirectory = (information.FileAttributes & FileAttributeDirectory) != 0;
            if (directory.HasValue && directory.Value != isDirectory)
                throw new InvalidDataException("A legacy Opticon cleanup target changed type during validation.");
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
        using var observed = OpenPinnedEntry(expected.Path, expected.IsDirectory, expected.Depth);
        if (observed.Identity != expected.Identity)
            throw new InvalidDataException("A legacy Opticon directory changed while its cleanup boundary was being established.");
    }

    private static ByHandleFileInformation ReadPinnedInformation(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var information))
            throw new IOException($"Could not inspect a legacy Opticon handle (Win32 error {Marshal.GetLastWin32Error()}).");
        if ((information.FileAttributes & FileAttributeReparsePoint) != 0)
            throw new InvalidDataException("Legacy Opticon cleanup refuses a link or junction.");
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
                    $"Could not seal the legacy Opticon directory ACL (Win32 error {result}).");
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
                $"Could not delete a pinned legacy Opticon entry (Win32 error {Marshal.GetLastWin32Error()}).");
    }

    private static void RequireRegularDirectoryTree(string directory)
    {
        if (!Directory.Exists(directory)
            || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("A legacy Opticon removal target is missing or linked.");

        var pending = new Stack<string>();
        pending.Push(directory);
        var entries = 0;
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(current, "*", SearchOption.TopDirectoryOnly))
            {
                if (++entries > MaximumTreeEntries)
                    throw new InvalidDataException("Legacy Opticon cleanup refused an unexpectedly large directory tree.");
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Legacy Opticon cleanup refuses to traverse a link or junction.");
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                }
                else if (!File.Exists(entry))
                {
                    throw new InvalidDataException("A legacy Opticon removal target changed during validation.");
                }
            }
        }
    }

    private static async Task<ValidatedTask?> QueryValidatedTaskAsync(
        LegacyTaskDefinition definition,
        string installDirectory)
    {
        var result = await RunSchtasksAsync(["/Query", "/TN", definition.Name, "/XML"]);
        if (!result.Succeeded)
        {
            if (IsTaskAbsent(result)) return null;
            throw new InvalidDataException(
                $"Could not determine whether scheduled task '{definition.Name}' is present. No files were removed: {DescribeResult(result)}");
        }
        var xml = result.StandardOutput.TrimStart('\uFEFF', '\r', '\n', ' ');
        if (xml.Length is <= 0 or > 256 * 1024)
            throw new InvalidDataException($"Scheduled task '{definition.Name}' returned invalid XML.");
        var expectedExecutable = Path.GetFullPath(Path.Combine([installDirectory, .. definition.RelativeExecutablePath]));
        RequireTaskOwnsExactExecutable(xml, definition.Name, expectedExecutable);
        return new ValidatedTask(definition.Name, expectedExecutable);
    }

    private static void RequireTaskOwnsExactExecutable(string xml, string taskName, string expectedExecutable)
    {
        XDocument document;
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 256 * 1024
            });
            document = XDocument.Load(reader, LoadOptions.None);
        }
        catch (XmlException exception)
        {
            throw new InvalidDataException($"Scheduled task '{taskName}' XML is invalid.", exception);
        }
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var root = document.Root;
        var actions = root?.Element(ns + "Actions");
        var actionList = actions?.Elements().ToArray() ?? [];
        var exec = actionList.SingleOrDefault();
        var command = exec?.Element(ns + "Command")?.Value ?? string.Empty;
        if (root?.Name != ns + "Task" || actionList.Length != 1 || exec?.Name != ns + "Exec"
            || !string.Equals(command, expectedExecutable, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"Scheduled task '{taskName}' is not the exact legacy Opticon task expected at its fixed path. No files were removed.");
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
        var expected = TaskDefinitions
            .Select(definition => Path.GetFullPath(Path.Combine([installDirectory, .. definition.RelativeExecutablePath])))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var expectedNames = expected.Select(Path.GetFileNameWithoutExtension)
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
                        $"Could not prove the path of a running process named '{process.ProcessName}'. No legacy files were removed.", exception);
                }
                if (!expected.Contains(image)) continue;
                try
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
                {
                    throw new InvalidOperationException(
                        $"Could not stop the validated legacy Opticon process '{Path.GetFileName(image)}'.", exception);
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

    internal sealed record ValidatedTask(string Name, string Executable);

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
                // A failed disposition may leave part of the legacy product in
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

    private sealed record LegacyTaskDefinition(string Name, params string[] RelativeExecutablePath);
}
