using Taildesk.Shared;

namespace Taildesk.Agent;

public sealed class FileOperations
{
    private readonly PathGuard _paths;
    private readonly AgentConfig _config;
    private readonly SemaphoreSlim _uploadSlots;

    public FileOperations(PathGuard paths, AgentConfig config)
    {
        _paths = paths;
        _config = config;
        var concurrency = Math.Clamp(config.MaxConcurrentUploads, 1, 8);
        _uploadSlots = new SemaphoreSlim(concurrency, concurrency);
    }

    public IReadOnlyList<RootDto> GetRoots() => _paths.GetRoots();

    public FileListingDto List(string root, string? relativePath)
    {
        using var directoryLease = _paths.Acquire(root, relativePath);
        if (!directoryLease.IsDirectory)
        {
            throw new IOException("The requested path is not a directory.");
        }

        var entries = new List<FileEntryDto>();
        foreach (var item in directoryLease.Enumerate())
        {
            if (item.Name.StartsWith(".taildesk-upload-", StringComparison.OrdinalIgnoreCase)
                && item.Name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (item.IsReparsePoint)
            {
                continue;
            }
            entries.Add(new FileEntryDto
            {
                Name = item.Name,
                RelativePath = Normalize(Path.Combine(relativePath ?? string.Empty, item.Name)),
                IsDirectory = item.IsDirectory,
                Size = item.IsDirectory ? 0 : item.Size,
                LastWriteTime = item.LastWriteTime
            });
        }

        return new FileListingDto
        {
            Root = root,
            RelativePath = Normalize(relativePath ?? string.Empty),
            Entries = entries.OrderByDescending(entry => entry.IsDirectory).ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).ToList()
        };
    }

