using System.Security.Cryptography;
using Taildesk.Shared;

namespace Taildesk.Admin;

public sealed class ScheduledTransferEngine
{
    private const int MaximumFilesPerRun = 10_000;
    private readonly AdminState _state;
    private readonly AgentClient _agents;
    private readonly ScheduledTransferStore _store;

    public ScheduledTransferEngine(AdminState state, AgentClient agents, ScheduledTransferStore store)
    {
        _state = state;
        _agents = agents;
        _store = store;
    }

    public async Task<ScheduledTransferRun> RunClaimedAsync(
        ScheduledTransferRun run,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ScheduledTransferRules.Validate(run.Definition);

            var device = _state.Config.Devices.FirstOrDefault(item => item.Id == run.Definition.DeviceId)
                         ?? throw new InvalidOperationException("The scheduled transfer's device is no longer registered.");
            var token = UnprotectAgentToken(device);
            await VerifyDeviceAsync(device, token, cancellationToken);

            IReadOnlyList<Candidate> candidates;
            if (run.RetryOfRunId.HasValue)
            {
                if (run.RetryRequiresDiscovery)
                    candidates = await DiscoverAsync(run.Definition, device, token, cancellationToken);
                else
                {
                    candidates = run.RetryCandidates
                        .Select(item => new Candidate(item.RelativePath, item.Bytes, item.Copy()))
                        .ToArray();
                    if (candidates.Count == 0)
                        throw new InvalidOperationException("The retry has no durable failed-file snapshot; no files were transferred.");
                }
            }
            else candidates = await DiscoverAsync(run.Definition, device, token, cancellationToken);

            run.FilesDiscovered = candidates.Count;
            progress?.Report($"Found {candidates.Count:N0} matching file(s) for {run.ScheduleName}.");
            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = new ScheduledTransferFileResult
                {
                    RelativePath = candidate.RelativePath,
                    Bytes = candidate.Bytes,
                    State = ScheduledTransferFileState.Failed
                };
                run.Files.Add(result);
                try
                {
                    await TransferOneAsync(run.Definition, device, token, candidate, result, cancellationToken);
                    result.State = ScheduledTransferFileState.Succeeded;
                    run.FilesTransferred++;
                    run.BytesTransferred += Math.Max(0, candidate.Bytes);
                    progress?.Report($"Transferred {candidate.RelativePath}.");
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    result.Error = SafeMessage(exception);
                    run.FilesFailed++;
                    progress?.Report($"Failed {candidate.RelativePath}: {result.Error}");
                }
            }

