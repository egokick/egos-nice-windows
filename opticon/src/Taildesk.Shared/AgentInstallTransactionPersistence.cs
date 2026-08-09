using System.Text.Json;

namespace Taildesk.Shared;

public enum AgentInstallTransactionPhase
{
    Preparing = 0,
    CandidateReady = 1,
    PreviousMoved = 2,
    CandidateActivated = 3,
    AgentTaskApplied = 4,
    RollbackStarted = 5,
    PreviousRestored = 6,
    StateRestored = 7,
    TaskStateRestored = 8
}

public sealed class AgentInstallFileRecord
{
    public string Path { get; set; } = string.Empty;
    public long Size { get; set; }
    public string Sha256 { get; set; } = string.Empty;
}

public sealed class AgentInstallTransactionJournal
{
    public int SchemaVersion { get; set; } = 2;
    public Guid OperationId { get; set; }
    public Guid InviteId { get; set; }
    public AgentInstallTransactionPhase Phase { get; set; }
    public bool HadPreviousAgent { get; set; }
    public bool HadPreviousConfig { get; set; }
    public bool HadPreviousReceipt { get; set; }
    public bool StateSnapshotReady { get; set; }
    public bool HadPreviousTask { get; set; }
    public bool TaskSnapshotReady { get; set; }
    public string PreviousTaskXml { get; set; } = string.Empty;
    public byte[] PreviousConfig { get; set; } = [];
    public byte[] PreviousReceipt { get; set; } = [];
    public List<AgentInstallFileRecord> PreviousAgentFiles { get; set; } = [];
}

public static class AgentInstallTransactionPersistence
{
    private const int MaximumJournalBytes = 8 * 1024 * 1024;

    public static AgentInstallTransactionJournal? Load()
    {
        MachineStorageSecurity.RequireRestrictedDirectory(AppPaths.SetupStagingDirectory);
        if (!File.Exists(AppPaths.AgentInstallTransactionFile)) return null;
        var content = MachineStorageSecurity.ReadRestrictedFile(
            AppPaths.AgentInstallTransactionFile, MaximumJournalBytes);
        var journal = JsonSerializer.Deserialize<AgentInstallTransactionJournal>(content, JsonDefaults.Options)
                      ?? throw new InvalidDataException("The protected Agent installation journal is empty.");
        Validate(journal);
        return journal;
    }

    public static async Task SaveAsync(
        AgentInstallTransactionJournal journal,
        CancellationToken cancellationToken = default)
    {
        Validate(journal);
        MachineStorageSecurity.RequireRestrictedDirectory(AppPaths.SetupStagingDirectory);
        var content = JsonSerializer.SerializeToUtf8Bytes(journal, JsonDefaults.Options);
        if (content.Length is <= 0 or > MaximumJournalBytes)
            throw new InvalidDataException("The protected Agent installation journal is too large.");
        await MachineStorageSecurity.WriteRestrictedFileAtomicAsync(
            AppPaths.AgentInstallTransactionFile, content, cancellationToken);
    }

    public static void Delete()
    {
        MachineStorageSecurity.DeleteRestrictedFileIfExists(AppPaths.AgentInstallTransactionFile);
    }

    public static string CandidateDirectory(Guid operationId) =>
        TransactionDirectory("installing", operationId);

    public static string RollbackDirectory(Guid operationId) =>
        TransactionDirectory("rollback", operationId);

    public static string FailedDirectory(Guid operationId) =>
        TransactionDirectory("failed", operationId);

    private static string TransactionDirectory(string kind, Guid operationId)
    {
        if (operationId == Guid.Empty)
            throw new InvalidDataException("The Agent installation operation ID is empty.");
        return Path.Combine(AppPaths.InstallDirectory, $"Agent.{kind}-{operationId:N}");
    }

    private static void Validate(AgentInstallTransactionJournal journal)
    {
        if (journal.SchemaVersion != 2
            || journal.OperationId == Guid.Empty
            || journal.InviteId == Guid.Empty
            || !Enum.IsDefined(journal.Phase)
            || journal.HadPreviousConfig != (journal.PreviousConfig.Length > 0)
            || journal.HadPreviousReceipt != (journal.PreviousReceipt.Length > 0)
            || journal.HadPreviousTask != !string.IsNullOrEmpty(journal.PreviousTaskXml)
            || journal.TaskSnapshotReady != (journal.Phase >= AgentInstallTransactionPhase.CandidateReady)
            || journal.PreviousTaskXml.Length > 256 * 1024
            || (journal.StateSnapshotReady && journal.Phase == AgentInstallTransactionPhase.Preparing)
            || journal.HadPreviousAgent != (journal.PreviousAgentFiles.Count > 0)
            || journal.PreviousConfig.Length > 4 * 1024 * 1024
            || journal.PreviousReceipt.Length > 256 * 1024
            || journal.PreviousAgentFiles.Count > 512
            || journal.PreviousAgentFiles.Select(file => file.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != journal.PreviousAgentFiles.Count
            || journal.PreviousAgentFiles.Any(file =>
                string.IsNullOrWhiteSpace(file.Path)
                || Path.IsPathRooted(file.Path)
                || file.Path.Replace('\\', '/').Split('/').Any(part => part is "" or "." or "..")
                || file.Size <= 0
                || file.Sha256.Length != 64
                || file.Sha256.Any(character => !Uri.IsHexDigit(character) || char.IsUpper(character))))
            throw new InvalidDataException("The protected Agent installation journal is invalid.");
    }
}