    public Stream OpenRead(string root, string relativePath)
    {
        var lease = _paths.Acquire(root, relativePath, readFile: true);
        try { return lease.OpenReadStream(); }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    public async Task<string> UploadAsync(
        string root,
        string destinationDirectory,
        string fileName,
        Guid transferId,
        long totalLength,
        long offset,
        Stream source,
        long expectedLength,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        var (safeName, temporaryName) = ValidateUpload(fileName, transferId, totalLength);
        using var directoryLease = _paths.Acquire(root, destinationDirectory);
        if (!directoryLease.IsDirectory)
        {
            throw new DirectoryNotFoundException("The destination directory does not exist.");
        }
        ThrowIfDestinationExists(directoryLease, safeName, overwrite);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(Math.Clamp(_config.MaxUploadDurationMinutes, 1, 7 * 24 * 60)));
        await _uploadSlots.WaitAsync(timeout.Token);
        PathLease? temporaryLease = null;
        try
        {
            temporaryLease = directoryLease.OpenOrCreateFile(temporaryName);
            var existingLength = temporaryLease.Length;
            if (existingLength < 0 || existingLength > totalLength)
                throw new IOException("The resumable upload partial has an invalid length.");
            if (offset != existingLength)
                throw new IOException($"The resumable upload offset changed; the Agent has {existingLength} bytes. Resume the transfer again.");
            if (expectedLength < 0 || expectedLength != totalLength - offset)
                throw new IOException("The upload body length does not match its resumable range.");

            var drive = new DriveInfo(Path.GetPathRoot(directoryLease.FullPath)!);
            if (drive.AvailableFreeSpace - _config.MinimumFreeSpaceBytes < totalLength - existingLength)
                throw new IOException("The destination does not have enough free space while preserving its configured reserve.");

            await using (var output = temporaryLease.OpenWriteStream())
            {
                output.Position = offset;
                var buffer = new byte[1024 * 1024];
                long written = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, timeout.Token)) > 0)
                {
                    written += read;
                    if (written > expectedLength || offset + written > _config.MaxUploadBytes)
                        throw new IOException("The upload exceeded its declared or configured size.");
                    await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
                }
                if (written != expectedLength)
                    throw new IOException("The upload ended before its declared size was received.");
                await output.FlushAsync(timeout.Token);
                output.Flush(flushToDisk: true);
            }

            if (temporaryLease.Length != totalLength)
                throw new IOException("The resumable upload did not reach its declared size.");

            DeleteDestinationForPromotion(directoryLease, safeName, overwrite);
            temporaryLease.RenameTo(directoryLease, safeName);
            temporaryLease.Dispose();
            temporaryLease = null;
            return Normalize(Path.Combine(destinationDirectory, safeName));
        }
        finally
        {
            temporaryLease?.Dispose();
            _uploadSlots.Release();
        }
    }

    public async Task<string> UploadLegacyAsync(
        string root,
        string destinationDirectory,
        string fileName,
        Stream source,
        long expectedLength,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        var transferId = Guid.NewGuid();
        var temporaryName = $".taildesk-upload-{transferId:N}.partial";
        try
        {
            return await UploadAsync(
                root, destinationDirectory, fileName, transferId, expectedLength, 0,
                source, expectedLength, overwrite, cancellationToken);
        }
        finally
        {
            try
            {
                using var directoryLease = _paths.Acquire(root, destinationDirectory);
                if (directoryLease.TryOpenChild(temporaryName, delete: true, out var temporaryLease))
                {
                    using (temporaryLease) temporaryLease!.Delete();
                }
            }
            catch
            {
                // Best effort cleanup preserves the legacy non-resumable contract.
            }
        }
    }

    public UploadStatusDto GetUploadStatus(
        string root,
        string destinationDirectory,
        string fileName,
        Guid transferId,
        long totalLength,
        bool overwrite)
    {
        var (safeName, temporaryName) = ValidateUpload(fileName, transferId, totalLength);
        using var directoryLease = _paths.Acquire(root, destinationDirectory);
        if (!directoryLease.IsDirectory)
            throw new DirectoryNotFoundException("The destination directory does not exist.");
        ThrowIfDestinationExists(directoryLease, safeName, overwrite);

        long received = 0;
        if (directoryLease.TryOpenChild(temporaryName, delete: false, out var temporaryLease))
        {
            using (temporaryLease)
            {
                if (temporaryLease!.IsDirectory)
                    throw new IOException("The resumable upload partial is not a file.");
                received = temporaryLease.Length;
            }
        }
        if (received < 0 || received > totalLength)
            throw new IOException("The resumable upload partial has an invalid length.");
        return new UploadStatusDto { BytesReceived = received, TotalBytes = totalLength };
    }

    public void CreateDirectory(string root, string relativePath)
    {
        var parentPath = Path.GetDirectoryName(relativePath) ?? string.Empty;
        var directoryName = Path.GetFileName(relativePath);
        using var parentLease = _paths.Acquire(root, parentPath);
        if (!parentLease.IsDirectory) throw new DirectoryNotFoundException("The parent directory does not exist.");
        parentLease.CreateDirectory(directoryName);
        using var createdLease = parentLease.OpenChild(directoryName);
        if (!createdLease.IsDirectory) throw new IOException("The requested directory path is occupied by a file.");
    }

    public void Delete(string root, string relativePath, bool recursive)
    {
        using var lease = _paths.Acquire(root, relativePath, delete: true);
        var path = lease.FullPath;
        var rootPath = GetRootPath(root);
        if (path.Equals(rootPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("A shared root cannot be deleted.");
        }

        if (lease.IsDirectory && recursive) DeleteTree(lease);
        else lease.Delete();
    }

    public async Task DeleteIfMatchAsync(
        string root,
        string relativePath,
        long expectedLength,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        if (expectedLength < 0 || string.IsNullOrWhiteSpace(expectedSha256) || expectedSha256.Length != 64)
            throw new InvalidDataException("A valid SHA-256 digest and length are required for conditional deletion.");
        using var lease = _paths.Acquire(root, relativePath, readFile: true, delete: true);
        if (lease.IsDirectory) throw new IOException("Conditional deletion is available only for files.");
        if (lease.Length != expectedLength) throw new IOException("The source file changed after transfer; it was not deleted.");
        string digest;
        await using (var stream = lease.OpenReadStream(keepLeaseOpen: true))
        {
            digest = Convert.ToHexString(
                await System.Security.Cryptography.SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
        }
        if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.ASCII.GetBytes(digest),
                System.Text.Encoding.ASCII.GetBytes(expectedSha256.ToLowerInvariant())))
            throw new IOException("The source file changed after transfer; it was not deleted.");
        lease.Delete();
    }
    public string ResolveFile(string root, string relativePath)
    {
        using var lease = _paths.Acquire(root, relativePath, readFile: true);
        if (lease.IsDirectory) throw new FileNotFoundException("The requested media file was not found.");
        return lease.FullPath;
    }

    private (string SafeName, string TemporaryName) ValidateUpload(string fileName, Guid transferId, long totalLength)
    {
        if (transferId == Guid.Empty) throw new IOException("The upload transfer ID is invalid.");
        if (totalLength <= 0 || totalLength > _config.MaxUploadBytes)
            throw new IOException("The upload size is outside this machine's configured limit.");
        var safeName = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(safeName) || !safeName.Equals(fileName, StringComparison.Ordinal))
            throw new IOException("The file name is invalid.");
        return (safeName, $".taildesk-upload-{transferId:N}.partial");
    }

    private static void ThrowIfDestinationExists(PathLease directoryLease, string safeName, bool overwrite)
    {
        if (!directoryLease.TryOpenChild(safeName, delete: false, out var destinationLease)) return;
        using (destinationLease)
        {
            if (!overwrite) throw new IOException("A file with that name already exists.");
            if (destinationLease!.IsDirectory) throw new IOException("The upload destination is a directory.");
        }
    }

    private static void DeleteDestinationForPromotion(PathLease directoryLease, string safeName, bool overwrite)
    {
        if (!directoryLease.TryOpenChild(safeName, delete: overwrite, out var destinationLease)) return;
        using (destinationLease)
        {
            if (!overwrite) throw new IOException("A file with that name already exists.");
            if (destinationLease!.IsDirectory) throw new IOException("The upload destination is a directory.");
            destinationLease.Delete();
        }
    }

    private string GetRootPath(string root) => _paths.Resolve(root, string.Empty);

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');

    private static void DeleteTree(PathLease directoryLease)
    {
        foreach (var entry in directoryLease.Enumerate())
        {
            if (entry.IsReparsePoint)
                throw new UnauthorizedAccessException("Recursive deletion stops at links and junctions.");
            using var childLease = directoryLease.OpenChild(entry.Name, delete: true);
            if (childLease.IsDirectory) DeleteTree(childLease);
            else childLease.Delete();
        }
        directoryLease.Delete();
    }
}
