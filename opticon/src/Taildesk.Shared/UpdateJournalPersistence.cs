using System.Text.Json;

namespace Taildesk.Shared;

public static class UpdateJournalPersistence
{
    public static UpdateJournal? Load(string? path = null)
    {
        path ??= AppPaths.UpdateJournalFile;
        if (!File.Exists(path)) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return JsonSerializer.Deserialize<UpdateJournal>(stream, JsonDefaults.Options);
    }

    public static async Task SaveAsync(UpdateJournal journal, string? path = null, CancellationToken cancellationToken = default)
    {
        path ??= AppPaths.UpdateJournalFile;
        journal.UpdatedAt = DateTimeOffset.UtcNow;
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("The update journal has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporary = path + ".new";
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, true))
        {
            await JsonSerializer.SerializeAsync(stream, journal, JsonDefaults.Options, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, true);
    }

    public static async Task RequestCommitAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        var request = new UpdateCommitRequest { OperationId = operationId, RequestedAt = DateTimeOffset.UtcNow };
        Directory.CreateDirectory(AppPaths.UpdateDataDirectory);
        var temporary = AppPaths.UpdateCommitRequestFile + ".new";
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 16 * 1024, true))
        {
            await JsonSerializer.SerializeAsync(stream, request, JsonDefaults.Options, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, AppPaths.UpdateCommitRequestFile, true);
    }

    public static UpdateCommitRequest? LoadCommitRequest()
    {
        if (!File.Exists(AppPaths.UpdateCommitRequestFile)) return null;
        using var stream = new FileStream(AppPaths.UpdateCommitRequestFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return JsonSerializer.Deserialize<UpdateCommitRequest>(stream, JsonDefaults.Options);
    }
}
