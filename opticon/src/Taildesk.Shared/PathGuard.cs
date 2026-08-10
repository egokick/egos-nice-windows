using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Taildesk.Shared;

public sealed class PathGuard
{
    private readonly Dictionary<string, string> _roots;

    public PathGuard(IReadOnlyDictionary<string, string> roots, bool includeLocalVolumes = false)
    {
        if (includeLocalVolumes)
            throw new InvalidOperationException(
                "Whole local volumes cannot be exposed through the SYSTEM Agent file API.");
        _roots = roots.ToDictionary(
            pair => pair.Key,
            pair => ValidateRemoteFileRoot(pair.Value),
            StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<RootDto> GetRoots()
    {
        return _roots.Select(pair => new RootDto
        {
            Id = pair.Key,
            DisplayName = pair.Key,
            PathHint = pair.Value
        })
            .GroupBy(root => root.PathHint, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(root => root.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public string Resolve(string rootId, string? relativePath, bool mustExist = true)
    {
        var (root, candidate, _) = ResolveLexically(rootId, relativePath);
        RejectReparseTraversal(root, candidate);
        if (mustExist && !File.Exists(candidate) && !Directory.Exists(candidate))
            throw new FileNotFoundException("The requested path does not exist.");
        return candidate;
    }

    public PathLease Acquire(
        string rootId,
        string? relativePath,
        bool readFile = false,
        bool delete = false)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Guarded filesystem handles require Windows.");
        var (root, candidate, safeRelative) = ResolveLexically(rootId, relativePath);
        var rootHandle = NativePath.OpenAbsoluteRoot(root);
        try
        {
            var finalRoot = NormalizeRootPath(NativePath.GetFinalPath(rootHandle));
            if (!finalRoot.Equals(root, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("The configured shared root resolves through another filesystem location.");
            if (safeRelative.Length == 0)
                return new PathLease(rootHandle, candidate, isDirectory: true);

            var target = NativePath.OpenRelative(
                rootHandle,
                safeRelative,
                readFile: readFile,
                writeFile: false,
                delete: delete,
                requireDirectory: false,
                createDisposition: NativePath.FileOpen);
            var isDirectory = NativePath.IsDirectory(target);
            if (isDirectory)
            {
                target.Dispose();
                target = NativePath.OpenRelative(
                    rootHandle, safeRelative, readFile: false, writeFile: false, delete,
                    requireDirectory: true, createDisposition: NativePath.FileOpen);
            }
            return new PathLease(target, candidate, isDirectory);
        }
        finally
        {
            if (safeRelative.Length != 0) rootHandle.Dispose();
        }
    }

    private (string Root, string Candidate, string SafeRelative) ResolveLexically(
        string rootId,
        string? relativePath)
    {
        var availableRoots = GetAvailableRoots();
        if (!availableRoots.TryGetValue(rootId, out var root))
            throw new UnauthorizedAccessException("That shared root is not available.");
        var suppliedPath = relativePath ?? string.Empty;
        if (Path.IsPathRooted(suppliedPath)
            || suppliedPath.StartsWith("\\\\", StringComparison.Ordinal)
            || suppliedPath.StartsWith("\\\\?\\", StringComparison.Ordinal)
            || suppliedPath.StartsWith("\\\\.\\", StringComparison.Ordinal)
            || suppliedPath.Contains(':'))
            throw new UnauthorizedAccessException("Absolute, device, UNC, and alternate-stream paths are not allowed.");

        var safeRelative = suppliedPath
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar);
        var candidate = Path.GetFullPath(Path.Combine(root, safeRelative));
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        if (!candidate.Equals(root, StringComparison.OrdinalIgnoreCase)
            && !candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("The requested path leaves the shared root.");
        return (root, candidate, safeRelative);
    }

    private IReadOnlyDictionary<string, string> GetAvailableRoots()
        => _roots;

    public static string ValidateRemoteFileRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidDataException("A configured shared root is empty.");
        var full = NormalizeRootPath(Environment.ExpandEnvironmentVariables(path));
        var volumeRoot = Path.GetPathRoot(full);
        if (!string.IsNullOrWhiteSpace(volumeRoot)
            && full.Equals(NormalizeRootPath(volumeRoot), StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("A whole local volume cannot be a shared file root.");

        var protectedRoots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
        };
        foreach (var protectedRoot in protectedRoots.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            var normalizedProtected = NormalizeRootPath(protectedRoot);
            if (IsWithin(full, normalizedProtected) || IsWithin(normalizedProtected, full))
                throw new UnauthorizedAccessException(
                    "Shared file roots cannot overlap Windows, Program Files, or ProgramData.");
        }
        return full;
    }

    private static bool IsWithin(string candidate, string root)
    {
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return candidate.Equals(root, StringComparison.OrdinalIgnoreCase)
               || candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeRootPath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static void RejectReparseTraversal(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        var current = root;
        if (HasReparsePoint(current))
            throw new UnauthorizedAccessException("Shared roots cannot be links or junctions.");
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((File.Exists(current) || Directory.Exists(current)) && HasReparsePoint(current))
                throw new UnauthorizedAccessException("Links and junctions are not followed in shared roots.");
        }
    }

    private static bool HasReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
}

public sealed record GuardedDirectoryEntry(
    string Name,
    bool IsDirectory,
    bool IsHiddenOrSystem,
    bool IsReparsePoint,
    long Size,
    DateTimeOffset LastWriteTime);

public sealed class PathLease : IDisposable
{
    private SafeFileHandle? _handle;

    internal PathLease(SafeFileHandle handle, string fullPath, bool isDirectory)
    {
        _handle = handle;
        FullPath = fullPath;
        IsDirectory = isDirectory;
    }

    public string FullPath { get; }
    public bool IsDirectory { get; }

    public Stream OpenReadStream(bool keepLeaseOpen = false)
    {
        if (IsDirectory) throw new FileNotFoundException("The requested file was not found.");
        var duplicate = NativePath.Duplicate(Handle);
        if (!keepLeaseOpen) Dispose();
        return new FileStream(duplicate, FileAccess.Read, 1024 * 1024, isAsync: false);
    }

    public FileStream OpenWriteStream()
    {
        if (IsDirectory) throw new IOException("A directory cannot be opened for writing.");
        return new FileStream(NativePath.Duplicate(Handle), FileAccess.Write, 1024 * 1024, isAsync: false);
    }

    public long Length
    {
        get
        {
            if (IsDirectory) throw new IOException("A directory does not have a file length.");
            return NativePath.GetLength(Handle);
        }
    }

    public IReadOnlyList<GuardedDirectoryEntry> Enumerate()
    {
        if (!IsDirectory) throw new IOException("The requested path is not a directory.");
        return NativePath.Enumerate(Handle);
    }

    public PathLease OpenChild(string name, bool readFile = false, bool delete = false)
    {
        RequireLeafName(name);
        var handle = NativePath.OpenRelative(
            Handle, name, readFile, writeFile: false, delete,
            requireDirectory: false, NativePath.FileOpen);
        var isDirectory = NativePath.IsDirectory(handle);
        if (isDirectory)
        {
            handle.Dispose();
            handle = NativePath.OpenRelative(
                Handle, name, readFile: false, writeFile: false, delete,
                requireDirectory: true, NativePath.FileOpen);
        }
        return new PathLease(handle, Path.Combine(FullPath, name), isDirectory);
    }

    public bool TryOpenChild(string name, bool delete, out PathLease? child)
    {
        try
        {
            child = OpenChild(name, delete: delete);
            return true;
        }
        catch (FileNotFoundException)
        {
            child = null;
            return false;
        }
    }

    public PathLease CreateFile(string name)
    {
        RequireLeafName(name);
        var handle = NativePath.OpenRelative(
            Handle, name, readFile: false, writeFile: true, delete: true,
            requireDirectory: false, NativePath.FileCreate, exclusive: true);
        return new PathLease(handle, Path.Combine(FullPath, name), isDirectory: false);
    }

    public PathLease OpenOrCreateFile(string name)
    {
        RequireLeafName(name);
        var handle = NativePath.OpenRelative(
            Handle, name, readFile: false, writeFile: true, delete: true,
            requireDirectory: false, NativePath.FileOpenIf, exclusive: true);
        return new PathLease(handle, Path.Combine(FullPath, name), isDirectory: false);
    }

    public void CreateDirectory(string name)
    {
        RequireLeafName(name);
        using var created = NativePath.OpenRelative(
            Handle, name, readFile: false, writeFile: false, delete: false,
            requireDirectory: true, NativePath.FileOpenIf);
    }

    public void RenameTo(PathLease destinationDirectory, string name)
    {
        ArgumentNullException.ThrowIfNull(destinationDirectory);
        if (!destinationDirectory.IsDirectory) throw new IOException("The rename destination is not a directory.");
        RequireLeafName(name);
        NativePath.Rename(Handle, destinationDirectory.Handle, name);
    }

    public void Delete() => NativePath.Delete(Handle);

    public void Dispose() => Interlocked.Exchange(ref _handle, null)?.Dispose();

    private SafeFileHandle Handle => _handle ?? throw new ObjectDisposedException(nameof(PathLease));

    private static void RequireLeafName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)
            || !Path.GetFileName(name).Equals(name, StringComparison.Ordinal)
            || name is "." or ".."
            || name.Contains(':'))
            throw new IOException("The file or directory name is invalid.");
    }
}

internal static class NativePath
{
    internal const uint FileOpen = 1;
    internal const uint FileCreate = 2;
    internal const uint FileOpenIf = 3;

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint DeleteAccess = 0x00010000;
    private const uint ReadControl = 0x00020000;
    private const uint WriteDac = 0x00040000;
    private const uint WriteOwner = 0x00080000;
    private const uint Synchronize = 0x00100000;
    private const uint FileListDirectory = 0x00000001;
    private const uint FileTraverse = 0x00000020;
    private const uint FileReadAttributes = 0x00000080;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileDirectoryFile = 0x00000001;
    private const uint FileSynchronousIoNonAlert = 0x00000020;
    private const uint FileNonDirectoryFile = 0x00000040;
    private const uint FileOpenReparsePoint = 0x00200000;
    private const uint ObjCaseInsensitive = 0x00000040;
    private const uint ObjDontReparse = 0x00001000;
    private const int StatusNoMoreFiles = unchecked((int)0x80000006);
    private const int StatusReparsePointEncountered = unchecked((int)0xC000050B);
    private static readonly IntPtr CurrentProcess = new(-1);

    internal static SafeFileHandle OpenAbsoluteRoot(string path)
    {
        var handle = CreateFileW(
            path,
            FileListDirectory | FileTraverse | FileReadAttributes | Synchronize,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw FromWin32(error, "Windows could not open the configured shared root.");
        }
        RequireNotReparse(handle);
        if (!IsDirectory(handle))
        {
            handle.Dispose();
            throw new DirectoryNotFoundException("The configured shared root is not a directory.");
        }
        return handle;
    }

    internal static SafeFileHandle OpenRelative(
        SafeFileHandle root,
        string relativePath,
        bool readFile,
        bool writeFile,
        bool delete,
        bool requireDirectory,
        uint createDisposition,
        bool exclusive = false,
        bool changeSecurity = false)
    {
        var access = FileReadAttributes | Synchronize;
        if (readFile) access |= GenericRead;
        if (writeFile) access |= GenericWrite;
        if (delete) access |= DeleteAccess;
        if (changeSecurity) access |= ReadControl | WriteDac | WriteOwner;
        if (requireDirectory) access |= FileListDirectory | FileTraverse;
        var options = FileSynchronousIoNonAlert | FileOpenReparsePoint
                      | (requireDirectory ? FileDirectoryFile : 0u)
                      | (readFile || writeFile ? FileNonDirectoryFile : 0u);

        using var name = new NativeUnicodeString(relativePath);
        var attributes = new ObjectAttributes
        {
            Length = Marshal.SizeOf<ObjectAttributes>(),
            RootDirectory = root.DangerousGetHandle(),
            ObjectName = name.Structure,
            Attributes = ObjCaseInsensitive | ObjDontReparse
        };
        var status = NtCreateFile(
            out var handle,
            access,
            ref attributes,
            out _,
            IntPtr.Zero,
            0,
            exclusive ? 0u : FileShareRead | FileShareWrite | FileShareDelete,
            createDisposition,
            options,
            IntPtr.Zero,
            0);
        if (status < 0)
        {
            handle?.Dispose();
            throw FromNtStatus(status, "Windows could not safely open the guarded relative path.");
        }
        bool isDirectory;
        try
        {
            RequireNotReparse(handle);
            isDirectory = IsDirectory(handle);
            if (!isDirectory) RequireSingleLink(handle);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
        if (requireDirectory && !isDirectory)
        {
            handle.Dispose();
            throw new DirectoryNotFoundException("The guarded child is not a directory.");
        }
        return handle;
    }

    internal static SafeFileHandle Duplicate(SafeFileHandle source)
    {
        if (!DuplicateHandle(
                CurrentProcess, source, CurrentProcess, out var duplicate,
                0, false, 0x00000002))
            throw FromWin32(Marshal.GetLastWin32Error(), "Windows could not duplicate the guarded file handle.");
        return duplicate;
    }

    internal static bool IsDirectory(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var information))
            throw FromWin32(Marshal.GetLastWin32Error(), "Windows could not inspect the guarded path.");
        return (information.FileAttributes & (uint)FileAttributes.Directory) != 0;
    }

    internal static long GetLength(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var information))
            throw FromWin32(Marshal.GetLastWin32Error(), "Windows could not inspect the guarded file.");
        return ((long)information.FileSizeHigh << 32) | information.FileSizeLow;
    }

