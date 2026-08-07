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
        var fullPath = _paths.Resolve(root, relativePath);
        if (!Directory.Exists(fullPath))
        {
            throw new IOException("The requested path is not a directory.");
        }

        var entries = new List<FileEntryDto>();
        foreach (var item in new DirectoryInfo(fullPath).EnumerateFileSystemInfos())
        {
            try
            {
                if (item.Name.StartsWith(".taildesk-upload-", StringComparison.OrdinalIgnoreCase)
                    && item.Name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase))
                    continue;
                if ((item.Attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) != 0)
                {
                    continue;
                }

                var isDirectory = (item.Attributes & FileAttributes.Directory) != 0;
                entries.Add(new FileEntryDto
                {
                    Name = item.Name,
                    RelativePath = Normalize(Path.GetRelativePath(GetRootPath(root), item.FullName)),
                    IsDirectory = isDirectory,
                    Size = isDirectory ? 0 : ((FileInfo)item).Length,
                    LastWriteTime = item.LastWriteTimeUtc
                });
            }
            catch (UnauthorizedAccessException)
            {
                // Skip filesystem entries the service account cannot read.
            }
        }

        return new FileListingDto
        {
            Root = root,
            RelativePath = Normalize(relativePath ?? string.Empty),
            Entries = entries.OrderByDescending(entry => entry.IsDirectory).ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).ToList()
        };
    }

    public FileStream OpenRead(string root, string relativePath)
    {
        var path = _paths.Resolve(root, relativePath);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The requested file was not found.");
        }

        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
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
        var (directory, destination, temporary) = ResolveUpload(
            root, destinationDirectory, fileName, transferId, totalLength);
        if (!overwrite && File.Exists(destination))
            throw new IOException("A file with that name already exists.");
        var existingLength = ResumableTransferFile.GetValidatedLength(temporary, totalLength);
        if (offset != existingLength)
            throw new IOException($"The resumable upload offset changed; the Agent has {existingLength} bytes. Resume the transfer again.");
        if (expectedLength < 0 || expectedLength != totalLength - offset)
            throw new IOException("The upload body length does not match its resumable range.");
        var drive = new DriveInfo(Path.GetPathRoot(directory)!);
        if (drive.AvailableFreeSpace - _config.MinimumFreeSpaceBytes < totalLength - existingLength)
            throw new IOException("The destination does not have enough free space while preserving its configured reserve.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(Math.Clamp(_config.MaxUploadDurationMinutes, 1, 7 * 24 * 60)));
        await _uploadSlots.WaitAsync(timeout.Token);
        try
        {
            await ResumableTransferFile.AppendToLengthAsync(
                temporary, source, offset, totalLength, _config.MaxUploadBytes, timeout.Token);

            File.Move(temporary, destination, overwrite);
            return Normalize(Path.GetRelativePath(GetRootPath(root), destination));
        }
        finally
        {
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
        var (_, _, temporary) = ResolveUpload(
            root, destinationDirectory, fileName, transferId, expectedLength);
        try
        {
            return await UploadAsync(
                root, destinationDirectory, fileName, transferId, expectedLength, 0,
                source, expectedLength, overwrite, cancellationToken);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
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
        var (_, destination, temporary) = ResolveUpload(
            root, destinationDirectory, fileName, transferId, totalLength);
        if (!overwrite && File.Exists(destination))
            throw new IOException("A file with that name already exists.");
        var received = ResumableTransferFile.GetValidatedLength(temporary, totalLength);
        return new UploadStatusDto { BytesReceived = received, TotalBytes = totalLength };
    }

    private (string Directory, string Destination, string Temporary) ResolveUpload(
        string root,
        string destinationDirectory,
        string fileName,
        Guid transferId,
        long totalLength)
    {
        if (transferId == Guid.Empty) throw new IOException("The upload transfer ID is invalid.");
        if (totalLength <= 0 || totalLength > _config.MaxUploadBytes)
            throw new IOException("The upload size is outside this machine's configured limit.");
        var directory = _paths.Resolve(root, destinationDirectory);
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException("The destination directory does not exist.");
        var safeName = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(safeName) || !safeName.Equals(fileName, StringComparison.Ordinal))
            throw new IOException("The file name is invalid.");
        var destination = _paths.Resolve(root, Path.Combine(destinationDirectory, safeName), mustExist: false);
        var temporary = _paths.Resolve(
            root,
            Path.Combine(destinationDirectory, $".taildesk-upload-{transferId:N}.partial"),
            mustExist: false);
        return (directory, destination, temporary);
    }

    public void CreateDirectory(string root, string relativePath)
    {
        var path = _paths.Resolve(root, relativePath, mustExist: false);
        Directory.CreateDirectory(path);
    }

    public void Delete(string root, string relativePath, bool recursive)
    {
        var path = _paths.Resolve(root, relativePath);
        var rootPath = GetRootPath(root);
        if (path.Equals(rootPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("A shared root cannot be deleted.");
        }

        if (File.Exists(path))
        {
            File.Delete(path);
        }
        else
        {
            if (recursive) EnsureTreeHasNoReparsePoints(path);
            Directory.Delete(path, recursive);
        }
    }

    public string ResolveFile(string root, string relativePath) => _paths.Resolve(root, relativePath);

    private string GetRootPath(string root) => _paths.Resolve(root, string.Empty);

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');

    private static void EnsureTreeHasNoReparsePoints(string directory)
    {
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(current))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new UnauthorizedAccessException("Recursive deletion stops at links and junctions.");
                }
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
            }
        }
    }
}
