using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace Taildesk.Shared;

/// <summary>
/// One deliberately narrow compatibility bridge for the 1.1.38 Agent layout.
///
/// 1.1.38 created the Taildesk ProgramData root before it had the current
/// exact ACL.  The bridge never validates a weak object and later adopts it:
/// it opens each fixed-layout object without sharing, rejects links and any
/// ACL other than the known legacy provenance, seals that exact handle, and
/// only then reads the sealed snapshot.  Any ambiguity leaves the old
/// Guardian free to restore the previous Agent.
/// </summary>
internal static partial class LegacyMachineStateMigration
{
    private const int MaximumConfigBytes = 4 * 1024 * 1024;
    private const int MaximumJournalBytes = 4 * 1024 * 1024;
    private const int MaximumSmallJsonBytes = 256 * 1024;
    private const long MaximumPackageBytes = 1024L * 1024 * 1024;
    private const int MaximumPayloadEntries = 4096;
    private const int MaximumPayloadDepth = 16;
    private const long MaximumPayloadBytes = 2L * 1024 * 1024 * 1024;
    private const int MaximumRootEntries = 16;
    private const int MaximumAgentEntries = 4;
    private const int MaximumUpdateEntries = 512;

    private const uint FileReadAttributes = 0x00000080;
    private const uint FileListDirectory = 0x00000001;
    private const uint FileTraverse = 0x00000020;
    private const uint ReadControl = 0x00020000;
    private const uint WriteDac = 0x00040000;
    private const uint WriteOwner = 0x00080000;
    private const uint Synchronize = 0x00100000;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint SeFileObject = 1;
    private const uint OwnerSecurityInformation = 0x00000001;
    private const uint DaclSecurityInformation = 0x00000004;
    private const uint ProtectedDaclSecurityInformation = 0x80000000;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorSharingViolation = 32;

    private static readonly TimeSpan ExclusiveOpenDeadline = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ExclusiveOpenRetryDelay = TimeSpan.FromMilliseconds(250);
    private static readonly SecurityIdentifier SystemSid =
        new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier AdministratorsSid =
        new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier BuiltinUsersSid =
        new(WellKnownSidType.BuiltinUsersSid, null);
    private static readonly SecurityIdentifier CreatorOwnerSid =
        new(WellKnownSidType.CreatorOwnerSid, null);
    // The inherited ProgramData Users RX ACE carries the SYNCHRONIZE bit in
    // addition to the expanded ReadAndExecute mask (0x1200A9).
    private const int ReadOnlyRights = (int)(FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize);
    // ProgramData's inherited CREATOR OWNER ACE is intentionally stored as
    // GENERIC_ALL rather than the expanded FileSystemRights.FullControl mask.
    // It is accepted only with the exact inheritance-only flags below.
    private const int GenericAllRights = unchecked((int)0x10000000);
    private const int RootLegacyCreateRights = (int)(FileSystemRights.CreateFiles
                                                      | FileSystemRights.CreateDirectories
                                                      | FileSystemRights.WriteAttributes
                                                      | FileSystemRights.WriteExtendedAttributes);

    internal static void MigrateIfRequiredForSignedBridge()
    {
        if (!IsAuthorizedBridgeBuild() || !OperatingSystem.IsWindows()) return;

        var root = FullPath(AppPaths.MachineDataDirectory);
        RequireFixedMachineRootPath(root);

        // A successful bridge can be invoked again by the candidate's own
        // helper or matching Guardian.  No-op only after every core object is
        // already exact and the protected journal proves this is no longer the
        // active bridge.  This preserves crash retries that left a weak staged
        // payload below an otherwise protected root.
        var coreIsExact = !RequiresAclMigration(root);
        if (coreIsExact && !HasProtectedActiveBridgeJournal()) return;

        using var sealedState = SealLegacyStateBeforeValidation(root);
        if (sealedState is null) return;
        ValidateLegacyState(sealedState);
    }

    private static bool IsAuthorizedBridgeBuild()
    {
        var bridgeVersion = RemoteAdministrationProtocol.LegacyMachineStateMigrationBridgeVersion;
        var runningVersion = UpdatePackageVerifier.NormalizeVersion(
            typeof(LegacyMachineStateMigration).Assembly.GetName().Version?.ToString() ?? string.Empty);
        return BuildSigningTrust.IsLegacyMigrationBuild
               && string.Equals(BuildSigningTrust.LegacyMigrationVersion, bridgeVersion, StringComparison.Ordinal)
               && string.Equals(runningVersion, bridgeVersion, StringComparison.Ordinal);
    }

    private static bool RequiresAclMigration(string root)
    {
        try
        {
            MachineStorageSecurity.RequireRestrictedDirectory(root);
            MachineStorageSecurity.RequireRestrictedDirectory(AppPaths.AgentDataDirectory);
            MachineStorageSecurity.RequireRestrictedDirectory(AppPaths.UpdateDataDirectory);
            MachineStorageSecurity.RequireRestrictedFile(AppPaths.AgentConfigFile);
            MachineStorageSecurity.RequireRestrictedFile(AppPaths.UpdateJournalFile);
            return false;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
                                          or InvalidDataException
                                          or DirectoryNotFoundException
                                          or FileNotFoundException
                                          or IOException)
        {
            return true;
        }
    }

    private static bool HasProtectedActiveBridgeJournal()
    {
        try
        {
            var bytes = MachineStorageSecurity.ReadRestrictedFile(
                AppPaths.UpdateJournalFile, MaximumJournalBytes);
            var journal = JsonSerializer.Deserialize<UpdateJournal>(bytes, JsonDefaults.Options);
            return journal is not null
                   && journal.Phase == UpdatePhase.AwaitingCommit
                   && string.Equals(NormalizeVersion(journal.CurrentVersion), "1.1.38", StringComparison.Ordinal)
                   && string.Equals(NormalizeVersion(journal.TargetVersion),
                       RemoteAdministrationProtocol.LegacyMachineStateMigrationBridgeVersion,
                       StringComparison.Ordinal);
        }
        catch
        {
            // An unreadable purportedly protected journal is never grounds for
            // skipping the bridge.  The sealed validation below will either
            // prove the exact active transaction or fail closed.
            return true;
        }
    }

    private static SealedLegacyState? SealLegacyStateBeforeValidation(string root)
    {
        using var rootHandle = TryOpenLegacyRootNoSharing(root);
        if (rootHandle is null) return null;

        SealLegacyObject(rootHandle, isDirectory: true, LegacyAclClass.Root, "legacy machine-state root");
        var state = new SealedLegacyState(root);
        try
        {
            SealMachineRoot(rootHandle, state);
            return state;
        }
        catch
        {
            state.Dispose();
            throw;
        }
    }

