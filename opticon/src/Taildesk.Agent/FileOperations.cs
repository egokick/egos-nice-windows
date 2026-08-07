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
            if (item.IsHiddenOrSystem || item.IsReparsePoint)
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
        Stream source,
        long expectedLength,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        using var directoryLease = _paths.Acquire(root, destinationDirectory);
        if (!directoryLease.IsDirectory)
        {
            throw new DirectoryNotFoundException("The destination directory does not exist.");
        }
        var directory = directoryLease.FullPath;

        var safeName = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(safeName) || !safeName.Equals(fileName, StringComparison.Ordinal))
        {
            throw new IOException("The file name is invalid.");
        }

        if (expectedLength <= 0 || expectedLength > _config.MaxUploadBytes)
            throw new IOException("The upload size is outside this machine's configured limit.");
        var drive = new DriveInfo(Path.GetPathRoot(directory)!);
        if (drive.AvailableFreeSpace - _config.MinimumFreeSpaceBytes < expectedLength)
            throw new IOException("The destination does not have enough free space while preserving its configured reserve.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(Math.Clamp(_config.MaxUploadDurationMinutes, 1, 7 * 24 * 60)));
        await _uploadSlots.WaitAsync(timeout.Token);
        var temporaryName = $".taildesk-upload-{Guid.NewGuid():N}.partial";
        PathLease? temporaryLease = null;
        try
        {
            temporaryLease = directoryLease.CreateFile(temporaryName);
            await using (var output = temporaryLease.OpenWriteStream())
            {
                var buffer = new byte[1024 * 1024];
                long written = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, timeout.Token)) > 0)
                {
                    written += read;
                    if (written > expectedLength || written > _config.MaxUploadBytes)
                        throw new IOException("The upload exceeded its declared or configured size.");
                    await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
                }
                if (written != expectedLength) throw new IOException("The upload ended before its declared size was received.");
                await output.FlushAsync(timeout.Token);
            }

            if (directoryLease.TryOpenChild(safeName, delete: overwrite, out var destinationLease))
            {
                using (destinationLease)
                {
                    if (!overwrite) throw new IOException("A file with that name already exists.");
                    if (destinationLease!.IsDirectory) throw new IOException("The upload destination is a directory.");
                    destinationLease.Delete();
                }
            }
            temporaryLease.RenameTo(directoryLease, safeName);
            temporaryLease.Dispose();
            temporaryLease = null;
            return Normalize(Path.Combine(destinationDirectory, safeName));
        }
        finally
        {
            if (temporaryLease is not null)
            {
                try { temporaryLease.Delete(); } catch { }
                temporaryLease.Dispose();
            }
            _uploadSlots.Release();
        }
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

    public string ResolveFile(string root, string relativePath) => _paths.Resolve(root, relativePath);

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
