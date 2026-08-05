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
        Stream source,
        long expectedLength,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        var directory = _paths.Resolve(root, destinationDirectory);
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException("The destination directory does not exist.");
        }

        var safeName = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(safeName) || !safeName.Equals(fileName, StringComparison.Ordinal))
        {
            throw new IOException("The file name is invalid.");
        }

        var destination = _paths.Resolve(root, Path.Combine(destinationDirectory, safeName), mustExist: false);
        if (!overwrite && File.Exists(destination))
        {
            throw new IOException("A file with that name already exists.");
        }

        if (expectedLength <= 0 || expectedLength > _config.MaxUploadBytes)
            throw new IOException("The upload size is outside this machine's configured limit.");
        var drive = new DriveInfo(Path.GetPathRoot(directory)!);
        if (drive.AvailableFreeSpace - _config.MinimumFreeSpaceBytes < expectedLength)
            throw new IOException("The destination does not have enough free space while preserving its configured reserve.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(Math.Clamp(_config.MaxUploadDurationMinutes, 1, 7 * 24 * 60)));
        await _uploadSlots.WaitAsync(timeout.Token);
        var temporary = Path.Combine(directory, $".taildesk-upload-{Guid.NewGuid():N}.partial");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough))
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

            File.Move(temporary, destination, overwrite);
            return Normalize(Path.GetRelativePath(GetRootPath(root), destination));
        }
        finally
        {
            _uploadSlots.Release();
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
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