            run.State = run.FilesFailed == 0
                ? ScheduledTransferRunState.Succeeded
                : run.FilesTransferred > 0 ? ScheduledTransferRunState.PartiallySucceeded : ScheduledTransferRunState.Failed;
            run.Message = candidates.Count == 0 ? "No files matched the configured filter."
                : run.State == ScheduledTransferRunState.Succeeded ? $"Transferred {run.FilesTransferred:N0} file(s)."
                : $"Transferred {run.FilesTransferred:N0} file(s); {run.FilesFailed:N0} failed.";
        }
        catch (OperationCanceledException)
        {
            run.State = ScheduledTransferRunState.Failed;
            run.Message = "The scheduled transfer was cancelled.";
            throw;
        }
        catch (Exception exception)
        {
            run.State = ScheduledTransferRunState.Failed;
            run.Message = SafeMessage(exception);
        }
        finally
        {
            run.FinishedAt = DateTimeOffset.UtcNow;
            await _store.CompleteAsync(run, CancellationToken.None);
        }
        return run;
    }

    private async Task<IReadOnlyList<Candidate>> DiscoverAsync(
        ScheduledTransferDefinition definition,
        DeviceRecord device,
        string token,
        CancellationToken cancellationToken)
    {
        if (definition.Direction == ScheduledTransferDirection.Upload)
        {
            if (!Directory.Exists(definition.LocalFolder))
                throw new DirectoryNotFoundException($"The local source folder does not exist: {definition.LocalFolder}");
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = definition.Recursive,
                IgnoreInaccessible = false,
                AttributesToSkip = FileAttributes.ReparsePoint,
                ReturnSpecialDirectories = false
            };
            var candidates = Directory.EnumerateFiles(definition.LocalFolder, "*", options)
                .Select(path => new Candidate(Path.GetRelativePath(definition.LocalFolder, path).Replace('\\', '/'), new FileInfo(path).Length, null))
                .Where(item => ScheduledTransferRules.Matches(definition, item.RelativePath))
                .Take(MaximumFilesPerRun + 1)
                .ToArray();
            RequireBounded(candidates.Length);
            return candidates;
        }

        var files = new List<Candidate>();
        var folders = new Queue<string>();
        folders.Enqueue(definition.RemoteFolder);
        while (folders.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folder = folders.Dequeue();
            var listing = await _agents.GetFilesAsync(device, token, definition.RemoteRoot, folder, cancellationToken);
            foreach (var entry in listing.Entries)
            {
                var sourcePath = ScheduledTransferRules.NormalizeRemotePath(entry.RelativePath);
                if (entry.IsDirectory)
                {
                    if (definition.Recursive) folders.Enqueue(sourcePath);
                    continue;
                }
                var relative = RelativeRemote(definition.RemoteFolder, sourcePath);
                if (ScheduledTransferRules.Matches(definition, relative)) files.Add(new Candidate(relative, entry.Size, null));
                RequireBounded(files.Count);
            }
        }
        return files;
    }

    private async Task TransferOneAsync(
        ScheduledTransferDefinition definition,
        DeviceRecord device,
        string token,
        Candidate candidate,
        ScheduledTransferFileResult result,
        CancellationToken cancellationToken)
    {
        var retryingConfirmedMove = definition.Mode == ScheduledTransferMode.Move
                                    && candidate.Previous is { TransferConfirmed: true, SourceDeleted: false };
        if (definition.Direction == ScheduledTransferDirection.Upload)
        {
            _ = SafeLocalChild(definition.LocalFolder, candidate.RelativePath);
            var remoteDestination = CombineRemote(definition.RemoteFolder, candidate.RelativePath);
            result.DestinationPath = remoteDestination;
            using var source = GuardedLocalTransferSource.Open(definition.LocalFolder, candidate.RelativePath);
            var sourceBefore = await source.ComputeDigestAsync(cancellationToken);
            FileTransferDigest remoteDestinationDigest;
            if (retryingConfirmedMove)
            {
                RequireRecordedProof(candidate.Previous!);
                remoteDestinationDigest = await _agents.GetRemoteFileDigestAsync(
                    device, token, definition.RemoteRoot, remoteDestination, cancellationToken);
                RequireDigest(sourceBefore, candidate.Previous!.SourceSha256, "The local Move source changed after the first transfer.");
                if (!source.Identity.Equals(candidate.Previous.SourceIdentity, StringComparison.Ordinal))
                    throw new IOException("The local Move source was replaced after the first transfer.");
                RequireDigest(remoteDestinationDigest, candidate.Previous.DestinationSha256, "The remote Move destination changed before retry.");
                RequireSameDigest(sourceBefore, remoteDestinationDigest,
                    "The Move source and destination no longer contain identical bytes.");
            }
            else
            {
                var separator = remoteDestination.LastIndexOf('/');
                var destinationFolder = separator < 0 ? string.Empty : remoteDestination[..separator];
                var destinationFileName = separator < 0 ? remoteDestination : remoteDestination[(separator + 1)..];
                await EnsureRemoteDirectoriesAsync(device, token, definition.RemoteRoot, destinationFolder, cancellationToken);
                await _agents.UploadStreamAsync(
                    device, token, Guid.NewGuid(), source.Stream, destinationFileName, sourceBefore.Length,
                    definition.RemoteRoot, destinationFolder, definition.Overwrite, progress: null, cancellationToken);
                var sourceAfter = await source.ComputeDigestAsync(cancellationToken);
                RequireSameDigest(sourceBefore, sourceAfter, "The local source changed while it was being uploaded.");
                remoteDestinationDigest = await _agents.GetRemoteFileDigestAsync(
                    device, token, definition.RemoteRoot, remoteDestination, cancellationToken);
                RequireSameDigest(sourceBefore, remoteDestinationDigest,
                    "The Agent could not prove that the complete upload reached its destination.");
            }

            result.Bytes = sourceBefore.Length;
            result.SourceIdentity = source.Identity;
            result.SourceSha256 = sourceBefore.Sha256;
            result.DestinationSha256 = remoteDestinationDigest.Sha256;
            result.TransferConfirmed = true;

            if (definition.Mode == ScheduledTransferMode.Move)
            {
                // Delete the exact already-open source object, never a pathname that
                // could have been swapped after verification.
                source.Delete();
                result.SourceDeleted = true;
            }
            return;
        }

        var remoteSource = CombineRemote(definition.RemoteFolder, candidate.RelativePath);
        var localDestination = SafeLocalChild(definition.LocalFolder, candidate.RelativePath);
        result.DestinationPath = localDestination;
        var downloadDigest = await _agents.DownloadToRootAsync(
            device, token, definition.RemoteRoot, remoteSource,
            definition.LocalFolder, candidate.RelativePath, progress: null, cancellationToken,
            overwrite: definition.Overwrite);
        var localDigest = await GuardedLocalTransferPath.HashAsync(
            definition.LocalFolder, candidate.RelativePath, cancellationToken);
        RequireSameDigest(downloadDigest, localDigest,
            "The guarded local destination did not match the completed download.");
        result.Bytes = downloadDigest.Length;
        result.SourceSha256 = downloadDigest.Sha256;
        result.DestinationSha256 = localDigest.Sha256;
        result.TransferConfirmed = true;
        if (definition.Mode == ScheduledTransferMode.Move)
        {
            await _agents.DeleteIfMatchAsync(
                device, token, definition.RemoteRoot, remoteSource, downloadDigest, cancellationToken);
            result.SourceDeleted = true;
        }
    }

    private async Task EnsureRemoteDirectoriesAsync(
        DeviceRecord device,
        string token,
        string root,
        string destinationFolder,
        CancellationToken cancellationToken)
    {
        var current = string.Empty;
        foreach (var segment in ScheduledTransferRules.NormalizeRemotePath(destinationFolder).Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = CombineRemote(current, segment);
            try { await _agents.CreateDirectoryAsync(device, token, root, current, cancellationToken); }
            catch (InvalidOperationException exception) when (exception.Message.Contains("exists", StringComparison.OrdinalIgnoreCase)) { }
        }
    }

    private async Task VerifyDeviceAsync(DeviceRecord device, string token, CancellationToken cancellationToken)
    {
        var status = await _agents.GetStatusAsync(device, token, cancellationToken);
        if (!AgentClient.IsTailscaleIp(status.TailscaleIp)
            || !status.TailscaleIp.Equals(device.TailscaleIp, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(device.TailnetDeviceId)
            || !string.Equals(status.TailnetDeviceId, device.TailnetDeviceId, StringComparison.Ordinal))
            throw new InvalidOperationException("The authenticated Agent identity does not match the scheduled device.");
    }

    private static string UnprotectAgentToken(DeviceRecord device)
    {
        if (string.IsNullOrWhiteSpace(device.AgentTokenProtected))
            throw new InvalidOperationException("The scheduled device has no local Agent credential.");
        try { return SecretProtector.Unprotect(device.AgentTokenProtected); }
        catch (Exception exception) when (exception is CryptographicException or FormatException or System.ComponentModel.Win32Exception)
        { throw new InvalidOperationException("The scheduled device credential cannot be opened by this Windows user.", exception); }
    }

    private static string SafeLocalChild(string root, string relative)
    {
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException("The configured local transfer root does not exist.");
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("The configured local transfer root cannot be a link or junction.");
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!candidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A transfer path escaped its configured local folder.");
        var current = fullRoot.Equals(Path.GetPathRoot(fullRoot), StringComparison.OrdinalIgnoreCase)
            ? fullRoot
            : fullRoot.TrimEnd(Path.DirectorySeparatorChar);
        foreach (var segment in Path.GetRelativePath(current, candidate)
                     .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("A transfer path contains a link or junction below its configured local folder.");
        }
        return candidate;
    }

    private static string CombineRemote(string left, string right) =>
        ScheduledTransferRules.NormalizeRemotePath(string.Join('/', new[] { left, right }.Where(item => !string.IsNullOrWhiteSpace(item))));

    private static string RelativeRemote(string parent, string child)
    {
        parent = ScheduledTransferRules.NormalizeRemotePath(parent);
        child = ScheduledTransferRules.NormalizeRemotePath(child);
        if (parent.Length == 0) return child;
        var prefix = parent + "/";
        if (!child.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !child.Equals(parent, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The Agent returned a file outside the scheduled remote folder.");
        return child.Equals(parent, StringComparison.OrdinalIgnoreCase) ? string.Empty : child[prefix.Length..];
    }

    private static void RequireBounded(int count)
    {
        if (count > MaximumFilesPerRun)
            throw new InvalidDataException($"A scheduled transfer is limited to {MaximumFilesPerRun:N0} matching files per run.");
    }

    private static void RequireRecordedProof(ScheduledTransferFileResult previous)
    {
        if (string.IsNullOrWhiteSpace(previous.SourceSha256)
            || string.IsNullOrWhiteSpace(previous.DestinationSha256)
            || string.IsNullOrWhiteSpace(previous.SourceIdentity))
            throw new InvalidOperationException(
                "The earlier Move has no cryptographic source/destination proof. The source was not deleted; run a new Copy first.");
    }

    private static void RequireDigest(FileTransferDigest actual, string expectedSha256, string message)
    {
        if (!actual.Sha256.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException(message);
    }

    private static void RequireSameDigest(FileTransferDigest left, FileTransferDigest right, string message)
    {
        if (left.Length != right.Length
            || !left.Sha256.Equals(right.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException(message);
    }

    private static string SafeMessage(Exception exception)
    {
        var message = exception.GetBaseException().Message;
        return string.IsNullOrWhiteSpace(message) ? "The transfer failed without a diagnostic message."
            : message.Length <= 1000 ? message : message[..1000];
    }

    private sealed record Candidate(string RelativePath, long Bytes, ScheduledTransferFileResult? Previous);
}
