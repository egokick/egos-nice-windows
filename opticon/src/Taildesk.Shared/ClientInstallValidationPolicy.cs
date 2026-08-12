namespace Taildesk.Shared;

public enum ClientInstallValidationStep
{
    InvitationAuthenticity,
    InvitationConstraints,
    ProtectedPaths,
    DownloadIntegrity,
    SourceArchiveAuthenticity,
    LauncherBinding,
    SourceBuildProvenance,
    SetupPreflight,
    MachineState,
    PayloadAuthenticity,
    DependencyIntegrity,
    ComponentPostconditions,
    NetworkIdentity,
    FirewallPolicy,
    EnrollmentConfirmation
}

/// <summary>
/// An operator-selected emergency policy carried by the release manifest and
/// copied into each encrypted invitation. Missing policy data always means the
/// normal fail-closed behavior used by older releases.
/// </summary>
public sealed class ClientInstallValidationPolicy
{
    public bool DisableAll { get; set; }
    public string[] DisabledSteps { get; set; } = [];

    public bool IsEnabled(ClientInstallValidationStep step) =>
        !DisableAll && !(DisabledSteps ?? []).Contains(step.ToString(), StringComparer.Ordinal);

    public ClientInstallValidationPolicy Clone() => new()
    {
        DisableAll = DisableAll,
        DisabledSteps = (DisabledSteps ?? []).Distinct(StringComparer.Ordinal).ToArray()
    };

    public static ClientInstallValidationPolicy Normalize(ClientInstallValidationPolicy? policy)
    {
        if (policy is null) return new ClientInstallValidationPolicy();
        var known = Enum.GetNames<ClientInstallValidationStep>().ToHashSet(StringComparer.Ordinal);
        return new ClientInstallValidationPolicy
        {
            DisableAll = policy.DisableAll,
            DisabledSteps = (policy.DisabledSteps ?? [])
                .Where(known.Contains)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray()
        };
    }
}