    private static void SealMachineRoot(SafeFileHandle rootHandle, SealedLegacyState state)
    {
        var entries = CaptureDirectoryEntries(rootHandle, state.Root, "legacy machine-state root", MaximumRootEntries);
        state.AddDirectory(state.Root, entries);
        RequireOnlyEntries(entries, "legacy machine-state root", static name =>
            name.Equals("Agent", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Update", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Ssh", StringComparison.OrdinalIgnoreCase)
            || name.Equals("SetupStaging", StringComparison.OrdinalIgnoreCase)
            || name.Equals("ControllerHandoff", StringComparison.OrdinalIgnoreCase)
            || name.Equals("install-receipt.json", StringComparison.OrdinalIgnoreCase));

        var agent = RequireEntry(entries, "Agent", isDirectory: true, "legacy machine-state root");
        using (var agentHandle = OpenLegacyChildNoSharing(rootHandle, agent.Name, isDirectory: true, "legacy Agent state"))
            SealAgentDirectory(agentHandle, agent.Path, state);

        var update = RequireEntry(entries, "Update", isDirectory: true, "legacy machine-state root");
        using (var updateHandle = OpenLegacyChildNoSharing(rootHandle, update.Name, isDirectory: true, "legacy update state"))
            SealUpdateDirectory(updateHandle, update.Path, state);

        SealOptionalEmptyRootDirectory(rootHandle, entries, "Ssh", "legacy SSH state", state);
        SealOptionalEmptyRootDirectory(rootHandle, entries, "SetupStaging", "legacy setup staging", state);
        SealOptionalEmptyRootDirectory(rootHandle, entries, "ControllerHandoff", "legacy controller handoff", state);

        if (entries.TryGetValue("install-receipt.json", out var receipt))
        {
            if (receipt.IsDirectory)
                throw new InvalidDataException("The legacy install receipt is not a regular file.");
            SealRegularFile(rootHandle, receipt, MaximumSmallJsonBytes, retainForValidation: true,
                "legacy install receipt", state);
        }
    }

    private static void SealAgentDirectory(SafeFileHandle handle, string directory, SealedLegacyState state)
    {
        SealLegacyObject(handle, isDirectory: true, LegacyAclClass.Regular, "legacy Agent state");
        var entries = CaptureDirectoryEntries(handle, directory, "legacy Agent state", MaximumAgentEntries);
        state.AddDirectory(directory, entries);
        RequireOnlyEntries(entries, "legacy Agent state", static name =>
            name.Equals("agent.json", StringComparison.OrdinalIgnoreCase)
            || name.Equals("SshAccess", StringComparison.OrdinalIgnoreCase));

        var config = RequireEntry(entries, "agent.json", isDirectory: false, "legacy Agent state");
        SealRegularFile(handle, config, MaximumConfigBytes, retainForValidation: true,
            "legacy Agent configuration", state);

        if (entries.TryGetValue("SshAccess", out var sshAccess))
        {
            if (!sshAccess.IsDirectory)
                throw new InvalidDataException("The isolated SSH access state is not a directory.");
            RequireIsolatedSshAccessDirectory(handle, sshAccess.Name);
        }
    }

    private static void SealUpdateDirectory(SafeFileHandle handle, string directory, SealedLegacyState state)
    {
        SealLegacyObject(handle, isDirectory: true, LegacyAclClass.Regular, "legacy update state");
        var entries = CaptureDirectoryEntries(handle, directory, "legacy update state", MaximumUpdateEntries);
        state.AddDirectory(directory, entries);

        var journal = RequireEntry(entries, "state.json", isDirectory: false, "legacy update state");
        SealRegularFile(handle, journal, MaximumJournalBytes, retainForValidation: true,
            "legacy update journal", state);

        var lockEntry = RequireEntry(entries, "transaction.lock", isDirectory: false, "legacy update state");
        foreach (var entry in entries.Values.OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (entry.Name.Equals("state.json", StringComparison.OrdinalIgnoreCase)
                || entry.Name.Equals("transaction.lock", StringComparison.OrdinalIgnoreCase))
                continue;

            if (entry.Name.Equals("update-health-token.json", StringComparison.OrdinalIgnoreCase)
                || entry.Name.Equals("commit-request.json", StringComparison.OrdinalIgnoreCase)
                || entry.Name.Equals("guardian-startup-failure.json", StringComparison.OrdinalIgnoreCase))
            {
                if (entry.IsDirectory)
                    throw new InvalidDataException($"The legacy update metadata is not a regular file: {entry.Name}");
                SealRegularFile(handle, entry, MaximumSmallJsonBytes, retainForValidation: true,
                    "legacy update metadata", state);
                continue;
            }

            if (entry.IsDirectory && OperationDirectoryName().IsMatch(entry.Name))
            {
                using var operationHandle = OpenLegacyChildNoSharing(
                    handle, entry.Name, isDirectory: true, "legacy Agent update operation");
                SealAgentOperationDirectory(operationHandle, entry.Path, state);
                continue;
            }

            if (entry.IsDirectory && GuardianOperationDirectoryName().IsMatch(entry.Name))
            {
                using var guardianHandle = OpenLegacyChildNoSharing(
                    handle, entry.Name, isDirectory: true, "legacy Guardian update operation");
                SealGuardianOperationDirectory(guardianHandle, entry.Path, state);
                continue;
            }

            throw new InvalidDataException($"The legacy update state contains an unknown entry: {entry.Name}");
        }

        // The 1.1.38 Guardian owns this FileShare.None lock for the complete
        // AwaitingCommit health window. We never adopt its bytes; its sharing
        // violation proves the trusted Guardian owns the live transaction, so
        // the dedicated helper can safely seal this one future-use lock.
        SealGuardianHeldTransactionLock(handle, lockEntry);
    }

    private static void SealAgentOperationDirectory(SafeFileHandle handle, string directory, SealedLegacyState state)
    {
        SealLegacyObject(handle, isDirectory: true, LegacyAclClass.Regular, "legacy Agent update operation");
        var entries = CaptureDirectoryEntries(handle, directory, "legacy Agent update operation", MaximumPayloadEntries);
        state.AddDirectory(directory, entries);
        foreach (var entry in entries.Values.OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (entry.IsDirectory && entry.Name.Equals("staged-agent", StringComparison.OrdinalIgnoreCase))
            {
                using var payloadHandle = OpenLegacyChildNoSharing(
                    handle, entry.Name, isDirectory: true, "legacy staged Agent");
                var counter = new PayloadCounter();
                SealPackagePayloadDirectory(payloadHandle, entry.Path, "legacy staged Agent", 0, counter, state);
                continue;
            }

            if (!entry.IsDirectory && entry.Name.Equals("package.zip", StringComparison.OrdinalIgnoreCase))
            {
                SealRegularFile(handle, entry, MaximumPackageBytes, retainForValidation: false,
                    "legacy Agent update package", state);
                continue;
            }

            throw new InvalidDataException($"The legacy Agent update operation contains an unknown entry: {entry.Name}");
        }
    }

