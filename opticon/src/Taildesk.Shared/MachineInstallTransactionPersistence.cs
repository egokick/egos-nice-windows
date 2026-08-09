using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Taildesk.Shared;

public enum MachineInstallTransactionPhase
{
    Prepared = 0,
    NetworkComponentInstallStarted = 10,
    NetworkComponentReady = 20,
    NetworkEnrollmentStarted = 30,
    NetworkEnrolled = 40,
    NetworkPolicyApplied = 50,
    RemoteIsolationPrepared = 55,
    RemoteComponentInstallStarted = 60,
    RemoteComponentReady = 70,
    RemoteIsolationApplied = 75,
    RemoteConfigurationStarted = 80,
    RemoteConfigured = 90,
    AgentInstallStarted = 100,
    AgentInstalled = 110,
    FirewallConfigured = 120,
    ComponentsIntegrated = 130,
    ControllerInstalled = 140,
    AgentStartRequested = 150,
    AgentRunning = 160,
    EnrollmentWaitStarted = 170,
    EnrollmentReceiptWritten = 180
}

public sealed class MachineInstallTransactionJournal
{
    public int SchemaVersion { get; set; } = 1;
    public Guid OperationId { get; set; }
    public Guid InviteId { get; set; }
    public string SourceTransactionId { get; set; } = string.Empty;
    public string InviteCiphertextSha256 { get; set; } = string.Empty;
    public string SourceSha256 { get; set; } = string.Empty;
    public string SourceManifestSha256 { get; set; } = string.Empty;
    public MachineInstallTransactionPhase Phase { get; set; }
}

public static class MachineInstallTransactionPersistence
{
    private const int MaximumJournalBytes = 64 * 1024;
    private static readonly Regex TransactionPattern = new("^[a-f0-9]{32}$", RegexOptions.CultureInvariant);
    private static readonly Regex Sha256Pattern = new("^[a-f0-9]{64}$", RegexOptions.CultureInvariant);

    public static MachineInstallTransactionJournal Create(SourceInstallationBinding binding)
    {
        ValidateBinding(binding);
        var journal = new MachineInstallTransactionJournal
        {
            OperationId = Guid.NewGuid(),
            InviteId = binding.InviteId,
            SourceTransactionId = binding.TransactionId,
            InviteCiphertextSha256 = binding.InviteCiphertextSha256,
            SourceSha256 = binding.SourceSha256,
            SourceManifestSha256 = binding.SourceManifestSha256,
            Phase = MachineInstallTransactionPhase.Prepared
        };
        Validate(journal);
        return journal;
    }

    public static MachineInstallTransactionJournal? Load()
    {
        if (!File.Exists(AppPaths.MachineInstallTransactionFile)
            && !Directory.Exists(AppPaths.MachineInstallTransactionFile))
            return null;
        MachineStorageSecurity.RequireRestrictedDirectory(AppPaths.SetupStagingDirectory);
        if (Directory.Exists(AppPaths.MachineInstallTransactionFile))
            throw new InvalidDataException("The protected machine-install journal path is a directory.");
        var content = MachineStorageSecurity.ReadRestrictedFile(
            AppPaths.MachineInstallTransactionFile, MaximumJournalBytes);
        var journal = JsonSerializer.Deserialize<MachineInstallTransactionJournal>(content, JsonDefaults.Options)
                      ?? throw new InvalidDataException("The protected machine-install journal is empty.");
        Validate(journal);
        return journal;
    }

    public static async Task SaveAsync(
        MachineInstallTransactionJournal journal,
        CancellationToken cancellationToken = default)
    {
        Validate(journal);
        MachineStorageSecurity.RequireRestrictedDirectory(AppPaths.SetupStagingDirectory);
        var content = JsonSerializer.SerializeToUtf8Bytes(journal, JsonDefaults.Options);
        if (content.Length is <= 0 or > MaximumJournalBytes)
            throw new InvalidDataException("The protected machine-install journal is too large.");
        await MachineStorageSecurity.WriteRestrictedFileAtomicAsync(
            AppPaths.MachineInstallTransactionFile, content, cancellationToken);
    }

    public static void Delete()
    {
        MachineStorageSecurity.DeleteRestrictedFileIfExists(AppPaths.MachineInstallTransactionFile);
    }

    public static void RequireMatches(
        MachineInstallTransactionJournal journal,
        SourceInstallationBinding binding)
    {
        Validate(journal);
        ValidateBinding(binding);
        if (journal.InviteId != binding.InviteId
            || journal.SourceTransactionId != binding.TransactionId
            || !FixedHashEquals(journal.InviteCiphertextSha256, binding.InviteCiphertextSha256)
            || !FixedHashEquals(journal.SourceSha256, binding.SourceSha256)
            || !FixedHashEquals(journal.SourceManifestSha256, binding.SourceManifestSha256))
            throw new InvalidDataException(
                "A different authenticated invitation or source generation already owns machine-install recovery.");
    }

    public static void Advance(
        MachineInstallTransactionJournal journal,
        MachineInstallTransactionPhase next)
    {
        Validate(journal);
        if (!Enum.IsDefined(next) || next < journal.Phase)
            throw new InvalidDataException("The protected machine-install phase cannot move backward or become unknown.");
        journal.Phase = next;
        Validate(journal);
    }

    public static bool RequiresNetworkRollForward(MachineInstallTransactionJournal journal)
    {
        Validate(journal);
        return journal.Phase >= MachineInstallTransactionPhase.NetworkEnrollmentStarted;
    }

    private static void Validate(MachineInstallTransactionJournal journal)
    {
        if (journal.SchemaVersion != 1
            || journal.OperationId == Guid.Empty
            || journal.InviteId == Guid.Empty
            || !TransactionPattern.IsMatch(journal.SourceTransactionId)
            || !Sha256Pattern.IsMatch(journal.InviteCiphertextSha256)
            || !Sha256Pattern.IsMatch(journal.SourceSha256)
            || !Sha256Pattern.IsMatch(journal.SourceManifestSha256)
            || !Enum.IsDefined(journal.Phase))
            throw new InvalidDataException("The protected machine-install journal is invalid.");
    }

    private static void ValidateBinding(SourceInstallationBinding binding)
    {
        if (binding.InviteId == Guid.Empty
            || !TransactionPattern.IsMatch(binding.TransactionId)
            || !Sha256Pattern.IsMatch(binding.InviteCiphertextSha256)
            || !Sha256Pattern.IsMatch(binding.SourceSha256)
            || !Sha256Pattern.IsMatch(binding.SourceManifestSha256))
            throw new InvalidDataException("The active source-installation binding is invalid.");
    }

    private static bool FixedHashEquals(string left, string right)
    {
        return left.Length == right.Length
               && CryptographicOperations.FixedTimeEquals(
                   System.Text.Encoding.ASCII.GetBytes(left),
                   System.Text.Encoding.ASCII.GetBytes(right));
    }
}
