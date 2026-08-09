using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Taildesk.Shared;

namespace Taildesk.Admin;

public sealed record TranscriptionTransferResult(Guid DeviceId, string DeviceName, string Destination,
    int TranscriptFiles, int AudioFiles, int ManifestFiles, long BytesTransferred);

public sealed partial class TranscriptionTransferService
{
    public const string PreferredRootId = "ContinuousTranscriber";
    private const int MaximumFiles = 20_000;
    private readonly AgentClient _agents;
    public TranscriptionTransferService(AgentClient agents) => _agents = agents;

    public static async Task<AgentConfig?> LoadLocalAgentAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(AppPaths.AgentConfigFile)) return null;
        try { return await new JsonFileStore<AgentConfig>(AppPaths.AgentConfigFile).LoadAsync(cancellationToken); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { return null; }
    }

    public static void RequireControllerAndManaged(AgentConfig? localAgent)
    {
        if (localAgent?.Role != DeviceRole.ControllerAndManaged)
            throw new UnauthorizedAccessException("Remote transcriptions require this machine's Opticon role to be ControllerAndManaged.");
    }

    public async Task<TranscriptionTransferResult> SyncAsync(DeviceRecord device, string destination,
        DateTimeOffset start, DateTimeOffset end, bool metadataOnly, bool deleteFromOrigin,
        CancellationToken cancellationToken = default)
    {
        RequireControllerAndManaged(await LoadLocalAgentAsync(cancellationToken));
        if (end < start) throw new InvalidDataException("The end date must not be before the start date.");
        destination = Path.GetFullPath(destination);
        Directory.CreateDirectory(destination);
        var token = Unprotect(device);
        await VerifyIdentityAsync(device, token, cancellationToken);
        var location = await ResolveArchiveAsync(device, token, cancellationToken);
        var files = await EnumerateAsync(device, token, location, cancellationToken);
        var temporaryRoot = Path.Combine(Path.GetTempPath(), "opticon-transcriptions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);
        var selectedAudioFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var transcriptCount = 0; var audioCount = 0; var manifestCount = 0; long bytes = 0;
        try
        {
            foreach (var file in files.Where(item => TranscriptName().IsMatch(item.RelativePath)))
            {
                var staged = await DownloadTemporaryAsync(device, token, location, file, temporaryRoot, cancellationToken);
                var bounds = ReadTranscriptBounds(staged);
                if (bounds is null || !Overlaps(bounds.Value.Start, bounds.Value.End, start, end)) continue;
                bytes += await PromoteAsync(device, token, location, file, staged, destination, deleteFromOrigin, cancellationToken);
                transcriptCount++;
            }
            if (!metadataOnly)
            {
                foreach (var file in files.Where(item => item.RelativePath.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)
                                                         && item.LastWriteTime >= start && item.LastWriteTime <= end.AddHours(1)))
                {
                    var staged = await DownloadTemporaryAsync(device, token, location, file, temporaryRoot, cancellationToken);
                    var clipEnd = file.LastWriteTime;
                    var clipStart = clipEnd.AddSeconds(-ReadWaveDuration(staged));
                    if (!Overlaps(clipStart, clipEnd, start, end)) continue;
                    bytes += await PromoteAsync(device, token, location, file, staged, destination, deleteFromOrigin, cancellationToken);
                    audioCount++;
                    selectedAudioFolders.Add(Parent(file.RelativePath));
                }
                foreach (var file in files.Where(item => item.RelativePath.EndsWith("manifest.jsonl", StringComparison.OrdinalIgnoreCase)
                                                         && selectedAudioFolders.Contains(Parent(item.RelativePath))))
                {
                    var staged = await DownloadTemporaryAsync(device, token, location, file, temporaryRoot, cancellationToken);
                    bytes += await PromoteAsync(device, token, location, file, staged, destination, deleteFromOrigin, cancellationToken);
                    manifestCount++;
                }
            }
        }
        finally { try { Directory.Delete(temporaryRoot, recursive: true); } catch { } }
        return new TranscriptionTransferResult(device.Id, DisplayName(device), destination,
            transcriptCount, audioCount, manifestCount, bytes);
    }

    public async Task<(string Root, string Folder)> ResolveArchiveAsync(
        DeviceRecord device,
        CancellationToken cancellationToken = default)
    {
        RequireControllerAndManaged(await LoadLocalAgentAsync(cancellationToken));
        var token = Unprotect(device);
        await VerifyIdentityAsync(device, token, cancellationToken);
        return await ResolveArchiveAsync(device, token, cancellationToken);
    }
    public async Task<(string Root, string Folder)> ResolveArchiveAsync(DeviceRecord device, string token,
        CancellationToken cancellationToken)
    {
        var roots = await _agents.GetRootsAsync(device, token, cancellationToken);
        foreach (var root in roots.OrderByDescending(item => item.Id.Equals(PreferredRootId, StringComparison.OrdinalIgnoreCase)))
        {
            if (root.Id.Equals(PreferredRootId, StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(Path.TrimEndingDirectorySeparator(root.PathHint)).Equals("Continuous-transcriber", StringComparison.OrdinalIgnoreCase))
                return (root.Id, string.Empty);
            var found = await FindFolderAsync(device, token, root.Id, string.Empty, 0, cancellationToken);
            if (found is not null) return (root.Id, found);
        }
        throw new DirectoryNotFoundException("This device has not shared its Continuous-transcriber folder with Opticon. Add it as a guarded shared root first.");
    }

    private async Task<string?> FindFolderAsync(DeviceRecord device, string token, string root, string folder,
        int depth, CancellationToken cancellationToken)
    {
        if (depth > 3) return null;
        FileListingDto listing;
        try { listing = await _agents.GetFilesAsync(device, token, root, folder, cancellationToken); }
        catch (Exception) when (depth > 0) { return null; }
        if (listing.Entries.Any(item => !item.IsDirectory && item.Name.Equals("transcribe_microphone.py", StringComparison.OrdinalIgnoreCase))) return folder;
        foreach (var child in listing.Entries.Where(item => item.IsDirectory
                     && (item.Name.Contains("transcrib", StringComparison.OrdinalIgnoreCase) || depth < 1)))
        {
            var found = await FindFolderAsync(device, token, root, child.RelativePath, depth + 1, cancellationToken);
            if (found is not null) return found;
        }
        return null;
    }

    private async Task<IReadOnlyList<FileEntryDto>> EnumerateAsync(DeviceRecord device, string token,
        (string Root, string Folder) location, CancellationToken cancellationToken)
    {
        var files = new List<FileEntryDto>(); var folders = new Queue<string>(); folders.Enqueue(location.Folder);
        while (folders.Count > 0)
        {
            var listing = await _agents.GetFilesAsync(device, token, location.Root, folders.Dequeue(), cancellationToken);
            foreach (var entry in listing.Entries)
            {
                var relative = Relative(location.Folder, entry.RelativePath);
                if (entry.IsDirectory)
                {
                    if (relative.Length == 0 || relative.Equals("recordings", StringComparison.OrdinalIgnoreCase)
                        || relative.StartsWith("recordings/kept", StringComparison.OrdinalIgnoreCase)) folders.Enqueue(entry.RelativePath);
                }
                else files.Add(new FileEntryDto { Name = entry.Name, RelativePath = relative, IsDirectory = false,
                    Size = entry.Size, LastWriteTime = entry.LastWriteTime });
                if (files.Count > MaximumFiles) throw new InvalidDataException($"A transcription sync is limited to {MaximumFiles:N0} files.");
            }
        }
        return files;
    }

    private async Task<string> DownloadTemporaryAsync(DeviceRecord device, string token,
        (string Root, string Folder) location, FileEntryDto file, string temporaryRoot, CancellationToken cancellationToken)
    {
        var local = Path.Combine(temporaryRoot, Guid.NewGuid().ToString("N") + Path.GetExtension(file.Name));
        await _agents.DownloadAsync(device, token, location.Root, Combine(location.Folder, file.RelativePath), local, null, cancellationToken);
        return local;
    }

    private async Task<long> PromoteAsync(DeviceRecord device, string token, (string Root, string Folder) location,
        FileEntryDto file, string staged, string destination, bool deleteFromOrigin, CancellationToken cancellationToken)
    {
        var target = Path.GetFullPath(Path.Combine(destination, file.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = Path.TrimEndingDirectorySeparator(destination) + Path.DirectorySeparatorChar;
        if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("A remote transcription path escaped its destination.");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Move(staged, target, overwrite: true);
        var digest = await GuardedLocalTransferPath.HashAsync(destination, file.RelativePath, cancellationToken);
        if (deleteFromOrigin) await _agents.DeleteIfMatchAsync(device, token, location.Root,
            Combine(location.Folder, file.RelativePath), digest, cancellationToken);
        return digest.Length;
    }

    private async Task VerifyIdentityAsync(DeviceRecord device, string token, CancellationToken cancellationToken)
    {
        var status = await _agents.GetStatusAsync(device, token, cancellationToken);
        if (!status.TailscaleIp.Equals(device.TailscaleIp, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(device.TailnetDeviceId)
            || !status.TailnetDeviceId.Equals(device.TailnetDeviceId, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("The authenticated Opticon Agent identity does not match the selected device.");
    }

    private static string Unprotect(DeviceRecord device)
    {
        if (string.IsNullOrWhiteSpace(device.AgentTokenProtected)) throw new UnauthorizedAccessException("No Agent credential is available for this device.");
        try { return SecretProtector.Unprotect(device.AgentTokenProtected); }
        catch (Exception exception) when (exception is System.Security.Cryptography.CryptographicException or FormatException or System.ComponentModel.Win32Exception)
        { throw new UnauthorizedAccessException("The Agent credential cannot be opened by this Windows user.", exception); }
    }

    private static string Relative(string parent, string child)
    {
        parent = parent.Replace('\\', '/').Trim('/'); child = child.Replace('\\', '/').Trim('/');
        if (parent.Length == 0) return child;
        return child.Equals(parent, StringComparison.OrdinalIgnoreCase) ? string.Empty
            : child.StartsWith(parent + "/", StringComparison.OrdinalIgnoreCase) ? child[(parent.Length + 1)..]
            : throw new InvalidDataException("The Agent returned a transcription path outside its shared folder.");
    }
    private static string Combine(string left, string right) => string.Join('/', new[] { left.Trim('/'), right.Trim('/') }.Where(value => value.Length > 0));
    private static string Parent(string path) => Path.GetDirectoryName(path.Replace('/', Path.DirectorySeparatorChar))?.Replace('\\', '/') ?? string.Empty;
    private static string DisplayName(DeviceRecord device) => string.IsNullOrWhiteSpace(device.Name) ? device.HostName : device.Name;
    private static bool Overlaps(DateTimeOffset fileStart, DateTimeOffset fileEnd, DateTimeOffset rangeStart, DateTimeOffset rangeEnd) => fileStart <= rangeEnd && fileEnd >= rangeStart;

    private static (DateTimeOffset Start, DateTimeOffset End)? ReadTranscriptBounds(string path)
    {
        DateTimeOffset? first = null, last = null;
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            var match = TranscriptLine().Match(line);
            if (!match.Success || !DateTime.TryParseExact(match.Groups[1].Value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)) continue;
            var value = new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), TimeZoneInfo.Local.GetUtcOffset(local));
            first = !first.HasValue || value < first ? value : first; last = !last.HasValue || value > last ? value : last;
        }
        return first.HasValue && last.HasValue ? (first.Value, last.Value) : null;
    }

    private static double ReadWaveDuration(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "RIFF") return 0;
        reader.ReadUInt32(); if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "WAVE") return 0;
        uint byteRate = 0, dataLength = 0;
        while (stream.Position + 8 <= stream.Length)
        {
            var id = Encoding.ASCII.GetString(reader.ReadBytes(4)); var length = reader.ReadUInt32();
            var next = Math.Min(stream.Length, stream.Position + length + length % 2);
            if (id == "fmt " && length >= 16) { reader.ReadUInt16(); reader.ReadUInt16(); reader.ReadUInt32(); byteRate = reader.ReadUInt32(); }
            else if (id == "data") dataLength = (uint)Math.Min(length, stream.Length - stream.Position);
            stream.Position = next;
        }
        return byteRate == 0 ? 0 : dataLength / (double)byteRate;
    }

    [GeneratedRegex(@"(^|/)transcript .+\.txt$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TranscriptName();
    [GeneratedRegex(@"^\[(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}) [^\]]+\]")]
    private static partial Regex TranscriptLine();
}