    private static void SealGuardianOperationDirectory(SafeFileHandle handle, string directory, SealedLegacyState state)
    {
        SealLegacyObject(handle, isDirectory: true, LegacyAclClass.Regular, "legacy Guardian update operation");
        var entries = CaptureDirectoryEntries(handle, directory, "legacy Guardian update operation", MaximumPayloadEntries);
        state.AddDirectory(directory, entries);
        foreach (var entry in entries.Values.OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (entry.IsDirectory && entry.Name.Equals("staged-guardian", StringComparison.OrdinalIgnoreCase))
            {
                using var payloadHandle = OpenLegacyChildNoSharing(
                    handle, entry.Name, isDirectory: true, "legacy staged Guardian");
                var counter = new PayloadCounter();
                SealPackagePayloadDirectory(payloadHandle, entry.Path, "legacy staged Guardian", 0, counter, state);
                continue;
            }

            if (!entry.IsDirectory && entry.Name.Equals("package.zip", StringComparison.OrdinalIgnoreCase))
            {
                SealRegularFile(handle, entry, MaximumPackageBytes, retainForValidation: false,
                    "legacy Guardian update package", state);
                continue;
            }

            throw new InvalidDataException($"The legacy Guardian update operation contains an unknown entry: {entry.Name}");
        }
    }

    private static void SealPackagePayloadDirectory(
        SafeFileHandle handle,
        string directory,
        string description,
        int depth,
        PayloadCounter counter,
        SealedLegacyState state)
    {
        if (depth > MaximumPayloadDepth)
            throw new InvalidDataException($"The {description} is nested too deeply.");

        SealLegacyObject(handle, isDirectory: true, LegacyAclClass.Regular, description);
        var entries = CaptureDirectoryEntries(handle, directory, description, MaximumPayloadEntries);
        state.AddDirectory(directory, entries);
        foreach (var entry in entries.Values.OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (++counter.Entries > MaximumPayloadEntries)
                throw new InvalidDataException($"The {description} contains too many entries.");

            if (entry.IsDirectory)
            {
                using var child = OpenLegacyChildNoSharing(handle, entry.Name, isDirectory: true, description + " directory");
                SealPackagePayloadDirectory(child, entry.Path, description, checked(depth + 1), counter, state);
                continue;
            }

            var length = SealRegularFile(handle, entry, MaximumPayloadBytes, retainForValidation: false,
                description + " file", state);
            counter.TotalBytes = checked(counter.TotalBytes + length);
            if (counter.TotalBytes > MaximumPayloadBytes)
                throw new InvalidDataException($"The {description} exceeds the safe size limit.");
        }
    }

    private static void SealOptionalEmptyRootDirectory(
        SafeFileHandle parent,
        IReadOnlyDictionary<string, SealedEntry> entries,
        string name,
        string description,
        SealedLegacyState state)
    {
        if (!entries.TryGetValue(name, out var entry)) return;
        if (!entry.IsDirectory)
            throw new InvalidDataException($"The {description} is not a directory.");

        using var handle = OpenLegacyChildNoSharing(parent, entry.Name, isDirectory: true, description);
        SealLegacyObject(handle, isDirectory: true, LegacyAclClass.RootStyleEmptyDirectory, description);
        var childEntries = CaptureDirectoryEntries(handle, entry.Path, description, MaximumRootEntries);
        if (childEntries.Count != 0)
            throw new InvalidDataException($"The {description} is unexpectedly non-empty during the legacy bridge.");
        state.AddDirectory(entry.Path, childEntries);
    }

    private static long SealRegularFile(
        SafeFileHandle parent,
        SealedEntry entry,
        long maximumBytes,
        bool retainForValidation,
        string description,
        SealedLegacyState state)
    {
        if (entry.IsDirectory)
            throw new InvalidDataException($"The {description} is not a regular file: {entry.Path}");

        SafeFileHandle? handle = null;
        try
        {
            handle = OpenLegacyChildNoSharing(parent, entry.Name, isDirectory: false, description);
            SealLegacyObject(handle, isDirectory: false, LegacyAclClass.Regular, description);
            var length = NativePath.GetLength(handle);
            if (length < 0 || length > maximumBytes)
                throw new InvalidDataException($"The {description} has an invalid size.");

            state.AddFile(entry.Path, length, retainForValidation ? handle : null);
            if (retainForValidation) handle = null;
            return length;
        }
        finally
        {
            handle?.Dispose();
        }
    }

    private static void RequireIsolatedSshAccessDirectory(SafeFileHandle parent, string name)
    {
        using var handle = NativePath.OpenRelative(
            parent,
            name,
            readFile: false,
            writeFile: false,
            delete: false,
            requireDirectory: true,
            createDisposition: NativePath.FileOpen,
            exclusive: false,
            changeSecurity: true);
        RequireExpectedHandleType(handle, isDirectory: true, "isolated SSH access state");
        var security = GetHandleSecurityDescriptor(handle);
        if ((security.ControlFlags & ControlFlags.DiscretionaryAclProtected) == 0)
            throw new InvalidDataException(
                "The isolated SSH access state inherits from the legacy Agent directory and cannot be safely bridged.");
    }

    private static void SealGuardianHeldTransactionLock(SafeFileHandle updateDirectory, SealedEntry lockEntry)
    {
        try
        {
            using var unexpected = NativePath.OpenRelative(
                updateDirectory,
                lockEntry.Name,
                readFile: true,
                writeFile: false,
                delete: false,
                requireDirectory: false,
                createDisposition: NativePath.FileOpen,
                exclusive: true,
                changeSecurity: false);
        }
        catch (IOException exception) when (IsSharingViolation(exception))
        {
            // The parent is already exact and held without sharing, and the
            // observed child was non-reparse before the parent was sealed.
            // The trusted old Guardian's exclusive lock therefore prevents
            // replacement while the named security API seals this one special
            // live file.  It has no adopted content; the active journal is
            // separately sealed and retained by handle before it is parsed.
            SetExactRestrictedSecurityByName(lockEntry.Path, isDirectory: false);
            RequireExactRestrictedNamedAcl(lockEntry.Path, isDirectory: false,
                "legacy update coordination lock");
            return;
        }

        throw new InvalidDataException(
            "The legacy bridge requires the trusted Guardian to hold transaction.lock for the active update.");
    }

    private static bool IsSharingViolation(IOException exception) =>
        exception.InnerException is Win32Exception { NativeErrorCode: ErrorSharingViolation };

