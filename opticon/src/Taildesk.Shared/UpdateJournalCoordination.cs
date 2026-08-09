namespace Taildesk.Shared;

/// <summary>
/// Serializes cross-process ownership of the durable update transaction. The
/// operating system releases the file handle if an Agent, Setup, or Guardian
/// process crashes, so an abandoned update cannot permanently wedge the device.
/// </summary>
public static class UpdateJournalCoordination
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(250);

    public static async Task<IDisposable> AcquireAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default,
        string? path = null)
    {
        if (timeout <= TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        path = Path.GetFullPath(path ?? AppPaths.UpdateCoordinationLockFile);
        var protectedMachineLock = path.Equals(
            Path.GetFullPath(AppPaths.UpdateCoordinationLockFile), StringComparison.OrdinalIgnoreCase);
        var parent = Path.GetDirectoryName(path)
                     ?? throw new InvalidOperationException("The update coordination lock has no parent directory.");
        if (protectedMachineLock)
        {
            MachineStorageSecurity.EnsureOpticonMachineState();
            MachineStorageSecurity.RequireRestrictedDirectory(parent);
            _ = await MachineStorageSecurity.WriteRestrictedFileCreateNewAsync(
                path, new byte[] { 0 }, cancellationToken);
            MachineStorageSecurity.RequireRestrictedFile(path);
        }
        else
        {
            // A custom path is retained solely as a non-production test seam.
            Directory.CreateDirectory(parent);
        }
        var deadline = timeout == Timeout.InfiniteTimeSpan
            ? DateTimeOffset.MaxValue
            : DateTimeOffset.UtcNow.Add(timeout);
        IOException? lastContention = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var stream = new FileStream(
                    path,
                    protectedMachineLock ? FileMode.Open : FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.WriteThrough);
                if (protectedMachineLock) MachineStorageSecurity.RequireRestrictedFile(path);
                return new Lease(stream);
            }
            catch (IOException exception)
            {
                lastContention = exception;
            }
            catch (UnauthorizedAccessException exception)
            {
                lastContention = new IOException("The protected update coordination lock is inaccessible.", exception);
            }

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) break;
            await Task.Delay(remaining < RetryDelay ? remaining : RetryDelay, cancellationToken);
        }

        throw new TimeoutException(
            $"Timed out after {timeout} waiting for the fail-safe update Guardian to become idle.",
            lastContention);
    }

    private sealed class Lease(FileStream stream) : IDisposable
    {
        private FileStream? _stream = stream;

        public void Dispose() => Interlocked.Exchange(ref _stream, null)?.Dispose();
    }
}