    internal static string GetFinalPath(SafeFileHandle handle)
    {
        var buffer = new char[32768];
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
        if (length == 0 || length >= buffer.Length)
            throw FromWin32(Marshal.GetLastWin32Error(), "Windows could not resolve the guarded path.");
        var path = new string(buffer, 0, (int)length);
        return path.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase)
            ? "\\\\" + path[8..]
            : path.StartsWith("\\\\?\\", StringComparison.Ordinal) ? path[4..] : path;
    }

    internal static IReadOnlyList<GuardedDirectoryEntry> Enumerate(SafeFileHandle directory)
    {
        const int bufferLength = 64 * 1024;
        var buffer = Marshal.AllocHGlobal(bufferLength);
        try
        {
            var entries = new List<GuardedDirectoryEntry>();
            var restart = true;
            while (true)
            {
                var status = NtQueryDirectoryFile(
                    directory, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out _,
                    buffer, (uint)bufferLength, 1, false, IntPtr.Zero, restart);
                restart = false;
                if (status == StatusNoMoreFiles) break;
                if (status < 0) throw FromNtStatus(status, "Windows could not enumerate the guarded directory handle.");

                var offset = 0;
                while (true)
                {
                    var current = IntPtr.Add(buffer, offset);
                    var next = Marshal.ReadInt32(current, 0);
                    var lastWrite = Marshal.ReadInt64(current, 24);
                    var size = Marshal.ReadInt64(current, 40);
                    var attributes = unchecked((uint)Marshal.ReadInt32(current, 56));
                    var nameLength = Marshal.ReadInt32(current, 60);
                    var name = Marshal.PtrToStringUni(IntPtr.Add(current, 64), nameLength / 2) ?? string.Empty;
                    if (name is not "." and not "..")
                    {
                        var flags = (FileAttributes)attributes;
                        entries.Add(new GuardedDirectoryEntry(
                            name,
                            (flags & FileAttributes.Directory) != 0,
                            (flags & (FileAttributes.Hidden | FileAttributes.System)) != 0,
                            (flags & FileAttributes.ReparsePoint) != 0,
                            size,
                            new DateTimeOffset(DateTime.FromFileTimeUtc(lastWrite), TimeSpan.Zero)));
                    }
                    if (next == 0) break;
                    offset += next;
                }
            }
            return entries;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static void Rename(SafeFileHandle source, SafeFileHandle destinationDirectory, string name)
    {
        var nameBytes = System.Text.Encoding.Unicode.GetBytes(name);
        var rootOffset = IntPtr.Size == 8 ? 8 : 4;
        var lengthOffset = rootOffset + IntPtr.Size;
        var nameOffset = lengthOffset + sizeof(uint);
        var buffer = Marshal.AllocHGlobal(nameOffset + nameBytes.Length);
        try
        {
            for (var index = 0; index < nameOffset; index++) Marshal.WriteByte(buffer, index, 0);
            Marshal.WriteIntPtr(buffer, rootOffset, destinationDirectory.DangerousGetHandle());
            Marshal.WriteInt32(buffer, lengthOffset, nameBytes.Length);
            Marshal.Copy(nameBytes, 0, IntPtr.Add(buffer, nameOffset), nameBytes.Length);
            var status = NtSetInformationFile(
                source, out _, buffer, (uint)(nameOffset + nameBytes.Length), 10);
            if (status < 0)
                throw FromNtStatus(status, "Windows could not atomically promote the guarded file.");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static void Delete(SafeFileHandle handle)
    {
        var disposition = new FileDispositionInfo { DeleteFile = true };
        if (!SetFileInformationByHandle(
                handle, 4, ref disposition, (uint)Marshal.SizeOf<FileDispositionInfo>()))
            throw FromWin32(Marshal.GetLastWin32Error(), "Windows could not delete the guarded filesystem object.");
    }

    private static void RequireNotReparse(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var information))
            throw FromWin32(Marshal.GetLastWin32Error(), "Windows could not inspect the guarded path.");
        if ((information.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0)
        {
            handle.Dispose();
            throw new UnauthorizedAccessException("Links and junctions are not followed in shared roots.");
        }
    }

    private static void RequireSingleLink(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var information))
            throw FromWin32(Marshal.GetLastWin32Error(), "Windows could not inspect the guarded file link count.");
        if (information.NumberOfLinks != 1)
        {
            throw new UnauthorizedAccessException(
                "Hard-linked files are not exposed through the SYSTEM Agent file API.");
        }
    }

    private static Exception FromNtStatus(int status, string message)
    {
        if (status == StatusReparsePointEncountered)
            return new UnauthorizedAccessException("Links and junctions are not followed in shared roots.");
        return FromWin32(unchecked((int)RtlNtStatusToDosError(status)), message);
    }

    private static Exception FromWin32(int error, string message) => error switch
    {
        2 or 3 => new FileNotFoundException("The requested path does not exist.", new Win32Exception(error)),
        5 => new UnauthorizedAccessException(message, new Win32Exception(error)),
        _ => new IOException($"{message} Windows error {error}: {new Win32Exception(error).Message}", new Win32Exception(error))
    };

    private sealed class NativeUnicodeString : IDisposable
    {
        private readonly IntPtr _buffer;
        internal IntPtr Structure { get; }

        internal NativeUnicodeString(string value)
        {
            _buffer = Marshal.StringToHGlobalUni(value);
            var unicode = new UnicodeString
            {
                Length = checked((ushort)(value.Length * 2)),
                MaximumLength = checked((ushort)((value.Length + 1) * 2)),
                Buffer = _buffer
            };
            Structure = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
            Marshal.StructureToPtr(unicode, Structure, false);
        }

        public void Dispose()
        {
            Marshal.FreeHGlobal(Structure);
            Marshal.FreeHGlobal(_buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        public int Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public IntPtr Status;
        public IntPtr Information;
    }

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

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInfo
    {
        [MarshalAs(UnmanagedType.Bool)] public bool DeleteFile;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file, out ByHandleFileInformation fileInformation);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle file, [Out] char[] filePath, uint filePathLength, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(
        IntPtr sourceProcess, SafeFileHandle sourceHandle, IntPtr targetProcess,
        out SafeFileHandle targetHandle, uint desiredAccess, bool inheritHandle, uint options);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file, int fileInformationClass, ref FileDispositionInfo fileInformation, uint bufferSize);

    [DllImport("ntdll.dll")]
    private static extern int NtCreateFile(
        out SafeFileHandle fileHandle, uint desiredAccess, ref ObjectAttributes objectAttributes,
        out IoStatusBlock ioStatusBlock, IntPtr allocationSize, uint fileAttributes,
        uint shareAccess, uint createDisposition, uint createOptions,
        IntPtr eaBuffer, uint eaLength);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryDirectoryFile(
        SafeFileHandle fileHandle, IntPtr eventHandle, IntPtr apcRoutine, IntPtr apcContext,
        out IoStatusBlock ioStatusBlock, IntPtr fileInformation, uint length,
        int fileInformationClass, [MarshalAs(UnmanagedType.U1)] bool returnSingleEntry,
        IntPtr fileName, [MarshalAs(UnmanagedType.U1)] bool restartScan);

    [DllImport("ntdll.dll")]
    private static extern int NtSetInformationFile(
        SafeFileHandle fileHandle, out IoStatusBlock ioStatusBlock,
        IntPtr fileInformation, uint length, int fileInformationClass);

    [DllImport("ntdll.dll")]
    private static extern uint RtlNtStatusToDosError(int status);
}