    private static SafeFileHandle? TryOpenLegacyRootNoSharing(string root)
    {
        var stopwatch = Stopwatch.StartNew();
        IOException? lastContention = null;
        while (true)
        {
            var handle = CreateFileW(
                root,
                FileReadAttributes | FileListDirectory | FileTraverse | ReadControl | WriteDac | WriteOwner | Synchronize,
                shareMode: 0,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics | FileFlagOpenReparsePoint,
                IntPtr.Zero);
            if (!handle.IsInvalid)
            {
                try
                {
                    RequireExpectedHandleType(handle, isDirectory: true, "legacy machine-state root");
                    return handle;
                }
                catch
                {
                    handle.Dispose();
                    throw;
                }
            }

            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            if (error is ErrorFileNotFound or ErrorPathNotFound) return null;
            if (error != ErrorSharingViolation)
                throw Win32Failure(error, "Windows could not open the legacy machine-state root without sharing.");

            lastContention = Win32Failure(error, "The legacy machine-state root is in use.");
            if (stopwatch.Elapsed >= ExclusiveOpenDeadline)
                throw new IOException(
                    $"The bridge could not acquire the legacy machine-state root exclusively within {ExclusiveOpenDeadline}.",
                    lastContention);
            Thread.Sleep(ExclusiveOpenRetryDelay);
        }
    }

    private static SafeFileHandle OpenLegacyChildNoSharing(
        SafeFileHandle parent,
        string name,
        bool isDirectory,
        string description)
    {
        var stopwatch = Stopwatch.StartNew();
        IOException? lastContention = null;
        while (true)
        {
            try
            {
                var handle = NativePath.OpenRelative(
                    parent,
                    name,
                    readFile: !isDirectory,
                    writeFile: false,
                    delete: false,
                    requireDirectory: isDirectory,
                    createDisposition: NativePath.FileOpen,
                    exclusive: true,
                    changeSecurity: true);
                try
                {
                    RequireExpectedHandleType(handle, isDirectory, description);
                    return handle;
                }
                catch
                {
                    handle.Dispose();
                    throw;
                }
            }
            catch (FileNotFoundException)
            {
                throw;
            }
            catch (DirectoryNotFoundException)
            {
                throw;
            }
            catch (IOException exception)
            {
                lastContention = exception;
                if (stopwatch.Elapsed >= ExclusiveOpenDeadline)
                    throw new IOException(
                        $"The bridge could not acquire {description} exclusively within {ExclusiveOpenDeadline}.",
                        lastContention);
                Thread.Sleep(ExclusiveOpenRetryDelay);
            }
        }
    }

    private static void SealLegacyObject(
        SafeFileHandle handle,
        bool isDirectory,
        LegacyAclClass aclClass,
        string description)
    {
        RequireExpectedHandleType(handle, isDirectory, description);
        RequireLegacyProvenanceAcl(handle, isDirectory, aclClass, description);
        SetExactRestrictedSecurity(handle, isDirectory);
        RequireExactRestrictedAcl(handle, isDirectory, description);
    }

    private static void RequireLegacyProvenanceAcl(
        SafeFileHandle handle,
        bool isDirectory,
        LegacyAclClass aclClass,
        string description)
    {
        var security = GetHandleSecurityDescriptor(handle);
        if (security.Owner is null || (!security.Owner.Equals(SystemSid) && !security.Owner.Equals(AdministratorsSid)))
            throw new UnauthorizedAccessException($"The {description} owner is not SYSTEM or Administrators.");
        var dacl = security.DiscretionaryAcl
                   ?? throw new UnauthorizedAccessException($"The {description} has no discretionary ACL.");

        var hasSystem = false;
        var hasAdministrators = false;
        foreach (GenericAce ace in dacl)
        {
            if (ace is not CommonAce common || common.AceQualifier != AceQualifier.AccessAllowed)
                throw new UnauthorizedAccessException($"The {description} has an unsupported legacy ACL entry.");

            var sid = common.SecurityIdentifier;
            if (sid.Equals(SystemSid) || sid.Equals(AdministratorsSid))
            {
                if (common.AccessMask != (int)FileSystemRights.FullControl
                    || (common.AceFlags & AceFlags.InheritOnly) != 0)
                    throw new UnauthorizedAccessException($"The {description} trusted ACL entry is not full control.");
                if (sid.Equals(SystemSid)) hasSystem = true;
                else hasAdministrators = true;
                continue;
            }

            var directFlags = common.AceFlags & ~AceFlags.Inherited;
            if (sid.Equals(CreatorOwnerSid))
            {
                if (common.AccessMask != GenericAllRights
                    || directFlags != (AceFlags.ObjectInherit | AceFlags.ContainerInherit | AceFlags.InheritOnly))
                    throw new UnauthorizedAccessException($"The {description} has an unsafe CREATOR OWNER ACL entry.");
                continue;
            }

            if (sid.Equals(BuiltinUsersSid)
                && isDirectory
                && aclClass is LegacyAclClass.Root or LegacyAclClass.RootStyleEmptyDirectory
                && common.AccessMask == RootLegacyCreateRights
                && directFlags == AceFlags.ContainerInherit)
                continue;

            // Unknown principals may retain only the standard read/execute
            // surface.  Any write, append, delete, ownership, DACL, attribute,
            // or inheritance mutation right fails before the object is sealed.
            if ((common.AccessMask & ~ReadOnlyRights) != 0)
                throw new UnauthorizedAccessException($"The {description} grants mutable access to an untrusted principal.");
        }

        if (!hasSystem || !hasAdministrators)
            throw new UnauthorizedAccessException($"The {description} lacks a trusted SYSTEM/Administrators ACL entry.");
    }

    private static void SetExactRestrictedSecurity(SafeFileHandle handle, bool isDirectory)
    {
        WithExactRestrictedSecurity(isDirectory, (ownerPointer, daclPointer) =>
        {
            var status = SetSecurityInfo(
                handle,
                SeFileObject,
                OwnerSecurityInformation | DaclSecurityInformation | ProtectedDaclSecurityInformation,
                ownerPointer,
                IntPtr.Zero,
                daclPointer,
                IntPtr.Zero);
            if (status != 0)
                throw Win32Failure(unchecked((int)status), "Windows could not seal the legacy machine-state object.");
        });
    }

    private static void SetExactRestrictedSecurityByName(string path, bool isDirectory)
    {
        WithExactRestrictedSecurity(isDirectory, (ownerPointer, daclPointer) =>
        {
            var status = SetNamedSecurityInfo(
                path,
                SeFileObject,
                OwnerSecurityInformation | DaclSecurityInformation | ProtectedDaclSecurityInformation,
                ownerPointer,
                IntPtr.Zero,
                daclPointer,
                IntPtr.Zero);
            if (status != 0)
                throw Win32Failure(unchecked((int)status), "Windows could not seal the trusted Guardian coordination lock.");
        });
    }

