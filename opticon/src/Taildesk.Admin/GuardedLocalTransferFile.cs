using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using Taildesk.Shared;

namespace Taildesk.Admin;

public readonly record struct FileTransferDigest(long Length, string Sha256)
{
    public static async Task<FileTransferDigest> ComputeAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek)
            throw new InvalidOperationException("A seekable readable stream is required for transfer verification.");

        stream.Position = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        long length = 0;
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                length += read;
            }
            return new FileTransferDigest(length, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
        }
        finally
        {
            stream.Position = 0;
        }
    }
}

internal sealed class GuardedLocalTransferTarget : IDisposable
{
    private readonly PathLease _directory;
    private readonly PathLease _temporary;
    private readonly string _fileName;
    private bool _promoted;
    private bool _disposed;

    private GuardedLocalTransferTarget(PathLease directory, PathLease temporary, string fileName)
    {
        _directory = directory;
        _temporary = temporary;
        _fileName = fileName;
    }

    public string FullPath => Path.Combine(_directory.FullPath, _fileName);

    public static GuardedLocalTransferTarget Create(string root, string relativePath)
    {
        var (directory, fileName) = GuardedLocalTransferPath.OpenParent(root, relativePath, createDirectories: true);
        try
        {
            for (var attempt = 0; attempt < 10; attempt++)
            {
                var temporaryName = $".taildesk-{Guid.NewGuid():N}.partial";
                try
                {
                    return new GuardedLocalTransferTarget(directory, directory.CreateFile(temporaryName), fileName);
                }
                catch (IOException) when (attempt < 9) { }
            }
            throw new IOException("Windows could not allocate a unique guarded transfer file.");
        }
        catch
        {
            directory.Dispose();
            throw;
        }
    }

    public FileStream OpenWriteStream() => _temporary.OpenWriteStream();

    public void Promote(bool overwrite)
    {
        ThrowIfDisposed();
        if (_directory.TryOpenChild(_fileName, delete: overwrite, out var existing))
        {
            using (existing)
            {
                if (existing!.IsDirectory)
                    throw new IOException("The local transfer destination is a directory.");
                if (!overwrite)
                    throw new IOException("The local destination file already exists; enable overwrite to replace it.");
                existing.Delete();
            }
        }

        _temporary.RenameTo(_directory, _fileName);
        _promoted = true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (!_promoted)
        {
            try { _temporary.Delete(); }
            catch { }
        }
        _temporary.Dispose();
        _directory.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(GuardedLocalTransferTarget));
    }
}

internal sealed class GuardedLocalTransferSource : IDisposable
{
    private readonly FileStream _stream;
    private bool _deleted;

    private GuardedLocalTransferSource(FileStream stream, string fileName)
    {
        _stream = stream;
        FileName = fileName;
        Identity = ReadIdentity(stream.SafeFileHandle);
    }

    public Stream Stream => _stream;
    public string FileName { get; }
    public string Identity { get; }

    public static GuardedLocalTransferSource Open(string root, string relativePath)
    {
        var (directory, fileName) = GuardedLocalTransferPath.OpenParent(root, relativePath, createDirectories: false);
        try
        {
            using var source = directory.OpenChild(fileName, readFile: true, delete: true);
            if (source.IsDirectory) throw new FileNotFoundException("The local transfer source is not a file.");
            var stream = source.OpenReadStream() as FileStream
                         ?? throw new InvalidOperationException("The guarded source did not expose a file handle.");
            return new GuardedLocalTransferSource(stream, fileName);
        }
        finally
        {
            directory.Dispose();
        }
    }

    public Task<FileTransferDigest> ComputeDigestAsync(CancellationToken cancellationToken) =>
        FileTransferDigest.ComputeAsync(_stream, cancellationToken);

    public void Delete()
    {
        if (_deleted) return;
        var disposition = new FileDispositionInfo { DeleteFile = true };
        if (!SetFileInformationByHandle(
                _stream.SafeFileHandle,
                FileDispositionInfoClass,
                ref disposition,
                (uint)Marshal.SizeOf<FileDispositionInfo>()))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not delete the verified local source file.");
        _deleted = true;
    }

    public void Dispose() => _stream.Dispose();

    private const int FileDispositionInfoClass = 4;

    private static string ReadIdentity(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var information))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not identify the guarded local source file.");
        return $"{information.VolumeSerialNumber:x8}:{information.FileIndexHigh:x8}{information.FileIndexLow:x8}";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInfo
    {
        [MarshalAs(UnmanagedType.Bool)] public bool DeleteFile;
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

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file,
        int fileInformationClass,
        ref FileDispositionInfo fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation fileInformation);
}

internal static class GuardedLocalTransferPath
{
    public static void EnsureDirectory(string root, string relativeDirectory)
    {
        var probe = string.IsNullOrWhiteSpace(relativeDirectory) || relativeDirectory == "."
            ? ".taildesk-directory-probe"
            : Path.Combine(relativeDirectory, ".taildesk-directory-probe");
        var (directory, _) = OpenParent(root, probe, createDirectories: true);
        directory.Dispose();
    }

    public static async Task<FileTransferDigest> HashAsync(
        string root,
        string relativePath,
        CancellationToken cancellationToken)
    {
        using var source = GuardedLocalTransferSource.Open(root, relativePath);
        return await source.ComputeDigestAsync(cancellationToken);
    }

    public static (PathLease Directory, string FileName) OpenParent(
        string root,
        string relativePath,
        bool createDirectories)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            throw new DirectoryNotFoundException("The configured local transfer root does not exist.");
        if (string.IsNullOrWhiteSpace(relativePath)
            || Path.IsPathRooted(relativePath)
            || relativePath.StartsWith("\\\\", StringComparison.Ordinal)
            || relativePath.Contains(':'))
            throw new InvalidDataException("The local transfer path must be relative to its configured root.");

        var segments = relativePath
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
            throw new InvalidDataException("The local transfer path contains an unsafe segment.");
        var fileName = segments[^1];
        if (!Path.GetFileName(fileName).Equals(fileName, StringComparison.Ordinal)
            || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidDataException("The local transfer file name is invalid.");

        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var candidate = Path.GetFullPath(Path.Combine(fullRoot, string.Join(Path.DirectorySeparatorChar, segments)));
        var prefix = fullRoot + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The local transfer path escaped its configured root.");

        var guard = new PathGuard(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["local-transfer"] = fullRoot
        });
        PathLease current = guard.Acquire("local-transfer", string.Empty);
        try
        {
            foreach (var segment in segments[..^1])
            {
                if (!current.TryOpenChild(segment, delete: false, out var child))
                {
                    if (!createDirectories)
                        throw new DirectoryNotFoundException("A local transfer directory no longer exists.");
                    current.CreateDirectory(segment);
                    child = current.OpenChild(segment);
                }
                if (!child!.IsDirectory)
                {
                    child.Dispose();
                    throw new IOException("A local transfer path component is not a directory.");
                }
                current.Dispose();
                current = child;
            }
            return (current, fileName);
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }
}
