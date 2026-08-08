using System.Text.Json;

namespace Taildesk.Shared;

public sealed class UpdateGuardianStartupFailure
{
    public int SchemaVersion { get; set; } = 1;
    public DateTimeOffset RecordedAt { get; set; }
    public string Mode { get; set; } = string.Empty;
    public Guid OperationId { get; set; }
    public string GuardianVersion { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
}

public static class UpdateGuardianStartupDiagnostics
{
    private const int MaximumFileBytes = 64 * 1024;
    private const int MaximumErrorCharacters = 16 * 1024;

    public static void Clear()
    {
        if (File.Exists(AppPaths.UpdateGuardianStartupFailureFile))
            File.Delete(AppPaths.UpdateGuardianStartupFailureFile);
    }

    public static UpdateGuardianStartupFailure? Read()
    {
        var path = AppPaths.UpdateGuardianStartupFailureFile;
        if (!File.Exists(path)) return null;
        var info = new FileInfo(path);
        if (info.Length is <= 0 or > MaximumFileBytes)
            throw new InvalidDataException("The Guardian startup diagnostic has an invalid size.");
        var failure = JsonSerializer.Deserialize<UpdateGuardianStartupFailure>(
            File.ReadAllText(path), JsonDefaults.Options)
            ?? throw new InvalidDataException("The Guardian startup diagnostic is empty.");
        if (failure.SchemaVersion != 1
            || failure.RecordedAt == default
            || string.IsNullOrWhiteSpace(failure.Mode)
            || string.IsNullOrWhiteSpace(failure.Error))
            throw new InvalidDataException("The Guardian startup diagnostic is invalid.");
        return failure;
    }

    public static void TryWrite(string mode, Exception exception)
    {
        try
        {
            var journal = TryReadJournal();
            var error = exception.ToString();
            if (error.Length > MaximumErrorCharacters)
                error = error[..MaximumErrorCharacters];
            var failure = new UpdateGuardianStartupFailure
            {
                RecordedAt = DateTimeOffset.UtcNow,
                Mode = mode,
                OperationId = journal?.OperationId ?? Guid.Empty,
                GuardianVersion = UpdatePackageVerifier.NormalizeVersion(
                    typeof(UpdateGuardianStartupDiagnostics).Assembly.GetName().Version?.ToString() ?? string.Empty),
                Error = error
            };
            var path = AppPaths.UpdateGuardianStartupFailureFile;
            Directory.CreateDirectory(Path.GetDirectoryName(path)
                                      ?? throw new InvalidOperationException("The Guardian diagnostic has no parent directory."));
            var temporary = path + "." + Environment.ProcessId + ".new";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(failure, JsonDefaults.Options));
                File.Move(temporary, path, true);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            }
        }
        catch
        {
            // Startup diagnostics are best-effort and must never mask the
            // original Guardian failure or alter the recovery decision.
        }
    }

    private static UpdateJournal? TryReadJournal()
    {
        try { return UpdateJournalPersistence.Load(); }
        catch { return null; }
    }
}