    private static void WithExactRestrictedSecurity(bool isDirectory, Action<IntPtr, IntPtr> apply)
    {
        var template = isDirectory
            ? (FileSystemSecurity)MachineStorageSecurity.CreateRestrictedDirectorySecurity()
            : MachineStorageSecurity.CreateRestrictedFileSecurity();
        var descriptor = new RawSecurityDescriptor(template.GetSecurityDescriptorBinaryForm(), 0);
        var owner = descriptor.Owner ?? throw new InvalidOperationException("The restricted ACL template has no owner.");
        var dacl = descriptor.DiscretionaryAcl ?? throw new InvalidOperationException("The restricted ACL template has no DACL.");
        var ownerBytes = new byte[owner.BinaryLength];
        owner.GetBinaryForm(ownerBytes, 0);
        var daclBytes = new byte[dacl.BinaryLength];
        dacl.GetBinaryForm(daclBytes, 0);
        var ownerPointer = Marshal.AllocHGlobal(ownerBytes.Length);
        var daclPointer = Marshal.AllocHGlobal(daclBytes.Length);
        try
        {
            Marshal.Copy(ownerBytes, 0, ownerPointer, ownerBytes.Length);
            Marshal.Copy(daclBytes, 0, daclPointer, daclBytes.Length);
            apply(ownerPointer, daclPointer);
        }
        finally
        {
            Marshal.FreeHGlobal(daclPointer);
            Marshal.FreeHGlobal(ownerPointer);
        }
    }

    private static void RequireExactRestrictedAcl(SafeFileHandle handle, bool isDirectory, string description)
    {
        RequireExactRestrictedAcl(GetHandleSecurityDescriptor(handle), isDirectory, description);
    }

    private static void RequireExactRestrictedNamedAcl(string path, bool isDirectory, string description)
    {
        RequireExactRestrictedAcl(GetNamedSecurityDescriptor(path), isDirectory, description);
    }

    private static void RequireExactRestrictedAcl(
        RawSecurityDescriptor security,
        bool isDirectory,
        string description)
    {
        if (security.Owner is null || (!security.Owner.Equals(SystemSid) && !security.Owner.Equals(AdministratorsSid)))
            throw new UnauthorizedAccessException($"The sealed {description} owner is not SYSTEM or Administrators.");
        if ((security.ControlFlags & ControlFlags.DiscretionaryAclProtected) == 0)
            throw new UnauthorizedAccessException($"The sealed {description} inherits an ACL.");
        var dacl = security.DiscretionaryAcl
                   ?? throw new UnauthorizedAccessException($"The sealed {description} has no discretionary ACL.");
        if (dacl.Count != 2
            || !HasExactRestrictedAce(dacl, SystemSid, isDirectory)
            || !HasExactRestrictedAce(dacl, AdministratorsSid, isDirectory))
            throw new UnauthorizedAccessException($"The sealed {description} ACL is not exactly SYSTEM and Administrators full control.");
    }

    private static bool HasExactRestrictedAce(RawAcl dacl, SecurityIdentifier expectedSid, bool isDirectory)
    {
        var expectedFlags = isDirectory
            ? AceFlags.ObjectInherit | AceFlags.ContainerInherit
            : AceFlags.None;
        return dacl.Cast<GenericAce>().Any(ace => ace is CommonAce common
                                                   && common.AceQualifier == AceQualifier.AccessAllowed
                                                   && common.SecurityIdentifier.Equals(expectedSid)
                                                   && common.AccessMask == (int)FileSystemRights.FullControl
                                                   && common.AceFlags == expectedFlags);
    }

    private static RawSecurityDescriptor GetHandleSecurityDescriptor(SafeFileHandle handle)
    {
        var status = GetSecurityInfo(
            handle,
            SeFileObject,
            OwnerSecurityInformation | DaclSecurityInformation,
            out _,
            out _,
            out _,
            out _,
            out var descriptorPointer);
        if (status != 0)
            throw Win32Failure(unchecked((int)status), "Windows could not inspect the legacy machine-state ACL.");
        return ReadSecurityDescriptorAndFree(descriptorPointer);
    }

    private static RawSecurityDescriptor GetNamedSecurityDescriptor(string path)
    {
        var status = GetNamedSecurityInfo(
            path,
            SeFileObject,
            OwnerSecurityInformation | DaclSecurityInformation,
            out _,
            out _,
            out _,
            out _,
            out var descriptorPointer);
        if (status != 0)
            throw Win32Failure(unchecked((int)status), "Windows could not inspect the trusted Guardian coordination lock ACL.");
        return ReadSecurityDescriptorAndFree(descriptorPointer);
    }

    private static RawSecurityDescriptor ReadSecurityDescriptorAndFree(IntPtr descriptorPointer)
    {
        if (descriptorPointer == IntPtr.Zero)
            throw new UnauthorizedAccessException("Windows returned no legacy machine-state security descriptor.");
        try
        {
            var length = checked((int)GetSecurityDescriptorLength(descriptorPointer));
            if (length is <= 0 or > 64 * 1024)
                throw new InvalidDataException("The legacy machine-state security descriptor has an invalid size.");
            var bytes = new byte[length];
            Marshal.Copy(descriptorPointer, bytes, 0, bytes.Length);
            return new RawSecurityDescriptor(bytes, 0);
        }
        finally
        {
            _ = LocalFree(descriptorPointer);
        }
    }

