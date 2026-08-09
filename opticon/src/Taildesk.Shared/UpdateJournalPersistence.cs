using System.Text.Json;

namespace Taildesk.Shared;

public static class UpdateJournalPersistence
{
    private const int MaximumJournalBytes = 4 * 1024 * 1024;

    public static UpdateJournal? Load(string? path = null)
    {
        path = RequireUpdatePath(path ?? AppPaths.UpdateJournalFile);
        MachineStorageSecurity.RequireRestrictedDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path)) return null;
        var content = MachineStorageSecurity.ReadRestrictedFile(path, MaximumJournalBytes);
        return JsonSerializer.Deserialize<UpdateJournal>(content, JsonDefaults.Options)
               ?? throw new InvalidDataException("The protected update journal is empty.");
    }

    public static async Task SaveAsync(UpdateJournal journal, string? path = null, CancellationToken cancellationToken = default)
    {
        path = RequireUpdatePath(path ?? AppPaths.UpdateJournalFile);
        journal.UpdatedAt = DateTimeOffset.UtcNow;
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("The update journal has no parent directory.");
        MachineStorageSecurity.RequireRestrictedDirectory(directory);
        var content = JsonSerializer.SerializeToUtf8Bytes(journal, JsonDefaults.Options);
        if (content.Length is <= 0 or > MaximumJournalBytes)
            throw new InvalidDataException("The protected update journal has an invalid size.");
        await MachineStorageSecurity.WriteRestrictedFileAtomicAsync(path, content, cancellationToken);
    }

    public static async Task RequestCommitAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        var request = new UpdateCommitRequest { OperationId = operationId, RequestedAt = DateTimeOffset.UtcNow };
        MachineStorageSecurity.RequireRestrictedDirectory(AppPaths.UpdateDataDirectory);
        var content = JsonSerializer.SerializeToUtf8Bytes(request, JsonDefaults.Options);
        await MachineStorageSecurity.WriteRestrictedFileAtomicAsync(
            AppPaths.UpdateCommitRequestFile, content, cancellationToken);
    }

    public static UpdateCommitRequest? LoadCommitRequest()
    {
        if (!File.Exists(AppPaths.UpdateCommitRequestFile)) return null;
        var content = MachineStorageSecurity.ReadRestrictedFile(
            AppPaths.UpdateCommitRequestFile, 64 * 1024);
        return JsonSerializer.Deserialize<UpdateCommitRequest>(content, JsonDefaults.Options)
               ?? throw new InvalidDataException("The protected update commit request is empty.");
    }

    private static string RequireUpdatePath(string path)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppPaths.UpdateDataDirectory));
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The update state path escaped the protected update directory.");
        return full;
    }
}
