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
    /// <summary>
    /// Schema 2 turns the original coarse roll-forward marker into the Setup
    /// recovery record.  The specialized Agent and Guardian journals continue
    /// to own their respective atomic swaps; this journal records the desired
    /// machine state and decisions that must survive a relaunch or reboot.
    /// </summary>
    public int SchemaVersion { get; set; } = 2;
    public Guid OperationId { get; set; }
    public Guid InviteId { get; set; }
    public string SourceTransactionId { get; set; } = string.Empty;
    public string InviteCiphertextSha256 { get; set; } = string.Empty;
    public string SourceSha256 { get; set; } = string.Empty;
    public string SourceManifestSha256 { get; set; } = string.Empty;
    public MachineInstallTransactionPhase Phase { get; set; }

    public string CurrentOperation { get; set; } = "DiscoverState";
    public string LastVerifiedPostcondition { get; set; } = string.Empty;
    public List<string> RepairsAttempted { get; set; } = [];
    public List<string> ResourcesChangedByOpticon { get; set; } = [];
    public Dictionary<string, string> PreviousComponentVersions { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public string TailscaleNodeIdentity { get; set; } = string.Empty;
    public bool TailscaleReauthenticationApproved { get; set; }
    public bool RebootPending { get; set; }
    public string PendingUserDecision { get; set; } = string.Empty;
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
        UpgradeLegacyJournal(journal);
        Validate(journal);
        return journal;
    }

    /// <summary>
    /// A torn or malformed journal is not trusted as a transaction boundary.
    /// Once its fixed path and ACL are proven protected, quarantine it and let
    /// Setup reconstruct desired state from the authenticated source binding.
    /// ACL/path failures are deliberately not recovered here.
    /// </summary>
    public static MachineInstallTransactionJournal? LoadRecoverably(
        out bool corruptJournalQuarantined)
    {
        corruptJournalQuarantined = false;
        if (!File.Exists(AppPaths.MachineInstallTransactionFile))
            return Load();
        MachineStorageSecurity.RequireRestrictedDirectory(AppPaths.SetupStagingDirectory);
        MachineStorageSecurity.RequireRestrictedFile(AppPaths.MachineInstallTransactionFile);
        try
        {
            return Load();
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException)
        {
            MachineStorageSecurity.QuarantineRestrictedFile(
                AppPaths.MachineInstallTransactionFile, "corrupt");
            corruptJournalQuarantined = true;
            return null;
        }
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

    /// <summary>
    /// Records a repair only after the repair's postcondition has been checked.
    /// It is intentionally append-only so a resumed installation can explain
    /// what it changed without trusting that a previous phase is still healthy.
    /// </summary>
    public static void RecordVerifiedRepair(
        MachineInstallTransactionJournal journal,
        string operation,
        bool repaired,
        string postcondition,
        string? resourceChanged = null)
    {
        ArgumentNullException.ThrowIfNull(journal);
        RequireShortText(operation, nameof(operation));
        RequireShortText(postcondition, nameof(postcondition));
        if (resourceChanged is not null) RequireShortText(resourceChanged, nameof(resourceChanged));
        UpgradeLegacyJournal(journal);
        if (repaired) AddDistinct(journal.RepairsAttempted, operation);
        if (resourceChanged is not null) AddDistinct(journal.ResourcesChangedByOpticon, resourceChanged);
        journal.CurrentOperation = operation;
        journal.LastVerifiedPostcondition = postcondition;
        Validate(journal);
    }

    public static void RecordPreviousComponentVersion(
        MachineInstallTransactionJournal journal,
        string component,
        string version)
    {
        ArgumentNullException.ThrowIfNull(journal);
        RequireShortText(component, nameof(component));
        RequireShortText(version, nameof(version));
        UpgradeLegacyJournal(journal);
        journal.PreviousComponentVersions[component] = version;
        Validate(journal);
    }

    public static void RecordTailscaleDecision(
        MachineInstallTransactionJournal journal,
        bool reauthenticationApproved,
        string? nodeIdentity = null)
    {
        ArgumentNullException.ThrowIfNull(journal);
        if (nodeIdentity is not null) RequireShortText(nodeIdentity, nameof(nodeIdentity));
        UpgradeLegacyJournal(journal);
        journal.TailscaleReauthenticationApproved = reauthenticationApproved;
        if (nodeIdentity is not null) journal.TailscaleNodeIdentity = nodeIdentity;
        journal.PendingUserDecision = reauthenticationApproved ? string.Empty : "TailscaleReauthentication";
        Validate(journal);
    }

    public static void RecordBlocked(
        MachineInstallTransactionJournal journal,
        string operation,
        string detail)
    {
        ArgumentNullException.ThrowIfNull(journal);
        RequireShortText(operation, nameof(operation));
        RequireShortText(detail, nameof(detail));
        UpgradeLegacyJournal(journal);
        journal.CurrentOperation = operation;
        journal.PendingUserDecision = detail;
        Validate(journal);
    }

    public static void RecordRebootState(
        MachineInstallTransactionJournal journal,
        bool rebootPending,
        string? operation = null)
    {
        ArgumentNullException.ThrowIfNull(journal);
        if (operation is not null) RequireShortText(operation, nameof(operation));
        UpgradeLegacyJournal(journal);
        journal.RebootPending = rebootPending;
        if (operation is not null) journal.CurrentOperation = operation;
        Validate(journal);
    }

    public static bool RequiresNetworkRollForward(MachineInstallTransactionJournal journal)
    {
        Validate(journal);
        return journal.Phase >= MachineInstallTransactionPhase.NetworkEnrollmentStarted;
    }

    private static void Validate(MachineInstallTransactionJournal journal)
    {
        UpgradeLegacyJournal(journal);
        if (journal.SchemaVersion != 2
            || journal.OperationId == Guid.Empty
            || journal.InviteId == Guid.Empty
            || !TransactionPattern.IsMatch(journal.SourceTransactionId)
            || !Sha256Pattern.IsMatch(journal.InviteCiphertextSha256)
            || !Sha256Pattern.IsMatch(journal.SourceSha256)
            || !Sha256Pattern.IsMatch(journal.SourceManifestSha256)
            || !Enum.IsDefined(journal.Phase)
            || !IsShortTextOrEmpty(journal.CurrentOperation)
            || !IsShortTextOrEmpty(journal.LastVerifiedPostcondition)
            || !IsShortTextOrEmpty(journal.TailscaleNodeIdentity)
            || !IsShortTextOrEmpty(journal.PendingUserDecision)
            || journal.RepairsAttempted is null
            || journal.ResourcesChangedByOpticon is null
            || journal.PreviousComponentVersions is null
            || journal.RepairsAttempted.Count > 64
            || journal.ResourcesChangedByOpticon.Count > 128
            || journal.PreviousComponentVersions.Count > 32
            || journal.RepairsAttempted.Any(item => !IsShortTextOrEmpty(item))
            || journal.ResourcesChangedByOpticon.Any(item => !IsShortTextOrEmpty(item))
            || journal.PreviousComponentVersions.Any(pair => !IsShortTextOrEmpty(pair.Key) || !IsShortTextOrEmpty(pair.Value)))
            throw new InvalidDataException("The protected machine-install journal is invalid.");
    }

    private static void UpgradeLegacyJournal(MachineInstallTransactionJournal journal)
    {
        if (journal.SchemaVersion == 1)
            journal.SchemaVersion = 2;
        journal.CurrentOperation ??= string.Empty;
        journal.LastVerifiedPostcondition ??= string.Empty;
        journal.RepairsAttempted ??= [];
        journal.ResourcesChangedByOpticon ??= [];
        journal.PreviousComponentVersions ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        journal.TailscaleNodeIdentity ??= string.Empty;
        journal.PendingUserDecision ??= string.Empty;
    }

    private static void AddDistinct(List<string> values, string value)
    {
        if (!values.Contains(value, StringComparer.Ordinal)) values.Add(value);
    }

    private static void RequireShortText(string value, string parameterName)
    {
        if (!IsShortTextOrEmpty(value) || string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("The recovery journal value is invalid.", parameterName);
    }

    private static bool IsShortTextOrEmpty(string? value) => value is not null
        && value.Length <= 512
        && value.All(character => !char.IsControl(character));

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