    private static void RequireExpectedHandleType(SafeFileHandle handle, bool isDirectory, string description)
    {
        if (!GetFileInformationByHandle(handle, out var information))
            throw Win32Failure(Marshal.GetLastWin32Error(), "Windows could not inspect the legacy machine-state object.");
        var attributes = (FileAttributes)information.FileAttributes;
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"The {description} is a reparse point.");
        if (((attributes & FileAttributes.Directory) != 0) != isDirectory)
            throw new InvalidDataException($"The {description} has an unexpected object type.");
    }

    private static IReadOnlyDictionary<string, SealedEntry> CaptureDirectoryEntries(
        SafeFileHandle handle,
        string directory,
        string description,
        int maximumEntries)
    {
        var captured = new Dictionary<string, SealedEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in NativePath.Enumerate(handle))
        {
            if (captured.Count >= maximumEntries)
                throw new InvalidDataException($"The {description} contains too many entries.");
            if (string.IsNullOrWhiteSpace(entry.Name)
                || !Path.GetFileName(entry.Name).Equals(entry.Name, StringComparison.Ordinal)
                || entry.Name is "." or ".."
                || entry.Name.Contains(':'))
                throw new InvalidDataException($"The {description} contains an unsafe entry name.");
            if (entry.IsReparsePoint)
                throw new InvalidDataException($"The {description} contains a reparse point: {entry.Name}");
            if (entry.IsHiddenOrSystem)
                throw new InvalidDataException($"The {description} contains a hidden or system entry: {entry.Name}");

            var path = FullPath(Path.Combine(directory, entry.Name));
            if (!PathEquals(Path.GetDirectoryName(path) ?? string.Empty, directory))
                throw new InvalidDataException($"The {description} entry escaped its sealed parent.");
            if (!captured.TryAdd(entry.Name, new SealedEntry(entry.Name, path, entry.IsDirectory)))
                throw new InvalidDataException($"The {description} contains duplicate entry names.");
        }
        return captured;
    }

    private static void RequireOnlyEntries(
        IReadOnlyDictionary<string, SealedEntry> entries,
        string description,
        Func<string, bool> allowed)
    {
        foreach (var entry in entries.Values)
            if (!allowed(entry.Name))
                throw new InvalidDataException($"The {description} contains an unknown entry: {entry.Name}");
    }

    private static SealedEntry RequireEntry(
        IReadOnlyDictionary<string, SealedEntry> entries,
        string name,
        bool isDirectory,
        string description)
    {
        if (!entries.TryGetValue(name, out var entry))
            throw new InvalidDataException($"The {description} is missing required {name} state.");
        if (entry.IsDirectory != isDirectory)
            throw new InvalidDataException($"The {description} has an unexpected type for {name}.");
        return entry;
    }

    private static void ValidateLegacyState(SealedLegacyState state)
    {
        RequireFixedMachineRootPath(state.Root);
        var entries = state.Entries(state.Root);
        var agentDirectory = RequireEntry(entries, "Agent", isDirectory: true, "legacy machine-state root").Path;
        var updateDirectory = RequireEntry(entries, "Update", isDirectory: true, "legacy machine-state root").Path;

        var config = ValidateAgentDirectory(state, agentDirectory);
        ValidateUpdateDirectory(state, updateDirectory, config);

        if (entries.TryGetValue("Ssh", out var sshDirectory))
            ValidateEmptyDirectory(state, sshDirectory.Path, "legacy SSH state");
        if (entries.TryGetValue("SetupStaging", out var setupStagingDirectory))
            ValidateEmptyDirectory(state, setupStagingDirectory.Path, "legacy setup staging");
        if (entries.TryGetValue("ControllerHandoff", out var controllerHandoffDirectory))
            ValidateEmptyDirectory(state, controllerHandoffDirectory.Path, "legacy controller handoff");
        if (entries.TryGetValue("install-receipt.json", out var receiptFile))
        {
            var receipt = ReadRegularFile(state, receiptFile.Path, MaximumSmallJsonBytes, "legacy install receipt");
            ValidateJson(receipt, "legacy install receipt");
        }
    }

    private static AgentConfig ValidateAgentDirectory(SealedLegacyState state, string directory)
    {
        state.RequireDirectory(directory, "legacy Agent state");
        var entries = state.Entries(directory);
        var configPath = RequireEntry(entries, "agent.json", isDirectory: false, "legacy Agent state").Path;
        if (!PathEquals(configPath, AppPaths.AgentConfigFile))
            throw new InvalidDataException("The legacy Agent configuration escaped its fixed path.");

        var bytes = ReadRegularFile(state, configPath, MaximumConfigBytes, "legacy Agent configuration");
        AgentConfig config;
        try
        {
            config = JsonSerializer.Deserialize<AgentConfig>(bytes, JsonDefaults.Options)
                     ?? throw new InvalidDataException("The legacy Agent configuration is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The legacy Agent configuration is malformed.", exception);
        }
        ValidateAgentConfig(config);
        return config;
    }

    private static void ValidateAgentConfig(AgentConfig config)
    {
        if (config.SchemaVersion is < 1 or > 2
            || config.DeviceId == Guid.Empty
            || config.ApiPort != 45831
            || !Enum.IsDefined(config.Role)
            || !RemoteAdministrationProtocol.IsTailscaleIpv4(config.BindAddress)
            || config.CompletedInviteId is not Guid completedInvite || completedInvite == Guid.Empty
            || config.PendingInviteId is not null
            || !string.IsNullOrEmpty(config.PendingInviteSecretProtected)
            || config.SharedRoots is null
            || config.SharedRoots.Count is < 1 or > 128
            || !IsSha256(config.AgentTokenHash))
            throw new InvalidDataException("The legacy Agent configuration is not an enrolled machine identity.");

        if (!Uri.TryCreate(config.CoordinatorUrl, UriKind.Absolute, out var coordinator)
            || coordinator.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(coordinator.UserInfo)
            || !RemoteAdministrationProtocol.IsTailscaleIpv4(coordinator.Host))
            throw new InvalidDataException("The legacy Agent configuration has no valid Tailscale coordinator identity.");

        if (config.SharedRoots.Any(pair => string.IsNullOrWhiteSpace(pair.Key)
                                           || pair.Key.Length > 128
                                           || string.IsNullOrWhiteSpace(pair.Value)
                                           || pair.Value.Length > 32 * 1024
                                           || pair.Key.Any(char.IsControl)
                                           || pair.Value.Any(char.IsControl)))
            throw new InvalidDataException("The legacy Agent configuration has invalid shared-root metadata.");

        try
        {
            var mediaKey = config.MediaSigningKey;
            var healthToken = config.UpdateHealthToken;
            if (!IsBoundedSecret(mediaKey) || !IsBoundedSecret(healthToken))
                throw new InvalidDataException("The legacy Agent configuration has an invalid protected credential.");
        }
        catch (Exception exception) when (exception is not InvalidDataException)
        {
            throw new InvalidDataException("The legacy Agent configuration has an unreadable protected credential.", exception);
        }
    }

    private static void ValidateUpdateDirectory(SealedLegacyState state, string directory, AgentConfig config)
    {
        state.RequireDirectory(directory, "legacy update state");
        if (!PathEquals(directory, AppPaths.UpdateDataDirectory))
            throw new InvalidDataException("The legacy update state escaped its fixed path.");
        var entries = state.Entries(directory);
        var journalPath = RequireEntry(entries, "state.json", isDirectory: false, "legacy update state").Path;
        _ = RequireEntry(entries, "transaction.lock", isDirectory: false, "legacy update state");
        if (!PathEquals(journalPath, AppPaths.UpdateJournalFile))
            throw new InvalidDataException("The legacy update journal escaped its fixed path.");

        var journalBytes = ReadRegularFile(state, journalPath, MaximumJournalBytes, "legacy update journal");
        UpdateJournal journal;
        try
        {
            journal = JsonSerializer.Deserialize<UpdateJournal>(journalBytes, JsonDefaults.Options)
                      ?? throw new InvalidDataException("The legacy update journal is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The legacy update journal is malformed.", exception);
        }
        ValidateBridgeJournal(journal, config, directory);

        foreach (var entry in entries.Values)
        {
            if (entry.Name.Equals("state.json", StringComparison.OrdinalIgnoreCase)
                || entry.Name.Equals("transaction.lock", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!entry.IsDirectory
                && (entry.Name.Equals("update-health-token.json", StringComparison.OrdinalIgnoreCase)
                    || entry.Name.Equals("commit-request.json", StringComparison.OrdinalIgnoreCase)
                    || entry.Name.Equals("guardian-startup-failure.json", StringComparison.OrdinalIgnoreCase)))
            {
                var bytes = ReadRegularFile(state, entry.Path, MaximumSmallJsonBytes, "legacy update metadata");
                ValidateJson(bytes, "legacy update metadata");
                if (entry.Name.Equals("commit-request.json", StringComparison.OrdinalIgnoreCase))
                    ValidateCommitRequest(bytes, journal.OperationId);
                continue;
            }

            if (entry.IsDirectory)
            {
                if (entry.Name.Equals(journal.OperationId.ToString("N"), StringComparison.OrdinalIgnoreCase))
                    ValidateAgentOperationDirectory(state, entry.Path, journal, isActive: true);
                else if (OperationDirectoryName().IsMatch(entry.Name))
                    ValidateAgentOperationDirectory(state, entry.Path, journal, isActive: false);
                else if (GuardianOperationDirectoryName().IsMatch(entry.Name))
                    ValidateGuardianOperationDirectory(state, entry.Path);
                else
                    throw new InvalidDataException($"The legacy update state contains an unknown directory: {entry.Name}");
                continue;
            }

            throw new InvalidDataException($"The legacy update state contains an unknown file: {entry.Name}");
        }
    }

    private static void ValidateBridgeJournal(UpdateJournal journal, AgentConfig config, string updateDirectory)
    {
        var bridgeVersion = RemoteAdministrationProtocol.LegacyMachineStateMigrationBridgeVersion;
        if (journal.SchemaVersion != 1
            || journal.MaintenanceBootstrap
            || journal.OperationId == Guid.Empty
            || journal.Phase != UpdatePhase.AwaitingCommit
            || !NormalizeVersion(journal.CurrentVersion).Equals("1.1.38", StringComparison.Ordinal)
            || !NormalizeVersion(journal.TargetVersion).Equals(bridgeVersion, StringComparison.Ordinal)
            || journal.Role != config.Role
            || !string.Equals(journal.Architecture, "x64", StringComparison.OrdinalIgnoreCase)
            || !RemoteAdministrationProtocol.IsTailscaleIpv4(journal.BindAddress)
            || !string.Equals(journal.BindAddress, config.BindAddress, StringComparison.Ordinal)
            || journal.PackageSize is < 1024 or > MaximumPackageBytes
            || !IsSha256(journal.PackageSha256)
            || journal.StartedAt == default
            || journal.UpdatedAt == default
            || journal.AgentProcessId < 0)
            throw new InvalidDataException("The legacy update journal is not the active 1.1.38 bridge transaction.");

        var operationDirectory = Path.Combine(updateDirectory, journal.OperationId.ToString("N"));
        RequireFixedPath(journal.PackagePath, Path.Combine(operationDirectory, "package.zip"), "update package");
        RequireFixedPath(journal.StagedAgentDirectory, Path.Combine(operationDirectory, "staged-agent"), "staged Agent");
        RequireFixedPath(journal.CandidateDirectory,
            AppPaths.AgentInstallDirectory + ".candidate-" + journal.OperationId.ToString("N"), "candidate Agent");
        RequireFixedPath(journal.RollbackDirectory,
            AppPaths.AgentInstallDirectory + ".rollback-" + journal.OperationId.ToString("N"), "rollback Agent");
        RequireFixedPath(journal.FailedCandidateDirectory,
            AppPaths.AgentInstallDirectory + ".failed-" + journal.OperationId.ToString("N"), "failed candidate Agent");
    }

    private static void ValidateAgentOperationDirectory(
        SealedLegacyState state,
        string directory,
        UpdateJournal activeJournal,
        bool isActive)
    {
        state.RequireDirectory(directory, "legacy Agent update operation");
        var packageFound = false;
        foreach (var entry in state.Entries(directory).Values)
        {
            if (entry.IsDirectory && entry.Name.Equals("staged-agent", StringComparison.OrdinalIgnoreCase))
            {
                ValidatePackagePayloadDirectory(state, entry.Path, "legacy staged Agent");
                continue;
            }
            if (!entry.IsDirectory && entry.Name.Equals("package.zip", StringComparison.OrdinalIgnoreCase))
            {
                var maximum = isActive ? activeJournal.PackageSize : MaximumPackageBytes;
                AddRegularFile(state, entry.Path, maximum, "legacy Agent update package");
                if (isActive)
                {
                    if (!PathEquals(entry.Path, activeJournal.PackagePath)
                        || state.FileLength(entry.Path) != activeJournal.PackageSize)
                        throw new InvalidDataException("The legacy active update package does not match its journal.");
                }
                packageFound = true;
                continue;
            }
            throw new InvalidDataException($"The legacy Agent update operation contains an unknown entry: {entry.Name}");
        }

        if (isActive && !packageFound)
            throw new InvalidDataException("The active legacy update operation has no verified package.");
    }

    private static void ValidateGuardianOperationDirectory(SealedLegacyState state, string directory)
    {
        state.RequireDirectory(directory, "legacy Guardian update operation");
        foreach (var entry in state.Entries(directory).Values)
        {
            if (entry.IsDirectory && entry.Name.Equals("staged-guardian", StringComparison.OrdinalIgnoreCase))
            {
                ValidatePackagePayloadDirectory(state, entry.Path, "legacy staged Guardian");
                continue;
            }
            if (!entry.IsDirectory && entry.Name.Equals("package.zip", StringComparison.OrdinalIgnoreCase))
            {
                AddRegularFile(state, entry.Path, MaximumPackageBytes, "legacy Guardian update package");
                continue;
            }
            throw new InvalidDataException($"The legacy Guardian update operation contains an unknown entry: {entry.Name}");
        }
    }

    private static void ValidatePackagePayloadDirectory(SealedLegacyState state, string root, string description)
    {
        state.RequireDirectory(root, description);
        var pending = new Stack<(string Directory, int Depth)>();
        pending.Push((root, 0));
        var entries = 0;
        long totalBytes = 0;
        while (pending.TryPop(out var current))
        {
            if (current.Depth > MaximumPayloadDepth)
                throw new InvalidDataException($"The {description} is nested too deeply.");
            foreach (var entry in state.Entries(current.Directory).Values)
            {
                if (++entries > MaximumPayloadEntries)
                    throw new InvalidDataException($"The {description} contains too many entries.");
                if (entry.IsDirectory)
                {
                    pending.Push((entry.Path, checked(current.Depth + 1)));
                    continue;
                }

                AddRegularFile(state, entry.Path, MaximumPayloadBytes, description + " file");
                totalBytes = checked(totalBytes + state.FileLength(entry.Path));
                if (totalBytes > MaximumPayloadBytes)
                    throw new InvalidDataException($"The {description} exceeds the safe size limit.");
            }
        }
    }

    private static void ValidateEmptyDirectory(SealedLegacyState state, string directory, string description)
    {
        state.RequireDirectory(directory, description);
        if (state.Entries(directory).Count != 0)
            throw new InvalidDataException($"The {description} is unexpectedly non-empty during the legacy bridge.");
    }

    private static byte[] ReadRegularFile(SealedLegacyState state, string path, int maximumBytes, string description)
    {
        var file = state.RequireFile(path, description);
        if (file.Length < 0 || file.Length > maximumBytes)
            throw new InvalidDataException($"The {description} has an invalid size.");
        return file.ReadBytes(maximumBytes, description);
    }

    private static void AddRegularFile(SealedLegacyState state, string path, long maximumBytes, string description)
    {
        var file = state.RequireFile(path, description);
        if (file.Length < 0 || file.Length > maximumBytes)
            throw new InvalidDataException($"The {description} has an invalid size.");
    }

    private static void ValidateCommitRequest(byte[] bytes, Guid operationId)
    {
        try
        {
            var request = JsonSerializer.Deserialize<UpdateCommitRequest>(bytes, JsonDefaults.Options)
                          ?? throw new InvalidDataException("The legacy update commit request is empty.");
            if (request.OperationId != operationId || request.RequestedAt == default)
                throw new InvalidDataException("The legacy update commit request does not match the active operation.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The legacy update commit request is malformed.", exception);
        }
    }

    private static void ValidateJson(byte[] bytes, string description)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32
            });
            if (document.RootElement.ValueKind is not JsonValueKind.Object and not JsonValueKind.Array)
                throw new InvalidDataException($"The {description} must be a JSON object or array.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"The {description} is malformed.", exception);
        }
    }

    private static void RequireFixedMachineRootPath(string root)
    {
        var programData = FullPath(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
        if (!PathEquals(Path.GetDirectoryName(root) ?? string.Empty, programData))
            throw new InvalidDataException("The legacy machine-state root is not a direct ProgramData child.");
        if ((File.GetAttributes(programData) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The ProgramData root is a reparse point.");
    }

    private static void RequireFixedPath(string? actual, string expected, string description)
    {
        if (string.IsNullOrWhiteSpace(actual) || !PathEquals(actual, expected))
            throw new InvalidDataException($"The legacy update journal has an unsafe {description} path.");
    }

    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static string NormalizeVersion(string? value) =>
        UpdatePackageVerifier.NormalizeVersion(value ?? string.Empty);

    private static bool IsBoundedSecret(string? value) =>
        value is { Length: >= 32 and <= 4096 } && !value.Any(char.IsControl);

    private static string FullPath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool PathEquals(string first, string second) =>
        string.Equals(FullPath(first), FullPath(second), StringComparison.OrdinalIgnoreCase);

    private static IOException Win32Failure(int error, string message) =>
        new($"{message} Windows error {error}: {new Win32Exception(error).Message}", new Win32Exception(error));

    [GeneratedRegex("^[a-f0-9]{32}$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex OperationDirectoryName();

    [GeneratedRegex("^guardian-[a-f0-9]{32}$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex GuardianOperationDirectoryName();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
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

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint GetSecurityInfo(
        SafeFileHandle handle,
        uint objectType,
        uint securityInformation,
        out IntPtr owner,
        out IntPtr group,
        out IntPtr dacl,
        out IntPtr sacl,
        out IntPtr securityDescriptor);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint SetSecurityInfo(
        SafeFileHandle handle,
        uint objectType,
        uint securityInformation,
        IntPtr owner,
        IntPtr group,
        IntPtr dacl,
        IntPtr sacl);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetNamedSecurityInfo(
        string objectName,
        uint objectType,
        uint securityInformation,
        out IntPtr owner,
        out IntPtr group,
        out IntPtr dacl,
        out IntPtr sacl,
        out IntPtr securityDescriptor);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint SetNamedSecurityInfo(
        string objectName,
        uint objectType,
        uint securityInformation,
        IntPtr owner,
        IntPtr group,
        IntPtr dacl,
        IntPtr sacl);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint GetSecurityDescriptorLength(IntPtr securityDescriptor);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    private enum LegacyAclClass
    {
        Root,
        RootStyleEmptyDirectory,
        Regular
    }

    private sealed class PayloadCounter
    {
        public int Entries { get; set; }
        public long TotalBytes { get; set; }
    }

    private sealed record SealedEntry(string Name, string Path, bool IsDirectory);

    private sealed class SealedLegacyState : IDisposable
    {
        private readonly Dictionary<string, IReadOnlyDictionary<string, SealedEntry>> _directories =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, SealedFile> _files = new(StringComparer.OrdinalIgnoreCase);

        public SealedLegacyState(string root) => Root = FullPath(root);

        public string Root { get; }

        public void AddDirectory(string path, IReadOnlyDictionary<string, SealedEntry> entries)
        {
            var full = FullPath(path);
            if (!_directories.TryAdd(full, entries))
                throw new InvalidDataException($"The sealed legacy state contains a duplicate directory: {full}");
        }

        public void AddFile(string path, long length, SafeFileHandle? readHandle)
        {
            var full = FullPath(path);
            if (!_files.TryAdd(full, new SealedFile(length, readHandle)))
                throw new InvalidDataException($"The sealed legacy state contains a duplicate file: {full}");
        }

        public IReadOnlyDictionary<string, SealedEntry> Entries(string directory)
        {
            var full = FullPath(directory);
            if (!_directories.TryGetValue(full, out var entries))
                throw new DirectoryNotFoundException($"The sealed legacy directory is missing: {full}");
            return entries;
        }

        public void RequireDirectory(string path, string description)
        {
            if (!_directories.ContainsKey(FullPath(path)))
                throw new DirectoryNotFoundException($"The {description} is missing from the sealed legacy state.");
        }

        public SealedFile RequireFile(string path, string description)
        {
            var full = FullPath(path);
            if (!_files.TryGetValue(full, out var file))
                throw new FileNotFoundException($"The {description} is missing from the sealed legacy state.", full);
            return file;
        }

        public long FileLength(string path) => RequireFile(path, "sealed legacy file").Length;

        public void Dispose()
        {
            foreach (var file in _files.Values) file.Dispose();
        }
    }

    private sealed class SealedFile : IDisposable
    {
        private SafeFileHandle? _readHandle;

        public SealedFile(long length, SafeFileHandle? readHandle)
        {
            Length = length;
            _readHandle = readHandle;
        }

        public long Length { get; }

        public byte[] ReadBytes(int maximumBytes, string description)
        {
            if (Length < 0 || Length > maximumBytes)
                throw new InvalidDataException($"The {description} has an invalid size.");
            var handle = _readHandle
                         ?? throw new InvalidOperationException($"The sealed {description} was not retained for validation.");
            using var duplicate = NativePath.Duplicate(handle);
            using var stream = new FileStream(duplicate, FileAccess.Read, 64 * 1024, isAsync: false);
            var bytes = new byte[checked((int)Length)];
            stream.ReadExactly(bytes);
            return bytes;
        }

        public void Dispose() => Interlocked.Exchange(ref _readHandle, null)?.Dispose();
    }
}
